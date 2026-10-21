using System.Runtime.InteropServices;

namespace Xanvil.NamedPipeRpc;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct MsgHeader
{
    public const int Fix_Length = 16;
    public readonly long Id;           // 8 bytes
    public readonly int PayloadLength; // 4 bytes
    public readonly byte MethodLength; // 1 byte
    public readonly byte V;            // 1 byte
    public readonly MessageType Type;  // 1 byte

    // 显式添加 1 字节填充，将总大小精准撑到 16 字节
    private readonly byte _reserved;

    public MsgHeader(long id, int payloadLength, byte methodLength, byte v, MessageType type)
    {
        Id = id;
        PayloadLength = payloadLength;
        MethodLength = methodLength;
        V = v;
        Type = type;
        _reserved = 0; // 保持默认值为 0
    }
    /// <summary>
    /// 将当前 Header 序列化到目标 Span 中（绝对零分配）
    /// </summary>
    public void SerializeToSpan(Span<byte> buffer)
    {

        // 1. 写入 Id (8 bytes) - 小端序
        buffer[0] = (byte)(Id);
        buffer[1] = (byte)(Id >> 8);
        buffer[2] = (byte)(Id >> 16);
        buffer[3] = (byte)(Id >> 24);
        buffer[4] = (byte)(Id >> 32);
        buffer[5] = (byte)(Id >> 40);
        buffer[6] = (byte)(Id >> 48);
        buffer[7] = (byte)(Id >> 56);

        // 2. 写入 PayloadLength (4 bytes)
        buffer[8] = (byte)(PayloadLength);
        buffer[9] = (byte)(PayloadLength >> 8);
        buffer[10] = (byte)(PayloadLength >> 16);
        buffer[11] = (byte)(PayloadLength >> 24);

        // 3. 写入剩余单字节字段
        buffer[12] = MethodLength;
        buffer[13] = V;
        buffer[14] = (byte)Type;
        buffer[15] = 0; // _reserved
    }

    /// <summary>
    /// 从 Span 中直接反序列化出 Header（绝对零分配）
    /// </summary>
    public static MsgHeader DeserializeFromSpan(ReadOnlySpan<byte> buffer)
    {
        // 1. 读取 Id (8 bytes)
        long id = (long)buffer[0]

                | ((long)buffer[1] << 8)
                | ((long)buffer[2] << 16)
                | ((long)buffer[3] << 24)
                | ((long)buffer[4] << 32)
                | ((long)buffer[5] << 40)
                | ((long)buffer[6] << 48)
                | ((long)buffer[7] << 56);

        // 2. 读取 PayloadLength (4 bytes)
        int payloadLength = buffer[8]

                          | (buffer[9] << 8)
                          | (buffer[10] << 16)
                          | (buffer[11] << 24);

        // 3. 读取单字节字段
        byte methodLength = buffer[12];
        byte v = buffer[13];
        MessageType type = (MessageType)buffer[14];

        return new MsgHeader(id, payloadLength, methodLength, v, type);
    }
}
