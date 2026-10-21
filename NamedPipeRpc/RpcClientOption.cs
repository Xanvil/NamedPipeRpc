namespace Xanvil.NamedPipeRpc;

public class RpcClientOption
{
    public byte Version { get; set; } = 1;
    public int MaxConcurrent { get; set; } = 64;
    public int SendBuffer { get; set; } = 64;
    public int ReceiveBuffer { get; set; } = 64;
    public int RequestTimeout { get; set; } = 30000;
}