using System.Buffers.Binary;
using System.Net.NetworkInformation;
using PacketDotNet;

namespace GameHacker.Core.Net;

/// <summary>交换机上过了一帧的记录。</summary>
/// <param name="Index">全局序号，从 1 开始。</param>
/// <param name="Elapsed">相对交换机启动的时刻。</param>
/// <param name="SourcePort">从哪个端口进来的。</param>
/// <param name="Protocol">给玩家看的协议名（ARP / ICMP / TCP / UDP / 0x8100 …）。</param>
/// <param name="Summary">一行摘要，仿 Wireshark 的 Info 列。</param>
/// <param name="Bytes">原始帧，导出 pcap 时要用。</param>
public sealed record PacketRecord(
    long Index,
    TimeSpan Elapsed,
    int SourcePort,
    string Source,
    string Destination,
    string Protocol,
    string Summary,
    byte[] Bytes)
{
    public int Length => Bytes.Length;
}

/// <summary>
/// 挂在虚拟交换机上的抓包器：解协议、留一段环形缓冲、按需导出 pcap。
/// </summary>
/// <remarks>
/// <para>概要书里交换机是<b>唯一</b>的流量观察点，所以这层同时服务三件事：
/// 给玩家看的抓包界面、关卡判定、以及出问题时导出 pcap 拿 Wireshark 对照。</para>
/// <para><b>不自己写协议解析。</b> 用 PacketDotNet（MPL-2.0，文件级 copyleft，
/// 只要不改它的源文件就能链进闭源商业发行）。自己写 ARP/IP/TCP 的解析
/// 是在造轮子，而且边界情况会一直咬人。</para>
/// <para><b>线程。</b> <see cref="OnFrame"/> 在交换机的转发线程上被调用，
/// <see cref="PacketCaptured"/> 也就在那个线程上触发。UI 侧必须自己倒手到主线程。
/// 解析失败绝不能让转发线程抛异常 —— 畸形帧是玩家能造出来的东西。</para>
/// </remarks>
public sealed class PacketLog : IFrameObserver
{
    private readonly object _gate = new();
    private readonly Queue<PacketRecord> _records = new();
    private readonly int _capacity;
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private long _index;

    public PacketLog(int capacity = 4096) => _capacity = capacity;

    /// <summary>抓到一帧。<b>在交换机的转发线程上触发。</b></summary>
    public event Action<PacketRecord>? PacketCaptured;

    /// <summary>累计抓到多少帧（含已经被环形缓冲挤掉的）。</summary>
    public long Total { get { lock (_gate) return _index; } }

    public void OnFrame(int sourcePortId, ReadOnlySpan<byte> frame)
    {
        // 必须拷贝：ReadOnlySpan 指向交换机的复用缓冲区，出了这个方法就失效了
        byte[] bytes = frame.ToArray();

        PacketRecord record;
        lock (_gate)
        {
            record = Describe(++_index, DateTime.UtcNow - _startUtc, sourcePortId, bytes);
            _records.Enqueue(record);
            while (_records.Count > _capacity) _records.Dequeue();
        }

        PacketCaptured?.Invoke(record);
    }

    public IReadOnlyList<PacketRecord> Snapshot()
    {
        lock (_gate) return _records.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _records.Clear();
    }

    // --- 解析 ---------------------------------------------------------------

    private static PacketRecord Describe(long index, TimeSpan elapsed, int port, byte[] bytes)
    {
        string src = "?", dst = "?", proto = "?", info = "";
        try
        {
            var eth = (EthernetPacket)Packet.ParsePacket(LinkLayers.Ethernet, bytes);
            src = FormatMac(eth.SourceHardwareAddress);
            dst = FormatMac(eth.DestinationHardwareAddress);
            proto = eth.Type.ToString();

            switch (eth.PayloadPacket)
            {
                case ArpPacket arp:
                    proto = "ARP";
                    info = arp.Operation == ArpOperation.Request
                        ? $"谁是 {arp.TargetProtocolAddress}？告诉 {arp.SenderProtocolAddress}"
                        : $"{arp.SenderProtocolAddress} 在 {FormatMac(arp.SenderHardwareAddress)}";
                    break;

                case IPPacket ip:
                    src = ip.SourceAddress.ToString();
                    dst = ip.DestinationAddress.ToString();
                    (proto, info) = DescribeIp(ip);
                    break;

                default:
                    info = $"{bytes.Length} 字节";
                    break;
            }
        }
        catch (Exception ex)
        {
            // 畸形帧是玩家能造出来的东西，绝不能让它掀掉转发线程
            proto = "畸形";
            info = ex.GetType().Name;
        }

        return new PacketRecord(index, elapsed, port, src, dst, proto, info, bytes);
    }

    private static (string Protocol, string Info) DescribeIp(IPPacket ip) => ip.PayloadPacket switch
    {
        IcmpV4Packet icmp => ("ICMP",
            $"{IcmpName(icmp.TypeCode)} id={icmp.Id} seq={icmp.Sequence}"),

        // 客户机开机会自己发一串 IPv6 邻居发现，不认的话列里只剩个
        // 截断的 "Icm"，玩家完全看不懂那是什么
        IcmpV6Packet icmp6 => ("ICMPv6", icmp6.Type.ToString()),

        TcpPacket tcp => ("TCP",
            $"{tcp.SourcePort} → {tcp.DestinationPort} [{TcpFlags(tcp)}] "
            + $"seq={tcp.SequenceNumber} len={tcp.PayloadData?.Length ?? 0}"),

        UdpPacket udp => ("UDP",
            $"{udp.SourcePort} → {udp.DestinationPort} len={udp.PayloadData?.Length ?? 0}"),

        _ => (ip.Protocol.ToString(), $"{ip.TotalLength} 字节"),
    };

    /// <summary>PhysicalAddress.ToString() 出来是 525400000002，玩家读不了。</summary>
    private static string FormatMac(PhysicalAddress mac) =>
        string.Join(':', mac.GetAddressBytes().Select(b => b.ToString("x2")));

    private static string IcmpName(IcmpV4TypeCode code) => code switch
    {
        IcmpV4TypeCode.EchoRequest => "echo 请求",
        IcmpV4TypeCode.EchoReply => "echo 应答",
        _ => code.ToString(),
    };

    private static string TcpFlags(TcpPacket tcp)
    {
        var flags = new List<string>(4);
        if (tcp.Synchronize) flags.Add("SYN");
        if (tcp.Acknowledgment) flags.Add("ACK");
        if (tcp.Finished) flags.Add("FIN");
        if (tcp.Reset) flags.Add("RST");
        if (tcp.Push) flags.Add("PSH");
        return flags.Count == 0 ? "-" : string.Join(",", flags);
    }

    // --- pcap 导出 ----------------------------------------------------------

    /// <summary>
    /// 把当前缓冲写成 pcap，直接能用 Wireshark 打开。
    /// </summary>
    /// <remarks>
    /// 格式很小：24 字节全局头 + 每帧 16 字节记录头。
    /// 自己写比拉一个抓包库进来划算得多，而且不受平台限制。
    /// </remarks>
    public void WritePcap(string path)
    {
        var records = Snapshot();
        using var stream = File.Create(path);
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], 0xA1B2C3D4);  // 魔数
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], 2);          // 主版本
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], 4);          // 次版本
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], 0);          // 时区
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], 0);        // 精度
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 65535);    // snaplen
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], 1);        // LINKTYPE_ETHERNET
        stream.Write(header);

        Span<byte> entry = stackalloc byte[16];
        foreach (var record in records)
        {
            DateTime stamp = _startUtc + record.Elapsed;
            long unix = new DateTimeOffset(stamp, TimeSpan.Zero).ToUnixTimeSeconds();
            BinaryPrimitives.WriteUInt32LittleEndian(entry[..4], (uint)unix);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..8], (uint)(record.Elapsed.Microseconds
                                                                        + record.Elapsed.Milliseconds * 1000));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..12], (uint)record.Bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..16], (uint)record.Bytes.Length);
            stream.Write(entry);
            stream.Write(record.Bytes);
        }
    }
}
