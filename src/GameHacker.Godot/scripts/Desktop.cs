using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 玩家的虚拟桌面：左上角一列图标，上面摆窗口。管层叠次序、拖动时的吸附、靠边停靠，
/// 以及画面大小变了时按比例重排。
/// </summary>
/// <remarks>
/// <para>它<b>不是</b>布局容器 —— 子节点的 <c>Position</c> 由自己说了算，
/// 容器会把它们排回去。</para>
/// <para>窗口可以互相压着，和平常用的桌面一样：点到哪扇，哪扇就提到最前、标题亮起来。</para>
/// <list type="bullet">
/// <item><b>对齐吸附</b>（悄悄发生）：拖到离别的窗或桌面边缘 <see cref="Magnet"/> 像素
/// 以内时边对边贴上去。玩家想拼出一个整齐的布局不用瞄准；离得远就完全跟手。</item>
/// <item><b>靠边停靠</b>（有高亮）：把指针拖到桌面边缘，高亮出一块区域，松手就滑过去铺满它。
/// 左右半屏、顶部整屏、底部半屏 —— 常用的几种布局一下就摆好。</item>
/// <item><b>按比例重排</b>：每扇窗记的是它占桌面的比例，游戏窗口拉大缩小时
/// 各扇窗跟着等比缩放，半屏的还是半屏。</item>
/// </list>
/// </remarks>
public partial class Desktop : Control
{
    /// <summary>桌面图标那一列占的宽度。默认布局把窗口摆在它右边，开局就看得见图标。</summary>
    public const float IconGutter = 104;

    /// <summary>边对边吸附的距离。</summary>
    private const float Magnet = 8;

    /// <summary>指针进到离边缘这么近就触发停靠。</summary>
    private const float EdgeZone = 20;

    /// <summary>停靠高亮追目标的快慢（每秒收敛的指数速率）。</summary>
    private const float DockEase = 22;

    /// <summary>每扇窗占桌面的比例（0..1）。重排时按它算，不按上一次的像素，缩来缩去不会走样。</summary>
    private readonly Dictionary<GameWindow, Rect2> _share = [];

    private Rect2? _dock;       // 指针现在指着的停靠区
    private Rect2 _dockShown;   // 高亮框当前画在哪（追着 _dock 滑）
    private float _dockAlpha;   // 高亮框当前的浓淡

    private VBoxContainer? _icons;

    /// <summary>层叠次序或哪扇在前台变了。顶栏那排按钮靠它同步亮着的那一个。</summary>
    public event Action? StackChanged;

    public IEnumerable<GameWindow> Windows => GetChildren().OfType<GameWindow>();

    /// <summary>
    /// 算比例用的桌面尺寸。还没排过版时 Size 是 0（无头模式下 Resized 可能一次都不响），
    /// 先拿视口顶上 —— 真尺寸到了会按比例重排一次，照样对得上。
    /// </summary>
    private Vector2 Area => Size.X >= 1 && Size.Y >= 1 ? Size : GetViewportRect().Size;

    public override void _Ready()
    {
        // 窗口自己定位置，别让任何东西替它排版
        MouseFilter = MouseFilterEnum.Pass;
        Resized += Rescale;
        SetProcess(false);
    }

    /// <summary>开一扇新窗，把 <paramref name="content"/> 搬进去。</summary>
    public GameWindow Open(string title, Control content, bool closable = true)
    {
        var window = new GameWindow(title, closable) { Name = $"Window_{title}" };
        AddChild(window);
        window.Adopt(content);
        window.TreeExiting += () => _share.Remove(window);
        RefreshActive();
        return window;
    }

    // --- 层叠 ---------------------------------------------------------------

    public void BringToFront(GameWindow window)
    {
        MoveChild(window, GetChildCount() - 1);
        RefreshActive();
    }

    /// <summary>最上面那扇看得见的窗亮着，其余的都暗下去。</summary>
    public void RefreshActive()
    {
        var top = Windows.LastOrDefault(w => w.Shown);
        foreach (var w in Windows) w.SetActive(w == top);
        StackChanged?.Invoke();
    }

    /// <summary>
    /// 桌面上 <paramref name="point"/> 这一点最上面的那扇窗，提到最前。没点在任何窗上返回 null。
    /// </summary>
    public GameWindow? RaiseAt(Vector2 point)
    {
        var hit = Windows.LastOrDefault(w => w.Shown && w.Rect.HasPoint(point));
        if (hit is not null && !hit.Active) BringToFront(hit);
        return hit;
    }

    /// <summary>
    /// 点在窗里任何地方都把它提上来，不只是标题栏。
    /// </summary>
    /// <remarks>
    /// 放在 <c>_Input</c> 里而不是各扇窗的 <c>_GuiInput</c>：窗里的终端会把鼠标事件吃掉，
    /// 窗自己收不到。这里只看一眼、不吞事件，点击照样落到终端上。
    /// </remarks>
    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true }) return;
        if (!IsVisibleInTree()) return;
        var point = GetLocalMousePosition();
        if (!new Rect2(Vector2.Zero, Size).HasPoint(point)) return;
        // 点到窗上，或者点在空白桌面上，图标的选中都该取消；点在图标上由图标自己选中
        if (RaiseAt(point) is not null || !IconAt(point)) SelectIcon(null);
    }

    // --- 桌面图标 -----------------------------------------------------------

    /// <summary>在左上角那一列里加一个图标。图标永远在所有窗口下面。</summary>
    public void AddIcon(DesktopIcon icon)
    {
        if (_icons is null)
        {
            _icons = new VBoxContainer { Name = "Icons", Position = new Vector2(12, 12), MouseFilter = MouseFilterEnum.Ignore };
            _icons.AddThemeConstantOverride("separation", 6);
            AddChild(_icons);
            MoveChild(_icons, 0);
        }
        icon.Selected += () => SelectIcon(icon);
        _icons.AddChild(icon);
    }

    public IEnumerable<DesktopIcon> Icons => _icons?.GetChildren().OfType<DesktopIcon>() ?? [];

    private void SelectIcon(DesktopIcon? selected)
    {
        foreach (var icon in Icons) icon.Highlighted = icon == selected;
    }

    private bool IconAt(Vector2 point) =>
        Icons.Any(i => new Rect2(i.GlobalPosition - GlobalPosition, i.Size).HasPoint(point));

    // --- 拖动 ---------------------------------------------------------------

    /// <summary>拖动中：窗口跟着指针走，边缘够近就贴上去。</summary>
    public void DragTo(GameWindow moving, Vector2 wanted)
    {
        var (xs, ys) = EdgesExcept(moving);
        float x = Nearest(wanted.X, moving.Size.X, xs);
        float y = Nearest(wanted.Y, moving.Size.Y, ys);
        moving.Position = ClampInside(new Vector2(x, y), moving.Size);
    }

    /// <summary>拖动中：指针在边缘就高亮出要停靠的那块区域。</summary>
    public void AimDock(Vector2 pointer)
    {
        var zone = DockZone(pointer);
        if (zone == _dock) return;
        // 高亮刚冒出来时从指针那一点长出来，而不是凭空出现一整块
        if (_dock is null && _dockAlpha <= 0.01f && zone is not null)
            _dockShown = new Rect2(pointer, Vector2.Zero);
        _dock = zone;
        SetProcess(true);
    }

    /// <summary>松手：高亮着哪块就滑过去铺满哪块；否则就停在这儿，记下新位置。</summary>
    public void FinishDrag(GameWindow moving)
    {
        if (_dock is { } dock) moving.GlideTo(dock);
        else Remember(moving, moving.Rect);
        _dock = null;
        SetProcess(true);
    }

    /// <summary>标题栏不能被拖出桌面，否则这扇窗就再也抓不回来了。</summary>
    private Vector2 ClampInside(Vector2 pos, Vector2 size) => new(
        Mathf.Clamp(pos.X, -size.X + GameWindow.TitleHeight * 2, Mathf.Max(0, Size.X - GameWindow.TitleHeight * 2)),
        Mathf.Clamp(pos.Y, 0, Mathf.Max(0, Size.Y - GameWindow.TitleHeight)));

    /// <summary>除了正在拖的那扇，其余窗口和桌面本身的所有边。</summary>
    private (List<float> Xs, List<float> Ys) EdgesExcept(GameWindow moving)
    {
        var xs = new List<float> { 0, Size.X };
        var ys = new List<float> { 0, Size.Y };
        foreach (var other in Windows)
        {
            if (other == moving || !other.Shown) continue;
            xs.Add(other.Position.X);
            xs.Add(other.Position.X + other.Size.X);
            ys.Add(other.Position.Y);
            ys.Add(other.Position.Y + other.Size.Y);
        }
        return (xs, ys);
    }

    /// <summary>这条轴上离得最近的那条边。近边和远边都算，所以两边都能贴。</summary>
    private static float Nearest(float wanted, float length, List<float> candidates)
    {
        float best = Magnet, result = wanted;
        foreach (float c in candidates)
        {
            float near = Mathf.Abs(wanted - c);
            if (near < best) { best = near; result = c; }
            float far = Mathf.Abs(wanted + length - c);
            if (far < best) { best = far; result = c - length; }
        }
        return result;
    }

    // --- 停靠 ---------------------------------------------------------------

    /// <summary>指针落在哪个停靠区。不在任何一个就返回 null。</summary>
    public Rect2? DockZone(Vector2 pointer)
    {
        if (Size.X <= 0 || Size.Y <= 0) return null;
        var half = new Vector2(Size.X / 2, Size.Y / 2);
        if (pointer.X < EdgeZone) return new Rect2(0, 0, half.X, Size.Y);
        if (pointer.X > Size.X - EdgeZone) return new Rect2(half.X, 0, half.X, Size.Y);
        if (pointer.Y < EdgeZone) return new Rect2(Vector2.Zero, Size);
        if (pointer.Y > Size.Y - EdgeZone) return new Rect2(0, half.Y, Size.X, half.Y);
        return null;
    }

    /// <summary>停靠高亮的淡入淡出和换区时的滑动。到位了就停掉，不空转。</summary>
    public override void _Process(double delta)
    {
        float k = 1 - Mathf.Exp(-DockEase * (float)delta);
        if (_dock is { } target)
        {
            _dockShown = new Rect2(_dockShown.Position.Lerp(target.Position, k), _dockShown.Size.Lerp(target.Size, k));
            _dockAlpha = Mathf.Lerp(_dockAlpha, 1, k);
            bool settled = _dockAlpha > 0.99f
                           && _dockShown.Position.DistanceTo(target.Position) < 0.5f
                           && _dockShown.Size.DistanceTo(target.Size) < 0.5f;
            if (settled) { _dockShown = target; _dockAlpha = 1; SetProcess(false); }
        }
        else
        {
            _dockAlpha = Mathf.Lerp(_dockAlpha, 0, k);
            if (_dockAlpha < 0.01f) { _dockAlpha = 0; SetProcess(false); }
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_dockAlpha <= 0) return;
        DrawRect(_dockShown, new Color(0.45f, 0.72f, 1f, 0.18f * _dockAlpha));
        DrawRect(_dockShown, new Color(0.55f, 0.80f, 1f, 0.75f * _dockAlpha), filled: false, width: 2);
    }

    // --- 按比例重排 ---------------------------------------------------------

    /// <summary>记下这扇窗占桌面的比例。窗口摆定（放置、拖完、缩放完、停靠）时调。</summary>
    public void Remember(GameWindow window, Rect2 rect)
    {
        if (Area.X < 1 || Area.Y < 1) return;
        _share[window] = ToShare(rect);
    }

    /// <summary>像素矩形 → 占桌面的比例。</summary>
    public Rect2 ToShare(Rect2 rect)
    {
        var area = Area;
        return area.X < 1 || area.Y < 1 ? rect : new Rect2(rect.Position / area, rect.Size / area);
    }

    /// <summary>占桌面的比例 → 眼下这个桌面上的像素矩形。</summary>
    public Rect2 FromShare(Rect2 share) => new(share.Position * Area, share.Size * Area);

    /// <summary>
    /// 桌面变了尺寸：每扇窗按记下的比例重新摆，摆不下的（最小尺寸撑出去的）再往里收。
    /// </summary>
    private void Rescale()
    {
        var area = Size;
        if (area.X < 1 || area.Y < 1) return;
        foreach (var window in Windows)
        {
            if (_share.TryGetValue(window, out var share))
                window.Fit(FromShare(share));
            window.Position = ClampInside(window.Position, window.Size);
        }
    }
}
