using System;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Game;

/// <summary>
/// 十字路口难度的解锁进度 —— 大厅难度选择那一排锁的数据源。
///
/// ## 玩家看到的
///
/// 悬停在锁上写着:
///
///     INFECTIOUS
///     Unlock by completing 3 more Normal games with at least 400 Supplies
///     collected each game. (0/3 Completed)
///
/// 玩家 2026-09-21 报"完成了 normal 模式的游戏, 还是显示一局没完成"。
///
/// ## 数全是原厂的
///
/// <c>CraftingManager.CrossroadLevels</c> (静态构造函数里的硬编码表,
/// 和 <see cref="CrossroadDifficulty"/> 用的是同一张):
///
/// | 档 | Steps | 补给要求 |
/// |---|---|---|
/// | Normal     | 3 | 400 |
/// | Infectious | 5 | 500 |
/// | Contagious | 5 | 600 |
/// | Deadly     | 5 | 600 |
/// | Endemic    | 5 | 600 |
///
/// 界面上那两个数 (3 和 400) 就是从这里读的, 和截图一字不差。
///
/// ## 进度是**一个累计数**, 不是每档一个计数器
///
/// 账号上只存一个 <c>CrossroadProgress</c>, 档位和"这档做了几局"都是从它算的
/// (<c>CraftingManager.CrossroadProgressToLevel</c> / <c>...ToStep</c>):
///
///     0..2   Normal     step 0..2
///     3..7   Infectious step 0..4
///     8..12  Contagious
///     13..17 Deadly
///     18..22 Endemic
///     23     满级
///
/// 所以推进就是 +1, 升档是自然发生的 —— 不需要我们自己判"够不够升"。
///
/// ## 缺的是哪一半
///
/// 字段一直都在: 账号库有 <c>crossroad_progress</c> 列, <c>AccountMapper</c>
/// 也把它下发了。但全代码树只有 SELECT **没有一处 UPDATE** —— 局末推进的
/// 那一半在服务端, 随服务端丢了。所以玩家打多少局都是 0/3。
/// </summary>
public static class CrossroadUnlock
{
    /// <summary>
    /// 一共几档。和 <see cref="CrossroadDifficulty.Count"/> 同源 ——
    /// 那张表 (<c>CraftingManager.CrossroadLevels</c>) 本身是 private,
    /// 只能通过 <c>GetCrossroadLevelInformation</c> 一档一档读。
    /// </summary>
    public static int Levels => CrossroadDifficulty.Count;

    /// <summary>全部打满要多少步 (五档加起来 23)。进度到顶就不再涨。</summary>
    public static int TotalSteps =>
        Enumerable.Range(0, Levels).Sum(level => Math.Max(0, StepsFor(level)));

    /// <summary>这个累计进度对应第几档 (0 = Normal)。走原厂的换算。</summary>
    public static int LevelOf(int progress) =>
        CraftingManager.CrossroadProgressToLevel(Math.Max(0, progress));

    /// <summary>这一档已经做了几局 (界面上 "(0/3)" 的左边那个数)。</summary>
    public static int StepOf(int progress) =>
        CraftingManager.CrossroadProgressToStep(Math.Max(0, progress));

    /// <summary>这一档要做几局 (右边那个数)。</summary>
    public static int StepsFor(int level) => CraftingManager.CrossroadLevelSteps(level);

    /// <summary>
    /// 这一档每局要收集多少补给。
    ///
    /// 原厂那张表里每档只挂一条 <c>Supplies</c> 要求; 万一以后多出别的类型,
    /// 这里只认补给那条, 别的忽略 (不认识的要求当作没有, 总比误判成 0 好)。
    /// </summary>
    public static int RequiredSupplies(int level)
    {
        if (level < 0 || level >= Levels)
        {
            return 0;
        }

        CraftingManager.CrossroadLevelRequirement[] requirements =
            CraftingManager.GetCrossroadLevelInformation((ushort)level).Requirements;

        if (requirements == null)
        {
            return 0;
        }

        foreach (CraftingManager.CrossroadLevelRequirement requirement in requirements)
        {
            if (requirement.RequirementType
                == CraftingManager.CrossroadLevelRequirementEnum.Supplies)
            {
                return (int)Math.Ceiling(requirement.Value);
            }
        }

        return 0;
    }

    /// <summary>进度满了吗 —— 满了就不再推。</summary>
    public static bool IsMaxed(int progress) => progress >= TotalSteps;

    /// <summary>
    /// 这一局算不算一次有效的解锁推进。
    /// </summary>
    /// <param name="progress">这个人现在的累计进度。</param>
    /// <param name="difficulty">这局打的难度档 (0 = Normal)。</param>
    /// <param name="supplies">这局这个人收集的补给。</param>
    /// <remarks>
    /// ## 为什么要求难度**正好等于**当前档
    ///
    /// 界面上那句话是按当前档写的 ("3 more <b>Normal</b> games")。玩家能选的
    /// 档位上限就是当前档, 所以"等于"实际上是"选了能选的最高档"。
    ///
    /// 打更低的档不算 —— 否则已经解锁到 Deadly 的人可以回头刷 Normal 来解锁
    /// Endemic, 那条路比正常打要轻松得多, 和"越往上越难"的设计相反。
    ///
    /// 原厂的精确规则和局末推进那一半一起丢了, 这是按界面文案推的。
    /// </remarks>
    public static bool Qualifies(int progress, int difficulty, int supplies)
    {
        if (IsMaxed(progress))
        {
            return false;
        }

        int level = LevelOf(progress);

        return difficulty == level && supplies >= RequiredSupplies(level);
    }

    /// <summary>推一步, 到顶就停。</summary>
    public static int Advance(int progress) =>
        Math.Min(TotalSteps, Math.Max(0, progress) + 1);
}
