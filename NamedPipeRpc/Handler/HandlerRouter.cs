using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 基于委托的请求处理器适配。
/// </summary>
internal class RequestHandler<TRequest, TResponse> : IRequestHandler<TRequest, TResponse>
{
    readonly Func<RpcContext<TRequest>, CancellationToken, Task<TResponse>> _handler;

    public RequestHandler(string method, Func<RpcContext<TRequest>, CancellationToken, Task<TResponse>> func)
    {
        MethodName = method;
        _handler = func;
    }

    public string MethodName { get; }

    public Task<TResponse> Handle(RpcContext<TRequest> context, CancellationToken ct)
    {
        return _handler(context, ct);
    }
}

/// <summary>
/// 基于委托的通知处理器适配。
/// </summary>
internal class NotifyHandler<TMsg> : INotifyHandler<TMsg>
{
    readonly Func<RpcContext<TMsg>, CancellationToken, Task> _handler;

    public NotifyHandler(string method, Func<RpcContext<TMsg>, CancellationToken, Task> func)
    {
        MethodName = method;
        _handler = func;
    }

    public string MethodName { get; }

    public Task Handle(RpcContext<TMsg> context, CancellationToken ct)
    {
        return _handler(context, ct);
    }
}

/// <summary>
/// 按方法名注册并查找本端 Request / Notify Handler。
/// <para>
/// 查询对端已暴露方法、目录开闭事件均在 <see cref="NamedPipePeer"/> 侧经 Sys 完成，不在本类型暴露。
/// 本端 Add（新方法）/ Remove 成功时仅触发包内回调，供 <see cref="RpcService"/> 向 Peers 推送 Sys；
/// 同名覆盖已有 Handler 不触发 Opened。
/// </para>
/// </summary>
public class HandlerRouter
{
    readonly Dictionary<string, INotifyHandlerWrapper> _notifyHandlers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, IRequestHandlerWrapper> _requestHandlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>包内：新 Request 方法首次注册时回调（供 RpcService 推 <c>requestOpened</c>）。</summary>
    internal Action<RpcRequestMethodInfo>? OnRequestOpened;

    /// <summary>包内：Request 方法移除成功时回调（供 RpcService 推 <c>requestClosed</c>）。</summary>
    internal Action<RpcRequestMethodInfo>? OnRequestClosed;

    /// <summary>包内：新 Notify 方法首次注册时回调（供 RpcService 推 <c>notifyOpened</c>）。</summary>
    internal Action<RpcNotifyMethodInfo>? OnNotifyOpened;

    /// <summary>包内：Notify 方法移除成功时回调（供 RpcService 清订阅并推 <c>notifyClosed</c>）。</summary>
    internal Action<RpcNotifyMethodInfo>? OnNotifyClosed;

    /// <summary>按方法名获取通知处理器。</summary>
    public INotifyHandlerWrapper? GetNotifyHandler(string method)
    {
        return _notifyHandlers.TryGetValue(method, out var handler) ? handler : null;
    }

    /// <summary>按方法名获取请求处理器。</summary>
    public IRequestHandlerWrapper? GetRequestHandler(string method)
    {
        return _requestHandlers.TryGetValue(method, out var handler) ? handler : null;
    }

    /// <summary>移除请求处理器；成功时触发包内 <see cref="OnRequestClosed"/>。</summary>
    /// <returns>是否曾存在并已移除。</returns>
    public bool RemoveRequestHandler(string method)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        if (!_requestHandlers.TryGetValue(method, out var existing))
            return false;

        _requestHandlers.Remove(method);
        OnRequestClosed?.Invoke(ToRequestMethodInfo(existing));
        return true;
    }

    /// <summary>移除通知处理器；成功时触发包内 <see cref="OnNotifyClosed"/>。</summary>
    /// <returns>是否曾存在并已移除。</returns>
    public bool RemoveNotifyHandler(string method)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        if (!_notifyHandlers.TryGetValue(method, out var existing))
            return false;

        _notifyHandlers.Remove(method);
        OnNotifyClosed?.Invoke(ToNotifyMethodInfo(existing));
        return true;
    }

    /// <summary>注册强类型请求处理器。</summary>
    public void AddRequestHandler<TRequest, TResponse>(IRequestHandler<TRequest, TResponse> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        RegisterRequestHandler(handler.MethodName, new RequestHanderWrapper<TRequest, TResponse>(handler));
    }

    /// <summary>注册强类型通知处理器。</summary>
    public void AddNotifyHandler<TMsg>(INotifyHandler<TMsg> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        RegisterNotifyHandler(handler.MethodName, new NotifyHandlerWrapper<TMsg>(handler));
    }

    /// <summary>以异步委托注册请求处理器。</summary>
    public void AddRequestHandler<TRequest, TResponse>(string method, Func<RpcContext<TRequest>, CancellationToken, Task<TResponse>> func)
    {
        var h = new RequestHandler<TRequest, TResponse>(method, func);
        RegisterRequestHandler(method, new RequestHanderWrapper<TRequest, TResponse>(h));
    }

    /// <summary>以同步 Action 注册仅请求载荷的处理器（响应为 Unit）。</summary>
    public void AddRequestHandler<TRequest>(string method, Action<TRequest> action)
    {
        Func<RpcContext<TRequest>, CancellationToken, Task<Unit>> f = (ctx, ct) =>
        {
            action(ctx.Message);
            return Unit.Task;
        };
        AddRequestHandler(method, f);
    }

    /// <summary>以无参 Action 注册无载荷请求处理器。</summary>
    public void AddRequestHandler(string method, Action action)
    {
        Func<RpcContext<Unit>, CancellationToken, Task<Unit>> f = (ctx, ct) =>
        {
            action();
            return Unit.Task;
        };
        AddRequestHandler(method, f);
    }

    /// <summary>以异步委托注册通知处理器。</summary>
    public void AddNotifyHandler<TMsg>(string method, Func<RpcContext<TMsg>, CancellationToken, Task> func)
    {
        var h = new NotifyHandler<TMsg>(method, func);
        RegisterNotifyHandler(method, new NotifyHandlerWrapper<TMsg>(h));
    }

    /// <summary>以同步 Action 注册带载荷的通知处理器。</summary>
    public void AddNotifyHandler<TMsg>(string method, Action<TMsg> action)
    {
        Func<RpcContext<TMsg>, CancellationToken, Task> func = (ctx, ct) =>
        {
            action(ctx.Message);
            return Task.CompletedTask;
        };
        AddNotifyHandler(method, func);
    }

    /// <summary>以无参 Action 注册无载荷通知处理器。</summary>
    public void AddNotifyHandler(string method, Action action)
    {
        Func<RpcContext<Unit>, CancellationToken, Task> func = (ctx, ct) =>
        {
            action();
            return Task.CompletedTask;
        };
        AddNotifyHandler(method, func);
    }

    /// <summary>
    /// 写入 Request 路由表；仅当方法名此前不存在时触发 <see cref="OnRequestOpened"/>（同名覆盖不推）。
    /// </summary>
    void RegisterRequestHandler(string method, IRequestHandlerWrapper wrapper)
    {
        bool isNew = !_requestHandlers.ContainsKey(method);
        _requestHandlers[method] = wrapper;
        if (isNew)
            OnRequestOpened?.Invoke(ToRequestMethodInfo(wrapper));
    }

    /// <summary>
    /// 写入 Notify 路由表；仅当方法名此前不存在时触发 <see cref="OnNotifyOpened"/>（同名覆盖不推）。
    /// </summary>
    void RegisterNotifyHandler(string method, INotifyHandlerWrapper wrapper)
    {
        bool isNew = !_notifyHandlers.ContainsKey(method);
        _notifyHandlers[method] = wrapper;
        if (isNew)
            OnNotifyOpened?.Invoke(ToNotifyMethodInfo(wrapper));
    }

    /// <summary>
    /// 包内快照：构建本端已注册 Request 的 <see cref="RpcRequestMethodInfo"/> 数组（供 Sys listRequests 应答）。
    /// 不作为对外列表 API。
    /// </summary>
    internal RpcRequestMethodInfo[] SnapshotRequestMethodInfos()
    {
        var result = new RpcRequestMethodInfo[_requestHandlers.Count];
        int i = 0;
        foreach (var kv in _requestHandlers)
            result[i++] = ToRequestMethodInfo(kv.Value);
        return result;
    }

    /// <summary>
    /// 包内快照：构建本端已注册 Notify 的 <see cref="RpcNotifyMethodInfo"/> 数组（供 Sys listNotifies 应答）。
    /// 不作为对外列表 API。
    /// </summary>
    internal RpcNotifyMethodInfo[] SnapshotNotifyMethodInfos()
    {
        var result = new RpcNotifyMethodInfo[_notifyHandlers.Count];
        int i = 0;
        foreach (var kv in _notifyHandlers)
            result[i++] = ToNotifyMethodInfo(kv.Value);
        return result;
    }

    /// <summary>
    /// 包内快照：本端已注册 Notify 方法名（供客户端 <c>SubscribeLocalNotifyHandlersAsync</c>）。
    /// </summary>
    internal string[] SnapshotNotifyMethodNames()
    {
        var names = new string[_notifyHandlers.Count];
        int i = 0;
        foreach (var kv in _notifyHandlers)
            names[i++] = kv.Value.MethodName;
        return names;
    }

    /// <summary>
    /// 由 Handler 构建单条 Request Info（供开闭推送与 list 快照复用）。
    /// </summary>
    internal static RpcRequestMethodInfo ToRequestMethodInfo(IRequestHandlerWrapper handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        return new RpcRequestMethodInfo
        {
            MethodName = handler.MethodName,
            RequestTypeName = handler.RequestType.FullName,
            ResponseTypeName = handler.ResponseType.FullName,
        };
    }

    /// <summary>
    /// 由 Handler 构建单条 Notify Info（供 subscribe 成功应答与开闭推送）。
    /// </summary>
    internal static RpcNotifyMethodInfo ToNotifyMethodInfo(INotifyHandlerWrapper handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        return new RpcNotifyMethodInfo
        {
            MethodName = handler.MethodName,
            MsgTypeName = handler.MsgType.FullName,
        };
    }
}
