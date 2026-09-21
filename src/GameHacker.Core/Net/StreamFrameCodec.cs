using System.Buffers.Binary;

namespace GameHacker.Core.Net;

/// <summary>
/// QEMU <c>-netdev stream</c> 的成帧格式：4 字节大端长度 + 裸以太网帧。
/// </summary>
/// <remarks>
/// 格式已对 qemu-11.1.1 源码核实，不是猜的：
/// <list type="bullet">
///   <item><c>net/stream_data.c:34</c> — <c>uint32_t len = htonl(size)</c></item>
///   <item><c>net/net.c:2100</c> — <c>net_fill_rstate()</c> 的三态机：
///         先读 4 字节长度，再（若开了 vnet_hdr）读 4 字节 vnet 头长，最后读载荷</item>
/// </list>
/// 我们不开 vnet_hdr，所以只有一个长度字段。
/// </remarks>
public static class StreamFrameCodec
{
    public const int HeaderSize = 4;

    /// <summary>以太网最大帧长，用来识别成帧失步。</summary>
    public const int MaxFrameSize = 65535;

    public static void WriteHeader(Span<byte> destination, int frameLength) =>
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)frameLength);

    public static int ReadHeader(ReadOnlySpan<byte> source) =>
        (int)BinaryPrimitives.ReadUInt32BigEndian(source);

    /// <summary>
    /// 从累积缓冲区里切出一个完整帧。
    /// </summary>
    /// <returns>
    /// 切出的帧；若缓冲区里还不够一个完整帧则为 <c>null</c>。
    /// <paramref name="consumed"/> 返回本次消耗掉的字节数。
    /// </returns>
    /// <exception cref="InvalidDataException">长度字段越界，说明成帧已失步。</exception>
    public static ReadOnlyMemory<byte>? TryReadFrame(ReadOnlyMemory<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.Length < HeaderSize)
            return null;

        int length = ReadHeader(buffer.Span);
        if (length is < 0 or > MaxFrameSize)
            throw new InvalidDataException(
                $"帧长 {length} 越界，成帧已失步（是否误开了 vnet_hdr？）");

        if (buffer.Length < HeaderSize + length)
            return null;

        consumed = HeaderSize + length;
        return buffer.Slice(HeaderSize, length);
    }
}
