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
/// STP（星型拓扑由我们自己构造，物理上不可能成环）、
/// ARP/IP 逻辑（二层不关心，客户机自己会 ARP）、
/// 分片重组（QEMU 给的就是完整帧）、校验和（客户机内核的事）。
/// </para>
/// <para>
/// 拓扑方向：交换机 listen，QEMU 用 <c>server=off,reconnect-ms=1000</c> 连进来。
/// 这样启动顺序无关，且 VM 重启后会自动接回。
/// </para>
/// <para>
/// <b>VLAN：每个 VLAN 一个监听口，全部是 access 口。</b> 虚拟机网卡连到哪个口，
/// 就属于哪个 VLAN —— 不需要握手、不看 MAC，身份由连接本身决定，客户机改不了。
/// 帧只在同一 VLAN 的端口之间转发，MAC 表也按 (VLAN, MAC) 分开学。
/// 客户机自己打了 802.1Q 标签的帧一律丢弃：access 口上不认标签，
/// 否则玩家手工造一个带标签的帧就能跳进别的 VLAN。
/// </para>
/// </remarks>
public sealed class VirtualSwitch : IAsyncDisposable
{
    private static readonly TimeSpan MacAgeTimeout = TimeSpan.FromMinutes(5);

    /// <summary>只有一个网段时用的 VLAN 号。</summary>
    public const int DefaultVlan = 1;

    private readonly Dictionary<int, TcpListener> _listeners = new();
    private readonly ConcurrentDictionary<int, SwitchPort> _ports = new();
    private readonly ConcurrentDictionary<(int Vlan, MacAddressKey Mac), MacEntry> _macTable = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly IFrameObserver? _observer;
    private int _nextPortId;
    private long _framesForwarded;
    private long _taggedDropped;

    /// <summary>只有一个 VLAN（<see cref="DefaultVlan"/>）的交换机。</summary>
    /// <param name="port">监听端口，0 由系统分配。</param>
    public VirtualSwitch(int port, IFrameObserver? observer = null)
        : this([DefaultVlan], observer, port) { }

    /// <summary>每个 VLAN 开一个监听口，端口号全部由系统分配。</summary>
    public VirtualSwitch(IEnumerable<int> vlans, IFrameObserver? observer = null)
        : this(vlans, observer, 0) { }

    private VirtualSwitch(IEnumerable<int> vlans, IFrameObserver? observer, int firstPort)
    {
        _observer = observer;
        var list = vlans.Distinct().ToList();
        if (list.Count == 0) throw new ArgumentException("至少要有一个 VLAN", nameof(vlans));
        // 先全部校验完再开监听，否则前面开好的口会在抛异常时漏掉
        foreach (int vlan in list)
            if (vlan is < 1 or > 4094)
                throw new ArgumentOutOfRangeException(nameof(vlans), vlan, "VLAN 号在 1..4094");
        foreach (int vlan in list)
        {
            var listener = new TcpListener(IPAddress.Loopback, _listeners.Count == 0 ? firstPort : 0);
            listener.Start();
            _listeners[vlan] = listener;
        }
        Vlans = list;
    }

    public IReadOnlyList<int> Vlans { get; }

    /// <summary>某个 VLAN 的监听端口。虚拟机的网卡连这个口就进了这个 VLAN。</summary>
    public int PortFor(int vlan) =>
        _listeners.TryGetValue(vlan, out var l)
            ? ((IPEndPoint)l.LocalEndpoint).Port
            : throw new ArgumentException($"交换机上没有 VLAN {vlan}", nameof(vlan));

    /// <summary>第一个 VLAN 的监听端口。只有一个 VLAN 时就是它。</summary>
    public int Port => PortFor(Vlans[0]);

    public long FramesForwarded => Interlocked.Read(ref _framesForwarded);

    /// <summary>因为带了 802.1Q 标签被丢掉的帧数。</summary>
    public long TaggedFramesDropped => Interlocked.Read(ref _taggedDropped);

    public IReadOnlyDictionary<(int Vlan, MacAddressKey Mac), int> MacTable =>
        _macTable.ToDictionary(kv => kv.Key, kv => kv.Value.PortId);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token, cancellationToken);
        await Task.WhenAll(_listeners.Select(kv => AcceptLoopAsync(kv.Key, kv.Value, linked.Token))).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(int vlan, TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            client.NoDelay = true;
            int id = Interlocked.Increment(ref _nextPortId) - 1;
            var switchPort = new SwitchPort(id, vlan, client);
            _ports[id] = switchPort;

            _ = Task.Run(() => PumpPortAsync(switchPort, token), token);
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
                    buffer.AsMemory(filled, buffer.Length - filled), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                filled += read;

                // 一次 read 可能拿到半个帧，也可能拿到好几个（TCP 是字节流）
                int offset = 0;
                while (true)
                {
                    var window = new ReadOnlyMemory<byte>(buffer, offset, filled - offset);
                    var frame = StreamFrameCodec.TryReadFrame(window, out int consumed);
                    if (frame is null) break;
                    Forward(port, frame.Value.Span);
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
            foreach (var (key, entry) in _macTable)
                if (entry.PortId == port.Id)
                    _macTable.TryRemove(key, out _);
            port.Dispose();
        }
    }

    private void Forward(SwitchPort source, ReadOnlySpan<byte> frame)
    {
        if (!EthernetFrame.IsValid(frame)) return;

        var eth = new EthernetFrame(frame);
        if (eth.IsVlanTagged)
        {
            // access 口不认客户机自己打的标签，见类注释
            Interlocked.Increment(ref _taggedDropped);
            return;
        }

        int vlan = source.Vlan;
        Interlocked.Increment(ref _framesForwarded);
        _observer?.OnFrame(source.Id, vlan, frame);

        var now = DateTime.UtcNow;
        _macTable[(vlan, eth.Source)] = new MacEntry(source.Id, now);

        foreach (var (key, entry) in _macTable)
            if (now - entry.LastSeen > MacAgeTimeout)
                _macTable.TryRemove(key, out _);

        var header = new byte[StreamFrameCodec.HeaderSize];
        StreamFrameCodec.WriteHeader(header, frame.Length);
        var payload = frame.ToArray();

        // 目的地址已知且是单播 -> 只发那一个口；未知 / 广播 / 组播 -> 在本 VLAN 内泛洪。
        // 注意只查一次表：查两次的话表项可能在两次之间被老化掉，
        // 第二次拿到 default 会把帧发去 0 号端口。
        var destination = eth.Destination;
        if (!destination.IsGroupAddress
            && _macTable.TryGetValue((vlan, destination), out var target)
            && target.PortId != source.Id
            && _ports.TryGetValue(target.PortId, out var only))
        {
            only.TrySend(header, payload);
            return;
        }

        foreach (var (id, p) in _ports)
            if (id != source.Id && p.Vlan == vlan)
                p.TrySend(header, payload);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var l in _listeners.Values) l.Stop();
        foreach (var (_, p) in _ports) p.Dispose();
        _ports.Clear();
        _cts.Dispose();
    }

    private readonly record struct MacEntry(int PortId, DateTime LastSeen);

    private sealed class SwitchPort(int id, int vlan, TcpClient client) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public int Id { get; } = id;
        public int Vlan { get; } = vlan;
        public TcpClient Client { get; } = client;

        public void TrySend(byte[] header, byte[] payload)
        {
            _ = Task.Run(async () =>
            {
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var stream = Client.GetStream();
                    await stream.WriteAsync(header).ConfigureAwait(false);
                    await stream.WriteAsync(payload).ConfigureAwait(false);
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
    /// <param name="vlan">帧所在的 VLAN（即进来那个端口所属的 VLAN）。</param>
    void OnFrame(int sourcePortId, int vlan, ReadOnlySpan<byte> frame);
}
