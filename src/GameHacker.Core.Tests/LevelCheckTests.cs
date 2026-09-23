using System.Net;
using System.Net.NetworkInformation;
using GameHacker.Core.Admin;
using GameHacker.Core.Levels;
using GameHacker.Core.Net;
using PacketDotNet;

namespace GameHacker.Core.Tests;

/// <summary>
/// 五个步骤各自的判定原语：隧道、扫描、拿取、掩盖、隐蔽。
/// </summary>
/// <remarks>
/// 帧都是真造出来再走一遍 <see cref="PacketLog"/>，和游戏里的路径一致；
/// 客户机状态与管理员结论则直接喂进 <see cref="LevelRun"/>，
/// 因为那两条路在真机上跑的那一半由 <c>AdminTtyIntegrationTests</c> 管。
/// </remarks>
public class LevelCheckTests
{
    private const int Outside = 10, Inside = 20;

    private static readonly LevelDefinition Jump = new()
    {
        Id = "jump", Title = "jump", Track = LevelTrack.Mission,
        Networks =
        [
            new NetworkDefinition { Name = "outside", Vlan = Outside, Subnet = "10.0.0.0/24" },
            new NetworkDefinition { Name = "inside", Vlan = Inside, Subnet = "172.16.5.0/24" },
        ],
        Machines =
        [
            Machine("ws", ("outside", "10.0.0.1")),
            Machine("jump01", ("outside", "10.0.0.2"), ("inside", "172.16.5.1")),
            Machine("files01", ("inside", "172.16.5.20")),
        ],
        Admin = new AdminDefinition
        {
            Machine = "jump01", User = "opsadm",
            Allow = new AdminAllow { Processes = ["syslogd*"] },
        },
    };

    private static MachineDefinition Machine(string name, params (string Network, string Ip)[] nics) => new()
    {
        Name = name,
        Nics = nics.Select(n => new NicDefinition { Network = n.Network, Ip = n.Ip }).ToList(),
    };

    private static LevelRun RunWith(LevelCheck check) => new(Jump with
    {
        Steps = [new LevelStep { Id = "s", Title = "s", Check = check }],
    });

    // --- 造帧 ---------------------------------------------------------------

    private static readonly PhysicalAddress MacA = PhysicalAddress.Parse("52-54-00-00-01-00");
    private static readonly PhysicalAddress MacB = PhysicalAddress.Parse("52-54-00-00-02-00");

    private static PacketRecord Record(Packet payload, EthernetType type, int vlan, TimeSpan at)
    {
        var eth = new EthernetPacket(MacA, MacB, type) { PayloadPacket = payload };
        // PacketLog 自己按真实时钟打时间戳，而扫描判定要看时间窗口，
        // 所以这里在记录上重写 Elapsed —— 测试要能自己掌控时间
        var log = new PacketLog();
        PacketRecord? record = null;
        log.PacketCaptured += r => record = r;
        log.OnFrame(0, vlan, eth.Bytes);
        return record! with { Elapsed = at };
    }

    private static PacketRecord Ip(string src, string dst, int vlan, double seconds = 0)
    {
        var ip = new IPv4Packet(IPAddress.Parse(src), IPAddress.Parse(dst))
        {
            Protocol = PacketDotNet.ProtocolType.Udp,
            PayloadPacket = new UdpPacket(1234, 9),
        };
        ip.UpdateCalculatedValues();
        return Record(ip, EthernetType.IPv4, vlan, TimeSpan.FromSeconds(seconds));
    }

    private static PacketRecord Arp(string sender, string target, int vlan, double seconds = 0)
    {
        var arp = new ArpPacket(ArpOperation.Request, PhysicalAddress.Parse("00-00-00-00-00-00"),
                                IPAddress.Parse(target), MacA, IPAddress.Parse(sender));
        return Record(arp, EthernetType.Arp, vlan, TimeSpan.FromSeconds(seconds));
    }

    // --- 建隧道 -------------------------------------------------------------

    [Fact]
    public void 隧道_内网里出现玩家机器的源地址_而且回得来()
    {
        var run = RunWith(new RouteCheck { From = "ws", Network = "inside" });

        // 包进去了，但还没有回程
        run.Observe(Ip("10.0.0.1", "172.16.5.20", Inside));
        Assert.Equal(0, run.CurrentIndex);
        Assert.Contains("没有回程", run.Remaining);

        run.Observe(Ip("172.16.5.20", "10.0.0.1", Inside));
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 隧道_外网里的同一对地址不算()
    {
        // ws 和跳板机在外网本来就通，那不是隧道
        var run = RunWith(new RouteCheck { From = "ws", Network = "inside" });
        run.Observe(Ip("10.0.0.1", "10.0.0.2", Outside));
        run.Observe(Ip("10.0.0.2", "10.0.0.1", Outside));
        Assert.Equal(0, run.CurrentIndex);
    }

    // --- 扫描 ---------------------------------------------------------------

    [Fact]
    public void 扫描_没人应答的地址也算_否则整片扫描恰好全被漏掉()
    {
        // 扫一个 /24，里面只有一台机器：只数 IP 包的话永远只数到 1
        var run = RunWith(new ScanCheck { Network = "inside", Hosts = 8, WithinSeconds = 60 });
        for (int host = 1; host <= 8; host++)
            run.Observe(Arp("172.16.5.1", $"172.16.5.{host}", Inside, host * 0.2));
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 扫描_慢慢一台台连不算扫描()
    {
        var run = RunWith(new ScanCheck { Network = "inside", Hosts = 8, WithinSeconds = 60 });
        for (int host = 1; host <= 20; host++)
            run.Observe(Arp("172.16.5.1", $"172.16.5.{host}", Inside, host * 30));
        Assert.Equal(0, run.CurrentIndex);
        // 窗口是滑动的：每 30 秒连一台，60 秒里最多同时留下三台，离扫描差得远
        Assert.Contains("3/8", run.Remaining);
    }

    [Fact]
    public void 扫描_只认这个网段里的地址()
    {
        var run = RunWith(new ScanCheck { Network = "inside", Hosts = 3, WithinSeconds = 60 });
        run.Observe(Ip("10.0.0.1", "10.0.0.7", Outside));
        run.Observe(Ip("10.0.0.1", "10.0.0.8", Outside));
        run.Observe(Ip("10.0.0.1", "10.0.0.9", Outside));
        Assert.Equal(0, run.CurrentIndex);
    }

    [Fact]
    public void 扫描_反复试同一台不算()
    {
        var run = RunWith(new ScanCheck { Network = "inside", Hosts = 3, WithinSeconds = 60 });
        for (int i = 0; i < 20; i++) run.Observe(Ip("10.0.0.1", "172.16.5.20", Inside, i));
        Assert.Equal(0, run.CurrentIndex);
    }

    // --- 拿取 ---------------------------------------------------------------

    [Fact]
    public void 拿取_文件在自己机器上才算()
    {
        string sha = new('a', 64);
        var run = RunWith(new FileCheck { Machine = "ws", Sha256 = sha });

        Assert.Equal(new StateQuery("ws") { FileSha = sha }, run.Wanted);

        run.Observe(new StateSnapshot("ws") { FileFound = false });
        Assert.Equal(0, run.CurrentIndex);

        // 别的机器上有那份文件不算 —— 目标是「复制回你的机器」
        run.Observe(new StateSnapshot("jump01") { FileFound = true });
        Assert.Equal(0, run.CurrentIndex);

        run.Observe(new StateSnapshot("ws") { FileFound = true });
        Assert.True(run.IsComplete);
    }

    // --- 掩盖 ---------------------------------------------------------------

    private static ProcessLine Process(string command, string user = "root", string tty = "?") =>
        new(700, user, tty, command);

    [Fact]
    public void 掩盖_白名单外的进程还在就不算清干净()
    {
        var run = RunWith(new CleanCheck { Machine = "jump01" });
        Assert.Equal(new StateQuery("jump01") { Processes = true, Forwarding = true }, run.Wanted);

        run.Observe(new StateSnapshot("jump01")
        {
            Processes = [Process("{init} /bin/sh /init"), Process("syslogd -n"), Process("nc -lk -p 8000 -e nc 172.16.5.20 9000")],
        });
        Assert.Equal(0, run.CurrentIndex);
        Assert.Contains("nc -lk", run.Remaining);

        run.Observe(new StateSnapshot("jump01")
        {
            Processes = [Process("{init} /bin/sh /init"), Process("syslogd -n")],
        });
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 掩盖_转发还开着也是痕迹()
    {
        var run = RunWith(new CleanCheck { Machine = "jump01" });
        run.Observe(new StateSnapshot("jump01") { Processes = [Process("syslogd -n")], Forwarding = true });
        Assert.Equal(0, run.CurrentIndex);
        Assert.Contains("转发还开着", run.Remaining);
    }

    [Fact]
    public void 掩盖_和管理员用同一份白名单()
    {
        // 关卡放行 syslogd，管理员也就不会报它；两边必须是同一份，
        // 否则会出现「系统说你清干净了，管理员却还是把你抓了」
        var run = RunWith(new CleanCheck { Machine = "jump01" });
        run.Observe(new StateSnapshot("jump01") { Processes = [Process("syslogd -n")] });
        Assert.True(run.IsComplete);
    }

    // --- 隐蔽 ---------------------------------------------------------------

    private static PatrolReport Patrol(params AdminFinding[] findings) =>
        new("jump01", false, [AdminCheck.Processes], findings, 0, false);

    [Fact]
    public void 隐蔽_要连着几次查岗都干净()
    {
        var run = RunWith(new PatrolCheck { Patrols = 2 });

        run.Observe(Patrol());
        Assert.Equal(0, run.CurrentIndex);
        Assert.Contains("1/2", run.Remaining);

        // 中间被看出一处，前面攒的不算了
        run.Observe(Patrol(new AdminFinding(AdminCheck.Processes, "nc", "", "有个不该在的进程")));
        Assert.Contains("0/2", run.Remaining);

        run.Observe(Patrol());
        run.Observe(Patrol());
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void 隐蔽_他什么都没看的那次不算数()
    {
        var run = RunWith(new PatrolCheck { Patrols = 1 });
        run.Observe(new PatrolReport("jump01", false, [], [], 0, false));
        Assert.Equal(0, run.CurrentIndex);
    }

    // --- 整关 ---------------------------------------------------------------

    [Fact]
    public void 五步按顺序推进_三条判定通路各管一段()
    {
        var level = Jump with
        {
            Steps =
            [
                new LevelStep { Id = "tunnel", Title = "建隧道", Check = new RouteCheck { From = "ws", Network = "inside" } },
                new LevelStep { Id = "scan", Title = "扫描", Check = new ScanCheck { Network = "inside", Hosts = 4, WithinSeconds = 60 } },
                new LevelStep { Id = "fetch", Title = "拿取", Check = new FileCheck { Machine = "ws", Sha256 = new string('b', 64) } },
                new LevelStep { Id = "cover", Title = "掩盖", Check = new CleanCheck { Machine = "jump01" } },
                new LevelStep { Id = "stealth", Title = "隐蔽", Check = new PatrolCheck { Patrols = 1 } },
            ],
        };
        var run = new LevelRun(level);
        var done = new List<string>();
        run.StepCompleted += s => done.Add(s.Id);

        // 第一步要的是包，喂状态和查岗结论都不该推进
        run.Observe(new StateSnapshot("ws") { FileFound = true });
        run.Observe(Patrol());
        Assert.Null(run.Wanted);

        run.Observe(Ip("10.0.0.1", "172.16.5.20", Inside));
        run.Observe(Ip("172.16.5.20", "10.0.0.1", Inside));
        for (int host = 1; host <= 4; host++) run.Observe(Arp("172.16.5.1", $"172.16.5.{host}", Inside, host));
        Assert.Equal(["tunnel", "scan"], done);

        // 到了拿取这步才开始想看客户机内部
        Assert.Equal("ws", run.Wanted?.Machine);
        run.Observe(new StateSnapshot("ws") { FileFound = true });

        Assert.Equal("jump01", run.Wanted?.Machine);
        run.Observe(new StateSnapshot("jump01") { Processes = [Process("syslogd -n")] });

        // 最后一步不问客户机：听管理员的
        Assert.Null(run.Wanted);
        run.Observe(Patrol());

        Assert.Equal(["tunnel", "scan", "fetch", "cover", "stealth"], done);
        Assert.True(run.IsComplete);
    }
}
