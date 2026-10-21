using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Xanvil.NamedPipeRpc;

public sealed class IpcInMsg : ArrayBufferWriter<byte>, IDisposable
{
    public MsgHeader Header { get; }
    public string? Method { get; private set; }


    public IpcInMsg(byte[] buffer)
    {
        Header = MsgHeader.DeserializeFromSpan(buffer);
    }
    public void WriteMethod(byte[] buffer)
    {
        if (Header.MethodLength > 0)
        {
            Method = Encoding.UTF8.GetString(buffer, 0, Header.MethodLength);
        }
    }
    public object? DeserializePayload(Type type, JsonSerializerOptions? options = null)
    {
        if (type == typeof(Unit)) return Unit.Value;
        if (Header.PayloadLength <= 0) return null;
        var o = JsonSerializer.Deserialize(this.WrittenSpan, type, options);
        return o;
    }
    public string? DeserializePayloadString()
    {

        if (Header.PayloadLength <= 0) return null;
        return Encoding.UTF8.GetString(UnderlyingArray, 0, Header.PayloadLength);

    }
}
