namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 将强类型 <see cref="INotifyHandler{TMsg}"/> 适配为非泛型调度接口。
/// </summary>
public sealed class NotifyHandlerWrapper<TMsg> : INotifyHandlerWrapper
{
    readonly INotifyHandler<TMsg> _handler;

    public string MethodName => _handler.MethodName;

    public Type MsgType { get; }

    public NotifyHandlerWrapper(INotifyHandler<TMsg> handler)
    {
        _handler = handler;
        MsgType = typeof(TMsg);
    }

    /// <inheritdoc />
    public Task Handle(NamedPipePeer peer, string method, long messageId, object msg, CancellationToken ct)
    {
        TMsg m = (TMsg)msg;
        var context = new RpcContext<TMsg>(m, peer, method, messageId);
        return _handler.Handle(context, ct);
    }
}
