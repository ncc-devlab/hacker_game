using GameHacker.Core.Channels;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Admin;

/// <summary>管理员当下在做什么。</summary>
public enum AdminActivity
{
    /// <summary>不在机器上。</summary>
    Away,
    /// <summary>正在登录。</summary>
    LoggingIn,
    /// <summary>登录着，随手翻翻。</summary>
    Checking,
    /// <summary>起了疑心，正在把机器从头查一遍。</summary>
    Sweeping,
}

/// <summary>
/// 管理员假人：按自己的节奏来，每次随手看几样，疑心攒够了就彻底查。
/// </summary>
/// <remarks>
/// <para><b>他是人，不是巡检脚本。</b> 每次登录只随机挑几件事看，两条命令之间
/// 还会停一下。所以玩家没法靠「他每次都查这几样」来规划，只能真的把痕迹处理干净。</para>
/// <para><b>两段式的疑心</b>（见 <see cref="SuspicionMeter"/>）：随手看的那几眼很难要命，
/// 但每一处小事都在攒疑心；攒够了他会认真翻一遍，那一遍基本什么都藏不住。
/// 正因为平时那几眼不致命，界面上不显示他的动向（高手模式）才玩得下去。</para>
/// <para><b>行为是关卡数据。</b> 查什么、多大概率查、什么算可疑、什么时候来，
/// 全在 <see cref="AdminDefinition"/> 里；不同阶段还能整套换掉，见
/// <see cref="EnterStage"/>。</para>
/// <para><b>线程。</b> 事件都在后台线程上触发，界面侧要自己倒回主线程。</para>
/// </remarks>
public sealed class AdminAgent
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly TtySession _tty;
    private readonly AdminInspectors _inspectors;
    private readonly SuspicionMeter _meter;
    private readonly Random _random;
    private readonly object _gate = new();

    private AdminDefinition _definition;
    private AdminSchedule _schedule;
    private IReadOnlyList<AdminAction> _routine;

    public AdminAgent(AdminDefinition definition, AdminAccount account, TtySession tty, int? seed = null)
    {
        _definition = definition;
        _schedule = definition.Schedule;
        _routine = definition.Routine;
        Account = account;
        _tty = tty;
        _inspectors = new AdminInspectors(definition.Allow, definition.Machine, account.User);
        _meter = new SuspicionMeter(definition.Suspicion);
        _random = seed is null ? new Random() : new Random(seed.Value);
        Visibility = definition.Visibility;
    }

    public AdminAccount Account { get; }
    public string Machine => _definition.Machine;
    public AdminActivity Activity { get; private set; } = AdminActivity.Away;
    public AdminVisibility Visibility { get; private set; }

    public int Suspicion => _meter.Level;
    public int ExposedAt => _meter.Rules.ExposedAt;
    public bool Exposed => _meter.Exposed;
    public IReadOnlyList<AdminFinding> Findings => _meter.Findings;

    /// <summary>下一次来还有多久。</summary>
    public TimeSpan TimeToNextPatrol { get; private set; }

    public event Action<AdminActivity>? ActivityChanged;
    public event Action<PatrolReport>? PatrolCompleted;

    /// <summary>
    /// 进入某个阶段：按关卡里给这一步挂的设定，换掉他的作息、脾气或要查的东西。
    /// </summary>
    /// <remarks>已经攒下的怀疑度不清零 —— 阶段变了，他不会忘掉之前看见的事。</remarks>
    public void EnterStage(string stepId)
    {
        if (!_definition.Stages.TryGetValue(stepId, out var stage)) return;
        lock (_gate)
        {
            if (stage.Schedule is not null) _schedule = stage.Schedule;
            if (stage.Routine is not null) _routine = stage.Routine;
            if (stage.Suspicion is not null) _meter.UseRules(stage.Suspicion);
            if (stage.Visibility is { } visibility) Visibility = visibility;
        }
    }

    /// <summary>一直查下去，直到取消。某次没查成不影响下一次。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait = TimeSpan.FromSeconds(_schedule.FirstPatrolSeconds);
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
                // 这次没查成（机器忙、tty 卡住），不该把关卡带崩，下次再来
                SetActivity(AdminActivity.Away);
            }
            catch (OperationCanceledException) { break; }

            if (Exposed) break;
            wait = NextInterval();
        }
        SetActivity(AdminActivity.Away);
    }

    /// <summary>来一次：登录、看几样（或者全看一遍）、下线。</summary>
    public async Task<PatrolReport> PatrolAsync(CancellationToken cancellationToken)
    {
        bool sweep = _meter.SweepNext;
        var actions = sweep ? SweepActions() : PickActions();

        SetActivity(AdminActivity.LoggingIn);
        await _tty.LoginAsync(Account.User, Account.Password, LoginTimeout, cancellationToken)
            .ConfigureAwait(false);
        SetActivity(sweep ? AdminActivity.Sweeping : AdminActivity.Checking);

        var seen = new List<(AdminFinding Finding, int Weight)>();
        var did = new List<AdminCheck>();
        foreach (var action in actions)
        {
            // 人不会连珠炮似的敲命令。这个停顿也让玩家有机会察觉他正在看
            await Task.Delay(NextPause(), cancellationToken).ConfigureAwait(false);
            string output = await _tty.RunAsync(AdminInspectors.CommandFor(action.Check),
                                                CommandTimeout, cancellationToken).ConfigureAwait(false);
            did.Add(action.Check);
            seen.AddRange(_inspectors.Inspect(action.Check, output).Select(f => (f, action.Weight)));
        }

        await _tty.LogoutAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        SetActivity(AdminActivity.Away);

        var report = Settle(sweep, did, seen);
        PatrolCompleted?.Invoke(report);
        return report;
    }

    /// <summary>纯算账：把这次看到的东西记进怀疑度，出一份报告。供测试直接调用。</summary>
    public PatrolReport Settle(bool sweep, IReadOnlyList<AdminCheck> did,
                               IReadOnlyList<(AdminFinding Finding, int Weight)> seen)
    {
        var fresh = _meter.Record(seen, sweep ? _meter.Rules.SweepMultiplier : 1);
        if (seen.Count == 0) _meter.Calm();
        return new PatrolReport(_definition.Machine, sweep, did, fresh, _meter.Level, _meter.Exposed);
    }

    /// <summary>这次随手看哪几样。按各自的概率抽，抽空了就随便挑一样。</summary>
    public IReadOnlyList<AdminAction> PickActions()
    {
        lock (_gate)
        {
            if (_routine.Count == 0) return [];

            var picked = _routine.Where(a => _random.NextDouble() < a.Chance).ToList();
            if (picked.Count == 0) picked.Add(_routine[_random.Next(_routine.Count)]);
            if (picked.Count > _schedule.MaxActions)
                picked = picked.Take(_schedule.MaxActions).ToList();
            while (picked.Count < Math.Min(_schedule.MinActions, _routine.Count))
            {
                var more = _routine.FirstOrDefault(a => !picked.Contains(a));
                if (more is null) break;
                picked.Add(more);
            }

            // 顺序也要乱：每次都按同样的顺序查，玩家照样能数着来
            return picked.OrderBy(_ => _random.Next()).ToList();
        }
    }

    /// <summary>彻底检查做哪些。关卡没指定就全做一遍。</summary>
    public IReadOnlyList<AdminAction> SweepActions()
    {
        lock (_gate)
        {
            if (_definition.Sweep.Count == 0) return _routine.ToList();
            return _routine.Where(a => _definition.Sweep.Contains(a.Id)).ToList();
        }
    }

    private TimeSpan NextPause()
    {
        lock (_gate)
        {
            double seconds = _schedule.PauseMin
                             + _random.NextDouble() * Math.Max(0, _schedule.PauseMax - _schedule.PauseMin);
            return TimeSpan.FromSeconds(seconds);
        }
    }

    private TimeSpan NextInterval()
    {
        lock (_gate)
        {
            int jitter = _schedule.JitterSeconds;
            int seconds = _schedule.IntervalSeconds + (jitter > 0 ? _random.Next(-jitter, jitter + 1) : 0);
            return TimeSpan.FromSeconds(Math.Max(5, seconds));
        }
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
