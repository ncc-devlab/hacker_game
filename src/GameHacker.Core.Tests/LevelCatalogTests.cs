using GameHacker.Core.Levels;

namespace GameHacker.Core.Tests;

/// <summary>
/// 关卡文件的解析与校验。
/// </summary>
/// <remarks>
/// 这些错误如果漏到运行时，表现都是「进了关才卡住」—— 比如 check 里的机器名拼错，
/// 那一步永远判定不了完成。所以宁可在加载时就拒绝。
/// </remarks>
public class LevelCatalogTests
{
    private const string Lab = """
        {
          "id": "lab", "title": "实验场", "track": "tutorial",
          "machines": [
            { "name": "a", "ip": "10.0.0.1" },
            { "name": "b", "ip": "10.0.0.2", "persona": "legacy-server" }
          ],
          "steps": [ { "id": "s", "title": "通", "check": { "type": "ping", "from": "b", "to": "a" } } ]
        }
        """;

    private static LevelCatalog Parse(params string[] jsons) =>
        LevelCatalog.Parse(jsons.Select((j, i) => ($"level{i}.json", j)));

    private static LevelFormatException Rejects(params string[] jsons) =>
        Assert.Throws<LevelFormatException>(() => Parse(jsons));

    [Fact]
    public void 解析出机器步骤与检测条件()
    {
        var level = Assert.Single(Parse(Lab).Levels);
        Assert.Equal(LevelTrack.Tutorial, level.Track);
        Assert.Equal(LevelStatus.Playable, level.Status);
        Assert.Equal(["a", "b"], level.Machines.Select(m => m.Name));
        Assert.Equal("workstation", level.Machines[0].Persona);
        var check = Assert.IsType<PingCheck>(level.Steps[0].Check);
        Assert.Equal(("b", "a"), (check.From, check.To));
    }

    [Fact]
    public void 教学关排在实战关前面_同一条线里按_order()
    {
        var catalog = Parse(
            """{ "id": "m2", "title": "x", "track": "mission", "order": 2, "status": "draft" }""",
            """{ "id": "m1", "title": "x", "track": "mission", "order": 1, "status": "draft" }""",
            """{ "id": "t9", "title": "x", "track": "tutorial", "order": 9, "status": "draft" }""");
        Assert.Equal(["t9", "m1", "m2"], catalog.Levels.Select(l => l.Id));
    }

    [Fact]
    public void 拼错的字段名被拒绝而不是忽略()
    {
        // "require" 少了个 s：忽略的话这关会悄悄变成不需要前置
        var ex = Rejects("""{ "id": "x", "title": "x", "track": "mission", "status": "draft", "require": ["lab"] }""");
        Assert.Contains("require", ex.Message);
    }

    [Fact]
    public void 未知的检测类型被拒绝()
    {
        var ex = Rejects(Lab.Replace("\"ping\"", "\"portscan\""));
        Assert.Contains("level0.json", ex.Message);
    }

    [Fact]
    public void 检测条件引用不存在的机器被拒绝()
    {
        var ex = Rejects(Lab.Replace("\"from\": \"b\"", "\"from\": \"c\""));
        Assert.Contains("\"c\" 不存在", ex.Message);
    }

    [Fact]
    public void 可玩关的步骤必须有检测_草稿关可以没有()
    {
        string noCheck = """
            { "id": "x", "title": "x", "track": "mission",
              "machines": [ { "name": "a", "ip": "10.0.0.1" } ],
              "steps": [ { "id": "s", "title": "待定" } ] }
            """;
        Assert.Contains("没有 check", Rejects(noCheck).Message);
        Parse(noCheck.Replace("\"track\": \"mission\"", "\"track\": \"mission\", \"status\": \"draft\""));
    }

    [Fact]
    public void 所有错误攒齐一起报()
    {
        string bad = """
            { "id": "Bad_Id", "title": "x", "track": "mission",
              "machines": [ { "name": "a", "ip": "10.0.0.300", "persona": "mainframe" },
                            { "name": "a", "ip": "10.0.0.2" } ],
              "steps": [ { "id": "s", "title": "x", "check": { "type": "ping", "from": "a", "to": "a" } } ] }
            """;
        var ex = Rejects(bad);
        Assert.Contains(ex.Errors, e => e.Contains("Bad_Id"));
        Assert.Contains(ex.Errors, e => e.Contains("10.0.0.300"));
        Assert.Contains(ex.Errors, e => e.Contains("mainframe"));
        Assert.Contains(ex.Errors, e => e.Contains("机器名 \"a\" 重复"));
    }

    [Fact]
    public void 重复_id_与不存在的前置关被拒绝()
    {
        string draft(string id, string requires) =>
            $$"""{ "id": "{{id}}", "title": "x", "track": "mission", "status": "draft", "requires": [{{requires}}] }""";
        Assert.Contains("重复", Rejects(draft("a", ""), draft("a", "")).Message);
        Assert.Contains("\"ghost\" 不存在", Rejects(draft("a", "\"ghost\"")).Message);
    }

    [Fact]
    public void 解锁条件成环被拒绝()
    {
        string draft(string id, string req) =>
            $$"""{ "id": "{{id}}", "title": "x", "track": "mission", "status": "draft", "requires": ["{{req}}"] }""";
        var ex = Rejects(draft("a", "b"), draft("b", "c"), draft("c", "a"));
        Assert.Contains("成环", ex.Message);
    }

    [Fact]
    public void 仓库里的关卡文件都能通过校验()
    {
        // 游戏发布的就是这些文件，放进单元测试里，改坏了 CI 上就能看到
        string dir = Path.Combine(TestImages.RepoRoot, "src", "GameHacker.Godot", "levels");
        var files = Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);

        var catalog = LevelCatalog.Parse(files.Select(f => (Path.GetFileName(f), File.ReadAllText(f))));
        // 文件名和 id 一致，找关卡文件时不用挨个打开
        Assert.Equal(files.Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal),
                     catalog.Levels.Select(l => l.Id).Order(StringComparer.Ordinal));
        // 新存档至少有一关能进
        Assert.Contains(catalog.Levels, l => l.Status == LevelStatus.Playable && l.Requires.Count == 0);
    }
}
