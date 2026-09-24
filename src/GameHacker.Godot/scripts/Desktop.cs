using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 摆窗口的那块地方：管层叠次序、拖动时的吸附、以及靠边停靠。
/// </summary>
/// <remarks>
/// <para>它<b>不是</b>布局容器 —— 子节点的 <c>Position</c> 由自己说了算，
/// 容器会把它们排回去。</para>
/// <para>两种吸附，分工不同：</para>
/// <list type="bullet">
/// <item><b>对齐吸附</b>（悄悄发生）：拖到离别的窗或桌面边缘 <see cref="Magnet"/> 像素
/// 以内时边对边贴上去。玩家想拼出一个整齐的布局不用瞄准。</item>
/// <item><b>靠边停靠</b>（有高亮）：把指针拖到桌面边缘，高亮出一块区域，松手就铺满它。
/// 左右半屏、顶部整屏、底部半屏 —— 常用的几种布局一下就摆好。</item>
/// </list>
/// </remarks>
public partial class Desktop : Control
{
    /// <summary>边对边吸附的距离。</summary>
    private const float Magnet = 8;

    /// <summary>指针进到离边缘这么近就触发停靠。</summary>
    private const float EdgeZone = 20;

    private Rect2? _dock;

    public IEnumerable<GameWindow> Windows => GetChildren().OfType<GameWindow>();

    public override void _Ready()
    {
        // 窗口自己定位置，别让任何东西替它排版
        MouseFilter = MouseFilterEnum.Pass;
        Resized += KeepWindowsInside;
    }

    /// <summary>开一扇新窗，把 <paramref name="content"/> 搬进去。</summary>
    public GameWindow Open(string title, Control content, bool closable = true)
    {
        var window = new GameWindow(title, closable) { Name = $"Window_{title}" };
        AddChild(window);
        window.Adopt(content);
        return window;
    }

    public void BringToFront(GameWindow window) => MoveChild(window, GetChildCount() - 1);

    // --- 吸附 ---------------------------------------------------------------

    /// <summary>
    /// 拖到 <paramref name="wanted"/> 时实际该落在哪：边缘够近就贴上去。
    /// </summary>
    public Vector2 SnapPosition(GameWindow moving, Vector2 wanted)
    {
        var (xs, ys) = EdgesExcept(moving);
        float x = Nearest(wanted.X, moving.Size.X, xs);
        float y = Nearest(wanted.Y, moving.Size.Y, ys);

        // 标题栏不能被拖出桌面，否则这扇窗就再也抓不回来了
        x = Mathf.Clamp(x, -moving.Size.X + GameWindow.TitleHeight * 2, Size.X - GameWindow.TitleHeight * 2);
        y = Mathf.Clamp(y, 0, Mathf.Max(0, Size.Y - GameWindow.TitleHeight));
        return new Vector2(x, y);
    }

    /// <summary>拖动中：指针在边缘就高亮出要停靠的那块区域。</summary>
    public void AimDock(Vector2 pointer)
    {
        var zone = DockZone(pointer);
        if (zone == _dock) return;
        _dock = zone;
        QueueRedraw();
    }

    /// <summary>松手：高亮着哪块就铺满哪块。</summary>
    public void FinishDrag(GameWindow window)
    {
        if (_dock is { } rect) window.PlaceAt(rect);
        _dock = null;
        QueueRedraw();
    }

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

    public override void _Draw()
    {
        if (_dock is not { } rect) return;
        DrawRect(rect, new Color(0.45f, 0.72f, 1f, 0.18f));
        DrawRect(rect, new Color(0.55f, 0.80f, 1f, 0.75f), filled: false, width: 2);
    }

    /// <summary>除了正在拖的那扇，其余窗口和桌面本身的所有边。</summary>
    private (List<float> Xs, List<float> Ys) EdgesExcept(GameWindow moving)
    {
        var xs = new List<float> { 0, Size.X };
        var ys = new List<float> { 0, Size.Y };
        foreach (var other in Windows)
        {
            if (other == moving || !other.Visible) continue;
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

    /// <summary>窗口变小了也不能把窗丢在画面外面。</summary>
    private void KeepWindowsInside()
    {
        foreach (var window in Windows)
        {
            float x = Mathf.Clamp(window.Position.X, -window.Size.X + GameWindow.TitleHeight * 2,
                                  Mathf.Max(0, Size.X - GameWindow.TitleHeight * 2));
            float y = Mathf.Clamp(window.Position.Y, 0, Mathf.Max(0, Size.Y - GameWindow.TitleHeight));
            window.Position = new Vector2(x, y);
        }
    }
}
