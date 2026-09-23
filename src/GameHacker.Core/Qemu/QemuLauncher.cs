using System.Diagnostics;
using System.Net.Sockets;
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

    public int MemoryMegabytes { get; init; } = 256;

    /// <summary>
    /// 网卡，按顺序在客户机里成为 eth0、eth1……。每块接交换机上一个 VLAN 的监听口。
    /// </summary>
    public required IReadOnlyList<VmNic> Nics { get; init; }

    /// <summary>
    /// 管理员账号。给了就在 ttyS2 上开一个真的登录终端（getty + login），
    /// 游戏侧的管理员假人从那里登录这台机器。
    /// </summary>
    public AdminAccount? Admin { get; init; }

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

/// <summary>管理员在客户机上的账号。口令由宿主生成，只有游戏自己知道。</summary>
public sealed record AdminAccount(string User, string Password);

/// <summary>一块网卡。</summary>
/// <param name="Mac">形如 52:54:00:00:01:00。</param>
/// <param name="SwitchPort">要连的交换机监听口，决定这块网卡在哪个 VLAN。</param>
/// <param name="IpAddress">形如 10.0.0.1/24，经内核 cmdline 传给客户机 init；为空则不配地址。</param>
public sealed record VmNic(string Mac, int SwitchPort, string? IpAddress = null);

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
    /// <summary>一台机器最多几块网卡。客户机 init 只认到 eth3。</summary>
    public const int MaxNics = 4;

    /// <summary>等 QEMU 自己退出多久，超时就强杀。实测本机一台约 30ms。</summary>
    public static readonly TimeSpan QuitGrace = TimeSpan.FromMilliseconds(1500);

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

    /// <summary>管理员的登录终端（ttyS2）。<see cref="VmSpec.Admin"/> 为空时不开。</summary>
    public SerialChannel? Admin { get; private set; }
    public int QmpPort { get; private set; }

    public IReadOnlyList<string> BuildArguments(VmSpec spec, int consolePort, int controlPort, int qmpPort,
                                               int adminPort = 0)
    {
        string chardev(string id, int port) =>
            $"socket,id={id},host=127.0.0.1,port={port},server=off,reconnect-ms=1000";

        // 有盘就让 initramfs 挂载并 switch_root 进去（完整 Alpine 主角机），
        // 无盘就地当纯内存的极小目标机跑。
        // 镜像是 mke2fs 直接造的整盘文件系统、没有分区表，所以根设备是 /dev/vda。
        // AHCI 而不是 virtio-blk：virtio 盘挂出来叫 /dev/vda，一眼就是虚拟机。
        string rootArg = spec.DiskPath is null ? "" : " m0.root=/dev/sda";
        if (spec.Nics.Count is < 1 or > MaxNics)
            throw new ArgumentException($"网卡数要在 1..{MaxNics}，实际 {spec.Nics.Count}", nameof(spec));
        // eth0 沿用 m0.ip（m0/probe 的 Python 脚本也在用），eth1 起是 m0.ip1、m0.ip2……
        string ipArg = string.Concat(spec.Nics.Select((n, i) =>
            n.IpAddress is null ? "" : $" m0.ip{(i == 0 ? "" : i.ToString())}={n.IpAddress}"));
        string personaArg = spec.Persona.KernelCmdlineFragment();
        // 账号和口令经 cmdline 传给客户机 init。真的 /proc/cmdline 已经被 m0_disguise
        // 盖掉，玩家在客户机里读到的是伪造的那份
        string adminArg = spec.Admin is null ? "" : $" m0.admin={spec.Admin.User}:{spec.Admin.Password}";

        var args = new List<string>
        {
            "-machine", "q35,accel=tcg",       // TCG 是性能基准：不要求 VT-x，不进 BIOS
            "-m", spec.MemoryMegabytes.ToString(),
            "-smp", "1",
            "-display", "none", "-vga", "none", "-monitor", "none",
            "-kernel", spec.KernelPath,
            "-initrd", spec.InitrdPath,
            "-append", $"console=ttyS0 quiet loglevel=3 tsc=unstable "
                       + $"m0.host={spec.Name}{ipArg}{rootArg}{personaArg}{adminArg}",
            "-chardev", chardev("con", consolePort), "-serial", "chardev:con",
            "-chardev", chardev("ctl", controlPort), "-serial", "chardev:ctl",
            "-qmp", $"tcp:127.0.0.1:{qmpPort},server=on,wait=off",
        };

        // ttyS2：管理员的登录终端。没有管理员的机器就不开这个口，
        // 客户机里连 /dev/ttyS2 都不存在，玩家看不出这台机器「本来可以被谁登录」
        if (spec.Admin is not null)
        {
            args.Add("-chardev");
            args.Add(chardev("adm", adminPort));
            args.Add("-serial");
            args.Add("chardev:adm");
        }

        for (int i = 0; i < spec.Nics.Count; i++)
        {
            var nic = spec.Nics[i];
            args.Add("-netdev");
            args.Add($"stream,id=n{i},addr.type=inet,addr.host=127.0.0.1," +
                     $"addr.port={nic.SwitchPort},server=off,reconnect-ms=1000");
            // Intel 82574L 而不是 virtio-net：virtio 的 PCI ID 是 0x1af4（Red Hat），
            // 客户机里 lspci / /sys/class/net/eth0/device/vendor 直接就穿帮了。
            // romfile= 关掉 PXE 引导 ROM：我们永远不网络引导，
            // 留着就得多发一个 efi-e1000e.rom，还白占客户机内存。
            // 按顺序添加，PCI 槽位依次递增，客户机里的 eth 编号就和这里的顺序一致
            args.Add("-device");
            args.Add($"e1000e,netdev=n{i},romfile=,mac={nic.Mac}");
        }

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
        Admin = spec.Admin is null ? null : new SerialChannel();
        QmpPort = FreeTcpPort();

        var startInfo = new ProcessStartInfo(_qemuPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var args = BuildArguments(spec, Console.Port, Control.Port, QmpPort, Admin?.Port ?? 0);
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

    /// <summary>
    /// 走 QMP 让 QEMU 自己退出，而不是直接杀。
    /// </summary>
    /// <remarks>
    /// <para>强杀等同于拔电源：块设备来不及刷盘，Windows 上还会弹「QEMU 遇到致命错误」。
    /// QMP 的 <c>quit</c> 是 QEMU 自己的正常退出路径，先试它，超时再强杀兜底。</para>
    /// <para>不抛异常：关机路径上任何一步失败都只是退回强杀，不该把调用方带崩。</para>
    /// </remarks>
    public async Task<bool> TryQuitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_process is null || _process.HasExited) return true;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await using var qmp = new QmpClient();
            await qmp.ConnectAsync("127.0.0.1", QmpPort, cts.Token).ConfigureAwait(false);
            await qmp.ExecuteAsync("quit", cancellationToken: cts.Token).ConfigureAwait(false);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException
                                     or IOException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await TryQuitAsync(QuitGrace).ConfigureAwait(false);
        KillProcess();
        if (_process is not null) await _process.WaitForExitAsync().ConfigureAwait(false);
        Cleanup();
        if (Console is not null) await Console.DisposeAsync().ConfigureAwait(false);
        if (Control is not null) await Control.DisposeAsync().ConfigureAwait(false);
        if (Admin is not null) await Admin.DisposeAsync().ConfigureAwait(false);
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
        Admin?.DisposeAsync().AsTask().Wait(2000);
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
