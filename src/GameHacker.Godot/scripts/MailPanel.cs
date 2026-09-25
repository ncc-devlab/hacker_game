using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GameHacker.Godot;

/// <summary>一封邮件。正文可以在收到之后再改（委托信里的任务清单跟着进度走）。</summary>
public sealed class Mail
{
    public required string From { get; init; }
    public required string Subject { get; init; }
    public required DateTime At { get; init; }
    public string Body { get; set; } = "";
    public bool Read { get; set; }
}

/// <summary>
/// 桌面上的邮件：收件箱 + 阅读区。任务就是从这里来的 —— 开局收到委托信，
/// 每完成一步收到一封回信，里面写着下一步。
/// </summary>
/// <remarks>
/// <para>窗宽的时候左右分栏（列表在左、正文在右），窄的时候上下分栏，
/// 默认布局里它在右边那一条窄列里，上下分才放得下。</para>
/// <para>正文可以选中复制：提示里有这一局现生成的口令，玩家要能把它贴进终端。</para>
/// </remarks>
public partial class MailPanel : PanelContainer
{
    /// <summary>比这宽就左右分栏。</summary>
    private const float SideBySideWidth = 560;

    private readonly List<Mail> _mails = [];
    private readonly SplitContainer _split;
    private readonly VBoxContainer _list;
    private readonly RichTextLabel _reader;
    private readonly Label _header;
    private readonly ButtonGroup _group = new();
    private Mail? _open;

    /// <summary>未读数变了。桌面图标上的红点、顶栏按钮上的数字靠它。</summary>
    public event Action<int>? UnreadChanged;

    public int Unread => _mails.Count(m => !m.Read);

    public MailPanel()
    {
        Name = "Mail";
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        AddChild(column);

        _header = new Label { Text = "收件箱" };
        _header.AddThemeColorOverride("font_color", new Color(0.7f, 0.76f, 0.85f));
        column.AddChild(_header);

        _split = new SplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Vertical = true };
        column.AddChild(_split);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(180, 90),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_list);
        _split.AddChild(scroll);

        _reader = new RichTextLabel
        {
            BbcodeEnabled = true,
            SelectionEnabled = true,
            ContextMenuEnabled = true,
            ScrollActive = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(180, 80),
            FocusMode = FocusModeEnum.Click,
        };
        _split.AddChild(_reader);

        Resized += Arrange;
        VisibilityChanged += () => { if (IsVisibleInTree() && _open is not null) MarkRead(_open); };
    }

    /// <summary>收到一封信。最新的排在最上面；还没打开过任何信时直接打开它。</summary>
    public Mail Deliver(string from, string subject, string body)
    {
        var mail = new Mail { From = from, Subject = subject, Body = body, At = DateTime.Now };
        _mails.Insert(0, mail);
        Rebuild();
        if (_open is null) Show(mail);
        UnreadChanged?.Invoke(Unread);
        return mail;
    }

    /// <summary>改一封信的正文。正开着的话立刻重画。</summary>
    public void Revise(Mail mail, string body)
    {
        mail.Body = body;
        if (mail == _open) Render(mail);
    }

    private void Show(Mail mail)
    {
        _open = mail;
        Render(mail);
        // 窗口开着、玩家看得见才算读过；最小化着收到的信还是未读
        if (IsVisibleInTree()) MarkRead(mail);
        else Rebuild();
    }

    private void MarkRead(Mail mail)
    {
        bool changed = !mail.Read;
        mail.Read = true;
        Rebuild();
        if (changed) UnreadChanged?.Invoke(Unread);
    }

    private void Rebuild()
    {
        foreach (Node child in _list.GetChildren()) child.QueueFree();
        foreach (var mail in _mails)
        {
            var row = new Button
            {
                Text = $"{(mail.Read ? "   " : "●  ")}{mail.Subject}\n     {mail.From} · {mail.At:HH:mm}",
                Alignment = HorizontalAlignment.Left,
                ToggleMode = true,
                ButtonGroup = _group,
                ButtonPressed = mail == _open,
                FocusMode = FocusModeEnum.None,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Flat = true,
            };
            if (!mail.Read) row.AddThemeColorOverride("font_color", new Color(0.62f, 0.8f, 1f));
            row.Pressed += () => Show(mail);
            _list.AddChild(row);
        }
        int unread = Unread;
        _header.Text = unread > 0 ? $"收件箱 · {unread} 封未读" : "收件箱";
    }

    private void Render(Mail mail)
    {
        const string dim = "#8b95a5";
        _reader.Text =
            $"[b]{Escape(mail.Subject)}[/b]\n" +
            $"[color={dim}]发件人[/color]  {Escape(mail.From)}\n" +
            $"[color={dim}]时间[/color]      {mail.At:yyyy-MM-dd HH:mm}\n\n" +
            mail.Body;
        _reader.ScrollToLine(0);
    }

    /// <summary>纯文本进 BBCode 之前把方括号转掉，免得关卡文本里的 [ ] 被当成标签。</summary>
    public static string Escape(string text) => text.Replace("[", "[lb]");

    private void Arrange()
    {
        bool wide = Size.X >= SideBySideWidth;
        if (_split.Vertical == !wide) return;
        _split.Vertical = !wide;
        _split.SplitOffsets = [0];
    }
}
