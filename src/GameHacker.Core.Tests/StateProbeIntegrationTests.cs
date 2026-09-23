using System.Security.Cryptography;
using System.Text;
using GameHacker.Core.Channels;
using GameHacker.Core.Levels;
using GameHacker.Core.Net;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 私有 API 在真客户机上的那一半：只读探查三样，答案必须是机器的真实状态。
/// </summary>
/// <remarks>
/// 判定逻辑本身在 <see cref="LevelCheckTests"/> 里用假数据测；这里测的是
/// 「问得到、问得准」—— 文件真放上去、转发真打开、进程真起来，再问一次看答案变没变。
/// </remarks>
public class StateProbeIntegrationTests
{
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(90);

    [SkippableFact]
    public async Task 只凭内容哈希就能认出玩家拿到的文件()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        await using var vSwitch = new VirtualSwitch(port: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _ = vSwitch.RunAsync(cts.Token);

        await using var vm = new QemuLauncher(TestImages.QemuPath);
        vm.Start(new VmSpec
        {
            Name = "ws",
            KernelPath = TestImages.Kernel,
            InitrdPath = TestImages.Initrd,
            Nics = [new VmNic("52:54:00:00:01:00", vSwitch.Port, "10.0.0.1/24")],
        });
        var run = Task.WhenAll(vm.Console!.RunAsync(cts.Token), vm.Control!.RunAsync(cts.Token));
        await using var control = new ControlChannel(vm.Control!);
        await control.WaitReadyAsync(BootTimeout, cts.Token);
        await control.WaitEventAsync("console", BootTimeout, cts.Token);

        const string secret = "ACCT-2291-CLOSED";
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret + "\n"))).ToLowerInvariant();
        var query = new StateQuery("ws") { FileSha = sha };

        // 还没拿到东西
        Assert.False((await StateProbe.AskAsync(query, control, cts.Token)).FileFound);

        // 玩家把文件弄到手了。名字和目录都与判定无关，只认内容 ——
        // 所以这里故意起个和目标文件毫不相干的名字
        await vm.Console!.SendAsync(Encoding.UTF8.GetBytes($"echo {secret} > /tmp/notes.txt\n"), cts.Token);
        await Task.Delay(1500, cts.Token);

        Assert.True((await StateProbe.AskAsync(query, control, cts.Token)).FileFound);

        // 内容不一样就不算：改一个字也认不出来
        await vm.Console.SendAsync("echo ACCT-2291-OPEN > /tmp/notes.txt\n"u8.ToArray(), cts.Token);
        await Task.Delay(1500, cts.Token);
        Assert.False((await StateProbe.AskAsync(query, control, cts.Token)).FileFound);

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }

    [SkippableFact]
    public async Task 进程表与转发开关问回来的是真状态()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        await using var vSwitch = new VirtualSwitch(port: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _ = vSwitch.RunAsync(cts.Token);

        await using var vm = new QemuLauncher(TestImages.QemuPath);
        vm.Start(new VmSpec
        {
            Name = "jump01",
            KernelPath = TestImages.Kernel,
            InitrdPath = TestImages.Initrd,
            Nics = [new VmNic("52:54:00:00:01:00", vSwitch.Port, "10.0.0.2/24")],
        });
        var run = Task.WhenAll(vm.Console!.RunAsync(cts.Token), vm.Control!.RunAsync(cts.Token));
        await using var control = new ControlChannel(vm.Control!);
        await control.WaitReadyAsync(BootTimeout, cts.Token);
        await control.WaitEventAsync("console", BootTimeout, cts.Token);

        var query = new StateQuery("jump01") { Processes = true, Forwarding = true };

        var before = await StateProbe.AskAsync(query, control, cts.Token);
        Assert.NotEmpty(before.Processes);
        Assert.Contains(before.Processes, p => p.Command.Contains("syslogd"));
        Assert.False(before.Forwarding);                       // 一台普通机器不转发

        // 玩家把跳板机变成路由器，还留了个中继进程
        await vm.Console!.SendAsync(
            "echo 1 > /proc/sys/net/ipv4/ip_forward; sleep 600 &\n"u8.ToArray(), cts.Token);
        await Task.Delay(1500, cts.Token);

        var after = await StateProbe.AskAsync(query, control, cts.Token);
        Assert.True(after.Forwarding);
        Assert.Contains(after.Processes, p => p.Command.Contains("sleep 600"));

        // 「掩盖」这一步看的就是这两样
        var clean = new CleanCheck { Machine = "jump01" }.CreateTracker(
            new LevelWorld(new LevelDefinition { Id = "x", Title = "x", Track = LevelTrack.Mission }));
        Assert.False(clean.Observe(after));
        Assert.Contains("sleep 600", clean.Remaining);

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }
}
