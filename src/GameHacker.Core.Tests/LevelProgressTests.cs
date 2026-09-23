using GameHacker.Core.Levels;

namespace GameHacker.Core.Tests;

public class LevelProgressTests
{
    private static LevelDefinition Level(string id, params string[] requires) => new()
    {
        Id = id, Title = id, Track = LevelTrack.Mission, Requires = requires,
    };

    [Fact]
    public void 前置关全部完成才解锁()
    {
        var a = Level("a");
        var b = Level("b");
        var c = Level("c", "a", "b");
        var p = new LevelProgress();

        Assert.Equal(LevelState.Available, p.StateOf(a));
        Assert.Equal(LevelState.Locked, p.StateOf(c));
        Assert.Equal(["a", "b"], p.MissingRequirements(c));

        p.MarkCompleted("a");
        Assert.Equal(LevelState.Completed, p.StateOf(a));
        Assert.Equal(LevelState.Locked, p.StateOf(c));
        Assert.Equal(["b"], p.MissingRequirements(c));

        p.MarkCompleted("b");
        Assert.Equal(LevelState.Available, p.StateOf(c));
    }

    [Fact]
    public void 存档往返()
    {
        var p = new LevelProgress(["lab-ping", "jump-scan"]);
        var back = LevelProgress.FromJson(p.ToJson(), out string? problem);
        Assert.Null(problem);
        Assert.Equal(["jump-scan", "lab-ping"], back.Completed.Order());
    }

    [Fact]
    public void 存档坏了返回空进度并说明原因()
    {
        var p = LevelProgress.FromJson("{ not json", out string? problem);
        Assert.Empty(p.Completed);
        Assert.NotNull(problem);

        Assert.Empty(LevelProgress.FromJson(null, out problem).Completed);
        Assert.Null(problem);
    }

    [Fact]
    public void 存档里有已删除的关卡不影响加载()
    {
        // 关卡改名或删掉以后，老存档里会留着不认识的 id —— 忽略即可
        var p = LevelProgress.FromJson("""{ "version": 1, "completed": ["gone", "a"] }""", out string? problem);
        Assert.Null(problem);
        Assert.Equal(LevelState.Available, p.StateOf(Level("b", "a")));
    }

    [Fact]
    public void 写盘后能读回_不留临时文件()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gh-progress-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "sub", "progress.json");
        try
        {
            new LevelProgress(["a"]).Save(path);
            new LevelProgress(["a", "b"]).Save(path);   // 覆盖已有存档
            Assert.Equal(["a", "b"], LevelProgress.Load(path, out _).Completed.Order());
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Empty(LevelProgress.Load(Path.Combine(dir, "missing.json"), out _).Completed);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 游玩模式跟着存档走_重置进度不动它()
    {
        var progress = new LevelProgress(["lab"]) { Mode = PlayMode.Expert };
        var loaded = LevelProgress.FromJson(progress.ToJson(), out string? problem);
        Assert.Null(problem);
        Assert.Equal(PlayMode.Expert, loaded.Mode);
        Assert.Contains("\"expert\"", progress.ToJson());

        loaded.Reset();
        Assert.Equal(PlayMode.Expert, loaded.Mode);
    }

    [Fact]
    public void 老存档没有模式时按高级模式()
    {
        var loaded = LevelProgress.FromJson("""{ "version": 1, "completed": ["lab"] }""", out string? problem);
        Assert.Null(problem);
        Assert.Equal(PlayMode.Advanced, loaded.Mode);
        Assert.True(loaded.IsCompleted("lab"));
    }
}
