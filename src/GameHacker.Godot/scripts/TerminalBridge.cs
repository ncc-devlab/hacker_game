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
    private Vector2I _lastSize = Vector2I.Zero;

    /// <summary>终端尺寸变化时会经控制通道下发 resize，这里报告结果。</summary>
    public event Action<int, int>? Resized;

    public void Attach(Control terminal, SerialChannel serial, ControlChannel? control = null)
    {
        _terminal = terminal;
        _serial = serial;
        _control = control;

        _serial.DataReceived += OnSerialData;
        _terminal.Connect("data_sent", Callable.From<byte[]>(OnTerminalData));
        _terminal.Connect("size_changed", Callable.From<Vector2I>(OnTerminalResized));
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
    private void OnTerminalData(byte[] data) => _ = _serial.SendAsync(data);

    private void OnTerminalResized(Vector2I size)
    {
        // headless 下没有布局，Terminal 会报出 rows=0 这样的尺寸。
        // 把它下发给客户机会让 stty 设出一个 0 行的终端，
        // 而且那条 resize 的等待者会抢在 ready 之前占住控制通道。
        if (size.X <= 0 || size.Y <= 0) return;
        if (size == _lastSize || _control is null) return;
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
            await _control!.ResizeAsync(rows, cols);
            Callable.From(() => Resized?.Invoke(rows, cols)).CallDeferred();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"resize 下发失败 ({rows}x{cols}): {ex.Message}");
        }
    }

    public override void _ExitTree()
    {
        if (_serial is not null) _serial.DataReceived -= OnSerialData;
    }
}
