using System;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 桌面上的一个图标：单击选中，双击打开。
/// </summary>
/// <remarks>
/// 图案是几笔矢量画出来的扁平小方块，不用图片：和窗口右上角那三个小圆一个路数，
/// 缩放清楚、换色容易，也不用往仓库里塞美术资源。
/// </remarks>
public partial class DesktopIcon : Control
{
    public enum Art { Mail, Terminal, Packets }

    private const float Width = 80;
    private const float TileSize = 48;

    private readonly Art _art;
    private readonly Label _label;
    private bool _hover, _highlighted;
    private int _badge;

    /// <summary>双击（或者选中后按回车）：打开它。</summary>
    public event Action? Activated;

    /// <summary>被点了一下，想被选中。选中谁由桌面统一管，保证同一时刻只有一个。</summary>
    public event Action? Selected;

    /// <summary>选中的样子。</summary>
    public bool Highlighted
    {
        get => _highlighted;
        set { _highlighted = value; QueueRedraw(); }
    }

    /// <summary>右上角的红点数字（未读邮件数）。0 就不画。</summary>
    public int Badge
    {
        get => _badge;
        set { _badge = value; QueueRedraw(); }
    }

    public DesktopIcon(string label, Art art, string tooltip)
    {
        _art = art;
        Name = $"Icon_{label}";
        TooltipText = tooltip;
        CustomMinimumSize = new Vector2(Width, 86);
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.Click;

        _label = new Label
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector2(0, TileSize + 12),
            Size = new Vector2(Width, 22),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        // 桌面底色是什么都读得清：描一圈黑边
        _label.AddThemeFontSizeOverride("font_size", 13);
        _label.AddThemeConstantOverride("outline_size", 4);
        _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        AddChild(_label);

        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; QueueRedraw(); };
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }:
                Selected?.Invoke();
                Activated?.Invoke();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                Selected?.Invoke();
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } when _highlighted:
                Activated?.Invoke();
                AcceptEvent();
                break;
        }
    }

    public override void _Draw()
    {
        if (_highlighted || _hover)
        {
            var back = new StyleBoxFlat
            {
                BgColor = _highlighted ? new Color(0.45f, 0.65f, 1f, 0.28f) : new Color(1, 1, 1, 0.08f),
                BorderColor = new Color(0.55f, 0.75f, 1f, _highlighted ? 0.6f : 0),
            };
            back.SetCornerRadiusAll(8);
            back.SetBorderWidthAll(_highlighted ? 1 : 0);
            DrawStyleBox(back, new Rect2(Vector2.Zero, Size));
        }

        var o = new Vector2((Width - TileSize) / 2, 8);
        var tile = new StyleBoxFlat { BgColor = TileColor() };
        tile.SetCornerRadiusAll(11);
        tile.ShadowColor = new Color(0, 0, 0, 0.35f);
        tile.ShadowSize = 4;
        tile.ShadowOffset = new Vector2(0, 2);
        DrawStyleBox(tile, new Rect2(o, new Vector2(TileSize, TileSize)));

        Vector2 P(float x, float y) => o + new Vector2(x, y);
        switch (_art)
        {
            case Art.Mail:
                DrawRect(new Rect2(P(10, 15), new Vector2(28, 19)), new Color(0.97f, 0.97f, 0.98f));
                DrawPolyline([P(10, 15.5f), P(24, 26), P(38, 15.5f)], new Color("2f6aa3"), 1.8f, true);
                break;
            case Art.Terminal:
                DrawPolyline([P(12, 16), P(19, 22.5f), P(12, 29)], new Color("7ee787"), 2.4f, true);
                DrawLine(P(22, 30), P(34, 30), new Color("7ee787"), 2.4f, true);
                break;
            case Art.Packets:
                DrawPolyline([P(8, 26), P(16, 26), P(20, 15), P(26, 35), P(30, 21), P(33, 26), P(40, 26)],
                             Colors.White, 2.2f, true);
                break;
        }

        if (_badge > 0)
        {
            var at = P(TileSize - 2, 2);
            DrawCircle(at, 9, new Color("e5484d"), antialiased: true);
            var font = GetThemeDefaultFont();
            string text = _badge > 9 ? "9+" : _badge.ToString();
            const int size = 12;
            var extent = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
            DrawString(font, at + new Vector2(-extent.X / 2, extent.Y / 2 - 3), text,
                       HorizontalAlignment.Left, -1, size, Colors.White);
        }
    }

    private Color TileColor() => _art switch
    {
        Art.Mail => new Color("3b82c4"),
        Art.Terminal => new Color("1f2530"),
        _ => new Color("1f8f86"),
    };
}
