using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameHacker.Core.Qemu;

/// <summary>
/// QMP（QEMU Machine Protocol）客户端 —— 虚拟机的控制面。
/// </summary>
/// <remarks>
/// <para>
/// 这是「不魔改 QEMU」这条硬约束能成立的关键：QEMU 已经把整台机器的控制
/// 通过一个进程外的 JSON-over-socket 接口暴露出来了。开关机、reset、
/// 查状态、热插拔、screendump、以及对游戏最有价值的 <c>savevm</c>/<c>loadvm</c>
/// 快照，全都在这里，不需要碰一行 QEMU 源码。
/// </para>
/// <para>
/// 协议形态：连上后服务端先发 <c>{"QMP": {...}}</c> 问候，客户端必须先
/// <c>qmp_capabilities</c> 握手才能发别的命令。之后是请求/应答（靠 id 配对），
/// 中间随时可能插入异步事件（<c>{"event": ...}</c>）。
/// </para>
/// </remarks>
public sealed class QmpClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private StreamWriter? _writer;
    private Task? _readLoop;
    private int _nextId;

    /// <summary>QEMU 主动推送的异步事件，例如 SHUTDOWN、RESET、STOP。</summary>
    public event Action<string, JsonNode?>? EventReceived;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        _client.NoDelay = true;

        var stream = _client.GetStream();
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new StreamReader(stream, Encoding.UTF8);

        // 问候语必须先读掉，否则它会被当成某条命令的应答
        string? greeting = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (greeting is null || JsonNode.Parse(greeting)?["QMP"] is null)
            throw new InvalidOperationException($"不是合法的 QMP 问候语: {greeting}");

        _readLoop = Task.Run(() => ReadLoopAsync(reader, _cts.Token), _cts.Token);

        // 握手之前 QEMU 只接受 qmp_capabilities 这一条命令
        await ExecuteAsync("qmp_capabilities", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonNode?> ExecuteAsync(
        string command,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (_writer is null)
            throw new InvalidOperationException("尚未连接");

        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonObject { ["execute"] = command, ["id"] = id };
        if (arguments is not null) request["arguments"] = arguments;

        await _writer.WriteLineAsync(request.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);

        await using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)).ConfigureAwait(false))
            return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>虚拟机当前是否在运行（对应 QMP 的 query-status）。</summary>
    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("query-status", cancellationToken: cancellationToken).ConfigureAwait(false);
        return result?["running"]?.GetValue<bool>() ?? false;
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode? message;
                try { message = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                if (message is null) continue;

                if (message["event"] is { } evt)
                {
                    EventReceived?.Invoke(evt.GetValue<string>(), message["data"]);
                    continue;
                }

                if (message["id"]?.GetValue<int>() is not { } id ||
                    !_pending.TryRemove(id, out var tcs))
                    continue;

                if (message["error"] is { } error)
                    tcs.TrySetException(new QmpException(
                        error["class"]?.GetValue<string>() ?? "GenericError",
                        error["desc"]?.GetValue<string>() ?? line));
                else
                    tcs.TrySetResult(message["return"]);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException
                                     or OperationCanceledException or ObjectDisposedException)
        {
            // QEMU 退出时的正常收尾
        }
        finally
        {
            var reason = new ObjectDisposedException(nameof(QmpClient), "QMP 连接已关闭");
            foreach (var (_, tcs) in _pending) tcs.TrySetException(reason);
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _writer?.Dispose();
        _client.Dispose();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}

public sealed class QmpException(string errorClass, string description)
    : Exception($"{errorClass}: {description}")
{
    public string ErrorClass { get; } = errorClass;
}
