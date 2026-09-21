using System;
using System.Linq;
using System.Threading.Tasks;
using GameHacker.Core.Net;
using GameHacker.Core.Qemu;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// M2 验证壳：在 Godot 里把技术脊椎完整跑一遍。
/// </summary>
/// <remarks>
/// 对应概要书 MVP 的一到五条：两台真实虚拟机用 TCG 启动、各自一个可用终端、
/// 经自研虚拟交换机组网、配好 IP 后互相 ping 通、隐藏控制通道检测到并通知游戏。
/// 第六条（vim/tmux 渲染）由玩家在终端里手动验证 —— 自动化版本在 M0-c。
/// </remarks>
public partial class Main : Control
{
    private const string AlphaIp = "10.0.0.1";
    private const string BetaIp = "10.0.0.2";

    private Label _status = null!;
    private Control _mainTerm = null!;
    private Control _targetTerm = null!;
    private PacketPanel _packets = null!;

    private readonly PacketLog _packetLog = new();
    private VirtualSwitch? _switch;
    private VmSession? _alpha;
    private VmSession? _beta;

    public override void _Ready()
    {
        _status = GetNode<Label>("%Status");
        _mainTerm = GetNode<Control>("%MainTerminal");
        _targetTerm = GetNode<Control>("%TargetTerminal");
        _packets = GetNode<PacketPanel>("%Packets");
        _packets.Attach(_packetLog);

        if (!GamePaths.ImagesReady)
        {
            SetStatus($"缺少客户机镜像。先跑 m0/scripts/00-fetch-images.sh 与 01-build-guest-initramfs.sh\n"
                      + $"找的位置: {GamePaths.ImagesDir}", Colors.Orange);
            return;
        }

        _ = BootAsync();
    }

    private async Task BootAsync()
    {
        try
        {
            // 先回收上次残留的 QEMU。TCG 每台占满一核，孤儿堆积会让
            // 本次启动越来越慢直至超时 —— 这个症状在 M2 里实测到过。
            int reaped = VmProcessRegistry.ReapOrphans(VmProcessRegistry.DefaultDirectory);
            if (reaped > 0) GD.Print($"[m2] 回收了 {reaped} 个上次残留的虚拟机");

            SetStatus("启动虚拟交换机…");
            // 交换机是概要书里唯一的流量观察点，抓包面板和关卡判定都挂在这个旁观者上
            _switch = new VirtualSwitch(port: 0, _packetLog);
            _ = _switch.RunAsync();

            SetStatus($"交换机监听 127.0.0.1:{_switch.Port}，正在拉起两台虚拟机…");

            // 主角机带可写磁盘（完整 Alpine，有 vim/tmux）；目标机纯内存，对应"遍地的老旧小机器"
            // 人设决定客户机对玩家宣称的主板 / BIOS / CPU / 内核版本。
            // 主机名也跟着人设走 —— alpha/beta 这种名字一看就是测试用的。
            _alpha = new VmSession(NewSpec("web01", AlphaIp, "52:54:00:00:00:01", _switch.Port,
                                           HardwarePersona.Workstation,
                                           disk: GamePaths.AlpineDisk, memory: 512),
                                   _mainTerm, this);
            _beta = new VmSession(NewSpec("backup", BetaIp, "52:54:00:00:00:02", _switch.Port,
                                          HardwarePersona.LegacyServer),
                                  _targetTerm, this);

            // 排查间歇性启动问题时用 GAMEHACKER_BOOT_TIMEOUT 缩短等待
            double bootSeconds = double.TryParse(
                System.Environment.GetEnvironmentVariable("GAMEHACKER_BOOT_TIMEOUT"),
                out double t) ? t : 90;
            var boot = TimeSpan.FromSeconds(bootSeconds);
            await Task.WhenAll(_alpha.WaitReadyAsync(boot), _beta.WaitReadyAsync(boot));
            SetStatus("两台客户机就绪，正在验证二层连通性…");

            bool ok = await _beta.PingAsync(AlphaIp) && await _alpha.PingAsync(BetaIp);

            SetStatus(ok
                ? $"任务完成：{BetaIp} 与 {AlphaIp} 互通，交换机已转发 {_switch.FramesForwarded} 帧"
                : "ping 未通 —— 见 m0/run 下的日志", ok ? Colors.LightGreen : Colors.IndianRed);

            await MaybeSelfTestAsync(ok ? "互通" : "不通");
            await MaybeScreenshotAsync();
        }
        catch (Exception ex)
        {
            // QEMU 起不来时只在 stderr 说一句话，不带上它的话
            // 这里只能看到「等 ready 信标超时」，完全无从下手
            string detail = string.Join("\n", new[]
            {
                _alpha is null ? null : $"alpha: {_alpha.DescribeFailure()}",
                _beta is null ? null : $"beta:  {_beta.DescribeFailure()}",
            }.Where(x => x is not null));

            SetStatus($"启动失败: {ex.Message}\n{detail}", Colors.IndianRed);
            GD.PushError($"{ex}\n{detail}");
        }
    }

    private static VmSpec NewSpec(string name, string ip, string mac, int switchPort,
                                  HardwarePersona persona,
                                  string? disk = null, int memory = 256) => new()
    {
        Name = name,
        Persona = persona,
        KernelPath = GamePaths.Kernel,
        InitrdPath = GamePaths.Initrd,
        DiskPath = disk,
        IpAddress = $"{ip}/24",
        MacAddress = mac,
        MemoryMegabytes = memory,
        // 验证壳里不写回镜像，保持基础镜像干净。
        // 真正的存档走 qcow2 backing file + user:// 下的 overlay。
        Ephemeral = disk is not null,
        // 交换机用 bind 0 让系统分配端口，避免多开或并发测试时撞端口
        SwitchPort = switchPort,
    };

    /// <summary>
    /// 设了 GAMEHACKER_SELFTEST 就跑一遍无人值守自检，把报告写到那个路径。
    /// </summary>
    /// <remarks>
    /// 给 Windows / macOS 验证用。Linux 上 M0/M1 的测试走裸串口，
    /// 绕开了 Godot 输入链和 GDExtension —— 恰恰是换平台最容易坏的两处。
    /// </remarks>
    private async Task MaybeSelfTestAsync(string bootDetail)
    {
        string? path = System.Environment.GetEnvironmentVariable("GAMEHACKER_SELFTEST");
        if (string.IsNullOrWhiteSpace(path)) return;

        bool ok;
        try
        {
            ok = await SelfTest.RunAsync(this, _mainTerm, bootDetail);
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

        // 往两个终端里敲点东西，好让截图上有内容
        // 挑的命令要同时展示三样东西：终端渲染、伪装生效、以及抓包面板有货
        await TypeAsync(_alpha!, "uname -a; cat /sys/class/dmi/id/product_name\n");
        await TypeAsync(_beta!, "ping -c2 10.0.0.1\n");
        await Task.Delay(4000);

        // 截图必须在主线程、且要等当前帧画完
        await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        Error err = image.SavePng(path);
        GD.Print(err == Error.Ok ? $"[m2] 截图已存 {path}" : $"[m2] 截图失败: {err}");

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
        // 可能从后台线程调进来，统一走主线程
        Callable.From(() =>
        {
            _status.Text = text;
            _status.Modulate = color ?? Colors.White;
        }).CallDeferred();
        GD.Print($"[m2] {text}");
    }

    /// <summary>
    /// 关闭时同步清理。
    /// </summary>
    /// <remarks>
    /// 这里<b>不能</b>写成 <c>async void</c> 再 await —— 引擎不会等它，
    /// 进程会在 QEMU 被杀之前就退出，留下每台占满一核的孤儿虚拟机。
    /// </remarks>
    public override void _ExitTree()
    {
        // 三端验证时把这一轮的流量留成证据，用 Wireshark 就能对照
        string? pcap = System.Environment.GetEnvironmentVariable("GAMEHACKER_PCAP");
        if (!string.IsNullOrWhiteSpace(pcap))
        {
            try { _packetLog.WritePcap(pcap); GD.Print($"[m2] 抓包已存 {pcap}"); }
            catch (Exception ex) { GD.PushError($"[m2] 抓包写不出去: {ex.Message}"); }
        }

        _alpha?.Dispose();
        _beta?.Dispose();
        _switch?.DisposeAsync().AsTask().Wait(2000);
    }
}
