using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 无人值守自检：把技术脊椎从键盘一路走到客户机再走回屏幕。
/// </summary>
/// <remarks>
/// <para>存在的理由是三端验证。M0/M1 的测试跑在 Linux 上、走的是裸串口，
/// 绕开了 Godot 的输入法和 GDExtension —— 而这两样正是换平台最容易坏的地方。
/// 所以这里刻意<b>不</b>直接往串口写字节，而是伪造 <c>InputEventKey</c>
/// 丢进 Godot 的输入队列，让它经 godot-xterm 的键盘映射出来，
/// 再看客户机的回显有没有渲染到终端上。</para>
/// <para>断言读的是 <c>copy_all()</c>，即 libtsm 渲染后的屏幕文本，
/// 不是串口流水 —— 渲染坏掉也要能抓到。</para>
/// </remarks>
public static class SelfTest
{
    private readonly record struct Step(string Name, bool Ok, string Detail);

    private static readonly List<Step> Results = [];

    private static bool Check(string name, bool ok, string detail = "")
    {
        Results.Add(new Step(name, ok, detail));
        GD.Print($"[selftest] {(ok ? "ok  " : "FAIL")} {name}{(detail.Length > 0 ? "  " + detail : "")}");
        return ok;
    }

    /// <summary>跑完整套检查，返回是否全绿。</summary>
    /// <param name="openExtraTerminal">桌面上双击「终端」图标会做的事。返回开出来的终端节点。</param>
    public static async Task<bool> RunAsync(Node host, Control terminal, string bootDetail,
                                            Func<Control?>? openExtraTerminal = null)
    {
        Results.Clear();
        Check("虚拟机启动并互通", bootDetail.Contains("互通"), bootDetail);

        // --- 焦点 ---------------------------------------------------------
        // 玩家要能点进终端打字，焦点拿不到的话后面所有键盘断言都没意义。
        terminal.FocusMode = Control.FocusModeEnum.All;
        terminal.GrabFocus();
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        Check("终端能拿到键盘焦点", terminal.HasFocus(),
              $"focus_mode={terminal.FocusMode}");

        // --- 键盘 -> 客户机 -> 渲染 ---------------------------------------
        terminal.Call("clear");
        await TypeAsync(host, "\n");                 // 讨一个干净的提示符
        await Task.Delay(1200);

        terminal.Call("clear");
        await TypeAsync(host, "ls /et");
        await Task.Delay(1500);
        string beforeTab = ScreenText(terminal);
        Check("普通按键经 Godot 输入链到达客户机并回显",
              beforeTab.Contains("ls /et"), Tail(beforeTab));

        await TypeKeyAsync(host, Key.Tab);
        await Task.Delay(2000);
        string afterTab = ScreenText(terminal);
        Check("Tab 触发客户机补全（ls /et -> ls /etc/）",
              afterTab.Contains("ls /etc/"), Tail(afterTab));

        // 收拾干净，别把半条命令留在 shell 里
        await TypeKeyAsync(host, Key.U, ctrl: true);
        await TypeAsync(host, "\n");
        await Task.Delay(800);

        // --- 渲染 ---------------------------------------------------------
        terminal.Call("clear");
        await TypeAsync(host, "printf '\\033[31mRED\\033[0m-\\033[32mGREEN\\033[0m\\n'\n");
        await Task.Delay(1500);
        string colored = ScreenText(terminal);
        Check("ANSI 输出渲染到屏幕", colored.Contains("RED-GREEN"), Tail(colored));

        // --- 伪装 ---------------------------------------------------------
        // 三端都要查：-smbios / -cpu 是 QEMU 的能力，不同平台的构建未必一致。
        string uname = await CaptureAsync(host, terminal, "uname -a");
        Check("uname 不再暴露 -virt", uname.Length > 0 && !uname.Contains("virt"), Tail(uname));

        string ver = await CaptureAsync(host, terminal, "cat /proc/version");
        Check("/proc/version 已覆盖", ver.Length > 0 && !ver.Contains("virt"), Tail(ver));

        string cmdline = await CaptureAsync(host, terminal, "cat /proc/cmdline");
        Check("/proc/cmdline 不泄露 m0.* 与 console=",
              cmdline.Length > 0 && !cmdline.Contains("m0.") && !cmdline.Contains("ttyS0"),
              Tail(cmdline));

        string dmi = await CaptureAsync(host, terminal, "cat /sys/class/dmi/id/sys_vendor");
        Check("DMI 厂商已伪装", dmi.Length > 0 && !dmi.Contains("QEMU"), Tail(dmi));

        string cpu = await CaptureAsync(host, terminal, "grep -m1 'model name' /proc/cpuinfo");
        Check("CPU 型号已伪装", cpu.Length > 0 && !cpu.Contains("QEMU"), Tail(cpu));

        string nic = await CaptureAsync(host, terminal, "cat /sys/class/net/eth0/device/vendor");
        Check("网卡 PCI 厂商不是 virtio(0x1af4)",
              nic.Contains("0x8086"), Tail(nic));

        string dmesg = await CaptureAsync(host, terminal, "dmesg | grep -ci qemu");
        Check("dmesg 里没有 qemu 字样", dmesg.Contains("0"), Tail(dmesg));

        // --- 桌面「终端」图标开出来的新终端 --------------------------------
        // 窗口建了、串口没接的话，玩家看到的是一个敲不进字的终端 —— 实测踩到过：
        // 规格里忘了要串口，Core 层的集成测试照样全绿，只有这条端到端的路看得出来
        if (openExtraTerminal is not null)
        {
            var extra = openExtraTerminal();
            if (extra is null)
                Check("桌面上开得出新终端", false, "一个都开不出来");
            else
            {
                await Task.Delay(1500);   // 等它补敲的那一下回车换回一行提示符
                extra.GrabFocus();
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                // 不能直接敲 tty：它的输出 /dev/ttyS2 里含有命令本身，会被 CaptureAsync 当成回显跳过
                string tty = await CaptureAsync(host, extra, "echo EXTRA-$(tty)");
                Check("桌面上新开的终端能输入并回显", tty.StartsWith("EXTRA-/dev/ttyS"), $"{tty} | focus_mode={extra.FocusMode} has_focus={extra.HasFocus()} "
                      + $"owner={extra.GetViewport().GuiGetFocusOwner()?.Name} | {Tail(ScreenText(extra))}");

                // 关回去，把焦点还给主终端，别影响后面的探针
                Node walk = extra;
                while (walk is not null && walk is not GameWindow) walk = walk.GetParent();
                (walk as GameWindow)?.Close();
                terminal.GrabFocus();
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }

        // --- 探针：玩家的真实路径（鼠标点击聚焦 + Tab/方向键）--------------
        // 放在最后：它要移动终端窗、重设根窗口尺寸、还会在 shell 里留下半截状态，
        // 夹在上面那些断言中间会把它们带偏（实测后续命令读到错行、冒出 bc 报错）。
        // 前面用 GrabFocus 验的是「焦点在时输入链通不通」，这里补的是玩家真实走的
        // 「点一下终端才聚焦」那条路 —— issue「Tab/方向键不好用」正出在这条路上。
        if (System.Environment.GetEnvironmentVariable("GAMEHACKER_PROBE_CLICK") is "1")
            await ProbeClickFocusAsync(host, terminal);

        int failed = Results.FindAll(r => !r.Ok).Count;
        GD.Print($"[selftest] {Results.Count - failed}/{Results.Count} 项通过");
        return failed == 0;
    }

    /// <summary>
    /// 玩家的真实路径：鼠标点进终端才聚焦，然后 Tab 补全、方向键移光标都要能用。
    /// </summary>
    /// <remarks>
    /// 无头下根 Window 尺寸近乎为零，full-rect 控件会坍缩到负坐标，按全局坐标点击
    /// 命中不了 —— 所以先给根窗口一个真实尺寸、再把终端窗摆到确定的屏幕内位置。
    /// </remarks>
    private static async Task ProbeClickFocusAsync(Node host, Control terminal)
    {
        host.GetTree().Root.Size = new Vector2I(1600, 900);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

        Node walk = terminal;
        while (walk is not null && walk is not GameWindow) walk = walk.GetParent();
        if (walk is GameWindow win)
        {
            win.PlaceAt(new Rect2(120, 120, 640, 400));
            win.GetParent<Desktop>()?.BringToFront(win);
        }
        terminal.ReleaseFocus();
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

        var rect = terminal.GetGlobalRect();
        await ClickAsync(host, rect.Position + rect.Size / 2);
        Check("[探针] 点击后终端拿到焦点", terminal.HasFocus(), $"focus_mode={terminal.FocusMode}");

        terminal.Call("clear");
        await TypeAsync(host, "ls /et");
        await Task.Delay(1200);
        await TypeKeyAsync(host, Key.Tab);
        await Task.Delay(2000);
        string tab = ScreenText(terminal);
        Check("[探针] 点击聚焦后 Tab 补全", tab.Contains("ls /etc/"), $"focus={terminal.HasFocus()} | {Tail(tab)}");

        await TypeKeyAsync(host, Key.U, ctrl: true);
        terminal.Call("clear");
        await TypeAsync(host, "abc");
        await Task.Delay(500);
        await TypeKeyAsync(host, Key.Left);
        await TypeKeyAsync(host, Key.Left);
        await TypeAsync(host, "X");
        await Task.Delay(800);
        string arrow = ScreenText(terminal);
        Check("[探针] 方向键左移光标 (abc -> aXbc)", arrow.Contains("aXbc"), $"focus={terminal.HasFocus()} | {Tail(arrow)}");
        await TypeKeyAsync(host, Key.U, ctrl: true);
    }

    /// <summary>自检报告，写进日志给三端验证留证据。</summary>
    public static string Report()
    {
        var sb = new StringBuilder();
        foreach (var r in Results)
            sb.AppendLine($"{(r.Ok ? "PASS" : "FAIL")}\t{r.Name}\t{r.Detail.Replace('\n', ' ')}");
        return sb.ToString();
    }

    /// <summary>
    /// 在客户机里跑一条命令，取回它输出的最后一行。
    /// </summary>
    /// <remarks>
    /// 读的是清屏之后的渲染结果，所以拿到的是"玩家眼里的那一行"，
    /// 而不是串口流水 —— 渲染坏掉也会在这里暴露。
    /// </remarks>
    private static async Task<string> CaptureAsync(Node host, Control terminal, string command)
    {
        terminal.Call("clear");
        await TypeAsync(host, command + "\n");
        await Task.Delay(1800);
        string screen = ScreenText(terminal).TrimEnd();
        // 从下往上找第一行"既不是提示符、也不是命令回显"的内容。
        // 注意提示符要在 TrimEnd 之后判：客户机送的是 "/ # "，
        // 按带空格的样子去匹配永远匹配不上，会把提示符当成命令输出返回，
        // 于是所有断言都在拿 "/ #" 做判断、全部假阳性。
        // 注意要 AsEnumerable：数组上的 Reverse 解析到 Array.Reverse（返回 void）
        foreach (string raw in screen.Split('\n').AsEnumerable().Reverse())
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.EndsWith('#') || line.EndsWith('$')) continue;
            if (line.Contains(command)) continue;      // 命令本身的回显
            return line.Trim();
        }
        return "";
    }

    private static string ScreenText(Control terminal) =>
        terminal.Call("copy_all").AsString();

    private static string Tail(string s)
    {
        s = s.TrimEnd();
        int nl = s.LastIndexOf('\n');
        return nl >= 0 ? s[(nl + 1)..] : s;
    }

    /// <summary>
    /// 把一串字符<b>当按键</b>送进去，而不是直接写串口。
    /// </summary>
    /// <remarks>
    /// 走的是 <c>Input.ParseInputEvent</c> -> Godot 输入队列 -> godot-xterm
    /// 的 <c>_gui_input</c> -> <c>data_sent</c> 信号 -> 我们的桥接 -> 串口。
    /// 整条链任何一环在某个平台上断了，这里就会红。
    /// </remarks>
    private static async Task TypeAsync(Node host, string text)
    {
        foreach (char c in text)
        {
            if (c == '\n') await TypeKeyAsync(host, Key.Enter);
            else await TypeKeyAsync(host, KeyOf(c), unicode: c);
            await Task.Delay(40);
        }
    }

    /// <summary>像玩家一样用鼠标点一下某个屏幕坐标（按下+抬起）。</summary>
    private static async Task ClickAsync(Node host, Vector2 globalPos)
    {
        foreach (bool pressed in new[] { true, false })
        {
            var ev = new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = pressed,
                Position = globalPos,
                GlobalPosition = globalPos,
            };
            Input.ParseInputEvent(ev);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static async Task TypeKeyAsync(Node host, Key key, bool ctrl = false, char unicode = '\0')
    {
        var ev = new InputEventKey
        {
            Keycode = key,
            PhysicalKeycode = key,
            Pressed = true,
            CtrlPressed = ctrl,
            Unicode = unicode,
        };
        Input.ParseInputEvent(ev);
        // 输入队列要到下一帧才派发，不等就会几十个事件挤在一起
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>ASCII 到 Godot 键码。只覆盖自检用得到的那些。</summary>
    private static Key KeyOf(char c) => c switch
    {
        >= 'a' and <= 'z' => Key.A + (c - 'a'),
        >= '0' and <= '9' => Key.Key0 + (c - '0'),
        ' ' => Key.Space,
        '/' => Key.Slash,
        '-' => Key.Minus,
        '\'' => Key.Apostrophe,
        '\\' => Key.Backslash,
        '[' => Key.Bracketleft,
        ']' => Key.Bracketright,
        ';' => Key.Semicolon,
        '.' => Key.Period,
        ',' => Key.Comma,
        '=' => Key.Equal,
        _ => Key.None,      // 其余靠 unicode 字段走
    };
}
