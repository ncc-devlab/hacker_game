using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace GameHacker.Core.Net;

/// <summary>
/// 用户态二层学习交换机。所有客户机的以太网帧都从这里过。
/// </summary>
/// <remarks>
/// <para>
/// 为什么自己写而不用现成的：VDE2 在 Windows 上没有可用实现（三端发行一票否决），
/// Open vSwitch 要 root/内核模块，Linux bridge+TAP 要提权，
/// <c>-netdev socket,mcast=</c> 是集线器而非交换机、且没有检测切入点。
/// 更根本的是，交换机是流量检测与可视化的<b>唯一入口</b>，
/// 换成第三方就等于把这个天然免费的能力变成一个额外的旁路抓包难题。
/// </para>
/// <para>
/// 全部持久状态就是一张 <see cref="_macTable"/>。刻意不做的事：
/// STP（星型拓扑由我们自己构造，物理上不可能成环）、VLAN（等关卡真要用再加）、
/// ARP/IP 逻辑（二层不关心，客户机自己会 ARP）、
/// 分片重组（QEMU 给的就是完整帧）、校验和（客户机内核的事）。
/// </para>
/// <para>
/// 拓扑方向：交换机 listen，QEMU 用 <c>server=off,reconnect-ms=1000</c> 连进来。
/// 这样启动顺序无关，且 VM 重启后会自动接回。
/// </para>
/// </remarks>
public sealed class VirtualSwitch : IAsyncDisposable
{
    private static readonly TimeSpan MacAgeTimeout = TimeSpan.FromMinutes(5);

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<int, SwitchPort> _ports = new();
    private readonly ConcurrentDictionary<MacAddressKey, MacEntry> _macTable = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly IFrameObserver? _observer;
    private int _nextPortId;
    private long _framesForwarded;

    public VirtualSwitch(int port, IFrameObserver? observer = null)
    {
        _observer = observer;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>实际监听的端口（构造时传 0 则由系统分配）。</summary>
    public int Port { get; }

    public long FramesForwarded => Interlocked.Read(ref _framesForwarded);

    public IReadOnlyDictionary<MacAddressKey, int> MacTable =>
        _macTable.ToDictionary(kv => kv.Key, kv => kv.Value.PortId);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token, cancellationToken);

        while (!linked.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(linked.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }

            client.NoDelay = true;
            int id = Interlocked.Increment(ref _nextPortId) - 1;
            var switchPort = new SwitchPort(id, client);
            _ports[id] = switchPort;

            _ = Task.Run(() => PumpPortAsync(switchPort, linked.Token), linked.Token);
        }
    }

    private async Task PumpPortAsync(SwitchPort port, CancellationToken cancellationToken)
    {
        // 收帧是热路径：用一个可增长的字节缓冲 + 已填充长度，
        // 不要每帧 ToArray() —— 那是 O(n²) 的分配。
        var buffer = new byte[128 * 1024];
        int filled = 0;

        try
        {
            var stream = port.Client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (filled == buffer.Length)
                    Array.Resize(ref buffer, buffer.Length * 2);

                int read = await stream.ReadAsync(
                    buffer.AsMemory(filled, buffer.Length - filled), cancellationToken);
                if (read == 0) break;
                filled += read;

                // 一次 read 可能拿到半个帧，也可能拿到好几个（TCP 是字节流）
                int offset = 0;
                while (true)
                {
                    var window = new ReadOnlyMemory<byte>(buffer, offset, filled - offset);
                    var frame = StreamFrameCodec.TryReadFrame(window, out int consumed);
                    if (frame is null) break;
                    Forward(port.Id, frame.Value.Span);
                    offset += consumed;
                }

                // 把没拆完的尾巴挪到开头
                if (offset > 0)
                {
                    Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset);
                    filled -= offset;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException
                                     or OperationCanceledException or InvalidDataException)
        {
            // 客户机关机或 QEMU 重连都会走到这里，不是错误
        }
        finally
        {
            _ports.TryRemove(port.Id, out _);
            foreach (var (mac, entry) in _macTable)
                if (entry.PortId == port.Id)
                    _macTable.TryRemove(mac, out _);
            port.Dispose();
        }
    }

    private void Forward(int sourcePortId, ReadOnlySpan<byte> frame)
    {
        if (!EthernetFrame.IsValid(frame)) return;

        var eth = new EthernetFrame(frame);
        Interlocked.Increment(ref _framesForwarded);
        _observer?.OnFrame(sourcePortId, frame);

        var now = DateTime.UtcNow;
        _macTable[eth.Source] = new MacEntry(sourcePortId, now);

        foreach (var (mac, entry) in _macTable)
            if (now - entry.LastSeen > MacAgeTimeout)
                _macTable.TryRemove(mac, out _);

        var header = new byte[StreamFrameCodec.HeaderSize];
        StreamFrameCodec.WriteHeader(header, frame.Length);
        var payload = frame.ToArray();

        // 目的地址已知且是单播 -> 只发那一个口；未知 / 广播 / 组播 -> 泛洪。
        // 注意只查一次表：查两次的话表项可能在两次之间被老化掉，
        // 第二次拿到 default 会把帧发去 0 号端口。
        var destination = eth.Destination;
        if (!destination.IsGroupAddress
            && _macTable.TryGetValue(destination, out var target)
            && target.PortId != sourcePortId
            && _ports.TryGetValue(target.PortId, out var only))
        {
            only.TrySend(header, payload);
            return;
        }

        foreach (var (id, p) in _ports)
            if (id != sourcePortId)
                p.TrySend(header, payload);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        foreach (var (_, p) in _ports) p.Dispose();
        _ports.Clear();
        _cts.Dispose();
    }

    private readonly record struct MacEntry(int PortId, DateTime LastSeen);

    private sealed class SwitchPort(int id, TcpClient client) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public int Id { get; } = id;
        public TcpClient Client { get; } = client;

        public void TrySend(byte[] header, byte[] payload)
        {
            _ = Task.Run(async () =>
            {
                await _writeLock.WaitAsync();
                try
                {
                    var stream = Client.GetStream();
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(payload);
                }
                catch (Exception ex) when (ex is IOException or SocketException
                                             or ObjectDisposedException)
                {
                    // 对端已断开，等它自己重连
                }
                finally { _writeLock.Release(); }
            });
        }

        public void Dispose()
        {
            Client.Dispose();
            _writeLock.Dispose();
        }
    }
}

/// <summary>
/// 交换机的旁观者。pcap 落盘、任务判定、流量可视化都挂在这里。
/// </summary>
public interface IFrameObserver
{
    void OnFrame(int sourcePortId, ReadOnlySpan<byte> frame);
}
