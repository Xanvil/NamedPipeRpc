namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 带通知载荷类型的 RPC 通知处理器。
/// </summary>
public interface INotifyHandler<TMsg>
{
    /// <summary>路由方法名。</summary>
    string MethodName { get; }

    /// <summary>
    /// 处理一次通知。取消令牌由独立参数传入，不放在上下文中。
    /// </summary>
    Task Handle(RpcContext<TMsg> context, CancellationToken ct);
}

/// <summary>
/// 无载荷通知处理器（载荷为 <see cref="Unit"/>）。
/// </summary>
public interface INotifyHandler : INotifyHandler<Unit>
{

}
