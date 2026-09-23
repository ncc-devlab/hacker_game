using GameHacker.Core.Channels;
using GameHacker.Core.Net;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 管理员假人的手脚：在真虚拟机的 ttyS2 上真的登录一次。
/// </summary>
/// <remarks>
/// 这条路径的价值不在「能跑命令」—— 隐藏控制通道也能跑命令，而且更省事。
/// 它的价值在于<b>留下真实痕迹</b>：真的 getty、真的 login、真的账号，
/// 于是玩家在客户机里看得见管理员来过。所以这里不只断言命令有输出，
/// 还要断言客户机自己认得这个会话。
/// </remarks>
public class AdminTtyIntegrationTests
{
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TtyTimeout = TimeSpan.FromSeconds(30);

    [SkippableFact]
    public async Task 管理员从_ttyS2_登录并留下真实会话()
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
            Admin = new AdminAccount("opsadm", "Zx7-quiet-lane"),
        });

        Assert.NotNull(vm.Admin);
        var run = Task.WhenAll(vm.Console!.RunAsync(cts.Token), vm.Control!.RunAsync(cts.Token),
                               vm.Admin!.RunAsync(cts.Token));
        await using var control = new ControlChannel(vm.Control!);
        await control.WaitReadyAsync(BootTimeout, cts.Token);

        using var admin = new TtySession(vm.Admin!);
        await admin.LoginAsync("opsadm", "Zx7-quiet-lane", TtyTimeout, cts.Token);

        // 登录的是真账号，不是 root
        string id = await admin.RunAsync("id", TtyTimeout, cts.Token);
        Assert.Contains("opsadm", id);
        Assert.DoesNotContain("uid=0(", id);

        // 他确实坐在 ttyS2 上
        Assert.Contains("ttyS2", await admin.RunAsync("tty", TtyTimeout, cts.Token));

        // 关键断言：客户机自己看得见这个会话。玩家敲 ps 就会发现管理员在线
        string ps = await admin.RunAsync("ps -o user,tty,args", TtyTimeout, cts.Token);
        Assert.Contains("opsadm", ps);

        await admin.LogoutAsync(TtyTimeout, cts.Token);
        Assert.Null(admin.User);

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }

    [SkippableFact]
    public async Task 没有管理员的机器根本不开这个口()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        await using var vSwitch = new VirtualSwitch(port: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _ = vSwitch.RunAsync(cts.Token);

        await using var vm = new QemuLauncher(TestImages.QemuPath);
        vm.Start(new VmSpec
        {
            Name = "ws",
            KernelPath = TestImages.Kernel,
            InitrdPath = TestImages.Initrd,
            Nics = [new VmNic("52:54:00:00:01:00", vSwitch.Port, "10.0.0.1/24")],
        });

        Assert.Null(vm.Admin);
        var run = Task.WhenAll(vm.Console!.RunAsync(cts.Token), vm.Control!.RunAsync(cts.Token));
        await using var control = new ControlChannel(vm.Control!);
        await control.WaitReadyAsync(BootTimeout, cts.Token);

        // 命令行里没有第三个串口，客户机里也就没有 /dev/ttyS2 ——
        // 玩家看不出这台机器「本来可以被谁登录」
        Assert.DoesNotContain("chardev:adm", vm.CommandLine);
        Assert.DoesNotContain("m0.admin=", vm.CommandLine);

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }
}
