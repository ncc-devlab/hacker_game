using System.Text.RegularExpressions;

namespace GameHacker.Core.Admin;

/// <summary>管理员看出来的一处不对劲。</summary>
/// <param name="Check">哪一项检查看出来的。</param>
/// <param name="Subject">具体是什么。同一个 Subject 只会被记一次。</param>
/// <param name="Evidence">他看到的原样内容。</param>
/// <param name="Explanation">一句话说清楚：什么东西让他起了疑。</param>
public sealed record AdminFinding(AdminCheck Check, string Subject, string Evidence, string Explanation);

/// <summary>
/// 把一条命令的输出看出门道来。
/// </summary>
/// <remarks>
/// <para>每一项检查都对应客户机上一条真命令。命令是真的、输出是真的，
/// 所以玩家做的任何改动都会如实反映 —— 判定不靠额外的上帝视角。</para>
/// <para><b>有状态的检查放在这里。</b> 日志那项要记住上次看到多长，
/// 这样玩家把日志删短了才会被发现。管理员的记性就是这个对象。</para>
/// </remarks>
public sealed class AdminInspectors(AdminAllow allow, string machine, string adminUser)
{
    private const string MessagesPath = "/var/log/messages";

    private readonly ProcessWhitelist _processes = new(allow.Processes);

    /// <summary>上次看到的日志行数。第一次查岗时还没有记忆。</summary>
    private int? _lastLogLines;

    /// <summary>这项检查在客户机上敲哪条命令。</summary>
    public static string CommandFor(AdminCheck check) => check switch
    {
        AdminCheck.Processes => "ps -o pid,user,tty,args",
        // busybox 这边没有 w / who，utmp 也没人写，所以「谁登录着」只能看进程表：
        // 有控制终端的进程就是某个人的会话。结论一样，而且玩家改不了
        AdminCheck.Sessions => "ps -o pid,user,tty,args",
        AdminCheck.Log => $"wc -l < {MessagesPath}; tail -n 25 {MessagesPath}",
        // 和上面同一条命令：服务在不在也是从进程表看出来的，
        // 单独写一套解析只会多一处能出错的地方
        AdminCheck.Services => "ps -o pid,user,tty,args",
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, "没有这项检查"),
    };

    /// <summary>看一眼输出，说出哪里不对。</summary>
    public IReadOnlyList<AdminFinding> Inspect(AdminCheck check, string output) => check switch
    {
        AdminCheck.Processes => Processes(output),
        AdminCheck.Sessions => Sessions(output),
        AdminCheck.Log => Log(output),
        AdminCheck.Services => Services(output),
        _ => [],
    };

    private List<AdminFinding> Processes(string output) =>
        ProcessTable.Parse(output)
            .Where(p => !_processes.Allows(p))
            .Select(p => new AdminFinding(
                AdminCheck.Processes, p.Command,
                $"pid {p.Pid}  用户 {p.User}  终端 {p.TtyName}",
                $"{machine} 上有个不该在的进程：{p.Command}"
                + $"（{(p.Tty == "?" ? "没有终端" : "终端 " + p.TtyName)}，以 {p.User} 运行）"))
            .ToList();

    /// <summary>除了他自己，还有谁坐在这台机器上。</summary>
    /// <remarks>
    /// <b>先认出他自己坐在哪。</b> busybox 的 <c>ps</c> 在终端那列给的是设备号
    /// （<c>4,66</c>）而不是名字，所以白名单里写 <c>ttyS2</c> 是匹配不上的；
    /// 而他自己那个会话里还有个以 root 身份跑的 <c>login</c> 进程。
    /// 不先认出来的话，他每次查岗都会把自己举报一遍。
    /// </remarks>
    private List<AdminFinding> Sessions(string output)
    {
        var rows = ProcessTable.Parse(output);
        var own = rows.Where(p => p.User == adminUser).Select(p => p.Tty).ToHashSet();

        var findings = new List<AdminFinding>();
        var seen = new HashSet<string>();
        foreach (var p in rows)
        {
            if (p.Tty == "?" || p.IsKernelThread) continue;              // 没有终端就不是会话
            if (p.User == adminUser || own.Contains(p.Tty)) continue;    // 他自己那个会话
            if (allow.Sessions.Any(t => p.TtyName.Equals(t, StringComparison.OrdinalIgnoreCase))) continue;
            // 一个会话有好几个进程（login、shell、正在跑的命令），按终端去重
            if (!seen.Add(p.Tty)) continue;

            findings.Add(new AdminFinding(
                AdminCheck.Sessions, $"tty:{p.TtyName}",
                $"{p.User} 在终端 {p.TtyName} 上，正在跑 {p.Command}",
                $"{machine} 上还有别人登录着：终端 {p.TtyName}，用户 {p.User}"));
        }
        return findings;
    }

    /// <summary>
    /// 翻日志。两件事：里面有没有刺眼的内容，以及它有没有被人动过。
    /// </summary>
    /// <remarks>
    /// 删日志本身就是痕迹 —— 上次还有 120 行，这次只剩 30 行，他当然会问为什么。
    /// 这正是「掩盖」和「删除」的区别所在：玩家得让日志看起来自然，而不是让它消失。
    /// </remarks>
    private List<AdminFinding> Log(string output)
    {
        var findings = new List<AdminFinding>();
        var lines = output.Split('\n');
        bool counted = int.TryParse(lines.FirstOrDefault()?.Trim(), out int lineCount);

        if (counted)
        {
            if (_lastLogLines is { } previous && lineCount < previous)
                findings.Add(new AdminFinding(
                    AdminCheck.Log, "log:truncated",
                    $"上次 {previous} 行，现在 {lineCount} 行",
                    $"{machine} 的系统日志短了一截：上次看还有 {previous} 行，现在只剩 {lineCount} 行"));
            _lastLogLines = lineCount;
        }

        foreach (string line in lines.Skip(counted ? 1 : 0))
        {
            string flag = allow.LogRedFlags.FirstOrDefault(
                f => line.Contains(f, StringComparison.OrdinalIgnoreCase)) ?? "";
            if (flag.Length == 0) continue;
            findings.Add(new AdminFinding(
                AdminCheck.Log, $"log:{line.Trim()}", line.Trim(),
                $"{machine} 的日志里有条刺眼的记录：{line.Trim()}"));
        }
        return findings;
    }

    /// <summary>该一直跑着的服务还在不在。</summary>
    private List<AdminFinding> Services(string output)
    {
        var running = ProcessTable.Parse(output).Select(p => p.Command).ToList();
        return allow.Services
            .Where(service => !running.Any(command => Matches(command, service)))
            .Select(service => new AdminFinding(
                AdminCheck.Services, $"service:{service}",
                $"进程表里找不到 {service}",
                $"{machine} 上该一直跑着的 {service} 不见了"))
            .ToList();
    }

    private static bool Matches(string command, string pattern) =>
        new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$").IsMatch(command)
        || command.Split(' ')[0].EndsWith(pattern, StringComparison.Ordinal);
}
