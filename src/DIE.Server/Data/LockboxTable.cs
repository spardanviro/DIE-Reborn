using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Data;

/// <summary>
/// 开箱掉什么。
///
/// 开箱是拿蓝图的"碰运气"那条路 —— 定向要某一张就去商店买。
///
/// ## 这一整个类的数都是我们定的
///
/// 原厂的掉落表来自运营下发的 <c>rewardsetup.data</c> (客户端只有读它的代码,
/// 数据我们没有)。所以规则是自己编的, 但**物品池不是** —— 蓝图和改装图都是
/// 从 <c>CraftingManager</c> 的图鉴里挑的, 图鉴里没有的东西不会掉。
///
/// ## 箱子里是蓝图和改装图, **不是零件**
///
/// 上一版做的是"武器箱开零件"。客户端表达不了那个 —— 它算"能开出什么"时
/// 按箱子类型分流 (RewardManager.cs:1067), 只认 Blueprint 和 Design,
/// 别的当场 StunLog.Error 并算出 0 件可开。玩家实测报的
/// "商店里 lockbox 全部显示 no items available" 就是这么来的。
///
/// 详见 <see cref="DropTableBuilder"/>。零件改由商店直售。
///
///   SilverWeaponBox        武器蓝图, 低品阶
///   GoldWeaponBox          武器蓝图, 不封顶, 数量也多
///   SilverModificationBox  改装图 (Design)
///   GoldModificationBox    改装图, 数量多
///
/// 金箱和银箱的差别做成**品阶上限 + 数量**而不是"必出稀有" —— 前者玩家
/// 能理解 ("金箱出好蓝图"), 后者要玩家记概率表。
/// </summary>
public sealed class LockboxTable
{
    /// <summary>银箱开出几样。**我们定的。**</summary>
    public int SilverBoxDrops { get; set; } = 3;

    /// <summary>金箱开出几样。**我们定的。**</summary>
    public int GoldBoxDrops { get; set; } = 5;

    /// <summary>
    /// 银箱只出这个品阶及以下的蓝图。**我们定的。**
    ///
    /// 图鉴里武器品阶实测 0~16。银箱封到 6, 金箱不封。
    ///
    /// **要和 <see cref="DropTableBuilder.SilverBoxMaxTier"/> 保持一致** ——
    /// 那边是下发给客户端的"这箱能开出什么", 这边是真开出来的东西。
    /// 两边不一致的话玩家会开出预告里没有的东西。
    /// </summary>
    public int SilverBoxMaxTier { get; set; } = 6;

    /// <summary>
    /// 开一个箱子, 算出掉什么。
    ///
    /// <paramref name="random"/> 由调用方传进来 —— 服务端只有一个
    /// <see cref="Random"/> 实例, 每次开箱新建一个会因为种子相近而掉出
    /// 一模一样的东西。
    /// </summary>
    /// <param name="owned">
    /// 这个账号已经有的蓝图 / 改装图 ID。**开箱优先挑没有的** ——
    /// 原厂开箱那条路给掉落表传的正是 <c>duplicatesPossible: false</c>
    /// 加上这份清单 (<c>RewardManager.cs:350</c>), 到
    /// <c>GiveAutoGroup</c> 里就是一句"把已有的从候选里滤掉再抽"
    /// (<c>RewardManager.cs:793</c>)。
    ///
    /// 传 null 或空就是纯随机 (老行为)。
    /// </param>
    /// <returns>
    /// 掉落列表。池子是空的 (图鉴没加载), 或者这个池子里能掉的**已经全有了**,
    /// 就返回空表 —— 原厂在后一种情况下是让掉落作废而不是发重复, 见方法体里的说明。
    /// </returns>
    public IReadOnlyList<Drop> Open(LockboxTypes type, Random random,
                                    IReadOnlyCollection<ushort> owned = null)
    {
        bool designs = IsDesignBox(type);

        int count = IsGoldBox(type) ? GoldBoxDrops : SilverBoxDrops;

        // 和 HasAnythingNewFor 走同一个池子 —— 两处判据漂移的话会出现
        // "闸门说还有东西可开, 抽的时候却抽不到"。
        IReadOnlyList<int> pool = PoolFor(type);

        if (pool.Count == 0 || count <= 0)
        {
            return Array.Empty<Drop>();
        }

        StackableType dropType = designs ? StackableType.Design : StackableType.Schematic;

        // 已经有的先滤掉 —— 同一箱里已经抽到的也算 (免得一箱开出两张一样的)。
        HashSet<int> have = owned == null
            ? new HashSet<int>()
            : new HashSet<int>(owned.Select(id => (int)id));

        List<Drop> drops = new List<Drop>(count);

        for (int i = 0; i < count; i++)
        {
            List<int> fresh = pool.Where(id => !have.Contains(id)).ToList();

            // 滤空了 = 这个池子里能掉的全有了。原厂这时 return false, 上层试完
            // 备选奖励还不行就让这一份掉落作废 (RewardManager.cs:803) ——
            // **不发重复**。照做: 后面几份也不必再试了, 池子只会越滤越空。
            //
            // 正常走不到这里: 宿主开箱前会先判"全收齐"并回
            // AlreadyOwnsAllBlueprints / AlreadyOwnsAllDesigns 把箱子退回去
            // (见 MenuHandlers.OnUnlockLockbox)。这里是那道闸的兜底。
            if (fresh.Count == 0)
            {
                break;
            }

            int id = fresh[random.Next(fresh.Count)];
            have.Add(id);

            // 蓝图和改装图都是一次一张 —— 两者都是"有一张就够"的东西。
            drops.Add(new Drop((ushort)id, dropType, 1));
        }

        return drops;
    }

    /// <summary>
    /// 这个箱子还有没有这个账号没有的东西。
    ///
    /// 原厂给这两种情况留了专门的结果码 —— <c>AlreadyOwnsAllBlueprints</c> /
    /// <c>AlreadyOwnsAllDesigns</c>, 客户端翻成 "All Blueprints already owned" /
    /// "All modifications already owned" (<c>GUI_OpenLockBoxPopup.cs:260</c>)。
    /// 也就是说收齐之后原厂是**不让开**, 而不是开出一个空箱子。
    ///
    /// 按这个箱子**自己的**池子判 —— 银武器箱封到 <see cref="SilverBoxMaxTier"/> 阶,
    /// 所以完全可能银箱这一档收齐了而金箱还有东西。
    /// </summary>
    public bool HasAnythingNewFor(LockboxTypes type, IReadOnlyCollection<ushort> owned)
    {
        IReadOnlyList<int> pool = PoolFor(type);

        if (pool.Count == 0)
        {
            return false;
        }

        if (owned == null || owned.Count == 0)
        {
            return true;
        }

        HashSet<int> have = new HashSet<int>(owned.Select(id => (int)id));
        return pool.Any(id => !have.Contains(id));
    }

    /// <summary>这个箱子从哪个池子挑。</summary>
    private IReadOnlyList<int> PoolFor(LockboxTypes type) =>
        IsDesignBox(type)
            ? DesignPool()
            : BlueprintPool(IsGoldBox(type) ? int.MaxValue : SilverBoxMaxTier);

    /// <summary>改装图箱 (银 / 金)。别的都是武器蓝图箱。</summary>
    public static bool IsDesignBox(LockboxTypes type) =>
        type is LockboxTypes.SilverModificationBox or LockboxTypes.GoldModificationBox;

    private static bool IsGoldBox(LockboxTypes type) =>
        type is LockboxTypes.GoldWeaponBox or LockboxTypes.GoldModificationBox;
    /// <summary>开箱掉出来的一样东西。</summary>
    public readonly record struct Drop(ushort ItemId, StackableType Type, int Amount);

    /// <summary>
    /// 蓝图池 —— **必须和 <see cref="DropTableBuilder"/> 用同一个判据**。
    ///
    /// 那边生成的是下发给客户端的"这个箱子里有什么"清单, 这边是服务端真开箱时
    /// 从哪挑。两边池子不一致的表现很难查: 客户端算出"还有 N 件没开到",
    /// 结果开出来的是清单上没有的东西, 或者反过来永远开不到它列出的某几件。
    /// </summary>
    private static IReadOnlyList<int> BlueprintPool(int maxTier) =>
        (CraftingManager.WeaponSchematics ?? Array.Empty<ItemSchematic>())
        .Where(s => s != null && s.Tier <= maxTier && CanDrop(s.Tags))
        .Select(s => s.Id)
        .ToList();

    /// <summary>
    /// 改装图池。
    ///
    /// <c>ItemSchematicUpgrade</c> 上没有品阶字段, 所以银箱那条"只出低阶"的
    /// 规则对它不适用 —— 两种箱子的差别只剩数量。等哪天要分档再说。
    /// </summary>
    private static IReadOnlyList<int> DesignPool() =>
        (CraftingManager.Designs ?? Array.Empty<ItemSchematicUpgrade>())
        .Where(d => d != null && CanDrop(d.Tags))
        .Select(d => d.ID)
        .ToList();

    /// <summary>见 <c>DropTableBuilder.CanDrop</c> —— 同一个判据。</summary>
    private static bool CanDrop(TagEnum[] tags) =>
        tags != null && tags.Contains(TagEnum.Drop);
}
