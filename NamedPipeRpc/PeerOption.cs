namespace Xanvil.NamedPipeRpc;

public class PeerOption
{
    public byte V { get; } = 1;
    public int MaxConcurrent { get; set; } = 64;
    public int SendBuffer { get; set; } = 64;
    public int ReceiveBuffer { get; set; } = 64;
    public int RequestTimeout { get; set; } = 30000;
    public PeerOption(byte v)
    {
        V = v;
    }
}
