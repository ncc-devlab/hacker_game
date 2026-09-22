using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GameHacker.Core.Net;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 交换机流量的可视化面板：谁在跟谁说话、说的什么。
/// </summary>
/// <remarks>
/// <para>概要书里虚拟交换机是<b>唯一</b>的流量观察点 —— 这也是当初决定自己写
/// 交换机而不是用 VDE/OvS 的主要理由。这个面板就是那个决定的兑现：
/// 玩家在游戏里能看到自己刚敲的 <c>ping</c> 变成了一条 ARP 加两条 ICMP。</para>
/// <para><b>线程。</b> <see cref="PacketLog.PacketCaptured"/> 在交换机的转发线程上
/// 触发，Godot 的 Node 只能在主线程碰。所以后台只入队，建行在
/// <see cref="_Process"/> 里做，而且一帧里攒到的一起建 —— 一次 ping 会突然来
/// 好几帧，逐个 <c>CallDeferred</c> 的开销比排队高得多。</para>
/// </remarks>
public sealed partial class PacketPanel : PanelContainer
{
    /// <summary>界面上最多留多少行。抓包缓冲本身另有上限，见 <see cref="PacketLog"/>。</summary>
    private const int MaxRows = 400;

    private static readonly string[] Titles =
        ["#", "时间", "VLAN", "源", "目的", "协议", "字节", "摘要"];

    /// <summary>
    /// 各列的最小宽度（像素）。
    /// </summary>
    /// <remarks>
    /// 不给的话 Tree 会把所有列均分，MAC 和 IPv6 地址被压成 "52" "ff02:"，
    /// 而摘要列空一大片 —— 实测截图里就是这个样子。
    /// </remarks>
    private static readonly int[] Widths = [56, 76, 56, 150, 150, 80, 56, 0];

    private readonly ConcurrentQueue<PacketRecord> _pending = new();

    private PacketLog? _log;
    private Tree _tree = null!;
    private TreeItem _root = null!;
    private Label _status = null!;
    private LineEdit _filter = null!;
    private Button _pause = null!;
    private bool _paused;

    public override void _Ready()
    {
        var box = new VBoxContainer();
        AddChild(box);

        box.AddChild(BuildToolbar());

        _tree = new Tree
        {
            Columns = Titles.Length,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SelectMode = Tree.SelectModeEnum.Row,
        };
        for (int i = 0; i < Titles.Length; i++)
        {
            _tree.SetColumnTitle(i, Titles[i]);
            // 只有摘要列参与伸缩，其余按内容定宽
            _tree.SetColumnExpand(i, i == Titles.Length - 1);
            if (Widths[i] > 0) _tree.SetColumnCustomMinimumWidth(i, Widths[i]);
        }
        box.AddChild(_tree);
        _root = _tree.CreateItem();
    }

    private Control BuildToolbar()
    {
        var bar = new HBoxContainer();

        _pause = new Button { Text = "暂停" };
        _pause.Pressed += () =>
        {
            _paused = !_paused;
            _pause.Text = _paused ? "继续" : "暂停";
        };
        bar.AddChild(_pause);

        var clear = new Button { Text = "清空" };
        clear.Pressed += () => { _log?.Clear(); Rebuild(); };
        bar.AddChild(clear);

        var export = new Button { Text = "导出 pcap" };
        export.Pressed += ExportPcap;
        bar.AddChild(export);

        _filter = new LineEdit
        {
            PlaceholderText = "过滤：ICMP / 10.0.0.2 / ARP / vlan20 …",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _filter.TextChanged += _ => Rebuild();
        bar.AddChild(_filter);

        _status = new Label { Text = "等待流量…" };
        bar.AddChild(_status);

        return bar;
    }

    /// <summary>接上一台交换机的抓包器。</summary>
    public void Attach(PacketLog log)
    {
        _log = log;
        log.PacketCaptured += OnPacket;
    }

    /// <summary>后台线程：只入队。</summary>
    private void OnPacket(PacketRecord record) => _pending.Enqueue(record);

    public override void _Process(double delta)
    {
        if (_pending.IsEmpty) return;

        bool added = false;
        while (_pending.TryDequeue(out var record))
        {
            if (_paused) continue;                 // 暂停时照样把队列排空，不然恢复时会雪崩
            if (!Matches(record)) continue;
            AddRow(record);
            added = true;
        }

        if (added)
        {
            Trim();
            _tree.ScrollToItem(_root.GetChild(_root.GetChildCount() - 1));
        }
        _status.Text = $"共 {_log?.Total ?? 0} 帧";
    }

    private bool Matches(PacketRecord r)
    {
        string needle = _filter.Text.Trim();
        if (needle.Length == 0) return true;
        // "vlan20" / "vlan 20" 只看某个网段
        if (needle.StartsWith("vlan", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(needle[4..].Trim(), out int vlan))
            return r.Vlan == vlan;
        return r.Protocol.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || r.Source.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || r.Destination.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || r.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private void AddRow(PacketRecord r)
    {
        var item = _tree.CreateItem(_root);
        item.SetText(0, r.Index.ToString());
        item.SetText(1, $"{r.Elapsed.TotalSeconds:F3}");
        item.SetText(2, r.Vlan.ToString());
        item.SetText(3, r.Source);
        item.SetText(4, r.Destination);
        item.SetText(5, r.Protocol);
        item.SetText(6, r.Length.ToString());
        item.SetText(7, r.Summary);

        // 颜色让玩家一眼分得出握手、寻址和数据
        Color tint = r.Protocol switch
        {
            "ARP" => Colors.Goldenrod,
            "ICMP" => Colors.MediumAquamarine,
            "ICMPv6" => Colors.DarkSeaGreen,
            "TCP" => Colors.CornflowerBlue,
            "UDP" => Colors.MediumPurple,
            "畸形" => Colors.IndianRed,
            _ => Colors.Gainsboro,
        };
        for (int c = 0; c < Titles.Length; c++) item.SetCustomColor(c, tint);
    }

    /// <summary>超出 <see cref="MaxRows"/> 的老行要显式 Free，否则节点只增不减。</summary>
    private void Trim()
    {
        while (_root.GetChildCount() > MaxRows)
            _root.GetChild(0).Free();
    }

    private void Rebuild()
    {
        foreach (var child in _root.GetChildren().ToArray()) child.Free();
        if (_log is null) return;
        foreach (var record in _log.Snapshot().Where(Matches).TakeLast(MaxRows))
            AddRow(record);
    }

    private void ExportPcap()
    {
        if (_log is null) return;
        string path = ProjectSettings.GlobalizePath(
            $"user://capture-{DateTime.Now:yyyyMMdd-HHmmss}.pcap");
        try
        {
            _log.WritePcap(path);
            _status.Text = $"已导出 {path}";
            GD.Print($"[pcap] {path}");
        }
        catch (Exception ex)
        {
            _status.Text = $"导出失败: {ex.Message}";
            GD.PushError($"[pcap] 导出失败: {ex}");
        }
    }

    public override void _ExitTree()
    {
        if (_log is not null) _log.PacketCaptured -= OnPacket;
    }
}
