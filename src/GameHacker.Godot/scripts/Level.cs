using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

    public override void _Ready()
    {
        _status = GetNode<Label>("%Status");
        _levelTitle = GetNode<Label>("%LevelTitle");
        _back = GetNode<Button>("%Back");
        _machinesBox = GetNode<HBoxContainer>("%Machines");
        _stepsBox = GetNode<VBoxContainer>("%Steps");
        _hint = GetNode<Label>("%Hint");
        _packets = GetNode<PacketPanel>("%Packets");
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

        _run = new LevelRun(level);
        _run.StepCompleted += step => Callable.From(() => OnStepCompleted(step)).CallDeferred();
        _run.Completed += () => _completed.TrySetResult();
        // 交换机转发线程上触发；LevelRun 自己加锁，UI 更新再倒回主线程
        _packetLog.PacketCaptured += _run.Observe;

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
            pane.AddChild(new Label { Text = $"{m.Name} — {m.Ip}   {product}" });

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
            // 交换机是概要书里唯一的流量观察点，抓包面板和关卡判定都挂在这个旁观者上
            _switch = new VirtualSwitch(port: 0, _packetLog);
            _ = _switch.RunAsync();

            SetStatus($"交换机监听 127.0.0.1:{_switch.Port}，正在拉起 {_level.Machines.Count} 台虚拟机…");

            // 人设决定客户机对玩家宣称的主板 / BIOS / CPU / 内核版本，已经在加载关卡时校验过
            for (int i = 0; i < _level.Machines.Count; i++)
            {
                if (_closing) return;
                _sessions.Add(new VmSession(NewSpec(_level.Machines[i], i, _switch.Port), _terminals[i], this));
            }

            // 排查间歇性启动问题时用 GAMEHACKER_BOOT_TIMEOUT 缩短等待
            double bootSeconds = double.TryParse(
                System.Environment.GetEnvironmentVariable("GAMEHACKER_BOOT_TIMEOUT"),
                out double t) ? t : 90;
            var boot = TimeSpan.FromSeconds(bootSeconds);
            await Task.WhenAll(_sessions.Select(s => s.WaitReadyAsync(boot)));
            if (_closing) return;

            SetStatus($"{_sessions.Count} 台机器就绪");
            _terminals[0].GrabFocus();

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

    private static VmSpec NewSpec(MachineDefinition m, int index, int switchPort) => new()
    {
        Name = m.Name,
        Persona = HardwarePersona.ByName(m.Persona)!,
        KernelPath = GamePaths.Kernel,
        InitrdPath = GamePaths.Initrd,
        DiskPath = m.Disk is null ? null : GamePaths.Disk(m.Disk),
        IpAddress = $"{m.Ip}/24",
        MacAddress = $"52:54:00:00:00:{index + 1:x2}",
        MemoryMegabytes = m.Memory,
        // 关卡里不写回镜像，保持基础镜像干净。
        // 真正的存档走 qcow2 backing file + user:// 下的 overlay。
        Ephemeral = m.Disk is not null,
        // 交换机用 bind 0 让系统分配端口，避免多开或并发测试时撞端口
        SwitchPort = switchPort,
    };

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

    private void OnStepCompleted(LevelStep step)
    {
        if (_closing) return;
        GD.Print($"[level] 步骤完成: {step.Id}");
        ShowSteps();
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
            await TypeAsync(_sessions[1], $"ping -c2 {_level.Machines[0].Ip}\n");
            bool done = await Task.WhenAny(_completed.Task, Task.Delay(TimeSpan.FromSeconds(20))) == _completed.Task;
            bootDetail = done
                ? $"互通，关卡 {_level.Id} 判定完成，交换机已转发 {_switch!.FramesForwarded} 帧"
                : $"关卡 {_level.Id} 20 秒内没有判定完成（停在第 {_run.CurrentIndex + 1} 步）—— 见 m0/run 下的日志";
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

        // 挑的命令要同时展示三样东西：终端渲染、伪装生效、以及抓包面板有货
        await TypeAsync(_sessions[0], "uname -a; cat /sys/class/dmi/id/product_name\n");
        if (_sessions.Count >= 2)
            await TypeAsync(_sessions[1], $"ping -c2 {_level.Machines[0].Ip}\n");
        await Task.Delay(4000);

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
        if (_run is not null) _packetLog.PacketCaptured -= _run.Observe;

        // 三端验证时把这一轮的流量留成证据，用 Wireshark 就能对照
        string? pcap = System.Environment.GetEnvironmentVariable("GAMEHACKER_PCAP");
        if (!string.IsNullOrWhiteSpace(pcap))
        {
            try { _packetLog.WritePcap(pcap); GD.Print($"[level] 抓包已存 {pcap}"); }
            catch (Exception ex) { GD.PushError($"[level] 抓包写不出去: {ex.Message}"); }
        }

        foreach (var s in _sessions) s.Dispose();
        _sessions.Clear();
        _switch?.DisposeAsync().AsTask().Wait(2000);
    }
}
