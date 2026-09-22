using System.Net;
using System.Net.NetworkInformation;
using GameHacker.Core.Levels;
using GameHacker.Core.Net;
using PacketDotNet;
using PacketDotNet.Utils;

namespace GameHacker.Core.Tests;

/// <summary>
/// 关卡推进：交换机上的帧喂进来，看步骤是否按预期前进。
/// </summary>
/// <remarks>帧走真实的 <see cref="PacketLog"/> 再交给判定，和游戏里的路径一致。</remarks>
public class LevelRunTests
{
    private static readonly LevelDefinition Lab = new()
    {
        Id = "lab", Title = "lab", Track = LevelTrack.Tutorial,
        Networks = [new NetworkDefinition { Name = "lan", Vlan = 1, Subnet = "10.0.0.0/24" }],
        Machines =
        [
            Machine("web01", ("lan", "10.0.0.1")),
            Machine("backup", ("lan", "10.0.0.2")),
        ],
        Steps =
        [
            new LevelStep { Id = "out", Title = "web01 ping backup", Check = new PingCheck { From = "web01", To = "backup" } },
            new LevelStep { Id = "back", Title = "backup ping web01", Check = new PingCheck { From = "backup", To = "web01" } },
        ],
    };

    private static MachineDefinition Machine(string name, params (string Network, string Ip)[] nics) => new()
    {
        Name = name,
        Nics = nics.Select(n => new NicDefinition { Network = n.Network, Ip = n.Ip }).ToList(),
    };

    private static PacketRecord Icmp(string src, string dst, IcmpV4TypeCode code)
    {
        var icmp = new IcmpV4Packet(new ByteArraySegment(new byte[8])) { TypeCode = code, Id = 7, Sequence = 1 };
        var ip = new IPv4Packet(IPAddress.Parse(src), IPAddress.Parse(dst))
        {
            Protocol = PacketDotNet.ProtocolType.Icmp,
            PayloadPacket = icmp,
        };
        var eth = new EthernetPacket(PhysicalAddress.Parse("52-54-00-00-00-01"),
                                     PhysicalAddress.Parse("52-54-00-00-00-02"), EthernetType.IPv4)
        {
            PayloadPacket = ip,
        };
        ip.UpdateCalculatedValues();

        var log = new PacketLog();
        PacketRecord? record = null;
        log.PacketCaptured += r => record = r;
        log.OnFrame(0, 1, eth.Bytes);
        return record!;
    }

    [Fact]
    public void 看到应答才算通_只有请求不算()
    {
        var run = new LevelRun(Lab);
        run.Observe(Icmp("10.0.0.1", "10.0.0.2", IcmpV4TypeCode.EchoRequest));
        Assert.Equal(0, run.CurrentIndex);

        // web01 ping backup 成功 = backup 回给 web01 的应答
        run.Observe(Icmp("10.0.0.2", "10.0.0.1", IcmpV4TypeCode.EchoReply));
        Assert.Equal(1, run.CurrentIndex);
    }

    [Fact]
    public void 只认当前这一步_后面的步骤不会被提前满足()
    {
        var run = new LevelRun(Lab);
        // 第二步的条件（backup ping web01 的应答）在第一步完成前出现，不算
        run.Observe(Icmp("10.0.0.1", "10.0.0.2", IcmpV4TypeCode.EchoReply));
        Assert.Equal(0, run.CurrentIndex);
    }

    [Fact]
    public void 全部完成时依次触发步骤完成和关卡完成()
    {
        var run = new LevelRun(Lab);
        var events = new List<string>();
        run.StepCompleted += s => events.Add(s.Id);
        run.Completed += () => events.Add("完成");

        run.Observe(Icmp("10.0.0.2", "10.0.0.1", IcmpV4TypeCode.EchoReply));
        run.Observe(Icmp("10.0.0.1", "10.0.0.2", IcmpV4TypeCode.EchoReply));
        run.Observe(Icmp("10.0.0.1", "10.0.0.2", IcmpV4TypeCode.EchoReply));   // 完成后再来不重复触发

        Assert.Equal(["out", "back", "完成"], events);
        Assert.True(run.IsComplete);
        Assert.Null(run.CurrentStep);
    }

    [Fact]
    public void 多网卡机器从哪块网卡应答都算()
    {
        // 跳板机一脚外网一脚内网：内网机器 ping 跳板机，应答来自跳板机的内网地址
        var level = new LevelDefinition
        {
            Id = "j", Title = "j", Track = LevelTrack.Mission,
            Networks =
            [
                new NetworkDefinition { Name = "outside", Vlan = 10, Subnet = "10.0.0.0/24" },
                new NetworkDefinition { Name = "inside", Vlan = 20, Subnet = "172.16.5.0/24" },
            ],
            Machines =
            [
                Machine("jump01", ("outside", "10.0.0.2"), ("inside", "172.16.5.1")),
                Machine("files01", ("inside", "172.16.5.20")),
            ],
            Steps = [new LevelStep { Id = "s", Title = "s", Check = new PingCheck { From = "files01", To = "jump01" } }],
        };
        var run = new LevelRun(level);
        run.Observe(Icmp("172.16.5.1", "172.16.5.20", IcmpV4TypeCode.EchoReply));
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 畸形帧不抛异常()
    {
        var bogus = new PacketRecord(1, TimeSpan.Zero, 0, 1, "?", "?", "ICMP", "", [0x52, 0x54, 0x00]);
        new LevelRun(Lab).Observe(bogus);
    }
}
