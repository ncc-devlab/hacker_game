namespace GameHacker.Core.Admin;

/// <summary>管理员查岗时会做的一件事。</summary>
public enum AdminCheck
{
    /// <summary>看进程表里有没有不该在的东西。</summary>
    Processes,
    /// <summary>看还有谁登录着。</summary>
    Sessions,
    /// <summary>翻系统日志，也看它有没有被动过。</summary>
    Log,
    /// <summary>该在跑的服务还在不在。</summary>
    Services,
    /// <summary>这台机器是不是在替别人转发包。</summary>
    Forwarding,
}

/// <summary>玩家看不看得见管理员的动向。</summary>
public enum AdminVisibility
{
    /// <summary>界面上显示他在不在、什么时候来。</summary>
    Shown,
    /// <summary>界面上什么都不说。玩家只能自己从机器上看出他来过 —— 高手模式。</summary>
    Hidden,
}

/// <summary>例行动作：做什么、多大概率做、发现一处加多少怀疑。</summary>
public sealed record AdminAction
{
    public required string Id { get; init; }
    public required AdminCheck Check { get; init; }

    /// <summary>每次查岗做这件事的概率。他是人，不是巡检脚本，不会每次都全查一遍。</summary>
    public double Chance { get; init; } = 0.5;

    /// <summary>这项检查发现一处可疑，加多少怀疑度。</summary>
    public int Weight { get; init; } = 20;
}

/// <summary>他多久来一次、来了待多久。</summary>
public sealed record AdminSchedule
{
    public int FirstPatrolSeconds { get; init; } = 90;
    public int IntervalSeconds { get; init; } = 120;

    /// <summary>间隔的随机浮动。没有它玩家就会掐着秒表干活。</summary>
    public int JitterSeconds { get; init; } = 25;

    /// <summary>一次查岗最少 / 最多做几件事。</summary>
    public int MinActions { get; init; } = 1;
    public int MaxActions { get; init; } = 3;

    /// <summary>两条命令之间停多久（秒）。人不会连珠炮似的敲命令。</summary>
    public double PauseMin { get; init; } = 1.5;
    public double PauseMax { get; init; } = 5;
}

/// <summary>怀疑度怎么涨、涨到多少出事。</summary>
/// <remarks>
/// 两段式是这套设计的关键：平时他只是随手看看，发现一点小事只是<b>起疑</b>；
/// 疑心攒够了才会认真把机器从头翻一遍，那一翻基本什么都藏不住。
/// 于是玩家的压力不是「这次别被看到」，而是「别让他起疑心」。
/// </remarks>
public sealed record SuspicionRules
{
    /// <summary>怀疑度到这里，下次来就彻底查一遍。</summary>
    public int SweepAt { get; init; } = 40;

    /// <summary>到这里就是暴露，任务失败。</summary>
    public int ExposedAt { get; init; } = 100;

    /// <summary>一次什么都没查出来，疑心消一点。人会慢慢放松。</summary>
    public int CalmPerPatrol { get; init; } = 5;

    /// <summary>彻底检查时，每处发现的怀疑度乘这个数。</summary>
    public int SweepMultiplier { get; init; } = 2;
}

/// <summary>这台机器上「本来就该是这样」的东西。</summary>
public sealed record AdminAllow
{
    /// <summary>本来就该在的进程，写命令名或带 <c>*</c> 的模式。</summary>
    public IReadOnlyList<string> Processes { get; init; } = [];

    /// <summary>本来就该有人登录的终端，如 <c>ttyS2</c>（管理员自己）。</summary>
    public IReadOnlyList<string> Sessions { get; init; } = [];

    /// <summary>该一直跑着的服务。不在了同样可疑 —— 玩家可能为了清静把它杀了。</summary>
    public IReadOnlyList<string> Services { get; init; } = [];

    /// <summary>日志里出现就算可疑的内容（子串，不区分大小写）。</summary>
    public IReadOnlyList<string> LogRedFlags { get; init; } = [];

    /// <summary>
    /// 这台机器本来就该转发包（它真是台路由器）。默认不该。
    /// </summary>
    /// <remarks>
    /// 玩家把跳板机变成路由器才穿得进内网，所以转发开着本身就是一条痕迹 ——
    /// 而且是关不掉就带不走的那种：他每次看见都不会放松警惕。
    /// </remarks>
    public bool Forwarding { get; init; }
}

/// <summary>某个阶段里管理员的变化。只覆盖写了的部分。</summary>
public sealed record AdminStage
{
    public AdminSchedule? Schedule { get; init; }
    public SuspicionRules? Suspicion { get; init; }
    public IReadOnlyList<AdminAction>? Routine { get; init; }
    public AdminVisibility? Visibility { get; init; }
}

/// <summary>
/// 关卡里那位管理员：什么时候来、来了看什么、看到什么算可疑、疑到什么程度出事。
/// </summary>
/// <remarks>
/// <para>整套都是关卡数据，不是写死的行为。同一关的不同阶段还能换一套
/// （<see cref="Stages"/> 按步骤 id 挂），玩家推进到哪一步，他就变成什么样 ——
/// 比如玩家开始扫内网之后，他来得更勤、更容易起疑。</para>
/// <para>账号口令不在这里。每局现生成，只有游戏自己知道，关卡文件里不留。</para>
/// </remarks>
public sealed record AdminDefinition
{
    /// <summary>他查哪台机器。那台机器会因此多一个登录终端。</summary>
    public required string Machine { get; init; }

    public string User { get; init; } = "opsadm";

    /// <summary>玩家能不能在界面上看到他的动向。高手模式设成 hidden。</summary>
    public AdminVisibility Visibility { get; init; } = AdminVisibility.Shown;

    public AdminSchedule Schedule { get; init; } = new();
    public SuspicionRules Suspicion { get; init; } = new();
    public AdminAllow Allow { get; init; } = new();

    /// <summary>平时随手看的那些事，每次按各自的概率抽几件。</summary>
    public IReadOnlyList<AdminAction> Routine { get; init; } = [];

    /// <summary>
    /// 彻底检查时做哪些事（<see cref="Routine"/> 里的 id）。留空就是全做一遍。
    /// </summary>
    public IReadOnlyList<string> Sweep { get; init; } = [];

    /// <summary>按关卡步骤 id 挂的阶段设定。玩家推进到那一步时生效。</summary>
    public IReadOnlyDictionary<string, AdminStage> Stages { get; init; } =
        new Dictionary<string, AdminStage>();
}
