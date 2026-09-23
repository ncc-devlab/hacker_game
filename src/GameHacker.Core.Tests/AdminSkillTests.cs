using GameHacker.Core.Admin;
using GameHacker.Core.Levels;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 管理员的专业程度与手法库：谁来查、用什么手法查、查出来之后多狠。
/// </summary>
/// <remarks>
/// <c>netstat</c> / <c>find</c> 的样本照着客户机上 busybox 的原样输出写。
/// 真机上的那条路见 <see cref="AdminTtyIntegrationTests"/>。
/// </remarks>
public class AdminSkillTests
{
    private const string CleanPs = """
        PID   USER     TT     COMMAND
            1 root     ?      {init} /bin/sh /init
            2 root     ?      [kthreadd]
          601 root     ?      syslogd -n
          537 root     4,66   /bin/login -- opsadm
          549 opsadm   4,66   -sh
          550 opsadm   4,66   ps -o pid,user,tty,args
        """;

    private const string Netstat = """
        Active Internet connections (only servers)
        Proto Recv-Q Send-Q Local Address           Foreign Address         State
        tcp        0      0 0.0.0.0:22              0.0.0.0:*               LISTEN
        tcp        0      0 0.0.0.0:4444            0.0.0.0:*               LISTEN
        tcp        0      0 10.0.0.2:22             10.0.0.1:51514          ESTABLISHED
        tcp        0      0 :::8080                 :::*                    LISTEN
        udp        0      0 0.0.0.0:5353            0.0.0.0:*
        """;

    private static readonly AdminAllow Allow = new()
    {
        Processes = ["syslogd*"],
        Sessions = ["ttyS2"],
        Services = ["syslogd"],
        Ports = ["tcp/22"],
        Files = ["/tmp/.X*"],
    };

    private static readonly AdminDefinition Definition = new()
    {
        Machine = "jump01",
        User = "opsadm",
        Allow = Allow,
        Suspicion = new SuspicionRules { SweepAt = 40, ExposedAt = 100, CalmPerPatrol = 4, SweepMultiplier = 2 },
        Routine =
        [
            new AdminAction { Id = "sessions", Check = AdminCheck.Sessions, Chance = 0.6, Weight = 20 },
            new AdminAction { Id = "log", Check = AdminCheck.Log, Chance = 0.5, Weight = 25 },
        ],
        Sweep = ["sessions"],
        PasswordTargets = ["ops"],
    };

    private static readonly AdminAccount Account = new("opsadm", "pw");

    private static AdminAgent NewAgent(AdminSkill skill, AdminDefinition? definition = null, int seed = 1) =>
        new(definition ?? Definition, Account, null!, seed, skill);

    /// <summary>某一档管理员这一关实际会做的检查项，脚本里的那几项也算。</summary>
    private static IEnumerable<AdminCheck> ChecksOf(AdminSkill skill, AdminDefinition definition) =>
        definition.RoutineFor(skill).SelectMany(action => action.Check == AdminCheck.Script
            ? definition.EffectiveScripts.First(s => s.Name == action.Script).Checks
            : [action.Check]);

    [Theory]
    [InlineData(AdminSkill.Junior)]
    [InlineData(AdminSkill.Regular)]
    [InlineData(AdminSkill.Senior)]
    public void 关卡里写的检查项_每一档都查得到(AdminSkill skill)
    {
        // 自带一套手法的档位（实习、资深）会整个盖掉关卡的 routine。
        // 关卡写了某项检查、某一档却没有对应的一条，那条痕迹对这一档就等于不存在 ——
        // 关卡作者不会知道，玩家碰上那一档就白清了
        var definition = Definition with
        {
            Routine = [.. Definition.Routine, new AdminAction { Id = "forwarding", Check = AdminCheck.Forwarding }],
        };
        Assert.Contains(AdminCheck.Forwarding, ChecksOf(skill, definition));
    }

    private static AdminInspectors NewInspectors() => new(Allow, "jump01", "opsadm");

    private static (AdminFinding, int)[] Seen(int weight, params AdminFinding[] findings) =>
        findings.Select(f => (f, weight)).ToArray();

    private static AdminFinding Stray(string what) =>
        new(AdminCheck.Processes, what, what, $"jump01 上有个不该在的进程：{what}");

    // --- 手法库 -------------------------------------------------------------

    [Fact]
    public void 端口_只报白名单外的监听_已建立的连接不算()
    {
        var findings = NewInspectors().Inspect(AdminCheck.Ports, Netstat);

        Assert.Equal(["port:tcp/4444", "port:tcp/8080", "port:udp/5353"], findings.Select(f => f.Subject));
        Assert.Contains("0.0.0.0:4444", findings[0].Explanation);
    }

    [Fact]
    public void 端口_只写端口号时两种协议都放过()
    {
        var inspectors = new AdminInspectors(Allow with { Ports = ["22", "4444", "8080", "5353"] }, "jump01", "opsadm");
        Assert.Empty(inspectors.Inspect(AdminCheck.Ports, Netstat));
    }

    [Fact]
    public void 临时目录里多出来的文件会被看出来()
    {
        var findings = NewInspectors().Inspect(AdminCheck.Files, "/tmp/.X0-lock\n/tmp/nc\n/dev/shm/tunnel.sock");
        Assert.Equal(["file:/tmp/nc", "file:/dev/shm/tunnel.sock"], findings.Select(f => f.Subject));
        Assert.Contains("来路不明", findings[0].Explanation);
    }

    [Fact]
    public void 运维脚本_按段交给各项检查去看()
    {
        var inspectors = NewInspectors();
        var daily = new AdminAction { Id = "daily", Check = AdminCheck.Script, Script = "daily-check.sh" };

        // 脚本在跑时进程表里有它自己，他不会把自己的脚本当成可疑进程
        string ps = CleanPs + "\n  560 opsadm   4,66   sh /home/opsadm/bin/daily-check.sh\n  700 root     4,64   -sh";
        string output = $"== services ==\n{ps}\n== sessions ==\n{ps}";

        var finding = Assert.Single(inspectors.Inspect(daily, output));
        Assert.Equal(AdminCheck.Sessions, finding.Check);          // 算在那项检查头上，和直接查是同一处
        Assert.Equal("tty:ttyS0", finding.Subject);
        Assert.Empty(inspectors.Inspect(AdminCheck.Processes, ps.Replace("  700 root     4,64   -sh", "")));
    }

    [Fact]
    public void 运维脚本_没了或者被清空他会发现()
    {
        var daily = new AdminAction { Id = "daily", Check = AdminCheck.Script, Script = "daily-check.sh" };

        var missing = Assert.Single(NewInspectors().Inspect(daily,
            "sh: can't open '/home/opsadm/bin/daily-check.sh': No such file or directory"));
        Assert.Equal("script:daily-check.sh", missing.Subject);
        Assert.Contains("can't open", missing.Explanation);

        var empty = Assert.Single(NewInspectors().Inspect(daily, ""));
        Assert.Contains("什么都没打印", empty.Explanation);
    }

    [Fact]
    public void 运维脚本_被改成只打印一部分_实习运维看不出来()
    {
        // 真实的攻击手法：让他的脚本少打印一段。他只看脚本给的，不知道那段该有
        var daily = new AdminAction { Id = "daily", Check = AdminCheck.Script, Script = "daily-check.sh" };
        string ps = CleanPs + "\n  700 root     4,64   -sh";
        Assert.Empty(NewInspectors().Inspect(daily, $"== services ==\n{ps}"));
    }

    [Fact]
    public void 脚本内容与写入命令能原样落进客户机()
    {
        var inspectors = NewInspectors();
        var full = AdminProfile.DefaultScripts.Single(s => s.Name == "full-check.sh");
        var lines = inspectors.ScriptLines(full);

        Assert.Equal("#!/bin/sh", lines[0]);
        Assert.Contains("echo \"== processes ==\"", lines);
        Assert.Contains("tail -n 25 /var/log/messages", lines);   // 日志那条拆成了两行
        // 写入时整行套单引号，行里再有单引号就断了
        Assert.All(lines, l => Assert.DoesNotContain("'", l));

        var commands = inspectors.ProvisionCommands();
        Assert.Contains("echo '#!/bin/sh' >> daily-check.sh", commands);
        Assert.Contains("chmod 755 full-check.sh", commands);
    }

    // --- 三档人 -------------------------------------------------------------

    [Fact]
    public void 实习运维只跑脚本()
    {
        var agent = NewAgent(AdminSkill.Junior);
        for (int i = 0; i < 20; i++)
            Assert.All(agent.PickActions(), a => Assert.Equal(AdminCheck.Script, a.Check));
        Assert.Equal(["full"], agent.SweepActions().Select(a => a.Id));
    }

    [Fact]
    public void 运维照关卡写的来()
    {
        var agent = NewAgent(AdminSkill.Regular);
        Assert.Equal(["sessions"], agent.SweepActions().Select(a => a.Id));
        Assert.Equal(40, agent.SweepAt);
    }

    [Fact]
    public void 资深运维看得细_端口和临时文件都在他的手法里_彻底检查全做()
    {
        var agent = NewAgent(AdminSkill.Senior);
        var checks = agent.SweepActions().Select(a => a.Check).ToList();
        Assert.Contains(AdminCheck.Ports, checks);
        Assert.Contains(AdminCheck.Files, checks);
        Assert.Contains(AdminCheck.Processes, checks);
        Assert.Contains(AdminCheck.Services, checks);
        // 关卡的 sweep 是按关卡那套 routine 写的，不拿来裁资深运维的：他自己那套全做一遍
        Assert.Equal(AdminProfile.Preset(AdminSkill.Senior).Routine!.Count, checks.Count);

        var rounds = Enumerable.Range(0, 20).Select(_ => agent.PickActions().Count).ToList();
        Assert.All(rounds, n => Assert.InRange(n, 3, checks.Count));
    }

    [Fact]
    public void 资深运维的彻底检查阈值极低_一处异常就够()
    {
        var senior = NewAgent(AdminSkill.Senior);
        Assert.Equal(10, senior.SweepAt);        // 40 × 0.25

        senior.Settle(false, [AdminCheck.Processes], Seen(20, Stray("nc -l -p 4444")));
        Assert.True(senior.SweepAt <= senior.Suspicion);

        // 同一处异常，运维只是起疑
        var regular = NewAgent(AdminSkill.Regular);
        regular.Settle(false, [AdminCheck.Processes], Seen(20, Stray("nc -l -p 4444")));
        Assert.True(regular.Suspicion < regular.SweepAt);
    }

    [Fact]
    public void 阶段换了规则_档位的倍率照样叠上去()
    {
        var definition = Definition with
        {
            Stages = new Dictionary<string, AdminStage>
            {
                ["cover"] = new() { Suspicion = new SuspicionRules { SweepAt = 20, ExposedAt = 100 } },
            },
        };
        var junior = NewAgent(AdminSkill.Junior, definition);
        var senior = NewAgent(AdminSkill.Senior, definition);
        junior.EnterStage("cover");
        senior.EnterStage("cover");

        Assert.Equal(30, junior.SweepAt);
        Assert.Equal(5, senior.SweepAt);
        Assert.Equal(100, senior.ExposedAt);    // 输赢线是关卡的，档位不碰
    }

    [Fact]
    public void 资深运维可能当场全查_并按彻底检查的倍数记()
    {
        var definition = Definition with
        {
            Tiers = new Dictionary<AdminSkill, AdminProfile>
            {
                [AdminSkill.Senior] = new() { SweepOnTheSpotChance = 1 },
            },
        };
        var agent = NewAgent(AdminSkill.Senior, definition);

        var casual = agent.Settle(false, [AdminCheck.Processes], Seen(20, Stray("nc -l -p 4444")));
        Assert.True(agent.ShouldEscalate(casual));

        var report = agent.Escalate(casual, [AdminCheck.Ports],
            Seen(25, new AdminFinding(AdminCheck.Ports, "port:tcp/4444", "", "端口")));
        Assert.True(report.Sweep);
        Assert.True(report.Escalated);
        Assert.Equal(2, report.Findings.Count);
        Assert.Equal([AdminCheck.Processes, AdminCheck.Ports], report.Did);
        Assert.Equal(20 + 25 * 3, report.Suspicion);         // 资深运维的倍数是 2 + 1
    }

    [Fact]
    public void 实习运维与运维不会当场全查()
    {
        foreach (var skill in new[] { AdminSkill.Junior, AdminSkill.Regular })
        {
            var agent = NewAgent(skill);
            var report = agent.Settle(false, [AdminCheck.Processes], Seen(80, Stray("nc -l -p 4444")));
            Assert.False(agent.ShouldEscalate(report));
        }
    }

    [Fact]
    public void 改口令_要有目标账号()
    {
        var eager = new Dictionary<AdminSkill, AdminProfile>
        {
            [AdminSkill.Senior] = new() { PasswordChance = 1, PasswordChanceOnFinding = 1 },
        };
        var quiet = new PatrolReport("jump01", false, [], [], Seen: 0, Suspicion: 0, Exposed: false);

        Assert.True(NewAgent(AdminSkill.Senior, Definition with { Tiers = eager }).ShouldChangePasswords(quiet));
        Assert.False(NewAgent(AdminSkill.Senior, Definition with { Tiers = eager, PasswordTargets = [] })
            .ShouldChangePasswords(quiet));
        // 实习运维和运维默认不改
        Assert.False(NewAgent(AdminSkill.Junior).ShouldChangePasswords(quiet with { Findings = [Stray("x")] }));
        Assert.False(NewAgent(AdminSkill.Regular).ShouldChangePasswords(quiet with { Findings = [Stray("x")] }));
    }

    [Fact]
    public void 资深运维看出东西之后改口令的可能大得多()
    {
        var quiet = new PatrolReport("jump01", false, [], [], Seen: 0, Suspicion: 0, Exposed: false);
        var found = quiet with { Findings = [Stray("x")] };
        int Count(PatrolReport r)
        {
            var agent = NewAgent(AdminSkill.Senior, seed: 7);
            return Enumerable.Range(0, 2000).Count(_ => agent.ShouldChangePasswords(r));
        }
        int calm = Count(quiet), alarmed = Count(found);
        Assert.InRange(calm, 1, 200);           // 较小的可能：0.03
        Assert.True(alarmed > calm * 5, $"平时 {calm} 次，起疑后 {alarmed} 次");
    }

    // --- 游玩模式 -----------------------------------------------------------

    [Fact]
    public void 各模式的默认概率加起来是一()
    {
        foreach (var mode in Enum.GetValues<PlayMode>())
            Assert.Equal(1.0, AdminSkillOdds.Default(mode).Values.Sum(), 6);
    }

    [Fact]
    public void 模式越贴近实战资深运维越常来_新手模式碰不到()
    {
        int Seniors(PlayMode mode)
        {
            var random = new Random(3);
            return Enumerable.Range(0, 5000).Count(_ => AdminSkillOdds.Roll(mode, Definition, random) == AdminSkill.Senior);
        }
        int novice = Seniors(PlayMode.Novice), advanced = Seniors(PlayMode.Advanced), expert = Seniors(PlayMode.Expert);
        Assert.Equal(0, novice);
        Assert.True(advanced < expert, $"{advanced} / {expert}");
    }

    [Fact]
    public void 关卡能按模式改概率_也能强制档位()
    {
        var definition = Definition with
        {
            SkillOdds = new Dictionary<PlayMode, IReadOnlyDictionary<AdminSkill, double>>
            {
                [PlayMode.Novice] = new Dictionary<AdminSkill, double> { [AdminSkill.Senior] = 1 },
            },
        };
        var random = new Random(3);
        Assert.Equal(AdminSkill.Senior, AdminSkillOdds.Roll(PlayMode.Novice, definition, random));
        // 没写的模式照默认
        Assert.Equal(AdminSkillOdds.Default(PlayMode.Expert), AdminSkillOdds.For(PlayMode.Expert, definition));

        var forced = definition with { Skill = AdminSkill.Junior };
        for (int i = 0; i < 100; i++)
            Assert.Equal(AdminSkill.Junior, AdminSkillOdds.Roll(PlayMode.Expert, forced, random));
    }

    // --- 关卡文件 -----------------------------------------------------------

    private static string LevelWith(string admin) => $$"""
        {
          "id": "x", "title": "x", "track": "mission", "status": "draft",
          "networks": [ { "name": "lan", "vlan": 1, "subnet": "10.0.0.0/24" } ],
          "machines": [ { "name": "jump01", "nics": [ { "network": "lan", "ip": "10.0.0.2" } ] } ],
          "admin": { "machine": "jump01", {{admin}} }
        }
        """;

    private static LevelDefinition Parse(string admin) =>
        Assert.Single(LevelCatalog.Parse([("x.json", LevelWith(admin))]).Levels);

    private static LevelFormatException Rejects(string admin) =>
        Assert.Throws<LevelFormatException>(() => LevelCatalog.Parse([("x.json", LevelWith(admin))]));

    [Fact]
    public void 关卡文件里写档位_脚本_改口令_档位改动()
    {
        var level = Parse("""
            "skill": "senior",
            "passwordTargets": ["ops"],
            "scripts": [ { "name": "check.sh", "checks": ["ports", "files"] } ],
            "allow": { "ports": ["tcp/22"], "files": ["/tmp/.X*"] },
            "tiers": { "senior": { "sweepOnTheSpotChance": 1, "passwordChance": 0.5 } }
            """);
        var admin = level.Admin!;
        Assert.Equal(AdminSkill.Senior, admin.Skill);
        Assert.Equal([AdminSkill.Senior], admin.PossibleSkills);
        Assert.Equal(["ops"], admin.PasswordTargets);
        Assert.Equal([AdminCheck.Ports, AdminCheck.Files], admin.EffectiveScripts.Single().Checks);

        var profile = admin.ProfileFor(AdminSkill.Senior);
        Assert.Equal(1, profile.SweepOnTheSpotChance);
        Assert.Equal(0.5, profile.PasswordChance);
        Assert.Equal(0.25, profile.SweepAtScale);             // 没写的沿用预设
    }

    [Fact]
    public void 强制成资深运维时_关卡可以不写_routine()
    {
        // 资深运维自带一套手法；运维才照关卡的 routine 来
        Parse("\"skill\": \"senior\"");
        var ex = Rejects("\"routine\": []");
        Assert.Contains("regular 档的管理员没有例行要做的事", ex.Message);

        // 各模式都抽不到运维的话，也可以不写
        Parse("""
            "skillOdds": {
              "novice":   { "junior": 1 },
              "advanced": { "junior": 0.5, "senior": 0.5 },
              "expert":   { "senior": 1 }
            }
            """);
    }

    [Fact]
    public void 按模式给的概率要加起来是一()
    {
        var ex = Rejects("""
            "skill": "senior",
            "skillOdds": { "expert": { "junior": 0.5, "senior": 0.2 } }
            """);
        Assert.Contains("expert 模式的 skillOdds 加起来是 0.7", ex.Message);
    }

    [Fact]
    public void 脚本对不上号被拒绝()
    {
        // 自己写了 scripts 却没保留实习运维要跑的那两个
        var ex = Rejects("""
            "scripts": [ { "name": "check.sh", "checks": ["services"] } ],
            "routine": [ { "id": "s", "check": "sessions" } ]
            """);
        Assert.Contains("daily-check.sh", ex.Message);

        ex = Rejects("""
            "routine": [ { "id": "s", "check": "script" }, { "id": "p", "check": "processes", "script": "a.sh" } ]
            """);
        Assert.Contains("没写跑哪个", ex.Message);
        Assert.Contains("不该写 script", ex.Message);

        ex = Rejects("""
            "skill": "senior",
            "scripts": [ { "name": "../x", "checks": ["script"] } ]
            """);
        Assert.Contains("只能用字母", ex.Message);
        Assert.Contains("不能再跑脚本", ex.Message);
    }

    [Fact]
    public void 改口令的目标与档位数值被校验()
    {
        var ex = Rejects("""
            "skill": "senior",
            "passwordTargets": ["root", "opsadm", "Bad Name"],
            "tiers": { "senior": { "passwordChance": 2, "sweepAtScale": 0, "minActions": 4, "maxActions": 2 } }
            """);
        Assert.Contains("不能改 root 的口令", ex.Message);
        Assert.Contains("不能改 opsadm 的口令", ex.Message);
        Assert.Contains("不是合法的用户名", ex.Message);
        Assert.Contains("改口令的概率 2", ex.Message);
        Assert.Contains("彻底检查阈值倍率", ex.Message);
        Assert.Contains("上下限不对", ex.Message);
    }
}
