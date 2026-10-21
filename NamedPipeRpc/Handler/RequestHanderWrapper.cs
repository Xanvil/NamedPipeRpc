namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 将强类型 <see cref="IRequestHandler{TRequest,TResponse}"/> 适配为非泛型调度接口。
/// </summary>
internal sealed class RequestHanderWrapper<TRequest, TResponse> : IRequestHandlerWrapper
{
    readonly IRequestHandler<TRequest, TResponse> _handler;

    public string MethodName => _handler.MethodName;

    public Type RequestType { get; }

    public Type ResponseType { get; }

    public RequestHanderWrapper(IRequestHandler<TRequest, TResponse> handler)
    {
        _handler = handler;
        RequestType = typeof(TRequest);
        ResponseType = typeof(TResponse);
    }

    /// <inheritdoc />
    public async Task<object> Handle(NamedPipePeer peer, string method, long messageId, object request, CancellationToken ct)
    {
        TRequest req = (TRequest)request;
        var context = new RpcContext<TRequest>(req, peer, method, messageId);
        TResponse r = await _handler.Handle(context, ct);
        return r!;
    }
}
