namespace GameHacker.Core.Levels;

/// <summary>
/// 玩家选的难度。眼下只决定来查岗的管理员有多专业，见 <see cref="Admin.AdminSkillOdds"/>。
/// </summary>
public enum Difficulty
{
    Easy,
    Normal,
    Hard,
}
