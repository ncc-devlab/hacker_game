using System.Text.Json;

namespace GameHacker.Core.Levels;

/// <summary>选关界面上一关的状态。</summary>
public enum LevelState
{
    Locked,
    Available,
    Completed,
}

/// <summary>
/// 玩家的通关进度：完成了哪些关。解锁状态由它和关卡的
/// <see cref="LevelDefinition.Requires"/> 现算，不单独存。
/// </summary>
/// <remarks>
/// 只存「完成了什么」而不存「解锁了什么」，是为了改关卡依赖时老存档自动跟着变，
/// 不会出现解锁状态和依赖对不上的存档。
/// </remarks>
public sealed class LevelProgress
{
    private const int FormatVersion = 1;

    private readonly HashSet<string> _completed;

    public LevelProgress(IEnumerable<string>? completed = null) =>
        _completed = new HashSet<string>(completed ?? [], StringComparer.Ordinal);

    public IReadOnlyCollection<string> Completed => _completed;

    public bool IsCompleted(string id) => _completed.Contains(id);

    /// <summary>标记完成。返回 <c>false</c> 表示之前就完成过。</summary>
    public bool MarkCompleted(string id) => _completed.Add(id);

    public void Reset() => _completed.Clear();

    public LevelState StateOf(LevelDefinition level) =>
        IsCompleted(level.Id) ? LevelState.Completed
        : level.Requires.All(IsCompleted) ? LevelState.Available
        : LevelState.Locked;

    /// <summary>还缺哪几关才解锁，给选关界面解释「为什么锁着」。</summary>
    public IEnumerable<string> MissingRequirements(LevelDefinition level) =>
        level.Requires.Where(r => !IsCompleted(r));

    public string ToJson() => JsonSerializer.Serialize(
        new Dto(FormatVersion, [.. _completed.Order(StringComparer.Ordinal)]),
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    /// <summary>
    /// 从存档文本恢复。存档坏了不抛异常而是返回空进度，并在 <paramref name="problem"/>
    /// 里说明 —— 进度丢了能重玩，因为存档坏了游戏打不开就没法玩了。
    /// </summary>
    public static LevelProgress FromJson(string? json, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json)) return new LevelProgress();
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (dto is null) { problem = "存档内容为空"; return new LevelProgress(); }
            if (dto.Version > FormatVersion)
                problem = $"存档版本 {dto.Version} 比游戏新（{FormatVersion}），按能读懂的部分加载";
            return new LevelProgress(dto.Completed ?? []);
        }
        catch (JsonException ex)
        {
            problem = $"存档读不懂，进度从头开始: {ex.Message}";
            return new LevelProgress();
        }
    }

    /// <summary>
    /// 写到磁盘。先写临时文件再改名，写到一半断电也不会把旧存档弄坏。
    /// </summary>
    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    public static LevelProgress Load(string path, out string? problem)
    {
        problem = null;
        return File.Exists(path) ? FromJson(File.ReadAllText(path), out problem) : new LevelProgress();
    }

    private sealed record Dto(int Version, List<string>? Completed);
}
