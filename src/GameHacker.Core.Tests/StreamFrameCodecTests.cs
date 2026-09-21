using GameHacker.Core.Net;

namespace GameHacker.Core.Tests;

/// <summary>
/// 成帧格式的回归护栏。
/// </summary>
/// <remarks>
/// 这些用例锁死的是 QEMU 的线上格式，不是我们的设计自由度：
/// 4 字节大端长度 + 裸以太网帧（qemu-11.1.1 的 net/stream_data.c:34
/// 与 net/net.c:2100）。QEMU 升级后若这里挂了，说明上游改了格式，
/// 必须去读源码而不是改断言。
/// </remarks>
public class StreamFrameCodecTests
{
    [Fact]
    public void Header_是大端序()
    {
        Span<byte> header = stackalloc byte[4];
        StreamFrameCodec.WriteHeader(header, 0x01020304);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, header.ToArray());
    }

    [Fact]
    public void 头部读写可往返()
    {
        Span<byte> header = stackalloc byte[4];
        StreamFrameCodec.WriteHeader(header, 1514);
        Assert.Equal(1514, StreamFrameCodec.ReadHeader(header));
    }

    [Fact]
    public void 缓冲区不足一整帧时返回 _null()
    {
        var buffer = new byte[] { 0, 0, 0, 10, 1, 2, 3 };   // 声称 10 字节，只给了 3
        Assert.Null(StreamFrameCodec.TryReadFrame(buffer, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void 只有半个头部时返回 _null()
    {
        Assert.Null(StreamFrameCodec.TryReadFrame(new byte[] { 0, 0 }, out int consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void 粘包时逐帧切出()
    {
        // 两个帧粘在一次 read 里 —— TCP 下这是常态，不是异常
        var wire = new List<byte>();
        foreach (var payload in new[] { new byte[] { 0xAA, 0xBB }, new byte[] { 0xCC } })
        {
            var header = new byte[4];
            StreamFrameCodec.WriteHeader(header, payload.Length);
            wire.AddRange(header);
            wire.AddRange(payload);
        }

        var buffer = (ReadOnlyMemory<byte>)wire.ToArray();

        var first = StreamFrameCodec.TryReadFrame(buffer, out int consumed1);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, first!.Value.ToArray());
        Assert.Equal(6, consumed1);

        var second = StreamFrameCodec.TryReadFrame(buffer[consumed1..], out int consumed2);
        Assert.Equal(new byte[] { 0xCC }, second!.Value.ToArray());
        Assert.Equal(5, consumed2);
    }

    [Fact]
    public void 长度越界时抛异常_而不是静默错位()
    {
        // 成帧一旦失步就再也恢复不了，必须立刻炸掉而不是继续读垃圾。
        // 现实里最常见的触发原因是误开了 vnet_hdr（多出一个长度字段）。
        var buffer = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3, 4 };
        Assert.Throws<InvalidDataException>(() =>
            StreamFrameCodec.TryReadFrame(buffer, out _));
    }
}
