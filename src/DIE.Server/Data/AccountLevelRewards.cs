using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using Serilog;

namespace DIE.Server.Data;

/// <summary>
/// 账号升级奖励 —— 跨过一级就发一次。
///
/// ## 表是原厂的, 一个数都不用编
///
/// <c>AccountLevelHelpers.GetLevelUpRewards(level)</c> 完整保存着:
///
///     4/8/10/12/16/18/20/22/26/28/32/36/40/50/60/70 级   各一张指定改装图
///     17 / 35 级                                          各 5 个暗码
///     24 / 38 级                                          各一张 Focus 装置蓝图
///     其余 (level % 18 决定)                              一批消耗品, 或 2 个暗码
///
/// Focus Gadget 那两张 (ID 11 和 22) 在图鉴里标的是 <c>Reward</c> 而不是
/// <c>Shop</c>/<c>Drop</c> —— 也就是说**只有这条路能拿到**, 买不到也开不到。
/// 不接这一条的话那两个装置在私服里永远不存在。
///
/// ## 返回 null 的那些级不是"没奖励"
///
/// <c>GetLevelUpRewards</c> 开头是:
///
///     if (GetRewardsAtLevel(level).Any(a => a != Unspecified)) return null;
///
/// 那些级给的是**技能和槽位** (Ability1/2/3、终极技、强化、消耗品槽、饰品槽),
/// 由 <c>AccountLevelHelpers.Rewards</c> 那张表描述, 客户端自己按等级算,
/// 服务端不需要发东西。所以 null 是"这一级给的不是物品", 直接跳过。
///
/// ## 幂等靠等级本身
///
/// 只处理 <c>(升级前等级, 升级后等级]</c> 这个左开右闭区间 —— 同一级不会发
/// 两次, 因为等级只增不减。不另记"已领取"的账。
///
/// ## 另有一张**我们自己的**表: <see cref="RewardWeaponBlueprints"/>
///
/// 上面那张原厂表**一件武器都不发**, 而图鉴里有 19 把武器标着 <c>Reward</c>:
/// 既没有 <c>Shop</c> (商店买不到), 也没有 <c>Drop</c> (掉落和开箱都筛掉)。
/// 原版那批八成是活动/成就发的, 那套数据我们手上没有 —— 所以这一张是编的,
/// 不是还原。凡是我们编的都集中在那一个字典里, 一眼能认出来。
/// </summary>
public sealed class AccountLevelRewards
{
    private readonly AccountStore _accounts;

    public AccountLevelRewards(AccountStore accounts) =>
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

    /// <summary>
    /// 加经验, 顺便把这次跨过的所有等级的奖励发掉。
    ///
    /// 这是给 <c>MatchHostRegistry.TryCreate</c> 的 <c>awardAccountXp</c> 用的 ——
    /// 签名和 <see cref="AccountStore.AddStoryMapXp"/> 一致, 直接替换即可。
    /// </summary>
    /// <returns>加完之后的总经验。</returns>
    public uint AddExperience(ulong userId, uint amount)
    {
        // 读不到升级前的经验就**整个跳过**发奖, 而不是当成 0 ——
        // 当成 0 的话会把 1 级到当前等级的所有奖励重发一遍。
        uint? before = ExperienceOrNull(userId);
        uint after = _accounts.AddStoryMapXp(userId, amount);

        if (before.HasValue)
        {
            GrantBetween(userId, before.Value, after);
        }

        return after;
    }

    /// <summary>
    /// 把 <paramref name="fromExperience"/> 到 <paramref name="toExperience"/>
    /// 之间跨过的每一级的奖励都发掉。
    ///
    /// 一局跨两级是可能的 (低等级时一级只要几十点经验), 所以是循环不是判断。
    /// </summary>
    /// <returns>发了几件东西。</returns>
    public int GrantBetween(ulong userId, uint fromExperience, uint toExperience)
    {
        int fromLevel = AccountLevelHelpers.ExperienceToLevel(fromExperience);
        int toLevel = AccountLevelHelpers.ExperienceToLevel(toExperience);

        if (toLevel <= fromLevel)
        {
            return 0;
        }

        int granted = 0;

        for (int level = fromLevel + 1; level <= toLevel; level++)
        {
            granted += GrantForLevel(userId, level);
        }

        Log.Information("账号 {UserId} 升级 {From} -> {To}, 发放 {Count} 件升级奖励",
            userId, fromLevel, toLevel, granted);

        return granted;
    }

    /// <summary>发一级的奖励 —— 原厂那张表, 加上我们补的武器蓝图。</summary>
    private int GrantForLevel(ulong userId, int level)
    {
        int granted = GrantStockRewards(userId, level);

        // **加发**而不是二选一。原厂表在技能级返回 null (那些级不发物品),
        // 而我们补的几把正好挑那种级挂 —— 写成 else 的话它们永远发不出来。
        if (RewardWeaponBlueprints.TryGetValue(level, out ushort schematicId))
        {
            granted += GrantWeaponBlueprint(userId, level, schematicId);
        }

        return granted;
    }

    /// <summary>原厂那张表这一级给什么。</summary>
    private int GrantStockRewards(ulong userId, int level)
    {
        // null = 这一级给的是技能/槽位, 由客户端按等级自己算, 不发物品。
        IEnumerable<Reward> rewards = AccountLevelHelpers.GetLevelUpRewards(level);

        if (rewards == null)
        {
            return 0;
        }

        int granted = 0;

        foreach (Reward reward in rewards)
        {
            StackableType? type = StackableTypeOf(reward.Type);

            if (type == null)
            {
                Log.Warning("账号 {UserId} 的 {Level} 级奖励是 {Type}, 还不支持, 跳过",
                    userId, level, reward.Type);
                continue;
            }

            // 直接读 _SchematicID / _Quantity 而不是走 SchematicID / Quantity 属性 ——
            // 那两个属性对不在白名单里的 Type 会抛。这里的 Type 已经过滤过了,
            // 但字段是 [StructLayout(Explicit)] 的联合体, 读字段最直白。

            // 收藏品 (图纸 / 装置蓝图 / 改装图) 已经有了就不再发一份, 折成账号经验 ——
            // 同局末和开箱那两条路, 见 DuplicateCollectibles。24/38 级发的 Focus
            // 装置蓝图正好是玩家可能自己先买了的那种。
            if (DuplicateCollectibles.IsCollection(type.Value)
                && _accounts.CountStackable(userId, reward._SchematicID, type.Value) > 0)
            {
                int xp = DuplicateCollectibles.For(type.Value, reward._SchematicID).AccountXP;

                if (xp > 0)
                {
                    _accounts.AddStoryMapXp(userId, (uint)xp);
                }

                Log.Information("账号 {UserId} {Level} 级奖励: {Type} #{Id} 已经有了, 折 {Xp} 经验",
                    userId, level, type.Value, reward._SchematicID, xp);

                granted++;
                continue;
            }

            int total = _accounts.AddStackable(
                userId, reward._SchematicID, type.Value, reward._Quantity);

            Log.Information("账号 {UserId} {Level} 级奖励: {Type} #{Id} ×{Amount} (共 {Total})",
                userId, level, type.Value, reward._SchematicID, reward._Quantity, total);

            granted++;
        }

        return granted;
    }

    /// <summary>
    /// 发一张武器蓝图, 顺便把"发早了"挡掉。
    ///
    /// 发的是**蓝图**不是成品: 玩家还得自己攒零件、付手续费去造。这样这几把
    /// 武器和整套经济是通的 —— 直接塞成品的话它们会绕过零件这个唯一的产出坑。
    /// </summary>
    private int GrantWeaponBlueprint(ulong userId, int level, ushort schematicId)
    {
        ItemSchematic schematic = CraftingManager.GetWeaponSchematic(schematicId);

        if (schematic == null)
        {
            Log.Warning("{Level} 级要发的武器蓝图 #{Id} 图鉴里查不到, 跳过", level, schematicId);
            return 0;
        }

        // 发一张现在还造不出来的蓝图 = 一张只能看的废纸。品阶门槛读原厂的
        // LevelRequiredForCraftingTier, 不写死 —— 那张表改了这里自动跟。
        int needed = AccountLevelHelpers.LevelRequiredForCraftingTier(schematic.Tier);

        if (level < needed)
        {
            Log.Warning("{Level} 级发 {Name} (T{Tier}) 的蓝图太早了 —— 要 {Needed} 级才造得出来",
                level, schematic.Name, schematic.Tier, needed);
        }

        int total = _accounts.AddStackable(userId, schematicId, StackableType.Schematic, 1);

        Log.Information("账号 {UserId} {Level} 级奖励: 武器蓝图 {Name} (T{Tier}, 共 {Total})",
            userId, level, schematic.Name, schematic.Tier, total);

        return 1;
    }

    /// <summary>
    /// 哪一级送哪一把武器蓝图。**这张表是我们编的**, 不是原厂数据。
    ///
    /// ## 为什么得编
    ///
    /// 图鉴里 19 把武器标着 <c>Reward</c>, 意思是"靠奖励拿"。但原厂的升级奖励表
    /// (<c>AccountLevelHelpers.GetLevelUpRewards</c>) 只发改装图、装置蓝图、暗码和
    /// 消耗品, **一件武器都不发** —— 那批八成是活动或成就发的, 数据没留下来。
    ///
    /// 不编的话这 19 把在私服里根本不存在。
    ///
    /// ## 编的时候守的两条
    ///
    /// 1. **不发造不出来的**。每一档都排在
    ///    <c>LevelRequiredForCraftingTier(品阶)</c> 之后 —— T7 要 10 级,
    ///    T8 要 12, T9 要 15, T12 要 28, T13 要 33。有测试钉住。
    /// 2. **顺着原厂自己的命名走**。9027~9038 是六对 Silver/Gold 变体
    ///    (拳套 / 齿轮锤 / 砖锤 / 狙击霰弹 / 刺刀步枪 / 废料左轮), 银的排前面
    ///    金的排后面, 那个"进阶"的意思是原厂起名字时就带着的。
    ///
    /// ## 不在表里的两把
    ///
    /// 1005 Paddle 和 1009 ScrapShotgun 是初始武器, 建号时直接给**成品**
    /// (<c>CraftingCatalog.StarterWeaponIds</c>), 不必再送一次蓝图。
    /// (Paddle 在图鉴里压根没有配方, 只能白送。)
    ///
    /// 节奏按玩家实测调。
    /// </summary>
    public static readonly IReadOnlyDictionary<int, ushort> RewardWeaponBlueprints =
        new Dictionary<int, ushort>
        {
            // 早期两把 —— 刚够得着的品阶
            [2] = 1006,     // T2  2-by-4Nails
            [3] = 2015,     // T3  Pew

            // 银色六件套 (T7~T9)
            [11] = 9029,    // T7  Silver Cog wheel Hammer
            [13] = 9031,    // T7  SilverBrick Hammer
            [14] = 9033,    // T7  SilverSniperShotgun
            [15] = 9037,    // T7  Silver ScrapRevolver
            [16] = 9035,    // T8  Silver BladeRifle
            [19] = 9027,    // T9  SilverBarbedWire Boxing

            // 金色六件套 —— 同样六把的进阶版
            [21] = 9030,    // T7  Gold Cog wheel Hammer
            [23] = 9032,    // T7  GoldBrick Hammer
            [25] = 9034,    // T7  GoldSniperShotgun
            [27] = 9038,    // T7  Gold ScrapRevolver
            [29] = 9036,    // T8  Gold BladeRifle
            [31] = 9028,    // T9  GoldenBarbedWire Boxing

            // 三把 Premium+Reward —— 后期里程碑
            [34] = 9039,    // T12 Trident Cactus
            [45] = 9049,    // T12 LadyBlue
            [55] = 4020,    // T13 KegDestroyer
        };

    /// <summary>
    /// 升级奖励的 <c>RewardType</c> 对应哪一种可堆叠物。
    ///
    /// <c>AccountLevelHelpers.ItemReward</c> 只会产出这五种 (它自己那句 if 里
    /// 列的就是这几个), 别的一律返回 null 让调用方记一条日志。
    /// </summary>
    private static StackableType? StackableTypeOf(RewardType type) => type switch
    {
        RewardType.BluePrint => StackableType.Schematic,
        RewardType.GadgetBlueprint => StackableType.GadgetBlueprint,
        RewardType.Design => StackableType.Design,
        RewardType.Part => StackableType.Part,
        RewardType.Consumable => StackableType.Consumable,
        RewardType.Keycode => StackableType.Keycode,
        RewardType.Lockbox => StackableType.Lockbox,
        _ => null,
    };

    private uint? ExperienceOrNull(ulong userId)
    {
        try
        {
            return _accounts.Load(userId)?.StoryMapXp;
        }
        catch (Exception ex)
        {
            // Load 对不存在的账号是**抛**不是返回 null。
            // 发不了升级奖励不该连累经验入账本身, 所以吞掉往下走。
            Log.Warning(ex, "读不到账号 {UserId} 的经验, 这一局的升级奖励跳过", userId);
            return null;
        }
    }
}
