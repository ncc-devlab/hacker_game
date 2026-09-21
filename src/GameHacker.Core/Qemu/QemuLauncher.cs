using System.Diagnostics;
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
public sealed class QemuLauncher : IAsyncDisposable
{
    private readonly string _qemuPath;
    private Process? _process;

    public QemuLauncher(string qemuPath = "qemu-system-x86_64") => _qemuPath = qemuPath;

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
        string rootArg = spec.DiskPath is null ? "" : " m0.root=/dev/vda";
        string ipArg = spec.IpAddress is null ? "" : $" m0.ip={spec.IpAddress}";

        var args = new List<string>
        {
            "-machine", "q35,accel=tcg",       // TCG 是性能基准：不要求 VT-x，不进 BIOS
            "-m", spec.MemoryMegabytes.ToString(),
            "-smp", "1",
            "-display", "none", "-vga", "none", "-monitor", "none",
            "-kernel", spec.KernelPath,
            "-initrd", spec.InitrdPath,
            "-append", $"console=ttyS0 quiet loglevel=3 tsc=unstable m0.host={spec.Name}{ipArg}{rootArg}",
            "-chardev", chardev("con", consolePort), "-serial", "chardev:con",
            "-chardev", chardev("ctl", controlPort), "-serial", "chardev:ctl",
            "-netdev", $"stream,id=n0,addr.type=inet,addr.host=127.0.0.1," +
                       $"addr.port={spec.SwitchPort},server=off,reconnect-ms=1000",
            // romfile= 关掉 PXE 引导 ROM：我们永远不网络引导，
            // 留着就得多发一个 efi-virtio.rom，还白占客户机内存
            "-device", $"virtio-net-pci,netdev=n0,romfile=,mac={spec.MacAddress}",
            "-qmp", $"tcp:127.0.0.1:{qmpPort},server=on,wait=off",
        };

        if (spec.DiskPath is not null)
        {
            string snapshot = spec.Ephemeral ? ",snapshot=on" : "";
            args.Add("-drive");
            args.Add($"file={spec.DiskPath},if=virtio,format=qcow2{snapshot}");
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
        foreach (var arg in BuildArguments(spec, Console.Port, Control.Port, QmpPort))
            startInfo.ArgumentList.Add(arg);

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException($"无法启动 {_qemuPath}");
    }

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
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        if (Console is not null) await Console.DisposeAsync();
        if (Control is not null) await Control.DisposeAsync();
    }
}
