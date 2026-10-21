using System.Collections;

namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 线程安全的 <see cref="NamedPipePeer"/> 集合（Copy-on-Write）。
/// <para>
/// <see cref="Add"/> 成功时订阅 <see cref="NamedPipePeer.DisConnected"/>，连接关闭后自动移除并取消订阅。
/// 变更在锁内更新 <see cref="HashSet{T}"/> 并发布不可变快照；广播与枚举通过 <c>Volatile.Read</c> 无锁读取快照。
/// 带响应的 <see cref="Request{TRequest,TResponse}"/> 返回全部结果。
/// </para>
/// </summary>
public class PipePeers : ISet<NamedPipePeer>
{
    readonly object _sync = new();
    readonly HashSet<NamedPipePeer> _peers = new();
    /// <summary>对外只读快照；仅在持锁变更后整体替换。</summary>
    NamedPipePeer[] _snapshot = Array.Empty<NamedPipePeer>();

    /// <summary>当前连接数（无锁，读取最新快照长度）。</summary>
    public int Count => Volatile.Read(ref _snapshot).Length;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <summary>
    /// 加入 peer；成功时订阅断开事件并发布新快照。
    /// </summary>
    public bool Add(NamedPipePeer item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        lock (_sync)
        {
            if (!_peers.Add(item))
                return false;
            item.DisConnected += OnPeerDisconnected;
            PublishSnapshotNoLock();
            return true;
        }
    }

    /// <summary>
    /// 清空集合，取消所有断开订阅，并发布空快照。
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            foreach (var peer in _peers)
                peer.DisConnected -= OnPeerDisconnected;
            _peers.Clear();
            PublishSnapshotNoLock();
        }
    }

    /// <inheritdoc />
    public bool Contains(NamedPipePeer item)
    {
        lock (_sync) return _peers.Contains(item);
    }

    /// <inheritdoc />
    public void CopyTo(NamedPipePeer[] array, int arrayIndex)
    {
        var snap = Volatile.Read(ref _snapshot);
        Array.Copy(snap, 0, array, arrayIndex, snap.Length);
    }

    /// <inheritdoc />
    public void ExceptWith(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this))
        {
            Clear();
            return;
        }
        // 先物化 other，避免锁内枚举另一 PipePeers / 自身导致死锁
        var items = MaterializeOther(other);
        lock (_sync)
        {
            foreach (var peer in items)
            {
                if (_peers.Remove(peer))
                    peer.DisConnected -= OnPeerDisconnected;
            }
            PublishSnapshotNoLock();
        }
    }

    /// <summary>
    /// 枚举当前已发布快照（无锁）；不会在枚举时再分配数组。
    /// </summary>
    public IEnumerator<NamedPipePeer> GetEnumerator()
    {
        // 数组枚举器遍历的是不可变快照引用，无需加锁
        return ((IEnumerable<NamedPipePeer>)Volatile.Read(ref _snapshot)).GetEnumerator();
    }

    /// <inheritdoc />
    public void IntersectWith(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this))
            return;
        var keep = new HashSet<NamedPipePeer>(MaterializeOther(other));
        lock (_sync)
        {
            foreach (var peer in _peers)
            {
                if (!keep.Contains(peer))
                    peer.DisConnected -= OnPeerDisconnected;
            }
            _peers.IntersectWith(keep);
            PublishSnapshotNoLock();
        }
    }

    /// <inheritdoc />
    public bool IsProperSubsetOf(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this)) return false;
        var items = MaterializeOther(other);
        lock (_sync) return _peers.IsProperSubsetOf(items);
    }

    /// <inheritdoc />
    public bool IsProperSupersetOf(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this)) return false;
        var items = MaterializeOther(other);
        lock (_sync) return _peers.IsProperSupersetOf(items);
    }

    /// <inheritdoc />
    public bool IsSubsetOf(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this)) return true;
        var items = MaterializeOther(other);
        lock (_sync) return _peers.IsSubsetOf(items);
    }

    /// <inheritdoc />
    public bool IsSupersetOf(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this)) return true;
        var items = MaterializeOther(other);
        lock (_sync) return _peers.IsSupersetOf(items);
    }

    /// <inheritdoc />
    public bool Overlaps(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this))
            return Count > 0;
        var items = MaterializeOther(other);
        lock (_sync) return _peers.Overlaps(items);
    }

    /// <summary>
    /// 移除 peer；成功时取消订阅并发布新快照。
    /// </summary>
    public bool Remove(NamedPipePeer item)
    {
        if (item is null) return false;
        lock (_sync)
        {
            if (!_peers.Remove(item))
                return false;
            item.DisConnected -= OnPeerDisconnected;
            PublishSnapshotNoLock();
            return true;
        }
    }

    /// <inheritdoc />
    public bool SetEquals(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this)) return true;
        var items = MaterializeOther(other);
        lock (_sync) return _peers.SetEquals(items);
    }

    /// <inheritdoc />
    public void SymmetricExceptWith(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this))
        {
            Clear();
            return;
        }
        var items = MaterializeOther(other);
        lock (_sync)
        {
            foreach (var peer in items)
            {
                if (_peers.Remove(peer))
                {
                    peer.DisConnected -= OnPeerDisconnected;
                }
                else if (_peers.Add(peer))
                {
                    peer.DisConnected += OnPeerDisconnected;
                }
            }
            PublishSnapshotNoLock();
        }
    }

    /// <inheritdoc />
    public void UnionWith(IEnumerable<NamedPipePeer> other)
    {
        if (other is null) throw new ArgumentNullException(nameof(other));
        if (ReferenceEquals(other, this))
            return;
        var items = MaterializeOther(other);
        lock (_sync)
        {
            foreach (var peer in items)
            {
                if (_peers.Add(peer))
                    peer.DisConnected += OnPeerDisconnected;
            }
            PublishSnapshotNoLock();
        }
    }

    /// <summary>
    /// 在锁外物化 <paramref name="other"/>，避免持锁时枚举另一 <see cref="PipePeers"/> 死锁。
    /// </summary>
    static NamedPipePeer[] MaterializeOther(IEnumerable<NamedPipePeer> other)
    {
        if (other is NamedPipePeer[] arr)
            return arr;
        if (other is ICollection<NamedPipePeer> col)
        {
            var a = new NamedPipePeer[col.Count];
            col.CopyTo(a, 0);
            return a;
        }
        var list = new List<NamedPipePeer>();
        foreach (var p in other)
            list.Add(p);
        return list.ToArray();
    }

    /// <summary>
    /// 根据当前 <see cref="_peers"/> 发布新快照。调用方必须已持有 <see cref="_sync"/>。
    /// </summary>
    void PublishSnapshotNoLock()
    {
        if (_peers.Count == 0)
        {
            Volatile.Write(ref _snapshot, Array.Empty<NamedPipePeer>());
            return;
        }
        var arr = new NamedPipePeer[_peers.Count];
        _peers.CopyTo(arr);
        Volatile.Write(ref _snapshot, arr);
    }

    void ICollection<NamedPipePeer>.Add(NamedPipePeer item) => Add(item);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>peer 断开时自动从集合移除。</summary>
    void OnPeerDisconnected(NamedPipePeer peer) => Remove(peer);

    // ---- 广播发送：无锁读快照（与 NamedPipePeer 对齐；带响应的 Request 返回全部结果）----

    /// <summary>向每个 peer 发送无载荷通知（全量广播，不检查订阅）。</summary>
    public Task Notify(string method, CancellationToken ct = default)
        => Notify(method, Unit.Value, ct);

    /// <summary>向快照中每个 peer 发送通知（无锁；全量广播，不检查订阅）。</summary>
    public Task Notify<TMsg>(string method, TMsg msg, CancellationToken ct = default)
    {
        var peers = Volatile.Read(ref _snapshot);
        if (peers.Length == 0)
            return Task.CompletedTask;
        var tasks = new Task[peers.Length];
        for (int i = 0; i < peers.Length; i++)
            tasks[i] = peers[i].Notify(method, msg, ct);
        return Task.WhenAll(tasks);
    }

    /// <summary>
    /// 向已订阅指定 Notify 方法的 peer 发送无载荷通知（定向推送）。
    /// 未订阅的连接不会收到；与 <see cref="Notify"/> 全量广播相对。
    /// </summary>
    public Task NotifySubscribers(string method, CancellationToken ct = default)
        => NotifySubscribers(method, Unit.Value, ct);

    /// <summary>
    /// 向快照中已订阅 <paramref name="method"/> 的 peer 发送通知（无锁；定向推送）。
    /// 通过 <see cref="NamedPipePeer.IsSubscribed"/> 过滤；无订阅者时立即完成。
    /// </summary>
    public Task NotifySubscribers<TMsg>(string method, TMsg msg, CancellationToken ct = default)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));

        var peers = Volatile.Read(ref _snapshot);
        if (peers.Length == 0)
            return Task.CompletedTask;

        // 先统计订阅者，再分配任务数组，避免多余 List 分配
        int count = 0;
        for (int i = 0; i < peers.Length; i++)
        {
            if (peers[i].IsSubscribed(method))
                count++;
        }

        if (count == 0)
            return Task.CompletedTask;

        var tasks = new Task[count];
        int j = 0;
        for (int i = 0; i < peers.Length; i++)
        {
            if (!peers[i].IsSubscribed(method))
                continue;
            tasks[j++] = peers[i].Notify(method, msg, ct);
        }

        return Task.WhenAll(tasks);
    }

    /// <summary>向每个 peer 发起无载荷请求。</summary>
    public Task Request(string method, CancellationToken ct = default)
        => Request<Unit, Unit>(method, Unit.Value, ct);

    /// <summary>向每个 peer 发起请求（忽略响应内容，仅等待完成）。</summary>
    public Task Request<TRequest>(string method, TRequest req, CancellationToken ct = default)
        => Request<TRequest, Unit>(method, req, ct);

    /// <summary>
    /// 向快照中每个 peer 发起请求（无锁），返回全部响应（顺序与快照顺序一致）。
    /// </summary>
    public Task<TResponse[]> Request<TRequest, TResponse>(string method, TRequest req, CancellationToken ct = default)
    {
        var peers = Volatile.Read(ref _snapshot);
        if (peers.Length == 0)
            return Task.FromResult(Array.Empty<TResponse>());
        var tasks = new Task<TResponse>[peers.Length];
        for (int i = 0; i < peers.Length; i++)
            tasks[i] = peers[i].Request<TRequest, TResponse>(method, req, ct);
        return Task.WhenAll(tasks);
    }
}