using GameHacker.Core.Admin;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 管理员的判定与节奏：什么算可疑、疑心怎么涨怎么消、什么时候翻脸。
/// </summary>
/// <remarks>
/// 这里的 <c>ps</c> 与日志样本都是从真客户机上抄回来的原样输出 —— busybox 的
/// <c>4,64</c> 式终端号、内核线程的方括号、syslog 的行格式，都得照着真的来。
/// 真机上的那条路见 <see cref="AdminTtyIntegrationTests"/>。
/// </remarks>
public class AdminPatrolTests
{
    private const string CleanPs = """
        PID   USER     TT     COMMAND
            1 root     ?      {init} /bin/sh /init
            2 root     ?      [kthreadd]
          527 root     ?      {init} /bin/sh /init
          601 root     ?      syslogd -n
          537 root     4,66   /bin/login -- opsadm
          549 opsadm   4,66   -sh
          550 opsadm   4,66   ps -o pid,user,tty,args
        """;

    private const string CleanLog = """
        3
        Sep 23 08:34:41 jump01 syslog.info syslogd started: BusyBox v1.37.0
        Sep 23 08:34:41 jump01 user.notice init: system boot: jump01
        Sep 23 08:35:02 jump01 user.info dropbear: connection from 10.0.0.9
        """;

    private static readonly AdminAllow Allow = new()
    {
        Processes = ["syslogd*"],
        Sessions = ["ttyS2"],
        Services = ["syslogd"],
        LogRedFlags = ["nc ", "/tmp/"],
    };

    private static readonly AdminDefinition Definition = new()
    {
        Machine = "jump01",
        User = "opsadm",
        Allow = Allow,
        Suspicion = new SuspicionRules { SweepAt = 40, ExposedAt = 100, CalmPerPatrol = 5, SweepMultiplier = 2 },
        Routine =
        [
            new AdminAction { Id = "sessions", Check = AdminCheck.Sessions, Chance = 0.6, Weight = 20 },
            new AdminAction { Id = "processes", Check = AdminCheck.Processes, Chance = 0.5, Weight = 20 },
            new AdminAction { Id = "log", Check = AdminCheck.Log, Chance = 0.5, Weight = 25 },
            new AdminAction { Id = "services", Check = AdminCheck.Services, Chance = 0.3, Weight = 30 },
        ],
    };

    private static AdminAgent NewAgent(AdminDefinition? definition = null, int seed = 1) =>
        new(definition ?? Definition, new AdminAccount("opsadm", "pw"), null!, seed);

    private static AdminInspectors NewInspectors() => new(Allow, "jump01", "opsadm");

    /// <summary>玩家把碍事的 syslogd 杀了之后的进程表。</summary>
    private static string NoSyslogd =>
        string.Join('\n', CleanPs.Split('\n').Where(l => !l.Contains("syslogd")));

    /// <summary>照着一次查岗的样子结账：看了哪几样、各自看到什么。</summary>
    private static PatrolReport Patrol(AdminAgent agent, AdminInspectors inspectors,
                                       bool sweep, params (AdminCheck Check, string Output)[] looked)
    {
        var weights = Definition.Routine.ToDictionary(a => a.Check, a => a.Weight);
        var seen = looked
            .SelectMany(l => inspectors.Inspect(l.Check, l.Output).Select(f => (f, weights[l.Check])))
            .ToList();
        return agent.Settle(sweep, looked.Select(l => l.Check).ToList(), seen);
    }

    // --- 看出什么 -----------------------------------------------------------

    [Fact]
    public void 干净的机器上什么也看不出来()
    {
        var inspectors = NewInspectors();
        var report = Patrol(NewAgent(), inspectors, false,
            (AdminCheck.Processes, CleanPs), (AdminCheck.Sessions, CleanPs),
            (AdminCheck.Services, CleanPs), (AdminCheck.Log, CleanLog));

        Assert.False(report.FoundSomething);
        Assert.Equal(0, report.Suspicion);
    }

    [Fact]
    public void 别人登录着会被看出来_管理员自己的终端不算()
    {
        // 玩家的会话在 ttyS0（设备号 4,64），管理员自己在 ttyS2（4,66）
        string output = CleanPs + "\n  700 root     4,64   -sh";
        var findings = NewInspectors().Inspect(AdminCheck.Sessions, output);

        var finding = Assert.Single(findings);
        Assert.Equal("tty:ttyS0", finding.Subject);
        Assert.Contains("还有别人登录着", finding.Explanation);
    }

    [Fact]
    public void 该跑的服务被杀掉会被看出来()
    {
        var finding = Assert.Single(NewInspectors().Inspect(AdminCheck.Services, NoSyslogd));
        Assert.Equal("service:syslogd", finding.Subject);
        Assert.Contains("不见了", finding.Explanation);
    }

    [Fact]
    public void 日志里刺眼的记录会被看出来()
    {
        string log = CleanLog + "\nSep 23 08:40:11 jump01 user.info root: nc -l -p 4444";
        var finding = Assert.Single(NewInspectors().Inspect(AdminCheck.Log, log));
        Assert.Contains("nc -l -p 4444", finding.Explanation);
    }

    [Fact]
    public void 把日志删短了会被看出来()
    {
        // 「删除」和「掩盖」的区别就在这里：日志凭空短了一截，本身就是痕迹
        var inspectors = NewInspectors();
        Assert.Empty(inspectors.Inspect(AdminCheck.Log, CleanLog));        // 第一次，记住有多长

        string truncated = "1\nSep 23 08:34:41 jump01 syslog.info syslogd started: BusyBox v1.37.0";
        var finding = Assert.Single(inspectors.Inspect(AdminCheck.Log, truncated));
        Assert.Equal("log:truncated", finding.Subject);
        Assert.Contains("短了一截", finding.Explanation);

        // 之后维持这个长度就不再重复报 —— 他只会对「又变短了」起疑
        Assert.Empty(inspectors.Inspect(AdminCheck.Log, truncated));
    }

    // --- 疑心怎么走 ---------------------------------------------------------

    [Fact]
    public void 小事只是起疑_攒够了才彻底查()
    {
        var agent = NewAgent();
        var inspectors = NewInspectors();
        string dirty = CleanPs + "\n  700 root     4,64   -sh";

        // 看到一个陌生会话：起疑，还不至于翻脸
        var first = Patrol(agent, inspectors, false, (AdminCheck.Sessions, dirty));
        Assert.Equal(20, first.Suspicion);
        Assert.False(first.Exposed);

        // 又看到一个不该在的进程：疑心到 40，够他认真查一遍了
        var second = Patrol(agent, inspectors, false,
            (AdminCheck.Processes, dirty + "\n  701 root     ?      tcpdump -i eth0"));
        Assert.Equal(40, second.Suspicion);
        Assert.False(second.Exposed);

        // 彻底检查：全看一遍，而且每处都加倍算
        var sweep = Patrol(agent, inspectors, true,
            (AdminCheck.Sessions, dirty), (AdminCheck.Processes, dirty + "\n  701 root     ?      tcpdump -i eth0"),
            (AdminCheck.Services, NoSyslogd),
            (AdminCheck.Log, CleanLog + "\nSep 23 08:40:11 jump01 user.info root: nc -l -p 4444"));
        Assert.True(sweep.Sweep);
        Assert.True(sweep.Exposed);           // 40 + (30+25)*2 = 150 -> 封顶 100
        Assert.Equal(100, sweep.Suspicion);
    }

    [Fact]
    public void 什么都没查出来的时候疑心会消退()
    {
        // 没有这一条的话怀疑度只增不减，玩家迟早必然暴露，隐蔽就不是手艺只是拖时间
        var agent = NewAgent();
        var inspectors = NewInspectors();
        Patrol(agent, inspectors, false, (AdminCheck.Sessions, CleanPs + "\n  700 root     4,64   -sh"));
        Assert.Equal(20, agent.Suspicion);

        Patrol(agent, inspectors, false, (AdminCheck.Processes, CleanPs));
        Assert.Equal(15, agent.Suspicion);
        Patrol(agent, inspectors, false, (AdminCheck.Processes, CleanPs));
        Assert.Equal(10, agent.Suspicion);
    }

    [Fact]
    public void 同一处痕迹不会被反复算()
    {
        var agent = NewAgent();
        var inspectors = NewInspectors();
        string dirty = CleanPs + "\n  700 root     4,64   -sh";

        Assert.Equal(20, Patrol(agent, inspectors, false, (AdminCheck.Sessions, dirty)).Suspicion);
        var again = Patrol(agent, inspectors, false, (AdminCheck.Sessions, dirty));
        Assert.False(again.FoundSomething);
        Assert.Equal(20, again.Suspicion);    // 没涨，但也没消：他确实又看见了
    }

    // --- 节奏与编排 ---------------------------------------------------------

    [Fact]
    public void 每次只随手看几样_而且次次不同()
    {
        var agent = NewAgent();
        var rounds = Enumerable.Range(0, 12)
            .Select(_ => agent.PickActions().Select(a => a.Id).ToList())
            .ToList();

        Assert.All(rounds, r => Assert.InRange(r.Count, 1, Definition.Schedule.MaxActions));
        // 他是人不是巡检脚本：抽到的组合和顺序都该变
        Assert.True(rounds.Select(r => string.Join(",", r)).Distinct().Count() > 1,
                    "每次查岗做的事都一样，玩家数着次数就能算出来");
    }

    [Fact]
    public void 彻底检查做全套()
    {
        var agent = NewAgent(Definition with { Sweep = ["sessions", "log"] });
        Assert.Equal(["sessions", "log"], agent.SweepActions().Select(a => a.Id));
        // 没指定就是全做一遍
        Assert.Equal(4, NewAgent().SweepActions().Count);
    }

    [Fact]
    public void 进入某个阶段之后换一套脾气()
    {
        var agent = NewAgent(Definition with
        {
            Stages = new Dictionary<string, AdminStage>
            {
                ["cover"] = new()
                {
                    Suspicion = new SuspicionRules { SweepAt = 10, ExposedAt = 60, CalmPerPatrol = 0, SweepMultiplier = 3 },
                    Routine = [new AdminAction { Id = "log", Check = AdminCheck.Log, Chance = 1, Weight = 25 }],
                    Visibility = AdminVisibility.Hidden,
                },
            },
        });
        var inspectors = NewInspectors();

        agent.EnterStage("cover");
        Assert.Equal(AdminVisibility.Hidden, agent.Visibility);
        Assert.Equal(["log"], agent.PickActions().Select(a => a.Id));
        Assert.Equal(60, agent.ExposedAt);

        // 怀疑度不会因为换阶段就清零 —— 他不会忘掉之前看见的事
        Patrol(agent, inspectors, false, (AdminCheck.Sessions, CleanPs + "\n  700 root     4,64   -sh"));
        Assert.Equal(20, agent.Suspicion);
    }

    [Fact]
    public void 认不得的阶段名不会把他弄坏()
    {
        var agent = NewAgent();
        agent.EnterStage("没有这一步");
        Assert.Equal(4, agent.SweepActions().Count);      // 还是原来那套
        Assert.Equal(AdminVisibility.Shown, agent.Visibility);
    }
}
