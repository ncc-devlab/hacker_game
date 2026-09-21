using System.Net.Sockets;
using System.Text;
using GameHacker.Core.Channels;

namespace GameHacker.Core.Tests;

/// <summary>
/// 控制通道的事件分发。
/// </summary>
/// <remarks>
/// 这些用例是为 M2 里实测到的两个缺陷加的护栏，不是凑数：
/// 事件分发曾经是破坏性单消费者，并发等待者会互相吞事件；
/// 改成广播之后又差点丢掉订阅之前到达的 ready 信标（它只发一次）。
/// 两者都只在并发场景下发作，很容易在重构时被改回去。
/// </remarks>
public class ControlChannelTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private SerialChannel _serial = null!;
    private ControlChannel _control = null!;
    private TcpClient _guest = null!;
    private CancellationTokenSource _cts = null!;

    /// <summary>模拟客户机往 ttyS1 写一行。</summary>
    private async Task GuestSendAsync(string line)
    {
        await _guest.GetStream().WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await _guest.GetStream().FlushAsync();
        await Task.Delay(50);   // 给接收线程一点时间
    }

    public async Task InitializeAsync()
    {
        _cts = new CancellationTokenSource();
        _serial = new SerialChannel();
        _ = _serial.RunAsync(_cts.Token);

        // 宿主监听、客户机连进来 —— 与真实拓扑方向一致
        _guest = new TcpClient();
        await _guest.ConnectAsync("127.0.0.1", _serial.Port);
        _control = new ControlChannel(_serial);

        while (!_serial.IsConnected) await Task.Delay(10);
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        _guest.Dispose();
        await _control.DisposeAsync();
        await _serial.DisposeAsync();
        _cts.Dispose();
    }

    [Fact]
    public async Task 两个并发等待者互不吞事件()
    {
        // 这正是 M2 里的发作场景：resize 的等待者先起来，
        // 破坏性队列让它读到 ready 后直接丢弃，于是 ready 的等待者永远等不到。
        var resize = _control.WaitEventAsync("resize", Timeout);
        var ready = _control.WaitEventAsync("ready", Timeout);

        await GuestSendAsync("""{"ev":"ready","host":"alpha"}""");
        await GuestSendAsync("""{"ev":"resize","rows":30,"cols":100}""");

        Assert.Equal("alpha", (await ready)["host"]!.GetValue<string>());
        Assert.Equal(30, (await resize)["rows"]!.GetValue<int>());
    }

    [Fact]
    public async Task 订阅之前到达的事件也能等到()
    {
        // ready 信标只发一次。纯广播模型会把订阅之前到达的它丢掉，
        // 所以必须保留历史窗口给新订阅者补发。
        await GuestSendAsync("""{"ev":"ready","host":"beta"}""");

        var ready = await _control.WaitReadyAsync(Timeout);
        Assert.Equal("beta", ready["host"]!.GetValue<string>());
    }

    [Fact]
    public async Task 非JSON行被忽略且不影响后续解析()
    {
        // 串口上混着开机噪声和命令回显，解析不出来的行不能让通道卡住
        await GuestSendAsync("resize 30 100");          // 我们自己命令的回显
        await GuestSendAsync("[    1.23] random kernel noise");
        await GuestSendAsync("{不是合法 JSON");
        await GuestSendAsync("""{"ev":"ready","host":"gamma"}""");

        var ready = await _control.WaitReadyAsync(Timeout);
        Assert.Equal("gamma", ready["host"]!.GetValue<string>());
    }

    [Fact]
    public async Task 一行被拆成多次写入也能拼回来()
    {
        // TCP 是字节流，一行 JSON 完全可能分两次到达
        var stream = _guest.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{"ev":"rea"""));
        await stream.FlushAsync();
        await Task.Delay(50);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""dy","host":"split"}""" + "\n"));
        await stream.FlushAsync();

        var ready = await _control.WaitReadyAsync(Timeout);
        Assert.Equal("split", ready["host"]!.GetValue<string>());
    }

    [Fact]
    public async Task 一次写入含多行时逐行解析()
    {
        var stream = _guest.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(
            """{"ev":"status","up":1}""" + "\n" + """{"ev":"ready","host":"multi"}""" + "\n"));
        await stream.FlushAsync();

        var ready = await _control.WaitReadyAsync(Timeout);
        Assert.Equal("multi", ready["host"]!.GetValue<string>());
    }

    [Fact]
    public async Task 等不到事件时抛TimeoutException()
    {
        // 不能抛 OperationCanceledException —— 那样上层分不清是超时还是被取消
        await Assert.ThrowsAsync<TimeoutException>(() =>
            _control.WaitEventAsync("never", TimeSpan.FromMilliseconds(200)));
    }
}
