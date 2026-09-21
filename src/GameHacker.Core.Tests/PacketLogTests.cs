using System.Net;
using System.Net.NetworkInformation;
using GameHacker.Core.Net;
using PacketDotNet;

namespace GameHacker.Core.Tests;

/// <summary>
/// 抓包层。玩家看到的每一行都由这里产出，判定也读它，所以解析错了会同时坏两处。
/// </summary>
public class PacketLogTests
{
    private static readonly PhysicalAddress MacA = PhysicalAddress.Parse("52-54-00-00-00-01");
    private static readonly PhysicalAddress MacB = PhysicalAddress.Parse("52-54-00-00-00-02");
    private static readonly IPAddress IpA = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress IpB = IPAddress.Parse("10.0.0.2");

    [Fact]
    public void ARP_请求解析成谁是谁的问法()
    {
        var log = new PacketLog();
        var arp = new ArpPacket(ArpOperation.Request, PhysicalAddress.Parse("00-00-00-00-00-00"),
                                IpA, MacB, IpB);
        log.OnFrame(0, Wrap(arp, EthernetType.Arp).Bytes);

        var record = log.Snapshot().Single();
        Assert.Equal("ARP", record.Protocol);
        Assert.Contains("10.0.0.1", record.Summary);
        Assert.Contains("10.0.0.2", record.Summary);
    }

    [Fact]
    public void ICMP_echo_解析出类型与序号且源目的用_IP_而非_MAC()
    {
        var log = new PacketLog();
        var icmp = new IcmpV4Packet(new PacketDotNet.Utils.ByteArraySegment(new byte[8]))
        {
            TypeCode = IcmpV4TypeCode.EchoRequest,
            Id = 7,
            Sequence = 3,
        };
        var ip = new IPv4Packet(IpB, IpA) { Protocol = ProtocolType.Icmp, PayloadPacket = icmp };
        log.OnFrame(1, Wrap(ip, EthernetType.IPv4).Bytes);

        var record = log.Snapshot().Single();
        Assert.Equal("ICMP", record.Protocol);
        // 玩家关心的是 IP 层的谁 ping 谁，不是网卡地址
        Assert.Equal("10.0.0.2", record.Source);
        Assert.Equal("10.0.0.1", record.Destination);
        Assert.Contains("seq=3", record.Summary);
    }

    [Fact]
    public void MAC_按冒号分组而不是一长串十六进制()
    {
        // PhysicalAddress.ToString() 出来是 525400000002，玩家读不了
        // 注意 ArpPacket 的构造顺序是 (操作, 目标MAC, 目标IP, 发送方MAC, 发送方IP)
        var log = new PacketLog();
        var arp = new ArpPacket(ArpOperation.Response,
                                targetHardwareAddress: MacA, targetProtocolAddress: IpA,
                                senderHardwareAddress: MacB, senderProtocolAddress: IpB);
        log.OnFrame(0, Wrap(arp, EthernetType.Arp).Bytes);

        var record = log.Snapshot().Single();
        Assert.Equal("52:54:00:00:00:02", record.Source);          // 以太网头的源 MAC
        Assert.Contains("52:54:00:00:00:02", record.Summary);      // ARP 里的发送方 MAC
    }

    [Fact]
    public void 畸形帧不抛异常只标成畸形()
    {
        // 转发线程上抛异常会掀掉整台交换机，而畸形帧是玩家随手就能造出来的
        var log = new PacketLog();
        log.OnFrame(0, new byte[] { 1, 2, 3 });

        Assert.Equal("畸形", log.Snapshot().Single().Protocol);
    }

    [Fact]
    public void 超出容量时丢最老的但总数继续累加()
    {
        var log = new PacketLog(capacity: 3);
        for (int i = 0; i < 10; i++)
            log.OnFrame(0, Wrap(new ArpPacket(ArpOperation.Request,
                MacA, IpA, MacB, IpB), EthernetType.Arp).Bytes);

        Assert.Equal(3, log.Snapshot().Count);
        Assert.Equal(10, log.Total);
        Assert.Equal(8, log.Snapshot()[0].Index);      // 1..7 已经被挤掉
    }

    [Fact]
    public void 导出的_pcap_带正确的魔数与链路类型()
    {
        var log = new PacketLog();
        log.OnFrame(0, Wrap(new ArpPacket(ArpOperation.Request, MacA, IpA, MacB, IpB),
                            EthernetType.Arp).Bytes);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pcap");
        try
        {
            log.WritePcap(path);
            byte[] bytes = File.ReadAllBytes(path);

            Assert.Equal(0xD4, bytes[0]);                       // 小端魔数 a1b2c3d4
            Assert.Equal(0xC3, bytes[1]);
            Assert.Equal(1u, BitConverter.ToUInt32(bytes, 20));  // LINKTYPE_ETHERNET
            // 24 字节全局头 + 16 字节记录头 + 帧本身
            Assert.Equal(24 + 16 + log.Snapshot()[0].Length, bytes.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static EthernetPacket Wrap(Packet payload, EthernetType type) =>
        new(MacB, MacA, type) { PayloadPacket = payload };
}
