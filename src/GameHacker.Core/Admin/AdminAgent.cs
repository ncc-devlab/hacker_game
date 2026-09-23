using GameHacker.Core.Channels;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Admin;

/// <summary>管理员当下在做什么，给界面显示。</summary>
public enum AdminActivity
{
    /// <summary>不在机器上。</summary>
    Away,
    /// <summary>正在登录。</summary>
    LoggingIn,
    /// <summary>登录着，正在翻。</summary>
    Checking,
}

/// <summary>
/// 管理员假人：按自己的节奏登录目标机、看一圈、给分、下线。
/// </summary>
/// <remarks>
/// <para>他只用世界之内的手段 —— 真的 <c>login</c>、真的 <c>ps</c>。
/// 玩家能在 <c>ps</c> 里看见他的会话，也就能察觉他什么时候在。
/// 判定用的隐藏通道（ttyS1）他一概不碰，那是给步骤判定用的、玩家看不见的东西。</para>
/// <para><b>周期性在这里是正当的。</b> 别处我们都避免定时轮询，因为那是实现上的偷懒；
/// 而「管理员每隔一阵子来查岗」是游戏世界里的行为，间隔本身就是难度旋钮。</para>
/// </remarks>
public sealed class AdminAgent
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly AdminDefinition _definition;
    private readonly TtySession _tty;
    private readonly ProcessWhitelist _whitelist;
    private readonly ThreatMeter _meter;
    private readonly Random _random;

    public AdminAgent(AdminDefinition definition, AdminAccount account, TtySession tty, int? seed = null)
    {
        _definition = definition;
        Account = account;
        _tty = tty;
        _whitelist = new ProcessWhitelist(definition.AllowProcesses);
        _meter = new ThreatMeter(definition.Threshold);
        _random = seed is null ? new Random() : new Random(seed.Value);
    }

    public AdminAccount Account { get; }
    public AdminActivity Activity { get; private set; } = AdminActivity.Away;
    public int Score => _meter.Score;
    public int Threshold => _meter.Threshold;
    public bool Exposed => _meter.Exposed;
    public IReadOnlyList<AdminFinding> Findings => _meter.All;

    /// <summary>下一次查岗还有多久。界面拿它显示倒计时。</summary>
    public TimeSpan TimeToNextPatrol { get; private set; }

    /// <summary>他在做什么变了。<b>在后台线程上触发。</b></summary>
    public event Action<AdminActivity>? ActivityChanged;

    /// <summary>查完一次岗。<b>在后台线程上触发。</b></summary>
    public event Action<PatrolReport>? PatrolCompleted;

    /// <summary>一直查下去，直到取消。查岗途中出错只当这一次没查成，下一轮照常。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait = TimeSpan.FromSeconds(_definition.FirstPatrolSeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            await CountDownAsync(wait, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await PatrolAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TtyTimeoutException or InvalidOperationException
                                          or IOException or ObjectDisposedException)
            {
                // 这次没查成（客户机忙、tty 卡住），不该把整个关卡带崩，下一轮再来
                SetActivity(AdminActivity.Away);
            }
            catch (OperationCanceledException) { break; }

            wait = NextInterval();
        }
        SetActivity(AdminActivity.Away);
    }

    /// <summary>查一次岗：登录、看一圈、下线。</summary>
    public async Task<PatrolReport> PatrolAsync(CancellationToken cancellationToken)
    {
        SetActivity(AdminActivity.LoggingIn);
        await _tty.LoginAsync(Account.User, Account.Password, LoginTimeout, cancellationToken)
            .ConfigureAwait(false);

        SetActivity(AdminActivity.Checking);
        string ps = await _tty.RunAsync("ps -o pid,user,tty,args", CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        var report = Evaluate(ps);

        await _tty.LogoutAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        SetActivity(AdminActivity.Away);

        PatrolCompleted?.Invoke(report);
        return report;
    }

    /// <summary>纯判定：给一份 <c>ps</c> 输出，算出这次的发现与评分。</summary>
    public PatrolReport Evaluate(string psOutput)
    {
        var suspicious = ProcessTable.Parse(psOutput)
            .Where(p => !_whitelist.Allows(p))
            .Select(p => new AdminFinding(
                Kind: "process",
                Subject: p.Command,
                Evidence: $"pid {p.Pid}  用户 {p.User}  终端 {p.Tty}",
                Weight: _definition.ProcessWeight,
                Explanation: $"{_definition.User} 在 {_definition.Machine} 上看到一个不该在的进程："
                             + $"{p.Command}（{(p.Tty == "?" ? "没有终端" : "终端 " + p.Tty)}，以 {p.User} 运行）"));

        var fresh = _meter.Record(suspicious);
        return new PatrolReport(_definition.Machine, fresh, _meter.Score, _meter.Exposed);
    }

    /// <summary>下次间隔，带随机浮动，不让玩家掐着秒表干活。</summary>
    private TimeSpan NextInterval()
    {
        int jitter = _definition.JitterSeconds;
        int seconds = _definition.IntervalSeconds + (jitter > 0 ? _random.Next(-jitter, jitter + 1) : 0);
        return TimeSpan.FromSeconds(Math.Max(5, seconds));
    }

    /// <summary>边等边更新倒计时，界面才有东西可显示。</summary>
    private async Task CountDownAsync(TimeSpan total, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + total;
        while (true)
        {
            TimeToNextPatrol = deadline - DateTime.UtcNow;
            if (TimeToNextPatrol <= TimeSpan.Zero) return;
            var step = TimeToNextPatrol < TimeSpan.FromSeconds(1) ? TimeToNextPatrol : TimeSpan.FromSeconds(1);
            try { await Task.Delay(step, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SetActivity(AdminActivity activity)
    {
        if (Activity == activity) return;
        Activity = activity;
        ActivityChanged?.Invoke(activity);
    }
}
