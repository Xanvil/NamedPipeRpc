namespace Xanvil.NamedPipeRpc;

/// <summary>
/// RPC 系统命令方法名（MessageType.Sys）。
/// </summary>
public static class RpcSystemMethods
{
    /// <summary>查询对端已暴露的 Request 方法列表（应答为 <see cref="RpcRequestMethodInfo"/> 数组）。</summary>
    public const string ListRequests = "$rpc.listRequests";

    /// <summary>查询对端已暴露的 Notify 方法列表（应答为 <see cref="RpcNotifyMethodInfo"/> 数组）。</summary>
    public const string ListNotifies = "$rpc.listNotifies";

    /// <summary>订阅单个 Notify 方法（请求载荷为方法名 string；成功应答为 <see cref="RpcNotifyMethodInfo"/>）。</summary>
    public const string Subscribe = "$rpc.subscribe";

    /// <summary>取消订阅单个 Notify 方法（请求载荷为方法名 string）。</summary>
    public const string Unsubscribe = "$rpc.unsubscribe";

    /// <summary>服务端推送：Request 方法已开启（载荷为 <see cref="RpcRequestMethodInfo"/>）。</summary>
    public const string RequestOpened = "$rpc.requestOpened";

    /// <summary>服务端推送：Request 方法已关闭（载荷为 <see cref="RpcRequestMethodInfo"/>）。</summary>
    public const string RequestClosed = "$rpc.requestClosed";

    /// <summary>服务端推送：Notify 方法已开启（载荷为 <see cref="RpcNotifyMethodInfo"/>）。</summary>
    public const string NotifyOpened = "$rpc.notifyOpened";

    /// <summary>服务端推送：Notify 方法已关闭（载荷为 <see cref="RpcNotifyMethodInfo"/>）。</summary>
    public const string NotifyClosed = "$rpc.notifyClosed";
}

/// <summary>单条 Request 方法元数据。</summary>
public sealed class RpcRequestMethodInfo
{
    /// <summary>路由方法名。</summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>请求载荷类型 FullName。</summary>
    public string? RequestTypeName { get; set; }

    /// <summary>响应类型 FullName。</summary>
    public string? ResponseTypeName { get; set; }
}

/// <summary>单条 Notify 方法元数据。</summary>
public sealed class RpcNotifyMethodInfo
{
    /// <summary>路由方法名。</summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>通知载荷类型 FullName。</summary>
    public string? MsgTypeName { get; set; }
}

/// <summary>
/// 携带 <see cref="RpcRequestMethodInfo"/> 的事件参数。
/// 由 <see cref="NamedPipePeer"/> 在收到对端 Sys <see cref="RpcSystemMethods.RequestOpened"/> /
/// <see cref="RpcSystemMethods.RequestClosed"/> 时引发（非本端 Add/Remove）。
/// </summary>
public sealed class RpcRequestMethodEventArgs : EventArgs
{
    /// <summary>构造 Request 方法事件参数。</summary>
    public RpcRequestMethodEventArgs(RpcRequestMethodInfo method)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
    }

    /// <summary>对端变更的 Request 方法。</summary>
    public RpcRequestMethodInfo Method { get; }
}

/// <summary>
/// 携带 <see cref="RpcNotifyMethodInfo"/> 的事件参数。
/// 由 <see cref="NamedPipePeer"/> 在收到对端 Sys <see cref="RpcSystemMethods.NotifyOpened"/> /
/// <see cref="RpcSystemMethods.NotifyClosed"/> 时引发（非本端 Add/Remove）。
/// </summary>
public sealed class RpcNotifyMethodEventArgs : EventArgs
{
    /// <summary>构造 Notify 方法事件参数。</summary>
    public RpcNotifyMethodEventArgs(RpcNotifyMethodInfo method)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
    }

    /// <summary>对端变更的 Notify 方法。</summary>
    public RpcNotifyMethodInfo Method { get; }
}

/// <summary>
/// 仅携带方法名的事件参数。
/// 用于 <see cref="NamedPipePeer.NotifyNotFound"/>（订阅时期望 Notify 在对端不存在）
/// 与 <see cref="NamedPipePeer.RequestNotFound"/>（Request 收到 UnK）等客户端远端感知场景。
/// </summary>
public sealed class RpcMethodNameEventArgs : EventArgs
{
    /// <summary>构造方法名事件参数。</summary>
    public RpcMethodNameEventArgs(string methodName)
    {
        MethodName = methodName ?? throw new ArgumentNullException(nameof(methodName));
    }

    /// <summary>方法名。</summary>
    public string MethodName { get; }
}
