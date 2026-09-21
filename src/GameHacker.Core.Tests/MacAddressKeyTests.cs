using GameHacker.Core.Net;

namespace GameHacker.Core.Tests;

public class MacAddressKeyTests
{
    private static byte[] Mac(params int[] bytes) =>
        bytes.Select(b => (byte)b).ToArray();

    [Fact]
    public void 相同地址相等且哈希一致()
    {
        var a = MacAddressKey.From(Mac(0x52, 0x54, 0, 0, 0, 1));
        var b = MacAddressKey.From(Mac(0x52, 0x54, 0, 0, 0, 1));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void 不同地址不相等()
    {
        var a = MacAddressKey.From(Mac(0x52, 0x54, 0, 0, 0, 1));
        var b = MacAddressKey.From(Mac(0x52, 0x54, 0, 0, 0, 2));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 只看前六字节_多余部分忽略()
    {
        var frame = Mac(0x52, 0x54, 0, 0, 0, 1, 0xDE, 0xAD);   // 后面是源 MAC 的开头
        Assert.Equal(MacAddressKey.From(Mac(0x52, 0x54, 0, 0, 0, 1)),
                     MacAddressKey.From(frame));
    }

    [Theory]
    [InlineData(0xFF, true)]    // 广播
    [InlineData(0x01, true)]    // IPv4 组播 01:00:5e:..
    [InlineData(0x33, true)]    // IPv6 组播 33:33:..
    [InlineData(0x52, false)]   // 我们给客户机分配的单播 52:54:..
    public void 组播位识别正确(int firstByte, bool expected)
    {
        var key = MacAddressKey.From(Mac(firstByte, 0, 0, 0, 0, 0));
        Assert.Equal(expected, key.IsGroupAddress);
    }

    [Fact]
    public void ToString_是标准冒号格式()
    {
        Assert.Equal("52:54:00:00:00:01",
                     MacAddressKey.From(Mac(0x52, 0x54, 0, 0, 0, 1)).ToString());
    }
}
