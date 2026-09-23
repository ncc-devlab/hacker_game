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
public sealed partial class AdminInspectors(AdminAllow allow, string machine, string adminUser,
                                            IReadOnlyList<AdminScript>? scripts = null)
{
    private const string MessagesPath = "/var/log/messages";
    private const string PsCommand = "ps -o pid,user,tty,args";

    private readonly ProcessWhitelist _processes = new(allow.Processes);
    private readonly Dictionary<string, AdminScript> _scripts =
        (scripts ?? AdminProfile.DefaultScripts).ToDictionary(s => s.Name);

    /// <summary>上次看到的日志行数。第一次查岗时还没有记忆。</summary>
    private int? _lastLogLines;

    /// <summary>这项检查在客户机上敲哪条命令。</summary>
    public string CommandFor(AdminCheck check) => check switch
    {
        AdminCheck.Processes => PsCommand,
        // busybox 这边没有 w / who，utmp 也没人写，所以「谁登录着」只能看进程表：
        // 有控制终端的进程就是某个人的会话。结论一样，而且玩家改不了
        AdminCheck.Sessions => PsCommand,
        AdminCheck.Log => $"wc -l < {MessagesPath}; tail -n 25 {MessagesPath}",
        // 和上面同一条命令：服务在不在也是从进程表看出来的，
        // 单独写一套解析只会多一处能出错的地方
        AdminCheck.Services => PsCommand,
        // 不加 -p：他不是 root，看不到别人进程的 pid，加了也只是一列横杠
        AdminCheck.Ports => "netstat -ltnu",
        AdminCheck.Files => $"find {string.Join(' ', allow.FileDirs)} -type f 2>/dev/null",
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, "这项检查要按动作来问，见 CommandFor(AdminAction)"),
    };

    /// <summary>这件事在客户机上敲哪条命令。跑脚本的话就是 <c>sh ~/bin/脚本名</c>。</summary>
    public string CommandFor(AdminAction action) =>
        action.Check == AdminCheck.Script ? $"sh {ScriptPath(action.Script!)}" : CommandFor(action.Check);

    /// <summary>脚本在客户机上的位置。</summary>
    public static string ScriptPath(string name) => $"~/bin/{name}";

    /// <summary>脚本的内容：每项检查前打一行 <c>== 检查名 ==</c>，后面跟那项检查的命令。</summary>
    /// <remarks>
    /// 一条命令拆成一行一句（日志那项原本是 <c>a; b</c>）：脚本是按行往客户机里写的，
    /// 行短一点就不会被 shell 的行编辑折行。
    /// </remarks>
    public IReadOnlyList<string> ScriptLines(AdminScript script)
    {
        var lines = new List<string>
        {
            "#!/bin/sh",
            $"# {script.Name}: {string.Join(", ", script.Checks.Select(Marker))}",
        };
        foreach (var check in script.Checks)
        {
            lines.Add($"echo \"== {Marker(check)} ==\"");
            lines.AddRange(CommandFor(check).Split("; "));
        }
        return lines;
    }

    /// <summary>
    /// 把他的运维脚本写进客户机家目录的那几条命令。每条都短，而且不含单引号以外的花样。
    /// </summary>
    /// <remarks>
    /// 不用 here-document：续行提示符 <c>&gt; </c> 不是 shell 提示符，
    /// <see cref="Channels.TtySession"/> 会一直等下去。
    /// </remarks>
    public IReadOnlyList<string> ProvisionCommands()
    {
        // 行编辑按终端宽度折行，串口默认只有 80 列；折了行回显就对不上，拆不出输出
        var commands = new List<string> { "stty cols 250", "mkdir -p ~/bin", "cd ~/bin" };
        foreach (var script in _scripts.Values)
        {
            commands.Add($": > {script.Name}");
            commands.AddRange(ScriptLines(script).Select(line => $"echo '{line}' >> {script.Name}"));
            commands.Add($"chmod 755 {script.Name}");
        }
        commands.Add("cd");
        // 这些写文件的命令是开局布景，不是他的日常，不该进他的命令历史 ——
        // 历史里该留的是他查岗敲的那些，那是玩家能侦察到的东西。
        // 改 HISTFILE 没用（ash 启动时就定下了往哪写）；ash 读到一行就先记进历史再执行，
        // 所以清空这一句连自己一起清掉，最后只剩下线时那句 exit
        commands.Add(": > ~/.ash_history");
        return commands;
    }

    /// <summary>看一眼这件事的输出，说出哪里不对。</summary>
    public IReadOnlyList<AdminFinding> Inspect(AdminAction action, string output) =>
        action.Check == AdminCheck.Script ? Script(action.Script!, output) : Inspect(action.Check, output);

    /// <summary>看一眼输出，说出哪里不对。</summary>
    public IReadOnlyList<AdminFinding> Inspect(AdminCheck check, string output) => check switch
    {
        AdminCheck.Processes => Processes(output),
        AdminCheck.Sessions => Sessions(output),
        AdminCheck.Log => Log(output),
        AdminCheck.Services => Services(output),
        AdminCheck.Ports => Ports(output),
        AdminCheck.Files => Files(output),
        _ => [],
    };

    /// <summary>关卡文件和脚本里的检查名，如 <c>services</c>。</summary>
    public static string Marker(AdminCheck check) => check.ToString().ToLowerInvariant();

    /// <summary>进程表里有没有白名单外的东西。</summary>
    /// <remarks>
    /// 他自己那个会话里的进程不算 —— 跑脚本时进程表里会有一行
    /// <c>sh ~/bin/daily-check.sh</c>，他不会把自己的脚本举报了。
    /// </remarks>
    private List<AdminFinding> Processes(string output)
    {
        var rows = ProcessTable.Parse(output);
        var own = OwnTtys(rows);
        return rows
            .Where(p => !_processes.Allows(p) && !(p.Tty != "?" && own.Contains(p.Tty)))
            .Select(p => new AdminFinding(
                AdminCheck.Processes, p.Command,
                $"pid {p.Pid}  用户 {p.User}  终端 {p.TtyName}",
                $"{machine} 上有个不该在的进程：{p.Command}"
                + $"（{(p.Tty == "?" ? "没有终端" : "终端 " + p.TtyName)}，以 {p.User} 运行）"))
            .ToList();
    }

    /// <summary>他自己坐在哪几个终端上（按 <c>ps</c> 里的原样终端号）。</summary>
    private HashSet<string> OwnTtys(IEnumerable<ProcessLine> rows) =>
        rows.Where(p => p.User == adminUser && p.Tty != "?").Select(p => p.Tty).ToHashSet();

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
        var own = OwnTtys(rows);

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

    /// <summary>
    /// 有没有白名单外的端口在监听。
    /// </summary>
    /// <remarks>
    /// 玩家开个 <c>nc -l</c> 等回连、起个转发，进程名可以起得人畜无害，
    /// 但端口藏不住 —— 这是资深运维爱看它的原因。只看监听，不看已建立的连接：
    /// 那些来来去去，报出来只是噪音。
    /// </remarks>
    private List<AdminFinding> Ports(string output)
    {
        var findings = new List<AdminFinding>();
        foreach (string raw in output.Split('\n'))
        {
            var match = NetstatRow().Match(raw.Trim());
            if (!match.Success) continue;
            string proto = match.Groups["proto"].Value.TrimEnd('6');     // tcp6 与 tcp 是一回事
            string local = match.Groups["local"].Value;
            string port = local[(local.LastIndexOf(':') + 1)..];
            if (proto == "tcp" && match.Groups["state"].Value != "LISTEN") continue;
            if (allow.Ports.Any(a => a == port || a.Equals($"{proto}/{port}", StringComparison.OrdinalIgnoreCase)))
                continue;
            findings.Add(new AdminFinding(
                AdminCheck.Ports, $"port:{proto}/{port}", raw.Trim(),
                $"{machine} 上有个不该开的端口在监听：{proto} {local}"));
        }
        return findings;
    }

    /// <summary>临时目录里多出来的文件。</summary>
    private List<AdminFinding> Files(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(path => path.StartsWith('/') && !allow.Files.Any(pattern => Glob(pattern).IsMatch(path)))
            .Select(path => new AdminFinding(
                AdminCheck.Files, $"file:{path}", path,
                $"{machine} 上多了个来路不明的文件：{path}"))
            .ToList();

    /// <summary>
    /// 看脚本打印的东西：按 <c>== 检查名 ==</c> 切成段，每段交给那项检查去看。
    /// </summary>
    /// <remarks>
    /// <para>实习运维只看脚本打印的。脚本被改成不打印某一段，他就不知道那一段该有；
    /// 这是真实的攻击手法，留给玩家。</para>
    /// <para>但一段都切不出来 —— 脚本没了、被清空了、跑不起来 —— 他每天跑的东西
    /// 突然不灵了，这个他看得出来。</para>
    /// </remarks>
    private List<AdminFinding> Script(string name, string output)
    {
        var sections = new List<(AdminCheck Check, List<string> Lines)>();
        foreach (string line in output.Split('\n'))
        {
            var header = ScriptSection().Match(line.Trim());
            if (header.Success && Enum.TryParse<AdminCheck>(header.Groups["check"].Value, true, out var check)
                && check != AdminCheck.Script)
                sections.Add((check, []));
            else if (sections.Count > 0)
                sections[^1].Lines.Add(line);
        }

        if (sections.Count == 0)
        {
            string said = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "什么都没打印";
            return
            [
                new AdminFinding(AdminCheck.Script, $"script:{name}", said,
                                 $"{machine} 上他每天跑的 {name} 不灵了：{said}"),
            ];
        }
        return sections.SelectMany(s => Inspect(s.Check, string.Join('\n', s.Lines))).ToList();
    }

    [GeneratedRegex(@"^(?<proto>tcp6?|udp6?)\s+\d+\s+\d+\s+(?<local>\S+)\s+\S+\s*(?<state>[A-Z_]*)$")]
    private static partial Regex NetstatRow();

    [GeneratedRegex(@"^== (?<check>[a-z-]+) ==$")]
    private static partial Regex ScriptSection();

    private static Regex Glob(string pattern) => new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");

    private static bool Matches(string command, string pattern) =>
        Glob(pattern).IsMatch(command)
        || command.Split(' ')[0].EndsWith(pattern, StringComparison.Ordinal);
}
