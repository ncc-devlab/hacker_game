using System.Text.RegularExpressions;
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
/// <para><b>人和人不一样</b>（见 <see cref="AdminSkill"/>）。实习运维只跑家目录里现成的脚本；
/// 资深运维挨个细看，一处异常就可能当场全查，还可能顺手把口令改了。来的是谁跟着
/// 游玩模式和关卡走，关卡也可以指定。</para>
/// <para><b>行为是关卡数据。</b> 查什么、多大概率查、什么算可疑、什么时候来，
/// 全在 <see cref="AdminDefinition"/> 里；不同阶段还能整套换掉，见
/// <see cref="EnterStage"/>。</para>
/// <para><b>线程。</b> 事件都在后台线程上触发，界面侧要自己倒回主线程。</para>
/// </remarks>
public sealed class AdminAgent
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private const string PasswordAlphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly TtySession _tty;
    private readonly AdminInspectors _inspectors;
    private readonly SuspicionMeter _meter;
    private readonly AdminProfile _profile;
    private readonly IReadOnlyList<string> _sweepIds;
    private readonly Random _random;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _passwords = [];

    private AdminDefinition _definition;
    private AdminSchedule _rawSchedule;
    private IReadOnlyList<AdminAction> _routine;

    public AdminAgent(AdminDefinition definition, AdminAccount account, TtySession tty, int? seed = null,
                      AdminSkill skill = AdminSkill.Regular)
    {
        _definition = definition;
        Skill = skill;
        _profile = definition.ProfileFor(skill);
        _rawSchedule = definition.Schedule;
        _routine = _profile.Routine ?? definition.Routine;
        // 档位自带一套手法时，关卡按自己那套 routine 写的 sweep 对不上号，不用它
        _sweepIds = _profile.Sweep ?? (_profile.Routine is null ? definition.Sweep : []);
        Account = account;
        _tty = tty;
        _inspectors = new AdminInspectors(definition.Allow, definition.Machine, account.User,
                                          definition.EffectiveScripts);
        _meter = new SuspicionMeter(_profile.Apply(definition.Suspicion));
        _random = seed is null ? new Random() : new Random(seed.Value);
        Visibility = definition.Visibility;
    }

    public AdminAccount Account { get; }
    public AdminSkill Skill { get; }
    public string Machine => _definition.Machine;
    public AdminActivity Activity { get; private set; } = AdminActivity.Away;
    public AdminVisibility Visibility { get; private set; }

    public int Suspicion => _meter.Level;
    public int ExposedAt => _meter.Rules.ExposedAt;
    /// <summary>当前这个人到多少分就会彻底查（已经按档位换算过）。</summary>
    public int SweepAt => _meter.Rules.SweepAt;
    public bool Exposed => _meter.Exposed;
    public IReadOnlyList<AdminFinding> Findings => _meter.Findings;

    /// <summary>他改过的口令，账号 → 新口令。只有游戏自己知道。</summary>
    public IReadOnlyDictionary<string, string> ChangedPasswords
    {
        get { lock (_gate) return new Dictionary<string, string>(_passwords); }
    }

    /// <summary>下一次来还有多久。</summary>
    public TimeSpan TimeToNextPatrol { get; private set; }

    public event Action<AdminActivity>? ActivityChanged;
    public event Action<PatrolReport>? PatrolCompleted;

    /// <summary>作息，按这个人的手脚快慢换算过。</summary>
    private AdminSchedule Schedule
    {
        get { lock (_gate) return _profile.Apply(_rawSchedule); }
    }

    /// <summary>
    /// 进入某个阶段：按关卡里给这一步挂的设定，换掉他的作息、脾气或要查的东西。
    /// </summary>
    /// <remarks>
    /// <para>已经攒下的怀疑度不清零 —— 阶段变了，他不会忘掉之前看见的事。</para>
    /// <para>阶段给的怀疑度规则照样按档位换算：资深运维在哪个阶段都比实习的敏感。</para>
    /// </remarks>
    public void EnterStage(string stepId)
    {
        if (!_definition.Stages.TryGetValue(stepId, out var stage)) return;
        lock (_gate)
        {
            if (stage.Schedule is not null) _rawSchedule = stage.Schedule;
            if (stage.Routine is not null) _routine = stage.Routine;
            if (stage.Suspicion is not null) _meter.UseRules(_profile.Apply(stage.Suspicion));
            if (stage.Visibility is { } visibility) Visibility = visibility;
        }
    }

    /// <summary>一直查下去，直到取消。某次没查成不影响下一次。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ProvisionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TtyTimeoutException or InvalidOperationException
                                      or IOException or ObjectDisposedException)
        {
            // 脚本没放进去，实习运维来的时候会发现脚本跑不起来 —— 那也是真实的状态，不拦着
            SetActivity(AdminActivity.Away);
        }
        catch (OperationCanceledException) { return; }

        TimeSpan wait = TimeSpan.FromSeconds(Schedule.FirstPatrolSeconds);
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

    /// <summary>
    /// 开局先登录一次，把他的运维脚本放进家目录。
    /// </summary>
    /// <remarks>
    /// 真机上这些脚本是他很久以前写好的。这里是开局现写，所以会在登录记录里
    /// 留下一次很早的登录 —— 像是他早上刚来过，说得通。
    /// </remarks>
    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        if (_definition.EffectiveScripts.Count == 0) return;
        SetActivity(AdminActivity.LoggingIn);
        await _tty.LoginAsync(Account.User, Account.Password, LoginTimeout, cancellationToken)
            .ConfigureAwait(false);
        foreach (string command in _inspectors.ProvisionCommands())
            await _tty.RunAsync(command, CommandTimeout, cancellationToken).ConfigureAwait(false);
        await _tty.LogoutAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        SetActivity(AdminActivity.Away);
    }

    /// <summary>来一次：登录、看几样（或者全看一遍）、可能当场翻脸、可能改口令、下线。</summary>
    public async Task<PatrolReport> PatrolAsync(CancellationToken cancellationToken)
    {
        bool sweep = _meter.SweepNext;
        var actions = sweep ? SweepActions() : PickActions();

        SetActivity(AdminActivity.LoggingIn);
        await _tty.LoginAsync(Account.User, Account.Password, LoginTimeout, cancellationToken)
            .ConfigureAwait(false);
        SetActivity(sweep ? AdminActivity.Sweeping : AdminActivity.Checking);

        var (did, seen) = await RunActionsAsync(actions, cancellationToken).ConfigureAwait(false);
        var report = Settle(sweep, did, seen);

        // 资深运维随手一看就够到阈值的话，可能不等下次，当场把剩下的也翻一遍
        if (ShouldEscalate(report))
        {
            SetActivity(AdminActivity.Sweeping);
            var done = actions.Select(a => a.Id).ToHashSet();
            var rest = SweepActions().Where(a => !done.Contains(a.Id)).ToList();
            var (more, moreSeen) = await RunActionsAsync(rest, cancellationToken).ConfigureAwait(false);
            report = Escalate(report, more, moreSeen);
        }

        if (ShouldChangePasswords(report))
            report = report with { PasswordsChanged = await ChangePasswordsAsync(cancellationToken).ConfigureAwait(false) };

        await _tty.LogoutAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);

        SetActivity(AdminActivity.Away);
        PatrolCompleted?.Invoke(report);
        return report;
    }

    private async Task<(List<AdminCheck> Did, List<(AdminFinding Finding, int Weight)> Seen)> RunActionsAsync(
        IReadOnlyList<AdminAction> actions, CancellationToken cancellationToken)
    {
        var seen = new List<(AdminFinding Finding, int Weight)>();
        var did = new List<AdminCheck>();
        foreach (var action in actions)
        {
            // 人不会连珠炮似的敲命令。这个停顿也让玩家有机会察觉他正在看
            await Task.Delay(NextPause(), cancellationToken).ConfigureAwait(false);
            string output = await _tty.RunAsync(_inspectors.CommandFor(action), CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            did.Add(action.Check);
            seen.AddRange(_inspectors.Inspect(action, output).Select(f => (f, action.Weight)));
        }
        return (did, seen);
    }

    /// <summary>纯算账：把这次看到的东西记进怀疑度，出一份报告。供测试直接调用。</summary>
    public PatrolReport Settle(bool sweep, IReadOnlyList<AdminCheck> did,
                               IReadOnlyList<(AdminFinding Finding, int Weight)> seen)
    {
        var fresh = _meter.Record(seen, sweep ? _meter.Rules.SweepMultiplier : 1);
        if (seen.Count == 0) _meter.Calm();
        return new PatrolReport(_definition.Machine, sweep, did, fresh, seen.Count, _meter.Level, _meter.Exposed);
    }

    /// <summary>
    /// 当场翻脸那一段的算账：接在 <paramref name="casual"/> 后面，按彻底检查的倍数记，
    /// 合成一份报告。什么都没再看出来也不消退 —— 他刚刚才起的疑。供测试直接调用。
    /// </summary>
    public PatrolReport Escalate(PatrolReport casual, IReadOnlyList<AdminCheck> did,
                                 IReadOnlyList<(AdminFinding Finding, int Weight)> seen)
    {
        var fresh = _meter.Record(seen, _meter.Rules.SweepMultiplier);
        return casual with
        {
            Sweep = true,
            Escalated = true,
            Did = [.. casual.Did, .. did],
            Findings = [.. casual.Findings, .. fresh],
            // 当场翻脸那一段看到的也要算进来：「这台机器干不干净」问的是他这一趟
            // 一共看见了什么，不是只看随手那几眼
            Seen = casual.Seen + seen.Count,
            Suspicion = _meter.Level,
            Exposed = _meter.Exposed,
        };
    }

    /// <summary>随手看完这一轮，要不要当场接着全查。</summary>
    public bool ShouldEscalate(PatrolReport report) =>
        !report.Sweep && !report.Exposed && _meter.SweepNext
        && Chance(_profile.SweepOnTheSpotChance ?? 0);

    /// <summary>
    /// 这次要不要改口令。关卡得给了要改的账号；看出了东西时概率高得多。
    /// </summary>
    public bool ShouldChangePasswords(PatrolReport report)
    {
        if (_definition.PasswordTargets.Count == 0 || report.Exposed) return false;
        double chance = report.FoundSomething ? _profile.PasswordChanceOnFinding ?? 0 : _profile.PasswordChance ?? 0;
        return Chance(chance);
    }

    /// <summary>
    /// 趁还登录着，用 <c>sudo passwd</c> 把关卡指定的那些账号的口令改掉。返回确实改成了的账号。
    /// </summary>
    /// <remarks>
    /// <para>客户机的 busybox 没有 setuid，他自己的账号改不了别人的口令，要靠 sudo
    /// （他在 sudoers 里，要口令）。新口令是在 <c>passwd</c> 的提示后面敲进去的，
    /// 不在命令行上，所以不进他的命令历史 —— 历史里只有 <c>sudo passwd ops</c> 这一句，
    /// sudo 也会往系统日志里记一笔。这两处是玩家能察觉的痕迹。</para>
    /// <para>中途出错不影响这次查岗的结论，只是这回没改成。</para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> ChangePasswordsAsync(CancellationToken cancellationToken)
    {
        var changed = new List<string>();
        foreach (string user in _definition.PasswordTargets)
        {
            string password = NewPassword();
            string output;
            try
            {
                output = await _tty.RunInteractiveAsync($"sudo passwd {user}",
                [
                    (SudoPrompt, Account.Password),
                    (NewPasswordPrompt, password),
                ], CommandTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TtyTimeoutException) { break; }
            // busybox 的 passwd 改成了会说 password for x changed by root；账号不存在是 unknown user
            if (!output.Contains($"password for {user} changed", StringComparison.Ordinal)) continue;
            lock (_gate) _passwords[user] = password;
            changed.Add(user);
        }
        return changed;
    }

    private static readonly Regex SudoPrompt = new(@"\[sudo\] password for [^\n]*: ?$", RegexOptions.Compiled);
    private static readonly Regex NewPasswordPrompt = new(@"(New|Retype) password: ?$", RegexOptions.Compiled);

    private static string NewPassword() =>
        System.Security.Cryptography.RandomNumberGenerator.GetString(PasswordAlphabet, 14);

    /// <summary>这次随手看哪几样。按各自的概率抽，抽空了就随便挑一样。</summary>
    public IReadOnlyList<AdminAction> PickActions()
    {
        lock (_gate)
        {
            if (_routine.Count == 0) return [];

            // 先把顺序打乱，后面的取舍都按这个乱序来。
            //
            // 这一步不只是为了「每次查的顺序不一样」（虽然那也必要：顺序固定的话
            // 玩家数着就能算出他下一条敲什么）。更要紧的是取舍要公平 ——
            // 先按关卡里的顺序挑、再砍掉超出上限的部分，写在后面的检查项
            // 在动作多的时候就<b>永远轮不到</b>。实测踩到过：五项都必做、上限四项，
            // 第五项「这机器是不是在转发」一次也没被查过。
            var order = _routine.OrderBy(_ => _random.Next()).ToList();
            var schedule = _profile.Apply(_rawSchedule);

            var picked = order.Where(a => _random.NextDouble() < a.Chance).ToList();
            if (picked.Count == 0) picked.Add(order[0]);
            foreach (var more in order)
            {
                if (picked.Count >= Math.Min(schedule.MinActions, _routine.Count)) break;
                if (!picked.Contains(more)) picked.Add(more);
            }
            return picked.Count > schedule.MaxActions
                ? picked.Take(schedule.MaxActions).ToList()
                : picked;
        }
    }

    /// <summary>彻底检查做哪些。没指定、或者指定的在当前这套里一件都对不上，就全做一遍。</summary>
    public IReadOnlyList<AdminAction> SweepActions()
    {
        lock (_gate)
        {
            var chosen = _routine.Where(a => _sweepIds.Contains(a.Id)).ToList();
            return chosen.Count > 0 ? chosen : _routine.ToList();
        }
    }

    private bool Chance(double chance)
    {
        if (chance <= 0) return false;
        lock (_gate) return _random.NextDouble() < chance;
    }

    private TimeSpan NextPause()
    {
        lock (_gate)
        {
            var schedule = _profile.Apply(_rawSchedule);
            double seconds = schedule.PauseMin
                             + _random.NextDouble() * Math.Max(0, schedule.PauseMax - schedule.PauseMin);
            return TimeSpan.FromSeconds(seconds);
        }
    }

    private TimeSpan NextInterval()
    {
        lock (_gate)
        {
            int jitter = _rawSchedule.JitterSeconds;
            int seconds = _rawSchedule.IntervalSeconds + (jitter > 0 ? _random.Next(-jitter, jitter + 1) : 0);
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
