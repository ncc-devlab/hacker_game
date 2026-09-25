using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GameHacker.Core.Admin;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Levels;

/// <summary>关卡文件有问题。消息里逐条列出是哪个文件、哪里错了。</summary>
public sealed class LevelFormatException(IReadOnlyList<string> errors)
    : Exception("关卡文件有误:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// 全部关卡：解析、校验、排序。
/// </summary>
/// <remarks>
/// <para>只收文本不碰文件系统 —— 导出后关卡文件在 <c>.pck</c> 里，
/// 只有 Godot 读得到；测试则直接读仓库目录。两边各自读好了再交进来。</para>
/// <para>校验放在加载时一次做完、错误攒齐一起报：关卡作者改一个 JSON
/// 最怕的是进了关才发现引用的机器名拼错了。</para>
/// </remarks>
public sealed partial class LevelCatalog
{
    /// <summary>
    /// 关卡文件的解析口径。
    /// </summary>
    /// <remarks>公开是为了让别处（比如原语清单里的例子）能按同一口径解析一小段关卡片段。</remarks>
    public static JsonSerializerOptions JsonOptions => Json;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
        // 拼错的字段名直接报错，而不是静悄悄变成默认值
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, LevelDefinition> _byId;

    private LevelCatalog(List<LevelDefinition> levels)
    {
        Levels = levels.OrderBy(l => l.Track).ThenBy(l => l.Order).ThenBy(l => l.Id, StringComparer.Ordinal).ToList();
        _byId = Levels.ToDictionary(l => l.Id);
    }

    /// <summary>按内容线、再按 <see cref="LevelDefinition.Order"/> 排好的全部关卡。</summary>
    public IReadOnlyList<LevelDefinition> Levels { get; }

    public LevelDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>解析一组关卡文件。任何一处有错都抛 <see cref="LevelFormatException"/>。</summary>
    /// <param name="files">(来源名, JSON 文本)。来源名只用于报错。</param>
    public static LevelCatalog Parse(IEnumerable<(string Source, string Json)> files)
    {
        var errors = new List<string>();
        var levels = new List<(string Source, LevelDefinition Level)>();

        foreach (var (source, json) in files)
        {
            try
            {
                var level = JsonSerializer.Deserialize<LevelDefinition>(json, Json);
                if (level is null) errors.Add($"{source}: 内容为空");
                else levels.Add((source, level));
            }
            catch (JsonException ex)
            {
                errors.Add($"{source}: {ex.Message}");
            }
        }

        var seen = new Dictionary<string, string>();
        foreach (var (source, level) in levels)
        {
            if (seen.TryGetValue(level.Id, out string? other))
                errors.Add($"{source}: id \"{level.Id}\" 与 {other} 重复");
            else seen[level.Id] = source;
            Validate(source, level, errors);
        }

        foreach (var (source, level) in levels)
            foreach (string req in level.Requires)
                if (!seen.ContainsKey(req))
                    errors.Add($"{source}: requires 里的 \"{req}\" 不存在");

        if (errors.Count == 0 && FindCycle(levels.Select(l => l.Level).ToList()) is { } cycle)
            errors.Add($"解锁条件成环: {string.Join(" → ", cycle)}，这几关谁都解不开");

        if (errors.Count > 0) throw new LevelFormatException(errors);
        return new LevelCatalog(levels.Select(l => WithHouseKeeping(l.Level)).ToList());
    }

    /// <summary>
    /// 机器自带的那些东西，补进管理员的白名单。
    /// </summary>
    /// <remarks>
    /// <para>维护口的监听进程、经它登录进来的会话、关卡摆的那个备份服务 —— 都是
    /// <b>这台机器本来的样子</b>，不是玩家带来的。关卡作者不该再手抄一遍：抄漏了，
    /// 管理员就会为机器自带的东西把玩家抓了；端口一改，手抄的那份还会悄悄失效。</para>
    /// <para>「掩盖」那一步和管理员用的是同一份白名单（<see cref="LevelWorld.Allow"/>），
    /// 所以这里补一次，两边都对得上。</para>
    /// </remarks>
    private static LevelDefinition WithHouseKeeping(LevelDefinition level)
    {
        if (level.Admin is not { } admin) return level;
        if (level.Machines.FirstOrDefault(m => m.Name == admin.Machine) is not { } machine) return level;

        var processes = admin.Allow.Processes.ToList();
        var ports = admin.Allow.Ports.ToList();
        var sessionUsers = admin.Allow.SessionUsers.ToList();
        int before = processes.Count + ports.Count + sessionUsers.Count;

        foreach (var service in machine.Services)
        {
            processes.Add($"nc -lk -p {service.Port} *");
            ports.Add($"tcp/{service.Port}");
        }
        if (machine.Access is { } access)
        {
            processes.Add($"nc -lk -p {access.Port} *");
            ports.Add($"tcp/{access.Port}");
            // 维护口在服务端开 pty 的 script 以 root 跑、自己没有终端（终端归它开给会话的
            // 那个 pty），落不到会话终端上，单独放行它这条固定调用
            processes.Add("script -q -E never *");
            // 玩家从维护口登录进来干活正是这个口的用途，本身不算痕迹：连他整条会话
            //（服务端开 pty 的 script/su、里面那个 shell）一起放过。真正会暴露他的是
            // 留下的东西 —— 转发没关、工具没删、端口没收
            sessionUsers.Add(access.User);
        }

        return before == processes.Count + ports.Count + sessionUsers.Count
            ? level
            : level with { Admin = admin with { Allow = admin.Allow with
                { Processes = processes, Ports = ports, SessionUsers = sessionUsers } } };
    }

    private static void Validate(string source, LevelDefinition level, List<string> errors)
    {
        void Error(string message) => errors.Add($"{source}: {message}");

        if (!IdPattern().IsMatch(level.Id))
            Error($"id \"{level.Id}\" 只能用小写字母、数字和连字符");
        if (level.Requires.Contains(level.Id))
            Error("requires 里有自己");

        var networks = new Dictionary<string, IPNetwork>();
        var vlans = new HashSet<int>();
        foreach (var n in level.Networks)
        {
            if (networks.ContainsKey(n.Name)) { Error($"网段名 \"{n.Name}\" 重复"); continue; }
            if (n.Vlan is < 1 or > 4094) Error($"网段 {n.Name} 的 VLAN {n.Vlan} 不在 1..4094");
            else if (!vlans.Add(n.Vlan)) Error($"VLAN {n.Vlan} 重复");
            // .NET 8 的 IPNetwork.TryParse 对 10.0.0.1/24 这种主机位不为零的写法照单全收，
            // 所以要自己比一下：写的地址必须就是网络地址
            if (!IPNetwork.TryParse(n.Subnet, out var net) || net.BaseAddress.AddressFamily != AddressFamily.InterNetwork)
                Error($"网段 {n.Name} 的 subnet \"{n.Subnet}\" 不是形如 10.0.0.0/24 的 IPv4 网段");
            else if (net.BaseAddress.ToString() != n.Subnet.Split('/')[0] || !net.Contains(net.BaseAddress))
                Error($"网段 {n.Name} 的 subnet \"{n.Subnet}\" 主机位不为零，应写成网络地址");
            else if (net.PrefixLength is < 8 or > 30)
                Error($"网段 {n.Name} 的前缀 /{net.PrefixLength} 不在 /8../30");
            else networks[n.Name] = net;
        }

        var names = new HashSet<string>();
        var ips = new HashSet<IPAddress>();
        foreach (var m in level.Machines)
        {
            if (!names.Add(m.Name)) Error($"机器名 \"{m.Name}\" 重复");
            if (m.Nics.Count is < 1 or > QemuLauncher.MaxNics)
                Error($"机器 {m.Name} 有 {m.Nics.Count} 块网卡，要在 1..{QemuLauncher.MaxNics}");
            var joined = new HashSet<string>();
            foreach (var nic in m.Nics)
            {
                string where = $"机器 {m.Name} 的网卡 {nic.Ip}";
                if (!IPAddress.TryParse(nic.Ip, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                {
                    Error($"机器 {m.Name} 的 ip \"{nic.Ip}\" 不是 IPv4 地址");
                    continue;
                }
                if (!ips.Add(ip)) Error($"ip {nic.Ip} 重复");
                if (!joined.Add(nic.Network))
                    Error($"机器 {m.Name} 有两块网卡接在同一个网段 {nic.Network}");
                if (!networks.TryGetValue(nic.Network, out var net))
                {
                    if (level.Networks.All(n => n.Name != nic.Network))
                        Error($"{where} 接的网段 \"{nic.Network}\" 不存在");
                    continue;
                }
                if (!net.Contains(ip)) Error($"{where} 不在网段 {nic.Network}（{net}）里");
                else if (ip.Equals(net.BaseAddress) || ip.Equals(Broadcast(net)))
                    Error($"{where} 是网段 {nic.Network} 的网络地址或广播地址");
            }
            if (m.Gateway is { } gateway
                && (!IPAddress.TryParse(gateway, out var gatewayIp)
                    || !m.Nics.Any(n => networks.TryGetValue(n.Network, out var net) && net.Contains(gatewayIp))))
                Error($"机器 {m.Name} 的网关 \"{gateway}\" 不在它任何一块网卡所在的网段里");
            foreach (var f in m.Files)
                if (!f.Path.StartsWith('/'))
                    Error($"机器 {m.Name} 上的文件路径 \"{f.Path}\" 要写绝对路径");
            foreach (var s in m.Services)
            {
                if (s.Port is < 1 or > 65535) Error($"机器 {m.Name} 的服务端口 {s.Port} 不在 1..65535");
                if (m.Files.All(f => f.Path != s.File))
                    Error($"机器 {m.Name} 的服务要提供 \"{s.File}\"，但这台机器上没摆这个文件");
            }
            if (m.Access is { } access)
            {
                if (access.Port is < 1 or > 65535) Error($"机器 {m.Name} 的维护口端口 {access.Port} 不在 1..65535");
                else if (m.Services.Any(s => s.Port == access.Port))
                    Error($"机器 {m.Name} 的维护口和服务抢同一个端口 {access.Port}");
                if (!UserPattern().IsMatch(access.User))
                    Error($"机器 {m.Name} 维护口的账号 \"{access.User}\" 不是合法的用户名");
                // 白送 shell 还开维护口，等于让玩家白跑一趟：他坐在这台机器前面，不必再登录。
                // 第一台是玩家自己的机器，终端总是摆着的
                if (m.Shell || m.Name == level.Machines[0].Name)
                    Error($"机器 {m.Name} 的终端本来就摆在玩家面前，不该再开维护口 —— 二选一");
            }
            if (HardwarePersona.ByName(m.Persona) is null)
                Error($"机器 {m.Name} 的人设 \"{m.Persona}\" 不存在，可选: {string.Join(", ", HardwarePersona.Presets.Select(p => p.Name))}");
            if (m.Memory < 64) Error($"机器 {m.Name} 内存 {m.Memory}MB 太小");
            if (m.Blackwall)
            {
                // blackwall 是「从玩家机接进别的机器」的上帝模式，接自己没有意义
                if (m.Name == level.Machines[0].Name)
                    Error($"机器 {m.Name} 是玩家自己的机器，blackwall 是用来接进别人的机器的");
                // 两处都写才生效。只写了机器这边，作者多半以为它已经能用了
                if (level.Blackwall == BlackwallMode.Off)
                    Error($"机器 {m.Name} 开了 blackwall，但这一关没放出神器 —— 关卡里写 \"blackwall\": \"novice\" 或 \"story\"");
            }
        }
        if (level.Blackwall != BlackwallMode.Off && !level.Machines.Any(m => m.Blackwall))
            Error("这一关放出了神器 blackwall，却没有一台机器能被它接进去（机器上写 \"blackwall\": true）");

        if (level.Admin is { } admin)
        {
            if (!names.Contains(admin.Machine))
                Error($"管理员要查的机器 \"{admin.Machine}\" 不存在");
            if (string.IsNullOrWhiteSpace(admin.User))
                Error("管理员得有个账号名");

            ValidateAdminSchedule(admin.Schedule, "", Error);
            ValidateAdminSuspicion(admin.Suspicion, "", Error);

            var scriptNames = new HashSet<string>();
            foreach (var script in admin.Scripts ?? [])
            {
                if (!FileNamePattern().IsMatch(script.Name)) Error($"脚本名 \"{script.Name}\" 只能用字母、数字、点、连字符和下划线");
                if (!scriptNames.Add(script.Name)) Error($"脚本 \"{script.Name}\" 重复");
                if (script.Checks.Count == 0) Error($"脚本 {script.Name} 什么检查都不做");
                if (script.Checks.Contains(AdminCheck.Script)) Error($"脚本 {script.Name} 里不能再跑脚本");
            }
            var scripts = admin.EffectiveScripts.Select(s => s.Name).ToHashSet();

            foreach (string user in admin.PasswordTargets)
            {
                if (!UserPattern().IsMatch(user)) Error($"要改口令的账号 \"{user}\" 不是合法的用户名");
                // root 的口令是他改别人口令的钥匙；他自己的口令改了游戏就登不上去了
                else if (user == "root" || user == admin.User) Error($"管理员不能改 {user} 的口令");
            }

            foreach (var (mode, odds) in admin.SkillOdds)
            {
                string where = $"{Kebab(mode)} 模式的 skillOdds ";
                if (odds.Values.Any(p => p is < 0 or > 1)) Error($"{where}里的概率要在 0..1 之间");
                else if (Math.Abs(odds.Values.Sum() - 1) > 0.001)
                    Error($"{where}加起来是 {odds.Values.Sum():0.###}，要是 1");
            }

            foreach (var (skill, tier) in admin.Tiers)
                ValidateAdminProfile(tier, $"档位 {Kebab(skill)} ", Error);

            if (admin.Routine.Count > 0) ValidateAdminRoutine(admin.Routine, "", scripts, Error);
            var ids = admin.Routine.Select(a => a.Id).ToHashSet();
            foreach (string id in admin.Sweep)
                if (!ids.Contains(id))
                    Error($"彻底检查里的 \"{id}\" 不在 routine 里");

            // 哪个模式下可能抽到哪一档，这一关就得对哪一档说得通
            foreach (var skill in admin.PossibleSkills)
            {
                string who = $"{Kebab(skill)} 档的管理员";
                var profile = admin.ProfileFor(skill);
                var routine = admin.RoutineFor(skill);
                if (routine.Count == 0)
                {
                    Error($"{who}没有例行要做的事，否则他来了什么也不看 —— 给 routine，或者在 tiers 里给他一套");
                    continue;
                }
                if (profile.Routine is not null) ValidateAdminRoutine(routine, $"{who}", scripts, Error);
                var mine = routine.Select(a => a.Id).ToHashSet();
                foreach (string id in profile.Sweep ?? [])
                    if (!mine.Contains(id)) Error($"{who}彻底检查里的 \"{id}\" 不在他的 routine 里");
            }

            var stepIdSet = level.Steps.Select(s => s.Id).ToHashSet();
            foreach (var (stepId, stage) in admin.Stages)
            {
                string where = $"阶段 \"{stepId}\" ";
                if (!stepIdSet.Contains(stepId))
                    Error($"{where}对不上任何一个步骤 id");
                if (stage.Schedule is { } schedule) ValidateAdminSchedule(schedule, where, Error);
                if (stage.Suspicion is { } suspicion) ValidateAdminSuspicion(suspicion, where, Error);
                if (stage.Routine is { } routine) ValidateAdminRoutine(routine, where, scripts, Error);
            }
        }

        var stepIds = new HashSet<string>();
        foreach (var step in level.Steps)
        {
            if (!stepIds.Add(step.Id)) Error($"步骤 id \"{step.Id}\" 重复");
            if (step.Check is null)
            {
                if (level.Status == LevelStatus.Playable)
                    Error($"步骤 {step.Id} 没有 check —— 可玩关的每一步都得能判定完成，没写完就标成 draft");
                continue;
            }
            foreach (string r in step.Check.MachineRefs)
                if (!names.Contains(r)) Error($"步骤 {step.Id} 引用的机器 \"{r}\" 不存在");
            foreach (string r in step.Check.NetworkRefs)
                if (!networks.ContainsKey(r)) Error($"步骤 {step.Id} 引用的网段 \"{r}\" 不存在");
            ValidateCheck(step, step.Check, level, Error);
        }

        if (level.Status == LevelStatus.Playable)
        {
            if (level.Machines.Count == 0) Error("可玩关至少要有一台机器");
            if (level.Steps.Count == 0) Error("可玩关至少要有一个步骤，否则永远完成不了");
        }
    }

    /// <summary>检测原语自己那几个参数。写错了要在加载时就说，别等玩到那一步才发现判定永远不亮。</summary>
    /// <remarks>组合条件会一层层走下去，嵌套多深都校验得到。</remarks>
    private static void ValidateCheck(LevelStep step, LevelCheck check, LevelDefinition level, Action<string> error)
    {
        foreach (var child in check.Children) ValidateCheck(step, child, level, error);

        string where = $"步骤 {step.Id} 的 check";
        switch (check)
        {
            case TrafficCheck traffic:
                if (traffic.IsEmpty)
                    error($"{where}: traffic 一个条件都没写，那样任何一帧都算数");
                if (traffic.Times < 1) error($"{where}: times 至少是 1");
                if (traffic.Port is { } p && p is < 1 or > 65535)
                    error($"{where}: 端口 {p} 不在 1..65535");
                if (traffic.Port is not null && traffic.Protocol is null)
                    error($"{where}: 写了端口就得写 protocol（TCP 还是 UDP）");
                break;
            case AllCheck all when all.Checks.Count == 0:
                error($"{where}: all 里一个条件都没有，这一步会当场完成");
                break;
            case AnyCheck any when any.Checks.Count == 0:
                error($"{where}: any 里一个条件都没有，这一步永远完不成");
                break;
            case ScanCheck scan:
                if (scan.Hosts < 2) error($"{where}: 扫描至少要试过 2 台主机才算扫描");
                if (scan.WithinSeconds < 1) error($"{where}: 扫描的时间窗口要大于 0 秒");
                // 网段里装不下这么多台主机的话，这一步谁也做不出来
                if (IPNetwork.TryParse(level.Networks.FirstOrDefault(n => n.Name == scan.Network)?.Subnet ?? "",
                                       out var subnet)
                    && scan.Hosts > (1L << (32 - subnet.PrefixLength)) - 2)
                    error($"{where}: 网段 {scan.Network}（{subnet}）里根本没有 {scan.Hosts} 个可用地址");
                // 要扫到的那台机器得真的在这个网段里，否则这一步怎么扫都过不去
                if (scan.Finds is not null
                    && level.Machines.FirstOrDefault(m => m.Name == scan.Finds) is { } target
                    && target.Nics.All(n => n.Network != scan.Network))
                    error($"{where}: 要扫到的 {scan.Finds} 根本不在 {scan.Network} 里");
                break;
            case ProcessCheck process:
                if (process.Times < 1) error($"{where}: times 至少是 1");
                if (process.User is null && process.Command is null)
                    error($"{where}: process 至少要写 user 或 command，否则任何一个进程都算数");
                break;
            case FileCheck file:
                if (!ShaPattern().IsMatch(file.Sha256))
                    error($"{where}: sha256 要是 64 位十六进制，现在是 \"{file.Sha256}\"");
                break;
            case PatrolCheck patrol:
                if (patrol.Patrols < 1) error($"{where}: 至少要有一次干净的查岗");
                if (level.Admin is null) error($"{where}: 这一关没有管理员，没人来验收");
                break;
            case CleanCheck when level.Admin is null:
                error($"{where}: 这一关没有管理员，白名单无从谈起");
                break;
        }
    }

    private static void ValidateAdminSchedule(AdminSchedule schedule, string where, Action<string> error)
    {
        if (schedule.IntervalSeconds < 5 || schedule.FirstPatrolSeconds < 0)
            error($"{where}查岗间隔太短");
        if (schedule.JitterSeconds < 0 || schedule.JitterSeconds >= schedule.IntervalSeconds)
            error($"{where}查岗浮动 {schedule.JitterSeconds}s 要小于间隔 {schedule.IntervalSeconds}s，否则间隔可能变成 0");
        if (schedule.MinActions < 0 || schedule.MaxActions < 1 || schedule.MinActions > schedule.MaxActions)
            error($"{where}一次查岗做几件事的上下限不对：{schedule.MinActions}..{schedule.MaxActions}");
        if (schedule.PauseMin < 0 || schedule.PauseMax < schedule.PauseMin)
            error($"{where}命令之间的停顿不对：{schedule.PauseMin}..{schedule.PauseMax}");
    }

    private static void ValidateAdminSuspicion(SuspicionRules rules, string where, Action<string> error)
    {
        if (rules.ExposedAt <= 0) error($"{where}暴露阈值要大于 0");
        if (rules.SweepAt <= 0 || rules.SweepAt > rules.ExposedAt)
            error($"{where}彻底检查的阈值 {rules.SweepAt} 要在 1..{rules.ExposedAt} 之间，否则他永远不会认真查");
        if (rules.CalmPerPatrol < 0) error($"{where}疑心消退不能是负数");
        if (rules.SweepMultiplier < 1) error($"{where}彻底检查的倍率至少是 1");
    }

    private static void ValidateAdminRoutine(IReadOnlyList<AdminAction> routine, string where,
                                             IReadOnlySet<string> scripts, Action<string> error)
    {
        var seen = new HashSet<string>();
        foreach (var action in routine)
        {
            if (!seen.Add(action.Id)) error($"{where}例行动作 id \"{action.Id}\" 重复");
            if (action.Chance is <= 0 or > 1) error($"{where}动作 \"{action.Id}\" 的概率 {action.Chance} 要在 0..1 之间");
            if (action.Weight <= 0) error($"{where}动作 \"{action.Id}\" 的怀疑度要大于 0");
            if (action.Check == AdminCheck.Script)
            {
                if (action.Script is null) error($"{where}动作 \"{action.Id}\" 要跑脚本，但没写跑哪个（script）");
                else if (!scripts.Contains(action.Script))
                    error($"{where}动作 \"{action.Id}\" 要跑的脚本 \"{action.Script}\" 不在 scripts 里，可选: {string.Join(", ", scripts)}");
            }
            else if (action.Script is not null)
                error($"{where}动作 \"{action.Id}\" 不是跑脚本（check: script），不该写 script");
        }
    }

    private static void ValidateAdminProfile(AdminProfile profile, string where, Action<string> error)
    {
        void Probability(double? p, string what)
        {
            if (p is < 0 or > 1) error($"{where}{what} {p} 要在 0..1 之间");
        }
        void Positive(double? v, string what)
        {
            if (v is <= 0) error($"{where}{what} {v} 要大于 0");
        }

        Positive(profile.SweepAtScale, "彻底检查阈值倍率");
        Positive(profile.PauseScale, "停顿倍率");
        if (profile.CalmScale is < 0) error($"{where}疑心消退倍率不能是负数");
        if (profile.SweepMultiplierBonus is < 0) error($"{where}彻底检查倍数加成不能是负数");
        Probability(profile.SweepOnTheSpotChance, "当场全查的概率");
        Probability(profile.PasswordChance, "改口令的概率");
        Probability(profile.PasswordChanceOnFinding, "看出东西后改口令的概率");
        if (profile.MinActions is < 0 || profile.MaxActions is < 1
            || profile is { MinActions: { } min, MaxActions: { } max } && min > max)
            error($"{where}一次查岗做几件事的上下限不对：{profile.MinActions}..{profile.MaxActions}");
    }

    /// <summary>枚举在关卡文件里的写法，报错时照着写，作者才找得到。</summary>
    private static string Kebab<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());

    private static IPAddress Broadcast(IPNetwork net)
    {
        byte[] b = net.BaseAddress.GetAddressBytes();
        uint value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) | (uint.MaxValue >> net.PrefixLength);
        return new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }

    /// <summary>在解锁依赖图里找环，找到就返回环上的关卡 id（首尾相同）。</summary>
    private static List<string>? FindCycle(List<LevelDefinition> levels)
    {
        var byId = levels.ToDictionary(l => l.Id);
        var state = new Dictionary<string, int>();   // 0 未访问，1 在栈上，2 已完成
        var stack = new List<string>();

        List<string>? Visit(string id)
        {
            state[id] = 1;
            stack.Add(id);
            foreach (string next in byId[id].Requires)
            {
                int s = state.GetValueOrDefault(next);
                if (s == 1) return [.. stack.Skip(stack.IndexOf(next)), next];
                if (s == 0 && Visit(next) is { } found) return found;
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
            return null;
        }

        foreach (var l in levels)
            if (state.GetValueOrDefault(l.Id) == 0 && Visit(l.Id) is { } cycle)
                return cycle;
        return null;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex ShaPattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]*$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex("^[a-z_][a-z0-9_-]*$")]
    private static partial Regex UserPattern();
}
