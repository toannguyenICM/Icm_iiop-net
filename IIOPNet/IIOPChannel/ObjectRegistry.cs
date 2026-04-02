// ObjectRegistry.cs - Replaces System.Runtime.Remoting object registration
// This is a simple thread-safe registry that maps URIs to MarshalByRefObject instances.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ch.Elca.Iiop {

    /// <summary>
    /// Factory interface for creating CORBA client proxies.
    /// On .NET Framework, the default implementation uses RemotingServices.Connect via reflection.
    /// On .NET Core, an implementation using DispatchProxy should be registered at startup
    /// via <see cref="ObjectRegistry.SetProxyFactory"/>.
    /// </summary>
    public interface ICorbaProxyFactory {
        /// <summary>
        /// Creates a client proxy for a remote CORBA object.
        /// </summary>
        /// <param name="interfaceType">The CORBA interface type to proxy (e.g. typeof(evtListener)).</param>
        /// <param name="iorString">The IOR string or corbaloc URL identifying the remote object.</param>
        /// <returns>A proxy object that implements the specified interface and routes
        /// method calls to the remote CORBA object via IIOP.</returns>
        object CreateProxy(Type interfaceType, string iorString);
    }

    /// <summary>
    /// Replaces System.Runtime.Remoting.RemotingServices for object registration.
    /// Maps URI strings to MarshalByRefObject instances, and tracks the active 
    /// IiopServerChannel for host/port information needed for IOR construction.
    /// </summary>
    public static class ObjectRegistry {

        private static readonly Dictionary<string, MarshalByRefObject> s_objects = 
            new Dictionary<string, MarshalByRefObject>(StringComparer.OrdinalIgnoreCase);

        private static readonly object s_lock = new object();

        private static string s_hostName;
        private static int s_port;
        private static bool s_channelRegistered;

        /// <summary>Registers the active server channel info for IOR construction.</summary>
        public static void RegisterChannel(string hostName, int port) {
            lock (s_lock) {
                s_hostName = hostName;
                s_port = port;
                s_channelRegistered = true;
            }
        }

        public static bool IsChannelRegistered {
            get { lock (s_lock) { return s_channelRegistered; } }
        }

        public static string HostName {
            get { lock (s_lock) { return s_hostName; } }
        }

        public static int Port {
            get { lock (s_lock) { return s_port; } }
        }

        /// <summary>
        /// Registers (publishes) an object with the given URI.
        /// Replaces RemotingServices.Marshal(obj, uri).
        /// On .NET Framework, also registers with the Remoting infrastructure via reflection
        /// so the server-side sink chain can dispatch incoming GIOP requests to this object.
        /// Returns the URI used.
        /// </summary>
        public static string Marshal(MarshalByRefObject obj, string uri) {
            if (uri == null) {
                uri = Guid.NewGuid().ToString();
            }
            lock (s_lock) {
                s_objects[uri] = obj;
            }
            // Also register with .NET Remoting so the server-side dispatcher can find it
            EnsureRemotingResolved();
            if (s_remotingMarshal != null) {
                try {
                    s_remotingMarshal.Invoke(null, new object[] { obj, uri });
                } catch {
                    // Swallow � on platforms where Remoting is unavailable
                }
            }
            return uri;
        }

        /// <summary>
        /// Registers an object with an auto-generated URI.
        /// Replaces RemotingServices.Marshal(obj).
        /// </summary>
        public static string Marshal(MarshalByRefObject obj) {
            return Marshal(obj, Guid.NewGuid().ToString());
        }

        /// <summary>
        /// Unregisters an object.
        /// Replaces RemotingServices.Disconnect(obj).
        /// On .NET Framework, also unregisters from the Remoting infrastructure via reflection.
        /// </summary>
        public static void Disconnect(MarshalByRefObject obj) {
            lock (s_lock) {
                string keyToRemove = null;
                foreach (var kvp in s_objects) {
                    if (ReferenceEquals(kvp.Value, obj)) {
                        keyToRemove = kvp.Key;
                        break;
                    }
                }
                if (keyToRemove != null) {
                    s_objects.Remove(keyToRemove);
                }
            }
            // Also unregister from .NET Remoting
            EnsureRemotingResolved();
            if (s_remotingDisconnect != null) {
                try {
                    s_remotingDisconnect.Invoke(null, new object[] { obj });
                } catch {
                    // Swallow � on platforms where Remoting is unavailable
                }
            }
        }

        /// <summary>Resolves a URI to the registered object. Returns null if not found.</summary>
        public static MarshalByRefObject ResolveUri(string uri) {
            lock (s_lock) {
                MarshalByRefObject obj;
                if (s_objects.TryGetValue(uri, out obj)) {
                    return obj;
                }
                return null;
            }
        }

        /// <summary>Gets the URI for a registered object. Returns null if not found.</summary>
        public static string GetObjectUri(MarshalByRefObject obj) {
            lock (s_lock) {
                foreach (var kvp in s_objects) {
                    if (ReferenceEquals(kvp.Value, obj)) {
                        return kvp.Key;
                    }
                }
                return null;
            }
        }

        /// <summary>Returns true if the object is registered in our registry.</summary>
        public static bool IsRegistered(MarshalByRefObject obj) {
            return GetObjectUri(obj) != null;
        }

        /// <summary>
        /// Gets the Type of the server object registered at the given URI.
        /// Replaces RemotingServices.GetServerTypeForUri(uri).
        /// Returns null if not found.
        /// </summary>
        public static Type GetServerTypeForUri(string uri) {
            MarshalByRefObject obj = ResolveUri(uri);
            return obj != null ? obj.GetType() : null;
        }

        /// <summary>
        /// Returns true if the object is a local server object (registered in ObjectRegistry).
        /// Returns false if it's a remote proxy or unknown.
        /// Replaces the inverse of RemotingServices.IsTransparentProxy().
        /// </summary>
        public static bool IsLocalObject(MarshalByRefObject obj) {
            return IsRegistered(obj);
        }

        /// <summary>
        /// Checks whether a method has the [OneWay] attribute.
        /// Replaces RemotingServices.IsOneWay(methodBase).
        /// Uses attribute name check to avoid hard dependency on System.Runtime.Remoting.
        /// </summary>
        public static bool IsOneWay(System.Reflection.MethodBase methodBase) {
            if (methodBase == null) return false;
            object[] attrs = methodBase.GetCustomAttributes(false);
            for (int i = 0; i < attrs.Length; i++) {
                if (attrs[i].GetType().FullName == "System.Runtime.Remoting.Messaging.OneWayAttribute") {
                    return true;
                }
            }
            return false;
        }

        private static MethodInfo s_remotingMarshal;
        private static MethodInfo s_remotingDisconnect;
        private static MethodInfo s_remotingConnect;
        private static bool s_remotingResolved;
        private static readonly object s_connectLock = new object();
        private static ICorbaProxyFactory s_proxyFactory;

        /// <summary>
        /// One-time reflection resolution of RemotingServices methods from mscorlib.
        /// On .NET Framework these exist; on .NET Core they will be null.
        /// </summary>
        private static void EnsureRemotingResolved() {
            if (s_remotingResolved) return;
            lock (s_connectLock) {
                if (s_remotingResolved) return;
                s_remotingResolved = true;
                Type remotingServicesType = Type.GetType(
                    "System.Runtime.Remoting.RemotingServices, mscorlib", false);
                if (remotingServicesType != null) {
                    s_remotingMarshal = remotingServicesType.GetMethod("Marshal",
                        new Type[] { typeof(MarshalByRefObject), typeof(string) });
                    s_remotingDisconnect = remotingServicesType.GetMethod("Disconnect",
                        new Type[] { typeof(MarshalByRefObject) });
                    s_remotingConnect = remotingServicesType.GetMethod("Connect",
                        new Type[] { typeof(Type), typeof(string) });
                }
            }
        }

        /// <summary>
        /// Registers a custom proxy factory for creating CORBA client proxies.
        /// On .NET Core, call this at startup with a DispatchProxy-based implementation:
        /// <code>
        /// // Example .NET Core startup:
        /// ObjectRegistry.SetProxyFactory(new DispatchProxyCorbaFactory());
        /// </code>
        /// When no factory is registered, the default behavior uses
        /// RemotingServices.Connect via reflection (.NET Framework only).
        /// </summary>
        public static void SetProxyFactory(ICorbaProxyFactory factory) {
            lock (s_connectLock) {
                s_proxyFactory = factory;
            }
        }

        /// <summary>
        /// Gets the currently registered proxy factory, or null if using the default
        /// RemotingServices.Connect path.
        /// </summary>
        public static ICorbaProxyFactory ProxyFactory {
            get { lock (s_connectLock) { return s_proxyFactory; } }
        }

        /// <summary>
        /// Creates a client proxy for a remote CORBA object from an IOR string.
        /// Replaces RemotingServices.Connect(type, iorString).
        /// 
        /// Resolution order:
        /// 1. If a custom ICorbaProxyFactory has been registered via SetProxyFactory(),
        ///    it is used. This is the path for .NET Core (DispatchProxy-based).
        /// 2. Otherwise, RemotingServices.Connect is resolved via reflection from mscorlib.
        ///    This is the path for .NET Framework 4.7.2.
        /// 3. If neither is available, throws PlatformNotSupportedException with guidance.
        /// 
        /// No compile-time dependency on System.Runtime.Remoting exists.
        /// </summary>
        public static object CreateProxyFromIor(Type type, string iorString) {
            // Path 1: Custom factory (for .NET Core / DispatchProxy / testing)
            ICorbaProxyFactory factory;
            lock (s_connectLock) {
                factory = s_proxyFactory;
            }
            if (factory != null) {
                return factory.CreateProxy(type, iorString);
            }

            // Path 2: .NET Framework - RemotingServices.Connect via reflection
            EnsureRemotingResolved();
            if (s_remotingConnect != null) {
                return s_remotingConnect.Invoke(null, new object[] { type, iorString });
            }

            // Path 3: No proxy mechanism available
            throw new PlatformNotSupportedException(
                "No CORBA proxy mechanism is available. " +
                "On .NET Core, register a DispatchProxy-based ICorbaProxyFactory " +
                "via ObjectRegistry.SetProxyFactory() at application startup.");
        }
    }
}
