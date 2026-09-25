using System.Text;
using GameHacker.Core.Channels;
using GameHacker.Core.Net;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 玩家机上多开的终端：真虚拟机上，ttyS2 / ttyS3 各有一个 shell，
/// resize 只改它自己那个终端，挂断之后 getty 重开一个干净的会话。
/// </summary>
public class ExtraConsoleIntegrationTests
{
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 把一条串口上收到的东西攒成文本，等某个标记出现。
    /// </summary>
    /// <remarks>
    /// 多开的终端是 xterm 类型，busybox ash 每出一行提示符都会发 <c>ESC[6n</c> 问光标在哪、
    /// 然后吞掉接下来的输入当答复 —— 游戏里 godot-xterm 会替玩家答，这里得自己答，
    /// 不然敲进去的命令头几个字会被吃掉。
    /// </remarks>
    private sealed class Screen
    {
        private readonly ConsoleText _text = new();
        private readonly SerialChannel _channel;

        public Screen(SerialChannel channel)
        {
            _channel = channel;
            channel.DataReceived += data =>
            {
                _text.Append(data.Span);
                if (Encoding.ASCII.GetString(data.Span).Contains("\u001b[6n"))
                    _ = channel.SendAsync("\u001b[1;1R"u8.ToArray());
            };
        }

        public async Task RunAsync(string command, string expect, CancellationToken ct)
        {
            await _channel.SendAsync(Encoding.UTF8.GetBytes(command + "\r"), ct);
            await WaitAsync(expect, ct);
        }

        /// <summary>
        /// 等屏幕上出现某段文字。多开终端的 getty 是开机末尾才在后台起的，
        /// 比 console 信标晚一点 —— 提示符出来之前敲的字，串口还没人打开，直接丢了。
        /// </summary>
        public async Task WaitAsync(string expect, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                if (_text.Contains(expect)) return;
                await Task.Delay(100, ct);
            }
            throw new TimeoutException($"等不到 {expect}，屏幕上是：\n{_text.Tail().Replace("\u001b", "\\e")}");
        }
    }

    /// <remarks>
    /// 两种客户机都要测：玩家自己的机器是从 Alpine 磁盘 switch_root 起来的（游戏里就是这种），
    /// 目标机是纯内存的 initramfs。只测后者的话，前者上串口不通也照样全绿。
    /// </remarks>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 多开的终端各自是个_shell_能改尺寸_挂断后重开干净的(bool alpineDisk)
    {
        Skip.IfNot(TestImages.GuestImagesReady, TestImages.MissingImagesReason);
        Skip.If(alpineDisk && !File.Exists(TestImages.AlpineDisk), "缺少 alpine-main.qcow2");

        await using var vSwitch = new VirtualSwitch(port: 0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _ = vSwitch.RunAsync(cts.Token);

        await using var vm = new QemuLauncher(TestImages.QemuPath);
        vm.Start(new VmSpec
        {
            Name = "ws",
            KernelPath = TestImages.Kernel,
            InitrdPath = TestImages.Initrd,
            Nics = [new VmNic("52:54:00:00:01:00", vSwitch.Port, "10.0.0.1/24")],
            ExtraConsoles = 2,
            DiskPath = alpineDisk ? TestImages.AlpineDisk : null,
            Ephemeral = alpineDisk,
            MemoryMegabytes = alpineDisk ? 512 : 256,
        });

        Assert.Equal(["ttyS2", "ttyS3"], vm.Extras.Select(x => x.Tty));
        var s2 = new Screen(vm.Extras[0].Channel);
        var s3 = new Screen(vm.Extras[1].Channel);
        var run = Task.WhenAll(new[] { vm.Console!, vm.Control! }.Concat(vm.Extras.Select(x => x.Channel))
                                   .Select(c => c.RunAsync(cts.Token)));
        await using var control = new ControlChannel(vm.Control!);
        await control.WaitReadyAsync(BootTimeout, cts.Token);
        await control.WaitEventAsync("console", BootTimeout, cts.Token);

        // 两个串口上各坐着一个 shell，而且就是它自己那个终端
        await s2.WaitAsync("# ", cts.Token);
        await s3.WaitAsync("# ", cts.Token);
        await s2.RunAsync("echo M2-$(tty)", "M2-/dev/ttyS2", cts.Token);
        await s3.RunAsync("echo M3-$(tty)", "M3-/dev/ttyS3", cts.Token);

        // resize 只改指定的那个终端
        await control.ResizeAsync("ttyS3", 33, 97, cts.Token);
        await s3.RunAsync("echo S3-$(stty size)", "S3-33 97", cts.Token);
        await control.ResizeAsync("ttyS2", 21, 61, cts.Token);
        await s2.RunAsync("echo S2-$(stty size)", "S2-21 61", cts.Token);
        await s3.RunAsync("echo T3-$(stty size)", "T3-33 97", cts.Token);

        // 玩家在 ttyS3 上留了个后台进程，然后把那扇窗关了
        await s3.RunAsync("sleep 600 & echo BG-$!", "BG-", cts.Token);
        await control.HangupAsync("ttyS3", cts.Token);
        await s2.RunAsync("echo LEFT-$(ps -o args | grep -c 'sleep 60[0]')", "LEFT-0", cts.Token);

        // getty 重开了一个新 shell，照样能用
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        await s3.RunAsync("echo AGAIN-$(tty)", "AGAIN-/dev/ttyS3", cts.Token);

        // 挂断只认多开的终端：主控制台 ttyS0 挂不断
        await control.HangupAsync("ttyS0", cts.Token);
        var main = new Screen(vm.Console!);
        await main.RunAsync("echo ALIVE-$(tty)", "ALIVE-/dev/ttyS0", cts.Token);

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }
    }
}
