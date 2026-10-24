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
    /// <summary>已登记实现、但未选择连接后自动订阅的 Notify 方法名。</summary>
    readonly HashSet<string> _notifyExposeOnly = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>调用过自动订阅登记的 Notify 方法名。之后再登记实现时仍保持自动订阅。</summary>
    readonly HashSet<string> _notifyAutoSubscribe = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>契约工厂。连接 Start 时逐个调用，为该连接创建处理器。</summary>
    readonly List<Action<NamedPipePeer>> _connectionActivators = new();
    /// <summary>已经 Start、尚未释放的连接。后登记的工厂也会在这些连接上创建实例。</summary>
    readonly List<NamedPipePeer> _activePeers = new();
    /// <summary>已经登记过工厂的契约类型，避免同一契约登记两次。</summary>
    readonly HashSet<Type> _connectionContracts = new();
    readonly object _connectionSync = new();

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
        _notifyExposeOnly.Remove(method);
        bool isNew = !_notifyHandlers.ContainsKey(method);
        _notifyHandlers[method] = wrapper;
        if (isNew)
            OnNotifyOpened?.Invoke(ToNotifyMethodInfo(wrapper));
    }

    /// <summary>
    /// 契约 <c>AddNotifyHandler</c> 登记实现之后调用。
    /// 本端可以接收该方法；尚未 <see cref="UseNotifyAutoSubscribe"/> 时不进入自动订阅清单。
    /// </summary>
    public void KeepNotifyLocal(string method)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        if (_notifyAutoSubscribe.Contains(method))
            return;
        _notifyExposeOnly.Add(method);
    }

    /// <summary>
    /// 连接建立后自动订阅该方法。方法已有 Handler 时才会出现在订阅快照里。
    /// </summary>
    public void UseNotifyAutoSubscribe(string method)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        _notifyAutoSubscribe.Add(method);
        _notifyExposeOnly.Remove(method);
    }

    /// <summary>
    /// 标记契约已登记工厂。同一契约重复登记时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public void ReserveConnectionContract(Type contractType)
    {
        if (contractType is null) throw new ArgumentNullException(nameof(contractType));
        lock (_connectionSync)
        {
            if (!_connectionContracts.Add(contractType))
                throw new InvalidOperationException($"契约 {contractType.FullName} 已登记处理器工厂。");
        }
    }

    /// <summary>
    /// 记住连接级工厂，并立刻对已经 Start 的连接各创建一次。
    /// </summary>
    public void AddConnectionFactory(Action<NamedPipePeer> activate)
    {
        if (activate is null) throw new ArgumentNullException(nameof(activate));
        NamedPipePeer[] peers;
        lock (_connectionSync)
        {
            _connectionActivators.Add(activate);
            peers = _activePeers.ToArray();
        }

        foreach (var peer in peers)
            activate(peer);
    }

    /// <summary>
    /// 连接 <see cref="NamedPipePeer.Start"/> 开头调用。为已登记的工厂各创建一个实例。
    /// </summary>
    internal void ActivateConnection(NamedPipePeer peer)
    {
        if (peer is null) throw new ArgumentNullException(nameof(peer));
        Action<NamedPipePeer>[] activators;
        lock (_connectionSync)
        {
            if (!_activePeers.Contains(peer))
                _activePeers.Add(peer);
            activators = _connectionActivators.ToArray();
        }

        foreach (var activate in activators)
            activate(peer);
    }

    /// <summary>连接释放时移出活动列表，避免之后的工厂再在这条连接上创建实例。</summary>
    internal void DeactivateConnection(NamedPipePeer peer)
    {
        if (peer is null) return;
        lock (_connectionSync)
            _activePeers.Remove(peer);
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
        int count = 0;
        foreach (var kv in _notifyHandlers)
        {
            if (!_notifyExposeOnly.Contains(kv.Key))
                count++;
        }
        var names = new string[count];
        int i = 0;
        foreach (var kv in _notifyHandlers)
        {
            if (_notifyExposeOnly.Contains(kv.Key))
                continue;
            names[i++] = kv.Value.MethodName;
        }
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
