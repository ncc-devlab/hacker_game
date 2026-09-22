using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        return new LevelCatalog(levels.Select(l => l.Level).ToList());
    }

    private static void Validate(string source, LevelDefinition level, List<string> errors)
    {
        void Error(string message) => errors.Add($"{source}: {message}");

        if (!IdPattern().IsMatch(level.Id))
            Error($"id \"{level.Id}\" 只能用小写字母、数字和连字符");
        if (level.Requires.Contains(level.Id))
            Error("requires 里有自己");

        var names = new HashSet<string>();
        var ips = new HashSet<string>();
        foreach (var m in level.Machines)
        {
            if (!names.Add(m.Name)) Error($"机器名 \"{m.Name}\" 重复");
            if (!IPAddress.TryParse(m.Ip, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                Error($"机器 {m.Name} 的 ip \"{m.Ip}\" 不是 IPv4 地址");
            else if (!ips.Add(m.Ip)) Error($"ip {m.Ip} 重复");
            if (HardwarePersona.ByName(m.Persona) is null)
                Error($"机器 {m.Name} 的人设 \"{m.Persona}\" 不存在，可选: {string.Join(", ", HardwarePersona.Presets.Select(p => p.Name))}");
            if (m.Memory < 64) Error($"机器 {m.Name} 内存 {m.Memory}MB 太小");
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
        }

        if (level.Status == LevelStatus.Playable)
        {
            if (level.Machines.Count == 0) Error("可玩关至少要有一台机器");
            if (level.Steps.Count == 0) Error("可玩关至少要有一个步骤，否则永远完成不了");
        }
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
}
