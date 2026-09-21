using System.Buffers.Binary;

namespace GameHacker.Core.Net;

/// <summary>
/// 6 字节 MAC 地址的值类型封装，专门用来当字典键。
/// </summary>
/// <remarks>
/// 转发是每帧都要做的热路径，用 <c>byte[]</c> 当键会带来引用相等语义的坑
/// 和每帧一次的分配。这里压进一个 ulong（高 16 位补零），
/// 比较和哈希都是单指令，且完全无分配。
/// </remarks>
public readonly struct MacAddressKey : IEquatable<MacAddressKey>
{
    private readonly ulong _value;

    private MacAddressKey(ulong value) => _value = value;

    public static MacAddressKey From(ReadOnlySpan<byte> mac)
    {
        if (mac.Length < 6)
            throw new ArgumentException("MAC 地址至少需要 6 字节", nameof(mac));

        ulong v = (ulong)BinaryPrimitives.ReadUInt32BigEndian(mac) << 16
                  | BinaryPrimitives.ReadUInt16BigEndian(mac[4..]);
        return new MacAddressKey(v);
    }

    /// <summary>
    /// 首字节最低位为 1 表示组播（广播 ff:ff:.. 是其特例）。
    /// 这类帧一律泛洪，不查表。
    /// </summary>
    public bool IsGroupAddress => (_value & 0x0100_0000_0000UL) != 0;

    public bool Equals(MacAddressKey other) => _value == other._value;
    public override bool Equals(object? obj) => obj is MacAddressKey o && Equals(o);
    public override int GetHashCode() => _value.GetHashCode();

    public override string ToString()
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, _value);
        return string.Join(':', b[2..].ToArray().Select(x => x.ToString("x2")));
    }
}
