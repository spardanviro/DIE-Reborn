using System;
using ConductorCrafting;
using ConductorShop;
using Serilog;

namespace DIE.Server.Data;

/// <summary>
/// 重复的**收藏品**折成货币 —— 原厂 <c>RewardManager.AddDuplicatedCurrencies</c> 那一套。
///
/// ## 哪三样是收藏品
///
/// 武器图纸 / 装置蓝图 / 改装图。判据见 <c>MenuHandlers.OnAddDesignsToWeapon</c>
/// 那一大段: 这三样在客户端都**没有数量字段**
/// (<c>WeaponSchematicItemInfo</c> / <c>GadgetSchematicItemInfo</c> /
/// <c>DesignItemInfo</c> 建的时候把 <c>Stackable.Amount</c> 整个扔掉),
/// 而零件是 <c>new PartItemInfo(part, stackable.Amount)</c>。
///
/// 它们是"有或没有", 所以再掉一张毫无意义 —— 原厂把它折成货币。
/// 也正因为如此, 一个账号**永远不该出现 amount 大于 1 的收藏品行**。
///
/// ## 折多少是原厂的
///
/// 直接调 <see cref="CraftingManager.GetCurrencyFromDuplicate"/>, 一个数都不抄。
/// 它内部按 <c>AccountLevelHelpers.UseResearchPoints</c> 分两套:
///
///   false (本版本)  给**账号经验**: 图纸 40 + 品阶 × 5; 改装图按品类 40/45/50/55/60;
///                  装置蓝图按稀有度 50/55/60
///   true            给研究点: 图纸 10/15/30/60/120; 改装图 6/12/25/50/10; 装置 25/50/10
///
/// <c>UseResearchPoints</c> 在这一版客户端里是写死的 <c>false</c>
/// (<c>AccountLevelHelpers.cs:9</c>, 全代码树只读不写), 所以实际生效的是上面那套。
///
/// ## 两条发放路径, 各缺一半
///
/// <b>局末奖励</b>: 原厂 <c>GetRewards</c> 自己会调 <c>AddDuplicatedCurrencies</c>
/// (RewardManager.cs:116) —— 只要我们把"背包里已有的三份清单"喂进去 (
/// <c>RewardLedger.SnapshotFor</c> 一直在喂)。所以**报文那一半本来就是对的**,
/// 缺的是落库: 重复的图照样被我们 +1 进了库, 而折算出来的账号经验没人入账。
///
/// <b>开箱</b>: 走我们自己的 <see cref="LockboxTable"/>, 两半都缺 —— 见
/// <c>MenuHandlers.OnUnlockLockbox</c>。
///
/// ## 为什么落库那边只补账号经验
///
/// 见 <see cref="RewardLedger.Grant"/> 里的说明 —— 局末那条路上货币那部分
/// 原厂已经加进报文了, 而报文里的货币 <c>Grant</c> 会落库; 只有账号经验
/// 是另一条路进账的 (<c>AwardAccountXp</c> 只落任务经验)。
/// </summary>
public static class DuplicateCollectibles
{
    /// <summary>这一类是"有一张就够"的收藏品吗。</summary>
    public static bool IsCollection(StackableType type) =>
        type is StackableType.Schematic
             or StackableType.GadgetBlueprint
             or StackableType.Design;

    /// <summary>
    /// 重复的这一件折成多少 —— 原厂的表。
    ///
    /// 图鉴里查不到这个 ID 时原厂那几个 getter 会返回 null 再被解引用
    /// (<c>GetDesign</c> 明写着找不到返回 null), 所以整段包起来: 折不出来
    /// 就当 0, 不能让一件脏数据把整份奖励掀掉。
    /// </summary>
    public static Currencies For(StackableType type, ushort itemId)
    {
        if (!IsCollection(type) || !CraftingManager.HasLoadedData)
        {
            return default;
        }

        try
        {
            return CraftingManager.GetCurrencyFromDuplicate(type, itemId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "算不出重复 {Type} {ItemId} 的折算价 (图鉴里没这个 ID?), 当 0 处理",
                type, itemId);
            return default;
        }
    }
}
