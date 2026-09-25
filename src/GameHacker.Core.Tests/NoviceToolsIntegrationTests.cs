using System.Text;
using GameHacker.Core.Channels;
using GameHacker.Core.Levels;
using GameHacker.Core.Net;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 新手模式工具箱：<c>mktunnel</c> 在两台真虚拟机上真的把隧道搭起来。
/// </summary>
/// <remarks>
/// <para>这个工具的全部价值在于「它不做玩家做不到的事」：它敲的就是玩家自己会敲的
/// 那几条命令 —— 从维护口登录、<c>sudo</c> 打开转发、本机加一条路由。所以这里验的
/// 不是「工具打印了成功」，而是<b>两台机器的真实状态真的变了</b>：跳板机的
/// <c>ip_forward</c> 是 1，玩家机器的路由表里真有那一条。</para>
/// <para>还要验它是可读的 —— 新手模式的出口是「看懂它然后不再需要它」。</para>
/// </remarks>
public class NoviceToolsIntegrationTests
{
    private static readonly TimeSpan Boot = TimeSpan.FromSeconds(120);
    private const string Password = "Rk4-tin-roof";

    [SkippableFact]
    public async Task mktunnel_真的登录_开转发_加路由()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var packets = new PacketLog();
        await using var vSwitch = new VirtualSwitch([10], packets);
        _ = vSwitch.RunAsync(cts.Token);

        var vms = new Dictionary<string, QemuLauncher>();
        var consoles = new Dictionary<string, ConsoleText>();
        var controls = new Dictionary<string, ControlChannel>();

        // ws：玩家的机器，装着新手工具箱。jump01：跳板机，开着维护口
        var specs = new[]
        {
            new VmSpec
            {
                Name = "ws", KernelPath = TestImages.Kernel, InitrdPath = TestImages.Initrd,
                Nics = [new VmNic("52:54:00:00:01:00", vSwitch.PortFor(10), "10.0.0.1/24")],
                Tools = true,
            },
            new VmSpec
            {
                Name = "jump01", KernelPath = TestImages.Kernel, InitrdPath = TestImages.Initrd,
                Nics = [new VmNic("52:54:00:00:02:00", vSwitch.PortFor(10), "10.0.0.2/24")],
                Access = new GuestAccess(2222, "svc-backup", Password, Sudo: true),
            },
        };

        string Console(string name) => consoles[name].ToString();

        try
        {
            foreach (var spec in specs)
            {
                var vm = new QemuLauncher(TestImages.QemuPath);
                vms[spec.Name] = vm;
                vm.Start(spec);
                var text = new ConsoleText();
                consoles[spec.Name] = text;
                vm.Console!.DataReceived += d => text.Append(d.Span);
                _ = vm.Console!.RunAsync(cts.Token);
                _ = vm.Control!.RunAsync(cts.Token);
                controls[spec.Name] = new ControlChannel(vm.Control!);
            }
            foreach (var (_, control) in controls)
            {
                await control.WaitReadyAsync(Boot, cts.Token);
                await control.WaitEventAsync("console", Boot, cts.Token);
            }

            async Task Type(string machine, string command, int settleMs = 1500)
            {
                await vms[machine].Console!.SendAsync(Encoding.UTF8.GetBytes(command + "\n"), cts.Token);
                await Task.Delay(settleMs, cts.Token);
            }

            // getty 接上 tty 之前送进去的字会被丢掉，先用一条废命令把这一下用掉
            for (int i = 0; i < 4 && !Console("ws").Contains("warm-42"); i++) await Type("ws", "echo warm-$((6*7))");

            // 工具箱装上了，而且是能读的脚本 —— 新手模式的出口就是读懂它
            await Type("ws", "tools");
            Assert.Contains("mktunnel", Console("ws"));
            await Type("ws", "head -5 /usr/local/bin/mktunnel");
            Assert.Contains("#!/bin/sh", Console("ws"));

            // 先确认这会儿确实还不通：转发没开，路由也没有
            Assert.False(await Forwarding(), "还没动手，跳板机就已经在转发了");

            await Type("ws", $"mktunnel 10.0.0.2 2222 svc-backup {Password} 172.16.5.0/24", 6000);

            // 真实状态：跳板机那边转发开了
            Assert.True(await Forwarding(), "mktunnel 跑完了，跳板机却没在转发:\n" + Tail(Console("ws"), 25));

            // 真实状态：玩家机器上真有那条路由
            await Type("ws", "ip route | grep 172.16.5");
            Assert.Contains("172.16.5.0/24 via 10.0.0.2", Console("ws"));

            // 它自己也说清楚了每一步做了什么，而且提醒了走之前要关回去
            string said = Console("ws");
            Assert.Contains("能 sudo", said);
            Assert.Contains("关回去", said);

            async Task<bool> Forwarding() =>
                (await StateProbe.AskAsync(new StateQuery("jump01") { Forwarding = true },
                                           controls["jump01"], cts.Token)).Forwarding is true;
        }
        finally
        {
            foreach (var control in controls.Values) await control.DisposeAsync();
            foreach (var vm in vms.Values) await vm.DisposeAsync();
            await cts.CancelAsync();
        }
    }

    [SkippableFact]
    public async Task 口令不对时_mktunnel_说清楚是没进去_而不是闷头失败()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var packets = new PacketLog();
        await using var vSwitch = new VirtualSwitch([10], packets);
        _ = vSwitch.RunAsync(cts.Token);

        await using var ws = new QemuLauncher(TestImages.QemuPath);
        await using var jump = new QemuLauncher(TestImages.QemuPath);
        var text = new ConsoleText();

        ws.Start(new VmSpec
        {
            Name = "ws", KernelPath = TestImages.Kernel, InitrdPath = TestImages.Initrd,
            Nics = [new VmNic("52:54:00:00:01:00", vSwitch.PortFor(10), "10.0.0.1/24")],
            Tools = true,
        });
        jump.Start(new VmSpec
        {
            Name = "jump01", KernelPath = TestImages.Kernel, InitrdPath = TestImages.Initrd,
            Nics = [new VmNic("52:54:00:00:02:00", vSwitch.PortFor(10), "10.0.0.2/24")],
            Access = new GuestAccess(2222, "svc-backup", Password, Sudo: true),
        });
        ws.Console!.DataReceived += d => text.Append(d.Span);
        _ = ws.Console!.RunAsync(cts.Token);
        _ = ws.Control!.RunAsync(cts.Token);
        _ = jump.Console!.RunAsync(cts.Token);
        _ = jump.Control!.RunAsync(cts.Token);
        await using var wsControl = new ControlChannel(ws.Control!);
        await using var jumpControl = new ControlChannel(jump.Control!);
        await wsControl.WaitReadyAsync(Boot, cts.Token);
        await wsControl.WaitEventAsync("console", Boot, cts.Token);
        await jumpControl.WaitReadyAsync(Boot, cts.Token);

        string Dump() => text.ToString();

        async Task Type(string command, int settleMs = 1500)
        {
            await ws.Console!.SendAsync(Encoding.UTF8.GetBytes(command + "\n"), cts.Token);
            await Task.Delay(settleMs, cts.Token);
        }

        for (int i = 0; i < 4 && !Dump().Contains("warm-42"); i++) await Type("echo warm-$((6*7))");

        await Type("mktunnel 10.0.0.2 2222 svc-backup wrong-password 172.16.5.0/24", 6000);
        Assert.Contains("账号或口令不对", Dump());

        // 失败了就不该留下半截：路由不能加
        await Type("ip route | grep -c 172.16.5 || echo NO-ROUTE");
        Assert.Contains("NO-ROUTE", Dump());
    }

    /// <summary>
    /// 从磁盘 switch_root 起来的玩家机（游戏里 0 号机就是这样）也要装上工具箱。
    /// </summary>
    /// <remarks>
    /// 钉的是一个真踩到的坑：工具是在 <c>m0_install_tools</c> 里装的，而这台机器
    /// 会 <c>switch_root</c> 进 qcow2、由 <c>stage2</c> 调这个函数。qcow2 里那份
    /// common.sh 是烤镜像时装的，一旦它比 initramfs 旧、缺了这个新函数，stage2
    /// 调用就会静默 "not found"，工具装不上、还查不出为什么 —— 前面两个用例跑的
    /// 是纯内存机（走 <c>init</c>），恰好绕开了这条路，没能拦住。
    /// 现在 init 在 switch_root 前把 initramfs 里那份 common.sh 覆盖进 newroot，
    /// 单一真源，这个用例守着别再回退。
    /// </remarks>
    [SkippableFact]
    public async Task 从磁盘起的玩家机也装上工具箱()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);
        Skip.IfNot(File.Exists(TestImages.AlpineDisk),
                   "缺少 alpine-main.qcow2，先跑 m0/scripts/02-build-alpine-rootfs.sh");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var packets = new PacketLog();
        await using var vSwitch = new VirtualSwitch([10], packets);
        _ = vSwitch.RunAsync(cts.Token);

        await using var ws = new QemuLauncher(TestImages.QemuPath);
        var text = new ConsoleText();
        ws.Start(new VmSpec
        {
            Name = "ws", KernelPath = TestImages.Kernel, InitrdPath = TestImages.Initrd,
            // 有磁盘 -> 走 switch_root 到 qcow2，工具由 stage2 装（正是出过问题那条路）
            DiskPath = TestImages.AlpineDisk, Ephemeral = true, MemoryMegabytes = 512,
            Persona = HardwarePersona.Workstation,
            Nics = [new VmNic("52:54:00:00:01:00", vSwitch.PortFor(10), "10.0.0.1/24")],
            Tools = true,
        });
        ws.Console!.DataReceived += d => text.Append(d.Span);
        _ = ws.Console!.RunAsync(cts.Token);
        _ = ws.Control!.RunAsync(cts.Token);
        await using var control = new ControlChannel(ws.Control!);
        await control.WaitReadyAsync(Boot, cts.Token);
        await control.WaitEventAsync("console", Boot, cts.Token);

        string Dump() => text.ToString();
        async Task Type(string command, int settleMs = 1500)
        {
            await ws.Console!.SendAsync(Encoding.UTF8.GetBytes(command + "\n"), cts.Token);
            await Task.Delay(settleMs, cts.Token);
        }

        for (int i = 0; i < 4 && !Dump().Contains("warm-42"); i++) await Type("echo warm-$((6*7))");

        // tools 得在 PATH 上真能跑，而不是只躺在 /usr/local/bin 里
        await Type("command -v tools && tools");
        Assert.Contains("mktunnel", Tail(Dump(), 20));

        // 而且是能读的脚本 —— 新手模式的出口就是读懂它
        await Type("head -1 /usr/local/bin/mktunnel");
        Assert.Contains("#!/bin/sh", Dump());
    }

    private static string Tail(string s, int lines) =>
        string.Join('\n', s.Replace("\r", "").Split('\n').TakeLast(lines));
}
