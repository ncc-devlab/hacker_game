namespace GameHacker.Core.Admin;

/// <summary>一次查岗的结果。</summary>
/// <param name="Sweep">这次是不是彻底检查。</param>
/// <param name="Findings">这次<b>新</b>看出来的东西，之前记过的不重复算。</param>
/// <param name="Suspicion">这次之后的怀疑度。</param>
/// <param name="Exposed">是否已经查实，也就是任务失败。</param>
/// <param name="Escalated">随手看着看着够到了阈值，当场接着全查了（老手才会）。</param>
public sealed record PatrolReport(
    string Machine,
    bool Sweep,
    IReadOnlyList<AdminCheck> Did,
    IReadOnlyList<AdminFinding> Findings,
    int Suspicion,
    bool Exposed,
    bool Escalated = false)
{
    public bool FoundSomething => Findings.Count > 0;

    /// <summary>这次查完他改掉了哪些账号的口令。</summary>
    public IReadOnlyList<string> PasswordsChanged { get; init; } = [];
}

/// <summary>
/// 管理员的疑心。
/// </summary>
/// <remarks>
/// <para><b>两段式。</b> 平时随手看看，看出点小事只是起疑；疑心攒到
/// <see cref="SuspicionRules.SweepAt"/>，下次来就把机器从头翻一遍，
/// 那一翻基本什么都藏不住。所以玩家真正要管理的不是「这次别被看到」，
/// 而是「别让他起疑心」。</para>
/// <para><b>疑心会消。</b> 一次什么都没查出来就退一点：人会慢慢放松。
/// 没有这一条的话怀疑度只增不减，玩家迟早必然暴露，隐蔽就不是一门手艺，
/// 只是拖时间。</para>
/// <para><b>同一处只算一次。</b> 那个还在跑的进程他每次都看得见，
/// 按次累加的话分数只取决于玩家动作多慢。</para>
/// </remarks>
public sealed class SuspicionMeter(SuspicionRules rules)
{
    private readonly HashSet<string> _counted = [];
    private readonly List<AdminFinding> _all = [];

    public SuspicionRules Rules { get; private set; } = rules;

    /// <summary>当前怀疑度。</summary>
    public int Level { get; private set; }

    /// <summary>下次来要不要彻底查一遍。</summary>
    public bool SweepNext => Level >= Rules.SweepAt && !Exposed;

    /// <summary>已经查实：任务失败。</summary>
    public bool Exposed => Level >= Rules.ExposedAt;

    /// <summary>他看出来的全部东西，按先后顺序。</summary>
    public IReadOnlyList<AdminFinding> Findings => _all;

    /// <summary>阶段切换时换一套规则，已经攒下的怀疑度保留。</summary>
    public void UseRules(SuspicionRules replacement) => Rules = replacement;

    /// <summary>记下这次看出来的东西，返回其中确实是新的那些。</summary>
    public IReadOnlyList<AdminFinding> Record(IEnumerable<(AdminFinding Finding, int Weight)> findings, int multiplier)
    {
        var fresh = new List<AdminFinding>();
        foreach (var (finding, weight) in findings)
        {
            if (!_counted.Add($"{finding.Check}\u0000{finding.Subject}")) continue;
            fresh.Add(finding);
            _all.Add(finding);
            Level = Math.Min(Rules.ExposedAt, Level + weight * multiplier);
        }
        return fresh;
    }

    /// <summary>这次什么都没看出来，疑心退一点。</summary>
    public void Calm() => Level = Math.Max(0, Level - Rules.CalmPerPatrol);
}
