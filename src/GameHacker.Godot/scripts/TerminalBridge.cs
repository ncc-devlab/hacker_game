using System;
using System.Collections.Concurrent;
using System.IO;
using GameHacker.Core.Channels;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 把一条串口通道接到一个 godot-xterm <c>Terminal</c> 节点上。
/// </summary>
/// <remarks>
/// <para><b>线程模型是这里唯一容易出事的地方。</b>
/// <see cref="SerialChannel.DataReceived"/> 在后台线程触发，而 Godot 的 Node
/// 只能在主线程碰。所以后台线程只往队列里塞，真正的 <c>write()</c> 在
/// <see cref="_Process"/> 里做。</para>
/// <para>用队列而不是 <c>CallDeferred</c> 还有一个好处：终端输出是突发的，
/// 一帧内攒到的若干段可以合并成一次 <c>write()</c>，省掉大量跨语言调用。</para>
/// <para><b>反向的 <c>data_sent</c> 不只是玩家键盘输入。</b> libtsm 对
/// DSR（<c>ESC[6n</c>）、设备属性查询等的应答也从这个信号出来 ——
/// 客户机的 shell 提示符依赖这些应答，不转发回去 shell 会错乱。</para>
/// </remarks>
public sealed partial class TerminalBridge : Node
{
    private readonly ConcurrentQueue<byte[]> _inbound = new();

    private Control _terminal = null!;
    private SerialChannel _serial = null!;
    private ControlChannel? _control;
    private string _tty = "ttyS0";
    private Vector2I _lastSize = Vector2I.Zero;

    /// <summary>客户机 ready 之前攒着的最新尺寸，ready 后补发一次。只在主线程读写。</summary>
    private Vector2I _pendingSize = Vector2I.Zero;
    private bool _guestReady;

    /// <summary>终端尺寸变化时会经控制通道下发 resize，这里报告结果。</summary>
    public event Action<int, int>? Resized;

    /// <summary>玩家在这个终端里按下了回车。</summary>
    public event Action? PlayerSubmitted;

    /// <summary>这个终端在客户机里是哪个设备。主终端是 ttyS0，多开的是 ttyS2 / ttyS3。</summary>
    public string Tty => _tty;

    public void Attach(Control terminal, SerialChannel serial, ControlChannel? control = null, string tty = "ttyS0")
    {
        _terminal = terminal;
        _serial = serial;
        _control = control;
        _tty = tty;

        _serial.DataReceived += OnSerialData;
        _terminal.Connect("data_sent", Callable.From<byte[]>(OnTerminalData));
        _terminal.Connect("size_changed", Callable.From<Vector2I>(OnTerminalResized));

        if (_control is not null) _ = FlushResizeWhenReadyAsync(_control);
    }

    /// <summary>
    /// 客户机 ready 之后把攒着的尺寸发出去。
    /// </summary>
    /// <remarks>
    /// ready 之前 ttyS1 还没切成 raw -echo，这时发的 resize 会被客户机的 tty 原样回显，
    /// 回显和 ready 信标挤在同一行，ready 就解析不出来了 —— 跳板关三台机器并排、
    /// 布局期间终端尺寸多变了几次，实测三台里两台卡在「等 ready 超时」。
    /// </remarks>
    private async System.Threading.Tasks.Task FlushResizeWhenReadyAsync(ControlChannel control)
    {
        try { await control.WaitReadyAsync(TimeSpan.FromMinutes(10)); }
        catch (Exception) { return; }   // 起不来的话启动流程自己会报错
        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;
            _guestReady = true;
            if (_pendingSize != Vector2I.Zero) OnTerminalResized(_pendingSize);
        }).CallDeferred();
    }

    /// <summary>后台线程：只入队，不碰任何 Node。</summary>
    private void OnSerialData(ReadOnlyMemory<byte> data) => _inbound.Enqueue(data.ToArray());

    public override void _Process(double delta)
    {
        if (_inbound.IsEmpty) return;

        // 把这一帧攒到的所有段合并成一次 write()
        using var merged = new MemoryStream();
        while (_inbound.TryDequeue(out var chunk))
            merged.Write(chunk, 0, chunk.Length);

        _terminal.Call("write", merged.ToArray());
    }

    /// <summary>主线程：玩家击键，以及 libtsm 对 DSR/DA 的应答。</summary>
    private void OnTerminalData(byte[] data)
    {
        _ = _serial.SendAsync(data);
        // 玩家按下回车 = 他刚让这台机器做了点什么。
        // 任务判定拿这个当「该去看一眼客户机状态了」的信号 ——
        // 只是触发时机，判定本身还是看状态，不看他敲了什么（也确实看不到：
        // 这里是一串按键字节，连行内容都没拼出来）
        if (System.Array.IndexOf(data, (byte)'\r') >= 0 || System.Array.IndexOf(data, (byte)'\n') >= 0)
            PlayerSubmitted?.Invoke();
    }

    private void OnTerminalResized(Vector2I size)
    {
        // headless 下没有布局，Terminal 会报出 rows=0 这样的尺寸。
        // 把它下发给客户机会让 stty 设出一个 0 行的终端，
        if (size.X <= 0 || size.Y <= 0) return;
        if (size == _lastSize || _control is null) return;
        if (!_guestReady) { _pendingSize = size; return; }
        _lastSize = size;

        // 裸串口不是 PTY，没有 TIOCSWINSZ 带外信令，
        // 尺寸只能经 ttyS1 控制通道告诉客户机，由它对 ttyS0 执行 stty。
        int rows = size.Y, cols = size.X;
        _ = SendResizeAsync(rows, cols);
    }

    private async System.Threading.Tasks.Task SendResizeAsync(int rows, int cols)
    {
        try
        {
            await _control!.ResizeAsync(_tty, rows, cols);
            Callable.From(() => Resized?.Invoke(rows, cols)).CallDeferred();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"resize 下发失败 ({rows}x{cols}): {ex.Message}");
        }
    }

    /// <summary>
    /// 窗口关了：清屏，并让客户机挂断这个终端上的会话。下次打开是一个全新的 shell。
    /// </summary>
    public void HangUp()
    {
        ResetScreen();
        if (_control is not null && _guestReady) _ = HangUpAsync(_control);
    }

    /// <summary>把终端整个复位（RIS），屏幕和回滚都清掉。只动宿主这头，客户机不知道。</summary>
    public void ResetScreen()
    {
        while (_inbound.TryDequeue(out _)) { }
        _terminal.Call("write", "\u001bc");
    }

    /// <summary>替玩家敲一下回车，让 shell 重新打一行提示符。不算玩家提交了命令。</summary>
    public void Poke() => _ = _serial.SendAsync("\r"u8.ToArray());

    private async System.Threading.Tasks.Task HangUpAsync(ControlChannel control)
    {
        try { await control.HangupAsync(_tty); }
        catch (Exception ex) { GD.PushWarning($"挂断 {_tty} 失败: {ex.Message}"); }
    }

    /// <summary>
    /// 把尺寸再告诉客户机一遍。挂断之后 getty 重开的是一个新会话，
    /// 串口上原来 stty 设的行列数已经跟着上一个会话没了。
    /// </summary>
    public void ResendSize()
    {
        var size = _lastSize;
        _lastSize = Vector2I.Zero;
        if (size != Vector2I.Zero) OnTerminalResized(size);
    }

    public override void _ExitTree()
    {
        if (_serial is not null) _serial.DataReceived -= OnSerialData;
    }
}
