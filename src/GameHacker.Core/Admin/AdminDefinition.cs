namespace GameHacker.Core.Admin;

/// <summary>
/// 关卡里那位管理员：多久来查一次岗、什么算可疑、扣到多少算暴露。
/// </summary>
/// <remarks>
/// <para>这几个数就是 MVP2 说的难度旋钮：查岗频率、白名单松紧、暴露阈值。
/// 现在只是关卡里的常数，将来接到难度 / 辅助依赖轴上。</para>
/// <para>账号口令不在这里 —— 每次开局由游戏现生成一个，只有游戏自己知道，
/// 关卡文件里不留。</para>
/// </remarks>
public sealed record AdminDefinition
{
    /// <summary>他查哪台机器。必须是这一关里的机器，而且那台机器会因此多一个登录终端。</summary>
    public required string Machine { get; init; }

    /// <summary>他在那台机器上的账号名。</summary>
    public string User { get; init; } = "opsadm";

    /// <summary>进关之后多久来第一次。给玩家一点不被盯着的时间。</summary>
    public int FirstPatrolSeconds { get; init; } = 90;

    /// <summary>之后每隔多久来一次。</summary>
    public int IntervalSeconds { get; init; } = 120;

    /// <summary>
    /// 查岗间隔的随机浮动（正负这么多秒）。
    /// </summary>
    /// <remarks>
    /// 不加浮动的话玩家会掐着秒表干活，隐蔽就变成了背时刻表。
    /// 但浮动也不能太大，否则玩家没法判断「现在动手安不安全」，
    /// 被抓就成了运气问题 —— 那正是护栏要防的。
    /// </remarks>
    public int JitterSeconds { get; init; } = 20;

    /// <summary>威胁评分到这个数就算暴露。</summary>
    public int Threshold { get; init; } = 100;

    /// <summary>发现一个白名单外的进程扣多少分。</summary>
    public int ProcessWeight { get; init; } = 25;

    /// <summary>
    /// 这台机器上「本来就该有」的进程。
    /// </summary>
    /// <remarks>
    /// 写命令名（如 <c>syslogd</c>）或带 <c>*</c> 的模式（如 <c>/usr/sbin/sshd*</c>）。
    /// 客户机自带的那批（内核线程、init、getty、管理员自己的 shell）
    /// 由 <see cref="ProcessWhitelist.Baseline"/> 兜底，不用在关卡里重复写。
    /// </remarks>
    public IReadOnlyList<string> AllowProcesses { get; init; } = [];
}
