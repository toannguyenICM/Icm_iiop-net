// ObjectRegistryDispatchSink.cs — Server-side dispatch sink using ObjectRegistry.
// Replaces the .NET Remoting DispatchChannelSink for routing incoming CORBA requests
// to servant objects registered via ObjectRegistry.Marshal().
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Ch.Elca.Iiop.Remoting;

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

                // Check if this is a standard CORBA operation (e.g. _is_a, _non_existent).
                // These are routed to the StandardCorbaOps singleton, and the actual
                // implementation method has an extra objectUri parameter prepended.
                bool isStandardOp = false;
                object isStdProp = requestMsg.Properties[
                    Ch.Elca.Iiop.MessageHandling.SimpleGiopMsg.IS_STANDARD_CORBA_OP_KEY];
                if (isStdProp is bool) {
                    isStandardOp = (bool)isStdProp;
                }

                MarshalByRefObject servant = ObjectRegistry.ResolveUri(uri);
                if (servant == null) {
                    throw new omg.org.CORBA.OBJECT_NOT_EXIST(0,
                        omg.org.CORBA.CompletionStatus.Completed_No);
                }

                MethodInfo method;
                object[] invokeArgs;

                if (isStandardOp) {
                    // For standard CORBA ops, the GIOP deserializer has already:
                    // 1. Rerouted the URI to StandardCorbaOps.WELLKNOWN_URI
                    // 2. Resolved the method to the internal implementation (e.g. is_a)
                    // 3. Prepended the original object URI as the first argument
                    //    via AdaptArgsForStandardOp in GiopMessageBodySerializer
                    // So mcm.Args already contains [objectUri, ...idlArgs].
                    // Use the IDL method name (e.g. "_is_a") for the lookup, not
                    // mcm.MethodName which is the internal mapped name (e.g. "is_a").
                    string idlMethodName = (string)requestMsg.Properties[
                        Ch.Elca.Iiop.MessageHandling.SimpleGiopMsg.IDL_METHOD_NAME_KEY]
                        ?? mcm.MethodName;
                    method = StandardCorbaOps.GetMethodToCallForStandardMethod(idlMethodName);
                    if (method == null) {
                        throw new omg.org.CORBA.BAD_OPERATION(0,
                            omg.org.CORBA.CompletionStatus.Completed_No);
                    }
                    invokeArgs = mcm.Args ?? new object[0];
                } else {
                    // Regular operation — find the method on the servant type
                    method = GetTargetMethod(servant, mcm);
                    if (method == null) {
                        throw new omg.org.CORBA.BAD_OPERATION(0,
                            omg.org.CORBA.CompletionStatus.Completed_No);
                    }
                    object[] args = mcm.Args;
                    invokeArgs = new object[args.Length];
                    Array.Copy(args, invokeArgs, args.Length);
                }

                object returnValue = method.Invoke(servant, invokeArgs);

                // Extract only out/ref args for the ReturnMessage.
                // The GIOP serializer expects OutArgs to contain only out/ref
                // parameter values, indexed sequentially from the IDL method's
                // perspective (not including the injected objectUri for standard ops).
                ParameterInfo[] calledMethodParams = (mcm.MethodBase as MethodInfo ?? method).GetParameters();
                object[] outArgs;
                if (isStandardOp) {
                    // Standard ops have no out/ref params in the IDL signature
                    outArgs = new object[0];
                } else {
                    outArgs = ExtractOutArgs(calledMethodParams, invokeArgs);
                }

                responseMsg = new ReturnMessage(returnValue, outArgs,
                    outArgs.Length, mcm.LogicalCallContext, mcm);
            } catch (TargetInvocationException tie) {
                Exception actual = tie.InnerException ?? tie;
                string msg = string.Format(
                    "ObjectRegistryDispatchSink: TargetInvocationException dispatching {0}.{1} on {2}:\n{3}",
                    mcm.TypeName, mcm.MethodName, mcm.Uri, actual);
                Trace.WriteLine(msg);
                Console.Error.WriteLine(msg);
                responseMsg = new ReturnMessage(actual, mcm);
            } catch (Exception ex) {
                string msg = string.Format(
                    "ObjectRegistryDispatchSink: Exception dispatching {0}.{1} on {2}:\n{3}",
                    mcm.TypeName, mcm.MethodName, mcm.Uri, ex);
                Trace.WriteLine(msg);
                Console.Error.WriteLine(msg);
                responseMsg = new ReturnMessage(ex, mcm);
            }

            return ServerProcessing.Complete;
        }

        /// <summary>
        /// Extracts only the out/ref parameter values from the full args array.
        /// The GIOP response serializer indexes out args sequentially (0, 1, 2...)
        /// and expects only out/ref values, not all parameter values.
        /// </summary>
        private static object[] ExtractOutArgs(ParameterInfo[] methodParams, object[] allArgs) {
            int outCount = 0;
            for (int i = 0; i < methodParams.Length; i++) {
                if (methodParams[i].IsOut || methodParams[i].ParameterType.IsByRef) {
                    outCount++;
                }
            }
            if (outCount == 0) {
                return new object[0];
            }
            object[] outArgs = new object[outCount];
            int outIdx = 0;
            for (int i = 0; i < methodParams.Length; i++) {
                if (methodParams[i].IsOut || methodParams[i].ParameterType.IsByRef) {
                    outArgs[outIdx++] = allArgs[i];
                }
            }
            return outArgs;
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
