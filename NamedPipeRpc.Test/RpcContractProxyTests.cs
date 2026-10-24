extern alias ClientContracts;
extern alias ServerContracts;

using Xanvil.NamedPipeRpc;

using ClientExt = ClientContracts::Xanvil.NamedPipeRpc.RpcGeneratedExtensions;
using ServerExt = ServerContracts::Xanvil.NamedPipeRpc.RpcGeneratedExtensions;
using ClientBilling = ClientContracts::NamedPipeRpc.Test.ClientContracts.IBilling;
using ClientInventory = ClientContracts::NamedPipeRpc.Test.ClientContracts.IInventory;
using ClientOrderEvents = ClientContracts::NamedPipeRpc.Test.ClientContracts.IOrderEvents;
using ClientPayReq = ClientContracts::NamedPipeRpc.Test.ClientContracts.PayReq;
using ClientRefundReq = ClientContracts::NamedPipeRpc.Test.ClientContracts.RefundReq;
using ClientSkuReq = ClientContracts::NamedPipeRpc.Test.ClientContracts.SkuReq;
using ServerBilling = ServerContracts::NamedPipeRpc.Test.ServerContracts.IBilling;
using ServerBillingImpl = ServerContracts::NamedPipeRpc.Test.ServerContracts.BillingImpl;
using ServerInventory = ServerContracts::NamedPipeRpc.Test.ServerContracts.InventoryService;
using ServerOrderEvents = ServerContracts::NamedPipeRpc.Test.ServerContracts.IOrderEvents;

namespace NamedPipeRpc.Test;

/// <summary>
/// 契约 Handler / Proxy 跨程序集测试。两端各自生成扩展，通过 extern alias 消歧。
/// </summary>
public class RpcContractProxyTests
{
    static string NewPipeName() => "nprpc-contract-" + Guid.NewGuid().ToString("N");

    /// <summary>服务端 AddRequestHandler，客户端从连接上取 GetRequestProxy 调用。</summary>
    [Fact]
    public async Task RequestProxy_CrossAssembly_Works()
    {
        string pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        ServerExt.AddRequestHandler<ServerBilling>(serverRouter, _ => new ServerBillingImpl());

        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();
        try
        {
            ClientBilling billing = ClientExt.GetRequestProxy<ClientBilling>(peer);
            int n = await billing.PayAsync(new ClientPayReq { Amount = 41 });
            Assert.Equal(42, n);
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>按实现类注册其请求契约。</summary>
    [Fact]
    public async Task AddRequestHandler_ByConcreteClass_Works()
    {
        string pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        ServerExt.AddRequestHandler(serverRouter, _ => new ServerBillingImpl());

        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();
        try
        {
            int n = await ClientExt.GetRequestProxy<ClientBilling>(peer)
                .PayAsync(new ClientPayReq { Amount = 10 });
            Assert.Equal(11, n);
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>[RpcMethod] 整段名、空前缀与方法级前缀仍然对齐。</summary>
    [Fact]
    public async Task RpcMethod_Overrides_Work()
    {
        string pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        ServerExt.AddRequestHandler(serverRouter, _ => new ServerBillingImpl());
        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();
        try
        {
            ClientBilling billing = ClientExt.GetRequestProxy<ClientBilling>(peer);
            await billing.RefundAsync(new ClientRefundReq { Reason = "test" });
            await billing.PingAsync();
            Assert.Equal("ok", await billing.HealthAsync());
            Assert.Equal("3:x", await billing.DescribeAsync(3, "x"));
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>类上的 [RpcRequest] 用显式前缀与客户端接口对齐。</summary>
    [Fact]
    public async Task ClassContract_Works()
    {
        string pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        ServerExt.AddRequestHandler(serverRouter, _ => new ServerInventory());
        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();
        try
        {
            int stock = await ClientExt.GetRequestProxy<ClientInventory>(peer)
                .GetStockAsync(new ClientSkuReq { Sku = "A1" });
            Assert.Equal(7, stock);
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// 服务端 AddNotifyHandler 登记可接收的通知；客户端再 AddNotifyProxy 才会在连接后自动订阅。
    /// GetNotifyProxy 返回生成的代理，只向这一条连接发送。
    /// </summary>
    [Fact]
    public async Task NotifyProxy_ReachesSubscribedHandler()
    {
        string pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        ServerExt.AddNotifyHandler(serverRouter, _ => new ServerOrderEventsIdle());
        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        var received = new List<string>();
        var clientRouter = new HandlerRouter();
        ClientExt.AddNotifyHandler(clientRouter, _ => new ClientOrderEventsSink(received.Add));
        ClientExt.AddNotifyProxy<ClientOrderEvents>(clientRouter);
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();
        try
        {
            NamedPipePeer? serverPeer = null;
            await WaitUntilAsync(() =>
            {
                var peers = service.Peers.ToArray();
                if (peers.Length == 0)
                    return false;
                serverPeer = peers[0];
                return serverPeer.IsSubscribed("IOrderEvents.Updated");
            }, TimeSpan.FromSeconds(5), "客户端未能自动订阅");

            var outgoing = ServerExt.GetNotifyProxy<ServerOrderEvents>(serverPeer!);
            await outgoing.UpdatedAsync("hello-notify");
            await WaitUntilAsync(() => received.Count >= 1, TimeSpan.FromSeconds(5), "未收到通知");
            Assert.Equal("hello-notify", received[0]);
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>多条连接各自取出代理，调用发到对应的那一端。</summary>
    [Fact]
    public async Task MultiplePeers_EachProxyTargetsItsConnection()
    {
        string pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        using var service = new RpcService(pipe, serverRouter, new RpcServiceOption { MaxConnect = 2 });
        service.Start();

        var routerA = new HandlerRouter();
        var routerB = new HandlerRouter();
        ClientExt.AddRequestHandler(routerA, _ => new ClientBillingEcho());
        ClientExt.AddRequestHandler(routerB, _ => new ClientBillingEcho());
        using var clientA = new RpcClient(pipe, routerA);
        using var clientB = new RpcClient(pipe, routerB);
        var peerA = await clientA.ConnectAsync();
        var peerB = await clientB.ConnectAsync();
        try
        {
            await WaitUntilAsync(() => service.Peers.Count == 2, TimeSpan.FromSeconds(5), "未等到两个连接");

            var peers = service.Peers.ToArray();
            int a = await ServerExt.GetRequestProxy<ServerBilling>(peers[0]).PayAsync(
                new ServerContracts::NamedPipeRpc.Test.ServerContracts.PayReq { Amount = 1 });
            int b = await ServerExt.GetRequestProxy<ServerBilling>(peers[1]).PayAsync(
                new ServerContracts::NamedPipeRpc.Test.ServerContracts.PayReq { Amount = 2 });
            Assert.Equal(2, a);
            Assert.Equal(3, b);
        }
        finally
        {
            peerA.Close();
            peerB.Close();
            service.Close();
        }
    }

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failMessage)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException(failMessage);
    }
}

/// <summary>服务端只登记可接收的通知，不自动订阅。</summary>
file sealed class ServerOrderEventsIdle : ServerOrderEvents
{
    public Task UpdatedAsync(string msg, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>客户端通知处理。</summary>
file sealed class ClientOrderEventsSink : ClientOrderEvents
{
    readonly Action<string> _onMsg;
    public ClientOrderEventsSink(Action<string> onMsg) => _onMsg = onMsg;
    public Task UpdatedAsync(string msg, CancellationToken ct = default)
    {
        _onMsg(msg);
        return Task.CompletedTask;
    }
}

/// <summary>客户端请求服务，供服务端按连接回调。</summary>
file sealed class ClientBillingEcho : ClientBilling
{
    public Task<int> PayAsync(ClientPayReq req, CancellationToken ct = default)
        => Task.FromResult(req.Amount + 1);
    public Task RefundAsync(ClientRefundReq req, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> DescribeAsync(int id, string tag, CancellationToken ct = default) => Task.FromResult($"{id}:{tag}");
    public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> HealthAsync(CancellationToken ct = default) => Task.FromResult("ok");
}
