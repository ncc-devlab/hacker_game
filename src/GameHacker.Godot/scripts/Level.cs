using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameHacker.Core.Admin;
using GameHacker.Core.Channels;
using GameHacker.Core.Levels;
using GameHacker.Core.Net;
using GameHacker.Core.Qemu;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 关卡场景：按 <see cref="GameState.Current"/> 的定义拉起机器、组网、推进任务步骤。
/// </summary>
/// <remarks>
/// <para>前身是 M2 的验证壳，把写死的两台机器换成了读关卡定义。
/// 技术脊椎没变：真实虚拟机用 TCG 启动、各自一个终端、经自研交换机组网，
/// 交换机上的每一帧同时喂给抓包面板和步骤判定（<see cref="LevelRun"/>）。</para>
/// <para>三端验证的自检和截图也跑在这里，默认进的是联网实验场。</para>
/// </remarks>
public partial class Level : Control
{
    private Label _status = null!;
    private Label _levelTitle = null!;
    private Button _back = null!;
    private HBoxContainer _machinesBox = null!;
    private VBoxContainer _stepsBox = null!;
    private Label _hint = null!;
    private PacketPanel _packets = null!;

    private readonly PacketLog _packetLog = new();
    private VirtualSwitch? _switch;
    private readonly List<VmSession> _sessions = [];
    private readonly List<Control> _terminals = [];
    private LevelDefinition _level = null!;
    private LevelRun _run = null!;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _closing;

    // --- 管理员 ---
    private PanelContainer _adminPanel = null!;
    private Label _adminStatus = null!;
    private ProgressBar _threat = null!;
    private VBoxContainer _findings = null!;
    private AdminAgent? _admin;
    private TtySession? _adminTty;
    private AdminAccount? _adminAccount;
    private readonly System.Threading.CancellationTokenSource _adminCts = new();

    // --- 客户机状态探查 ---
    private int _probing;        // 同一时刻只许有一次探查在飞
    private int _probeAgain;     // 飞行期间又来了触发

    public override void _Ready()
    {
        _status = GetNode<Label>("%Status");
        _levelTitle = GetNode<Label>("%LevelTitle");
        _back = GetNode<Button>("%Back");
        _machinesBox = GetNode<HBoxContainer>("%Machines");
        _stepsBox = GetNode<VBoxContainer>("%Steps");
        _hint = GetNode<Label>("%Hint");
        _packets = GetNode<PacketPanel>("%Packets");
        _adminPanel = GetNode<PanelContainer>("%Admin");
        _adminStatus = GetNode<Label>("%Status2");
        _threat = GetNode<ProgressBar>("%Threat");
        _findings = GetNode<VBoxContainer>("%Findings");
        _packets.Attach(_packetLog);
        _back.Pressed += () => GameState.Instance.BackToSelect();

        // 直接在编辑器里打开本场景按 F6 时没有选中的关卡，退回选关界面
        if (GameState.Instance.Current is not { } level)
        {
            Callable.From(() => GameState.Instance.BackToSelect()).CallDeferred();
            return;
        }
        _level = level;
        _levelTitle.Text = level.Title;

        // 口令每局现生成，关卡文件里不留 —— 玩家翻关卡文件也拿不到管理员的账号
        if (level.Admin is { } adminDefinition)
        {
            _adminAccount = new AdminAccount(adminDefinition.User, NewPassword());
            // 高手模式（visibility: hidden）下界面上什么都不说，
            // 玩家只能自己从机器上看出他来过
            _adminPanel.Visible = adminDefinition.Visibility == AdminVisibility.Shown;
            _threat.MaxValue = adminDefinition.Suspicion.ExposedAt;
        }

        _run = new LevelRun(level);
        _run.StepCompleted += step => Callable.From(() => OnStepCompleted(step)).CallDeferred();
        _run.Completed += () => _completed.TrySetResult();
        // 交换机转发线程上触发；LevelRun 自己加锁，UI 更新再倒回主线程
        _packetLog.PacketCaptured += _run.Observe;
        // 文件是经网络过来的：玩家敲完回车之后它还要传一会儿，
        // 所以网络上有动静也算一次「该去看看了」
        _packetLog.PacketCaptured += _ => NudgeProbe();

        BuildMachinePanes();
        ShowSteps();

        if (!GamePaths.ImagesReady)
        {
            SetStatus($"缺少客户机镜像。先跑 m0/scripts/00-fetch-images.sh 与 01-build-guest-initramfs.sh\n"
                      + $"找的位置: {GamePaths.ImagesDir}", Colors.Orange);
            return;
        }

        _ = BootAsync();
    }

    /// <summary>每台机器一栏：标题 + 终端。第一台是玩家的机器，放最左边。</summary>
    private void BuildMachinePanes()
    {
        foreach (var m in _level.Machines)
        {
            var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            string product = HardwarePersona.ByName(m.Persona)?.SystemProduct ?? "";
            string ips = string.Join(" / ", m.Nics.Select(n => n.Ip));
            pane.AddChild(new Label { Text = $"{m.Name} — {ips}   {product}" });

            // Terminal 是 godot-xterm 的 GDExtension 类，C# 里没有对应类型，只能按类名实例化
            var terminal = ClassDB.Instantiate("Terminal").As<Control>();
            terminal.Name = $"Terminal_{m.Name}";
            terminal.SizeFlagsVertical = SizeFlags.ExpandFill;
            pane.AddChild(terminal);

            _machinesBox.AddChild(pane);
            _terminals.Add(terminal);
        }
    }

    private async Task BootAsync()
    {
        try
        {
            // 先回收上次残留的 QEMU。TCG 每台占满一核，孤儿堆积会让
            // 本次启动越来越慢直至超时 —— 这个症状在 M2 里实测到过。
            int reaped = VmProcessRegistry.ReapOrphans(VmProcessRegistry.DefaultDirectory);
            if (reaped > 0) GD.Print($"[level] 回收了 {reaped} 个上次残留的虚拟机");

            SetStatus("启动虚拟交换机…");
            // 交换机是概要书里唯一的流量观察点，抓包面板和关卡判定都挂在这个旁观者上。
            // 每个网段一个 VLAN，不同网段之间二层不通
            _switch = new VirtualSwitch(LevelTopology.Vlans(_level), _packetLog);
            _ = _switch.RunAsync();

            SetStatus($"交换机开了 {_switch.Vlans.Count} 个 VLAN，正在拉起 {_level.Machines.Count} 台虚拟机…");

            // 人设决定客户机对玩家宣称的主板 / BIOS / CPU / 内核版本，已经在加载关卡时校验过
            for (int i = 0; i < _level.Machines.Count; i++)
            {
                if (_closing) return;
                _sessions.Add(new VmSession(NewSpec(i, _switch), _terminals[i], this));
            }

            // 排查间歇性启动问题时用 GAMEHACKER_BOOT_TIMEOUT 缩短等待
            double bootSeconds = double.TryParse(
                System.Environment.GetEnvironmentVariable("GAMEHACKER_BOOT_TIMEOUT"),
                out double t) ? t : 90;
            var boot = TimeSpan.FromSeconds(bootSeconds);
            await Task.WhenAll(_sessions.Select(s => s.WaitReadyAsync(boot)));
            if (_closing) return;

            // 玩家在任何一台机器上敲了回车，就去看一眼这一步要看的状态
            foreach (var session in _sessions) session.Bridge.PlayerSubmitted += NudgeProbe;

            SetStatus($"{_sessions.Count} 台机器就绪");
            _terminals[0].GrabFocus();
            StartAdmin();

            await MaybeSelfTestAsync();
            await MaybeScreenshotAsync();
        }
        catch (Exception ex)
        {
            if (_closing) return;   // 玩家在开机途中点了返回，后面的异常都是拆场景引起的
            // QEMU 起不来时只在 stderr 说一句话，不带上它的话
            // 这里只能看到「等 ready 信标超时」，完全无从下手
            string detail = string.Join("\n", _sessions.Select(s => $"{s.Spec.Name}: {s.DescribeFailure()}"));
            SetStatus($"启动失败: {ex.Message}\n{detail}", Colors.IndianRed);
            GD.PushError($"{ex}\n{detail}");
        }
    }

    private VmSpec NewSpec(int index, VirtualSwitch vSwitch)
    {
        var m = _level.Machines[index];
        return new VmSpec
        {
            Name = m.Name,
            Persona = HardwarePersona.ByName(m.Persona)!,
            KernelPath = GamePaths.Kernel,
            InitrdPath = GamePaths.Initrd,
            DiskPath = m.Disk is null ? null : GamePaths.Disk(m.Disk),
            // 网卡连哪个口就进哪个 VLAN；交换机用 bind 0 分配端口，多开或并发测试时不撞
            Nics = LevelTopology.Nics(_level, index)
                .Select(n => new VmNic(n.Mac, vSwitch.PortFor(n.Vlan), n.IpWithPrefix))
                .ToList(),
            Admin = _level.Admin?.Machine == m.Name ? _adminAccount : null,
            MemoryMegabytes = m.Memory,
            Gateway = m.Gateway,
            Files = m.Files.Select(f => new GuestFile(f.Path, f.Text)).ToList(),
            Services = m.Services.Select(x => new GuestService(x.Port, x.File)).ToList(),
            // 关卡里不写回镜像，保持基础镜像干净。
            // 真正的存档走 qcow2 backing file + user:// 下的 overlay。
            Ephemeral = m.Disk is not null,
        };
    }

    // --- 任务步骤 -----------------------------------------------------------

    private void ShowSteps()
    {
        foreach (Node child in _stepsBox.GetChildren()) child.QueueFree();

        int current = _run.CurrentIndex;
        for (int i = 0; i < _level.Steps.Count; i++)
        {
            var step = _level.Steps[i];
            string mark = i < current ? "完成" : i == current ? "进行中" : "";
            var row = new Label
            {
                Text = $"{i + 1}. {step.Title}" + (mark.Length > 0 ? $"   {mark}" : ""),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = i < current ? new Color(0.55f, 0.85f, 0.55f)
                         : i == current ? Colors.White
                         : new Color(0.5f, 0.5f, 0.5f),
            };
            _stepsBox.AddChild(row);
        }

        _hint.Text = _run.CurrentStep switch
        {
            null => "全部完成。",
            { Check: null } s => $"{s.Hint}\n\n（草稿关：这一步还没写判定，不会自动推进）",
            var s => s.Hint,
        };
    }

    /// <summary>
    /// 去问一眼客户机：当前这一步要看什么状态，就问什么。
    /// </summary>
    /// <remarks>
    /// <para><b>这不是轮询。</b> 只有真的发生了可能改变状态的事才会调到这里
    /// （玩家敲了回车、交换机上过了包、管理员查完一次岗），而且当前这一步
    /// 不看客户机内部状态时（<see cref="LevelRun.Wanted"/> 为 null）一次也不问。</para>
    /// <para>同一时刻只许有一次探查在飞：每次探查都要在客户机上真跑几条命令，
    /// 一串按键触发一串探查的话，玩家自己 <c>ps</c> 的时候就会看见判定器在他机器上踱步。
    /// 飞行期间来的触发记一笔，落地后补做一次，免得漏掉最后那一下。</para>
    /// </remarks>
    private void NudgeProbe()
    {
        if (System.Threading.Interlocked.Exchange(ref _probing, 1) == 1)
        {
            System.Threading.Volatile.Write(ref _probeAgain, 1);
            return;
        }
        _ = ProbeAsync();
    }

    private async Task ProbeAsync()
    {
        try
        {
            do
            {
                System.Threading.Volatile.Write(ref _probeAgain, 0);
                // 等玩家那条命令自己跑完，再看结果
                await Task.Delay(TimeSpan.FromSeconds(1.5), _adminCts.Token);
                if (_closing) continue;

                // 一步可以拼好几个条件，各自想看的东西可能在不同机器上；
                // 每问完一样就交上去，说不定这一样就够了
                foreach (var query in _run.Wanted)
                {
                    var session = _sessions.FirstOrDefault(s => s.Spec.Name == query.Machine);
                    if (session is null) continue;
                    _run.Observe(await StateProbe.AskAsync(query, session.Control, _adminCts.Token));
                    if (_closing) break;
                }
            }
            while (System.Threading.Volatile.Read(ref _probeAgain) == 1 && !_closing);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException
                                      or ObjectDisposedException or System.IO.IOException)
        {
            // 这次没问着（机器忙、关卡正在拆），不该把关卡带崩：下次触发再问
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _probing, 0);
        }
    }

    private void OnStepCompleted(LevelStep step)
    {
        if (_closing) return;
        GD.Print($"[level] 步骤完成: {step.Id}");
        ShowSteps();
        // 玩家推进到下一步，管理员可能换一套作息和脾气
        if (_run.CurrentStep is { } next) _admin?.EnterStage(next.Id);
        // 新的一步可能一上来就想看客户机状态（比如痕迹其实早就清干净了）
        NudgeProbe();
        if (!_run.IsComplete)
        {
            SetStatus($"完成：{step.Title}", Colors.LightGreen);
            return;
        }

        bool first = GameState.Instance.Complete(_level);
        SetStatus($"任务完成：{_level.Title}，交换机共转发 {_switch?.FramesForwarded ?? 0} 帧", Colors.LightGreen);
        if (GameState.IsAutomated) return;

        var dialog = new AcceptDialog
        {
            Title = "任务完成",
            DialogText = first ? $"「{_level.Title}」完成。\n新的任务可能已经解锁。" : $"「{_level.Title}」完成。",
            OkButtonText = "返回选关",
        };
        AddChild(dialog);
        dialog.Confirmed += () => GameState.Instance.BackToSelect();
        // 留在关里接着看也行，关掉对话框就是继续玩
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }

    // --- 管理员 -------------------------------------------------------------

    /// <summary>客户机起来之后，管理员开始按自己的节奏来查岗。</summary>
    private void StartAdmin()
    {
        if (_level.Admin is not { } definition || _adminAccount is null) return;

        var session = _sessions.FirstOrDefault(s => s.Spec.Name == definition.Machine);
        if (session?.AdminTty is null)
        {
            GD.PushError($"[level] {definition.Machine} 上没有管理员登录终端");
            return;
        }

        _adminTty = new TtySession(session.AdminTty);
        _admin = new AdminAgent(definition, _adminAccount, _adminTty);
        // 两个事件都在后台线程上触发，碰界面之前先回主线程
        _admin.ActivityChanged += _ => Callable.From(ShowAdmin).CallDeferred();
        _admin.PatrolCompleted += report => Callable.From(() => OnPatrolCompleted(report)).CallDeferred();
        // 当前就停在某一步上，先把那一步的阶段设定应用上
        if (_run.CurrentStep is { } step) _admin.EnterStage(step.Id);
        _ = _admin.RunAsync(_adminCts.Token);
        ShowAdmin();
    }

    private void OnPatrolCompleted(PatrolReport report)
    {
        if (_closing || _admin is null) return;
        ShowAdmin();
        // 「隐蔽」那一步等的就是他的结论：他查了一遍什么都没发现，才算没被注意到
        _run.Observe(report);
        if (report.FoundSomething && _admin.Visibility == AdminVisibility.Shown)
            SetStatus(report.Sweep
                ? $"{_adminAccount!.User} 把 {report.Machine} 从头查了一遍"
                : $"{_adminAccount!.User} 在 {report.Machine} 上注意到了什么", Colors.Orange);
        if (report.Exposed) OnExposed();
    }

    /// <summary>怀疑度到顶：查实了，任务失败。</summary>
    /// <remarks>
    /// 失败之后按 MVP2 的设计应该能用快照复位到干净的初始状态重来，
    /// 那套还没做，现在只能退回选关重进。
    /// </remarks>
    private void OnExposed()
    {
        _adminCts.Cancel();
        SetStatus($"任务失败：{_adminAccount!.User} 已经确定有人动过 {_level.Admin!.Machine}", Colors.IndianRed);
        if (GameState.IsAutomated) return;

        string why = string.Join("\n", _admin!.Findings.Select(f => "· " + f.Explanation));
        var dialog = new AcceptDialog
        {
            Title = "被发现了",
            DialogText = $"管理员查岗时发现：\n\n{why}\n\n任务失败。",
            OkButtonText = "返回选关",
        };
        AddChild(dialog);
        dialog.Confirmed += () => GameState.Instance.BackToSelect();
        dialog.PopupCentered();
    }

    /// <summary>刷新管理员面板。倒计时每帧会变，所以 <see cref="_Process"/> 也调它。</summary>
    private void ShowAdmin()
    {
        if (_admin is null || !IsInstanceValid(_adminStatus)) return;

        // 阶段可能把他改成隐身（高手模式），也可能反过来
        _adminPanel.Visible = _admin.Visibility == AdminVisibility.Shown;
        if (!_adminPanel.Visible) return;

        string machine = _admin.Machine;
        _adminStatus.Text = _admin.Activity switch
        {
            AdminActivity.LoggingIn => $"{_adminAccount!.User} 正在登录 {machine}",
            AdminActivity.Checking => $"{_adminAccount!.User} 登录着 {machine}，在随手翻",
            AdminActivity.Sweeping => $"{_adminAccount!.User} 起了疑心，正在把 {machine} 从头查一遍",
            _ => $"{_adminAccount!.User} 不在 {machine} 上。下次约在 {Countdown(_admin.TimeToNextPatrol)} 后",
        };
        _adminStatus.Modulate = _admin.Activity switch
        {
            AdminActivity.Sweeping => new Color(1f, 0.5f, 0.45f),
            AdminActivity.Away => Colors.White,
            _ => new Color(1f, 0.8f, 0.4f),
        };

        _threat.MaxValue = _admin.ExposedAt;
        _threat.Value = _admin.Suspicion;

        // 这一栏只在他确实看出了东西时才有内容：没被注意到的时候界面不该有任何提示，
        // 否则玩家能靠界面反推自己有没有留痕
        if (_findings.GetChildCount() == _admin.Findings.Count) return;
        foreach (Node child in _findings.GetChildren()) child.QueueFree();
        foreach (var finding in _admin.Findings)
            _findings.AddChild(new Label
            {
                Text = $"· {finding.Explanation}\n  {finding.Evidence}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(1f, 0.6f, 0.5f),
                CustomMinimumSize = new Vector2(240, 0),
            });
    }

    private static string Countdown(TimeSpan left) =>
        left <= TimeSpan.Zero ? "随时" : $"{(int)left.TotalMinutes}:{left.Seconds:00}";

    public override void _Process(double delta)
    {
        if (_admin is not null && _admin.Activity == AdminActivity.Away) ShowAdmin();
    }

    /// <summary>管理员的口令。玩家在关卡文件里找不到它，只能在客户机上想办法。</summary>
    private static string NewPassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[16];
        random.GetBytes(bytes);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    // --- 自检与截图 ---------------------------------------------------------

    /// <summary>
    /// 设了 GAMEHACKER_SELFTEST 就跑一遍无人值守自检，把报告写到那个路径。
    /// </summary>
    /// <remarks>
    /// 给 Windows / macOS 验证用。Linux 上 M0/M1 的测试走裸串口，
    /// 绕开了 Godot 输入链和 GDExtension —— 恰恰是换平台最容易坏的两处。
    /// </remarks>
    private async Task MaybeSelfTestAsync()
    {
        string? path = System.Environment.GetEnvironmentVariable("GAMEHACKER_SELFTEST");
        if (string.IsNullOrWhiteSpace(path)) return;

        // 替玩家做一遍这一关：在第二台机器上 ping 第一台，等交换机判定步骤完成。
        // 这一路覆盖了 关卡加载 → 组网 → 交换机抓包 → 步骤判定 → 通关，
        // 键盘输入链留给 SelfTest 自己在玩家终端上验
        string bootDetail;
        if (_sessions.Count >= 2)
        {
            await _sessions[1].WaitConsoleReadyAsync(TimeSpan.FromSeconds(30));

            // 敲几次。就绪信标只说明 console_loop 开始跑了，getty 还要一瞬间才把
            // shell 接到 tty 上，这中间送进去的字会被丢掉 —— 实测偶发。
            // 玩家自己敲会发现没反应再敲一次，无人值守这边只能自己重试。
            bool done = false;
            for (int attempt = 0; attempt < 4 && !done; attempt++)
            {
                await TypeAsync(_sessions[1], $"ping -c2 {_level.Machines[0].Nics[0].Ip}\n");
                done = await Task.WhenAny(_completed.Task, Task.Delay(TimeSpan.FromSeconds(6))) == _completed.Task;
            }
            bootDetail = done
                ? $"互通，关卡 {_level.Id} 判定完成，交换机已转发 {_switch!.FramesForwarded} 帧"
                : $"关卡 {_level.Id} 敲了 4 次 ping 都没判定完成（停在第 {_run.CurrentIndex + 1} 步）—— 见 m0/run 下的日志";
        }
        else bootDetail = $"关卡 {_level.Id} 只有 {_sessions.Count} 台机器，自检需要两台";

        bool ok;
        try
        {
            ok = await SelfTest.RunAsync(this, _terminals[0], bootDetail);
        }
        catch (Exception ex)
        {
            ok = false;
            GD.PushError($"[selftest] 自检本身抛异常: {ex}");
        }

        try { System.IO.File.WriteAllText(path, SelfTest.Report()); }
        catch (Exception ex) { GD.PushError($"[selftest] 报告写不出去: {ex.Message}"); }

        SetStatus(ok ? "自检全部通过" : "自检有未通过项，见报告", ok ? Colors.LightGreen : Colors.IndianRed);
        await Task.Delay(300);

        // 没要截图就到此为止；要截图的话留给 MaybeScreenshotAsync 退出
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GAMEHACKER_SCREENSHOT")))
            GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// 设了 GAMEHACKER_SCREENSHOT 就在终端里演示几条命令、截图、退出。
    /// </summary>
    /// <remarks>
    /// M2 的验收标准是"终端里能看见东西"，headless 跑通不算数。
    /// 这条路径让 CI 和评审都能拿到一张实际渲染结果。
    /// </remarks>
    private async Task MaybeScreenshotAsync()
    {
        string? path = System.Environment.GetEnvironmentVariable("GAMEHACKER_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(path)) return;

        // 默认挑的命令要同时展示三样东西：终端渲染、伪装生效、以及抓包面板有货。
        // GAMEHACKER_TYPE 可以换成别的，格式是「机器名:命令」，多条用分号隔开 ——
        // 想截某个特定状态（比如管理员发现了什么）时用得上
        string? script = System.Environment.GetEnvironmentVariable("GAMEHACKER_TYPE");
        if (string.IsNullOrWhiteSpace(script))
        {
            await _sessions[0].WaitConsoleReadyAsync(TimeSpan.FromSeconds(30));
            await TypeAsync(_sessions[0], "uname -a; cat /sys/class/dmi/id/product_name\n");
            if (_sessions.Count >= 2)
                await TypeAsync(_sessions[1], $"ping -c2 {_level.Machines[0].Nics[0].Ip}\n");
        }
        else
        {
            foreach (string entry in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split(':', 2);
                var target = _sessions.FirstOrDefault(s => s.Spec.Name == parts[0]);
                if (parts.Length < 2 || target is null) { GD.PushWarning($"[level] 看不懂的 GAMEHACKER_TYPE 条目: {entry}"); continue; }
                await target.WaitConsoleReadyAsync(TimeSpan.FromSeconds(30));
                await TypeAsync(target, parts[1] + "\n");
            }
        }

        // 截图前多等一会儿，用来等某件事发生（比如管理员来查岗）
        double extra = double.TryParse(
            System.Environment.GetEnvironmentVariable("GAMEHACKER_SCREENSHOT_DELAY"), out double d) ? d : 4;
        await Task.Delay(TimeSpan.FromSeconds(extra));

        // 截图必须在主线程、且要等当前帧画完
        await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(path);
        GD.Print(err == Error.Ok ? $"[level] 截图已存 {path}" : $"[level] 截图失败: {err}");

        await Task.Delay(300);
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    private static async Task TypeAsync(VmSession vm, string text)
    {
        await vm.SendToConsoleAsync(System.Text.Encoding.UTF8.GetBytes(text));
        await Task.Delay(1200);
    }

    private void SetStatus(string text, Color? color = null)
    {
        // 可能从后台线程调进来，统一走主线程；返回选关后节点已释放，就不再碰它
        Callable.From(() =>
        {
            if (!IsInstanceValid(_status)) return;
            _status.Text = text;
            _status.TooltipText = text;
            _status.Modulate = color ?? Colors.White;
        }).CallDeferred();
        GD.Print($"[level] {text}");
    }

    /// <summary>
    /// 离开关卡（返回选关或退出游戏）时同步清理。
    /// </summary>
    /// <remarks>
    /// 这里<b>不能</b>写成 <c>async void</c> 再 await —— 引擎不会等它，
    /// 进程会在 QEMU 被杀之前就退出，留下每台占满一核的孤儿虚拟机。
    /// </remarks>
    public override void _ExitTree()
    {
        _closing = true;
        _adminCts.Cancel();
        _adminTty?.Dispose();
        if (_run is not null) _packetLog.PacketCaptured -= _run.Observe;

        // 三端验证时把这一轮的流量留成证据，用 Wireshark 就能对照
        string? pcap = System.Environment.GetEnvironmentVariable("GAMEHACKER_PCAP");
        if (!string.IsNullOrWhiteSpace(pcap))
        {
            try { _packetLog.WritePcap(pcap); GD.Print($"[level] 抓包已存 {pcap}"); }
            catch (Exception ex) { GD.PushError($"[level] 抓包写不出去: {ex.Message}"); }
        }

        // 关虚拟机是离开关卡时唯一的大头。先把「请你自己退出」一次性发给所有机器，
        // 再挨个收尾 —— 串行地一台台等的话，时间是相加的。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var quits = _sessions.Select(s => s.RequestQuitAsync()).ToArray();
        try { Task.WaitAll(quits, QemuLauncher.QuitGrace); }
        catch (AggregateException) { /* 失败的那台由下面的 Dispose 强杀兜底 */ }

        foreach (var s in _sessions) s.Dispose();
        _sessions.Clear();
        _switch?.DisposeAsync().AsTask().Wait(2000);
        GD.Print($"[level] 离开关卡，关掉 {quits.Length} 台虚拟机共 {watch.ElapsedMilliseconds}ms");
    }
}
