using System.Net;
using System.Net.Sockets;

namespace GameHacker.Core.Channels;

/// <summary>
/// 一条串口通道的宿主端。ttyS0（玩家终端）和 ttyS1（隐藏控制通道）都用它。
/// </summary>
/// <remarks>
/// <para>
/// <b>方向是宿主 listen、QEMU 连进来</b>（<c>server=off,reconnect-ms=1000</c>），
/// 不能反过来。M0 阶段踩过这个坑：用 <c>server=on,wait=off</c> 时客户机 1.5 秒
/// 就启动完并打印完开机信息，而宿主还没连上，<b>这些输出被 QEMU 直接丢弃</b>。
/// 宿主先监听则一个字节都不会丢，而且 VM 重启后 QEMU 会自己接回来。
/// </para>
/// <para>
/// 全部通道一律走 127.0.0.1 TCP，不用 AF_UNIX —— Windows 版 QEMU 不保证支持，
/// 而概要书要求三端都能跑。
/// </para>
/// <para>
/// <b>线程模型</b>：接收在后台线程上跑，<see cref="DataReceived"/> 也在后台线程上触发。
/// Godot 侧订阅者必须用 <c>CallDeferred</c> 把数据转回主线程再喂给 godot-xterm，
/// 直接在后台线程碰 Node 会炸。
/// </para>
/// </remarks>
public sealed class SerialChannel : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NetworkStream? _stream;
    private long _bytesReceived;

    public SerialChannel(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>实际监听的端口（构造时传 0 则由系统分配）。</summary>
    public int Port { get; }

    public bool IsConnected => _stream is not null;

    /// <summary>累计收到的字节数。排查「通道连上了但没数据」时的关键判据。</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>收到客户机送来的裸字节。<b>在后台线程上触发。</b></summary>
    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>QEMU 连上或断开。断开后它会靠 reconnect-ms 自己回来。</summary>
    public event Action<bool>? ConnectionChanged;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token, cancellationToken);
        var buffer = new byte[8192];

        while (!linked.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(linked.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }

            client.NoDelay = true;
            using (client)
            {
                _stream = client.GetStream();
                ConnectionChanged?.Invoke(true);
                try
                {
                    while (!linked.IsCancellationRequested)
                    {
                        int read = await _stream.ReadAsync(buffer, linked.Token);
                        if (read == 0) break;
                        Interlocked.Add(ref _bytesReceived, read);
                        DataReceived?.Invoke(buffer.AsMemory(0, read).ToArray());
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException
                                             or OperationCanceledException)
                {
                    // 客户机关机或 QEMU 重连，等它自己回来
                }
                finally
                {
                    _stream = null;
                    ConnectionChanged?.Invoke(false);
                }
            }
        }
    }

    /// <summary>往客户机串口写字节。未连接时静默丢弃，与真实串口行为一致。</summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream is null) return;

        await _writeLock.WaitAsync(cancellationToken);
        try { await stream.WriteAsync(data, cancellationToken); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
