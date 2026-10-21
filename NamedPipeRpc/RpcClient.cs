using System.IO.Pipes;

namespace Xanvil.NamedPipeRpc;

public class RpcClient : IDisposable
{
    public string PipeName { get; }
    public HandlerRouter Router { get; }
    RpcClientOption _option;
    PeerOption _peerOption;
    //CancellationTokenSource _cts;

    public RpcClient(string pipeName, HandlerRouter? router = null, RpcClientOption? option = null)
    {
        PipeName = pipeName;
        Router = router ?? new();
        _option = option ?? new();
        _peerOption = new PeerOption(_option.Version)
        {
            MaxConcurrent = _option.MaxConcurrent,
            SendBuffer = _option.SendBuffer,
            ReceiveBuffer = _option.ReceiveBuffer,
            RequestTimeout = _option.RequestTimeout,

        };
    }


    public async Task<NamedPipePeer> ConnectAsync(int timeout = -1, CancellationToken ct = default)
    {
        NamedPipeClientStream client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout, ct);

        var peer = new NamedPipePeer(client, Router, _peerOption);
        peer.Start();
        return peer;
    }

    bool _isDis = false;
    public void Dispose()
    {
        if (_isDis) return;
        _isDis = true;
        //_cts?.Cancel();
    }
}
