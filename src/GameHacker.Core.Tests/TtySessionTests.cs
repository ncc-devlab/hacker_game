using System.Net.Sockets;
using System.Text;
using GameHacker.Core.Channels;

namespace GameHacker.Core.Tests;

/// <summary>
/// 管理员登录会话的对话逻辑。这里用一个假客户机扮演 getty / login / shell，
/// 不需要真虚拟机，几十毫秒跑完；真机上的那条路见 <see cref="AdminTtyIntegrationTests"/>。
/// </summary>
public class TtySessionTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private SerialChannel _serial = null!;
    private TtySession _session = null!;
    private TcpClient _guest = null!;
    private CancellationTokenSource _cts = null!;

    /// <summary>客户机往 tty 上写一段（真 tty 送的是 CRLF）。</summary>
    private async Task GuestWriteAsync(string text)
    {
        var stream = _guest.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n")));
        await stream.FlushAsync();
    }

    /// <summary>读走宿主发来的一行（回车结尾），模拟 tty 收到输入。</summary>
    private async Task<string> GuestReadLineAsync()
    {
        var buffer = new byte[256];
        var text = new StringBuilder();
        while (!text.ToString().Contains('\n'))
        {
            int n = await _guest.GetStream().ReadAsync(buffer);
            if (n == 0) break;
            text.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
        return text.ToString().Trim('\n', '\r');
    }

    public async Task InitializeAsync()
    {
        _cts = new CancellationTokenSource();
        _serial = new SerialChannel();
        _ = _serial.RunAsync(_cts.Token);
        _guest = new TcpClient();
        await _guest.ConnectAsync("127.0.0.1", _serial.Port);
        _session = new TtySession(_serial);
        while (!_serial.IsConnected) await Task.Delay(10);
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        _session.Dispose();
        _guest.Dispose();
        await _serial.DisposeAsync();
        _cts.Dispose();
    }

    /// <summary>扮演 getty + login：等用户名和口令，对了就给个 shell 提示符。</summary>
    private async Task PlayLoginAsync(string password, string prompt = "jump01:~$ ")
    {
        await GuestReadLineAsync();                      // 宿主先敲的那个回车
        await GuestWriteAsync("\njump01 login: ");
        string user = await GuestReadLineAsync();
        await GuestWriteAsync(user + "\nPassword: ");
        string given = await GuestReadLineAsync();
        await GuestWriteAsync(given == password ? $"\nWelcome to jump01\n{prompt}" : "\njump01 login: ");
    }

    [Fact]
    public async Task 登录后能敲命令并拿到干净的输出()
    {
        var guest = Task.Run(async () =>
        {
            await PlayLoginAsync("s3cret");
            // shell 会回显命令本身，输出之后再给一个提示符
            string command = await GuestReadLineAsync();
            await GuestWriteAsync($"{command}\nuid=1000(admin) gid=1000(admin)\njump01:~$ ");
        });

        await _session.LoginAsync("admin", "s3cret", Timeout);
        Assert.Equal("admin", _session.User);

        string output = await _session.RunAsync("id", Timeout);
        Assert.Equal("uid=1000(admin) gid=1000(admin)", output);
        await guest;
    }

    [Fact]
    public async Task 多行输出按原样返回()
    {
        var guest = Task.Run(async () =>
        {
            await PlayLoginAsync("s3cret");
            string command = await GuestReadLineAsync();
            await GuestWriteAsync($"{command}\nline1\nline2\nline3\njump01:~$ ");
        });

        await _session.LoginAsync("admin", "s3cret", Timeout);
        Assert.Equal("line1\nline2\nline3", await _session.RunAsync("cat /tmp/x", Timeout));
        await guest;
    }

    [Fact]
    public async Task 口令不对时报错而不是一直等下去()
    {
        var guest = Task.Run(() => PlayLoginAsync("s3cret"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _session.LoginAsync("admin", "wrong", Timeout));
        Assert.Contains("口令不对", ex.Message);
        Assert.Null(_session.User);
        await guest;
    }

    [Fact]
    public async Task 客户机没反应时超时并带上看到的内容()
    {
        var guest = Task.Run(async () =>
        {
            await GuestReadLineAsync();
            await GuestWriteAsync("\nAlpine Linux 3.23\n");   // 只有开机噪声，没有 login:
        });

        var ex = await Assert.ThrowsAsync<TtyTimeoutException>(
            () => _session.LoginAsync("admin", "s3cret", TimeSpan.FromMilliseconds(500)));
        Assert.Contains("登录提示符", ex.Message);
        Assert.Contains("Alpine", ex.Seen);
        await guest;
    }

    [Fact]
    public async Task 没登录就敲命令会被拒绝()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _session.RunAsync("id", Timeout));
    }
}
