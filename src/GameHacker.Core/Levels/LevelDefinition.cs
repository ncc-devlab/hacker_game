using GameHacker.Core.Admin;

namespace GameHacker.Core.Levels;

/// <summary>关卡属于哪条内容线，见 MVP2 文档第一节。</summary>
public enum LevelTrack
{
    /// <summary>教学关卡：浅、重引导、普及原理。</summary>
    Tutorial,
    /// <summary>实战关卡：承载更深入的内容，跳板扫描是第一个。</summary>
    Mission,
}

/// <summary>关卡的制作进度。草稿关可以没有检测，只在编辑器里能进。</summary>
public enum LevelStatus
{
    Playable,
    Draft,
}

/// <summary>
/// 一关的完整描述：选关界面显示什么、进关后拉起哪些机器、按什么步骤推进。
/// </summary>
/// <remarks>
/// <para>这是 MVP2 要长出的「关卡格式」的种子。字段刻意只放选关和运行
/// 这一关真正用得到的东西 —— 跳板扫描的内容做起来以后，缺什么再加什么，
/// 而不是先凭想象把格式盖满。</para>
/// <para>存成 <c>levels/*.json</c>，由 <see cref="LevelCatalog"/> 解析并校验。</para>
/// </remarks>
public sealed record LevelDefinition
{
    /// <summary>稳定的标识，存档里记的就是它。只用小写字母、数字和连字符。</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required LevelTrack Track { get; init; }

    /// <summary>同一条内容线里的排序，小的在前。</summary>
    public int Order { get; init; }

    public LevelStatus Status { get; init; } = LevelStatus.Playable;

    /// <summary>选关列表里的一句话。</summary>
    public string Summary { get; init; } = "";

    /// <summary>任务简报，选关界面的详情区显示。</summary>
    public string Briefing { get; init; } = "";

    /// <summary>要先完成哪些关才解锁。</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>网段。每个网段是交换机上的一个 VLAN，不同网段之间二层不通。</summary>
    public IReadOnlyList<NetworkDefinition> Networks { get; init; } = [];

    /// <summary>
    /// 这一关的机器。<b>第一台是玩家自己的机器</b>，它的终端放在最左边，
    /// 自检也在它上面敲键盘。
    /// </summary>
    public IReadOnlyList<MachineDefinition> Machines { get; init; } = [];

    /// <summary>任务步骤，按顺序推进，一步完成才显示下一步的提示。</summary>
    public IReadOnlyList<LevelStep> Steps { get; init; } = [];

    /// <summary>
    /// 这一关有没有管理员会来查岗。给了的话，他那台机器会多一个登录终端。
    /// </summary>
    public AdminDefinition? Admin { get; init; }
}

/// <summary>一个网段。</summary>
public sealed record NetworkDefinition
{
    /// <summary>网卡定义里引用它的名字，如 <c>outside</c>、<c>inside</c>。</summary>
    public required string Name { get; init; }

    /// <summary>交换机上的 VLAN 号，1..4094，同一关里不能重复。</summary>
    public required int Vlan { get; init; }

    /// <summary>如 <c>10.0.0.0/24</c>。网卡的 IP 必须落在里面，前缀长度也取自这里。</summary>
    public required string Subnet { get; init; }
}

/// <summary>关卡里的一台机器。</summary>
public sealed record MachineDefinition
{
    /// <summary>主机名，也是检测条件里引用这台机器的名字。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 网卡，按顺序成为客户机里的 eth0、eth1……。跳板机这类机器插两块，
    /// 一块在外网、一块在内网。
    /// </summary>
    public required IReadOnlyList<NicDefinition> Nics { get; init; }

    /// <summary>硬件人设，对应 <see cref="Qemu.HardwarePersona.ByName"/>。</summary>
    public string Persona { get; init; } = "workstation";

    /// <summary>
    /// 可写磁盘镜像的名字（不带扩展名，在镜像目录下找 <c>.qcow2</c>）。
    /// 不给就是纯内存机器。
    /// </summary>
    public string? Disk { get; init; }

    public int Memory { get; init; } = 256;

    /// <summary>
    /// 默认网关。内网里的机器要有它，玩家的包才回得去。
    /// </summary>
    /// <remarks>
    /// 跳板机和玩家自己的机器不给 —— 把两边接起来正是玩家要做的事。
    /// </remarks>
    public string? Gateway { get; init; }

    /// <summary>开机时摆在这台机器上的文件，关卡的目标文件就是这么来的。</summary>
    public IReadOnlyList<FileDefinition> Files { get; init; } = [];

    /// <summary>这台机器上对外开的服务。</summary>
    public IReadOnlyList<ServiceDefinition> Services { get; init; } = [];

    /// <summary>
    /// 玩家面前摆着这台机器的终端。
    /// </summary>
    /// <remarks>
    /// <para><b>玩家自己的机器（第一台）总是摆着</b>，不用写。别的机器默认<b>没有</b>：
    /// 玩家要在那台机器上干活，就得自己想办法进去 —— 从自己的终端连过去
    /// （见 <see cref="Access"/>），而不是游戏白送一个 shell。</para>
    /// <para>教学关和特殊关可以打开它：白送 shell 是为了讲清楚某个机制，不是实战关的常态。
    /// 调试时不必改关卡文件，设 <c>GAMEHACKER_ALL_SHELLS=1</c> 就能把所有机器的终端都摆出来。</para>
    /// </remarks>
    public bool Shell { get; init; }

    /// <summary>
    /// 这台机器上开着的远程维护口：玩家从自己的终端连过去，用账号口令登录。
    /// </summary>
    /// <remarks>
    /// 口令每局现生成，关卡文件里不留 —— 和管理员的账号是同一个规矩。
    /// </remarks>
    public AccessDefinition? Access { get; init; }
}

/// <summary>
/// 一台机器上的远程维护口：玩家进这台机器的那条路。
/// </summary>
/// <remarks>
/// <para><b>为什么是它而不是白送终端</b>：跳板机是玩家<b>打进去</b>的，不是他本来就坐在前面的。
/// 界面上只摆玩家自己那台机器的终端，别人的机器一律得从网络上进 —— 这一条是关卡机制的地基，
/// 「开转发」「拿文件」这些步骤才有前提。</para>
/// <para><b>它是真的</b>：客户机上真的跑着一个监听进程，真的读账号口令、真的比对
/// <c>/etc/shadow</c> 里的哈希、真的 <c>su</c> 成那个账号。于是玩家在
/// <c>ps</c>、<c>netstat</c> 里看得见它，管理员也看得见；连错口令会被记进系统日志。</para>
/// <para><b>它没有 pty</b>，就是一条裸套接字上的 shell（老式维护口本来就这样）。
/// 这一点对玩家是可见的：命令照跑，但没有作业控制。</para>
/// </remarks>
public sealed record AccessDefinition
{
    /// <summary>监听端口。</summary>
    public required int Port { get; init; }

    /// <summary>玩家手上那个账号的用户名。口令每局现生成。</summary>
    public required string User { get; init; }

    /// <summary>
    /// 这个账号能不能 sudo。
    /// </summary>
    /// <remarks>
    /// 给 true 的话是免口令的 —— 裸套接字上没有 tty，真的 sudo 问口令时会
    /// 报「no tty present」，那不是关卡想考的东西。真正的提权留给以后的关卡做。
    /// </remarks>
    public bool Sudo { get; init; }
}

/// <summary>摆在机器上的一个文件。</summary>
public sealed record FileDefinition
{
    public required string Path { get; init; }

    /// <summary>文件内容。关卡文件里写什么，客户机上就是什么。</summary>
    public required string Text { get; init; }
}

/// <summary>机器上一个对外开的端口：连上来就把这个文件吐给对方。</summary>
public sealed record ServiceDefinition
{
    public required int Port { get; init; }
    public required string File { get; init; }
}

/// <summary>一块网卡：接在哪个网段、用什么地址。</summary>
public sealed record NicDefinition
{
    public required string Network { get; init; }

    /// <summary>IPv4 地址，不带前缀长度。</summary>
    public required string Ip { get; init; }
}

/// <summary>任务里的一步。</summary>
public sealed record LevelStep
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Hint { get; init; } = "";

    /// <summary>
    /// 这一步什么时候算完成。草稿关里可以先空着，可玩关必须每步都有。
    /// </summary>
    public LevelCheck? Check { get; init; }
}
