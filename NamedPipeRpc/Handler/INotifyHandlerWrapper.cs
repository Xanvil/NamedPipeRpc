namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 非泛型通知处理器包装，供 Peer 按方法名调度。
/// </summary>
public interface INotifyHandlerWrapper
{
    /// <summary>路由方法名。</summary>
    string MethodName { get; }

    /// <summary>通知载荷类型（反序列化用）。</summary>
    Type MsgType { get; }

    /// <summary>
    /// 由 Peer 传入连接与消息元数据，内部组装 <see cref="RpcContext{T}"/> 后调用真实 Handler。
    /// </summary>
    Task Handle(NamedPipePeer peer, string method, long messageId, object msg, CancellationToken ct);
}
