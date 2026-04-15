/*
 * RemotingShims.cs — IIOPChannel-owned replacements for System.Runtime.Remoting types
 *
 * Strategy C: These types replace ALL System.Runtime.Remoting dependencies so that
 * IIOPChannel compiles on both .NET Framework 4.7.2 and .NET Core with the SAME source.
 *
 * No #if NETFRAMEWORK in this file — these types are always compiled on both frameworks.
 *
 * After switching all IIOPChannel files from:
 *   using System.Runtime.Remoting;
 *   using System.Runtime.Remoting.Channels;
 *   using System.Runtime.Remoting.Messaging;
 * to:
 *   using Ch.Elca.Iiop.Remoting;
 *
 * the <Reference Include="System.Runtime.Remoting" /> can be removed from the .csproj.
 */

using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace Ch.Elca.Iiop.Remoting {

    // ???????????????????????????????????????????????????????????????????
    //  MESSAGE INTERFACES
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Base message interface — just a property dictionary.</summary>
    public interface IMessage {
        IDictionary Properties { get; }
    }

    /// <summary>Base for method call and return messages.</summary>
    public interface IMethodMessage : IMessage {
        string Uri { get; }
        string MethodName { get; }
        string TypeName { get; }
        MethodBase MethodBase { get; }
        object[] Args { get; }
        int ArgCount { get; }
        string GetArgName(int index);
        object GetArg(int index);
        LogicalCallContext LogicalCallContext { get; }
        object MethodSignature { get; }
        bool HasVarArgs { get; }
    }

    /// <summary>Carries method name, args, URI for an inbound/outbound call.</summary>
    public interface IMethodCallMessage : IMethodMessage {
        int InArgCount { get; }
        string GetInArgName(int index);
        object GetInArg(int index);
        object[] InArgs { get; }
    }

    /// <summary>Carries return value, out args, exception for a method response.</summary>
    public interface IMethodReturnMessage : IMethodMessage {
        int OutArgCount { get; }
        string GetOutArgName(int index);
        object GetOutArg(int index);
        object[] OutArgs { get; }
        object ReturnValue { get; }
        Exception Exception { get; }
    }

    // ???????????????????????????????????????????????????????????????????
    //  CONCRETE MESSAGE TYPES
    // ???????????????????????????????????????????????????????????????????

    /// <summary>
    /// Wraps an IMessage dictionary into typed IMethodCallMessage properties.
    /// Reads well-known keys: __Uri, __MethodName, __TypeName, __Args, __MethodSignature, etc.
    /// </summary>
    public class MethodCall : IMethodCallMessage {

        private readonly IDictionary m_properties;
        private readonly MethodBase m_methodBase;
        private readonly string m_uri;
        private readonly string m_methodName;
        private readonly string m_typeName;
        private readonly object[] m_args;
        private readonly object m_methodSignature;
        private readonly LogicalCallContext m_callContext;

        public MethodCall(IMessage msg) {
            m_properties = msg.Properties;
            m_uri = m_properties["__Uri"] as string;
            m_methodName = m_properties["__MethodName"] as string;
            m_typeName = m_properties["__TypeName"] as string;
            m_args = m_properties["__Args"] as object[];
            m_methodSignature = m_properties["__MethodSignature"];
            // The standard Remoting key is "__MethodBase", but IIOPChannel's
            // SimpleGiopMsg stores the called method under "_called_method".
            m_methodBase = m_properties["__MethodBase"] as MethodBase
                        ?? m_properties["_called_method"] as MethodBase;
            object ctx = m_properties["__CallContext"];
            m_callContext = ctx as LogicalCallContext ?? new LogicalCallContext();
        }

        public IDictionary Properties { get { return m_properties; } }
        public string Uri { get { return m_uri; } }
        public string MethodName { get { return m_methodName; } }
        public string TypeName { get { return m_typeName; } }
        public MethodBase MethodBase { get { return m_methodBase; } }
        public object[] Args { get { return m_args; } }
        public int ArgCount { get { return m_args != null ? m_args.Length : 0; } }
        public object MethodSignature { get { return m_methodSignature; } }
        public bool HasVarArgs { get { return false; } }
        public LogicalCallContext LogicalCallContext { get { return m_callContext; } }

        public string GetArgName(int index) {
            if (m_methodBase != null) {
                ParameterInfo[] parms = m_methodBase.GetParameters();
                if (index >= 0 && index < parms.Length) {
                    return parms[index].Name;
                }
            }
            return "arg" + index;
        }

        public object GetArg(int index) {
            return m_args[index];
        }

        public int InArgCount { get { return ArgCount; } }
        public object[] InArgs { get { return m_args; } }

        public string GetInArgName(int index) {
            return GetArgName(index);
        }

        public object GetInArg(int index) {
            return GetArg(index);
        }
    }

    /// <summary>
    /// Carries return value, out args, and exception for a method response.
    /// Two constructors: normal return and exception return.
    /// </summary>
    public class ReturnMessage : IMethodReturnMessage {

        private readonly object m_returnValue;
        private readonly object[] m_outArgs;
        private readonly int m_outArgsCount;
        private readonly Exception m_exception;
        private readonly IMethodCallMessage m_request;
        private readonly IDictionary m_properties;
        private readonly LogicalCallContext m_callContext;

        /// <summary>Normal return with value and out args.</summary>
        public ReturnMessage(object returnValue, object[] outArgs, int outArgsCount,
                            LogicalCallContext callCtx, IMethodCallMessage request) {
            m_returnValue = returnValue;
            m_outArgs = outArgs;
            m_outArgsCount = outArgsCount;
            m_exception = null;
            m_request = request;
            m_callContext = callCtx ?? (request != null ? request.LogicalCallContext : null);
            m_properties = new Hashtable();
        }

        /// <summary>Exception return.</summary>
        public ReturnMessage(Exception ex, IMethodCallMessage request) {
            m_returnValue = null;
            m_outArgs = null;
            m_outArgsCount = 0;
            m_exception = ex;
            m_request = request;
            m_callContext = request != null ? request.LogicalCallContext : null;
            m_properties = new Hashtable();
        }

        public IDictionary Properties { get { return m_properties; } }
        public object ReturnValue { get { return m_returnValue; } }
        public Exception Exception { get { return m_exception; } }
        public object[] OutArgs { get { return m_outArgs; } }
        public int OutArgCount { get { return m_outArgs != null ? m_outArgs.Length : 0; } }

        public object GetOutArg(int index) {
            return m_outArgs != null ? m_outArgs[index] : null;
        }

        public string GetOutArgName(int index) {
            if (m_request != null && m_request.MethodBase != null) {
                ParameterInfo[] parms = m_request.MethodBase.GetParameters();
                int outIdx = 0;
                for (int i = 0; i < parms.Length; i++) {
                    if (parms[i].IsOut || parms[i].ParameterType.IsByRef) {
                        if (outIdx == index) {
                            return parms[i].Name;
                        }
                        outIdx++;
                    }
                }
            }
            return "out_arg" + index;
        }

        // Delegate to original request for method info
        public string Uri { get { return m_request != null ? m_request.Uri : null; } }
        public string MethodName { get { return m_request != null ? m_request.MethodName : null; } }
        public string TypeName { get { return m_request != null ? m_request.TypeName : null; } }
        public MethodBase MethodBase { get { return m_request != null ? m_request.MethodBase : null; } }
        public object[] Args { get { return m_request != null ? m_request.Args : null; } }
        public int ArgCount { get { return m_request != null ? m_request.ArgCount : 0; } }
        public object MethodSignature { get { return m_request != null ? m_request.MethodSignature : null; } }
        public bool HasVarArgs { get { return m_request != null ? m_request.HasVarArgs : false; } }
        public LogicalCallContext LogicalCallContext { get { return m_callContext; } }

        public string GetArgName(int index) {
            return m_request != null ? m_request.GetArgName(index) : "arg" + index;
        }

        public object GetArg(int index) {
            return m_request != null ? m_request.GetArg(index) : null;
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  LOGICAL CALL CONTEXT
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Call context for propagating data along the call chain.
    /// IIOPChannel only uses SetData/GetData.</summary>
    public class LogicalCallContext {

        private readonly Dictionary<string, object> m_data = new Dictionary<string, object>();

        public void SetData(string name, object data) {
            m_data[name] = data;
        }

        public object GetData(string name) {
            object val;
            m_data.TryGetValue(name, out val);
            return val;
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  TRANSPORT HEADERS
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Request/response transport headers.</summary>
    public interface ITransportHeaders {
        object this[object key] { get; set; }
        IEnumerator GetEnumerator();
    }

    /// <summary>Concrete transport headers — Hashtable wrapper.</summary>
    public class TransportHeaders : ITransportHeaders {

        private readonly Hashtable m_headers = new Hashtable();

        public object this[object key] {
            get { return m_headers[key]; }
            set { m_headers[key] = value; }
        }

        public IEnumerator GetEnumerator() {
            return m_headers.GetEnumerator();
        }
    }

    /// <summary>Well-known transport header key constants.</summary>
    public static class CommonTransportKeys {
        public const string IPAddress = "__IPAddress";
        public const string RequestUri = "__RequestUri";
        public const string ContentType = "Content-Type";
    }

    // ???????????????????????????????????????????????????????????????????
    //  SERVER PROCESSING ENUM
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Result of server-side message processing.</summary>
    public enum ServerProcessing {
        Complete = 0,
        OneWay = 1,
        Async = 2
    }

    // ???????????????????????????????????????????????????????????????????
    //  SERVER SINK CHAIN
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Server-side channel sink interface.</summary>
    public interface IServerChannelSink {

        IServerChannelSink NextChannelSink { get; }

        IDictionary Properties { get; }

        ServerProcessing ProcessMessage(
            IServerChannelSinkStack sinkStack,
            IMessage requestMsg,
            ITransportHeaders requestHeaders,
            Stream requestStream,
            out IMessage responseMsg,
            out ITransportHeaders responseHeaders,
            out Stream responseStream);

        void AsyncProcessResponse(
            IServerResponseChannelSinkStack sinkStack,
            object state, IMessage msg,
            ITransportHeaders headers, Stream stream);

        Stream GetResponseStream(
            IServerResponseChannelSinkStack sinkStack,
            object state, IMessage msg, ITransportHeaders headers);
    }

    /// <summary>Server sink provider — creates sink instances.</summary>
    public interface IServerChannelSinkProvider {
        IServerChannelSinkProvider Next { get; set; }
        IServerChannelSink CreateSink(IChannelReceiver channel);
        void GetChannelData(IChannelDataStore channelData);
    }

    /// <summary>Server formatter sink provider marker interface.</summary>
    public interface IServerFormatterSinkProvider : IServerChannelSinkProvider { }

    // ??? Server Sink Stack ???

    /// <summary>Server response sink stack.</summary>
    public interface IServerResponseChannelSinkStack {
        void AsyncProcessResponse(IMessage msg, ITransportHeaders headers, Stream stream);
        Stream GetResponseStream(IMessage msg, ITransportHeaders headers);
    }

    /// <summary>Server sink stack — manages push/pop during request processing.</summary>
    public interface IServerChannelSinkStack : IServerResponseChannelSinkStack {
        void Push(IServerChannelSink sink, object state);
        object Pop(IServerChannelSink sink);
        void Store(IServerChannelSink sink, object state);
        void StoreAndDispatch(IServerChannelSink sink, object state);
    }

    /// <summary>Concrete server sink stack implementation.</summary>
    public class ServerChannelSinkStack : IServerChannelSinkStack {

        private struct SinkEntry {
            public IServerChannelSink Sink;
            public object State;
        }

        private readonly Stack<SinkEntry> m_stack = new Stack<SinkEntry>();
        private SinkEntry? m_stored;

        public void Push(IServerChannelSink sink, object state) {
            m_stack.Push(new SinkEntry { Sink = sink, State = state });
        }

        public object Pop(IServerChannelSink sink) {
            SinkEntry entry = m_stack.Pop();
            return entry.State;
        }

        public void Store(IServerChannelSink sink, object state) {
            m_stored = new SinkEntry { Sink = sink, State = state };
        }

        public void StoreAndDispatch(IServerChannelSink sink, object state) {
            m_stored = new SinkEntry { Sink = sink, State = state };
        }

        public void AsyncProcessResponse(IMessage msg, ITransportHeaders headers, Stream stream) {
            if (m_stored.HasValue) {
                m_stored.Value.Sink.AsyncProcessResponse(this, m_stored.Value.State, msg, headers, stream);
            }
        }

        public Stream GetResponseStream(IMessage msg, ITransportHeaders headers) {
            if (m_stored.HasValue) {
                return m_stored.Value.Sink.GetResponseStream(this, m_stored.Value.State, msg, headers);
            }
            return null;
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  CLIENT SINK CHAIN
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Client-side channel sink interface.</summary>
    public interface IClientChannelSink {

        IClientChannelSink NextChannelSink { get; }

        IDictionary Properties { get; }

        void ProcessMessage(
            IMessage msg,
            ITransportHeaders requestHeaders, Stream requestStream,
            out ITransportHeaders responseHeaders, out Stream responseStream);

        void AsyncProcessRequest(
            IClientChannelSinkStack sinkStack, IMessage msg,
            ITransportHeaders headers, Stream stream);

        void AsyncProcessResponse(
            IClientResponseChannelSinkStack sinkStack, object state,
            ITransportHeaders headers, Stream stream);

        Stream GetRequestStream(IMessage msg, ITransportHeaders headers);
    }

    /// <summary>Client sink provider — creates sink instances.</summary>
    public interface IClientChannelSinkProvider {
        IClientChannelSinkProvider Next { get; set; }
        IClientChannelSink CreateSink(IChannelSender channel, string url, object remoteChannelData);
    }

    /// <summary>Client formatter sink provider marker interface.</summary>
    public interface IClientFormatterSinkProvider : IClientChannelSinkProvider { }

    /// <summary>Combined formatter + message sink interface for client side.</summary>
    public interface IClientFormatterSink : IClientChannelSink, IMessageSink { }

    // ??? Client Sink Stack ???

    /// <summary>Client response sink stack.</summary>
    public interface IClientResponseChannelSinkStack {
        void AsyncProcessResponse(ITransportHeaders headers, Stream stream);
        void DispatchReplyMessage(IMessage msg);
        void DispatchException(Exception e);
    }

    /// <summary>Client sink stack.</summary>
    public interface IClientChannelSinkStack : IClientResponseChannelSinkStack {
        void Push(IClientChannelSink sink, object state);
        object Pop(IClientChannelSink sink);
    }

    /// <summary>Concrete client sink stack implementation.</summary>
    public class ClientChannelSinkStack : IClientChannelSinkStack {

        private struct SinkEntry {
            public IClientChannelSink Sink;
            public object State;
        }

        private readonly Stack<SinkEntry> m_stack = new Stack<SinkEntry>();
        private readonly IMessageSink m_replySink;

        public ClientChannelSinkStack() {
            m_replySink = null;
        }

        public ClientChannelSinkStack(IMessageSink replySink) {
            m_replySink = replySink;
        }

        public void Push(IClientChannelSink sink, object state) {
            m_stack.Push(new SinkEntry { Sink = sink, State = state });
        }

        public object Pop(IClientChannelSink sink) {
            SinkEntry entry = m_stack.Pop();
            return entry.State;
        }

        public void AsyncProcessResponse(ITransportHeaders headers, Stream stream) {
            if (m_stack.Count > 0) {
                SinkEntry entry = m_stack.Pop();
                entry.Sink.AsyncProcessResponse(this, entry.State, headers, stream);
            }
        }

        public void DispatchReplyMessage(IMessage msg) {
            if (m_replySink != null) {
                m_replySink.SyncProcessMessage(msg);
            }
        }

        public void DispatchException(Exception e) {
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  CHANNEL INTERFACES
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Base channel interface.</summary>
    public interface IChannel {
        string ChannelName { get; }
        int ChannelPriority { get; }
        string Parse(string url, out string objectURI);
    }

    /// <summary>Channel that can send messages (client side).</summary>
    public interface IChannelSender : IChannel {
        IMessageSink CreateMessageSink(string url, object remoteChannelData, out string objectURI);
    }

    /// <summary>Channel that can receive messages (server side).</summary>
    public interface IChannelReceiver : IChannel {
        object ChannelData { get; }
        string[] GetUrlsForUri(string objectURI);
        void StartListening(object data);
        void StopListening(object data);
    }

    /// <summary>Channel data store interface.</summary>
    public interface IChannelDataStore {
        string[] ChannelUris { get; set; }
    }

    /// <summary>Concrete channel data store.</summary>
    [Serializable]
    public class ChannelDataStore : IChannelDataStore {

        private string[] m_channelUris;

        public ChannelDataStore(string[] channelUrls) {
            m_channelUris = channelUrls;
        }

        public string[] ChannelUris {
            get { return m_channelUris; }
            set { m_channelUris = value; }
        }
    }

    // ???????????????????????????????????????????????????????????????????
    //  MESSAGE SINK
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Message sink for synchronous/asynchronous message processing.</summary>
    public interface IMessageSink {
        IMessage SyncProcessMessage(IMessage msg);
        IMessageCtrl AsyncProcessMessage(IMessage msg, IMessageSink replySink);
        IMessageSink NextSink { get; }
    }

    /// <summary>Message control — allows cancellation of async messages.</summary>
    public interface IMessageCtrl {
        void Cancel(int msToCancel);
    }

    // ???????????????????????????????????????????????????????????????????
    //  ACTIVATION (minimal — only used in a type check)
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Marker interface for construction call messages.
    /// IIOPChannel only uses this in an "is" type check to reject activation requests.</summary>
    public interface IConstructionCallMessage : IMethodCallMessage {
    }

    // ???????????????????????????????????????????????????????????????????
    //  LOGICAL THREAD AFFINATIVE
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Marker interface indicating data should flow with the logical thread.
    /// Used by CorbaContextElement to propagate CORBA context elements in the call chain.</summary>
    public interface ILogicalThreadAffinative {
    }

    // ???????????????????????????????????????????????????????????????????
    //  BASE CHANNEL PROPERTY CLASSES
    // ???????????????????????????????????????????????????????????????????

    /// <summary>Base class providing IDictionary properties for channel objects.</summary>
    public abstract class BaseChannelObjectWithProperties : IDictionary {

        private readonly Hashtable m_table = new Hashtable();

        public virtual object this[object key] {
            get { return m_table[key]; }
            set { m_table[key] = value; }
        }

        public virtual ICollection Keys { get { return m_table.Keys; } }
        public virtual ICollection Values { get { return m_table.Values; } }
        public virtual bool IsReadOnly { get { return false; } }
        public virtual bool IsFixedSize { get { return false; } }
        public virtual int Count { get { return m_table.Count; } }
        public virtual object SyncRoot { get { return m_table.SyncRoot; } }
        public virtual bool IsSynchronized { get { return false; } }

        public virtual bool Contains(object key) { return m_table.Contains(key); }
        public virtual void Add(object key, object value) { m_table.Add(key, value); }
        public virtual void Clear() { m_table.Clear(); }
        public virtual void Remove(object key) { m_table.Remove(key); }
        public virtual void CopyTo(Array array, int index) { m_table.CopyTo(array, index); }
        public virtual IDictionaryEnumerator GetEnumerator() { return m_table.GetEnumerator(); }

        IEnumerator IEnumerable.GetEnumerator() { return m_table.GetEnumerator(); }
    }

    /// <summary>Base for channel sink property bags.</summary>
    public abstract class BaseChannelSinkWithProperties : BaseChannelObjectWithProperties { }

    /// <summary>Base for channel property bags.</summary>
    public abstract class BaseChannelWithProperties : BaseChannelObjectWithProperties { }
}
