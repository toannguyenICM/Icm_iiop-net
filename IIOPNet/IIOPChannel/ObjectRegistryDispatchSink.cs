// ObjectRegistryDispatchSink.cs — Server-side dispatch sink using ObjectRegistry.
// Replaces the .NET Remoting DispatchChannelSink for routing incoming CORBA requests
// to servant objects registered via ObjectRegistry.Marshal().
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Messaging;

namespace Ch.Elca.Iiop {

    /// <summary>
    /// Terminal server-side channel sink that dispatches deserialized CORBA requests
    /// to servant objects registered in <see cref="ObjectRegistry"/>.
    /// This replaces .NET Remoting's built-in DispatchChannelSink, which requires
    /// objects to be published via RemotingServices.Marshal().
    /// </summary>
    internal class ObjectRegistryDispatchSink : IServerChannelSink {

        public IServerChannelSink NextChannelSink {
            get { return null; } // terminal sink
        }

        public IDictionary Properties {
            get { return null; }
        }

        public void AsyncProcessResponse(IServerResponseChannelSinkStack sinkStack,
                                         object state, IMessage msg,
                                         ITransportHeaders headers, Stream stream) {
            // not needed for terminal sink
        }

        public Stream GetResponseStream(IServerResponseChannelSinkStack sinkStack,
                                        object state, IMessage msg,
                                        ITransportHeaders headers) {
            return null;
        }

        public ServerProcessing ProcessMessage(IServerChannelSinkStack sinkStack,
                                               IMessage requestMsg,
                                               ITransportHeaders requestHeaders,
                                               Stream requestStream,
                                               out IMessage responseMsg,
                                               out ITransportHeaders responseHeaders,
                                               out Stream responseStream) {
            responseHeaders = null;
            responseStream = null;

            IMethodCallMessage mcm = requestMsg as IMethodCallMessage;
            if (mcm == null) {
                responseMsg = new ReturnMessage(
                    new InvalidOperationException("Expected IMethodCallMessage"), null);
                return ServerProcessing.Complete;
            }

            try {
                string uri = mcm.Uri;
                MarshalByRefObject servant = ObjectRegistry.ResolveUri(uri);
                if (servant == null) {
                    throw new omg.org.CORBA.OBJECT_NOT_EXIST(0,
                        omg.org.CORBA.CompletionStatus.Completed_No);
                }

                // Find the method on the actual servant type.
                // mcm.MethodBase may be from the interface type; we need the servant's concrete method.
                MethodInfo method = GetTargetMethod(servant, mcm);
                if (method == null) {
                    throw new omg.org.CORBA.BAD_OPERATION(0,
                        omg.org.CORBA.CompletionStatus.Completed_No);
                }

                // Invoke the servant method
                object[] args = mcm.Args;
                // Copy args so we can capture out/ref parameters
                object[] argsCopy = new object[args.Length];
                Array.Copy(args, argsCopy, args.Length);

                object returnValue = method.Invoke(servant, argsCopy);

                responseMsg = new ReturnMessage(returnValue, argsCopy,
                    argsCopy.Length, mcm.LogicalCallContext, mcm);
            } catch (TargetInvocationException tie) {
                responseMsg = new ReturnMessage(tie.InnerException ?? tie, mcm);
            } catch (Exception ex) {
                responseMsg = new ReturnMessage(ex, mcm);
            }

            return ServerProcessing.Complete;
        }

        /// <summary>
        /// Resolves the correct method on the servant object.
        /// The IMethodCallMessage.MethodBase may reference an interface method;
        /// we need to find the corresponding method on the concrete servant type.
        /// </summary>
        private static MethodInfo GetTargetMethod(object servant, IMethodCallMessage mcm) {
            MethodBase requestedMethod = mcm.MethodBase;
            Type servantType = servant.GetType();

            // Try direct match first (works when MethodBase is already on the concrete type)
            if (requestedMethod.DeclaringType != null &&
                requestedMethod.DeclaringType.IsAssignableFrom(servantType)) {
                return requestedMethod as MethodInfo;
            }

            // Interface method — find the implementation on the servant via interface map
            if (requestedMethod.DeclaringType != null && requestedMethod.DeclaringType.IsInterface) {
                InterfaceMapping map = servantType.GetInterfaceMap(requestedMethod.DeclaringType);
                for (int i = 0; i < map.InterfaceMethods.Length; i++) {
                    if (map.InterfaceMethods[i].Equals(requestedMethod)) {
                        return map.TargetMethods[i];
                    }
                }
            }

            // Fallback: search by name and parameter types
            Type[] paramTypes = new Type[mcm.InArgCount + mcm.ArgCount - mcm.InArgCount];
            ParameterInfo[] parms = requestedMethod.GetParameters();
            for (int i = 0; i < parms.Length; i++) {
                paramTypes[i] = parms[i].ParameterType;
            }
            return servantType.GetMethod(requestedMethod.Name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, paramTypes, null);
        }
    }

    /// <summary>
    /// Sink provider that creates <see cref="ObjectRegistryDispatchSink"/> as the
    /// terminal sink in the server-side channel sink chain.
    /// </summary>
    internal class ObjectRegistryDispatchSinkProvider : IServerChannelSinkProvider {

        public IServerChannelSinkProvider Next { get; set; }

        public IServerChannelSink CreateSink(IChannelReceiver channel) {
            return new ObjectRegistryDispatchSink();
        }

        public void GetChannelData(IChannelDataStore channelData) {
            // no channel data needed
        }
    }
}
