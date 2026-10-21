namespace Xanvil.NamedPipeRpc;

public enum MessageType : byte
{
    None,
    Sys,
    Req,
    Ntf,
    Res,
    Can,
    Err,
    UnK

}
