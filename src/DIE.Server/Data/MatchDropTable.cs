using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Data;

/// <summary>
/// 局末掉落表 —— 喂给**原厂** <c>RewardManager.GetRewards</c> 的那份数据。
///
/// ## 为什么是"数据"而不是"逻辑"
///
/// 原厂整套掉落引擎是完整的, 而且已经编译进我们的服务端
/// (<c>Stunlock.ConductorData</c> 整树引用 ConductorCrafting):
///
///     RewardManager.GetRewards(DropTable, players, teamData, random, ...)
///
/// 它自己会算分数、按曲线滚掉落、按品阶配平蓝图、处理重复奖励折算。缺的只有
/// **一份 DropTable** —— 原版那份来自运营下发的 <c>rewardsetup.data</c>,
/// 我们手上没有。
///
/// 所以这里不写掉落逻辑, 只按原厂的 <c>DropTable.LoadDropTable</c> 拼数据。
/// 那也是 <c>ImportDropTable</c> (读 XML 的那条路) 最终调的入口, 两者等价 ——
/// 用代码而不是 XML 是为了让数字旁边能写注释, 并且能被测试钉住。
///
/// ## 分数怎么变成掉落量
///
///     points        = Σ 相关 DropTableBox      (静态 / ×补给 / ×分钟)
///     valueModifier = points / BaseConstantLootValueModifier.Value
///     掉落量        = floor(曲线值[品阶] × valueModifier)
///                     小数部分当**概率**再滚一次 (StandardDrops)
///
/// 我们把 <see cref="BaseValue"/> 定成 100, 再让"一局 10 分钟的 Scout 任务"
/// 正好凑够 100 分 —— 于是曲线值读起来就是"一局标准局给多少", 直观。
/// 5 分钟速通 0.70 倍, 20 分钟磨满 1.60 倍 (局长在
/// <c>RewardManager.GetMaxMatchLength</c> 里按队列封顶, ResistanceEasy 是
/// [300, 1200] 秒)。
///
/// ## 掉什么
///
/// 大头是 GP / CP / 零件 —— 稳定、可预期, 是玩家攒装备的主线。
///
/// 蓝图、改装图、装置**也低概率直掉**, 走原厂的"自动组"
/// (<c>RewardManager.GiveAutoGroup</c>): 按玩家平均品阶挑档、按背包里远程近战
/// 的比例反向配平、按武器品类数量再配平一次, 每局各封顶 1 件。开箱仍然是
/// 拿蓝图的主路, 直掉是意外之喜 —— 所以概率压得很低 (见各 <c>*PerMatch</c>)。
///
/// 箱子和暗码 (Keycode) 也少量掉: 不掉的话开箱这条路就完全绑死在商店上,
/// 而原版是"账号升级送 + 商店买"两条腿 (<c>AccountLevelHelpers</c> 的
/// 17 级 / 35 级各 5 个, 之后每 18 级里两次各 2 个)。
///
/// ## 数是我们定的, 但锚点不是
///
/// GP 曲线抄的是原厂 <c>DropTable.GetFormulaValue(FormulaType.SilverCoins)</c>
/// 的每小时值除以 6 (= 10 分钟), 品阶 1~6 是原数, 7 往上按同一斜率外推 ——
/// 原厂那个公式只写到 6 档。CP 同理 (每小时 450, 与品阶无关)。
///
/// 零件按**配方反推**: 一局给"造一把同阶武器所需的 1/5", 也就是攒五局够造一把。
/// 配方直接从图鉴读, 不写死 —— 图鉴改了这张表自动跟。
///
/// 这些数都要按玩家实测校准, 尤其 <see cref="MatchesPerWeapon"/>。
/// </summary>
public sealed class MatchDropTable
{
    /// <summary>
    /// 分数基准。曲线值 × (本局分数 / 这个数) = 实际掉落量。
    ///
    /// **不能是 0** —— <c>RewardManager</c> 拿它当除数, 而 float 除零不抛异常,
    /// 结果是倍率变成无穷大、所有掉落一次性拉满。
    /// </summary>
    public const float BaseValue = 100f;

    /// <summary>攒几局的零件够造一把同阶武器。</summary>
    public int MatchesPerWeapon { get; set; } = 5;

    /// <summary>一局掉几个银武器箱 (期望值)。</summary>
    public float SilverWeaponBoxPerMatch { get; set; } = 0.10f;

    /// <summary>一局掉几个银改装箱 (期望值)。</summary>
    public float SilverModificationBoxPerMatch { get; set; } = 0.06f;

    /// <summary>
    /// 一局掉几个暗码 (Combination)。
    ///
    /// 银武器箱要 10 个才开得了 (<c>MonetizationStatics
    /// .CodesNeededToUnlock_WeaponCache_Silver</c>), 银改装箱 7 个。
    ///
    /// 按上面两个箱子的掉率算, 每局需要的暗码正好是
    /// <c>0.10 × 10 + 0.06 × 7 = 1.42</c>。取 1.6 而不是 1.42 是要留出**方差**
    /// 的余量 —— 恰好等于期望值时, "这一批箱子开不开得起"是抛硬币,
    /// 60 局实测就卡在 91 对 92 上。
    ///
    /// 目的是让"掉到的箱子都开得起"这条自洽, 不必被迫去商店补暗码。
    /// </summary>
    public float KeycodesPerMatch { get; set; } = 1.6f;

    /// <summary>
    /// 一局直接掉出一张**同阶武器蓝图**的概率。
    ///
    /// 开箱才是拿蓝图的主路, 这里只是意外之喜 —— 按 0.12 算大约八局一张。
    /// </summary>
    public float WeaponBlueprintPerMatch { get; set; } = 0.12f;

    /// <summary>
    /// 一局直接掉出一张**改装图**的概率, 按稀有度分。
    ///
    /// 数量级参照原厂的重复奖励折算 (<c>MonetizationStatics
    /// .DuplicateReward_Research_Other_*</c>: 6 / 12 / 25 / 50), 越稀有越难拿。
    /// 传奇压到 0.004 —— 250 局一张, 那一档本来就该是里程碑。
    /// </summary>
    public IReadOnlyDictionary<ToolRarity, float> DesignPerMatch { get; set; } =
        new Dictionary<ToolRarity, float>
        {
            [ToolRarity.Common] = 0.10f,
            [ToolRarity.Uncommon] = 0.06f,
            [ToolRarity.Rare] = 0.03f,
            [ToolRarity.Epic] = 0.012f,
            [ToolRarity.Legendary] = 0.004f,
        };

    /// <summary>一局直接掉出一张**装置蓝图**的概率。</summary>
    public float GadgetBlueprintPerMatch { get; set; } = 0.02f;

    /// <summary>
    /// 一局直接掉出一个**成品装置**的概率。
    ///
    /// 成品比蓝图值钱得多 —— 它省掉了零件和手续费, 而且
    /// <c>Gadget.GenerateTrinket</c> 会滚随机属性。所以压到蓝图的一半以下。
    /// </summary>
    public float GadgetPerMatch { get; set; } = 0.008f;

    /// <summary>
    /// 这张表在哪些队列生效。
    ///
    /// <c>RewardManager</c> 按 <c>group.DropsInGameTypes.Contains(GameType)</c>
    /// 过滤, 不在列表里的队列一件都不掉。教程不给 —— 那是新手引导, 原版也不给
    /// (它的计分箱是单独的 TutorialMissionBox)。
    /// </summary>
    private static readonly QueueType[] LiveQueues =
    {
        QueueType.ResistanceEasy,
        QueueType.ResistanceNormal,
        QueueType.ResistanceHard,
        QueueType.Scavenger,
        QueueType.RaidMission,
    };

    /// <summary>品阶上限 —— 图鉴实测 0~16, 曲线数组按这个长度开。</summary>
    private const int TierCount = 17;

    // 曲线编号。原厂只要求唯一, 分段是为了看日志时好认。
    private const int CurveGearPoints = 1;
    private const int CurveCharacterPoints = 2;
    private const int CurveLockboxWeapon = 3;
    private const int CurveLockboxModification = 4;
    private const int CurveKeycode = 5;
    private const int CurveFirstWin = 6;
    private const int CurvePartBase = 100;
    private const int CurveAutoGroupBase = 200;

    /// <summary>
    /// 每标准局的 Gear Points, 按品阶。
    ///
    /// 品阶 1~6 是原厂 <c>FormulaType.SilverCoins</c> 的每小时值 ÷ 6
    /// (4200 / 7800 / 12000 / 14000 / 19000 / 21000), 7 往上按 +400/档外推。
    /// 品阶 0 跟品阶 1 —— 0 档只有 Debug 武器, 不该比 1 档还赚。
    /// </summary>
    private static readonly float[] GearPointsCurve =
    {
        700, 700, 1300, 2000, 2350, 3150, 3500, 3900, 4300,
        4700, 5100, 5500, 5900, 6300, 6700, 7100, 7500,
    };

    /// <summary>
    /// 每标准局的角色点。原厂公式与品阶无关 (每小时 450), 所以是平的。
    /// </summary>
    private const float CharacterPointsPerMatch = 75f;

    /// <summary>
    /// 拼出掉落表。
    ///
    /// 需要 <c>CraftingManager</c> 已经载入图鉴 —— 零件曲线是从武器配方反推的。
    /// 没载入的话零件那一组是空的, 但表本身仍然合法 (GP/CP 照给)。
    /// </summary>
    /// <param name="cacheBoxes">
    /// 宝箱的内容表 (<see cref="DropTableBuilder"/> 生成的那份)。
    /// 开箱走的是同一张 DropTable, 所以两者要拼在一起。
    /// </param>
    public DropTable Build(IReadOnlyList<CacheBox> cacheBoxes = null)
    {
        List<DropCurve> curves = new List<DropCurve>();
        List<Group> groups = new List<Group>();

        // GP / CP 过 RewardBoost —— 私服自定倍率, 见 <see cref="RewardBoost"/>。
        // 曲线原值保持原厂锚点不动, 放大只发生在这里。
        curves.Add(Curve(CurveGearPoints, "GearPoints", RewardBoost.Scale(GearPointsCurve)));
        curves.Add(Curve(CurveCharacterPoints, "CharacterPoints",
            Flat(RewardBoost.Scale(CharacterPointsPerMatch))));
        curves.Add(Curve(CurveLockboxWeapon, "SilverWeaponBox",
            Flat(SilverWeaponBoxPerMatch)));
        curves.Add(Curve(CurveLockboxModification, "SilverModificationBox",
            Flat(SilverModificationBoxPerMatch)));
        curves.Add(Curve(CurveKeycode, "Keycode", Flat(KeycodesPerMatch)));

        // 货币走 ScaleOnAmount: 它读 currencyTier 而不是 weaponTier, 而且
        // **不滚概率** —— 钱按倍率线性给, 不该有"这局没掉钱"这种事。
        groups.Add(new Group
        {
            ID = 1,
            GroupID = 1,
            OrderID = 1,
            Name = "Currencies",
            DropType = RewardDropType.ScaleOnAmount,
            DropTimes = 1,
            DropsInGameTypes = LiveQueues,
            Rewards = new[]
            {
                new GroupReward
                {
                    Type = ConductorCrafting.RewardType.GearPoints,
                    Id = 0,
                    CurveID = CurveGearPoints,
                },
                new GroupReward
                {
                    Type = ConductorCrafting.RewardType.CharacterPoints,
                    Id = 0,
                    CurveID = CurveCharacterPoints,
                },
            },
        });

        // ## 一个奖励一个组
        //
        // 组不是"打包一起给", 是**"从里面挑一个"**。SplitDrops / StandardDrops
        // 那条分支最后是:
        //
        //     int index = random.NextInt(0, list.Count - 1);
        //     if (list[index].Amount > 0) rewards.Add(list[index]);
        //
        // 也就是整组只有一条中奖。第一版把 10 种零件塞进同一个组, 结果是
        // "一局最多掉一种零件", 实测五局才攒到 1 个动物胶 (配方要 50)。
        //
        // 所以要多样东西同时掉, 就得拆成多个组。
        foreach (GroupReward part in BuildPartCurves(curves))
        {
            // ## 零件用 SplitDrops 而不是 StandardDrops
            //
            // 同一条分支里还有一句:
            //
            //     if (num > 0 && group.DropType == StandardDrops) num = 1;
            //
            // StandardDrops 会把数量**压成 1**, 那是给"蓝图/箱子"这类
            // 一局最多来一件的东西用的。零件要按配方成批掉, 必须走 SplitDrops。
            groups.Add(SingleRewardGroup(
                id: 100 + part.Id,
                name: "Part" + part.Id,
                reward: part,
                dropType: RewardDropType.SplitDrops));
        }

        // 箱子: 一局最多一个, StandardDrops 的"压成 1"正合适。
        // 金箱不掉 —— 那是商店那条路的溢价品。
        groups.Add(SingleRewardGroup(10, "SilverWeaponBox",
            new GroupReward
            {
                Type = ConductorCrafting.RewardType.Lockbox,
                Id = (int)LockboxTypes.SilverWeaponBox,
                CurveID = CurveLockboxWeapon,
            },
            RewardDropType.StandardDrops));

        groups.Add(SingleRewardGroup(11, "SilverModificationBox",
            new GroupReward
            {
                Type = ConductorCrafting.RewardType.Lockbox,
                Id = (int)LockboxTypes.SilverModificationBox,
                CurveID = CurveLockboxModification,
            },
            RewardDropType.StandardDrops));

        // 暗码要成批掉 (开一个银武器箱要 10 个), 所以也是 SplitDrops。
        // 没有品类之分, 原厂一律 Id = 0 (AccountLevelHelpers 里那几条升级奖励也是 0)。
        groups.Add(SingleRewardGroup(12, "Keycode",
            new GroupReward
            {
                Type = ConductorCrafting.RewardType.Keycode,
                Id = 0,
                CurveID = CurveKeycode,
            },
            RewardDropType.SplitDrops));

        AddAutoGroups(groups, curves);

        curves.Add(Curve(CurveFirstWin, "FirstWin", Flat(1f)));
        List<CacheBox> boxes = (cacheBoxes ?? Array.Empty<CacheBox>()).ToList();
        boxes.Add(ScavengerFirstWinBox());

        return DropTable.LoadDropTable(
            Revision,
            groups.ToArray(),
            Boxes(),
            curves.ToArray(),
            boxes.ToArray());
    }

    /// <summary>
    /// 版本号。客户端只拿它判"要不要重拉", 内容变了就该变 —— 跟着可调项走。
    /// 避开 0: 那是客户端"我还没有任何版本"的初值。
    /// </summary>
    public int Revision =>
        Math.Abs(17 * 31 + FirstWinContentVersion + MatchesPerWeapon * 7
                 + (int)(SilverWeaponBoxPerMatch * 1000)
                 + (int)(SilverModificationBoxPerMatch * 100)
                 + (int)(KeycodesPerMatch * 10)) + 1;

    /// <summary>
    /// 零件曲线 —— 从**武器配方**反推, 不写死。
    ///
    /// 图鉴实测配方按品阶完全规整: 每一档只用两种零件 (胶水类 + 碎片类),
    /// 而且同一档所有武器用量一致 (品阶 9 一律 50 个动物胶 + 10 片塑料)。
    /// 所以"这一档掉哪两种、掉多少"从该档任意一张蓝图读出来就够。
    ///
    /// 一种零件一条曲线, 只在它所属的那几档非零 —— 这样品阶 3 的玩家不会
    /// 掉到品阶 13 才用得上的钢片。
    /// </summary>
    private GroupReward[] BuildPartCurves(List<DropCurve> curves)
    {
        ItemSchematic[] schematics = CraftingManager.WeaponSchematics
                                     ?? Array.Empty<ItemSchematic>();

        // 零件 ID -> 每一档掉多少
        Dictionary<int, float[]> perPart = new Dictionary<int, float[]>();

        for (int tier = 0; tier < TierCount; tier++)
        {
            ItemSchematic recipe = schematics.FirstOrDefault(
                s => s != null && s.Tier == tier
                     && s.ItemParts != null && s.ItemParts.Count > 0);

            if (recipe == null)
            {
                continue;
            }

            foreach (ItemSchematicPart part in recipe.ItemParts)
            {
                if (!perPart.TryGetValue(part.ID, out float[] curve))
                {
                    curve = new float[TierCount];
                    perPart[part.ID] = curve;
                }

                curve[tier] = (float)part.Quantity / Math.Max(1, MatchesPerWeapon);
            }
        }

        List<GroupReward> rewards = new List<GroupReward>();
        int curveId = CurvePartBase;

        foreach (KeyValuePair<int, float[]> pair in perPart.OrderBy(p => p.Key))
        {
            curves.Add(Curve(curveId, "Part" + pair.Key, pair.Value));

            rewards.Add(new GroupReward
            {
                Type = ConductorCrafting.RewardType.Part,
                Id = pair.Key,
                CurveID = curveId,
            });

            curveId++;
        }

        return rewards.ToArray();
    }

    /// <summary>
    /// 计分箱 —— 决定一局能拿多少"分", 分再换算成掉落倍率。
    ///
    /// <c>BaseConstantLootValueModifier</c> 是除数, **必须在**; 其余缺了只是
    /// 那一项不加分, 不会出错 (<c>GetRewardBox</c> 查不到返回 default,
    /// Type 为 None, switch 落空)。
    ///
    /// Scout 任务 (ResistanceEasy) 是我们唯一跑得起来的模式, 按它校准:
    /// 完成给 40, 每分钟 6 —— 5 分钟 70 分、10 分钟 100 分、20 分钟 160 分。
    ///
    /// 别的队列先按同一节奏铺一份, 等真跑起来再按实测调。
    /// </summary>
    private static DropTableBox[] Boxes() => new[]
    {
        Box(DropTableBoxID.BaseConstantLootValueModifier, DropTableBoxType.Static, BaseValue),

        // Scout (ResistanceEasy)
        Box(DropTableBoxID.HordeScoutMissionBox, DropTableBoxType.Static, 40f),
        Box(DropTableBoxID.HordeScoutTimeBox, DropTableBoxType.MultiplyWithTime, 6f),

        // Horde Normal / Heroic —— 更长更难, 每分钟给得多一点
        Box(DropTableBoxID.HordeNormalMissionBox, DropTableBoxType.Static, 50f),
        Box(DropTableBoxID.HordeNormalTimeBox, DropTableBoxType.MultiplyWithTime, 7f),
        Box(DropTableBoxID.HordeNormalGoldMedal, DropTableBoxType.Static, 30f),
        Box(DropTableBoxID.HordeNormalSilverMedal, DropTableBoxType.Static, 15f),
        Box(DropTableBoxID.HordeHeroicMissionBox, DropTableBoxType.Static, 60f),
        Box(DropTableBoxID.HordeHeroicTimeBox, DropTableBoxType.MultiplyWithTime, 8f),
        Box(DropTableBoxID.HordeHeroicGoldMedal, DropTableBoxType.Static, 40f),
        Box(DropTableBoxID.HordeHeroicSilverMedal, DropTableBoxType.Static, 20f),

        // Scavenger —— 补给是这个模式的核心指标, 所以有一项按补给数乘
        Box(DropTableBoxID.ScavengerMissionBox, DropTableBoxType.Static, 40f),
        Box(DropTableBoxID.ScavengerTimeBox, DropTableBoxType.MultiplyWithTime, 5f),
        Box(DropTableBoxID.ScavengerSupplyBox, DropTableBoxType.MultiplyWithSupplies, 0.5f),
        Box(DropTableBoxID.ScavengerVictoryBox, DropTableBoxType.Static, 30f),
        Box(DropTableBoxID.ScavengerFirstBarricade, DropTableBoxType.Static, 10f),
        Box(DropTableBoxID.ScavengerFirstEndGame, DropTableBoxType.Static, 10f),
        // 每日首胜: 计分箱再加一个任务箱那么多 (我们定的), 额外那一栏见 ScavengerFirstWinBox
        Box(DropTableBoxID.ScavengerFirstWinBonus, DropTableBoxType.Static, 40f),

        // Raid
        Box(DropTableBoxID.RaidMissionMissionBox, DropTableBoxType.Static, 50f),
        Box(DropTableBoxID.RaidMissionMissionBox_Normal, DropTableBoxType.Static, 50f),
        Box(DropTableBoxID.RaidMissionMissionBox_Infectious, DropTableBoxType.Static, 60f),
        Box(DropTableBoxID.RaidMissionMissionBox_Contagious, DropTableBoxType.Static, 70f),
        Box(DropTableBoxID.RaidMissionMissionBox_Endemic, DropTableBoxType.Static, 80f),
        Box(DropTableBoxID.RaidMissionMissionBox_Pandemonic, DropTableBoxType.Static, 90f),
        Box(DropTableBoxID.RaidMissionTimeBox, DropTableBoxType.MultiplyWithTime, 6f),
        Box(DropTableBoxID.RaidMissionSupplyBox, DropTableBoxType.MultiplyWithSupplies, 0.5f),

        // 组队和加成 —— 加成道具我们不卖, 但表里留着:
        // 客户端结算画面会把每个箱子的得分单独列出来 (BoxScore)。
        Box(DropTableBoxID.PartyBox, DropTableBoxType.Static, 10f),
        Box(DropTableBoxID.RegularBoost, DropTableBoxType.Static, 50f),
        Box(DropTableBoxID.PremiumBoost, DropTableBoxType.Static, 100f),
    };

    /// <summary>
    /// 蓝图 / 改装图 / 装置 —— 走原厂的"自动组"。
    ///
    /// ## 自动组是什么
    ///
    /// <c>RewardType.AutoGroup</c> 的 <c>Id</c> 不是物品编号, 而是一个**指令**:
    /// "给一张比玩家当前品阶高一档的武器蓝图"、"给一张稀有改装图"。真正挑哪一件
    /// 由 <c>RewardManager.GiveAutoGroup</c> 现场决定 —— 它会按玩家背包里远程/近战
    /// 的比例反向配平 (缺什么给什么), 再按武器品类的数量配平一次 (品类少的更容易出)。
    ///
    /// 编码规则在 <c>AutoGroupHelper</c> 里 (<c>GetRewardTypeFromID</c> / <c>GetTier</c> /
    /// <c>GetRarity</c>), 分段很不直观, 抄在这里免得每次重新推:
    ///
    ///     1  ~ 9    武器蓝图, **相对**品阶 = Id - 1   (1 = 同阶, 2 = 高一阶)
    ///     10 ~ 19   改装图,   稀有度 = Id % 10        (ToolRarity)
    ///     20 ~ 199  装置蓝图, 品阶 = Id % 100
    ///     200~ 299  成品装置, 品阶 = Id % 100
    ///     &gt;= 300    武器蓝图, **绝对**品阶 = Id % 100
    ///
    /// 注意装置那两档: 品阶 9 的装置蓝图要写 <c>109</c> 而不是 <c>29</c> ——
    /// 29 落在装置蓝图区间没错, 但 <c>GetTier(29) = 29</c>, 挑不到任何东西。
    ///
    /// ## 每种一个组
    ///
    /// 和零件同理: 组的语义是"从里面挑一个", 塞在一起会互相挤掉。
    /// 数量一律走 StandardDrops (压成 0 或 1) —— 自动组的 Amount 是**尝试次数**
    /// (<c>GiveAutoGroup</c> 里 <c>for (i = 0; i &lt; amount; i++)</c>), 一局给两次
    /// 机会没有意义, 原厂本来也是每类每局封顶 1 件。
    /// </summary>
    private void AddAutoGroups(List<Group> groups, List<DropCurve> curves)
    {
        int curveId = CurveAutoGroupBase;
        int groupId = 200;

        // 同阶武器蓝图。Id = 1 -> 相对品阶 +0。
        //
        // 保底挂 Drop_Weapon_Schematic_Samegrade —— 这个对应关系是原厂
        // DropTable.CheckCounter 定死的: 它按 GetTier(Id) - 1 分档,
        // Id=1 算出 0, 也就是"同阶"那一档。
        curves.Add(PityCurve(curveId, "WeaponBlueprint", Flat(WeaponBlueprintPerMatch),
            AccountCounter.Drop_Weapon_Schematic_Samegrade, WeaponBlueprintPerMatch));
        groups.Add(SingleRewardGroup(groupId++, "WeaponBlueprint",
            AutoGroup(id: 1, curveId++), RewardDropType.StandardDrops));

        // 改装图, 一个稀有度一档, 各自一个计数器。
        foreach (KeyValuePair<ToolRarity, float> pair in DesignPerMatch
                     .Where(p => p.Value > 0f)
                     .OrderBy(p => p.Key))
        {
            curves.Add(PityCurve(curveId, "Design" + pair.Key, Flat(pair.Value),
                DesignCounter(pair.Key), pair.Value));
            groups.Add(SingleRewardGroup(groupId++, "Design" + pair.Key,
                AutoGroup(id: 10 + (int)pair.Key, curveId++),
                RewardDropType.StandardDrops));
        }

        // 装置只有品阶 9 和 14 两档 (图鉴实测各 8 个)。
        //
        // 用曲线**卡住玩家品阶**: 品阶 3 的玩家掉出一个 T14 装置毫无意义 ——
        // GiveAutoGroup 对装置不做任何品阶校验, 拦不拦在我们。
        foreach ((int gadgetTier, int fromPlayerTier) in GadgetTiers)
        {
            // 两档装置共用一个计数器 (原厂 CheckCounter 不按品阶分),
            // 所以 T9 掉了会把 T14 的保底也清掉。那是原厂的语义, 照做。
            curves.Add(PityCurve(curveId, "GadgetBlueprintT" + gadgetTier,
                FromTier(fromPlayerTier, GadgetBlueprintPerMatch),
                AccountCounter.Drop_Gadget_Schematic, GadgetBlueprintPerMatch));
            groups.Add(SingleRewardGroup(groupId++, "GadgetBlueprintT" + gadgetTier,
                AutoGroup(id: 100 + gadgetTier, curveId++),
                RewardDropType.StandardDrops));

            curves.Add(PityCurve(curveId, "GadgetT" + gadgetTier,
                FromTier(fromPlayerTier, GadgetPerMatch),
                AccountCounter.Drop_Gadget, GadgetPerMatch));
            groups.Add(SingleRewardGroup(groupId++, "GadgetT" + gadgetTier,
                AutoGroup(id: 200 + gadgetTier, curveId++),
                RewardDropType.StandardDrops));
        }
    }

    /// <summary>
    /// 装置的品阶, 以及"玩家到哪一阶才开始掉"。
    ///
    /// T9 装置从品阶 7 起掉、T14 从 12 起 —— 提前两档, 让玩家在够得着之前
    /// 就能开始攒, 而不是刚好卡线才见到第一个。
    /// </summary>
    private static readonly (int GadgetTier, int FromPlayerTier)[] GadgetTiers =
    {
        (9, 7),
        (14, 12),
    };

    /// <summary>
    /// 哪个稀有度的改装图对应哪个计数器 —— 对应关系由原厂
    /// <c>DropTable.CheckCounter</c> 定死。
    /// </summary>
    private static AccountCounter DesignCounter(ToolRarity rarity) => rarity switch
    {
        ToolRarity.Common => AccountCounter.Drop_Design_Common,
        ToolRarity.Uncommon => AccountCounter.Drop_Design_Uncommon,
        ToolRarity.Rare => AccountCounter.Drop_Design_Rare,
        ToolRarity.Epic => AccountCounter.Drop_Design_Epic,
        _ => AccountCounter.Drop_Design_Legendary,
    };

    private static GroupReward AutoGroup(int id, int curveId) =>
        new GroupReward
        {
            Type = ConductorCrafting.RewardType.AutoGroup,
            Id = id,
            CurveID = curveId,
        };

    /// <summary>从某一品阶起才非零的曲线。</summary>
    private static float[] FromTier(int fromTier, float value)
    {
        float[] points = new float[TierCount];

        for (int i = fromTier; i < points.Length; i++)
        {
            points[i] = value;
        }

        return points;
    }

    /// <summary>
    /// 只装一样东西的组 —— 因为组的语义是"从里面挑一个"。
    /// </summary>
    private static Group SingleRewardGroup(int id, string name, GroupReward reward,
                                           RewardDropType dropType) =>
        new Group
        {
            ID = id,
            GroupID = id,
            OrderID = id,
            Name = name,
            DropType = dropType,
            DropTimes = 1,
            DropsInGameTypes = LiveQueues,
            Rewards = new[] { reward },
        };

    /// <summary>首胜箱子内容改了就加一 (掉落表版本号跟着变, 客户端才会重拉)。</summary>
    private const int FirstWinContentVersion = 1;

    /// <summary>
    /// 拾荒每日首胜的额外奖励 —— 结算画面上单独那一栏 (<c>PlayerRewardData.FirstWinRewards</c>)。
    ///
    /// 原厂: <c>RewardManager</c> 在 <c>FirstScavengerWinBonusAvailable</c> 时按 <c>GetFirstWinBox(ScavengerFirstWin)</c>
    /// 找 <c>Type = FirstWin</c>、<c>ID = -1</c> 的箱子开一次 (系数 1), 同时计分箱加 <c>ScavengerFirstWinBonus</c>。内容随服务端丢了。
    ///
    /// **内容是我们定的**: 必得一张同阶武器蓝图 + 一个银武器箱 (<c>Always</c> + 曲线恒 1)。
    /// 一天一次, 比一局常规掉落 (蓝图几局一张、银箱几局一个) 明显多一截, 但不至于压过正常打下来的收入。
    /// </summary>
    private static CacheBox ScavengerFirstWinBox() => new CacheBox
    {
        ID = -(int)FirstWinTypes.ScavengerFirstWin,
        Name = "ScavengerFirstWin",
        Type = CacheBoxType.FirstWin,
        RewardGroups = new[]
        {
            new Group
            {
                ID = 900,
                GroupID = 900,
                OrderID = 900,
                Name = "ScavengerFirstWin",
                DropType = RewardDropType.Always,
                DropTimes = 1,
                DropsInGameTypes = new[] { QueueType.Scavenger },
                Rewards = new[]
                {
                    AutoGroup(id: 1, CurveFirstWin),
                    new GroupReward
                    {
                        Type = ConductorCrafting.RewardType.Lockbox,
                        Id = (int)LockboxTypes.SilverWeaponBox,
                        CurveID = CurveFirstWin,
                    },
                },
            },
        },
    };

    private static DropTableBox Box(DropTableBoxID id, DropTableBoxType type, float value) =>
        new DropTableBox { ID = id, Type = type, Value = value };

    private static DropCurve Curve(int id, string name, float[] points) =>
        new DropCurve
        {
            ID = id,
            Name = name,
            DropChancePoints = points,

            // -1 = 不吃保底计数器。货币和零件不需要 —— 它们每局必给。
            BasedOnCounter = -1,
            GuaranteedLootPoints = Array.Empty<float>(),
        };

    /// <summary>
    /// 带保底的曲线。
    ///
    /// ## 保底怎么起作用
    ///
    /// 原厂 <c>DropTable.GetRewards</c> 里那一段:
    ///
    ///     float frac = 曲线值 x 倍率 - floor(...);
    ///     if (curve.BasedOnCounter != -1 &amp;&amp; ...) {
    ///         int 连续没掉的局数 = counters[curve.BasedOnCounter];
    ///         frac += curve.GetGuaranteedLootCurveValue(连续没掉的局数);
    ///     }
    ///     if (frac &gt; random.NextDouble()) num++;
    ///
    /// 也就是**直接往概率上加**。加到 1 就是必掉。计数器由
    /// <c>RewardManager.CalculateCounters</c> 维护: 掉出来归零, 没掉加一。
    ///
    /// <c>BasedOnCounter</c> 是那一排计数器里的下标, 也就是
    /// <see cref="AccountCounter"/> 的枚举值 —— 每一档掉落对应哪个计数器是
    /// 原厂 <c>DropTable.CheckCounter</c> 定死的, 不能乱配。
    /// </summary>
    private static DropCurve PityCurve(int id, string name, float[] points,
                                       AccountCounter counter, float baseChance)
    {
        float[] ramp = PityRamp(baseChance);

        // ## 空补偿曲线 + 挂着计数器 = 这个人这一局什么都拿不到
        //
        // 原厂 <c>DropCurve.GetGuaranteedLootCurveValue</c> 是:
        //
        //     if (GuaranteedLootPoints == null) return 0f;
        //     if (GuaranteedLootPoints.Length > index) return GuaranteedLootPoints[index];
        //     return GuaranteedLootPoints.Last();      // <- 空数组在这里抛
        //
        // null 有兜底, **空数组没有** —— Last() 直接
        // InvalidOperationException。而这个异常会被 GetRewards 自己的 try/catch
        // 吞掉, 结果是这个人整份奖励凭空消失, 日志里一个字都没有。
        //
        // 所以配不出保底时必须连计数器一起摘掉, 退回普通曲线。
        return ramp.Length == 0
            ? Curve(id, name, points)
            : new DropCurve
            {
                ID = id,
                Name = name,
                DropChancePoints = points,
                BasedOnCounter = (int)counter,
                GuaranteedLootPoints = ramp,
            };
    }

    /// <summary>
    /// 保底补偿曲线: 下标是"连续多少局没掉", 值是额外加到概率上的量。
    ///
    /// ## 为什么按基础概率反推, 而不是拍一个固定局数
    ///
    /// 一档 0.12 的掉落平均八局一个, 二十局没出是运气差; 一档 0.004 的
    /// 平均 250 局一个, 二十局没出是**正常**。用同一个"二十局必掉"会把稀有档
    /// 直接拉平成普通档。
    ///
    /// 所以按期望反推: 熬到期望的两倍才开始补, 三倍时补满 (必掉)。
    /// 概率越低保底越晚, 稀有度保持不变 —— 保底修的是"倒霉", 不是"稀有"。
    ///
    /// ## byte 的上限
    ///
    /// 计数器是 <c>byte</c>, 而原厂那句 <c>counters[i]++</c> 没有封顶,
    /// 到 255 会绕回 0。所以补偿曲线最多铺到 255 —— 再往后配也没用。
    /// 期望超过 85 局的档 (两倍就撞上 255) 干脆不配保底, 免得留一条
    /// 永远不生效的曲线让人以为有。
    /// </summary>
    private static float[] PityRamp(float baseChance)
    {
        if (baseChance <= 0f)
        {
            return Array.Empty<float>();
        }

        int expected = (int)Math.Ceiling(1f / baseChance);
        int start = expected * 2;
        int full = expected * 3;

        if (start >= CounterMax)
        {
            return Array.Empty<float>();
        }

        int length = Math.Min(full, CounterMax) + 1;
        float[] ramp = new float[length];

        for (int misses = start; misses < length; misses++)
        {
            ramp[misses] = full > start
                ? Math.Min(1f, (float)(misses - start) / (full - start))
                : 1f;
        }

        return ramp;
    }

    /// <summary>
    /// 计数器能数到多少。<c>byte</c> 的上限 —— 原厂那句 <c>counters[i]++</c>
    /// 没有封顶, 到这里就绕回 0。
    /// </summary>
    private const int CounterMax = 255;

    private static float[] Flat(float value)
    {
        float[] points = new float[TierCount];

        for (int i = 0; i < points.Length; i++)
        {
            points[i] = value;
        }

        return points;
    }
}
