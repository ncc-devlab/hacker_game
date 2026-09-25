using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
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
    private Desktop _desktop = null!;
    private HBoxContainer _windowBar = null!;
    private PacketPanel _packets = null!;

    // --- 桌面 ---
    /// <summary>玩家机上最多多开几个终端。实际能开几个还要看串口够不够（见 <see cref="QemuLauncher.ExtraConsoleTtys(bool, int)"/>）。</summary>
    private const int ExtraTerminals = 2;
    /// <summary>委托人的署名。任务信都从这里来。</summary>
    private const string Client = "委托人";
    private readonly MailPanel _mail = new();
    private Mail? _briefing;
    private GameWindow _mailWindow = null!;
    private GameWindow _packetWindow = null!;
    private DesktopIcon _mailIcon = null!;
    /// <summary>每台机器的主终端窗（玩家看得见的那几台）。默认布局把它们摆在左边一大块。</summary>
    private readonly List<GameWindow> _machineWindows = [];
    /// <summary>玩家机上多开的终端：窗、终端节点、客户机里的设备名。开局都是关着的。</summary>
    private readonly List<(GameWindow Window, Control Terminal, string Tty)> _extraTerms = [];
    private readonly List<(GameWindow Window, Button Button, string Label)> _taskbar = [];

    // 「神器」blackwall：教学关里开了接入口的目标机。IP -> 目标机会话；每台目标机
    // 在玩家桌面上最多一扇直连窗，第一次接进去时才建，之后复用
    private readonly Dictionary<string, VmSession> _blackwallByIp = [];
    private readonly Dictionary<VmSession, (GameWindow Window, Control Terminal, TerminalBridge Bridge)> _blackwallWins = [];

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
    /// <summary>看不见的那几台机器的终端挂在这儿：不显示，但还在场景树里收数据。</summary>
    private Control _hidden = null!;
    private GameWindow? _adminWindow;
    /// <summary>界面上显不显示管理员的动向。高手模式（visibility: hidden）下什么都不说。</summary>
    private bool _adminShown = true;
    private Label _adminStatus = null!;
    private ProgressBar _threat = null!;
    private VBoxContainer _findings = null!;
    private AdminAgent? _admin;
    private TtySession? _adminTty;
    private AdminAccount? _adminAccount;
    private AdminSkill _adminSkill;
    private readonly System.Threading.CancellationTokenSource _adminCts = new();

    /// <summary>玩家手上那几个账号，按机器名。口令这一局现生成。</summary>
    private readonly Dictionary<string, GuestAccess> _access = [];

    // --- 客户机状态探查 ---
    private int _probing;        // 同一时刻只许有一次探查在飞
    private int _probeAgain;     // 飞行期间又来了触发

    public override void _Ready()
    {
        _status = GetNode<Label>("%Status");
        _levelTitle = GetNode<Label>("%LevelTitle");
        _back = GetNode<Button>("%Back");
        _desktop = GetNode<Desktop>("%Desktop");
        _windowBar = GetNode<HBoxContainer>("%WindowBar");
        _packets = GetNode<PacketPanel>("%Packets");
        _adminPanel = GetNode<PanelContainer>("%Admin");
        _hidden = GetNode<Control>("%Desktop/Parts");
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

        // 玩家手上的登录方式。口令每局现生成，关卡文件里不留 —— 和管理员的账号同一个规矩，
        // 玩家翻关卡文件也拿不到。提示里的 {机器名.password} 会换成这一局真的那个
        foreach (var m in level.Machines)
            if (m.Access is { } access)
                _access[m.Name] = new GuestAccess(access.Port, access.User, NewPassword(), access.Sudo);

        // 口令每局现生成，关卡文件里不留 —— 玩家翻关卡文件也拿不到管理员的账号
        if (level.Admin is { } adminDefinition)
        {
            _adminAccount = new AdminAccount(adminDefinition.User, NewPassword());
            // 这一局来的是谁：关卡指定了就是那一档，否则按这一关在当前模式下的概率抽
            _adminSkill = GameState.ForcedAdminSkill
                          ?? AdminSkillOdds.Roll(GameState.Instance.Mode, adminDefinition, new Random());
            GD.Print($"[level] 管理员 {adminDefinition.User}：{SkillName(_adminSkill)}（{GameState.Instance.Mode} 模式）");
            // 高手模式（visibility: hidden）下界面上什么都不说，
            // 玩家只能自己从机器上看出他来过
            _adminShown = adminDefinition.Visibility == AdminVisibility.Shown;
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

        BuildWindows();
        DeliverBriefing();

        // 窗口层叠/吸附/停靠/缩放的无人值守自检：不需要虚拟机，跑完即退
        if (System.Environment.GetEnvironmentVariable("GAMEHACKER_PROBE_WINDOWS") is "1")
        {
            _ = ProbeWindowsAsync();
            return;
        }

        if (!GamePaths.ImagesReady)
        {
            SetStatus($"缺少客户机镜像。先跑 m0/scripts/00-fetch-images.sh 与 01-build-guest-initramfs.sh\n"
                      + $"找的位置: {GamePaths.ImagesDir}", Colors.Orange);
            return;
        }

        _ = BootAsync();
    }

    /// <summary>
    /// 桌面上摆哪几扇窗、哪几个图标。
    /// </summary>
    /// <remarks>
    /// <para><b>终端只摆玩家自己那台机器的</b>（第一台）。别人的机器要么写了
    /// <see cref="MachineDefinition.Shell"/>（教学关白送 shell 是为了讲机制），
    /// 要么玩家得自己从网络上进去。以前每台机器都摆一个终端，那只是调试时方便 ——
    /// 白送的 shell 会把「打进跳板机」这件事整个跳过去。</para>
    /// <para>看不见的机器照样建终端节点，只是不放进任何一扇窗：串口、桥接、resize
    /// 全都照常工作，自检和截图也还能往它的控制台里敲字。</para>
    /// <para>桌面图标是「关掉之后从哪儿再打开」的答案：邮件、抓包各一个，
    /// 「终端」在玩家自己的机器上多开一个 shell（多开的那几扇开局都是关着的）。
    /// 没有图标的窗（机器的主终端、管理员）就不给关，只能最小化。</para>
    /// <para>窗口的默认位置要等桌面真的有了尺寸才算得出来，所以挂在
    /// <see cref="Control.Resized"/> 上，摆一次就撤。</para>
    /// </remarks>
    private void BuildWindows()
    {
        // 调试用：把所有机器的终端都摆出来，不必改关卡文件
        bool all = System.Environment.GetEnvironmentVariable("GAMEHACKER_ALL_SHELLS") is "1" or "true";

        for (int i = 0; i < _level.Machines.Count; i++)
        {
            var m = _level.Machines[i];
            var terminal = NewTerminal($"Terminal_{m.Name}");
            _terminals.Add(terminal);

            if (!(all || i == 0 || m.Shell))
            {
                // 玩家看不见它，但它得在场景树里才收得到数据
                terminal.Visible = false;
                _hidden.AddChild(terminal);
                continue;
            }

            string product = HardwarePersona.ByName(m.Persona)?.SystemProduct ?? "";
            string ips = string.Join(" / ", m.Nics.Select(n => n.Ip));
            var window = _desktop.Open($"{m.Name} — {ips}   {product}", terminal, closable: false);
            // 点进终端就把它那扇窗提到最前，不然会被别的窗压着还在接收键盘
            terminal.FocusEntered += () => _desktop.BringToFront(window);
            _machineWindows.Add(window);
            AddTaskButton(window, m.Name);
        }

        _mailWindow = _desktop.Open("邮件", _mail);
        AddTaskButton(_mailWindow, "邮件");

        _adminWindow = _desktop.Open("管理员", _adminPanel, closable: false);
        // 高手模式下这扇窗根本不出现：玩家只能自己从机器上看出他来过
        if (_adminShown) AddTaskButton(_adminWindow, "管理员");
        else _adminWindow.StartClosed();

        _packetWindow = _desktop.Open("抓包", _packets);
        AddTaskButton(_packetWindow, "抓包");

        // 玩家机上多开的终端：串口在开机时就接好，窗口等玩家点图标才打开
        var home = _level.Machines[0];
        foreach (string tty in QemuLauncher.ExtraConsoleTtys(HasAdmin(home), ExtraTerminals))
        {
            var terminal = NewTerminal($"Terminal_{home.Name}_{tty}");
            var window = _desktop.Open($"{home.Name} — {tty}", terminal);
            terminal.FocusEntered += () => _desktop.BringToFront(window);
            window.StartClosed();
            AddTaskButton(window, $"{home.Name}:{tty[^1]}");
            int index = _extraTerms.Count;
            window.Closed += _ => ExtraBridge(index)?.HangUp();
            _extraTerms.Add((window, terminal, tty));
        }

        _mailIcon = new DesktopIcon("邮件", DesktopIcon.Art.Mail, "双击打开邮件：任务从这里来");
        _mailIcon.Activated += _mailWindow.Reopen;
        _desktop.AddIcon(_mailIcon);
        var terminalIcon = new DesktopIcon("终端", DesktopIcon.Art.Terminal, $"双击在 {home.Name} 上再开一个终端");
        terminalIcon.Activated += OpenExtraTerminal;
        _desktop.AddIcon(terminalIcon);
        var packetIcon = new DesktopIcon("抓包", DesktopIcon.Art.Packets, "双击打开抓包");
        packetIcon.Activated += _packetWindow.Reopen;
        _desktop.AddIcon(packetIcon);

        _mail.UnreadChanged += unread =>
        {
            _mailIcon.Badge = unread;
            SyncTaskbar();
        };
        _desktop.StackChanged += SyncTaskbar;

        _desktop.Resized += LayoutOnce;
        LayoutOnce();
    }

    /// <summary>Terminal 是 godot-xterm 的 GDExtension 类，C# 里没有对应类型，只能按类名实例化。</summary>
    private static Control NewTerminal(string name)
    {
        var terminal = ClassDB.Instantiate("Terminal").As<Control>();
        terminal.Name = name;
        return terminal;
    }

    /// <summary>这台机器上有没有管理员 —— 有的话 ttyS2 是他的，多开的终端少一个。</summary>
    private bool HasAdmin(MachineDefinition machine) =>
        _adminAccount is not null && _level.Admin?.Machine == machine.Name;

    private TerminalBridge? ExtraBridge(int index) =>
        _sessions.Count > 0 && index < _sessions[0].ExtraBridges.Count ? _sessions[0].ExtraBridges[index] : null;

    /// <summary>
    /// 桌面上「终端」图标：在玩家自己的机器上打开一个新 shell。
    /// </summary>
    /// <remarks>
    /// 每次打开的都是干净的：先把屏幕复位，再敲一下回车让 shell 在正确的宽度下重新出提示符。
    /// 窗口关着的时候终端没有排过版，开机时 getty 打的那行提示符是按一列宽排的，不清掉就是一团乱码。
    /// </remarks>
    private void OpenExtraTerminal() => OpenExtraTerminalCore();

    /// <returns>打开的那个终端节点；一个都开不出来时为 null。自检也走这条路。</returns>
    private Control? OpenExtraTerminalCore()
    {
        int index = _extraTerms.FindIndex(t => !t.Window.IsOpen);
        if (index < 0)
        {
            if (_extraTerms.Count == 0)
            {
                SetStatus($"{_level.Machines[0].Name} 上没有多余的串口可以再开终端了", Colors.Orange);
                return null;
            }
            // 都开着：把最后一个提到前面，说一声为什么没有新的
            var last = _extraTerms[^1].Window;
            last.Reopen();
            SetStatus($"{_level.Machines[0].Name} 只多接了 {_extraTerms.Count} 根串口，终端都已经开着了", Colors.Orange);
            return null;
        }

        var (window, terminal, _) = _extraTerms[index];
        var bridge = ExtraBridge(index);
        bridge?.ResetScreen();
        // 从最近那扇终端错开一点摆，一眼看得出是新开的
        var anchor = _desktop.Windows.LastOrDefault(w => w.Shown && _extraTerms.Any(t => t.Window == w))
                     ?? _machineWindows.FirstOrDefault();
        if (anchor is not null)
        {
            // 至少 560×320，但不超过桌面的七成。画面窄的时候七成比 560 还小，
            // 这时让「不超过桌面」说了算（Clamp 在 min > max 时直接抛异常，实测踩到过）
            var size = anchor.Rect.Size.Max(new Vector2(560, 320)).Min(_desktop.Size * 0.7f);
            window.PlaceAt(new Rect2(anchor.Position + new Vector2(36, 36), size));
        }
        window.Reopen();
        terminal.GrabFocus();
        if (bridge is not null)
            GetTree().CreateTimer(0.25).Timeout += () =>
            {
                if (!IsInstanceValid(bridge)) return;
                bridge.ResendSize();
                bridge.Poke();
            };
        return terminal;
    }

    /// <summary>
    /// 登记教学关里开了「神器」接入口的目标机，并监听玩家机发来的 reach 请求。
    /// </summary>
    /// <remarks>
    /// 玩家机上的 <c>blackwall &lt;ip&gt;</c> 脚本往自己的 ttyS1 写一行
    /// <c>{"ev":"reach","ip":..}</c>，宿主的 <see cref="ControlChannel"/> 解析出来经
    /// <see cref="ControlChannel.EventPublished"/> 到这里。这是唯一一处「客户机主动请宿主办事」，
    /// 靠教学关专属兜住（见 <see cref="NewSpec"/> 的 Blackwall 门槛）。
    /// </remarks>
    private void RegisterBlackwall()
    {
        for (int i = 0; i < _sessions.Count; i++)
        {
            if (_sessions[i].BlackwallTty is null) continue;   // 这台机器没开接入口
            foreach (var nic in _level.Machines[i].Nics)
                _blackwallByIp[nic.Ip] = _sessions[i];
        }
        if (_blackwallByIp.Count == 0) return;

        // 事件在后台读线程上来，UI 操作要倒回主线程
        _sessions[0].Control.EventPublished += ev =>
        {
            if (ev["ev"]?.GetValue<string>() != "reach") return;
            if (ev["ip"]?.GetValue<string>() is not { } ip) return;
            Callable.From(() => OpenBlackwallCore(ip)).CallDeferred();
        };
    }

    /// <summary>
    /// 「神器」接进一台机器：给个 IP，在桌面上弹出（或提回最前）一扇直连它的 root 终端。
    /// </summary>
    /// <remarks>
    /// 目标机那头是常驻的 <c>getty -n -l /bin/sh</c>，所以这是个真 pty ——
    /// Tab 补全、方向键都能用，不像维护口那条裸套接字。每台目标机只开一扇窗，复用。
    /// 自检也直接走这条路（不经玩家机的脚本）。
    /// </remarks>
    /// <returns>接进去的那个终端节点；够不着或没开接入口时为 null。</returns>
    private Control? OpenBlackwallCore(string ip)
    {
        if (!_blackwallByIp.TryGetValue(ip, out var target))
        {
            SetStatus($"blackwall: 够不着 {ip} —— 这台机器没开神器通道", Colors.Orange);
            return null;
        }
        if (target.BlackwallTty is not { } channel)
        {
            SetStatus($"blackwall: {ip} 的接入口没起来", Colors.Orange);
            return null;
        }

        if (!_blackwallWins.TryGetValue(target, out var slot))
        {
            var terminal = NewTerminal($"Blackwall_{target.Spec.Name}");
            var window = _desktop.Open($"blackwall — {target.Spec.Name} {ip}", terminal);
            terminal.FocusEntered += () => _desktop.BringToFront(window);
            var bridge = new TerminalBridge { Name = $"Bridge_bw_{target.Spec.Name}" };
            AddChild(bridge);
            bridge.Attach(terminal, channel, target.Control, QemuLauncher.BlackwallTty(target.Spec)!);
            bridge.PlayerSubmitted += NudgeProbe;
            // 关窗就挂断那头的会话，getty 重开一个干净的；下次接进去是全新 shell
            window.Closed += _ => bridge.HangUp();
            AddTaskButton(window, $"⚡{target.Spec.Name}");
            slot = (window, terminal, bridge);
            _blackwallWins[target] = slot;
        }

        slot.Bridge.ResetScreen();
        // 从玩家自己的机器窗错开一点摆，一眼看出是神器接进来的
        var anchor = _machineWindows.FirstOrDefault();
        if (anchor is not null)
        {
            var size = anchor.Rect.Size.Max(new Vector2(560, 320)).Min(_desktop.Size * 0.7f);
            slot.Window.PlaceAt(new Rect2(anchor.Position + new Vector2(48, 48), size));
        }
        slot.Window.Reopen();
        slot.Terminal.GrabFocus();
        var bw = slot.Bridge;
        GetTree().CreateTimer(0.25).Timeout += () =>
        {
            if (!IsInstanceValid(bw)) return;
            bw.ResendSize();
            bw.Poke();
        };
        return slot.Terminal;
    }

    /// <summary>
    /// 顶栏上那排按钮，相当于任务栏：点一下没在前台的窗就提上来，
    /// 点前台那扇就最小化，最小化着的就还原。关掉的窗按钮也跟着消失。
    /// </summary>
    private void AddTaskButton(GameWindow window, string label)
    {
        var button = new Button { Text = label, ToggleMode = true, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += () =>
        {
            if (!window.Shown) window.Reopen();
            else if (window.Active) window.Minimize();
            else _desktop.BringToFront(window);
            SyncTaskbar();
        };
        window.StateChanged += _ => SyncTaskbar();
        _taskbar.Add((window, button, label));
        _windowBar.AddChild(button);
        SyncTaskbar();
    }

    private void SyncTaskbar()
    {
        foreach (var (window, button, label) in _taskbar)
        {
            button.Visible = window.IsOpen;
            button.SetPressedNoSignal(window.Shown && window.Active);
            button.Text = window == _mailWindow && _mail.Unread > 0 ? $"{label} ({_mail.Unread})" : label;
        }
    }

    private bool _laidOut;

    /// <summary>
    /// 默认布局：左边留一列给桌面图标，终端占一大块，邮件和管理员在右边一列，抓包贴在下面。
    /// </summary>
    /// <remarks>
    /// 摆过一次就不再管，之后是玩家自己的布局（画面变大变小时按比例跟着走，见 <see cref="Desktop"/>）。
    /// </remarks>
    private void LayoutOnce()
    {
        if (_laidOut) return;

        // 桌面还没排过版时 Size 是 0（无头模式下 Resized 可能一次都不响）。
        // 不能就这么放过：窗口留在 0 尺寸，里面的终端就只有一列宽，
        // 玩家看到的是一条缝，自检读出来的屏幕内容只剩一个字符。实测踩到过
        var area = _desktop.Size;
        if (area.X < 1 || area.Y < 1) area = GetViewportRect().Size;
        if (area.X < 1 || area.Y < 1) return;
        _laidOut = true;

        float x0 = Desktop.IconGutter;
        float width = area.X - x0, height = area.Y;
        float rightWidth = Mathf.Clamp(width * 0.26f, 280, 400);
        float packetHeight = Mathf.Clamp(height * 0.28f, 160, 260);
        float left = width - rightWidth;

        float each = _machineWindows.Count == 0 ? 0 : (height - packetHeight) / _machineWindows.Count;
        for (int i = 0; i < _machineWindows.Count; i++)
            _machineWindows[i].PlaceAt(new Rect2(x0, i * each, left, each));

        _packetWindow.PlaceAt(new Rect2(x0, height - packetHeight, left, packetHeight));
        // 高手模式下没有管理员那扇，邮件就占满右边一整条
        float mailHeight = _adminShown ? height * 0.6f : height;
        _mailWindow.PlaceAt(new Rect2(x0 + left, 0, rightWidth, mailHeight));
        _adminWindow!.PlaceAt(new Rect2(x0 + left, mailHeight, rightWidth, height - mailHeight));
    }

    // --- 邮件 ---------------------------------------------------------------

    /// <summary>开局那封委托信。里面的任务清单跟着进度改，永远是最新的。</summary>
    private void DeliverBriefing()
    {
        _briefing = _mail.Deliver(Client, $"委托：{_level.Title}", BriefingBody());
    }

    private string BriefingBody()
    {
        var text = new System.Text.StringBuilder();
        if (_level.Briefing.Length > 0) text.Append(MailPanel.Escape(_level.Briefing)).Append("\n\n");

        text.Append("[b]要做的事[/b]\n");
        int current = _run.CurrentIndex;
        for (int i = 0; i < _level.Steps.Count; i++)
        {
            string line = MailPanel.Escape($"{i + 1}. {_level.Steps[i].Title}");
            text.Append(i < current ? $"[color=#8fd18f]{line}   完成[/color]\n"
                      : i == current ? $"[b]{line}   进行中[/b]\n"
                      : $"[color=#808080]{line}[/color]\n");
        }

        text.Append("\n[b]眼下这一步[/b]\n").Append(StepAdvice(_run.CurrentStep));
        return text.ToString();
    }

    /// <summary>这一步的提示（口令这些占位符换成这一局真的）。已经做完了就说做完了。</summary>
    private string StepAdvice(LevelStep? step) => MailPanel.Escape(Fill(step switch
    {
        null => "全部完成。",
        { Check: null } s => $"{s.Title}\n{s.Hint}\n\n（草稿关：这一步还没写判定，不会自动推进）",
        var s => $"{s.Title}\n{s.Hint}",
    }));

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
                // 玩家自己的机器多接几根串口，桌面上「终端」图标开出来的窗就接在这上面
                var extras = i == 0 ? _extraTerms.Select(t => t.Terminal).ToList() : null;
                var session = new VmSession(NewSpec(i, _switch), _terminals[i], this, extras);
                _sessions.Add(session);
                if (i == 0 && !session.ExtraBridges.Select(b => b.Tty).SequenceEqual(_extraTerms.Select(t => t.Tty)))
                    GD.PushError($"[level] 多开终端的串口对不上：窗口按 {string.Join(",", _extraTerms.Select(t => t.Tty))} 建的，"
                                 + $"虚拟机接的是 {string.Join(",", session.ExtraBridges.Select(b => b.Tty))}");
            }

            // 排查间歇性启动问题时用 GAMEHACKER_BOOT_TIMEOUT 缩短等待
            double bootSeconds = double.TryParse(
                System.Environment.GetEnvironmentVariable("GAMEHACKER_BOOT_TIMEOUT"),
                out double t) ? t : 90;
            var boot = TimeSpan.FromSeconds(bootSeconds);
            await Task.WhenAll(_sessions.Select(s => s.WaitReadyAsync(boot)));
            if (_closing) return;

            // 玩家在任何一台机器上敲了回车，就去看一眼这一步要看的状态
            foreach (var session in _sessions)
            {
                session.Bridge.PlayerSubmitted += NudgeProbe;
                foreach (var extra in session.ExtraBridges) extra.PlayerSubmitted += NudgeProbe;
            }

            RegisterBlackwall();

            SetStatus(GameState.Instance.Mode == PlayMode.Novice
                ? $"{_sessions.Count} 台机器就绪。新手模式：你的机器上装了几个工具，敲 tools 看看"
                : $"{_sessions.Count} 台机器就绪");
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

    /// <summary>
    /// 窗口层叠 / 吸附 / 停靠 / 随画面缩放的无人值守自检。不启动虚拟机，跑完即退。
    /// </summary>
    private async Task ProbeWindowsAsync()
    {
        GetTree().Root.Size = new Vector2I(1600, 900);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) fails++;
            GD.Print($"[winprobe] {(ok ? "ok  " : "FAIL")} {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }
        bool Overlap(GameWindow a, GameWindow b) =>
            new Rect2(a.Position, a.Size).Intersects(new Rect2(b.Position, b.Size));

        async Task<(Desktop, GameWindow, GameWindow)> FreshDesk(Vector2 size)
        {
            var desk = new Desktop { Position = Vector2.Zero, Size = size };
            AddChild(desk);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            desk.Size = size;
            var a = desk.Open("A", new Control());
            var b = desk.Open("B", new Control());
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            return (desk, a, b);
        }

        bool Near(Rect2 a, Rect2 b) =>
            a.Position.DistanceTo(b.Position) < 0.5f && a.Size.DistanceTo(b.Size) < 0.5f;
        Rect2 RectOf(GameWindow w) => new(w.Position, w.Size);

        // 1) 层叠：A 拖到 B 上，B 原地不动，两扇就这么压着，A 在上面
        {
            var (desk, a, b) = await FreshDesk(new Vector2(1200, 720));
            b.PlaceAt(new Rect2(240, 240, 400, 300));
            a.PlaceAt(new Rect2(720, 240, 400, 300));
            var bBefore = RectOf(b);
            desk.BringToFront(a);
            desk.DragTo(a, new Vector2(330, 310));
            desk.FinishDrag(a);
            Check("层叠：B 没被挤走", Near(RectOf(b), bBefore), $"B {bBefore} -> {RectOf(b)}");
            Check("层叠：A 压在 B 上", Overlap(a, b) && a.GetIndex() > b.GetIndex(), $"A={RectOf(a)}");
            Check("层叠：A 完全跟手", a.Position == new Vector2(330, 310), $"{a.Position}");
            Check("层叠：只有最前那扇亮着", a.Active && !b.Active);

            // 2) 点到哪扇提哪扇：点 B 露在外面的那一角
            desk.RaiseAt(new Vector2(260, 260));
            Check("点击提前：B 到了最前", b.GetIndex() > a.GetIndex() && b.Active && !a.Active);
            // 点在两扇重叠的地方，最上面那扇（B）还是最上面
            desk.RaiseAt(new Vector2(400, 400));
            Check("点击提前：重叠处点的是上面那扇", b.GetIndex() > a.GetIndex());
            desk.QueueFree();
        }

        // 3) 吸附：拖到离 B 右边缘 5 像素内就贴上去，离远了完全跟手
        {
            var (desk, a, b) = await FreshDesk(new Vector2(1200, 720));
            b.PlaceAt(new Rect2(100, 100, 300, 300));
            a.PlaceAt(new Rect2(700, 100, 300, 300));
            desk.DragTo(a, new Vector2(405, 150));
            Check("吸附：贴到 B 的右边", Mathf.IsEqualApprox(a.Position.X, 400), $"{a.Position}");
            desk.DragTo(a, new Vector2(437, 151));
            Check("吸附：离远了不吸", a.Position == new Vector2(437, 151), $"{a.Position}");
            desk.FinishDrag(a);
            desk.QueueFree();
        }

        // 4) 停靠：指针到左边缘，松手后滑过去铺满左半边
        {
            var (desk, a, _) = await FreshDesk(new Vector2(1200, 720));
            a.PlaceAt(new Rect2(500, 200, 300, 300));
            desk.AimDock(new Vector2(5, 300));
            desk.FinishDrag(a);
            for (int i = 0; i < 40; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check("停靠：滑到了左半屏", Near(RectOf(a), new Rect2(0, 0, 600, 720)), $"{RectOf(a)}");
            desk.QueueFree();
        }

        // 5) 随画面缩放：桌面变大，窗按比例跟着变；缩回原尺寸，分毫不差
        {
            var (desk, a, b) = await FreshDesk(new Vector2(1200, 720));
            a.PlaceAt(new Rect2(600, 0, 600, 720));
            b.PlaceAt(new Rect2(120, 72, 360, 288));
            desk.Size = new Vector2(1600, 900);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check("缩放：右半屏还是右半屏", Near(RectOf(a), new Rect2(800, 0, 800, 900)), $"{RectOf(a)}");
            Check("缩放：B 等比放大", Near(RectOf(b), new Rect2(160, 90, 480, 360)), $"{RectOf(b)}");
            desk.Size = new Vector2(500, 300);   // 小到 B 被最小尺寸撑住
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check("缩放：不小于最小窗", b.Size.X >= GameWindow.MinSize.X && b.Size.Y >= GameWindow.MinSize.Y, $"{b.Size}");
            desk.Size = new Vector2(1200, 720);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check("缩放：缩回去不走样", Near(RectOf(b), new Rect2(120, 72, 360, 288)), $"{RectOf(b)}");
            desk.QueueFree();
        }

        // 6) 拉边框：一小步一小步地拉也跟手；缩到最小后指针继续往里，再拉回来边框还在指针下
        {
            var (desk, a, _) = await FreshDesk(new Vector2(1200, 720));
            a.PlaceAt(new Rect2(100, 100, 400, 300));
            a.BeginResize();
            for (int dx = 1; dx <= 30; dx++) a.ResizeBy(new Vector2I(1, 0), new Vector2(dx, 0));
            Check("拉边框：小步拉得动", Mathf.IsEqualApprox(a.Size.X, 430), $"{a.Size}");
            a.ResizeBy(new Vector2I(1, 0), new Vector2(-500, 0));
            Check("拉边框：不小于最小窗", Mathf.IsEqualApprox(a.Size.X, GameWindow.MinSize.X), $"{a.Size}");
            a.ResizeBy(new Vector2I(1, 0), new Vector2(-50, 0));
            Check("拉边框：拉回来不跳", Mathf.IsEqualApprox(a.Size.X, 350), $"{a.Size}");
            desk.QueueFree();
        }

        async Task Frames(int n)
        {
            for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        // 7) 最小化 / 还原：收进去以后下面那扇亮起来，还原回来又在最前
        {
            var (desk, a, b) = await FreshDesk(new Vector2(1200, 720));
            a.PlaceAt(new Rect2(100, 100, 400, 300));
            b.PlaceAt(new Rect2(300, 200, 400, 300));
            desk.BringToFront(a);
            a.Minimize();
            Check("最小化：还在任务栏上", a.IsOpen && a.Minimized && !a.Shown);
            Check("最小化：下面那扇亮了", b.Active && !a.Active);
            await Frames(20);
            Check("最小化：淡出后真的藏起来了", !a.Visible && a.Modulate.A > 0.99f && a.Scale == Vector2.One);
            a.Reopen();
            await Frames(20);
            Check("还原：回到最前、亮着、位置没变", a.Shown && a.Active && a.GetIndex() > b.GetIndex()
                                                  && Near(RectOf(a), new Rect2(100, 100, 400, 300)), $"{RectOf(a)}");
            desk.QueueFree();
        }

        // 8) 关闭：能关的关掉就离开任务栏；不能关的点了也没用
        {
            var desk = new Desktop { Position = Vector2.Zero, Size = new Vector2(1200, 720) };
            AddChild(desk);
            await Frames(1);
            var a = desk.Open("A", new Control());
            var pinned = desk.Open("P", new Control(), closable: false);
            a.Close();
            pinned.Close();
            Check("关闭：能关的关掉了", !a.IsOpen && !a.Shown);
            Check("关闭：不能关的还开着", pinned.IsOpen && pinned.Shown);
            desk.QueueFree();
        }

        // 9) 最大化 / 还原：铺满桌面，再点回到原处；画面变了以后还原也回得到按比例的原处
        {
            var (desk, a, _) = await FreshDesk(new Vector2(1200, 720));
            a.PlaceAt(new Rect2(120, 72, 360, 288));
            a.ToggleMaximize();
            await Frames(30);
            Check("最大化：铺满桌面", a.Maximized && Near(RectOf(a), new Rect2(0, 0, 1200, 720)), $"{RectOf(a)}");
            desk.Size = new Vector2(1600, 900);
            await Frames(1);
            Check("最大化：画面变大还是满的", Near(RectOf(a), new Rect2(0, 0, 1600, 900)), $"{RectOf(a)}");
            a.ToggleMaximize();
            await Frames(30);
            Check("还原：回到按比例的原处", !a.Maximized && Near(RectOf(a), new Rect2(160, 90, 480, 360)), $"{RectOf(a)}");
            desk.QueueFree();
        }

        GD.Print($"[winprobe] {(fails == 0 ? "全部通过" : $"{fails} 项未通过")}");
        GetTree().Quit(fails == 0 ? 0 : 1);
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
            Access = _access.GetValueOrDefault(m.Name),
            // 新手模式给玩家自己的机器装一套工具。它们不做玩家做不到的事，
            // 只是把那几条命令替他跑一遍并打出来 —— 而且都是 cat 得出来的脚本
            Tools = index == 0 && GameState.Instance.Mode == PlayMode.Novice,
            // 只有教学关、且这一关真有 blackwall 目标机时，才给玩家机装「神器」命令
            BlackwallClient = index == 0 && GameState.Instance.Mode == PlayMode.Novice
                              && _level.Track == LevelTrack.Tutorial
                              && _level.Machines.Any(mm => mm.Blackwall),
            // 桌面上建了几扇多开终端窗，就接几根串口。两边都按
            // QemuLauncher.ExtraConsoleTtys 算，所以落在同样的 ttyS 上
            ExtraConsoles = index == 0 ? _extraTerms.Count : 0,
            // 「神器」接入口：只在教学关生效（实战关写了也不接，见 MachineDefinition.Blackwall），
            // 玩家自己的机器不接自己
            Blackwall = _level.Track == LevelTrack.Tutorial && index != 0 && m.Blackwall,
            // 关卡里不写回镜像，保持基础镜像干净。
            // 真正的存档走 qcow2 backing file + user:// 下的 overlay。
            Ephemeral = m.Disk is not null,
        };
    }

    // --- 任务步骤 -----------------------------------------------------------

    /// <summary>
    /// 把提示里的占位符换成这一局真的那些值：
    /// <c>{jump01.user}</c>、<c>{jump01.password}</c>、<c>{jump01.port}</c>、<c>{jump01.ip}</c>。
    /// </summary>
    /// <remarks>
    /// 口令每局都不一样，关卡文件里写不出来；而玩家总得从某处知道自己手上有什么。
    /// 放在提示里而不是任务简报里 —— 简报在选关界面就要显示，那时这一局还没开始。
    /// </remarks>
    private string Fill(string text)
    {
        foreach (var (name, access) in _access)
            text = text.Replace($"{{{name}.user}}", access.User)
                       .Replace($"{{{name}.password}}", access.Password)
                       .Replace($"{{{name}.port}}", access.Port.ToString());
        foreach (var m in _level.Machines)
            text = text.Replace($"{{{m.Name}.ip}}", m.Nics[0].Ip);
        return text;
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
        if (_briefing is not null) _mail.Revise(_briefing, BriefingBody());
        // 委托人回信：确认这一步，交代下一步。做完了就是一封收尾的信
        if (_run.CurrentStep is { } upcoming)
            _mail.Deliver(Client, $"收到。下一步：{upcoming.Title}",
                          MailPanel.Escape($"「{step.Title}」这边确认了。") + "\n\n" + StepAdvice(upcoming));
        else
            _mail.Deliver(Client, $"完成：{_level.Title}",
                          MailPanel.Escape($"「{step.Title}」确认。「{_level.Title}」到此完成。"));
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
        _admin = new AdminAgent(definition, _adminAccount, _adminTty, skill: _adminSkill);
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
        GD.Print($"[admin] {SkillName(_admin.Skill)}查岗 {report.Machine}：{string.Join(",", report.Did)}"
                 + $"{(report.Escalated ? "（当场全查）" : report.Sweep ? "（彻底检查）" : "")}，"
                 + $"新发现 {report.Findings.Count} 处，怀疑度 {report.Suspicion}/{_admin.ExposedAt}"
                 + (report.PasswordsChanged.Count > 0 ? $"，改了 {string.Join(",", report.PasswordsChanged)} 的口令" : ""));
        ShowAdmin();
        // 「隐蔽」那一步等的就是他的结论：他查了一遍什么都没发现，才算没被注意到
        _run.Observe(report);
        if (report.FoundSomething && _admin.Visibility == AdminVisibility.Shown)
            SetStatus(report.Escalated
                ? $"{_adminAccount!.User} 看出了不对，当场把 {report.Machine} 从头查了一遍"
                : report.Sweep
                ? $"{_adminAccount!.User} 把 {report.Machine} 从头查了一遍"
                : $"{_adminAccount!.User} 在 {report.Machine} 上注意到了什么", Colors.Orange);
        // 口令被改是实打实的后果：玩家下次登录就进不去了。隐身模式下不说，让他自己撞上
        if (report.PasswordsChanged.Count > 0 && _admin.Visibility == AdminVisibility.Shown)
            SetStatus($"{_adminAccount!.User} 改掉了 {string.Join("、", report.PasswordsChanged)} 的口令 —— "
                      + "你手上的登录方式失效了", Colors.Orange);
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
        _adminShown = _admin.Visibility == AdminVisibility.Shown;
        if (_adminWindow is { } window && window.Visible != _adminShown) window.Visible = _adminShown;
        if (!_adminShown) return;

        string machine = _admin.Machine;
        string who = $"{_adminAccount!.User}（{SkillName(_admin.Skill)}）";
        _adminStatus.Text = _admin.Activity switch
        {
            AdminActivity.LoggingIn => $"{who} 正在登录 {machine}",
            AdminActivity.Checking => _admin.Skill == AdminSkill.Junior
                ? $"{who} 登录着 {machine}，在跑他的巡检脚本"
                : $"{who} 登录着 {machine}，在随手翻",
            AdminActivity.Sweeping => $"{who} 起了疑心，正在把 {machine} 从头查一遍",
            _ => $"{who} 不在 {machine} 上。下次约在 {Countdown(_admin.TimeToNextPatrol)} 后",
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

    private static string SkillName(AdminSkill skill) => skill switch
    {
        AdminSkill.Junior => "实习运维",
        AdminSkill.Senior => "资深运维",
        _ => "运维",
    };

    private static string Countdown(TimeSpan left) =>
        left <= TimeSpan.Zero ? "随时" : $"{(int)left.TotalMinutes}:{left.Seconds:00}";

    public override void _Process(double delta)
    {
        // 桌面拿到尺寸的那一刻不一定有 Resized 信号，所以这里补一次；摆好就不再进来
        if (!_laidOut) LayoutOnce();
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
            // 教学关里若有 blackwall 目标机，把「神器」接入也一并自检
            string? bwIp = _blackwallByIp.Keys.FirstOrDefault();
            Control? PeekBlackwall() =>
                _blackwallWins.Values.FirstOrDefault(s => s.Window.Shown).Terminal;
            ok = await SelfTest.RunAsync(this, _terminals[0], bootDetail, OpenExtraTerminalCore,
                                         bwIp is null ? null : OpenBlackwallCore, bwIp,
                                         bwIp is null ? null : PeekBlackwall);
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

        // 截图要在主线程、而且画面得是新的。
        //
        // 不能等 frame_post_draw：屏幕锁了、窗口被挡住、或者跑在没有合成器的环境里时，
        // 引擎根本不画，那个信号就永远不来 —— 进程挂死在这一行，CI 只看得到超时。
        // 实测踩到过：_Process 照跑两万帧，frame_post_draw 一次没响，那台机器的
        // 会话是 LockedHint=yes。
        // 所以改成自己逼它画一帧：先等一个逻辑帧让界面状态落定，再 ForceDraw。
        // 注意锁屏时截出来的画面可能是半旧的（动态控件没重绘），要看真东西得先解锁。
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        RenderingServer.ForceDraw();
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
