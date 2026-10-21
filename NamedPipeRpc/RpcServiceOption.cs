using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Xanvil.NamedPipeRpc;

public class RpcServiceOption
{
    public int MaxConnect { get; set; } = -1;
    public byte Version { get; set; } = 1;
    public int MaxConcurrent { get; set; } = 64;
    public int SendBuffer { get; set; } = 64;
    public int ReceiveBuffer { get; set; } = 64;
    public int RequestTimeout { get; set; } = 30000;

}
