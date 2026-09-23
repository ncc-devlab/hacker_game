using System.Text.RegularExpressions;

namespace GameHacker.Core.Admin;

/// <summary><c>ps</c> 里的一行。</summary>
/// <param name="Tty">终端，<c>?</c> 表示没有控制终端。</param>
public sealed record ProcessLine(int Pid, string User, string Tty, string Command)
{
    /// <summary>
    /// 终端的名字，如 <c>ttyS0</c>。
    /// </summary>
    /// <remarks>
    /// busybox 的 <c>ps</c> 在这一列给的是设备号（<c>4,64</c>），关卡文件里没人想写这个。
    /// 主设备号 4 是串口与虚拟控制台：次设备号 64 起是 <c>ttyS0</c>、<c>ttyS1</c>……
    /// 136 是 pty。认不出来的就原样留着。
    /// </remarks>
    public string TtyName
    {
        get
        {
            string[] parts = Tty.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
                return Tty;
            return major switch
            {
                4 when minor >= 64 => $"ttyS{minor - 64}",
                4 => $"tty{minor}",
                136 => $"pts/{minor}",
                _ => Tty,
            };
        }
    }

    /// <summary>内核线程，<c>ps</c> 里显示成 <c>[kworker/0:1]</c> 这样。玩家伪造不了。</summary>
    public bool IsKernelThread => Command.StartsWith('[') && Command.EndsWith(']');

    /// <summary>命令的第一个词，即可执行文件。</summary>
    public string Executable => Command.Split(' ')[0];
}

/// <summary>
/// 解析客户机上 <c>ps -o pid,user,tty,args</c> 的输出。
/// </summary>
/// <remarks>
/// 按列宽切会错：命令里有空格，而且 busybox 的 TT 列会出现 <c>4,64</c> 这种设备号。
/// 所以按「前三列是无空格字段，剩下全是命令」来切。
/// </remarks>
public static partial class ProcessTable
{
    public static IReadOnlyList<ProcessLine> Parse(string output)
    {
        var result = new List<ProcessLine>();
        foreach (string raw in output.Split('\n'))
        {
            var match = Row().Match(raw.TrimEnd());
            if (!match.Success) continue;
            result.Add(new ProcessLine(
                int.Parse(match.Groups["pid"].Value),
                match.Groups["user"].Value,
                match.Groups["tty"].Value,
                match.Groups["cmd"].Value.Trim()));
        }
        return result;
    }

    [GeneratedRegex(@"^\s*(?<pid>\d+)\s+(?<user>\S+)\s+(?<tty>\S+)\s+(?<cmd>.+)$")]
    private static partial Regex Row();
}
