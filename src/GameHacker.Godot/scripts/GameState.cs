using System;
using System.Collections.Generic;
using System.Linq;
using GameHacker.Core.Levels;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 跨场景的游戏状态：关卡目录、通关进度、当前选中的关卡。以 autoload 常驻。
/// </summary>
/// <remarks>
/// <para>选关界面和关卡场景之间用 <c>ChangeSceneToFile</c> 切换，场景切换时
/// 旧场景整个被释放，所以「进哪一关」只能放在这种常驻节点上传递。</para>
/// <para>关卡文件在 <c>res://levels</c>。导出时它们不是 Godot 资源，
/// 要在导出预设的「非资源文件」过滤里加上 <c>levels/*.json</c>，否则不会进 .pck。</para>
/// </remarks>
public partial class GameState : Node
{
    public const string LevelsDir = "res://levels";
    public const string ProgressPath = "user://progress.json";
    public const string SelectScene = "res://scenes/LevelSelect.tscn";
    public const string LevelScene = "res://scenes/Level.tscn";

    public static GameState Instance { get; private set; } = null!;

    /// <summary>关卡目录。加载失败时为 <c>null</c>，原因在 <see cref="CatalogError"/>。</summary>
    public LevelCatalog? Catalog { get; private set; }
    public string? CatalogError { get; private set; }

    public LevelProgress Progress { get; private set; } = new();

    /// <summary>读存档时遇到的问题（存档坏了等），选关界面上提示一次。</summary>
    public string? ProgressProblem { get; private set; }

    /// <summary>正在玩（或将要进入）的关卡。</summary>
    public LevelDefinition? Current { get; private set; }

    /// <summary>
    /// 游玩模式（新手 / 高级 / 专家）。全局选一次；来查岗的管理员有多专业跟着它和关卡走，
    /// 见 <see cref="GameHacker.Core.Admin.AdminSkillOdds"/>。
    /// </summary>
    public PlayMode Mode => Progress.Mode;

    /// <summary>
    /// 开发用：<c>GAMEHACKER_ADMIN_SKILL=junior|regular|senior</c> 指定来的是哪一档管理员，
    /// 盖过游玩模式与关卡设定，方便挨个看三档的表现。
    /// </summary>
    public static GameHacker.Core.Admin.AdminSkill? ForcedAdminSkill =>
        Enum.TryParse<GameHacker.Core.Admin.AdminSkill>(
            System.Environment.GetEnvironmentVariable("GAMEHACKER_ADMIN_SKILL"), ignoreCase: true, out var skill)
            ? skill : null;

    /// <summary>编辑器里调试用：无视解锁条件。导出版本恒为 false。</summary>
    public bool IgnoreLocks { get; set; }

    public static bool IsEditor => OS.HasFeature("editor");

    /// <summary>
    /// 自检 / 截图这类无人值守运行。它们直接进 <see cref="AutoStartLevelId"/>，
    /// 也不写存档 —— 否则在开发机上跑一次验证就把玩家进度改了。
    /// </summary>
    public static bool IsAutomated =>
        !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GAMEHACKER_SELFTEST"))
        || !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("GAMEHACKER_SCREENSHOT"));

    /// <summary>
    /// 启动后直接进哪一关，不停在选关界面。<c>GAMEHACKER_LEVEL</c> 指定；
    /// 自检和截图不指定时默认进联网实验场（验证脚本跑的就是它）。
    /// </summary>
    public static string? AutoStartLevelId
    {
        get
        {
            string? id = System.Environment.GetEnvironmentVariable("GAMEHACKER_LEVEL");
            if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
            return IsAutomated ? "lab-ping" : null;
        }
    }

    public override void _EnterTree()
    {
        Instance = this;
        LoadCatalog();
        Progress = LevelProgress.Load(ProjectSettings.GlobalizePath(ProgressPath), out string? problem);
        ProgressProblem = problem;
        if (problem is not null) GD.PushWarning($"[levels] {problem}");
    }

    private void LoadCatalog()
    {
        try
        {
            var files = new List<(string, string)>();
            foreach (string name in DirAccess.GetFilesAt(LevelsDir).Where(f => f.EndsWith(".json")).Order(StringComparer.Ordinal))
                files.Add((name, FileAccess.GetFileAsString($"{LevelsDir}/{name}")));
            if (files.Count == 0) throw new InvalidOperationException($"{LevelsDir} 下没有关卡文件");

            Catalog = LevelCatalog.Parse(files);
            GD.Print($"[levels] 加载了 {Catalog.Levels.Count} 关: {string.Join(", ", Catalog.Levels.Select(l => l.Id))}");
        }
        catch (Exception ex)
        {
            CatalogError = ex.Message;
            GD.PushError($"[levels] {ex.Message}");
        }
    }

    public LevelState StateOf(LevelDefinition level) => Progress.StateOf(level);

    /// <summary>这一关现在能不能进；不能进时 <paramref name="reason"/> 说明为什么。</summary>
    public bool CanEnter(LevelDefinition level, out string reason)
    {
        reason = "";
        if (level.Status == LevelStatus.Draft && !IsEditor)
        {
            reason = "这一关还在制作中";
            return false;
        }
        if (StateOf(level) == LevelState.Locked && !(IsEditor && IgnoreLocks))
        {
            var missing = Progress.MissingRequirements(level).Select(id => Catalog?.Find(id)?.Title ?? id);
            reason = $"先完成：{string.Join("、", missing)}";
            return false;
        }
        return true;
    }

    public void Enter(LevelDefinition level)
    {
        Current = level;
        GD.Print($"[levels] 进入 {level.Id}");
        GetTree().ChangeSceneToFile(LevelScene);
    }

    public void BackToSelect()
    {
        Current = null;
        GetTree().ChangeSceneToFile(SelectScene);
    }

    /// <summary>记一关通关并存盘。返回是否第一次通关。</summary>
    public bool Complete(LevelDefinition level)
    {
        bool first = Progress.MarkCompleted(level.Id);
        if (IsAutomated) return first;
        try { Progress.Save(ProjectSettings.GlobalizePath(ProgressPath)); }
        catch (Exception ex) { GD.PushError($"[levels] 存档写不进去: {ex.Message}"); }
        return first;
    }

    public void ResetProgress()
    {
        Progress.Reset();
        SaveProgress();
    }

    public void SetMode(PlayMode mode)
    {
        if (Progress.Mode == mode) return;
        Progress.Mode = mode;
        SaveProgress();
    }

    private void SaveProgress()
    {
        if (IsAutomated) return;
        try { Progress.Save(ProjectSettings.GlobalizePath(ProgressPath)); }
        catch (Exception ex) { GD.PushError($"[levels] 存档写不进去: {ex.Message}"); }
    }
}
