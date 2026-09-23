namespace GameHacker.Core.Levels;

/// <summary>
/// 游玩模式：同一关在「辅助多少」这根轴上的三个刻度（MVP2 文档第二节）。
/// </summary>
/// <remarks>
/// <para>它不是单独的「难度」。新手模式给初学者：流程弱化、提示多、有额外工具；
/// 专家模式贴近实际渗透：撤掉提示，全靠自己。关卡里对手有多强（管理员是哪一档）
/// 跟着模式和关卡本身走，见 <see cref="Admin.AdminSkillOdds"/>。</para>
/// <para>现在模式只影响来查岗的管理员；提示、步骤、工具按模式派生是之后的事。</para>
/// </remarks>
public enum PlayMode
{
    /// <summary>新手模式：给初学者。</summary>
    Novice,
    /// <summary>高级模式：要素最全、带提示，第一版做的就是这一档。</summary>
    Advanced,
    /// <summary>专家模式：撤掉提示，贴近实际渗透。</summary>
    Expert,
}
