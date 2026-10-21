namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 带请求/响应类型的 RPC 请求处理器。
/// </summary>
public interface IRequestHandler<TRequest, TResponse>
{
    /// <summary>路由方法名。</summary>
    string MethodName { get; }

    /// <summary>
    /// 处理一次请求。取消令牌由独立参数传入，不放在上下文中。
    /// </summary>
    Task<TResponse> Handle(RpcContext<TRequest> context, CancellationToken ct);
}

/// <summary>
/// 仅有请求载荷、响应为 <see cref="Unit"/> 的处理器。
/// </summary>
public interface IRequestHandler<TRequest> : IRequestHandler<TRequest, Unit>
{

}

/// <summary>
/// 无载荷请求处理器（请求与响应均为 <see cref="Unit"/>）。
/// </summary>
public interface IRequestHandler : IRequestHandler<Unit, Unit>
{

}
