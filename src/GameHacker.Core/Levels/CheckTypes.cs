using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

namespace GameHacker.Core.Levels;

/// <summary>判定走哪条通路。</summary>
public enum CheckChannel
{
    /// <summary>交换机上的帧。</summary>
    Switch,
    /// <summary>经隐藏控制通道问客户机自己。</summary>
    Guest,
    /// <summary>听管理员查岗的结论。</summary>
    Admin,
    /// <summary>把别的条件拼起来，自己不看任何东西。</summary>
    Compose,
}

/// <summary>清单里的一条：一个可以写进关卡文件的检测原语。</summary>
/// <param name="Type">关卡文件里的 <c>"type"</c>。</param>
/// <param name="Summary">一句话说清楚它判定什么。</param>
/// <param name="Example">一段可以直接抄进关卡文件的 JSON。</param>
public sealed record CheckType(
    string Type, Type Clr, CheckChannel Channel, string Summary, string Example)
{
    /// <summary>
    /// 这个原语在关卡文件里认哪些字段（按 JSON 里的写法）。
    /// </summary>
    /// <remarks>
    /// 只算写得进去的属性：<c>MachineRefs</c> 那种是算出来给校验用的，
    /// 关卡文件里写了反而会被「字段名拼错」拦下来。
    /// </remarks>
    public IReadOnlyList<string> Fields { get; } =
        [.. Clr.GetProperties(BindingFlags.Public | BindingFlags.Instance)
              .Where(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
              .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])];
}

/// <summary>
/// 全部检测原语的清单 —— 写关卡的人要挑零件，先看这里。
/// </summary>
/// <remarks>
/// <para><b>为什么要有这么一份清单。</b> 原语是通用零件，不属于哪一关：
/// 「有没有发过这样的包」「有没有扫到目标」「有没有把文件拿到手」在第二关、第三关
/// 同样用得上。但零件散在各自的类里，写关卡的人不翻源码就不知道有什么可用 ——
/// 于是这份清单把它们导出成一张表，文档里那张表就是从这里生成的。</para>
/// <para><b>它不只是文档。</b> <see cref="Missing"/> 会指出哪些原语忘了登记，
/// 有测试盯着；新加一个原语却不写清楚它判定什么，就过不了。</para>
/// </remarks>
public static class CheckTypes
{
    public static IReadOnlyList<CheckType> All { get; } =
    [
        new(
            "ping", typeof(PingCheck), CheckChannel.Switch,
            "from 成功 ping 通了 to —— 交换机上看到了 to 回给 from 的 echo 应答。看应答而不是请求：请求发出去不代表通",
            """{ "type": "ping", "from": "web01", "to": "backup" }"""),

        new(
            "traffic", typeof(TrafficCheck), CheckChannel.Switch,
            "交换机上出现过这样的包。字段都可以不写，不写就是不限；用来表达「连过那个端口没有」「对着目标发过东西没有」",
            """{ "type": "traffic", "from": "ws", "to": "files01", "protocol": "TCP", "port": 9000 }"""),

        new(
            "route", typeof(RouteCheck), CheckChannel.Switch,
            "from 的包进得了 network，而且回得来 —— 隧道通了。只有中间那台两头插网卡的机器能把它转过去，所以这是铁证",
            """{ "type": "route", "from": "ws", "network": "inside" }"""),

        new(
            "scan", typeof(ScanCheck), CheckChannel.Switch,
            "有人在 network 里挨个试地址：窗口内问候了 hosts 台以上不同的主机。写了 finds 还要求真的扫到那台机器（它应了答）",
            """{ "type": "scan", "network": "inside", "hosts": 12, "withinSeconds": 60, "finds": "files01" }"""),

        new(
            "file", typeof(FileCheck), CheckChannel.Guest,
            "machine 上存着一份内容是这个哈希的文件 —— 东西到手了。存成什么名字、放哪个目录都行，只认内容",
            """{ "type": "file", "machine": "ws", "sha256": "fdf59bb6…" }"""),

        new(
            "clean", typeof(CleanCheck), CheckChannel.Guest,
            "machine 上看不出有人来过：白名单外的进程没了，转发也关回去了。白名单和管理员用的是同一份",
            """{ "type": "clean", "machine": "jump01" }"""),

        new(
            "patrol", typeof(PatrolCheck), CheckChannel.Admin,
            "管理员连着查了 patrols 次岗，一次也没看出什么。让对手来验收，不需要任何额外探查",
            """{ "type": "patrol", "patrols": 1 }"""),

        new(
            "all", typeof(AllCheck), CheckChannel.Compose,
            "里面的条件全部满足。各条各自记账、不要求同时成立：先满足的那条会记着",
            """{ "type": "all", "checks": [ { "type": "file", "machine": "ws", "sha256": "…" }, { "type": "clean", "machine": "jump01" } ] }"""),

        new(
            "any", typeof(AnyCheck), CheckChannel.Compose,
            "里面的条件满足任意一条。一件事有好几种正当做法时用它",
            """{ "type": "any", "checks": [ { "type": "ping", "from": "ws", "to": "files01" }, { "type": "traffic", "from": "ws", "to": "files01" } ] }"""),
    ];

    public static CheckType? Find(string type) =>
        All.FirstOrDefault(t => t.Type == type);

    /// <summary>
    /// 挂在 <see cref="LevelCheck"/> 上、却没在这份清单里登记的原语。
    /// </summary>
    /// <remarks>清单是文档的来源，漏登记等于加了个没人知道的零件，所以有测试盯着这里。</remarks>
    public static IReadOnlyList<string> Missing =>
        [.. typeof(LevelCheck).GetCustomAttributes<JsonDerivedTypeAttribute>()
              .Select(a => a.TypeDiscriminator?.ToString() ?? a.DerivedType.Name)
              .Where(name => All.All(t => t.Type != name))];

    /// <summary>把清单排成文档里那张表。</summary>
    public static string Markdown()
    {
        var text = new StringBuilder("| type | 判定什么 | 通路 | 字段 |\n| --- | --- | --- | --- |\n");
        foreach (var t in All)
            text.Append($"| `{t.Type}` | {t.Summary} | {Name(t.Channel)} | {Fields(t)} |\n");
        return text.ToString();
    }

    private static string Fields(CheckType type) =>
        type.Fields.Count == 0 ? "（无）" : string.Join("、", type.Fields.Select(f => $"`{f}`"));

    private static string Name(CheckChannel channel) => channel switch
    {
        CheckChannel.Switch => "交换机",
        CheckChannel.Guest => "客户机",
        CheckChannel.Admin => "管理员",
        _ => "组合",
    };
}
