using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Game;

/// <summary>
/// 感染等级 —— 一局的难度档。
///
/// ## 它是什么
///
/// 原版每台战斗服务器有一个感染等级, 玩家在服务器列表里自己挑。它**不是**
/// 账号等级, 两者是平行的两套:
///
///   感染等级  = 这一局的难度 = 武器品阶  (CraftingManager.GetTier 就是恒等式)
///   账号等级  = 你能造、能装备到几阶      (AccountLevelHelpers.LevelRequiredForCraftingTier)
///
/// 它管两件事:
///
///   1. **僵尸属性**。GameManager.GetInfectionLevelModifiers() 拿
///      <c>ServerModifier</c> 当乘数, 而 ServerModifier 原版就是由服务端按
///      <c>CraftingManager.GetInfectionModifier(ServerLevel, MapIndex)</c> 设的。
///      那条曲线在 GameCraftingData.xml 里, 17 档: 1.0 / 1.16 / ... / 5.12 / 7.52。
///   2. **掉落上限** —— 只在 Raid 模式。别的模式 <c>RewardManager.GetMaxTier</c>
///      直接返回 MaxWeaponTier, 决定掉落品阶的是玩家自己的平均武器品阶。
///
/// 客户端那边 <c>GameManagerClient.GetWalkerHealth()</c> 也调同一个函数算血量,
/// 所以服务端不设的话, 客户端显示的血条和实际打起来的手感对不上。
///
/// ## 我们怎么定
///
/// 玩家定的: **跟平均武器品阶走**。私服没有服务器列表, 也没有匹配大厅让人挑档,
/// 所以按装备自动定档 —— 效果等同于原版"你什么水平就进什么档服务器",
/// 只是不用手选。
///
/// ## 之前是 0
///
/// 我们一直没设 ServerLevel, 它保持 int 默认值 0; ServerModifier 由
/// <c>GameManager.Reset()</c> 设成 1。于是所有局都是最低档, 换了装备也不变强
/// 也不变难 —— 全程零报错, 只是"打起来一直一个味"。
/// </summary>
public static class InfectionLevel
{
    /// <summary>
    /// 感染等级的取值范围。
    ///
    /// 上限跟 <c>CraftingData.InfectionLevelIncrease</c> 的长度 (17) 对齐 ——
    /// <c>GetInfectionModifier</c> 越界时会记一条日志再夹回去, 与其让它记日志
    /// 不如这里就夹好。下限是 1: <c>GetInfectionModifier</c> 对 &lt; 1 的值
    /// 一律当 1 处理, 而 0 只是"我们没设过"的哨兵值。
    /// </summary>
    public const int Min = 1;

    public const int Max = 17;

    /// <summary>
    /// 按玩家装的武器定档。
    ///
    /// 取**平均**而不是最高: 主副两把差太多时按高的算, 等于逼玩家两把都顶配,
    /// 而原版的 <c>RewardPlayerInputData.AverageWeaponTier</c> 也是平均。
    ///
    /// 一把都读不出来时退回 <see cref="Min"/> —— 新账号第一局就是这条路。
    /// </summary>
    public static int FromWeapons(IReadOnlyList<ushort> weaponSchematicIds)
    {
        List<int> tiers = (weaponSchematicIds ?? Array.Empty<ushort>())
            .Where(id => id != 0)
            .Select(id => CraftingManager.GetWeaponSchematic(id))
            .Where(s => s != null)
            .Select(s => s.Tier)
            .ToList();

        if (tiers.Count == 0)
        {
            return Min;
        }

        return Clamp((int)Math.Round(tiers.Average()));
    }

    /// <summary>夹到合法范围。</summary>
    public static int Clamp(int level) => Math.Clamp(level, Min, Max);

    /// <summary>
    /// 这一档僵尸属性乘多少。
    ///
    /// 直接问原厂 —— 曲线是数据不是逻辑, 而那份数据我们有
    /// (<c>GameCraftingData.xml</c> 的 <c>InfectionLevelIncrease</c>)。
    ///
    /// 图鉴没载入时返回 1: 宁可一局打得平淡, 也不能让僵尸属性变成 0 ——
    /// 那会让所有人都打不死、也不掉血 (乘数直接乘在基础属性上)。
    /// </summary>
    public static float ZombieModifier(int level, MapIndex mapIndex)
    {
        if (!CraftingManager.HasLoadedData
            || CraftingManager.Data?.InfectionLevelIncrease == null
            || CraftingManager.Data.InfectionLevelIncrease.Length == 0)
        {
            return 1f;
        }

        return CraftingManager.GetInfectionModifier(Clamp(level), mapIndex);
    }
}
