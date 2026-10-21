namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 非泛型请求处理器包装，供 Peer 按方法名调度。
/// </summary>
public interface IRequestHandlerWrapper
{
    /// <summary>路由方法名。</summary>
    string MethodName { get; }

    /// <summary>请求载荷类型（反序列化用）。</summary>
    Type RequestType { get; }

    /// <summary>响应载荷类型（序列化用）。</summary>
    Type ResponseType { get; }

    /// <summary>
    /// 由 Peer 传入连接与消息元数据，内部组装 <see cref="RpcContext{T}"/> 后调用真实 Handler。
    /// </summary>
    Task<object> Handle(NamedPipePeer peer, string method, long messageId, object request, CancellationToken ct);
}
