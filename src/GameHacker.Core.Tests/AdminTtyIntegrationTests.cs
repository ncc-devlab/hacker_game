using GameHacker.Core.Admin;
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
    public async Task 管理员查岗_发现玩家留在机器上的进程()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        await using var vSwitch = new VirtualSwitch(port: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _ = vSwitch.RunAsync(cts.Token);

        var account = new AdminAccount("opsadm", "Zx7-quiet-lane");
        await using var vm = new QemuLauncher(TestImages.QemuPath);
        vm.Start(new VmSpec
        {
            Name = "jump01",
            KernelPath = TestImages.Kernel,
            InitrdPath = TestImages.Initrd,
            Nics = [new VmNic("52:54:00:00:01:00", vSwitch.Port, "10.0.0.2/24")],
            Admin = account,
        });
        var run = Task.WhenAll(vm.Console!.RunAsync(cts.Token), vm.Control!.RunAsync(cts.Token),
                               vm.Admin!.RunAsync(cts.Token));
        await using var control = new ControlChannel(vm.Control!);
        await control.WaitReadyAsync(BootTimeout, cts.Token);

        var definition = new AdminDefinition
        {
            Machine = "jump01",
            User = account.User,
            // ttyS0 是客户机的物理控制台，开机起就有个 shell 在上面，属于常态
            Allow = new AdminAllow { Processes = ["syslogd*"], Sessions = ["ttyS2", "ttyS0"], Services = ["syslogd"] },
            // 每样都必做、间隔压到最短：测试不等他磨蹭
            Schedule = new AdminSchedule { MinActions = 2, MaxActions = 4, PauseMin = 0.2, PauseMax = 0.4 },
            Routine =
            [
                new AdminAction { Id = "processes", Check = AdminCheck.Processes, Chance = 1, Weight = 20 },
                new AdminAction { Id = "sessions", Check = AdminCheck.Sessions, Chance = 1, Weight = 20 },
                new AdminAction { Id = "log", Check = AdminCheck.Log, Chance = 1, Weight = 25 },
                new AdminAction { Id = "services", Check = AdminCheck.Services, Chance = 1, Weight = 30 },
            ],
        };
        using var tty = new TtySession(vm.Admin!);
        var agent = new AdminAgent(definition, account, tty, seed: 1);

        // 先查一次：玩家还没登录也没留东西，这台机器是干净的
        var before = await agent.PatrolAsync(cts.Token);
        Assert.False(before.FoundSomething);
        Assert.Equal(0, before.Suspicion);
        Assert.Equal(AdminActivity.Away, agent.Activity);   // 查完就下线
        Assert.Contains(AdminCheck.Log, before.Did);        // 日志那一项也真的查了

        // 玩家坐到这台机器的终端前，还留了个后台进程
        await vm.Console!.SendAsync("sleep 600 &\n"u8.ToArray(), cts.Token);
        await Task.Delay(2000, cts.Token);

        var after = await agent.PatrolAsync(cts.Token);
        Assert.True(after.FoundSomething);
        var finding = Assert.Single(after.Findings);
        Assert.Contains("sleep 600", finding.Subject);
        Assert.Contains("sleep 600", finding.Explanation);
        Assert.Equal(20, after.Suspicion);

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }

    /// <summary>起一台带管理员终端的 jump01，等它开机。调用方负责取消 <paramref name="cts"/> 并等 run 收尾。</summary>
    private static async Task<(QemuLauncher Vm, VirtualSwitch Switch, Task Run)> BootJumpAsync(
        AdminAccount account, CancellationTokenSource cts)
    {
        var vSwitch = new VirtualSwitch(port: 0);
        _ = vSwitch.RunAsync(cts.Token);
        var vm = new QemuLauncher(TestImages.QemuPath);
        vm.Start(new VmSpec
        {
            Name = "jump01",
            KernelPath = TestImages.Kernel,
            InitrdPath = TestImages.Initrd,
            Nics = [new VmNic("52:54:00:00:01:00", vSwitch.Port, "10.0.0.2/24")],
            Admin = account,
        });
        var run = Task.WhenAll(vm.Console!.RunAsync(cts.Token), vm.Control!.RunAsync(cts.Token),
                               vm.Admin!.RunAsync(cts.Token));
        await using var control = new ControlChannel(vm.Control!);
        await control.WaitReadyAsync(BootTimeout, cts.Token);
        // ready 只说明控制通道起来了。console 事件之后 shell 还要一会儿才开始读，
        // 而 ash 进入行编辑时会丢掉之前敲进来的字 —— 所以要等到提示符真的出来
        await control.WaitEventAsync("console", BootTimeout, cts.Token);
        using var console = new TtySession(vm.Console!);
        using var prompt = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        prompt.CancelAfter(TtyTimeout);
        await vm.Console!.SendAsync("\n"u8.ToArray(), cts.Token);
        await console.ExpectAsync(new System.Text.RegularExpressions.Regex(@"# ?$"), "玩家终端的提示符", prompt.Token);
        return (vm, vSwitch, run);
    }

    /// <summary>玩家在这台机器的物理控制台上敲一行。</summary>
    private static async Task TypeAsync(QemuLauncher vm, string line, CancellationToken cancellationToken)
    {
        await vm.Console!.SendAsync(System.Text.Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);
        await Task.Delay(1500, cancellationToken);
    }

    private static readonly AdminAllow JumpAllow = new()
    {
        Processes = ["syslogd*"],
        Sessions = ["ttyS2", "ttyS0"],
        Services = ["syslogd"],
    };

    [SkippableFact]
    public async Task 实习运维的运维脚本真的放进了家目录_他只看得见脚本打印的东西()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var account = new AdminAccount("opsadm", "Zx7-quiet-lane");
        var (vm, vSwitch, run) = await BootJumpAsync(account, cts);
        await using var _vm = vm;
        await using var _switch = vSwitch;

        var definition = new AdminDefinition
        {
            Machine = "jump01",
            User = account.User,
            Allow = JumpAllow,
            Schedule = new AdminSchedule { PauseMin = 0.1, PauseMax = 0.2 },
            // 每天那个脚本必跑，全面的那个这次别跑：测的就是「只看 daily 打印的」
            Tiers = new Dictionary<AdminSkill, AdminProfile>
            {
                [AdminSkill.Junior] = new()
                {
                    Routine = [new AdminAction { Id = "daily", Check = AdminCheck.Script, Script = "daily-check.sh", Chance = 1, Weight = 15 }],
                    MaxActions = 1,
                },
            },
        };
        using var tty = new TtySession(vm.Admin!);
        var agent = new AdminAgent(definition, account, tty, seed: 1, skill: AdminSkill.Junior);
        await agent.ProvisionAsync(cts.Token);

        // 脚本真的在客户机上，玩家翻得到
        using (var peek = new TtySession(vm.Admin!))
        {
            await peek.LoginAsync(account.User, account.Password, TtyTimeout, cts.Token);
            string body = await peek.RunAsync("cat ~/bin/daily-check.sh", TtyTimeout, cts.Token);
            Assert.Contains("== services ==", body);
            Assert.Contains("ps -o pid,user,tty,args", body);
            // 写脚本的那一串 echo 是开局布景，不该出现在他的命令历史里
            Assert.DoesNotContain(">> daily-check.sh", await peek.RunAsync("cat ~/.ash_history", TtyTimeout, cts.Token));
            await peek.LogoutAsync(TtyTimeout, cts.Token);
        }

        // 干净的时候什么也没有
        var clean = await agent.PatrolAsync(cts.Token);
        Assert.False(clean.FoundSomething, string.Join("\n", clean.Findings.Select(f => f.Explanation)));

        // 玩家留了个后台进程：daily 只看服务和会话，实习运维看不见它
        await TypeAsync(vm, "sleep 600 &", cts.Token);
        var missed = await agent.PatrolAsync(cts.Token);
        Assert.False(missed.FoundSomething);

        // 把他的脚本删了：他每天跑的东西突然不灵，这个他看得出来
        await TypeAsync(vm, "rm /home/opsadm/bin/daily-check.sh", cts.Token);
        var broken = await agent.PatrolAsync(cts.Token);
        var finding = Assert.Single(broken.Findings);
        Assert.Equal("script:daily-check.sh", finding.Subject);

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }

    [SkippableFact]
    public async Task 查端口_玩家开的监听藏不住()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var account = new AdminAccount("opsadm", "Zx7-quiet-lane");
        var (vm, vSwitch, run) = await BootJumpAsync(account, cts);
        await using var _vm = vm;
        await using var _switch = vSwitch;

        var definition = new AdminDefinition
        {
            Machine = "jump01",
            User = account.User,
            Allow = JumpAllow,
            Schedule = new AdminSchedule { MinActions = 2, MaxActions = 2, PauseMin = 0.1, PauseMax = 0.2 },
            Routine =
            [
                new AdminAction { Id = "ports", Check = AdminCheck.Ports, Chance = 1, Weight = 25 },
                new AdminAction { Id = "files", Check = AdminCheck.Files, Chance = 1, Weight = 20 },
            ],
        };
        using var tty = new TtySession(vm.Admin!);
        var agent = new AdminAgent(definition, account, tty, seed: 1);

        var clean = await agent.PatrolAsync(cts.Token);
        Assert.False(clean.FoundSomething, string.Join("\n", clean.Findings.Select(f => f.Explanation)));

        // 这套 busybox 的 nc 不能监听，能开端口的只有 ntpd 的服务端模式（udp/123）。
        // 拷一份 busybox 到 /tmp 再起：文件和端口两样痕迹都留下了
        await TypeAsync(vm, "cp /bin/busybox /tmp/busybox; /tmp/busybox ntpd -n -l &", cts.Token);
        var dirty = await agent.PatrolAsync(cts.Token);
        var subjects = dirty.Findings.Select(f => f.Subject).ToList();
        Assert.Contains("port:udp/123", subjects);
        Assert.Contains("file:/tmp/busybox", subjects);

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }

    [SkippableFact]
    public async Task 资深运维改掉玩家账号的口令_新口令真的生效()
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var account = new AdminAccount("opsadm", "Zx7-quiet-lane");
        var (vm, vSwitch, run) = await BootJumpAsync(account, cts);
        await using var _vm = vm;
        await using var _switch = vSwitch;

        // 玩家手上的账号
        await TypeAsync(vm, "adduser -D ops; echo ops:stolen-pass | chpasswd", cts.Token);

        var definition = new AdminDefinition
        {
            Machine = "jump01",
            User = account.User,
            Allow = JumpAllow,
            Schedule = new AdminSchedule { PauseMin = 0.1, PauseMax = 0.2 },
            PasswordTargets = ["ops", "nobody-here"],
            Tiers = new Dictionary<AdminSkill, AdminProfile>
            {
                [AdminSkill.Senior] = new()
                {
                    Routine = [new AdminAction { Id = "services", Check = AdminCheck.Services, Chance = 1 }],
                    MinActions = 1,
                    PasswordChance = 1,
                },
            },
        };
        using var tty = new TtySession(vm.Admin!);
        var agent = new AdminAgent(definition, account, tty, seed: 1, skill: AdminSkill.Senior);

        var report = await agent.PatrolAsync(cts.Token);
        Assert.Equal(["ops"], report.PasswordsChanged);      // 不存在的账号改不成，不假装改了
        string fresh = agent.ChangedPasswords["ops"];

        // 旧口令进不去了，新口令进得去
        using var door = new TtySession(vm.Admin!);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => door.LoginAsync("ops", "stolen-pass", TtyTimeout, cts.Token));
        await door.LoginAsync("ops", fresh, TtyTimeout, cts.Token);
        Assert.Contains("ops", await door.RunAsync("id", TtyTimeout, cts.Token));
        await door.LogoutAsync(TtyTimeout, cts.Token);

        // 痕迹：他的命令历史里有 sudo passwd ops，但新口令不在里面（是在提示后面敲的）；
        // sudo 在系统日志里记了一笔。这些玩家都翻得到
        using var trace = new TtySession(vm.Admin!);
        await trace.LoginAsync(account.User, account.Password, TtyTimeout, cts.Token);
        string history = await trace.RunAsync("cat ~/.ash_history", TtyTimeout, cts.Token);
        Assert.Contains("sudo passwd ops", history);
        Assert.DoesNotContain(fresh, history);
        Assert.Contains("passwd ops", await trace.RunAsync("grep sudo /var/log/messages", TtyTimeout, cts.Token));
        await trace.LogoutAsync(TtyTimeout, cts.Token);

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
