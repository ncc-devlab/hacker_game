using System.Text.RegularExpressions;

namespace GameHacker.Core.Admin;

/// <summary>
/// 哪些进程是「这台机器本来就该有的」。
/// </summary>
/// <remarks>
/// <para>白名单而不是黑名单：黑名单要求我们预先想到玩家会用什么工具，
/// 而玩家一定会用我们没想到的东西。白名单只需要说清楚这台机器平时长什么样，
/// 这是关卡作者真的知道的事。</para>
/// <para>匹配的是整条命令，不是进程名 —— <c>nc -l -p 4444</c> 和
/// <c>nc example.com 80</c> 在关卡里可以是两回事。</para>
/// </remarks>
public sealed class ProcessWhitelist
{
    /// <summary>
    /// M0 客户机开机自带的那批。关卡的白名单是在这个基础上追加。
    /// </summary>
    /// <remarks>
    /// 管理员自己的会话（login / 他的 shell / 他敲的 ps）也在这里 ——
    /// 他不会把自己举报了。
    /// </remarks>
    public static IReadOnlyList<string> Baseline { get; } =
    [
        "{init} /bin/sh /init",
        "/bin/sh /init",
        "/bin/sh --",
        "/bin/sh",
        "-sh",
        "/bin/login*",
        "getty*",
        "/sbin/getty*",
        "ps*",
        "udhcpc*",
        // 判定器自己探查时用的那几样（m0_probe）。不放行的话，管理员会把
        // 判定器的动作当成玩家的痕迹举报 —— 玩家会为自己没做过的事被抓
        "find*",
        "xargs*",
        "sha256sum*",
        "cat*",
    ];

    private readonly List<(string Pattern, Regex Matcher)> _patterns;

    public ProcessWhitelist(IEnumerable<string> extra)
    {
        _patterns = Baseline.Concat(extra)
            .Select(p => (p, new Regex("^" + Regex.Escape(p).Replace("\\*", ".*") + "$")))
            .ToList();
    }

    /// <summary>这条进程是不是本来就该在。</summary>
    public bool Allows(ProcessLine process)
    {
        // 内核线程不是玩家能造出来的东西，一律放过：
        // 否则每换一个内核版本，白名单就要跟着改一遍
        if (process.IsKernelThread) return true;

        return _patterns.Any(p => p.Matcher.IsMatch(process.Command)
                                  || p.Matcher.IsMatch(process.Executable));
    }
}
