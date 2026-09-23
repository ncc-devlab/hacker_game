using GameHacker.Core.Admin;
using GameHacker.Core.Channels;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 管理员的判定逻辑：什么算可疑、扣多少分、玩家看不看得懂。
/// </summary>
/// <remarks>
/// 这里的 <c>ps</c> 样本是从真客户机上抄回来的原样输出（见
/// <see cref="AdminTtyIntegrationTests"/> 走的那条路），不是手编的 —— 列宽、
/// busybox 的 <c>4,64</c> 式终端号、内核线程的方括号，都得照着真的来。
/// </remarks>
public class AdminPatrolTests
{
    private const string CleanPs = """
        PID   USER     TT     COMMAND
            1 root     ?      {init} /bin/sh /init
            2 root     ?      [kthreadd]
           11 root     ?      [kworker/0:1]
          527 root     ?      {init} /bin/sh /init
          537 root     4,66   /bin/login -- opsadm
          548 root     4,64   /bin/sh --
          549 opsadm   4,66   -sh
          550 opsadm   4,66   ps -o pid,user,tty,args
        """;

    private static readonly AdminDefinition Definition = new()
    {
        Machine = "jump01",
        User = "opsadm",
        Threshold = 60,
        ProcessWeight = 25,
    };

    private static AdminAgent NewAgent(AdminDefinition? definition = null) =>
        new(definition ?? Definition, new AdminAccount("opsadm", "pw"), null!, seed: 1);

    [Fact]
    public void 干净的机器上什么也发现不了()
    {
        var report = NewAgent().Evaluate(CleanPs);
        Assert.False(report.FoundSomething);
        Assert.Equal(0, report.Score);
        Assert.False(report.Exposed);
    }

    [Fact]
    public void 白名单外的进程被发现_并说得出是哪一条()
    {
        var agent = NewAgent();
        var report = agent.Evaluate(CleanPs + "\n  612 root     4,64   nc -l -p 4444");

        var finding = Assert.Single(report.Findings);
        Assert.Equal("nc -l -p 4444", finding.Subject);
        Assert.Equal(25, report.Score);
        // 护栏：玩家要能看懂为什么被扣分 —— 说法里得有机器、进程和它的样子
        Assert.Contains("jump01", finding.Explanation);
        Assert.Contains("nc -l -p 4444", finding.Explanation);
        Assert.Contains("612", finding.Evidence);
    }

    [Fact]
    public void 同一个进程不会被反复扣分()
    {
        // 管理员每次来都看得见那个还在跑的进程。按次累加的话，
        // 玩家做什么都来不及，分数只取决于他动作多慢
        var agent = NewAgent();
        string dirty = CleanPs + "\n  612 root     4,64   nc -l -p 4444";

        Assert.Equal(25, agent.Evaluate(dirty).Score);
        var second = agent.Evaluate(dirty);
        Assert.False(second.FoundSomething);
        Assert.Equal(25, second.Score);
    }

    [Fact]
    public void 攒够阈值就是暴露()
    {
        // 阈值 50 = 两条发现（每条 25）
        var agent = NewAgent(Definition with { Threshold = 50 });
        Assert.False(agent.Evaluate(CleanPs + "\n  612 root     4,64   nc -l -p 4444").Exposed);

        var report = agent.Evaluate(CleanPs + "\n  700 root     ?      tcpdump -i eth0 -w /tmp/x.pcap");
        Assert.True(report.Exposed);
        Assert.Equal(50, report.Score);
        Assert.Equal(2, agent.Findings.Count);   // 两条都留着，玩家能回看自己栽在哪
    }

    [Fact]
    public void 关卡可以把本机该有的进程加进白名单()
    {
        var agent = NewAgent(Definition with { AllowProcesses = ["syslogd*", "dropbear*"] });
        var report = agent.Evaluate(CleanPs + """

              601 root     ?      syslogd -n
              602 root     ?      dropbear -R -p 22
              612 root     4,64   nc -l -p 4444
            """);

        Assert.Equal("nc -l -p 4444", Assert.Single(report.Findings).Subject);
    }

    [Fact]
    public void 内核线程一律放过()
    {
        // 玩家伪造不了内核线程；把它们算进来的话，换个内核版本白名单就要重写
        var agent = NewAgent();
        Assert.False(agent.Evaluate(CleanPs + "\n  999 root     ?      [kworker/u4:99-events]").FoundSomething);
    }

    [Fact]
    public void 解析_ps_输出_命令里的空格不会把列切错()
    {
        var rows = ProcessTable.Parse(CleanPs);
        Assert.Equal(8, rows.Count);
        var login = rows.Single(r => r.Pid == 537);
        Assert.Equal(("root", "4,66", "/bin/login -- opsadm"), (login.User, login.Tty, login.Command));
        Assert.Equal("/bin/login", login.Executable);
        Assert.True(rows.Single(r => r.Pid == 2).IsKernelThread);
    }
}
