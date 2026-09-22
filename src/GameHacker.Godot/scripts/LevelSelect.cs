using System.Linq;
using System.Text;
using GameHacker.Core.Levels;
using GameHacker.Core.Qemu;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 选关界面：左边按内容线分组的关卡列表，右边是选中关卡的简报和开始按钮。
/// </summary>
public partial class LevelSelect : Control
{
    private static readonly Color Done = new(0.55f, 0.85f, 0.55f);
    private static readonly Color Open = new(0.95f, 0.95f, 0.95f);
    private static readonly Color Dim = new(0.5f, 0.5f, 0.5f);

    private Tree _levels = null!;
    private Label _title = null!;
    private Label _meta = null!;
    private RichTextLabel _body = null!;
    private Button _start = null!;
    private Label _reason = null!;
    private CheckBox _ignoreLocks = null!;
    private Button _reset = null!;
    private Label _footer = null!;
    private ConfirmationDialog _confirmReset = null!;

    private GameState State => GameState.Instance;
    private LevelDefinition? _selected;

    public override void _Ready()
    {
        _levels = GetNode<Tree>("%Levels");
        _title = GetNode<Label>("%Title");
        _meta = GetNode<Label>("%Meta");
        _body = GetNode<RichTextLabel>("%Body");
        _start = GetNode<Button>("%Start");
        _reason = GetNode<Label>("%Reason");
        _ignoreLocks = GetNode<CheckBox>("%IgnoreLocks");
        _reset = GetNode<Button>("%Reset");
        _footer = GetNode<Label>("%Footer");

        _levels.SetColumnExpand(1, false);
        _levels.SetColumnCustomMinimumWidth(1, 90);
        _levels.ItemSelected += () => Select(_levels.GetSelected()?.GetMetadata(0).AsString());
        _levels.ItemActivated += TryStart;          // 双击或回车直接开始
        _start.Pressed += TryStart;

        _ignoreLocks.Visible = GameState.IsEditor;
        _ignoreLocks.ButtonPressed = State.IgnoreLocks;
        _ignoreLocks.Toggled += on => { State.IgnoreLocks = on; Refresh(); };

        _confirmReset = new ConfirmationDialog
        {
            Title = "重置进度",
            DialogText = "清空所有通关记录？已解锁的关卡会重新锁上。",
            OkButtonText = "清空",
            CancelButtonText = "取消",
        };
        AddChild(_confirmReset);
        _confirmReset.Confirmed += () => { State.ResetProgress(); Refresh(); };
        _reset.Pressed += () => _confirmReset.PopupCentered();

        _footer.Text = State.CatalogError ?? State.ProgressProblem ?? "";

        if (GameState.AutoStartLevelId is { } auto)
        {
            // 场景树在 _Ready 里还没搭完，换场景要推到下一帧
            var level = State.Catalog?.Find(auto);
            string why = "";
            if (level is null) _footer.Text = $"GAMEHACKER_LEVEL={auto} 不存在。{_footer.Text}";
            // 开发和验证时可以直接跳进任何一关；发布版本里这个变量不能拿来绕过解锁
            else if (GameState.IsEditor || GameState.IsAutomated || State.CanEnter(level, out why))
            {
                Callable.From(() => State.Enter(level)).CallDeferred();
                return;
            }
            else _footer.Text = $"GAMEHACKER_LEVEL={auto}: {why}";
        }

        Refresh();
    }

    /// <summary>重建列表并尽量保持选中项。进度或解锁开关变了都走这里。</summary>
    private void Refresh()
    {
        string? keep = _selected?.Id;
        _levels.Clear();
        TreeItem root = _levels.CreateItem();
        var catalog = State.Catalog;
        if (catalog is null) { ShowDetail(null); return; }

        TreeItem? toSelect = null;
        foreach (var group in catalog.Levels.GroupBy(l => l.Track))
        {
            TreeItem header = _levels.CreateItem(root);
            header.SetText(0, group.Key == LevelTrack.Tutorial ? "教学关卡" : "实战关卡");
            header.SetSelectable(0, false);
            header.SetSelectable(1, false);
            header.SetCustomColor(0, Dim);

            foreach (var level in group)
            {
                TreeItem item = _levels.CreateItem(header);
                item.SetMetadata(0, level.Id);
                item.SetText(0, level.Title);
                item.SetTooltipText(0, level.Summary);
                var (label, color) = Badge(level);
                item.SetText(1, label);
                item.SetCustomColor(0, color);
                item.SetCustomColor(1, color);
                item.SetSelectable(1, false);

                // 默认选中第一个能玩还没通关的，玩家打开游戏就知道下一步去哪
                if (level.Id == keep) toSelect = item;
                else if (keep is null && toSelect is null && State.StateOf(level) == LevelState.Available
                         && level.Status == LevelStatus.Playable) toSelect = item;
            }
        }

        toSelect ??= root.GetFirstChild()?.GetFirstChild();
        if (toSelect is not null)
        {
            toSelect.Select(0);   // 会触发 ItemSelected -> Select
            _levels.ScrollToItem(toSelect);
        }
        else ShowDetail(null);
    }

    private (string Label, Color Color) Badge(LevelDefinition level) =>
        level.Status == LevelStatus.Draft ? ("制作中", Dim)
        : State.StateOf(level) switch
        {
            LevelState.Completed => ("已完成", Done),
            LevelState.Available => ("", Open),
            _ => ("未解锁", Dim),
        };

    private void Select(string? id)
    {
        _selected = id is null ? null : State.Catalog?.Find(id);
        ShowDetail(_selected);
    }

    private void ShowDetail(LevelDefinition? level)
    {
        if (level is null)
        {
            _title.Text = State.Catalog is null ? "关卡加载失败" : "";
            _meta.Text = "";
            _body.Text = "";
            _start.Disabled = true;
            _reason.Text = "";
            return;
        }

        _title.Text = level.Title;
        string track = level.Track == LevelTrack.Tutorial ? "教学关卡" : "实战关卡";
        string state = level.Status == LevelStatus.Draft ? "制作中" : State.StateOf(level) switch
        {
            LevelState.Completed => "已完成",
            LevelState.Available => "可以开始",
            _ => "未解锁",
        };
        _meta.Text = $"{track}  ·  {state}";

        var sb = new StringBuilder();
        if (level.Summary.Length > 0) sb.Append($"[i]{Escape(level.Summary)}[/i]\n\n");
        if (level.Briefing.Length > 0) sb.Append($"{Escape(level.Briefing)}\n\n");

        if (level.Requires.Count > 0)
        {
            sb.Append("[b]前置任务[/b]\n");
            foreach (string req in level.Requires)
            {
                bool ok = State.Progress.IsCompleted(req);
                string name = State.Catalog?.Find(req)?.Title ?? req;
                sb.Append($"  [color={(ok ? "#8cd98c" : "#999999")}]{(ok ? "已完成" : "未完成")}[/color]  {Escape(name)}\n");
            }
            sb.Append('\n');
        }

        if (level.Machines.Count > 0)
        {
            sb.Append("[b]涉及的机器[/b]\n");
            for (int i = 0; i < level.Machines.Count; i++)
            {
                var m = level.Machines[i];
                string product = HardwarePersona.ByName(m.Persona)?.SystemProduct ?? "";
                string who = i == 0 ? "（你的机器）" : "";
                sb.Append($"  [code]{m.Name,-8} {m.Ip,-12}[/code] {Escape(product)} {who}\n");
            }
            sb.Append('\n');
        }

        // 只列步骤标题。提示进关后逐步给，提前全摊开就没意思了
        if (level.Steps.Count > 0)
        {
            sb.Append("[b]任务步骤[/b]\n");
            for (int i = 0; i < level.Steps.Count; i++)
                sb.Append($"  {i + 1}. {Escape(level.Steps[i].Title)}\n");
        }

        _body.Text = sb.ToString();

        bool can = State.CanEnter(level, out string reason);
        _start.Disabled = !can;
        _start.Text = State.StateOf(level) == LevelState.Completed ? "再玩一次" : "开始任务";
        _reason.Text = can && level.Status == LevelStatus.Draft ? "草稿关：步骤还没有判定，只在编辑器里能进" : reason;
    }

    private void TryStart()
    {
        if (_selected is { } level && State.CanEnter(level, out _))
            State.Enter(level);
    }

    /// <summary>关卡文本里的方括号会被当成 BBCode。</summary>
    private static string Escape(string s) => s.Replace("[", "[lb]");
}
