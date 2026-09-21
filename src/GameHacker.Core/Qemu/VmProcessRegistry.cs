using System.Diagnostics;
using System.Globalization;

namespace GameHacker.Core.Qemu;

/// <summary>
/// 记录我们拉起过的 QEMU 进程，并回收上次残留下来的孤儿。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它。</b> 宿主进程异常退出（崩溃、被强杀、引擎在
/// 清理完成前就结束）时，QEMU 子进程会活下来。TCG 是纯软件模拟，
/// 每台虚拟机占满一个核，孤儿堆积会让后续启动越来越慢直至超时 ——
/// 这个症状我们在 M2 里实测到过。对发行版游戏更严重：玩家崩溃一次，
/// 机器上就多一个永远跑满一核的后台进程。</para>
/// <para><b>为什么用 pid 文件而不是扫进程表。</b> 跨平台读取任意进程的命令行
/// 需要各写一套（Linux 读 /proc，Windows 走 WMI，macOS 调 ps）。
/// 记录 pid 加启动时间则完全走 <see cref="Process"/> 的可移植 API，
/// 而且只会动我们自己登记过的进程。</para>
/// <para>pid 会被系统复用，所以回收前要同时核对进程名和启动时间，
/// 两者都对得上才动手。</para>
/// </remarks>
public static class VmProcessRegistry
{
    private const string Extension = ".vmpid";

    /// <summary>Linux 上 /proc/pid/comm 截断到 15 字符，所以只能前缀匹配。</summary>
    private const string ProcessNamePrefix = "qemu-system";

    public static string DefaultDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "gamehacker-vms");

    /// <summary>登记一个刚拉起的 QEMU 进程，返回记录文件路径。</summary>
    public static string Register(Process process, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{Guid.NewGuid():N}{Extension}");
        File.WriteAllText(path, Serialize(process));
        return path;
    }

    public static void Unregister(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 杀掉上次运行残留下来的 QEMU，返回回收数量。应在拉起任何虚拟机之前调用。
    /// </summary>
    public static int ReapOrphans(string directory)
    {
        if (!Directory.Exists(directory)) return 0;

        int reaped = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            try
            {
                if (TryResolve(File.ReadAllText(path), out Process? victim) && victim is not null)
                {
                    using (victim)
                    {
                        victim.Kill(entireProcessTree: true);
                        victim.WaitForExit(5000);
                    }
                    reaped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or InvalidOperationException or FormatException)
            {
                // 记录文件损坏或进程已经没了，删掉记录接着走
            }
            Unregister(path);
        }
        return reaped;
    }

    private static string Serialize(Process process) =>
        string.Join('\n',
            process.Id.ToString(CultureInfo.InvariantCulture),
            process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// 只有进程还活着、名字像 QEMU、且启动时间对得上，才认为是我们的孤儿。
    /// 三个条件缺一不可 —— pid 会被系统复用，认错了就是杀别人的进程。
    /// </summary>
    private static bool TryResolve(string content, out Process? process)
    {
        process = null;
        string[] lines = content.Split('\n');
        if (lines.Length < 2) return false;
        if (!int.TryParse(lines[0], out int pid)) return false;
        if (!long.TryParse(lines[1], out long startTicks)) return false;

        Process candidate;
        try { candidate = Process.GetProcessById(pid); }
        catch (ArgumentException) { return false; }   // 进程已经不在了

        try
        {
            if (!candidate.ProcessName.StartsWith(ProcessNamePrefix, StringComparison.Ordinal))
            {
                candidate.Dispose();
                return false;
            }
            // 启动时间允许两秒误差：不同来源的精度不一致
            var recorded = new DateTime(startTicks, DateTimeKind.Utc);
            if (Math.Abs((candidate.StartTime.ToUniversalTime() - recorded).TotalSeconds) > 2)
            {
                candidate.Dispose();
                return false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            candidate.Dispose();
            return false;
        }

        process = candidate;
        return true;
    }
}
