using System.Text;
using System.Text.RegularExpressions;

namespace GameHacker.Core.Channels;

/// <summary>在 tty 上等某段文字没等到。</summary>
public sealed class TtyTimeoutException(string expected, string seen)
    : Exception($"等 {expected} 超时。tty 上最后看到的是:\n{seen}")
{
    /// <summary>超时前收到的尾部文本，排查用。</summary>
    public string Seen { get; } = seen;
}

/// <summary>
/// 一条串口 tty 上的登录会话：登录、敲命令、读输出。
/// </summary>
/// <remarks>
/// <para>这是管理员假人的手脚。他不走隐藏控制通道，而是像真人一样从
/// <c>ttyS2</c> 登录：真的 getty、真的 login、真的账号。于是 utmp/wtmp 里有他的会话、
/// <c>ps</c> 里有他的 shell，玩家能察觉他来过 —— 对手是世界之内的角色，
/// 不是一个看不见的判定器。</para>
/// <para><b>为什么不用固定标记而是认提示符。</b> 常见做法是每条命令后面跟一句
/// <c>echo &lt;随机标记&gt;</c> 来划分输出，但那条标记会出现在玩家的
/// <c>ps</c> 里，一眼就是自动化脚本。认 shell 自己的提示符不留任何多余痕迹。</para>
/// <para><b>线程。</b> 串口数据在后台线程到达，这里只把它追加进缓冲；
/// 等待由 <see cref="SemaphoreSlim"/> 唤醒，不轮询。</para>
/// </remarks>
public sealed partial class TtySession : IDisposable
{
    /// <summary>shell 提示符：行尾的 <c>$</c> 或 <c>#</c>，后面可能跟一个空格。</summary>
    private static readonly Regex ShellPrompt = new(@"(^|\n)[^\n]*[$#][ ]?$", RegexOptions.Compiled);

    private readonly SerialChannel _serial;
    private readonly StringBuilder _buffer = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly object _gate = new();

    public TtySession(SerialChannel serial)
    {
        _serial = serial;
        _serial.DataReceived += OnData;
    }

    /// <summary>已登录的用户名，还没登录时为 <c>null</c>。</summary>
    public string? User { get; private set; }

    private void OnData(ReadOnlyMemory<byte> data)
    {
        lock (_gate) _buffer.Append(Encoding.UTF8.GetString(data.Span));
        // 可能有多个等待者，但同一时刻只应该有一个（会话是串行使用的）
        if (_arrived.CurrentCount == 0) _arrived.Release();
    }

    /// <summary>
    /// 登录。需要客户机那边 <c>ttyS2</c> 上已经起了 getty。
    /// </summary>
    public async Task LoginAsync(string user, string password, TimeSpan timeout,
                                 CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        // getty 可能还没起来，或者已经打过一次提示符了。敲个回车把提示符逼出来
        await SendLineAsync("", cts.Token).ConfigureAwait(false);
        await ExpectAsync(new Regex(@"login:[ ]?$", RegexOptions.IgnoreCase), "登录提示符", cts.Token)
            .ConfigureAwait(false);

        await SendLineAsync(user, cts.Token).ConfigureAwait(false);
        await ExpectAsync(new Regex(@"assword:[ ]?$"), "口令提示符", cts.Token).ConfigureAwait(false);

        // 口令不回显，login 也不会在失败时立刻回提示符，所以这里等的是
        // 「拿到 shell 提示符」还是「又回到 login:」—— 后者就是口令不对
        await SendLineAsync(password, cts.Token).ConfigureAwait(false);
        string after = await ExpectAsync(
            new Regex(@"(?<bad>login:[ ]?$)|" + ShellPrompt), "shell 提示符", cts.Token).ConfigureAwait(false);
        if (after.TrimEnd().EndsWith("login:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{user} 登录失败：口令不对");

        User = user;
    }

    /// <summary>
    /// 敲一条命令，返回它的输出（不含回显的命令本身和结尾的提示符）。
    /// </summary>
    public async Task<string> RunAsync(string command, TimeSpan timeout,
                                       CancellationToken cancellationToken = default)
    {
        if (User is null) throw new InvalidOperationException("还没登录");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        lock (_gate) _buffer.Clear();
        await SendLineAsync(command, cts.Token).ConfigureAwait(false);
        string text = await ExpectAsync(ShellPrompt, $"命令 \"{command}\" 的提示符", cts.Token)
            .ConfigureAwait(false);
        return StripEcho(text, command);
    }

    /// <summary>退出登录。管理员查完岗要走，会话留在 wtmp 里。</summary>
    public async Task LogoutAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (User is null) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await SendLineAsync("exit", cts.Token).ConfigureAwait(false);
            await ExpectAsync(new Regex(@"login:[ ]?$", RegexOptions.IgnoreCase), "登录提示符", cts.Token)
                .ConfigureAwait(false);
        }
        catch (TtyTimeoutException) { /* 走就走了，没回到登录界面也不影响什么 */ }
        User = null;
    }

    /// <summary>等 tty 上出现匹配 <paramref name="pattern"/> 的内容，返回到此为止的全部文本。</summary>
    public async Task<string> ExpectAsync(Regex pattern, string what, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                string text = Normalize(_buffer.ToString());
                if (pattern.IsMatch(text)) return text;
            }

            try
            {
                await _arrived.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                string seen;
                lock (_gate) seen = Tail(Normalize(_buffer.ToString()));
                throw new TtyTimeoutException(what, seen);
            }
        }
    }

    private Task SendLineAsync(string text, CancellationToken cancellationToken) =>
        _serial.SendAsync(Encoding.UTF8.GetBytes(text + "\n"), cancellationToken);

    /// <summary>
    /// 统一成普通文本再做匹配：CRLF 换成 LF，去掉终端控制序列。
    /// </summary>
    /// <remarks>
    /// 控制序列必须去掉，否则提示符匹配不上 —— busybox 的 shell 打完提示符
    /// 会紧跟一个查光标位置的 <c>ESC[6n</c>，缓冲区就不再以 <c>$</c> 结尾了。
    /// </remarks>
    private static string Normalize(string text) =>
        AnsiEscape().Replace(text.Replace("\r\n", "\n").Replace("\r", ""), "");

    /// <summary>CSI（ESC[…字母）、OSC（ESC]…BEL/ST）以及单字符转义。</summary>
    [GeneratedRegex(@"\e\[[0-9;?]*[ -/]*[@-~]|\e\][^\a\e]*(\a|\e\\)|\e[@-Z\\-_]")]
    private static partial Regex AnsiEscape();

    /// <summary>去掉开头被 tty 回显的命令行，以及结尾的提示符。</summary>
    private static string StripEcho(string text, string command)
    {
        var lines = text.Split('\n').ToList();
        int echo = lines.FindIndex(l => l.TrimEnd().EndsWith(command, StringComparison.Ordinal));
        if (echo >= 0) lines.RemoveRange(0, echo + 1);
        if (lines.Count > 0) lines.RemoveAt(lines.Count - 1);      // 结尾的提示符
        return string.Join('\n', lines).Trim('\n');
    }

    private static string Tail(string text, int lines = 8) =>
        string.Join('\n', text.Split('\n').TakeLast(lines));

    public void Dispose()
    {
        _serial.DataReceived -= OnData;
        _arrived.Dispose();
    }
}
