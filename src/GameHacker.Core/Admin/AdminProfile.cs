using GameHacker.Core.Levels;

namespace GameHacker.Core.Admin;

/// <summary>管理员的专业程度。</summary>
public enum AdminSkill
{
    /// <summary>新手：只会跑家目录里那几个现成的运维脚本，脚本没打印的他就看不见。</summary>
    Junior,
    /// <summary>熟手：照关卡写的例行检查来，随手看几样。</summary>
    Regular,
    /// <summary>老手：进程、端口、服务、临时文件挨个细看；一点异常就可能当场全查，还可能直接改口令。</summary>
    Senior,
}

/// <summary>
/// 一档管理员的做派：看什么、多容易起疑、疑了之后多狠。
/// </summary>
/// <remarks>
/// <para><b>档位管「这个人怎么查」，关卡管「这台机器长什么样」。</b> 白名单、
/// 查岗频率这些是关卡的事；同一台机器换个人来查，区别在手法和脾气上，放在这里。</para>
/// <para><b>对怀疑度用倍率而不是直接给值。</b> 关卡的阶段（<see cref="AdminStage"/>）
/// 会整套换掉怀疑度规则，档位要是也直接给值，两边就只能有一个说了算。
/// 用倍率的话，「到了掩盖那一步他更敏感」和「老手本来就敏感」可以叠在一起。</para>
/// <para>所有字段都可以不写：关卡在 <c>tiers</c> 里只写想改的那几处，
/// 其余沿用 <see cref="Preset"/>。</para>
/// </remarks>
public sealed record AdminProfile
{
    /// <summary>平时做的事。不写就用关卡的 <see cref="AdminDefinition.Routine"/>。</summary>
    public IReadOnlyList<AdminAction>? Routine { get; init; }

    /// <summary>
    /// 彻底检查做哪几件（<see cref="Routine"/> 里的 id）。
    /// 不写时：档位自带 routine 就全做一遍，否则沿用关卡的 <see cref="AdminDefinition.Sweep"/>。
    /// </summary>
    public IReadOnlyList<string>? Sweep { get; init; }

    /// <summary>彻底检查阈值的倍率。老手的阈值低到一处异常就够。</summary>
    public double? SweepAtScale { get; init; }

    /// <summary>疑心消退的倍率。新手忘得快，老手记仇。</summary>
    public double? CalmScale { get; init; }

    /// <summary>彻底检查时的怀疑度倍数再加几。</summary>
    public int? SweepMultiplierBonus { get; init; }

    /// <summary>这次随手一看就够到彻底检查的阈值时，当场接着全查、而不是等下次来的概率。</summary>
    public double? SweepOnTheSpotChance { get; init; }

    /// <summary>每次查岗顺手改掉 <see cref="AdminDefinition.PasswordTargets"/> 口令的概率。</summary>
    public double? PasswordChance { get; init; }

    /// <summary>这次查岗看出了东西时改口令的概率。起了疑心的人第一反应就是先把门锁上。</summary>
    public double? PasswordChanceOnFinding { get; init; }

    /// <summary>一次查岗最少 / 最多做几件事，盖过关卡作息里的值。老手一次看得多。</summary>
    public int? MinActions { get; init; }
    public int? MaxActions { get; init; }

    /// <summary>命令之间停顿的倍率。新手敲得慢，老手敲得快。</summary>
    public double? PauseScale { get; init; }

    /// <summary>关卡没写 <c>scripts</c> 时，他家目录里的那两个脚本。</summary>
    public static IReadOnlyList<AdminScript> DefaultScripts { get; } =
    [
        new AdminScript { Name = "daily-check.sh", Checks = [AdminCheck.Services, AdminCheck.Sessions] },
        new AdminScript
        {
            Name = "full-check.sh",
            Checks = [AdminCheck.Services, AdminCheck.Sessions, AdminCheck.Processes, AdminCheck.Log],
        },
    ];

    private static readonly AdminProfile JuniorPreset = new()
    {
        // 他会的就是跑脚本：每天那个必跑，偶尔想起来跑一下全面的
        Routine =
        [
            new AdminAction { Id = "daily", Check = AdminCheck.Script, Script = "daily-check.sh", Chance = 1, Weight = 15 },
            new AdminAction { Id = "full", Check = AdminCheck.Script, Script = "full-check.sh", Chance = 0.1, Weight = 15 },
        ],
        // 起了疑心也还是跑脚本，只不过跑全的那个
        Sweep = ["full"],
        SweepAtScale = 1.5,
        CalmScale = 2,
        SweepMultiplierBonus = 0,
        SweepOnTheSpotChance = 0,
        PasswordChance = 0,
        PasswordChanceOnFinding = 0,
        MinActions = 1,
        MaxActions = 2,
        PauseScale = 1.4,
    };

    private static readonly AdminProfile RegularPreset = new()
    {
        SweepAtScale = 1,
        CalmScale = 1,
        SweepMultiplierBonus = 0,
        SweepOnTheSpotChance = 0,
        PasswordChance = 0,
        PasswordChanceOnFinding = 0,
        PauseScale = 1,
    };

    private static readonly AdminProfile SeniorPreset = new()
    {
        Routine =
        [
            new AdminAction { Id = "processes", Check = AdminCheck.Processes, Chance = 0.9, Weight = 20 },
            new AdminAction { Id = "ports", Check = AdminCheck.Ports, Chance = 0.8, Weight = 25 },
            new AdminAction { Id = "services", Check = AdminCheck.Services, Chance = 0.8, Weight = 25 },
            new AdminAction { Id = "sessions", Check = AdminCheck.Sessions, Chance = 0.7, Weight = 20 },
            new AdminAction { Id = "log", Check = AdminCheck.Log, Chance = 0.5, Weight = 25 },
            new AdminAction { Id = "files", Check = AdminCheck.Files, Chance = 0.4, Weight = 20 },
        ],
        // 阈值 40 的关卡里，他 10 分就翻脸：随便哪一处异常都够
        SweepAtScale = 0.25,
        CalmScale = 0.5,
        SweepMultiplierBonus = 1,
        SweepOnTheSpotChance = 0.6,
        PasswordChance = 0.03,
        PasswordChanceOnFinding = 0.35,
        MinActions = 3,
        MaxActions = 6,
        PauseScale = 0.6,
    };

    /// <summary>某一档的内置做派。每个字段都有值。</summary>
    public static AdminProfile Preset(AdminSkill skill) => skill switch
    {
        AdminSkill.Junior => JuniorPreset,
        AdminSkill.Senior => SeniorPreset,
        _ => RegularPreset,
    };

    /// <summary>在这一份上叠 <paramref name="overlay"/>：它写了的字段盖过来，没写的保留。</summary>
    public AdminProfile With(AdminProfile overlay) => new()
    {
        Routine = overlay.Routine ?? Routine,
        Sweep = overlay.Sweep ?? Sweep,
        SweepAtScale = overlay.SweepAtScale ?? SweepAtScale,
        CalmScale = overlay.CalmScale ?? CalmScale,
        SweepMultiplierBonus = overlay.SweepMultiplierBonus ?? SweepMultiplierBonus,
        SweepOnTheSpotChance = overlay.SweepOnTheSpotChance ?? SweepOnTheSpotChance,
        PasswordChance = overlay.PasswordChance ?? PasswordChance,
        PasswordChanceOnFinding = overlay.PasswordChanceOnFinding ?? PasswordChanceOnFinding,
        MinActions = overlay.MinActions ?? MinActions,
        MaxActions = overlay.MaxActions ?? MaxActions,
        PauseScale = overlay.PauseScale ?? PauseScale,
    };

    /// <summary>把关卡（或当前阶段）的怀疑度规则换算成这个人的。暴露阈值不动 —— 那是关卡的输赢线。</summary>
    public SuspicionRules Apply(SuspicionRules rules) => rules with
    {
        SweepAt = Math.Clamp((int)Math.Round(rules.SweepAt * (SweepAtScale ?? 1)), 1, rules.ExposedAt),
        CalmPerPatrol = Math.Max(0, (int)Math.Round(rules.CalmPerPatrol * (CalmScale ?? 1))),
        SweepMultiplier = Math.Max(1, rules.SweepMultiplier + (SweepMultiplierBonus ?? 0)),
    };

    /// <summary>把关卡（或当前阶段）的作息换算成这个人的。来的频率不动 —— 那是关卡的节奏。</summary>
    public AdminSchedule Apply(AdminSchedule schedule)
    {
        double pace = PauseScale ?? 1;
        int max = MaxActions ?? schedule.MaxActions;
        return schedule with
        {
            MinActions = Math.Min(MinActions ?? schedule.MinActions, max),
            MaxActions = max,
            PauseMin = schedule.PauseMin * pace,
            PauseMax = schedule.PauseMax * pace,
        };
    }
}

/// <summary>难度决定来的是哪一档管理员。</summary>
public static class AdminSkillOdds
{
    /// <summary>各难度下三档出现的概率，依次是新手、熟手、老手。</summary>
    public static IReadOnlyDictionary<AdminSkill, double> For(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => Odds(0.70, 0.25, 0.05),
        Difficulty.Hard => Odds(0.10, 0.40, 0.50),
        _ => Odds(0.35, 0.45, 0.20),
    };

    /// <summary>
    /// 这一局来的是谁。关卡强制了档位就是那一档；否则按难度抽。
    /// </summary>
    public static AdminSkill Roll(Difficulty difficulty, AdminSkill? forced, Random random)
    {
        if (forced is { } skill) return skill;
        double r = random.NextDouble();
        foreach (var (candidate, odds) in For(difficulty))
        {
            if (r < odds) return candidate;
            r -= odds;
        }
        return AdminSkill.Senior;   // 浮点累加差一点点的时候
    }

    private static Dictionary<AdminSkill, double> Odds(double junior, double regular, double senior) => new()
    {
        [AdminSkill.Junior] = junior,
        [AdminSkill.Regular] = regular,
        [AdminSkill.Senior] = senior,
    };
}
