using System;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 桌面上的一扇窗：标题栏拖动、边角缩放、最小化 / 最大化 / 关闭。
/// </summary>
/// <remarks>
/// <para><b>为什么自己画而不是用 Godot 的 <c>Window</c> 节点</b>：<c>Window</c> 是真正的
/// 操作系统窗口，主题不跟着游戏走、三端表现各不相同，headless 自检和截图还要另想办法。
/// 这里的窗全在主画面之内，是一层普通的 <see cref="Control"/>，所以截图截得到、
/// 自检点得到、三端长得一样。</para>
/// <para>窗口内容是<b>领养</b>进来的（<see cref="Adopt"/>）：抓包面板、邮件、
/// 终端这些节点本来在场景里怎么写就怎么写，进窗口只是换了个父节点。这样窗口系统
/// 和面板内容互不知情，加一扇新窗不用改任何面板。</para>
/// <para>三种「不在眼前」分得清清楚楚：<b>最小化</b>是收进顶栏、按钮还在，点一下就回来；
/// <b>关闭</b>是连顶栏按钮都没了，要从桌面图标重新打开；<b>最大化</b>是铺满桌面，
/// 再点一下回到原来那块。</para>
/// </remarks>
public partial class GameWindow : Control
{
    /// <summary>标题栏高度。</summary>
    public const float TitleHeight = 28;

    /// <summary>边角上留给缩放的那一圈有多宽。</summary>
    private const float GripWidth = 6;

    /// <summary>停靠、最大化时滑过去用多久。短到不耽误事，长到看得出是从哪儿去的哪儿。</summary>
    private const double GlideSeconds = 0.16;

    /// <summary>最小化 / 打开时淡出淡入用多久。</summary>
    private const double FadeSeconds = 0.12;

    /// <summary>窗口能缩到的最小尺寸。桌面随画面缩小时也读它。</summary>
    public static readonly Vector2 MinSize = new(220, 120);

    private Panel _frame = null!;
    private Label _label = null!;
    private MarginContainer _body = null!;
    private CircleButton[] _circles = null!;

    private bool _dragging;
    private Vector2 _grab;
    private Rect2 _resizeFrom;
    private Rect2 _restoreShare;   // 最大化之前那块地方，按占桌面的比例记，最大化期间画面变了也回得去
    private Tween? _glide;
    private Tween? _fade;

    // 前台 / 后台两套边框：前台那扇投影更深，一眼看得出谁压着谁
    private StyleBox? _activeStyle;
    private StyleBox? _inactiveStyle;

    /// <summary>这扇窗被关掉了（玩家点了关闭）。</summary>
    public event Action<GameWindow>? Closed;

    /// <summary>开着 / 最小化 / 关闭之间变了。顶栏那排按钮靠它同步。</summary>
    public event Action<GameWindow>? StateChanged;

    /// <summary>开着（在顶栏上有按钮），包括最小化着的。关掉了就是 false。</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>最小化了：收进顶栏，按钮还在。</summary>
    public bool Minimized { get; private set; }

    /// <summary>铺满了整个桌面。</summary>
    public bool Maximized { get; private set; }

    /// <summary>能不能关。玩家自己那台机器的主终端不能关 —— 关了就没有别的路回到这台机器。</summary>
    public bool Closable { get; }

    /// <summary>是不是最前面那扇（标题亮着的那扇）。</summary>
    public bool Active { get; private set; } = true;

    /// <summary>
    /// 摆在桌面上、看得见。正在淡出的那扇已经不算了 —— 下面那扇立刻亮起来，
    /// 点击也穿过它落到下面。
    /// </summary>
    public bool Shown => Visible && IsOpen && !Minimized;

    /// <summary>这扇窗现在占的那块地方。</summary>
    public Rect2 Rect => new(Position, Size);

    /// <summary>窗口标题，顶栏那排按钮也用它。</summary>
    public string Title
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    private Desktop Desk => GetParent<Desktop>();

    public GameWindow(string title, bool closable = true)
    {
        Closable = closable;
        MouseFilter = MouseFilterEnum.Pass;
        ClipContents = false;

        _frame = new Panel { MouseFilter = MouseFilterEnum.Pass };
        _frame.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_frame);

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        column.SetAnchorsPreset(LayoutPreset.FullRect);
        column.AddThemeConstantOverride("separation", 0);
        AddChild(column);

        // --- 标题栏 ---
        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, TitleHeight) };
        bar.GuiInput += OnTitleInput;
        column.AddChild(bar);

        var barRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        barRow.AddThemeConstantOverride("separation", 2);
        bar.AddChild(barRow);

        barRow.AddChild(new Control { CustomMinimumSize = new Vector2(8, 0), MouseFilter = MouseFilterEnum.Ignore });
        _label = new Label
        {
            Text = title,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        barRow.AddChild(_label);

        // 右上角三个小圆：最小化、最大化、关闭。同一种扁平样式，平时是颜色点，
        // 指针移上去才显出符号；不在前台的窗三个点一起变灰
        _circles =
        [
            new CircleButton(CircleButton.Glyph.Minimize, new Color("f5bf4f"), "最小化"),
            new CircleButton(CircleButton.Glyph.Maximize, new Color("61c554"), "最大化 / 还原"),
            new CircleButton(CircleButton.Glyph.Close, new Color("ed6a5e"), closable ? "关闭" : "这扇窗不能关"),
        ];
        _circles[0].Clicked += Minimize;
        _circles[1].Clicked += ToggleMaximize;
        _circles[2].Clicked += Close;
        _circles[2].Enabled = closable;
        foreach (var circle in _circles) barRow.AddChild(circle);
        barRow.AddChild(new Control { CustomMinimumSize = new Vector2(6, 0), MouseFilter = MouseFilterEnum.Ignore });

        // --- 内容 ---
        _body = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass };
        foreach (string side in new[] { "left", "top", "right", "bottom" })
            _body.AddThemeConstantOverride($"margin_{side}", 4);
        column.AddChild(_body);

        AddGrips();
    }

    public override void _Ready()
    {
        // 主题要进了场景树才查得到，所以边框在这里才做
        if (_frame.GetThemeStylebox("panel") is StyleBoxFlat flat)
        {
            _activeStyle = Shadowed(flat, 14, 0.55f);
            _inactiveStyle = Shadowed(flat, 5, 0.30f);
        }
        SetActive(Active);
    }

    private static StyleBoxFlat Shadowed(StyleBoxFlat source, int size, float alpha)
    {
        var box = (StyleBoxFlat)source.Duplicate();
        box.ShadowSize = size;
        box.ShadowColor = new Color(0, 0, 0, alpha);
        box.ShadowOffset = new Vector2(0, size / 3f);
        return box;
    }

    /// <summary>把一个已有的面板搬进这扇窗。它原来挂在哪儿不重要。</summary>
    public void Adopt(Control content)
    {
        content.GetParent()?.RemoveChild(content);
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        content.SizeFlagsVertical = SizeFlags.ExpandFill;
        content.Visible = true;
        _body.AddChild(content);
    }

    // --- 摆放 ---------------------------------------------------------------

    /// <summary>
    /// 放到桌面上的这个位置，并记成这扇窗的布局 —— 之后画面变大变小，它按比例跟着走。
    /// </summary>
    public void PlaceAt(Rect2 rect)
    {
        Fit(rect);
        Maximized = false;
        GetParentOrNull<Desktop>()?.Remember(this, Rect);
    }

    /// <summary>摆到这里，但不改记下的布局。桌面按比例重排时用。</summary>
    internal void Fit(Rect2 rect)
    {
        StopGlide();
        Position = rect.Position;
        Size = new Vector2(Mathf.Max(rect.Size.X, MinSize.X), Mathf.Max(rect.Size.Y, MinSize.Y));
    }

    /// <summary>滑到这个位置（停靠、最大化用）。布局立刻按终点记下，动画只是给眼睛看的。</summary>
    public void GlideTo(Rect2 rect)
    {
        StopGlide();
        var size = new Vector2(Mathf.Max(rect.Size.X, MinSize.X), Mathf.Max(rect.Size.Y, MinSize.Y));
        GetParentOrNull<Desktop>()?.Remember(this, new Rect2(rect.Position, size));

        _glide = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _glide.TweenProperty(this, "position", rect.Position, GlideSeconds);
        _glide.TweenProperty(this, "size", size, GlideSeconds);
    }

    private void StopGlide()
    {
        _glide?.Kill();
        _glide = null;
    }

    // --- 最小化 / 最大化 / 关闭 ---------------------------------------------

    public void ToggleMaximize()
    {
        if (Maximized)
        {
            Maximized = false;
            GlideTo(Desk.FromShare(_restoreShare));
        }
        else
        {
            _restoreShare = Desk.ToShare(Rect);
            GlideTo(new Rect2(Vector2.Zero, Desk.Size));
            Maximized = true;
        }
        Desk.BringToFront(this);
    }

    /// <summary>收进顶栏。顶栏上的按钮还在，点一下就回来。</summary>
    public void Minimize()
    {
        if (!IsOpen || Minimized) return;
        Minimized = true;
        StateChanged?.Invoke(this);
        FadeOut();
        Desk.RefreshActive();
    }

    /// <summary>关掉：顶栏上的按钮也没了，要从桌面图标重新打开。</summary>
    public void Close()
    {
        if (!Closable || !IsOpen) return;
        IsOpen = false;
        Minimized = false;
        Closed?.Invoke(this);
        StateChanged?.Invoke(this);
        FadeOut();
        Desk.RefreshActive();
    }

    /// <summary>从最小化或关闭里回来，提到最前。位置还在原处，不用玩家再找一遍。</summary>
    public void Reopen()
    {
        bool wasHidden = !Visible || _fade is not null;
        IsOpen = true;
        Minimized = false;
        StateChanged?.Invoke(this);
        if (wasHidden) FadeIn();
        Desk.BringToFront(this);
    }

    /// <summary>一开始就是关着的（比如多开的终端，要等玩家从桌面图标打开）。不播动画。</summary>
    public void StartClosed()
    {
        IsOpen = false;
        Minimized = false;
        Visible = false;
        StateChanged?.Invoke(this);
    }

    private void FadeOut()
    {
        _fade?.Kill();
        PivotOffset = Size / 2;
        _fade = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        _fade.TweenProperty(this, "modulate:a", 0f, FadeSeconds);
        _fade.TweenProperty(this, "scale", new Vector2(0.94f, 0.94f), FadeSeconds);
        _fade.Chain().TweenCallback(Callable.From(() =>
        {
            _fade = null;
            Visible = false;
            Modulate = Colors.White;
            Scale = Vector2.One;
        }));
    }

    private void FadeIn()
    {
        _fade?.Kill();
        PivotOffset = Size / 2;
        Visible = true;
        Modulate = new Color(1, 1, 1, 0);
        Scale = new Vector2(0.94f, 0.94f);
        _fade = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        _fade.TweenProperty(this, "modulate:a", 1f, FadeSeconds);
        _fade.TweenProperty(this, "scale", Vector2.One, FadeSeconds);
        _fade.Chain().TweenCallback(Callable.From(() => _fade = null));
    }

    /// <summary>标题亮 / 暗、投影深 / 浅、三个小圆有色 / 灰。由桌面在层叠次序变了之后统一设。</summary>
    internal void SetActive(bool active)
    {
        Active = active;
        _label.Modulate = active ? Colors.White : new Color(1, 1, 1, 0.5f);
        foreach (var circle in _circles) circle.Lit = active;
        var style = active ? _activeStyle : _inactiveStyle;
        if (style is not null) _frame.AddThemeStyleboxOverride("panel", style);
    }

    // --- 拖动 ---------------------------------------------------------------

    private void OnTitleInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }:
                _dragging = false;
                ToggleMaximize();
                AcceptEvent();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                StopGlide();
                _dragging = true;
                _grab = GetGlobalMousePosition() - GlobalPosition;
                Desk.BringToFront(this);
                AcceptEvent();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _dragging:
                _dragging = false;
                Desk.FinishDrag(this);
                AcceptEvent();
                break;

            case InputEventMouseMotion when _dragging:
                if (Maximized) UnmaximizeUnderPointer();
                // 用指针的绝对位置算，不累加每一帧的相对位移：窗口始终钉在指针下面，不会越拖越偏
                var wanted = GetGlobalMousePosition() - _grab - Desk.GlobalPosition;
                Desk.DragTo(this, wanted);
                Desk.AimDock(GetGlobalMousePosition() - Desk.GlobalPosition);
                AcceptEvent();
                break;
        }
    }

    /// <summary>
    /// 拖着最大化的窗走：先缩回原来的大小，而且指针还按住标题栏上同一个比例的位置 ——
    /// 抓的是标题中间，缩回去以后抓的还是标题中间。
    /// </summary>
    private void UnmaximizeUnderPointer()
    {
        var restore = Desk.FromShare(_restoreShare);
        float ratio = Size.X > 0 ? _grab.X / Size.X : 0.5f;
        Maximized = false;
        Size = new Vector2(Mathf.Max(restore.Size.X, MinSize.X), Mathf.Max(restore.Size.Y, MinSize.Y));
        _grab = new Vector2(ratio * Size.X, Mathf.Min(_grab.Y, TitleHeight - 1));
    }

    // --- 缩放 ---------------------------------------------------------------

    /// <summary>
    /// 八个方向的缩放手柄。
    /// </summary>
    /// <remarks>
    /// 做成独立的小节点，而不是在窗口自己身上判断鼠标落在哪一圈：窗口里装的是终端，
    /// 它会把鼠标事件全吃掉，父节点根本收不到。手柄压在最上层就没这个问题。
    /// </remarks>
    private void AddGrips()
    {
        foreach (var direction in new[]
                 {
                     new Vector2I(-1, 0), new Vector2I(1, 0), new Vector2I(0, -1), new Vector2I(0, 1),
                     new Vector2I(-1, -1), new Vector2I(1, -1), new Vector2I(-1, 1), new Vector2I(1, 1),
                 })
        {
            var grip = new ResizeGrip(direction);
            grip.Dragged += ResizeBy;
            grip.Grabbed += () =>
            {
                BeginResize();
                Desk.BringToFront(this);
            };
            grip.Released += () => Desk.Remember(this, Rect);
            AddChild(grip);
        }
    }

    /// <summary>
    /// 从按下手柄那一刻的矩形出发，往 <paramref name="direction"/> 那边拉了 <paramref name="offset"/>。
    /// </summary>
    /// <remarks>
    /// 按「从按下到现在一共拉了多少」算，而不是把每一帧的相对位移累加上去：
    /// 缩到最小尺寸以后指针还在往里走，累加的话再往外拉时边框会先猛地跳一下、
    /// 从此和指针对不上；按总位移算，边框永远停在指针底下。
    /// </remarks>
    internal void ResizeBy(Vector2I direction, Vector2 offset)
    {
        var from = _resizeFrom;
        float left = from.Position.X, top = from.Position.Y;
        float right = from.End.X, bottom = from.End.Y;

        if (direction.X < 0) left = Mathf.Min(left + offset.X, right - MinSize.X);
        else if (direction.X > 0) right = Mathf.Max(right + offset.X, left + MinSize.X);
        if (direction.Y < 0) top = Mathf.Min(top + offset.Y, bottom - MinSize.Y);
        else if (direction.Y > 0) bottom = Mathf.Max(bottom + offset.Y, top + MinSize.Y);

        Position = new Vector2(left, top);
        Size = new Vector2(right - left, bottom - top);
    }

    /// <summary>按下缩放手柄：记下起点。拉最大化的窗就等于不再最大化。</summary>
    internal void BeginResize()
    {
        StopGlide();
        Maximized = false;
        _resizeFrom = Rect;
    }

    /// <summary>标题栏右上角的一个小圆按钮。</summary>
    private sealed partial class CircleButton : Control
    {
        public enum Glyph { Minimize, Maximize, Close }

        private const float Radius = 6;
        private static readonly Color Idle = new(0.42f, 0.44f, 0.48f);

        private readonly Glyph _glyph;
        private readonly Color _color;
        private bool _hover, _down, _lit = true, _enabled = true;

        public event Action? Clicked;

        /// <summary>所在的窗在前台：有颜色。不在前台时灰掉，指针移上来才亮。</summary>
        public bool Lit { set { _lit = value; QueueRedraw(); } }

        /// <summary>不可用：一直是灰的，也点不动。</summary>
        public bool Enabled
        {
            set
            {
                _enabled = value;
                MouseDefaultCursorShape = value ? CursorShape.PointingHand : CursorShape.Arrow;
                QueueRedraw();
            }
        }

        public CircleButton(Glyph glyph, Color color, string tooltip)
        {
            _glyph = glyph;
            _color = color;
            TooltipText = tooltip;
            CustomMinimumSize = new Vector2(20, 20);
            SizeFlagsVertical = SizeFlags.ShrinkCenter;
            MouseFilter = MouseFilterEnum.Stop;
            FocusMode = FocusModeEnum.None;
            MouseDefaultCursorShape = CursorShape.PointingHand;
            MouseEntered += () => { _hover = true; QueueRedraw(); };
            MouseExited += () => { _hover = false; _down = false; QueueRedraw(); };
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left } click) return;
            AcceptEvent();   // 别让标题栏把它当成拖动或双击
            if (!_enabled) return;
            if (click.Pressed) _down = true;
            else if (_down)
            {
                _down = false;
                if (_hover) Clicked?.Invoke();
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            var c = Size / 2;
            bool colored = _enabled && (_lit || _hover);
            var fill = colored ? _color : Idle;
            if (_down) fill = fill.Darkened(0.2f);
            DrawCircle(c, Radius, fill, antialiased: true);
            if (!_enabled || !_hover) return;

            var ink = new Color(0, 0, 0, 0.55f);
            const float a = 2.8f;
            switch (_glyph)
            {
                case Glyph.Minimize:
                    DrawLine(c + new Vector2(-a, 0), c + new Vector2(a, 0), ink, 1.5f, true);
                    break;
                case Glyph.Maximize:
                    DrawRect(new Rect2(c - new Vector2(a - 0.4f, a - 0.4f), new Vector2(2 * a - 0.8f, 2 * a - 0.8f)),
                             ink, filled: false, width: 1.3f);
                    break;
                case Glyph.Close:
                    DrawLine(c + new Vector2(-a, -a), c + new Vector2(a, a), ink, 1.5f, true);
                    DrawLine(c + new Vector2(-a, a), c + new Vector2(a, -a), ink, 1.5f, true);
                    break;
            }
        }
    }

    /// <summary>窗口边上那一条看不见的缩放热区。</summary>
    private sealed partial class ResizeGrip : Control
    {
        private readonly Vector2I _direction;
        private bool _dragging;
        private Vector2 _pressedAt;

        /// <summary>方向，以及从按下到现在指针一共挪了多少。</summary>
        public event Action<Vector2I, Vector2>? Dragged;
        public event Action? Grabbed;
        public event Action? Released;

        public ResizeGrip(Vector2I direction)
        {
            _direction = direction;
            MouseFilter = MouseFilterEnum.Stop;
            MouseDefaultCursorShape = (direction.X, direction.Y) switch
            {
                (0, _) => CursorShape.Vsize,
                (_, 0) => CursorShape.Hsize,
                (-1, -1) or (1, 1) => CursorShape.Fdiagsize,
                _ => CursorShape.Bdiagsize,
            };
            // 贴在对应的边或角上：锚点跟着方向走，尺寸靠 offset 撑出 GripWidth 那一圈
            AnchorLeft = direction.X > 0 ? 1 : 0;
            AnchorRight = direction.X < 0 ? 0 : 1;
            AnchorTop = direction.Y > 0 ? 1 : 0;
            AnchorBottom = direction.Y < 0 ? 0 : 1;
            OffsetLeft = direction.X > 0 ? -GripWidth : 0;
            OffsetRight = direction.X < 0 ? GripWidth : 0;
            OffsetTop = direction.Y > 0 ? -GripWidth : 0;
            OffsetBottom = direction.Y < 0 ? GripWidth : 0;
        }

        public override void _GuiInput(InputEvent @event)
        {
            switch (@event)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                    _dragging = true;
                    _pressedAt = GetGlobalMousePosition();
                    Grabbed?.Invoke();
                    AcceptEvent();
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _dragging:
                    _dragging = false;
                    Released?.Invoke();
                    AcceptEvent();
                    break;
                case InputEventMouseMotion when _dragging:
                    Dragged?.Invoke(_direction, GetGlobalMousePosition() - _pressedAt);
                    AcceptEvent();
                    break;
            }
        }
    }
}
