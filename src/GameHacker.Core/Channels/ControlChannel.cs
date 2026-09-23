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

    /// <summary>补发窗口与排查缓冲的上限。</summary>
    private const int HistoryLimit = 64;

    private readonly SerialChannel _serial;
    private readonly StringBuilder _lineBuffer = new();

    // 事件按订阅广播，每个等待者一个独立队列。
    //
    // 这里不能用一个共享队列让大家去抢：那是破坏性单消费者语义，
    // 任意两个并发的等待者会互相吞掉对方的事件。M2 里实测到过 ——
    // 终端布局触发的 resize 等待者先起来，从共享队列里读到 ready，
    // 发现不是自己要的就丢掉，于是 WaitReadyAsync 永远等不到。
    private readonly List<Channel<JsonObject>> _subscribers = [];

    // 已经发生过的事件。新订阅者会先收到这些。
    //
    // 必须有：ready 信标只发一次，如果它先于任何等待者到达，
    // 纯广播模型会直接把它丢掉。
    private readonly Queue<JsonObject> _history = new();

    // 排查用：这条通道上出现过的原始文本行，包括解析不了的。
    private readonly List<string> _seenLines = [];

    public ControlChannel(SerialChannel serial)
    {
        _serial = serial;
        _serial.DataReceived += OnData;
    }

    /// <summary>这条通道上出现过的原始文本行，排查用。</summary>
    public IReadOnlyList<string> SeenLines
    {
        get { lock (_seenLines) return _seenLines.ToArray(); }
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

            lock (_seenLines)
            {
                if (_seenLines.Count < HistoryLimit) _seenLines.Add(line);
            }

            // 串口上混着开机噪声和命令回显，解析不出 JSON 的行直接忽略。
            // 事件前面可能粘着一段回显（客户机切 raw 之前收到的命令会被 tty 回显，
            // 实测见过 "resize {"ev":"ready",...}"），所以从第一个 { 开始解析
            int brace = line.IndexOf('{');
            if (brace < 0) continue;
            try
            {
                if (JsonNode.Parse(line[brace..]) is JsonObject ev) Publish(ev);
            }
            catch (JsonException) { }
        }
    }

    private void Publish(JsonObject ev)
    {
        lock (_subscribers)
        {
            _history.Enqueue(ev);
            while (_history.Count > HistoryLimit) _history.Dequeue();

            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(ev);
        }
    }

    private Channel<JsonObject> Subscribe()
    {
        var inbox = Channel.CreateUnbounded<JsonObject>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        lock (_subscribers)
        {
            // 先补发历史，再挂进订阅列表 —— 两步都在锁里，
            // 保证既不漏事件也不会重复收到同一条。
            foreach (var ev in _history) inbox.Writer.TryWrite(ev);
            _subscribers.Add(inbox);
        }
        return inbox;
    }

    private void Unsubscribe(Channel<JsonObject> inbox)
    {
        lock (_subscribers) _subscribers.Remove(inbox);
        inbox.Writer.TryComplete();
    }

    /// <summary>等客户机 init 发出 ready 信标。发任何命令之前必须先等到它。</summary>
    public Task<JsonObject> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitEventAsync("ready", timeout, cancellationToken);

    /// <summary>等一条指定类型的事件。订阅之前已经发生过的也算。</summary>
    public async Task<JsonObject> WaitEventAsync(
        string eventName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var inbox = Subscribe();
        try
        {
            await foreach (var ev in inbox.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                if (ev["ev"]?.GetValue<string>() == eventName)
                    return ev;

            throw new TimeoutException($"等待事件 {eventName} 时通道已关闭");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 是我们自己的超时，不是调用方取消。换成 TimeoutException，
            // 否则上层只看到一句 "The operation was canceled"，分不清是哪种。
            throw new TimeoutException($"等待事件 {eventName} 超时（{timeout.TotalSeconds:0}s）");
        }
        finally
        {
            Unsubscribe(inbox);
        }
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
            await _serial.SendAsync(payload, cts.Token).ConfigureAwait(false);
            var delay = Task.Delay(ResendInterval, cts.Token);
            if (await Task.WhenAny(waiter, delay).ConfigureAwait(false) == waiter) break;
        }
        return await waiter.ConfigureAwait(false);
    }

    /// <summary>把终端尺寸转发给客户机。裸串口没有 TIOCSWINSZ，只能走这里。</summary>
    public Task<JsonObject> ResizeAsync(int rows, int columns, CancellationToken cancellationToken = default) =>
        RequestAsync($"resize {rows} {columns}", "resize", TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>让客户机 ping 一个地址，返回判定结果。</summary>
    public async Task<bool> PingAsync(string target, CancellationToken cancellationToken = default)
    {
        var ev = await RequestAsync($"ping {target}", "ping",
                                    TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return ev["result"]?.GetValue<string>() == "ok";
    }

    public ValueTask DisposeAsync()
    {
        _serial.DataReceived -= OnData;
        lock (_subscribers)
        {
            foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
