using System.Text;
using GameHacker.Core.Channels;
using GameHacker.Core.Net;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 技术脊椎的端到端集成测试：两台真实虚拟机 -> 自研交换机 -> ping 通 -> 判定触发。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是 Core 不依赖 Godot 的全部理由。</b> 概要书要求三端稳定重现，
/// 那就必须能在 CI 上自动验证，而不是手工开三台机器点。因为这里没有一行
/// Godot 代码，Windows / macOS / Linux 的 runner 上一句 <c>dotnet test</c>
/// 就能跑完整条链路，不需要图形环境。
/// </para>
/// <para>
/// 它是 <c>m0/probe/run_switched.py</c> 的 C# 对应物，两边必须保持同样的结论。
/// </para>
/// </remarks>
public class SwitchIntegrationTests
{
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(90);

    [SkippableFact]
    public async Task 两台虚拟机经自研交换机互相ping通()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        var observer = new CountingObserver();
        await using var vSwitch = new VirtualSwitch(port: 0, observer);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _ = vSwitch.RunAsync(cts.Token);

        await using var alpha = new QemuLauncher(TestImages.QemuPath);
        await using var beta = new QemuLauncher(TestImages.QemuPath);

        alpha.Start(NewSpec("alpha", "10.0.0.1/24", "52:54:00:00:00:01", vSwitch.Port));
        beta.Start(NewSpec("beta", "10.0.0.2/24", "52:54:00:00:00:02", vSwitch.Port));

        var (alphaCtl, alphaRun) = Wire(alpha, cts.Token);
        var (betaCtl, betaRun) = Wire(beta, cts.Token);
        await using var alphaScope = alphaCtl;
        await using var betaScope = betaCtl;

        // ready 信标必须先等到：QEMU 一启动就连上 chardev 了（约 0.1 秒），
        // 但客户机要到约 1.5 秒才起 ttyS1 的读取循环，之前发的命令会被丢掉
        await alphaCtl.WaitReadyAsync(BootTimeout, cts.Token);
        await betaCtl.WaitReadyAsync(BootTimeout, cts.Token);

        Assert.True(await betaCtl.PingAsync("10.0.0.1", cts.Token), "beta 没能 ping 通 alpha");
        Assert.True(await alphaCtl.PingAsync("10.0.0.2", cts.Token), "alpha 没能 ping 通 beta");

        // 两台机器都该被学进 MAC 表，且落在不同端口上
        Assert.Equal(2, vSwitch.MacTable.Count);
        Assert.Equal(2, vSwitch.MacTable.Values.Distinct().Count());
        Assert.True(vSwitch.FramesForwarded > 0);

        // 判定的原料：交换机看到了真实的 ICMP 回显请求与应答
        Assert.True(observer.SawIcmpEchoRequest, "交换机没看到 ICMP echo request");
        Assert.True(observer.SawIcmpEchoReply, "交换机没看到 ICMP echo reply");

        await cts.CancelAsync();
        await Task.WhenAll(SafeAwait(alphaRun), SafeAwait(betaRun));
    }

    [SkippableFact]
    public async Task 跳板拓扑_两块网卡各进一个_VLAN_外网够不着内网()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        const int Outside = 10, Inside = 20;
        await using var vSwitch = new VirtualSwitch([Outside, Inside]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _ = vSwitch.RunAsync(cts.Token);
        int outside = vSwitch.PortFor(Outside), inside = vSwitch.PortFor(Inside);

        // ws 在外网；jump01 两脚各一；files01 在内网。
        // intruder 是对照组：地址和外网同网段，但插在内网 VLAN 上 ——
        // ws ping 不通它，才能说明挡住流量的是 VLAN 而不是路由
        var machines = new (string Name, VmNic[] Nics)[]
        {
            ("ws",       [new("52:54:00:00:01:00", outside, "10.0.0.1/24")]),
            ("jump01",   [new("52:54:00:00:02:00", outside, "10.0.0.2/24"),
                          new("52:54:00:00:02:01", inside,  "172.16.5.1/24")]),
            ("files01",  [new("52:54:00:00:03:00", inside,  "172.16.5.20/24")]),
            ("intruder", [new("52:54:00:00:04:00", inside,  "10.0.0.9/24")]),
        };

        var launchers = new List<QemuLauncher>();
        var controls = new Dictionary<string, ControlChannel>();
        var runs = new List<Task>();
        try
        {
            foreach (var (name, nics) in machines)
            {
                var vm = new QemuLauncher(TestImages.QemuPath);
                launchers.Add(vm);
                vm.Start(new VmSpec
                {
                    Name = name, KernelPath = TestImages.Kernel, InitrdPath = TestImages.Initrd, Nics = nics,
                });
                var (ctl, run) = Wire(vm, cts.Token);
                controls[name] = ctl;
                runs.Add(run);
            }
            await Task.WhenAll(controls.Values.Select(c => c.WaitReadyAsync(BootTimeout, cts.Token)));

            Assert.True(await controls["jump01"].PingAsync("10.0.0.1", cts.Token), "jump01 经 eth0 没能 ping 通外网的 ws");
            // 这一条同时验证了网卡顺序：第二块网卡确实成了 eth1、拿到了 m0.ip1 的地址
            Assert.True(await controls["jump01"].PingAsync("172.16.5.20", cts.Token), "jump01 经 eth1 没能 ping 通内网的 files01");
            Assert.False(await controls["ws"].PingAsync("172.16.5.20", cts.Token), "ws 不经跳板机就够到了内网");
            Assert.False(await controls["ws"].PingAsync("10.0.0.9", cts.Token), "同网段不同 VLAN 的机器居然能通");

            // 交换机分 VLAN 学 MAC：jump01 的两块网卡各在自己的 VLAN 里
            var table = vSwitch.MacTable.Keys.ToList();
            Assert.Contains(table, k => k.Vlan == Outside && k.Mac.Equals(Mac("52:54:00:00:02:00")));
            Assert.Contains(table, k => k.Vlan == Inside && k.Mac.Equals(Mac("52:54:00:00:02:01")));
            Assert.DoesNotContain(table, k => k.Vlan == Outside && k.Mac.Equals(Mac("52:54:00:00:04:00")));
        }
        finally
        {
            await cts.CancelAsync();
            foreach (var c in controls.Values) await c.DisposeAsync();
            foreach (var l in launchers) await l.DisposeAsync();
            await Task.WhenAll(runs.Select(SafeAwait));
        }
    }

    private static MacAddressKey Mac(string text) =>
        MacAddressKey.From(text.Split(':').Select(h => Convert.ToByte(h, 16)).ToArray());

    [SkippableFact]
    public async Task 隐藏控制通道能双向对话()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        await using var vSwitch = new VirtualSwitch(port: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _ = vSwitch.RunAsync(cts.Token);

        await using var vm = new QemuLauncher(TestImages.QemuPath);
        vm.Start(NewSpec("solo", "10.0.0.9/24", "52:54:00:00:00:09", vSwitch.Port));
        var (ctl, run) = Wire(vm, cts.Token);
        await using var ctlScope = ctl;

        var ready = await ctl.WaitReadyAsync(BootTimeout, cts.Token);
        Assert.Equal("solo", ready["host"]!.GetValue<string>());

        // resize 只能走这条通道：裸串口不是 PTY，没有 TIOCSWINSZ 带外信令
        var resized = await ctl.ResizeAsync(40, 120, cts.Token);
        Assert.Equal(40, resized["rows"]!.GetValue<int>());
        Assert.Equal(120, resized["cols"]!.GetValue<int>());

        await cts.CancelAsync();
        await SafeAwait(run);
    }

    private static VmSpec NewSpec(string name, string ip, string mac, int switchPort) => new()
    {
        Name = name,
        KernelPath = TestImages.Kernel,
        InitrdPath = TestImages.Initrd,
        Nics = [new VmNic(mac, switchPort, ip)],
    };

    private static (ControlChannel Ctl, Task Run) Wire(QemuLauncher vm, CancellationToken token)
    {
        var console = vm.Console!;
        var control = vm.Control!;
        var run = Task.WhenAll(console.RunAsync(token), control.RunAsync(token));
        return (new ControlChannel(control), run);
    }

    private static async Task SafeAwait(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 挂在交换机上的旁观者。任务判定、pcap 落盘、流量可视化都是这个接口的实现。
    /// </summary>
    private sealed class CountingObserver : IFrameObserver
    {
        public bool SawIcmpEchoRequest { get; private set; }
        public bool SawIcmpEchoReply { get; private set; }

        public void OnFrame(int sourcePortId, int vlan, ReadOnlySpan<byte> frame)
        {
            // 这里刻意手写最小解析而不引 PacketDotNet：测试要断言的是
            // 「交换机确实看见了真实 ICMP」，多引一层库反而让失败更难定位。
            // 产品代码里的检测逻辑用 PacketDotNet，理由见 m0/README.md。
            const int EthHeader = 14;
            if (frame.Length < EthHeader + 20 + 1) return;
            if (frame[12] != 0x08 || frame[13] != 0x00) return;              // 非 IPv4

            int ihl = (frame[EthHeader] & 0x0F) * 4;
            if (frame[EthHeader + 9] != 1) return;                            // 非 ICMP
            if (frame.Length < EthHeader + ihl + 1) return;

            byte icmpType = frame[EthHeader + ihl];
            if (icmpType == 8) SawIcmpEchoRequest = true;
            if (icmpType == 0) SawIcmpEchoReply = true;
        }
    }
}
