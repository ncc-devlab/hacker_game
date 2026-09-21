using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace GameHacker.Core.Channels;

/// <summary>
/// 隐藏控制通道（ttyS1）。任务判定与终端尺寸转发都走它。
/// </summary>
/// <remarks>
/// <para>
/// 协议刻意做得极简：宿主发一行文本命令，客户机回一行 JSON 事件。
/// 客户机侧的实现是 <c>m0/guest/common.sh</c> 里几十行 busybox shell，
/// 不需要在客户机里装任何 agent 运行时。
/// </para>
/// <para>
/// <b>两个必须遵守的时序约束</b>（M0 实测得出）：
/// </para>
/// <list type="number">
///   <item>
///     发命令前必须先等 <c>ready</c> 信标。QEMU 一启动就把 chardev 连上了（约 0.1 秒），
///     但客户机要到约 1.5 秒才起 ttyS1 的读取循环，在那之前发的命令会被直接丢掉。
///   </item>
///   <item>
///     命令要按固定间隔重发。裸串口没有应答层，重发是这里最省事也最可靠的
///     可靠化手段，所以所有命令都设计成幂等的。
///   </item>
/// </list>
/// <para>
/// 终端 resize 只能走这条通道：裸串口不是 PTY，没有 <c>TIOCSWINSZ</c> 带外信令，
/// 客户机侧收到 <c>resize</c> 后对 ttyS0 执行 <c>stty</c>。
/// </para>
/// </remarks>
public sealed class ControlChannel : IAsyncDisposable
{
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(2);

    private readonly SerialChannel _serial;
    private readonly Channel<JsonObject> _events =
        Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = false });
    private readonly StringBuilder _lineBuffer = new();

    public ControlChannel(SerialChannel serial)
    {
        _serial = serial;
        _serial.DataReceived += OnData;
    }

    private void OnData(ReadOnlyMemory<byte> data)
    {
        _lineBuffer.Append(Encoding.UTF8.GetString(data.Span));

        while (true)
        {
            string text = _lineBuffer.ToString();
            int newline = text.IndexOf('\n');
            if (newline < 0) break;

            string line = text[..newline].Trim();
            _lineBuffer.Clear();
            _lineBuffer.Append(text[(newline + 1)..]);

            if (line.Length == 0) continue;
            // 串口上混着开机噪声，解析不出 JSON 的行直接忽略
            try
            {
                if (JsonNode.Parse(line) is JsonObject ev)
                    _events.Writer.TryWrite(ev);
            }
            catch (JsonException) { }
        }
    }

    /// <summary>等客户机 init 发出 ready 信标。发任何命令之前必须先等到它。</summary>
    public Task<JsonObject> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitEventAsync("ready", timeout, cancellationToken);

    /// <summary>等一条指定类型的事件。</summary>
    public async Task<JsonObject> WaitEventAsync(
        string eventName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        await foreach (var ev in _events.Reader.ReadAllAsync(cts.Token))
            if (ev["ev"]?.GetValue<string>() == eventName)
                return ev;

        throw new TimeoutException($"等待事件 {eventName} 超时");
    }

    /// <summary>
    /// 发一条命令并等待指定类型的应答，期间按 <see cref="ResendInterval"/> 重发。
    /// </summary>
    public async Task<JsonObject> RequestAsync(
        string command, string expectedEvent, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var payload = Encoding.UTF8.GetBytes(command + "\n");
        var waiter = WaitEventAsync(expectedEvent, timeout, cts.Token);

        while (!waiter.IsCompleted)
        {
            await _serial.SendAsync(payload, cts.Token);
            var delay = Task.Delay(ResendInterval, cts.Token);
            if (await Task.WhenAny(waiter, delay) == waiter) break;
        }
        return await waiter;
    }

    /// <summary>把终端尺寸转发给客户机。裸串口没有 TIOCSWINSZ，只能走这里。</summary>
    public Task<JsonObject> ResizeAsync(int rows, int columns, CancellationToken cancellationToken = default) =>
        RequestAsync($"resize {rows} {columns}", "resize", TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>让客户机 ping 一个地址，返回判定结果。</summary>
    public async Task<bool> PingAsync(string target, CancellationToken cancellationToken = default)
    {
        var ev = await RequestAsync($"ping {target}", "ping",
                                    TimeSpan.FromSeconds(30), cancellationToken);
        return ev["result"]?.GetValue<string>() == "ok";
    }

    public ValueTask DisposeAsync()
    {
        _serial.DataReceived -= OnData;
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
