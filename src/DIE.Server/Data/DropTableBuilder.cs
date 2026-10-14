using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Data;

/// <summary>
/// 宝箱掉落表 —— 下发给客户端的那一份 (<c>GetDropTableResponse.Data</c>)。
///
/// ## 不下发的后果: 商店里宝箱全是 "no items available"
///
/// 玩家实测报的就是这个。<c>DropTableManager.AvailableItems</c> 第一句是:
///
///     if (_DropTableData._0023JLe == null || Count() == 0) return 0;
///
/// 而 <c>InventoryManager.HasItem</c> 判宝箱"已拥有"的条件是
/// <c>AvailableItems &lt;= GetLockBoxCount</c> —— 表是空的时候左边恒为 0,
/// 于是每一种箱子都被判成"没有新东西可开", 商店直接置灰。
///
/// 我们原来 <c>OnGetDropTable</c> 回的是 <c>default</c> (CacheBoxes 为 null)。
///
/// ## 箱子里只能是蓝图或改装图, **不能是零件**
///
/// 这一点推翻了我上一版的设计。客户端算"能开出什么"时按箱子类型分流
/// (RewardManager.cs:1067):
///
///     Schematic -> GetSchematics(group)   只收 RewardType.Blueprint
///     Design    -> GetDesigns(group)      只收 RewardType.Design
///     其它      -> StunLog.Error("...don't implements stackable type")
///
/// 而 <c>LockboxTypesExtensions.GetStackableType</c> 把武器箱映射成
/// <c>Schematic</c>、改装箱映射成 <c>Design</c>。也就是说"武器箱开零件"
/// 客户端根本表达不了 —— 它会当场报错并算出 0 件可开。
///
/// 所以零件改由商店直售 (见 <see cref="ShopCatalog"/>), 箱子回归原厂语义:
/// **开蓝图和改装图**。
///
/// ## 表是我们生成的, 池子不是
///
/// 原厂的掉落表来自运营下发的数据 (我们没有), 但每个箱子能开出什么是从
/// <c>CraftingManager</c> 的图鉴里挑的 —— 图鉴里没有的东西不会出现在表里。
/// </summary>
public sealed class DropTableBuilder
{
    /// <summary>
    /// 银箱只收这个品阶及以下的蓝图。**我们定的。**
    ///
    /// 图鉴里武器品阶实测 0~16。银箱封到 6, 金箱不封 —— 高阶蓝图只能靠金箱
    /// (或者去商店直接买那一张)。
    /// </summary>
    public int SilverBoxMaxTier { get; set; } = 6;

    /// <summary>
    /// 生成掉落表。
    ///
    /// 每种箱子一个 <see cref="CacheBox"/>, <c>ID</c> 就是
    /// <see cref="LockboxTypes"/> 的枚举值 —— 客户端按
    /// <c>x.ID == (int)lockbox</c> 找 (RewardManager.cs:1067), 不能另编号。
    /// </summary>
    public IReadOnlyList<CacheBox> Build()
    {
        List<CacheBox> boxes = new List<CacheBox>();

        foreach (LockboxTypes type in Enum.GetValues<LockboxTypes>())
        {
            if (type == LockboxTypes.None)
            {
                continue;
            }

            GroupReward[] rewards = RewardsFor(type);
            if (rewards.Length == 0)
            {
                continue;
            }

            boxes.Add(new CacheBox
            {
                ID = (int)type,
                Name = type.ToString(),

                // 必须是 CacheBox —— 客户端那句 LastOrDefault 同时按
                // Type == CacheBoxType.CacheBox 过滤, FirstWin 是首胜奖励那一套。
                Type = CacheBoxType.CacheBox,

                RewardGroups = new[]
                {
                    new Group
                    {
                        ID = (int)type,
                        GroupID = (int)type,
                        Name = type.ToString(),
                        Rewards = rewards,
                        DropType = RewardDropType.StandardDrops,
                        DropsInGameTypes = Array.Empty<QueueType>(),
                        DropTimes = 1,
                        OrderID = 0,
                    },
                },
            });
        }

        return boxes;
    }

    /// <summary>
    /// 这种箱子能开出什么。
    ///
    /// ## 只装带 <c>Drop</c> 标签的
    ///
    /// 和货架按 <c>Shop</c> 标签筛是同一件事的两面 (见 <see cref="ShopCatalog"/>):
    /// 图鉴里每件东西都标了销路, 原厂就按标签分。不筛的话 <c>Debug</c> 那 32 把
    /// 内部测试武器、以及只该从账号升级拿的 <c>Reward</c> 档, 都会混进箱子里。
    ///
    /// 筛完的池子: 武器 224 把 (206 Drop + 18 Drop&amp;Shop), 改装图 78 张。
    /// </summary>
    private GroupReward[] RewardsFor(LockboxTypes type)
    {
        bool gold = type is LockboxTypes.GoldWeaponBox or LockboxTypes.GoldModificationBox;
        int maxTier = gold ? int.MaxValue : SilverBoxMaxTier;

        return type.GetStackableType() switch
        {
            StackableType.Schematic => (CraftingManager.WeaponSchematics
                                        ?? Array.Empty<ItemSchematic>())
                .Where(s => s != null && s.Tier <= maxTier && CanDrop(s.Tags))
                .Select(s => new GroupReward { Type = ConductorCrafting.RewardType.Blueprint, Id = s.Id })
                .ToArray(),

            // 改装图上没有品阶字段, 所以金箱银箱这里出的是同一批 ——
            // 两者的差别只剩开出几样 (见 LockboxTable)。
            StackableType.Design => (CraftingManager.Designs
                                     ?? Array.Empty<ItemSchematicUpgrade>())
                .Where(d => d != null && CanDrop(d.Tags))
                .Select(d => new GroupReward { Type = ConductorCrafting.RewardType.Design, Id = d.ID })
                .ToArray(),

            _ => Array.Empty<GroupReward>(),
        };
    }

    /// <summary>
    /// 这件东西打不打得出来。
    ///
    /// 和原厂 <c>CraftingManager.GetDroppableWeapons/Designs/Gadgets</c> 是同一个
    /// 判据 —— 那三个是 <c>internal</c> 我们调不到, 但条件就这一句。
    /// </summary>
    private static bool CanDrop(TagEnum[] tags) =>
        tags != null && tags.Contains(TagEnum.Drop);
}
