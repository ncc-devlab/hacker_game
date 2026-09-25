using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameHacker.Core.Channels;
using GameHacker.Core.Qemu;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 一台客户机在游戏侧的完整会话：QEMU 进程 + 两条串口通道 + 终端桥接。
/// </summary>
/// <remarks>
/// 这里只做"接线"，不含任何编排逻辑 —— 拉起进程、成帧、控制通道协议
/// 全在 <c>GameHacker.Core</c> 里，那份是三端 CI 直接测过的。
/// </remarks>
public sealed class VmSession : IAsyncDisposable, IDisposable
{
    private readonly QemuLauncher _launcher;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;

    /// <param name="extraTerminals">多开的终端节点，按 <see cref="QemuLauncher.ExtraConsoleTtys(VmSpec)"/> 的顺序。</param>
    public VmSession(VmSpec spec, Control terminal, Node bridgeParent, IReadOnlyList<Control>? extraTerminals = null)
    {
        Spec = spec;
        _launcher = new QemuLauncher(GamePaths.QemuPath);
        _launcher.Start(spec);

        Control = new ControlChannel(_launcher.Control!);

        Bridge = new TerminalBridge { Name = $"Bridge_{spec.Name}" };
        bridgeParent.AddChild(Bridge);
        Bridge.Attach(terminal, _launcher.Console!, Control);

        // 多开的终端开机时就接好：窗口还没打开，getty 已经在那头等着了
        var extras = new List<TerminalBridge>();
        for (int i = 0; i < _launcher.Extras.Count && i < (extraTerminals?.Count ?? 0); i++)
        {
            var extra = _launcher.Extras[i];
            var bridge = new TerminalBridge { Name = $"Bridge_{spec.Name}_{extra.Tty}" };
            bridgeParent.AddChild(bridge);
            bridge.Attach(extraTerminals![i], extra.Channel, Control, extra.Tty);
            extras.Add(bridge);
        }
        ExtraBridges = extras;

        // 各条通道的接收循环。监听口在 Start() 里就已经开好了，
        // 所以客户机开机的第一个字节也不会丢。
        var pumps = new List<Task>
        {
            _launcher.Console!.RunAsync(_cts.Token),
            _launcher.Control!.RunAsync(_cts.Token),
        };
        if (_launcher.Admin is not null) pumps.Add(_launcher.Admin.RunAsync(_cts.Token));
        foreach (var extra in _launcher.Extras) pumps.Add(extra.Channel.RunAsync(_cts.Token));
        _pump = Task.WhenAll(pumps);
    }

    public VmSpec Spec { get; }

    /// <summary>虚拟机没起来时用来解释原因（QEMU 的 stderr）。</summary>
    public string DescribeFailure()
    {
        var lines = Control.SeenLines;
        string seen = lines.Count == 0
            ? "控制通道上一行完整文本都没收到"
            : $"控制通道收到 {lines.Count} 行: " + string.Join(" | ", lines);
        return _launcher.DescribeFailure() + "\n       " + seen;
    }
    public ControlChannel Control { get; }

    /// <summary>管理员的登录终端（ttyS2）。这台机器没有管理员时为 <c>null</c>。</summary>
    public SerialChannel? AdminTty => _launcher.Admin;
    public TerminalBridge Bridge { get; }

    /// <summary>多开终端的桥接，和传进来的终端节点一一对应。</summary>
    public IReadOnlyList<TerminalBridge> ExtraBridges { get; }

    /// <summary>
    /// 等客户机 init 发出 ready 信标。发任何控制命令之前必须先等到它 ——
    /// QEMU 一启动就连上 chardev 了，但客户机要约 1.5 秒才起 ttyS1 的读取循环。
    /// </summary>
    public Task<System.Text.Json.Nodes.JsonObject> WaitReadyAsync(TimeSpan timeout) =>
        Control.WaitReadyAsync(timeout, _cts.Token);

    public Task<bool> PingAsync(string target) => Control.PingAsync(target, _cts.Token);

    /// <summary>
    /// 等玩家终端那边的 shell 真正起来。往终端里送字之前必须先等到它。
    /// </summary>
    /// <remarks>
    /// ready 信标只说明控制通道的代理起来了，比 ttyS0 上的 shell 早好几秒；
    /// 之间敲进去的字会被丢掉。等不到就按老样子继续，别把流程卡死。
    /// </remarks>
    public async Task WaitConsoleReadyAsync(TimeSpan timeout)
    {
        try { await Control.WaitEventAsync("console", timeout, _cts.Token); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            GD.PushWarning($"[level] {Spec.Name} 没等到控制台就绪信标");
        }
    }

    /// <summary>直接往 ttyS0 送字节，等同于玩家在终端里敲字。</summary>
    public Task SendToConsoleAsync(byte[] data) =>
        _launcher.Console!.SendAsync(data, _cts.Token);

    /// <summary>
    /// 同步清理，供 Godot 的关闭路径使用。
    /// </summary>
    /// <remarks>
    /// <c>_ExitTree</c> 是 <c>async void</c>，引擎不会等它，
    /// 用异步版本会在杀完 QEMU 之前就退出进程，留下吃满 CPU 的孤儿。
    /// </remarks>
    /// <summary>
    /// 请 QEMU 自己退出。多台机器时先把请求都发出去，再挨个等，省得串行等待。
    /// </summary>
    public Task<bool> RequestQuitAsync() => _launcher.TryQuitAsync(QemuLauncher.QuitGrace);

    public void Dispose()
    {
        _cts.Cancel();
        Control.DisposeAsync().AsTask().Wait(2000);
        _launcher.Dispose();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_pump is not null)
        {
            try { await _pump; } catch (OperationCanceledException) { }
        }
        await Control.DisposeAsync();
        await _launcher.DisposeAsync();
        _cts.Dispose();
    }
}
