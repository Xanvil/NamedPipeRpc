namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 请求与通知共用的 RPC 消息上下文：载荷与连接/消息元数据。
/// CancellationToken 不放入上下文，由 Handle 方法独立参数传入，避免双源。
/// </summary>
/// <typeparam name="T">消息载荷类型。</typeparam>
public sealed class RpcContext<T>
{
    /// <summary>反序列化后的消息载荷（请求体或通知体）。</summary>
    public T Message { get; }

    /// <summary>当前处理该消息的 Peer 连接。</summary>
    public NamedPipePeer Peer { get; }

    /// <summary>RPC 方法名。</summary>
    public string Method { get; }

    /// <summary>消息头中的 Id（请求用于关联响应，通知为通知 Id）。</summary>
    public long MessageId { get; }

    /// <summary>
    /// 构造 RPC 消息上下文。
    /// </summary>
    public RpcContext(T message, NamedPipePeer peer, string method, long messageId)
    {
        Message = message;
        Peer = peer;
        Method = method;
        MessageId = messageId;
    }
}
