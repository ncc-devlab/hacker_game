namespace GameHacker.Core.Net;

/// <summary>以太网帧头的最小视图。交换机只需要这几样。</summary>
public readonly ref struct EthernetFrame
{
    /// <summary>目的 MAC(6) + 源 MAC(6) + EtherType(2)。</summary>
    public const int HeaderSize = 14;

    public EthernetFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
            throw new ArgumentException($"以太网帧至少 {HeaderSize} 字节", nameof(bytes));
        Bytes = bytes;
    }

    public ReadOnlySpan<byte> Bytes { get; }
    public MacAddressKey Destination => MacAddressKey.From(Bytes);
    public MacAddressKey Source => MacAddressKey.From(Bytes[6..]);
    public ushort EtherType => (ushort)(Bytes[12] << 8 | Bytes[13]);

    /// <summary>带 802.1Q（0x8100）或 802.1ad QinQ（0x88A8）标签。</summary>
    public bool IsVlanTagged => EtherType is 0x8100 or 0x88A8;

    public static bool IsValid(ReadOnlySpan<byte> bytes) => bytes.Length >= HeaderSize;
}
