using System.Net.Sockets;
using GameHacker.Core.Net;

namespace GameHacker.Core.Tests;

/// <summary>
/// 交换机的 VLAN 隔离。用裸 TCP 连接冒充虚拟机网卡，不需要 QEMU，几十毫秒跑完。
/// </summary>
/// <remarks>
/// 跳板关的前提是「玩家的机器物理上够不着内网」。这里要是漏一帧，
/// 玩家不经过跳板机就能直接扫到内网，整关就不成立了。
/// </remarks>
public class SwitchVlanTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    /// <summary>一个假网卡：连到交换机某个 VLAN 的口上收发帧。</summary>
    private sealed class FakeNic : IAsyncDisposable
    {
        private readonly TcpClient _client = new() { NoDelay = true };
        private readonly List<byte[]> _received = [];
        private Task? _pump;

        public byte[] Mac { get; }

        public FakeNic(byte last) => Mac = [0x52, 0x54, 0x00, 0x00, 0x00, last];

        public async Task ConnectAsync(int port)
        {
            await _client.ConnectAsync("127.0.0.1", port);
            _pump = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            var buffer = new byte[64 * 1024];
            int filled = 0;
            try
            {
                var stream = _client.GetStream();
                while (true)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(filled));
                    if (n == 0) return;
                    filled += n;
                    int offset = 0;
                    while (StreamFrameCodec.TryReadFrame(new ReadOnlyMemory<byte>(buffer, offset, filled - offset), out int used) is { } frame)
                    {
                        lock (_received) _received.Add(frame.ToArray());
                        offset += used;
                    }
                    Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset);
                    filled -= offset;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        }

        public int ReceivedCount { get { lock (_received) return _received.Count; } }

        public async Task SendAsync(byte[] destination, ushort etherType = 0x0800)
        {
            var frame = new byte[60];
            destination.CopyTo(frame, 0);
            Mac.CopyTo(frame, 6);
            frame[12] = (byte)(etherType >> 8);
            frame[13] = (byte)etherType;
            var header = new byte[StreamFrameCodec.HeaderSize];
            StreamFrameCodec.WriteHeader(header, frame.Length);
            var stream = _client.GetStream();
            await stream.WriteAsync(header);
            await stream.WriteAsync(frame);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            if (_pump is not null) await _pump;
        }
    }

    private static readonly byte[] Broadcast = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff];

    private sealed class VlanRecorder : IFrameObserver
    {
        public readonly List<int> Vlans = [];
        public void OnFrame(int sourcePortId, int vlan, ReadOnlySpan<byte> frame)
        {
            lock (Vlans) Vlans.Add(vlan);
        }
    }

    private static async Task<FakeNic> Nic(VirtualSwitch sw, int vlan, byte last)
    {
        var nic = new FakeNic(last);
        await nic.ConnectAsync(sw.PortFor(vlan));
        return nic;
    }

    /// <summary>等交换机把三个连接都接进来，不然第一帧可能在另一个口注册之前就泛洪完了。</summary>
    private static async Task SettleAsync() => await Task.Delay(Quiet);

    [Fact]
    public async Task 广播只在本_VLAN_内泛洪()
    {
        var recorder = new VlanRecorder();
        await using var sw = new VirtualSwitch([10, 20], recorder);
        using var cts = new CancellationTokenSource();
        var run = sw.RunAsync(cts.Token);

        await using var a = await Nic(sw, 10, 1);
        await using var b = await Nic(sw, 10, 2);
        await using var c = await Nic(sw, 20, 3);
        await SettleAsync();

        await a.SendAsync(Broadcast);
        await Task.Delay(Quiet);

        Assert.Equal(1, b.ReceivedCount);
        Assert.Equal(0, c.ReceivedCount);   // 另一个 VLAN 连广播都收不到
        Assert.Equal([10], recorder.Vlans);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task 知道对方_MAC_也发不过_VLAN()
    {
        await using var sw = new VirtualSwitch([10, 20]);
        using var cts = new CancellationTokenSource();
        var run = sw.RunAsync(cts.Token);

        await using var a = await Nic(sw, 10, 1);
        await using var c = await Nic(sw, 20, 3);
        await SettleAsync();

        // 先让交换机学到 c 的 MAC，再从 a 单播给它
        await c.SendAsync(Broadcast);
        await Task.Delay(Quiet);
        await a.SendAsync(c.Mac);
        await Task.Delay(Quiet);

        Assert.Equal(0, c.ReceivedCount);
        Assert.Contains((20, MacAddressKey.From(c.Mac)), sw.MacTable.Keys);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task 客户机自己打的_VLAN_标签被丢弃()
    {
        await using var sw = new VirtualSwitch([10, 20]);
        using var cts = new CancellationTokenSource();
        var run = sw.RunAsync(cts.Token);

        await using var a = await Nic(sw, 10, 1);
        await using var b = await Nic(sw, 10, 2);
        await SettleAsync();

        // 玩家手工造 802.1Q / QinQ 帧想跳 VLAN：access 口上一律不认，同 VLAN 也不转
        await a.SendAsync(Broadcast, etherType: 0x8100);
        await a.SendAsync(Broadcast, etherType: 0x88A8);
        await Task.Delay(Quiet);

        Assert.Equal(0, b.ReceivedCount);
        Assert.Equal(2, sw.TaggedFramesDropped);
        Assert.Equal(0, sw.FramesForwarded);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public void VLAN_号必须合法()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualSwitch([0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualSwitch([4095]));
        Assert.Throws<ArgumentException>(() => new VirtualSwitch(Array.Empty<int>()));
    }
}
