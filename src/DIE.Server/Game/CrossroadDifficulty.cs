using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Game;

/// <summary>
/// 十字路口的五档难度 —— 数**全部**来自原厂
/// <see cref="StaticCrossroadsGUIData"/>, 一个都不是我们编的。
///
/// ## 这五档是什么
///
/// 大厅"选择游戏模式"那一页列的就是它们, 和**感染等级**是两回事:
/// 感染等级是服务端按队伍装备品阶自动定的, 这五档是玩家在大厅里明着选的。
///
/// | 档 | LobbyDifficulty | 掉落上限 | 推荐品阶 | GP 加成 | 僵尸强度 | 地图 | 满队 |
/// |---|---|---|---|---|---|---|---|
/// | Normal     | Easy            | 4  | 1–3   | +0%   | 100% | Small  | 否 |
/// | Infectious | Normal          | 9  | 4–8   | +25%  | 180% | Medium | 否 |
/// | Contagious | Hard            | 15 | 9–14  | +50%  | 330% | Medium | 否 |
/// | Deadly     | Impossible      | 16 | 15–16 | +75%  | 620% | Medium | 否 |
/// | Endemic    | ExtraImpossible | 16 | 16    | +100% | 860% | Large  | **是** |
///
/// 对应关系来自客户端 <c>GUI_PlayGameModeInfoWindow.SetCrossroadGateStuff</c>
/// (那个 switch 一档一档写死的), 所以档号 0..4 就是
/// <c>LobbyDifficulty.Easy..ExtraImpossible</c>, 和票据里带上来的那个数一致
/// (<see cref="Data.QueueRosterCodec"/>)。
///
/// ## Occurences: 每档"多出来"什么
///
/// 原厂给每档列了几条特性, 界面上当卖点显示。注意那是**增量**不是全集 ——
/// Deadly 只写了 SlaughterousZombies, 不等于它没有精英僵尸。所以
/// <see cref="Has"/> 按"这一档**及以下**列过的都算数"来判 (累积),
/// 那才符合"越高越难"。
///
///   Normal     : (无)
///   Infectious : ZombieAttributes, EscortMissions
///   Contagious : EliteZombies, MassiveZombies
///   Deadly     : SlaughterousZombies
///   Endemic    : InsaneZombiePower
///
/// 目前真正接上行为的只有 <c>EscortMissions</c> (护送任务进不进任务池)。
/// 其余四条是僵尸强度那一侧的说法, 已经被
/// <see cref="PowerMultiplier"/> 统一表达了 —— 不另外再加一层,
/// 免得同一件事乘两遍。
/// </summary>
public static class CrossroadDifficulty
{
    /// <summary>档数 —— 原厂就这五档。</summary>
    public const int Count = 5;

    /// <summary>Normal 档 (大厅里的 Easy)。</summary>
    public const int Normal = 0;

    /// <summary>Infectious 档 —— 护送任务从这一档开始出现。</summary>
    public const int Infectious = 1;

    /// <summary>Contagious 档。</summary>
    public const int Contagious = 2;

    /// <summary>Deadly 档。</summary>
    public const int Deadly = 3;

    /// <summary>Endemic 档 —— 要满队才能进。</summary>
    public const int Endemic = 4;

    /// <summary>档号夹到 0..4。票据里的数是客户端给的, 不能信它一定在范围内。</summary>
    public static int Clamp(int difficulty) => Math.Max(0, Math.Min(difficulty, Count - 1));

    /// <summary>
    /// 这一档的原厂数据。
    ///
    /// <c>StaticCrossroadsGUIData</c> 那五个字段是静态构造函数填的, 服务端
    /// 引用了同一个 ConductorCrafting, 所以直接读就是原厂的数。
    /// </summary>
    public static StaticCrossroadGateData Gate(int difficulty) => Clamp(difficulty) switch
    {
        Infectious => StaticCrossroadsGUIData.InfectiousGate,
        Contagious => StaticCrossroadsGUIData.ContagiousGate,
        Deadly => StaticCrossroadsGUIData.DeadlyGate,
        Endemic => StaticCrossroadsGUIData.EndemicGate,
        _ => StaticCrossroadsGUIData.NormalGate,
    };

    /// <summary>
    /// 僵尸强度倍率 —— 原厂 <c>ZombiePowerPercentage</c> 除以 100。
    /// 1.0 / 1.8 / 3.3 / 6.2 / 8.6。
    ///
    /// ## 它替掉了我们自己编的那个数
    ///
    /// 上一版是"每档 +30%" (1.0 / 1.3 / 1.6 / 1.9 / 2.2), 那是在还没找到这份
    /// 原厂数据时的占位 —— <c>CrossroadMissionDirector.Scale</c> 的注释里写着
    /// "倍率是**我们定的**"。现在有原数了就该用原数。
    ///
    /// **落差很大**: 原厂 Endemic 是 8.6 倍, 我们那版只有 2.2 倍。原厂那一档
    /// 本来就要求满队 4 人 + 16 级装备 (见 <see cref="RequiresFullParty"/>
    /// 和推荐品阶), 所以陡是合理的 —— 但手感得实测, 觉得过头就调
    /// <see cref="PowerScale"/>。
    /// </summary>
    public static float PowerMultiplier(int difficulty) =>
        Gate(difficulty).ZombiePowerPercentage / 100f * PowerScale;

    /// <summary>
    /// 在原厂强度倍率之上再乘一道 —— 留给实测调手感用, 默认 1 (照原厂)。
    ///
    /// 做成旋钮而不是直接改上面那个换算, 是为了让"原厂的数"和"我们的调整"
    /// 分得清楚: 下次审校一眼能看出哪个是原厂锚点。
    /// </summary>
    public static float PowerScale { get; set; } = 1f;

    /// <summary>
    /// 这一档最高能掉几阶的东西 (原厂 <c>MaxLootTier</c>): 4 / 9 / 15 / 16 / 16。
    /// </summary>
    public static int MaxLootTier(int difficulty) => Gate(difficulty).MaxLootTier;

    /// <summary>
    /// Gear Points 加成百分比 (原厂 <c>GearPointsBonus</c>): 0 / 25 / 50 / 75 / 100。
    /// </summary>
    public static int GearPointsBonusPercent(int difficulty) => Gate(difficulty).GearPointsBonus;

    /// <summary>GP 结算倍率 —— 1.0 / 1.25 / 1.5 / 1.75 / 2.0。</summary>
    public static float GearPointsMultiplier(int difficulty) =>
        1f + GearPointsBonusPercent(difficulty) / 100f;

    /// <summary>
    /// 这一档要不要满队 (原厂 <c>RequiresFullParty</c>) —— 只有 Endemic 要。
    /// 客户端自己也会拦一道 (那一档的界面会亮出
    /// <c>ExtraImpossibleWarningShorter</c>), 服务端这道是防改过的客户端。
    /// </summary>
    public static bool RequiresFullParty(int difficulty) => Gate(difficulty).RequiresFullParty;

    /// <summary>这一档推荐的装备品阶区间 —— 日志和诊断用。</summary>
    public static (int Min, int Max) RecommendedTiers(int difficulty)
    {
        StaticCrossroadGateData gate = Gate(difficulty);
        return (gate.MinRecommendedTier, gate.MaxRecommendedTier);
    }

    /// <summary>这一档该用多大的图 (原厂 <c>MapSizes</c>)。</summary>
    public static IReadOnlyList<MapSize> MapSizes(int difficulty) =>
        Gate(difficulty).MapSizes ?? Array.Empty<MapSize>();

    /// <summary>
    /// 这一档有没有某条特性。
    ///
    /// **累积判定**: 原厂给每档列的是"这一档多出来什么", 不是全集 ——
    /// Deadly 那栏只写了 SlaughterousZombies, 但它当然也有 Infectious 的
    /// 僵尸属性和 Contagious 的精英僵尸。所以这里查的是"**这一档及以下**
    /// 任何一档列过它没有"。
    /// </summary>
    public static bool Has(int difficulty, CrossroadOccurence occurence)
    {
        int top = Clamp(difficulty);

        for (int level = 0; level <= top; level++)
        {
            CrossroadOccurence[] listed = Gate(level).Occurences;

            if (listed != null && listed.Contains(occurence))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 这一档出不出护送任务 (<c>CrossroadOccurence.EscortMissions</c>)。
    ///
    /// 原厂把它列在 Infectious 那一栏 —— 也就是**最低档 (Normal) 不该有
    /// 护送任务**, 那一档是给 1~3 阶新人的。玩家说的"不同难度里面的任务
    /// 不一样", 能从原厂数据里查到依据的就是这一条。
    /// </summary>
    public static bool AllowsEscortMissions(int difficulty) =>
        Has(difficulty, CrossroadOccurence.EscortMissions);

    /// <summary>
    /// 档名 —— 直接问原厂要 (<c>CrossroadLevelData.Name</c>), 不自己写死。
    ///
    /// 那张表在 <c>CraftingManager</c> 的静态构造函数里硬编码 (CraftingManager.cs:250),
    /// 不依赖图鉴 XML, 所以随时读得到。
    /// </summary>
    public static string Name(int difficulty) =>
        CraftingManager.GetCrossroadLevelInformation((ushort)Clamp(difficulty)).Name;

    /// <summary>
    /// 这一档对应的**感染等级**区间。
    ///
    /// ## 为什么难度档要管感染等级
    ///
    /// 原厂算掉落品阶上限走的是这条 (<c>RewardManager.GetMaxTier</c>):
    ///
    ///     int difficulty = CraftingManager.GetDifficulty(serverTier);   // 感染等级 -> 档
    ///     return CraftingManager.GetCrossroadLevelInformation(difficulty + 1).TierLimit;
    ///
    /// 也就是说**感染等级才是输入**, 难度档是从它反推出来的。我们这边反过来:
    /// 感染等级按队伍装备品阶自动定 (<see cref="InfectionLevel.FromWeapons"/>),
    /// 和玩家在大厅选的档没有任何关系 —— 于是选 Normal 档却带着 16 阶装备时,
    /// 原厂会算出 Endemic 的掉落上限, 掉落上限那一层等于没有。
    ///
    /// (<c>MatchLauncher</c> 那里的注释写着"原版是玩家自己在服务器列表里挑档,
    ///  私服没有那张列表" —— 那话对别的队列成立, 对十字路口**不成立**:
    ///  它有那张列表, 就是这五档。)
    ///
    /// 所以把按装备算出来的等级**夹**到这一档的区间里: 既保留"装备越好、同一档里
    /// 僵尸越强"的细粒度, 又保证原厂那个反推能落回玩家真正选的档。
    ///
    /// 区间取自原厂那张表 (<c>CrossroadLevels[d].TierLimit</c>):
    /// [1,3] / [4,8] / [9,14] / [15,15] / [16,17]。
    /// </summary>
    public static (int Low, int High) InfectionRange(int difficulty)
    {
        int gate = Clamp(difficulty);

        int low = CraftingManager.GetCrossroadLevelInformation((ushort)gate).TierLimit;

        int high = gate >= CraftingManager.GetMaxCrossroadLevel()
            ? InfectionLevel.Max
            : CraftingManager.GetCrossroadLevelInformation((ushort)(gate + 1)).TierLimit - 1;

        // 原厂表万一哪天变了也别给出一个倒过来的区间
        return (InfectionLevel.Clamp(low), InfectionLevel.Clamp(Math.Max(low, high)));
    }

    /// <summary>
    /// 把按装备算出的感染等级夹进这一档的区间 —— 见 <see cref="InfectionRange"/>。
    /// </summary>
    public static int ClampInfection(int level, int difficulty)
    {
        (int low, int high) = InfectionRange(difficulty);
        return Math.Max(low, Math.Min(level, high));
    }

    /// <summary>
    /// 这一档实际会掉到几阶 —— 按原厂 <c>RewardManager.GetMaxTier</c> 的算法
    /// 原样走一遍, 用来对照 <see cref="MaxLootTier"/> (那是界面上写给玩家看的数)。
    ///
    /// 两者对得上才说明感染等级被夹对了; 对不上就是这一档的掉落上限没生效。
    /// </summary>
    public static int EffectiveLootTier(int difficulty)
    {
        int gate = Clamp(difficulty);

        return gate < CraftingManager.GetMaxCrossroadLevel() - 1
            ? CraftingManager.GetCrossroadLevelInformation((ushort)(gate + 1)).TierLimit
            : CraftingManager.MaxWeaponTier;
    }
}
