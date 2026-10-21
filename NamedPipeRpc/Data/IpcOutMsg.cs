using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Xanvil.NamedPipeRpc;

public sealed class IpcOutMsg : ArrayBufferWriter<byte>, IDisposable
{

    public byte MethodLength { get; private set; } = 0;
    public int PayloadLength { get; private set; } = 0;
    public long Id { get; private set; } = 0;
    public IpcOutMsg()
    {
        Advance(MsgHeader.Fix_Length);
    }

    public void WriteMethod(string? method = null)
    {
        if (method != null)
        {
            int ml = Encoding.UTF8.GetBytes(method, 0, method.Length, this.UnderlyingArray, MsgHeader.Fix_Length);
            Advance(ml);
            if (ml > 255) throw new ArgumentOutOfRangeException("method 长度超出范围");
            MethodLength = (byte)ml;
        }
    }
    public void WritePayload(object? payload, Type type, JsonSerializerOptions? options = null)
    {
        if (payload != null && payload is not Unit)
        {

            using (var jsonw = new Utf8JsonWriter(this))
            {
                JsonSerializer.Serialize(jsonw, payload, type, options);
            }
            PayloadLength = WrittenCount - MsgHeader.Fix_Length - MethodLength;
        }
    }
    public void WriteHeader(long id, byte version, MessageType messageType)
    {
        Id = id;
        MsgHeader header = new MsgHeader(id, PayloadLength, MethodLength, version, messageType);
        header.SerializeToSpan(WritableSpan);
    }


}
