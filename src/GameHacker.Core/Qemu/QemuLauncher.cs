using System.Diagnostics;
using System.Linq;
using System.Text;
using GameHacker.Core.Channels;

namespace GameHacker.Core.Qemu;

/// <summary>一台客户机的完整定义。</summary>
public sealed record VmSpec
{
    public required string Name { get; init; }
    public required string KernelPath { get; init; }
    public required string InitrdPath { get; init; }

    /// <summary>可写磁盘（qcow2）。为空时客户机纯内存运行，不 switch_root。</summary>
    public string? DiskPath { get; init; }

    /// <summary>形如 10.0.0.1/24，经内核 cmdline 传给客户机 init。</summary>
    public string? IpAddress { get; init; }

    public int MemoryMegabytes { get; init; } = 256;
    public string MacAddress { get; init; } = "52:54:00:00:00:01";

    /// <summary>虚拟交换机的监听端口。客户机作为客户端连进去。</summary>
    public required int SwitchPort { get; init; }

    /// <summary>
    /// 只读基础镜像 + 每存档一份 overlay 的用法下，这里传 overlay 路径。
    /// snapshot 为 true 时改由 QEMU 把写入丢进临时文件，镜像保持原样（测试用）。
    /// </summary>
    public bool Ephemeral { get; init; }

    /// <summary>
    /// 这台机器对玩家宣称的身份（主板 / BIOS / CPU / 内核版本）。
    /// </summary>
    /// <remarks>
    /// 默认 <see cref="HardwarePersona.None"/> —— 不伪装，保持 M0/M1 老测试的口径。
    /// 游戏里应当显式给每台机器挑一个预设。
    /// </remarks>
    public HardwarePersona Persona { get; init; } = HardwarePersona.None;
}

/// <summary>
/// 按 <see cref="VmSpec"/> 拉起一个 QEMU 进程，并把三条通道接好。
/// </summary>
/// <remarks>
/// <para>
/// <b>所有通道一律 127.0.0.1 TCP</b>，不用 AF_UNIX —— Windows 版 QEMU 不保证支持。
/// 端口全部由系统分配（bind 0），避免多台虚拟机或多个测试并发时撞端口。
/// </para>
/// <para>
/// <b>QEMU 一侧一律是客户端</b>（<c>server=off,reconnect-ms=1000</c>）：
/// 宿主先把三个监听口开好再拉起进程，开机第一个字节都不会丢，
/// 而且 VM 重启后 QEMU 会自动接回来。
/// </para>
/// <para>
/// 不要用 <c>-nographic</c>：它会把串口抢去 stdio。用 <c>-display none</c>
/// 加两个显式的 <c>-serial chardev:</c>，这样 ttyS0 是玩家终端、ttyS1 是控制通道。
/// </para>
/// </remarks>
public sealed class QemuLauncher : IAsyncDisposable, IDisposable
{
    private readonly string _qemuPath;
    private readonly string _trackingDirectory;
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private string? _registration;

    public QemuLauncher(string qemuPath = "qemu-system-x86_64", string? trackingDirectory = null)
    {
        _qemuPath = qemuPath;
        _trackingDirectory = trackingDirectory ?? VmProcessRegistry.DefaultDirectory;
    }

    /// <summary>
    /// QEMU 写到 stderr 的内容。
    /// </summary>
    /// <remarks>
    /// 必须留着：QEMU 起不来时只在 stderr 说一句话（比如
    /// <c>failed to find romfile "efi-virtio.rom"</c>），
    /// 丢掉它的话上层只能看到「等 ready 信标超时」，完全无从下手。
    /// </remarks>
    public string StandardError
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    /// <summary>QEMU 进程是否已经退出（起不来时会立刻退）。</summary>
    public bool HasExited => _process?.HasExited ?? false;

    /// <summary>实际执行的命令行，排查时直接复制到终端里跑。</summary>
    public string CommandLine { get; private set; } = "";

    public SerialChannel? Console { get; private set; }
    public SerialChannel? Control { get; private set; }
    public int QmpPort { get; private set; }

    public IReadOnlyList<string> BuildArguments(VmSpec spec, int consolePort, int controlPort, int qmpPort)
    {
        string chardev(string id, int port) =>
            $"socket,id={id},host=127.0.0.1,port={port},server=off,reconnect-ms=1000";

        // 有盘就让 initramfs 挂载并 switch_root 进去（完整 Alpine 主角机），
        // 无盘就地当纯内存的极小目标机跑。
        // 镜像是 mke2fs 直接造的整盘文件系统、没有分区表，所以根设备是 /dev/vda。
        // AHCI 而不是 virtio-blk：virtio 盘挂出来叫 /dev/vda，一眼就是虚拟机。
        string rootArg = spec.DiskPath is null ? "" : " m0.root=/dev/sda";
        string ipArg = spec.IpAddress is null ? "" : $" m0.ip={spec.IpAddress}";
        string personaArg = spec.Persona.KernelCmdlineFragment();

        var args = new List<string>
        {
            "-machine", "q35,accel=tcg",       // TCG 是性能基准：不要求 VT-x，不进 BIOS
            "-m", spec.MemoryMegabytes.ToString(),
            "-smp", "1",
            "-display", "none", "-vga", "none", "-monitor", "none",
            "-kernel", spec.KernelPath,
            "-initrd", spec.InitrdPath,
            "-append", $"console=ttyS0 quiet loglevel=3 tsc=unstable "
                       + $"m0.host={spec.Name}{ipArg}{rootArg}{personaArg}",
            "-chardev", chardev("con", consolePort), "-serial", "chardev:con",
            "-chardev", chardev("ctl", controlPort), "-serial", "chardev:ctl",
            "-netdev", $"stream,id=n0,addr.type=inet,addr.host=127.0.0.1," +
                       $"addr.port={spec.SwitchPort},server=off,reconnect-ms=1000",
            // Intel 82574L 而不是 virtio-net：virtio 的 PCI ID 是 0x1af4（Red Hat），
            // 客户机里 lspci / /sys/class/net/eth0/device/vendor 直接就穿帮了。
            // romfile= 关掉 PXE 引导 ROM：我们永远不网络引导，
            // 留着就得多发一个 efi-e1000e.rom，还白占客户机内存
            "-device", $"e1000e,netdev=n0,romfile=,mac={spec.MacAddress}",
            "-qmp", $"tcp:127.0.0.1:{qmpPort},server=on,wait=off",
        };

        // 伪装：主板 / BIOS / CPU 型号。全是 QEMU 原生参数，不需要魔改。
        args.AddRange(spec.Persona.QemuArguments());

        if (spec.DiskPath is not null)
        {
            // q35 的 if=ide 不会接到内建的 ich9-ahci 上（QEMU 不报错，客户机里就是没盘），
            // 必须显式把 ide-hd 挂到 ide.0。
            string snapshot = spec.Ephemeral ? ",snapshot=on" : "";
            args.Add("-drive");
            args.Add($"file={spec.DiskPath},if=none,id=d0,format=qcow2{snapshot}");
            args.Add("-device");
            args.Add($"ide-hd,drive=d0,bus=ide.0{spec.Persona.DiskDeviceSuffix()}");
        }

        return args;
    }

    public void Start(VmSpec spec)
    {
        if (_process is not null)
            throw new InvalidOperationException("这个 launcher 已经启动过了");

        // 先把监听口开好，再拉起 QEMU —— 顺序反了会丢开机输出
        Console = new SerialChannel();
        Control = new SerialChannel();
        QmpPort = FreeTcpPort();

        var startInfo = new ProcessStartInfo(_qemuPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var args = BuildArguments(spec, Console.Port, Control.Port, QmpPort);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        CommandLine = _qemuPath + " " + string.Join(' ', args.Select(Quote));

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException($"无法启动 {_qemuPath}");

        // 登记进程，这样即便宿主崩溃，下次启动也能回收掉它
        _registration = VmProcessRegistry.Register(_process, _trackingDirectory);

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr) _stderr.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
        // stdout 也要读走，否则管道填满后 QEMU 会阻塞
        _process.OutputDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
    }

    /// <summary>
    /// 组装一条能解释「虚拟机没起来」的诊断信息。
    /// </summary>
    public string DescribeFailure()
    {
        string err = StandardError.Trim();
        string state = HasExited
            ? $"QEMU 已退出 (code {_process!.ExitCode})"
            : "QEMU 仍在运行";

        // 通道是否连上是关键判据：
        //   都没连上 -> QEMU 根本没起来或参数不对
        //   连上了但没 ready -> QEMU 正常，是客户机没启动完
        // 收到的字节数是关键判据：
        //   console 有字节 -> 客户机在启动，问题在 ttyS1 或判定逻辑
        //   console 也没字节 -> 客户机根本没跑起来
        string channels =
            $"console={(Console?.IsConnected == true ? "已连" : "未连")}/{Console?.BytesReceived ?? 0}B "
            + $"control={(Control?.IsConnected == true ? "已连" : "未连")}/{Control?.BytesReceived ?? 0}B";

        return $"{state}，{channels}"
               + (err.Length > 0 ? $"，stderr: {err}" : "，stderr 为空")
               + $"\n       命令行: {CommandLine}";
    }

    /// <summary>给命令行参数加引号，方便直接复制去 shell 里复现。</summary>
    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./=:,".Contains(c))
            ? arg
            : "'" + arg.Replace("'", "'\\''") + "'";

    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        KillProcess();
        if (_process is not null) await _process.WaitForExitAsync();
        Cleanup();
        if (Console is not null) await Console.DisposeAsync();
        if (Control is not null) await Control.DisposeAsync();
    }

    /// <summary>
    /// 同步清理。
    /// </summary>
    /// <remarks>
    /// Godot 的 <c>_ExitTree</c> 不会 await <c>async void</c>，
    /// 用 <see cref="DisposeAsync"/> 的话宿主进程会在杀完子进程之前就退出，
    /// 留下一堆吃满 CPU 的孤儿 QEMU。引擎关闭路径上必须用这个同步版本。
    /// </remarks>
    public void Dispose()
    {
        KillProcess();
        _process?.WaitForExit(5000);
        Cleanup();
        Console?.DisposeAsync().AsTask().Wait(2000);
        Control?.DisposeAsync().AsTask().Wait(2000);
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // 进程已经没了
        }
    }

    private void Cleanup()
    {
        VmProcessRegistry.Unregister(_registration);
        _registration = null;
        _process?.Dispose();
        _process = null;
    }
}
