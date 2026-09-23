using System.Net;
using System.Net.NetworkInformation;
using GameHacker.Core.Admin;
using GameHacker.Core.Levels;
using GameHacker.Core.Net;
using PacketDotNet;

namespace GameHacker.Core.Tests;

/// <summary>
/// 检测原语是通用零件：不属于哪一关，可以被任何关卡挑出来拼。
/// </summary>
/// <remarks>
/// 这里测的正是「复用」本身：通用的流量原语、把条件拼起来的 all / any，
/// 以及那份对外导出的原语清单 —— 少登记一个零件，写关卡的人就不知道它存在。
/// </remarks>
public class CheckReuseTests
{
    private const int Office = 7, Core = 8;

    /// <summary>另一关：和跳板扫描毫无关系的拓扑，用来证明零件不认关卡。</summary>
    private static readonly LevelDefinition OtherLevel = new()
    {
        Id = "other", Title = "另一关", Track = LevelTrack.Mission,
        Networks =
        [
            new NetworkDefinition { Name = "office", Vlan = Office, Subnet = "192.168.4.0/24" },
            new NetworkDefinition { Name = "core", Vlan = Core, Subnet = "10.40.0.0/24" },
        ],
        Machines =
        [
            Machine("laptop", ("office", "192.168.4.10")),
            Machine("printer", ("office", "192.168.4.30")),
            Machine("db01", ("core", "10.40.0.9")),
        ],
    };

    private static MachineDefinition Machine(string name, params (string Network, string Ip)[] nics) => new()
    {
        Name = name,
        Nics = nics.Select(n => new NicDefinition { Network = n.Network, Ip = n.Ip }).ToList(),
    };

    private static LevelRun RunWith(LevelCheck check, LevelDefinition? level = null) =>
        new((level ?? OtherLevel) with
        {
            Steps = [new LevelStep { Id = "s", Title = "s", Check = check }],
        });

    // --- 造帧 ---------------------------------------------------------------

    private static PacketRecord Record(Packet payload, EthernetType type, int vlan, double seconds = 0)
    {
        var eth = new EthernetPacket(PhysicalAddress.Parse("52-54-00-00-01-00"),
                                     PhysicalAddress.Parse("52-54-00-00-02-00"), type) { PayloadPacket = payload };
        var log = new PacketLog();
        PacketRecord? record = null;
        log.PacketCaptured += r => record = r;
        log.OnFrame(0, vlan, eth.Bytes);
        return record! with { Elapsed = TimeSpan.FromSeconds(seconds) };
    }

    private static PacketRecord Tcp(string src, string dst, int port, int vlan, double seconds = 0)
    {
        var tcp = new TcpPacket((ushort)(40000 + port), (ushort)port) { Synchronize = true };
        var ip = new IPv4Packet(IPAddress.Parse(src), IPAddress.Parse(dst))
        {
            Protocol = PacketDotNet.ProtocolType.Tcp,
            PayloadPacket = tcp,
        };
        ip.UpdateCalculatedValues();
        return Record(ip, EthernetType.IPv4, vlan, seconds);
    }

    private static PacketRecord Arp(string sender, string target, int vlan, double seconds = 0) =>
        Record(new ArpPacket(ArpOperation.Request, PhysicalAddress.Parse("00-00-00-00-00-00"),
                             IPAddress.Parse(target), PhysicalAddress.Parse("52-54-00-00-01-00"),
                             IPAddress.Parse(sender)),
               EthernetType.Arp, vlan, seconds);

    private static PacketRecord IcmpReply(string src, string dst, int vlan, double seconds = 0)
    {
        var icmp = new IcmpV4Packet(new PacketDotNet.Utils.ByteArraySegment(new byte[8]))
        {
            TypeCode = IcmpV4TypeCode.EchoReply,
        };
        var ip = new IPv4Packet(IPAddress.Parse(src), IPAddress.Parse(dst))
        {
            Protocol = PacketDotNet.ProtocolType.Icmp,
            PayloadPacket = icmp,
        };
        ip.UpdateCalculatedValues();
        return Record(ip, EthernetType.IPv4, vlan, seconds);
    }

    // --- 通用的流量原语 -----------------------------------------------------

    [Fact]
    public void 玩家有没有连过那个端口()
    {
        var run = RunWith(new TrafficCheck
        {
            From = "laptop", To = "db01", Protocol = "TCP", Port = 5432,
        });

        run.Observe(Tcp("192.168.4.10", "10.40.0.9", 22, Core));     // 端口不对
        run.Observe(Tcp("192.168.4.30", "10.40.0.9", 5432, Core));   // 不是这台机器发的
        Assert.Equal(0, run.CurrentIndex);

        run.Observe(Tcp("192.168.4.10", "10.40.0.9", 5432, Core));
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 流量原语不写的字段就是不限()
    {
        // 「有人对着 db01 发过东西」—— 不管谁发的、什么协议
        var run = RunWith(new TrafficCheck { To = "db01" });
        run.Observe(Tcp("192.168.4.30", "10.40.0.9", 5432, Core));
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 流量原语可以要求看到好几次()
    {
        var run = RunWith(new TrafficCheck { To = "db01", Protocol = "TCP", Times = 3 });
        run.Observe(Tcp("192.168.4.10", "10.40.0.9", 5432, Core));
        run.Observe(Tcp("192.168.4.10", "10.40.0.9", 5432, Core));
        Assert.Equal(0, run.CurrentIndex);
        Assert.Contains("2/3", run.Remaining);

        run.Observe(Tcp("192.168.4.10", "10.40.0.9", 5432, Core));
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 流量原语认得_ARP_的地址()
    {
        // ARP 的地址在它自己的字段里，不在 IP 头上
        var run = RunWith(new TrafficCheck { From = "laptop", To = "printer", Protocol = "ARP" });
        run.Observe(Arp("192.168.4.10", "192.168.4.30", Office));
        Assert.True(run.IsComplete);
    }

    // --- 扫到目标 -----------------------------------------------------------

    [Fact]
    public void 扫过还不够_还要真的扫到目标()
    {
        var run = RunWith(new ScanCheck { Network = "office", Hosts = 4, WithinSeconds = 60, Finds = "printer" });

        // 挨个试了一片，但被试的那些地址上没有机器应答
        for (int host = 40; host < 50; host++)
            run.Observe(Arp("192.168.4.10", $"192.168.4.{host}", Office, host * 0.1));
        Assert.Equal(0, run.CurrentIndex);
        Assert.Contains("还没扫到 printer", run.Remaining);

        // printer 应了答 = 扫到了
        run.Observe(IcmpReply("192.168.4.30", "192.168.4.10", Office, 5));
        run.Observe(Arp("192.168.4.10", "192.168.4.51", Office, 5.1));
        Assert.True(run.IsComplete);
    }

    // --- 拼起来 -------------------------------------------------------------

    [Fact]
    public void all_要求全部满足_但不要求同时成立()
    {
        var run = RunWith(new AllCheck
        {
            Checks =
            [
                new TrafficCheck { From = "laptop", To = "db01", Protocol = "TCP", Port = 5432 },
                new PingCheck { From = "laptop", To = "printer" },
            ],
        });

        run.Observe(Tcp("192.168.4.10", "10.40.0.9", 5432, Core));
        Assert.Equal(0, run.CurrentIndex);

        // 第一条早就满足过了，不会因为后来没再出现就掉回去
        for (int i = 0; i < 5; i++) run.Observe(Arp("192.168.4.10", "192.168.4.77", Office, i));
        run.Observe(IcmpReply("192.168.4.30", "192.168.4.10", Office, 6));
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void any_满足任意一条就算()
    {
        var run = RunWith(new AnyCheck
        {
            Checks =
            [
                new PingCheck { From = "laptop", To = "db01" },
                new TrafficCheck { From = "laptop", To = "db01", Protocol = "TCP", Port = 5432 },
            ],
        });

        run.Observe(Tcp("192.168.4.10", "10.40.0.9", 5432, Core));
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 组合可以嵌套()
    {
        var run = RunWith(new AllCheck
        {
            Checks =
            [
                new AnyCheck
                {
                    Checks =
                    [
                        new TrafficCheck { To = "db01", Protocol = "TCP", Port = 5432 },
                        new TrafficCheck { To = "db01", Protocol = "TCP", Port = 3306 },
                    ],
                },
                new TrafficCheck { To = "printer", Protocol = "ARP" },
            ],
        });

        run.Observe(Tcp("192.168.4.10", "10.40.0.9", 3306, Core));
        Assert.Equal(0, run.CurrentIndex);
        run.Observe(Arp("192.168.4.10", "192.168.4.30", Office));
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 组合里已经满足的那几条不再去问客户机()
    {
        // 每次探查都要在客户机上真跑几条命令，问已经满足的那条纯属白跑
        var level = OtherLevel with
        {
            Admin = new AdminDefinition { Machine = "laptop", User = "ops" },
        };
        string sha = new('c', 64);
        var run = RunWith(new AllCheck
        {
            Checks =
            [
                new FileCheck { Machine = "laptop", Sha256 = sha },
                new CleanCheck { Machine = "laptop" },
            ],
        }, level);

        Assert.Equal(2, run.Wanted.Count);

        run.Observe(new StateSnapshot("laptop") { FileSha = sha, FileFound = true });
        var left = Assert.Single(run.Wanted);
        Assert.True(left.Processes);
        Assert.Null(left.FileSha);
    }

    [Fact]
    public void 一步里挂两份文件_不会互相认领()
    {
        string first = new('1', 64), second = new('2', 64);
        var run = RunWith(new AllCheck
        {
            Checks =
            [
                new FileCheck { Machine = "laptop", Sha256 = first },
                new FileCheck { Machine = "laptop", Sha256 = second },
            ],
        });

        run.Observe(new StateSnapshot("laptop") { FileSha = first, FileFound = true });
        run.Observe(new StateSnapshot("laptop") { FileSha = first, FileFound = true });
        Assert.Equal(0, run.CurrentIndex);

        run.Observe(new StateSnapshot("laptop") { FileSha = second, FileFound = true });
        Assert.True(run.IsComplete);
    }

    // --- 导出的清单 ---------------------------------------------------------

    [Fact]
    public void 每个原语都登记在清单里()
    {
        // 清单是写关卡的人唯一的目录。加了零件却不登记，等于没人知道它存在
        Assert.Empty(CheckTypes.Missing);
    }

    [Fact]
    public void 清单里的例子都是能直接用的关卡片段()
    {
        foreach (var type in CheckTypes.All)
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<LevelCheck>(
                type.Example, LevelCatalog.JsonOptions);
            Assert.NotNull(parsed);
            Assert.Equal(type.Clr, parsed.GetType());
        }
    }

    [Fact]
    public void 文档里那张表和清单对得上()
    {
        // 表是从清单生成的，但文档是手抄过去的：漏抄一行就会和代码对不上
        string doc = File.ReadAllText(
            Path.Combine(TestImages.RepoRoot, "docs", "关卡系统.md"));
        foreach (var type in CheckTypes.All)
            Assert.True(doc.Contains($"| `{type.Type}` |"), $"文档里没有 {type.Type} 这一行");
    }
}
