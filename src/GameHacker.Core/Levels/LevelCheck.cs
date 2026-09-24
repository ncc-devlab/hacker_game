using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GameHacker.Core.Admin;
using GameHacker.Core.Net;

namespace GameHacker.Core.Levels;

/// <summary>
/// 判定器眼里的这一关：谁的地址是什么、哪个网段在哪个 VLAN、这台机器平时该有什么。
/// </summary>
public sealed class LevelWorld
{
    private readonly Dictionary<string, IPAddress[]> _ips;
    private readonly Dictionary<string, (int Vlan, IPNetwork Subnet)> _networks;

    public LevelWorld(LevelDefinition level)
    {
        Level = level;
        _ips = level.Machines.ToDictionary(m => m.Name, m => m.Nics.Select(n => IPAddress.Parse(n.Ip)).ToArray());
        _networks = level.Networks.ToDictionary(n => n.Name, n => (n.Vlan, IPNetwork.Parse(n.Subnet)));
    }

    public LevelDefinition Level { get; }

    public IPAddress[] IpsOf(string machine) => _ips.GetValueOrDefault(machine, []);

    /// <summary>网段在交换机上的 VLAN 号；没有这个网段时返回 -1，判定就永远不会命中。</summary>
    public int VlanOf(string network) => _networks.TryGetValue(network, out var n) ? n.Vlan : -1;

    public IPNetwork SubnetOf(string network) => _networks.TryGetValue(network, out var n) ? n.Subnet : default;

    /// <summary>这一关里「本来就该有」的东西。管理员和「掩盖」那步用的是同一份。</summary>
    public AdminAllow Allow => Level.Admin?.Allow ?? new AdminAllow();
}

/// <summary>
/// 判定要看的客户机内部状态。
/// </summary>
/// <remarks>
/// <para><b>为什么要有这个东西</b>：网络行为在交换机上看得一清二楚，但「文件到我机器上了没」
/// 「痕迹清干净了没」不是包，交换机永远看不见。这类事只能问客户机自己。</para>
/// <para><b>为什么不是定时轮询</b>：这是一份「当前这步想知道什么」的说明书，不是一个循环。
/// 只有当前步骤需要时它才不为 null，宿主也只在<b>发生了可能改变这些状态的事</b>
/// （玩家在某台机器上敲了一行、交换机上过了包、管理员查完岗）之后才去问一次。
/// 没人动，就不问。</para>
/// </remarks>
/// <param name="Machine">问哪台机器。</param>
public sealed record StateQuery(string Machine)
{
    /// <summary>要进程表。</summary>
    public bool Processes { get; init; }

    /// <summary>要 IPv4 转发开关的状态。</summary>
    public bool Forwarding { get; init; }

    /// <summary>要问「这台机器上有没有内容是这个哈希的文件」。</summary>
    public string? FileSha { get; init; }
}

/// <summary>
/// 客户机答回来的状态。
/// </summary>
/// <remarks>
/// <b>没问到的那几样是 null，不是空。</b> 一份只回答了「有没有那个文件」的快照，
/// 它的进程表是「没问」而不是「一个进程都没有」—— 不分清楚的话，
/// 「痕迹清干净了没」会把别人的答复当成「这台机器上什么都没跑」，当场判过。
/// </remarks>
public sealed record StateSnapshot(string Machine)
{
    public IReadOnlyList<ProcessLine>? Processes { get; init; }
    public bool? Forwarding { get; init; }

    /// <summary>
    /// 这份答复回的是哪个哈希的问题。
    /// </summary>
    /// <remarks>
    /// 要带上它：一步里可以同时挂好几个 <see cref="FileCheck"/>（比如「两份文件都要拿到」），
    /// 少了这个字段，其中一份的「找到了」会被另一份认领。
    /// </remarks>
    public string? FileSha { get; init; }

    public bool FileFound { get; init; }
}

/// <summary>
/// 一步的判定状态机。
/// </summary>
/// <remarks>
/// 检测原语大多不是「看一眼就知道」的：扫描要在一段时间里数够多少台主机，
/// 隧道要同时看到来和回。这些跨事件的记忆放在这里，而 <see cref="LevelCheck"/>
/// 本身保持不可变 —— 它是关卡文件里的一段数据，可以被复制、被多处引用。
/// 进入某一步时新建一个，退出这一步就扔掉。
/// </remarks>
public abstract class CheckTracker
{
    /// <summary>交换机上过了一帧。</summary>
    public virtual bool Observe(PacketRecord packet) => false;

    /// <summary>问回来的客户机状态。</summary>
    public virtual bool Observe(StateSnapshot state) => false;

    /// <summary>管理员查完了一次岗。</summary>
    public virtual bool Observe(PatrolReport patrol) => false;

    /// <summary>
    /// 此刻还想知道的客户机内部状态。不看客户机的原语返回空。
    /// </summary>
    /// <remarks>
    /// 放在 tracker 上而不是 <see cref="LevelCheck"/> 上，是因为它会变：
    /// 组合条件里已经满足的那几项不必再问，问了也是白跑一趟客户机。
    /// </remarks>
    public virtual IReadOnlyList<StateQuery> Wanted => [];

    /// <summary>还差什么。给 HUD 用的一句话，null 表示没什么好提示的。</summary>
    public virtual string? Remaining => null;
}

/// <summary>
/// 检测原语。检的是「玩家达成了什么状态」，不是「玩家敲了什么命令」
/// （MVP2 文档第三节）。
/// </summary>
/// <remarks>
/// <para><b>它们是零件，不是某一关的逻辑。</b> 每个原语只描述一件与关卡内容无关的事
/// （「这台机器的包进了那个网段」「有人在挨个试地址」「这台机器上有内容是这个哈希的文件」），
/// 关卡文件把它们拼起来用。第二关要「探测玩家有没有发过某个包」「有没有扫到目标」
/// 「有没有把文件拿到手」时，直接挑现成的写进 JSON 即可，不必再写一行 C#。
/// 现有原语见 <see cref="CheckTypes"/>，那份清单也是文档的来源。</para>
/// <para>三条通路：网络行为看交换机（<see cref="CheckTracker.Observe(PacketRecord)"/>），
/// 客户机内部状态经隐藏控制通道问（<see cref="CheckTracker.Wanted"/>），
/// 还有一条世界之内的：让管理员来验收（<see cref="CheckTracker.Observe(PatrolReport)"/>）。</para>
/// <para>一步要好几个条件时不必新写原语，用 <see cref="AllCheck"/> / <see cref="AnyCheck"/>
/// 拼；它们可以任意嵌套。</para>
/// <para>新原语加一个子类、挂一条 <see cref="JsonDerivedTypeAttribute"/>，
/// 实现自己的 <see cref="CreateTracker"/>，再在 <see cref="CheckTypes"/> 里登记一条
/// （有测试盯着，漏登记会红）。关卡文件里写错类型名会在加载时报出来。</para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
                 UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(PingCheck), "ping")]
[JsonDerivedType(typeof(TrafficCheck), "traffic")]
[JsonDerivedType(typeof(RouteCheck), "route")]
[JsonDerivedType(typeof(ScanCheck), "scan")]
[JsonDerivedType(typeof(FileCheck), "file")]
[JsonDerivedType(typeof(ProcessCheck), "process")]
[JsonDerivedType(typeof(CleanCheck), "clean")]
[JsonDerivedType(typeof(PatrolCheck), "patrol")]
[JsonDerivedType(typeof(AllCheck), "all")]
[JsonDerivedType(typeof(AnyCheck), "any")]
public abstract record LevelCheck
{
    /// <summary>引用到的机器名，加载时校验它们都存在。</summary>
    public virtual IEnumerable<string> MachineRefs => Children.SelectMany(c => c.MachineRefs);

    /// <summary>引用到的网段名，加载时校验它们都存在。</summary>
    public virtual IEnumerable<string> NetworkRefs => Children.SelectMany(c => c.NetworkRefs);

    /// <summary>组合条件里套着的那些。校验和引用检查会顺着它往下走。</summary>
    public virtual IReadOnlyList<LevelCheck> Children => [];

    public abstract CheckTracker CreateTracker(LevelWorld world);
}

/// <summary>
/// <c>From</c> 成功 ping 通了 <c>To</c>：交换机上看到 <c>To</c> 回给 <c>From</c>
/// 的 ICMP echo 应答。
/// </summary>
/// <remarks>
/// 走交换机而不是让客户机自己去 ping —— 后者会在抓包面板上凭空冒出
/// 玩家没敲过的 ping，也会让管理员看到判定器自己的流量。
/// 看应答而不是请求：请求发出去不代表网络是通的。
/// </remarks>
public sealed record PingCheck : LevelCheck
{
    public required string From { get; init; }
    public required string To { get; init; }

    public override IEnumerable<string> MachineRefs => [From, To];

    public override CheckTracker CreateTracker(LevelWorld world) => new Tracker(this, world);

    public bool Matches(PacketRecord packet, LevelWorld world) =>
        packet.Protocol == "ICMP"
        && PacketInspector.TryGetIcmpEchoReply(packet.Bytes, out var source, out var destination)
        && world.IpsOf(To).Contains(source)
        && world.IpsOf(From).Contains(destination);

    private sealed class Tracker(PingCheck check, LevelWorld world) : CheckTracker
    {
        public override bool Observe(PacketRecord packet) => check.Matches(packet, world);
        public override string Remaining => $"等 {check.From} ping 通 {check.To}";
    }
}

/// <summary>
/// <c>From</c> 的包能进 <c>Network</c>，而且回得来 —— 隧道通了。
/// </summary>
/// <remarks>
/// <para><b>为什么只看源地址就够</b>：<c>From</c> 那块网卡根本不在这个 VLAN 上，
/// 交换机二层也不会把它的帧送过去。所以内网 VLAN 里一旦出现源地址是它的 IPv4 包，
/// 只可能是中间那台两头都插着网卡的机器转过来的。这就是隧道存在的铁证，
/// 而且不关心玩家是用路由转发、端口转发还是别的什么手段做到的。</para>
/// <para><b>为什么要看回程</b>：包进得去不等于通。单看去程的话，玩家在跳板机上
/// 打开转发、内网却没人认识他的网段，判定也会亮 —— 然后下一步扫描怎么也做不出来。</para>
/// </remarks>
public sealed record RouteCheck : LevelCheck
{
    /// <summary>谁的包要出现在那边。</summary>
    public required string From { get; init; }

    /// <summary>要通到哪个网段。</summary>
    public required string Network { get; init; }

    public override IEnumerable<string> MachineRefs => [From];
    public override IEnumerable<string> NetworkRefs => [Network];

    public override CheckTracker CreateTracker(LevelWorld world) => new Tracker(this, world);

    private sealed class Tracker(RouteCheck check, LevelWorld world) : CheckTracker
    {
        private readonly int _vlan = world.VlanOf(check.Network);
        private readonly IPAddress[] _ips = world.IpsOf(check.From);
        private bool _out, _back;

        public override bool Observe(PacketRecord packet)
        {
            if (packet.Vlan != _vlan
                || !PacketInspector.TryGetIpv4(packet.Bytes, out var source, out var destination))
                return false;

            if (_ips.Contains(source)) _out = true;
            if (_ips.Contains(destination)) _back = true;
            return _out && _back;
        }

        public override string Remaining => (_out, _back) switch
        {
            (false, _) => $"{check.Network} 里还没出现过 {check.From} 的包",
            (true, false) => $"{check.From} 的包进去了，但没有回程 —— 内网那边不认识回去的路",
            _ => "",
        };
    }
}

/// <summary>
/// 有人在 <c>Network</c> 里挨个试地址：一段时间内问候了 <c>Hosts</c> 台以上不同的主机。
/// </summary>
/// <remarks>
/// <para><b>不认发起者</b>。扫描的特征是「在这个网段里挨个问『有人吗』」，
/// 至于是玩家的机器直接扫、还是他在跳板机上扫，都是扫。而且真要认也认不准：
/// 玩家的包经跳板机转发进来时，替他挨家挨户敲门（ARP）的是跳板机。</para>
/// <para><b>ARP 必须算</b>。扫一个 /24 而里面只有一台机器时，唯一一个有回应的
/// 地址才会产生 IP 包，其余 250 多次试探全是没人应答的 ARP 询问 ——
/// 只数 IP 包的话，最像扫描的那部分恰好全被漏掉。</para>
/// <para><b>要在窗口内数够</b>：一台一台慢慢连是正常使用，短时间内扫一片才是扫描。</para>
/// </remarks>
public sealed record ScanCheck : LevelCheck
{
    public required string Network { get; init; }

    /// <summary>要试过多少台不同的主机才算扫描。</summary>
    public int Hosts { get; init; } = 8;

    /// <summary>这些试探要落在多长的时间窗口里。</summary>
    public int WithinSeconds { get; init; } = 60;

    /// <summary>
    /// 还得真的扫到这台机器：它在窗口里应过答。不写就只要求「扫过」，不要求「扫到」。
    /// </summary>
    public string? Finds { get; init; }

    public override IEnumerable<string> MachineRefs => Finds is null ? [] : [Finds];
    public override IEnumerable<string> NetworkRefs => [Network];

    public override CheckTracker CreateTracker(LevelWorld world) => new Tracker(this, world);

    private sealed class Tracker(ScanCheck check, LevelWorld world) : CheckTracker
    {
        private readonly int _vlan = world.VlanOf(check.Network);
        private readonly IPNetwork _subnet = world.SubnetOf(check.Network);
        private readonly IPAddress[] _target = check.Finds is null ? [] : world.IpsOf(check.Finds);
        private readonly Dictionary<IPAddress, TimeSpan> _probed = [];
        private int _peak;
        private bool _found;

        public override bool Observe(PacketRecord packet)
        {
            if (packet.Vlan != _vlan) return false;

            // 目标应了答 = 真的被扫到了。它自己发出来的包不算试探，所以先看这一头
            if (check.Finds is not null && !_found
                && (PacketInspector.TryGetIpv4(packet.Bytes, out var replier, out _)
                    || PacketInspector.TryGetArp(packet.Bytes, out replier, out _))
                && _target.Contains(replier))
                _found = true;

            IPAddress target;
            if (PacketInspector.TryGetArp(packet.Bytes, out _, out var asked)) target = asked;
            else if (PacketInspector.TryGetIpv4(packet.Bytes, out _, out var destination)) target = destination;
            else return false;

            if (!_subnet.Contains(target)) return false;

            _probed[target] = packet.Elapsed;
            var window = TimeSpan.FromSeconds(check.WithinSeconds);
            foreach (var stale in _probed.Where(p => packet.Elapsed - p.Value > window).ToList())
                _probed.Remove(stale.Key);

            _peak = Math.Max(_peak, _probed.Count);
            return _probed.Count >= check.Hosts && (check.Finds is null || _found);
        }

        public override string Remaining =>
            $"{check.WithinSeconds} 秒内试过 {_peak}/{check.Hosts} 台内网主机"
            + (check.Finds is null || _found ? "" : $"，还没扫到 {check.Finds}");
    }
}

/// <summary>
/// <c>Machine</c> 上存着一份内容是 <c>Sha256</c> 的文件 —— 东西到手了。
/// </summary>
/// <remarks>
/// <para><b>为什么不看交换机</b>：文件躺在磁盘上不是包。看传输过程也不对 ——
/// 玩家可以把它 cat 到屏幕上看一眼就算完，那不叫「拿到了」；而将来传输一旦加密，
/// 交换机连看都看不见。所以这一项只能问客户机本人。</para>
/// <para><b>为什么用内容哈希</b>：玩家怎么拿、存成什么名字、放在哪个目录都随意，
/// 只认东西对不对。顺带也挡住了「自己造一个同名空文件」这种走捷径的办法。</para>
/// </remarks>
public sealed record FileCheck : LevelCheck
{
    public required string Machine { get; init; }

    /// <summary>目标文件内容的 sha256，64 位小写十六进制。</summary>
    public required string Sha256 { get; init; }

    public override IEnumerable<string> MachineRefs => [Machine];

    public override CheckTracker CreateTracker(LevelWorld world) => new Tracker(this);

    private sealed class Tracker(FileCheck check) : CheckTracker
    {
        public override IReadOnlyList<StateQuery> Wanted => [new(check.Machine) { FileSha = check.Sha256 }];

        // 认准回的是自己问的那个哈希：一步里可以同时挂好几份文件
        public override bool Observe(StateSnapshot state) =>
            state.Machine == check.Machine && state.FileSha == check.Sha256 && state.FileFound;

        public override string Remaining => $"{check.Machine} 上还没有那份文件";
    }
}

/// <summary>
/// <c>Machine</c> 的进程表里有这样一个进程 —— 有人在那台机器上。
/// </summary>
/// <remarks>
/// <para><b><see cref="CleanCheck"/> 的正面。</b> 「清干净了」问的是「白名单外什么都没剩」，
/// 这一个问的是「确实有这么一个东西在跑」。玩家远程登录进了跳板机、在目标机上留了个后门、
/// 某个服务确实被他起起来了 —— 都是它。</para>
/// <para><b>为什么用进程表认「玩家进去了」</b>：远程登录进去以后，那台机器上真的多了一个属于
/// 那个账号的 shell。这是玩家做到了那件事的结果，不是「他敲了什么命令」 ——
/// 走哪条路进去的、用不用得着我们预设的那个维护口，判定都不关心。</para>
/// <para><c>User</c> 和 <c>Command</c> 都写的话要同时满足。<c>Command</c> 可以带 <c>*</c>。</para>
/// </remarks>
public sealed record ProcessCheck : LevelCheck
{
    public required string Machine { get; init; }

    /// <summary>进程属于这个账号。不写就不限。</summary>
    public string? User { get; init; }

    /// <summary>整条命令要长这样，可以带 <c>*</c>。不写就不限。</summary>
    public string? Command { get; init; }

    /// <summary>要同时有这么多条。默认一条。</summary>
    public int Times { get; init; } = 1;

    public override IEnumerable<string> MachineRefs => [Machine];

    public override CheckTracker CreateTracker(LevelWorld world) => new Tracker(this);

    private sealed class Tracker(ProcessCheck check) : CheckTracker
    {
        private readonly Regex? _command = check.Command is null ? null
            : new Regex("^" + Regex.Escape(check.Command).Replace("\\*", ".*") + "$");
        private string _remaining = "";

        public override IReadOnlyList<StateQuery> Wanted => [new(check.Machine) { Processes = true }];

        public override bool Observe(StateSnapshot state)
        {
            if (state.Machine != check.Machine || state.Processes is null) return false;

            int found = state.Processes.Count(p =>
                (check.User is null || p.IsUser(check.User))
                && (_command is null || _command.IsMatch(p.Command)));
            _remaining = found >= check.Times ? ""
                : $"{check.Machine} 上还没有{Describe()}（找到 {found}/{check.Times}）";
            return found >= check.Times;
        }

        private string Describe() => (check.User, check.Command) switch
        {
            (null, null) => "任何进程",
            ({ } u, null) => $"属于 {u} 的进程",
            (null, { } c) => $"这样的进程：{c}",
            var (u, c) => $"属于 {u} 的这样的进程：{c}",
        };

        public override string Remaining => _remaining;
    }
}

/// <summary>
/// <c>Machine</c> 上看不出有人来过：白名单外的进程没了，转发也关回去了。
/// </summary>
/// <remarks>
/// <para><b>和管理员用同一份白名单</b>（<see cref="LevelWorld.Allow"/>）。玩家的通关条件
/// 和对手的判断标准必须是同一个 —— 否则会出现「系统说你清干净了，管理员却还是把你抓了」。</para>
/// <para><b>日志被删短了不在这里管</b>。那是管理员的记性：他从进关就一直在看，
/// 记得上次有多少行。删日志不会卡住这一步，但会让他起疑，最后栽在下一步「隐蔽」上 ——
/// 这正是「掩盖」和「删除」的区别，交给人去发现比写成判定条件更贴题。</para>
/// </remarks>
public sealed record CleanCheck : LevelCheck
{
    public required string Machine { get; init; }

    public override IEnumerable<string> MachineRefs => [Machine];

    public override CheckTracker CreateTracker(LevelWorld world) => new Tracker(this, world);

    private sealed class Tracker(CleanCheck check, LevelWorld world) : CheckTracker
    {
        private readonly ProcessWhitelist _whitelist = new(world.Allow.Processes);
        private string _remaining = "";

        public override IReadOnlyList<StateQuery> Wanted =>
            [new(check.Machine) { Processes = true, Forwarding = true }];

        public override bool Observe(StateSnapshot state)
        {
            // 这份答复得真的带着我们要的那两样，否则它说明不了任何事
            if (state.Machine != check.Machine || state.Processes is null || state.Forwarding is null)
                return false;

            var left = state.Processes.Where(p => !_whitelist.Allows(p)).Select(p => p.Command).ToList();
            if (state.Forwarding is true) left.Add("转发还开着（/proc/sys/net/ipv4/ip_forward）");

            _remaining = left.Count == 0 ? "" : "还留着：" + string.Join("、", left);
            return left.Count == 0;
        }

        public override string Remaining => _remaining;
    }
}

/// <summary>
/// 管理员来查了 <c>Patrols</c> 次岗，一次也没看出什么 —— 你确实没被注意到。
/// </summary>
/// <remarks>
/// <b>让对手来验收。</b> 「隐蔽」本来就不是玩家能自己宣布完成的事，
/// 而且这一项不需要任何额外的探查：管理员本来就会按自己的节奏登录、翻看。
/// 判定只是听他的结论，一次多余的轮询都没有。
/// </remarks>
public sealed record PatrolCheck : LevelCheck
{
    /// <summary>要连着几次查岗都干干净净。</summary>
    public int Patrols { get; init; } = 1;

    public override IEnumerable<string> MachineRefs => [];

    public override CheckTracker CreateTracker(LevelWorld world) => new Tracker(this);

    private sealed class Tracker(PatrolCheck check) : CheckTracker
    {
        private int _clean;

        public override bool Observe(PatrolReport patrol)
        {
            // 他什么都没看的那次不算数（阶段刚切换、动作被抽空）
            if (patrol.Did.Count == 0) return false;
            // 看的是「这次什么都没看见」，不是「没有新发现」：同一处痕迹只算一次怀疑度，
            // 他第二次看见那个还在跑的进程时新发现是空的 —— 但玩家显然没全身而退
            _clean = patrol.Clean ? _clean + 1 : 0;
            return _clean >= check.Patrols;
        }

        public override string Remaining => $"等管理员查岗，连着 {_clean}/{check.Patrols} 次没发现异常";
    }
}

/// <summary>
/// 交换机上出现过这样的包：<c>Times</c> 次以上。
/// </summary>
/// <remarks>
/// <para>最通用的那个网络原语。上面那些（<see cref="PingCheck"/>、<see cref="RouteCheck"/>）
/// 说的是「通没通」这类有讲究的事；这一个只管「有没有这样的包飞过」，
/// 用来表达「玩家有没有连过那个端口」「有没有对着目标发过东西」。</para>
/// <para>字段都可以不写，不写就是不限。全不写的话任何一帧都算数 —— 加载时会拦下来。</para>
/// </remarks>
public sealed record TrafficCheck : LevelCheck
{
    /// <summary>源是哪台机器（它的任何一个地址都算）。</summary>
    public string? From { get; init; }

    /// <summary>目的是哪台机器。</summary>
    public string? To { get; init; }

    /// <summary>限定在哪个网段里看。</summary>
    public string? Network { get; init; }

    /// <summary><c>ICMP</c> / <c>TCP</c> / <c>UDP</c> / <c>ARP</c>。</summary>
    public string? Protocol { get; init; }

    /// <summary>目的端口，TCP / UDP 才有。</summary>
    public int? Port { get; init; }

    /// <summary>要看到几次。</summary>
    public int Times { get; init; } = 1;

    public override IEnumerable<string> MachineRefs =>
        new[] { From, To }.Where(m => m is not null)!;

    public override IEnumerable<string> NetworkRefs => Network is null ? [] : [Network];

    /// <summary>一个条件都没写的话，它会对任何一帧亮 —— 这一定是写关卡的人漏了字段。</summary>
    public bool IsEmpty => From is null && To is null && Protocol is null && Port is null;

    public override CheckTracker CreateTracker(LevelWorld world) => new Tracker(this, world);

    private sealed class Tracker(TrafficCheck check, LevelWorld world) : CheckTracker
    {
        private readonly int _vlan = check.Network is null ? -1 : world.VlanOf(check.Network);
        private readonly IPAddress[] _from = check.From is null ? [] : world.IpsOf(check.From);
        private readonly IPAddress[] _to = check.To is null ? [] : world.IpsOf(check.To);
        private int _seen;

        public override bool Observe(PacketRecord packet)
        {
            if (check.Network is not null && packet.Vlan != _vlan) return false;
            if (check.Protocol is not null
                && !packet.Protocol.Equals(check.Protocol, StringComparison.OrdinalIgnoreCase)) return false;

            // ARP 的地址在它自己的字段里，不在 IP 头上
            if (!PacketInspector.TryGetIpv4(packet.Bytes, out var source, out var destination)
                && !PacketInspector.TryGetArp(packet.Bytes, out source, out destination))
                return false;

            if (check.From is not null && !_from.Contains(source)) return false;
            if (check.To is not null && !_to.Contains(destination)) return false;
            if (check.Port is { } port
                && (!PacketInspector.TryGetPorts(packet.Bytes, out _, out int actual) || actual != port))
                return false;

            return ++_seen >= check.Times;
        }

        public override string Remaining =>
            check.Times > 1 ? $"这样的包看到了 {_seen}/{check.Times} 个" : "";
    }
}

/// <summary>
/// 里面的条件<b>全部</b>满足。
/// </summary>
/// <remarks>
/// <para>各条件各自记账、互不干扰，而且<b>不要求同时成立</b>：先满足的那条会记着，
/// 不会因为后来状态变了又掉回去。一步里「既要拿到文件，又要没留下痕迹」就是这个形状。</para>
/// <para>已经满足的那几条不再往客户机要状态（见 <see cref="CheckTracker.Wanted"/>）。</para>
/// </remarks>
public sealed record AllCheck : LevelCheck
{
    public required IReadOnlyList<LevelCheck> Checks { get; init; }

    public override IReadOnlyList<LevelCheck> Children => Checks;

    public override CheckTracker CreateTracker(LevelWorld world) =>
        new Tracker([.. Checks.Select(c => c.CreateTracker(world))]);

    private sealed class Tracker(IReadOnlyList<CheckTracker> parts) : CheckTracker
    {
        private readonly bool[] _done = new bool[parts.Count];

        private bool Feed(Func<CheckTracker, bool> observe)
        {
            for (int i = 0; i < parts.Count; i++)
                if (!_done[i] && observe(parts[i]))
                    _done[i] = true;
            return _done.All(d => d);
        }

        public override bool Observe(PacketRecord packet) => Feed(t => t.Observe(packet));
        public override bool Observe(StateSnapshot state) => Feed(t => t.Observe(state));
        public override bool Observe(PatrolReport patrol) => Feed(t => t.Observe(patrol));

        public override IReadOnlyList<StateQuery> Wanted =>
            [.. parts.Where((_, i) => !_done[i]).SelectMany(t => t.Wanted).Distinct()];

        public override string Remaining => string.Join("；",
            parts.Where((_, i) => !_done[i])
                 .Select(t => t.Remaining)
                 .Where(r => !string.IsNullOrEmpty(r)));
    }
}

/// <summary>
/// 里面的条件满足<b>任意一条</b>即可。
/// </summary>
/// <remarks>
/// 一件事有好几种正当做法时用它 —— 比如「文件拿到自己机器上，或者拿到跳板机上也行」。
/// 这比把判定放宽到「随便什么都算」要好：每条路都是明写出来的。
/// </remarks>
public sealed record AnyCheck : LevelCheck
{
    public required IReadOnlyList<LevelCheck> Checks { get; init; }

    public override IReadOnlyList<LevelCheck> Children => Checks;

    public override CheckTracker CreateTracker(LevelWorld world) =>
        new Tracker([.. Checks.Select(c => c.CreateTracker(world))]);

    private sealed class Tracker(IReadOnlyList<CheckTracker> parts) : CheckTracker
    {
        // 每条都要喂到：它们各自攒着自己的账（比如扫描的时间窗口），
        // 短路求值会让没被喂的那条永远停在半路
        private bool Feed(Func<CheckTracker, bool> observe)
        {
            bool hit = false;
            foreach (var part in parts) hit |= observe(part);
            return hit;
        }

        public override bool Observe(PacketRecord packet) => Feed(t => t.Observe(packet));
        public override bool Observe(StateSnapshot state) => Feed(t => t.Observe(state));
        public override bool Observe(PatrolReport patrol) => Feed(t => t.Observe(patrol));

        public override IReadOnlyList<StateQuery> Wanted => [.. parts.SelectMany(t => t.Wanted).Distinct()];

        public override string Remaining => string.Join(" 或 ",
            parts.Select(t => t.Remaining).Where(r => !string.IsNullOrEmpty(r)));
    }
}
