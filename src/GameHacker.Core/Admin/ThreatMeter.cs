namespace GameHacker.Core.Admin;

/// <summary>管理员这次查岗发现的一条东西。</summary>
/// <param name="Subject">被发现的对象，比如那条进程的命令。</param>
/// <param name="Evidence">他看到的原样证据，用来支撑说法。</param>
/// <param name="Explanation">给玩家的一句话：什么暴露了、为什么可疑。</param>
public sealed record AdminFinding(string Kind, string Subject, string Evidence, int Weight, string Explanation);

/// <summary>一次查岗的结果。</summary>
/// <param name="Findings">这次<b>新</b>发现的东西，之前已经记过的不重复算。</param>
/// <param name="Score">这次之后的累计威胁评分。</param>
/// <param name="Exposed">是否已经到阈值，也就是任务失败。</param>
public sealed record PatrolReport(string Machine, IReadOnlyList<AdminFinding> Findings, int Score, bool Exposed)
{
    public bool FoundSomething => Findings.Count > 0;
}

/// <summary>
/// 威胁评分：管理员发现的东西累加起来，到阈值就是暴露。
/// </summary>
/// <remarks>
/// <para><b>护栏：评分必须可理解。</b> 每一分都挂着一条说得出口的理由
/// （<see cref="AdminFinding.Explanation"/>），玩家被扣分时要能看懂是哪条痕迹出卖了他。
/// 这条现在就得守住 —— 等评分规则复杂起来再补，它就退化成玄学了。</para>
/// <para><b>同一条痕迹只算一次。</b> 管理员每次查岗都会看见那个还在跑的进程，
/// 按次累加的话，玩家做什么都来不及了，分数只取决于他动作有多慢。</para>
/// <para>现在只有「进程」这一种发现。日志、文件痕迹等按同样的形状加进来即可，
/// 分数与可解释性的逻辑不用动。</para>
/// </remarks>
public sealed class ThreatMeter(int threshold)
{
    private readonly HashSet<string> _counted = [];

    public int Score { get; private set; }
    public int Threshold { get; } = threshold;
    public bool Exposed => Score >= Threshold;

    /// <summary>已经记下的全部发现，按时间顺序。给玩家看的「他知道了什么」。</summary>
    public IReadOnlyList<AdminFinding> All => _all;
    private readonly List<AdminFinding> _all = [];

    /// <summary>记下新发现，返回其中确实是新的那些。</summary>
    public IReadOnlyList<AdminFinding> Record(IEnumerable<AdminFinding> findings)
    {
        var fresh = new List<AdminFinding>();
        foreach (var f in findings)
        {
            if (!_counted.Add($"{f.Kind}\u0000{f.Subject}")) continue;
            fresh.Add(f);
            _all.Add(f);
            Score += f.Weight;
        }
        return fresh;
    }
}
