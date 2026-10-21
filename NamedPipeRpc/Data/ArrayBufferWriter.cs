using System.Buffers;

namespace Xanvil.NamedPipeRpc;

public class ArrayBufferWriter<T> : IBufferWriter<T>
{
    private T[] _buffer;
    private int _index;

    public ArrayBufferWriter(int initialCapacity = 256)
    {
        _buffer = ArrayPool<T>.Shared.Rent(initialCapacity);
        _index = 0;
    }

    public ReadOnlyMemory<T> WrittenMemory => _buffer.AsMemory(0, _index);
    public ReadOnlySpan<T> WrittenSpan => _buffer.AsSpan(0, _index);
    public int WrittenCount => _index;
    public int Capacity => _buffer.Length;

    public T[] UnderlyingArray => _buffer;

    /// <summary>
    /// 【新增】获取已写入区域的可写 Span 视图。
    /// 允许在序列化完成后，回过头来修改已写入的数据（例如填充协议头）。
    /// </summary>
    public Span<T> WritableSpan => _buffer.AsSpan(0, _index);

    /// <summary>
    /// 【新增】获取已写入区域的可写 Memory 视图。
    /// </summary>
    public Memory<T> WritableMemory => _buffer.AsMemory(0, _index);


    public void Advance(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (_index + count > _buffer.Length)
            throw new InvalidOperationException("Buffer ran out of space.");
        _index += count;
    }

    public Memory<T> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_index);
    }

    public Span<T> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_index);
    }

    public void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (sizeHint == 0) sizeHint = 1;

        int availableSpace = _buffer.Length - _index;
        if (availableSpace >= sizeHint) return;

        int newSize = Math.Max(_buffer.Length * 2, _index + sizeHint);
        T[] newBuffer = ArrayPool<T>.Shared.Rent(newSize);
        Array.Copy(_buffer, 0, newBuffer, 0, _index);
        ArrayPool<T>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    public void Dispose()
    {
        if (_buffer != null)
        {
            ArrayPool<T>.Shared.Return(_buffer);
            _buffer = null!;
        }
    }
}