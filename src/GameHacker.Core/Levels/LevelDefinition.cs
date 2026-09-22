using System.Net;
using System.Text.Json.Serialization;
using GameHacker.Core.Net;

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

/// <summary>
/// 检测原语。检的是「玩家达成了什么状态」，不是「玩家敲了什么命令」
/// （MVP2 文档第三节）。
/// </summary>
/// <remarks>
/// 新原语加一个子类、挂一条 <see cref="JsonDerivedTypeAttribute"/>，
/// 再在 <see cref="LevelRun"/> 里接上即可。关卡文件里写错类型名会在加载时报出来。
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
                 UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(PingCheck), "ping")]
public abstract record LevelCheck
{
    /// <summary>引用到的机器名，加载时校验它们都存在。</summary>
    public abstract IEnumerable<string> MachineRefs { get; }
}

/// <summary>
/// <c>From</c> 成功 ping 通了 <c>To</c>：交换机上看到 <c>To</c> 回给 <c>From</c>
/// 的 ICMP echo 应答。
/// </summary>
/// <remarks>
/// 走交换机而不是让客户机自己去 ping —— 后者会在抓包面板上凭空冒出
/// 玩家没敲过的 ping，也会让管理员（将来）看到判定器自己的流量。
/// 看应答而不是请求：请求发出去不代表网络是通的。
/// </remarks>
public sealed record PingCheck : LevelCheck
{
    public required string From { get; init; }
    public required string To { get; init; }

    public override IEnumerable<string> MachineRefs => [From, To];

    /// <param name="ipsOf">每台机器的全部地址（多网卡的机器有好几个）。</param>
    public bool Matches(PacketRecord packet, IReadOnlyDictionary<string, IPAddress[]> ipsOf) =>
        packet.Protocol == "ICMP"
        && PacketInspector.TryGetIcmpEchoReply(packet.Bytes, out var source, out var destination)
        && ipsOf[To].Contains(source)
        && ipsOf[From].Contains(destination);
}
