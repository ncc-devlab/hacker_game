using System;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 桌面上的一扇窗：标题栏拖动、边角缩放、折叠、关闭。
/// </summary>
/// <remarks>
/// <para><b>为什么自己画而不是用 Godot 的 <c>Window</c> 节点</b>：<c>Window</c> 是真正的
/// 操作系统窗口，主题不跟着游戏走、三端表现各不相同，headless 自检和截图还要另想办法。
/// 这里的窗全在主画面之内，是一层普通的 <see cref="Control"/>，所以截图截得到、
/// 自检点得到、三端长得一样。</para>
/// <para>窗口内容是<b>领养</b>进来的（<see cref="Adopt"/>）：抓包面板、任务目标、
/// 终端这些节点本来在场景里怎么写就怎么写，进窗口只是换了个父节点。这样窗口系统
/// 和面板内容互不知情，加一扇新窗不用改任何面板。</para>
/// </remarks>
public partial class GameWindow : Control
{
    /// <summary>标题栏高度。折叠起来的时候整扇窗就是这么高。</summary>
    public const float TitleHeight = 28;

    /// <summary>边角上留给缩放的那一圈有多宽。</summary>
    private const float GripWidth = 6;

    private static readonly Vector2 MinSize = new(220, 120);

    private Panel _frame = null!;
    private Label _label = null!;
    private MarginContainer _body = null!;
    private Button _foldButton = null!;
    private Button _closeButton = null!;

    private bool _dragging;
    private Vector2 _grab;
    private Vector2 _unfoldedSize;

    /// <summary>这扇窗被关掉了（玩家点了 ×）。顶栏那排开关靠它同步。</summary>
    public event Action<GameWindow>? Closed;

    /// <summary>折叠起来了：只剩标题栏。</summary>
    public bool Folded { get; private set; }

    /// <summary>窗口标题，顶栏那排开关也用它。</summary>
    public string Title
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    private Desktop Desk => GetParent<Desktop>();

    public GameWindow(string title, bool closable = true)
    {
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
        barRow.AddThemeConstantOverride("separation", 4);
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

        _foldButton = new Button { Text = "▭", Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "折叠 / 展开" };
        _foldButton.Pressed += ToggleFold;
        barRow.AddChild(_foldButton);

        _closeButton = new Button { Text = "×", Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "关闭", Visible = closable };
        _closeButton.Pressed += Close;
        barRow.AddChild(_closeButton);

        // --- 内容 ---
        _body = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass };
        foreach (string side in new[] { "left", "top", "right", "bottom" })
            _body.AddThemeConstantOverride($"margin_{side}", 4);
        column.AddChild(_body);

        AddGrips();
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

    /// <summary>放到桌面上的这个位置。</summary>
    public void PlaceAt(Rect2 rect)
    {
        Position = rect.Position;
        Size = new Vector2(Mathf.Max(rect.Size.X, MinSize.X), Mathf.Max(rect.Size.Y, MinSize.Y));
        _unfoldedSize = Size;
        if (Folded) Size = new Vector2(Size.X, TitleHeight);
    }

    public void ToggleFold()
    {
        Folded = !Folded;
        if (Folded)
        {
            _unfoldedSize = Size;
            _body.Visible = false;
            Size = new Vector2(Size.X, TitleHeight);
        }
        else
        {
            _body.Visible = true;
            Size = _unfoldedSize;
        }
    }

    public void Close()
    {
        Visible = false;
        Closed?.Invoke(this);
    }

    /// <summary>重新打开。位置还在原处，不用玩家再找一遍。</summary>
    public void Reopen()
    {
        Visible = true;
        Desk.BringToFront(this);
    }

    // --- 拖动 ---------------------------------------------------------------

    private void OnTitleInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }:
                ToggleFold();
                AcceptEvent();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
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
                var wanted = GetGlobalMousePosition() - _grab - Desk.GlobalPosition;
                Position = Desk.SnapPosition(this, wanted);
                Desk.AimDock(GetGlobalMousePosition() - Desk.GlobalPosition);
                AcceptEvent();
                break;
        }
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
            grip.Dragged += OnGripDragged;
            grip.Grabbed += () => Desk.BringToFront(this);
            AddChild(grip);
        }
    }

    private void OnGripDragged(Vector2I direction, Vector2 delta)
    {
        if (Folded) return;
        var rect = new Rect2(Position, Size);

        if (direction.X < 0)
        {
            float right = rect.Position.X + rect.Size.X;
            rect.Position = new Vector2(Mathf.Min(rect.Position.X + delta.X, right - MinSize.X), rect.Position.Y);
            rect.Size = new Vector2(right - rect.Position.X, rect.Size.Y);
        }
        else if (direction.X > 0)
            rect.Size = new Vector2(Mathf.Max(rect.Size.X + delta.X, MinSize.X), rect.Size.Y);

        if (direction.Y < 0)
        {
            float bottom = rect.Position.Y + rect.Size.Y;
            rect.Position = new Vector2(rect.Position.X, Mathf.Min(rect.Position.Y + delta.Y, bottom - MinSize.Y));
            rect.Size = new Vector2(rect.Size.X, bottom - rect.Position.Y);
        }
        else if (direction.Y > 0)
            rect.Size = new Vector2(rect.Size.X, Mathf.Max(rect.Size.Y + delta.Y, MinSize.Y));

        Position = rect.Position;
        Size = rect.Size;
        _unfoldedSize = Size;
    }

    /// <summary>窗口边上那一条看不见的缩放热区。</summary>
    private sealed partial class ResizeGrip : Control
    {
        private readonly Vector2I _direction;
        private bool _dragging;

        public event Action<Vector2I, Vector2>? Dragged;
        public event Action? Grabbed;

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
                case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
                    _dragging = click.Pressed;
                    if (click.Pressed) Grabbed?.Invoke();
                    AcceptEvent();
                    break;
                case InputEventMouseMotion motion when _dragging:
                    Dragged?.Invoke(_direction, motion.Relative);
                    AcceptEvent();
                    break;
            }
        }
    }
}
