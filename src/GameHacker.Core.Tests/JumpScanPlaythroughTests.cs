using System.Text;
using GameHacker.Core.Admin;
using GameHacker.Core.Channels;
using GameHacker.Core.Levels;
using GameHacker.Core.Net;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 把跳板扫描这一关从头到尾打一遍：三台真虚拟机、真交换机、真管理员。
/// </summary>
/// <remarks>
/// <para>关卡读的是仓库里那份 <c>jump-scan.json</c> 本体，所以这个测试同时在验
/// 关卡内容 —— 目标文件的哈希写错了、备份服务的端口写错了，这里都会红。</para>
/// <para>「玩家」就是往客户机的控制台串口里敲字，和真人按键盘走的是同一条路。
/// 判定那边也照游戏里的接法：网络看交换机，客户机内部状态在敲完命令之后问一次，
/// 最后一步听管理员的结论。</para>
/// <para>三台机器纯 TCG 跑，整个流程两三分钟，所以这是个慢测试。</para>
/// </remarks>
public class JumpScanPlaythroughTests
{
    private static readonly TimeSpan Boot = TimeSpan.FromSeconds(120);

    [SkippableFact]
    public async Task 整关能真的一步步走完_没清的痕迹由管理员找出来()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        var level = LoadJumpScan();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        var packets = new PacketLog();
        await using var vSwitch = new VirtualSwitch(LevelTopology.Vlans(level), packets);
        _ = vSwitch.RunAsync(cts.Token);

        var run = new LevelRun(level);
        var done = new List<string>();
        run.StepCompleted += s => { lock (done) done.Add(s.Id); };
        packets.PacketCaptured += run.Observe;

        // --- 开机 -----------------------------------------------------------
        var account = new AdminAccount(level.Admin!.User, "Zx7-quiet-lane");
        var vms = new List<QemuLauncher>();
        var controls = new Dictionary<string, ControlChannel>();
        var consoles = new Dictionary<string, StringBuilder>();

        string Console(string machine)
        {
            lock (consoles[machine]) return consoles[machine].ToString();
        }

        string Why(string what) =>
            $"{what}\n当前这步还差: {run.Remaining}\n--- ws ---\n{Tail(Console("ws"))}\n--- jump01 ---\n{Tail(Console("jump01"))}"
            + $"\n--- files01 ---\n{Tail(Console("files01"))}"
            + $"\n--- 交换机最后 20 帧（共 {packets.Total}）---\n"
            + string.Join('\n', packets.Snapshot().TakeLast(20).Select(r => $"vlan{r.Vlan} {r.Source} -> {r.Destination} {r.Protocol} {r.Summary}"));
        try
        {
            for (int i = 0; i < level.Machines.Count; i++)
            {
                var m = level.Machines[i];
                var vm = new QemuLauncher(TestImages.QemuPath);
                vms.Add(vm);
                vm.Start(new VmSpec
                {
                    Name = m.Name,
                    KernelPath = TestImages.Kernel,
                    InitrdPath = TestImages.Initrd,
                    // 主角机的完整 Alpine 盘不是这一关判定的一部分，纯内存起更快
                    Nics = LevelTopology.Nics(level, i)
                        .Select(n => new VmNic(n.Mac, vSwitch.PortFor(n.Vlan), n.IpWithPrefix)).ToList(),
                    Gateway = m.Gateway,
                    Files = m.Files.Select(f => new GuestFile(f.Path, f.Text)).ToList(),
                    Services = m.Services.Select(s => new GuestService(s.Port, s.File)).ToList(),
                    Admin = level.Admin.Machine == m.Name ? account : null,
                });
                // 留一份控制台输出：这个测试一旦红了，没有它就只知道「第几步没过」，
                // 不知道客户机上那条命令到底说了什么
                var text = new StringBuilder();
                consoles[m.Name] = text;
                vm.Console!.DataReceived += d => { lock (text) text.Append(Encoding.UTF8.GetString(d.Span)); };
                _ = vm.Console!.RunAsync(cts.Token);
                _ = vm.Control!.RunAsync(cts.Token);
                if (vm.Admin is not null) _ = vm.Admin.RunAsync(cts.Token);
                controls[m.Name] = new ControlChannel(vm.Control!);
            }

            foreach (var (name, control) in controls)
            {
                await control.WaitReadyAsync(Boot, cts.Token);
                await control.WaitEventAsync("console", Boot, cts.Token);
                _ = name;
            }

            async Task Type(string machine, string command)
            {
                var vm = vms[level.Machines.ToList().FindIndex(m => m.Name == machine)];
                await vm.Console!.SendAsync(Encoding.UTF8.GetBytes(command + "\n"), cts.Token);
                await Task.Delay(1200, cts.Token);
                await Probe();
            }

            // 判定要看客户机内部状态时才去问，而且只在玩家敲完一条命令之后问一次 ——
            // 游戏里 Level.NudgeProbe 做的是同一件事
            async Task Probe()
            {
                foreach (var query in run.Wanted)
                    run.Observe(await StateProbe.AskAsync(query, controls[query.Machine], cts.Token));
            }

            // 客户机的 getty 要在就绪信标之后一瞬间才把 shell 接到 tty 上，
            // 这中间送进去的字会被丢掉（实测偶发）。真人敲下去没反应会再敲一次，这里也一样
            async Task TypeUntil(string machine, string command, Func<Task<bool>> ok)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    await Type(machine, command);
                    if (await ok()) return;
                    await Task.Delay(1000, cts.Token);
                }
                Assert.Fail(Why($"{machine} 上这条命令一直没生效: {command}"));
            }

            async Task<bool> Forwarding() =>
                (await StateProbe.AskAsync(new StateQuery("jump01") { Forwarding = true },
                                           controls["jump01"], cts.Token)).Forwarding is true;

            // --- 一、建隧道 --------------------------------------------------
            // 玩家把跳板机变成路由器，再让自己的机器知道内网要走它
            await TypeUntil("jump01", "echo 1 > /proc/sys/net/ipv4/ip_forward", Forwarding);
            Assert.Empty(done);                       // 还没有包进去，不算通

            await TypeUntil("ws", "ip route add 172.16.5.0/24 via 10.0.0.2", async () =>
            {
                await Type("ws", "ping -c2 -W2 172.16.5.20");
                await Task.Delay(2000, cts.Token);
                return Snapshot(done).Contains("tunnel");
            });
            Assert.True(Snapshot(done) is ["tunnel"], Why("建隧道没判定通过"));

            // --- 二、扫描 ----------------------------------------------------
            // 一片地址挨个试过去。绝大多数地址上没有机器，试探止步于没人应答的 ARP ——
            // 判定必须把这些也算上，否则最像扫描的那部分恰好全被漏掉
            await Type("ws", "for i in $(seq 1 30); do ping -c1 -W1 172.16.5.$i >/dev/null 2>&1 & done");
            await Task.Delay(8000, cts.Token);
            Assert.True(Snapshot(done).Contains("scan"), Why("扫描没判定通过"));

            // --- 三、拿取 ----------------------------------------------------
            // 内网那台机器上的备份口连上就给。名字随便起，判定只认内容
            await Type("ws", "nc 172.16.5.20 9000 > /root/n.txt");
            await Task.Delay(2000, cts.Token);
            await Probe();
            Assert.True(Snapshot(done).Contains("fetch"), Why("拿取没判定通过"));

            // --- 四、隐蔽 ----------------------------------------------------
            // 「把痕迹清掉」不在任务清单里。玩家要是不管它，就由管理员来告诉他后果
            var adminVm = vms[level.Machines.ToList().FindIndex(m => m.Name == level.Admin.Machine)];
            using var tty = new TtySession(adminVm.Admin!);
            // 查的还是关卡里写的那几项，只是把概率提到 1，免得这个测试靠运气
            var certain = level.Admin with
            {
                Routine = [.. level.Admin.Routine.Select(a => a with { Chance = 1 })],
                Schedule = level.Admin.Schedule with { PauseMin = 0.2, PauseMax = 0.4 },
            };
            var admin = new AdminAgent(certain, account, tty, seed: 7);
            admin.PatrolCompleted += run.Observe;
            admin.EnterStage("stealth");

            // 玩家把跳板机变成了路由器，走之前没关回去 —— 他看得出来
            var caught = await admin.PatrolAsync(cts.Token);
            Assert.True(caught.FoundSomething,
                        Why($"转发还开着，管理员却什么也没看出来（他查了 {string.Join("、", caught.Did)}）"));
            Assert.Contains(caught.Findings, f => f.Check == AdminCheck.Forwarding);
            Assert.False(run.IsComplete);

            // 再查一次也还是不干净：同一处痕迹不重复算怀疑度，但机器并没有变干净
            var again = await admin.PatrolAsync(cts.Token);
            Assert.False(again.FoundSomething);      // 没有「新」发现
            Assert.False(again.Clean);               // 可是东西还在
            Assert.False(run.IsComplete);

            // 玩家把用过的东西恢复原样
            await TypeUntil("jump01", "echo 0 > /proc/sys/net/ipv4/ip_forward",
                            async () => !await Forwarding());

            var clean = await admin.PatrolAsync(cts.Token);
            Assert.True(clean.Clean, Why("清干净了，管理员却还是看出了东西"));
            Assert.Equal(["tunnel", "scan", "fetch", "stealth"], Snapshot(done));
            Assert.True(run.IsComplete);
        }
        finally
        {
            foreach (var control in controls.Values) await control.DisposeAsync();
            foreach (var vm in vms) await vm.DisposeAsync();
            await cts.CancelAsync();
        }
    }

    /// <summary>控制台输出的末尾，够看清最后几条命令说了什么。</summary>
    private static string Tail(string text, int lines = 12) =>
        string.Join('\n', text.Replace("\r", "").Split('\n').TakeLast(lines));

    private static List<string> Snapshot(List<string> done)
    {
        lock (done) return [.. done];
    }

    private static LevelDefinition LoadJumpScan()
    {
        // 整个目录一起读：jump-scan 的解锁条件指向 lab-ping，单读一份过不了校验
        string dir = Path.Combine(TestImages.RepoRoot, "src", "GameHacker.Godot", "levels");
        var files = Directory.GetFiles(dir, "*.json").Select(f => (f, File.ReadAllText(f)));
        return LevelCatalog.Parse(files).Find("jump-scan")!;
    }
}
