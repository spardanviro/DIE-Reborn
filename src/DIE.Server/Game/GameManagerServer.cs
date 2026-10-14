using System;
using System.Collections.Generic;
using ConductorGameLogic;
// CharacterVoiceClip 住在 Stunlock.ConductorData.dll 的 ConductorCrafting 里,
// 不在 GameLogic 树中 —— 反射查出来的, 别再靠 using 猜。
using ConductorCrafting;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Stats;
using _0023rye;
using ConductorGameLogic.GameModes;
using _0023bd;

using ConductorGameLogic.View;
using StunGameNetwork;
using System.IO;

namespace DIE.Server.Game;

/// <summary>
/// 服务端的 <see cref="GameManager"/>。
///
/// ## 为什么只有一个成员
///
/// <c>GameManager</c> 3206 行, 但只有**一个**抽象成员 —— <c>IsServer</c>。
/// 双端差异不是靠继承表达的, 而是散在 19 处 <c>if (IsServer)</c> 分支里。
/// 也就是说服务端逻辑本身是现成的, 只要把这个开关拨到 true。
///
/// 这也是 3A 勘察最重要的结论: 原厂服务端和客户端共用同一套模拟层,
/// 我们不需要重写游戏逻辑, 只需要把它以服务端模式跑起来。
/// </summary>
public sealed class GameManagerServer : GameManager
{
    public GameManagerServer(IViewManager viewManager, string mapsDirectory, string gameHash,
                             Stream craftingDataStream = null)
        : base(viewManager, mapsDirectory, gameHash, craftingDataStream)
    {
        // GameObjectBase.GameManager 走的是 ThreadStaticInstance, 而且带一致性校验
        // (GameObjectBase.cs:321 —— 对不上就抛 "ThreadStaticInstance is not correct")。
        // CacheGame 里创建的每个对象都会读它, 所以必须在构造时就设好, 不能等到 LoadMap
        // (LoadMap 里也有一次赋值, 但那已经晚了)。客户端同样在构造函数末尾设
        // (GameManagerClient.cs:264)。
        ThreadStaticInstance = this;
    }

    public override bool IsServer => true;

    /// <summary>
    /// 有角色死了 —— 在这里发经验。
    ///
    /// ## 经验系统本身是完整的, 只是没人调
    ///
    /// <c>_0023bd._0023Zt</c> (-bd/-Zt.cs, 204 行) 把整套规则都留下了:
    ///
    ///   _00234R  按 ObjCat 分类查经验值 (Walker 10 / Infected 15 / Specials 45
    ///            / Elites 125 / Hoarder 200 / 玩家 100, 取自 BalanceData)
    ///   _00238R  分发: 同队、活着、且离尸体 150 单位内的玩家均摊
    ///   _00239R  按等级差修正
    ///   _00235R / _00237R  各种武器升级的加成
    ///
    /// 全代码树搜 <c>_0023Zt.</c> —— **零命中**。调用方是服务端, 随服务端丢失。
    /// 我们不重写任何规则, 只是把它接回来。
    ///
    /// ## 为什么挂在这里
    ///
    /// <c>GameManager.CharacterDied</c> (:2076) 是死亡的唯一汇合点, 它已经在调
    /// <c>GameMode.OnCharacterDied</c>。但那个方法的服务端分支 (GameMode.cs:1481)
    /// 只管击杀播报和语音, **没有经验** —— 所以不能指望它。
    ///
    /// ## 教程里升级慢一级
    ///
    /// <c>Entity.GetLevel</c> (:1161) 在教程模式下返回 <c>i</c> 而不是 <c>i + 1</c>,
    /// 也就是说教程里同样的经验比正式局低一级。等级门槛是 80/176/292/...
    /// (Entity.cs:383), Walker 值 10 —— 教程里要打 8 只才到 1 级。
    ///
    /// 这是原厂行为, 不是我们的设定。
    /// </summary>
    public override void CharacterDied(Character character, GameObjectBase killer)
    {
        base.CharacterDied(character, killer);

        if (character is Player died)
        {
            ApplyRaidRespawnTime(died);
            DropCarriedSupplies(died);
        }

        if (character != null && GameMode is { IsScavengerHunt: true })
        {
            Player killingPlayer = KillingPlayer(character, killer);
            _killedBy[character] = (killingPlayer, character.NumberOfGlobalCacheUses);
            ScavengerScores.OnCharacterDied(this, character, killingPlayer);
        }

        if (character == null || killer == null)
        {
            if (character != null)
            {
                _lastPlayerHit.Remove(character);
            }

            return;
        }

        float before = LocalExperienceForDiagnostics();

        _0023Zt._00232R(character, killer, this);

        float after = LocalExperienceForDiagnostics();
        if (after > before)
        {
            OnExperienceGranted?.Invoke(character, after - before, after);
        }

        DropSupplies(character, killer);

        // 死了就把"谁打过它"清掉 —— 对象回池再取出来时使用次数不一定变, 不清的话下一只会继承上一只的记录
        _lastPlayerHit.Remove(character);
    }

    /// <summary>
    /// 十字路口和 Scout 的复活等待时间: **等级越高等越久**。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "平时复活的等待时间和角色等级有关系, 等级越高等待时间越长。"
    ///
    /// ## 原厂那条公式还在, 只是十字路口没走到
    ///
    /// <c>Player.SetRespawnTime</c> (Player.cs:2239) 在死亡时算好, 按模式分支:
    ///
    ///   被夺魂                 BalanceData._0023d5f._0023fq
    ///   Scout / 十字路口       GameMode._0023Bi()  —— 基类返回 0, 服务端子类覆盖的那个丢了
    ///   TvT                   5 + 等级 × 3
    ///   其余                   (5 + 等级 × 2) × (1 + 残酷命运 − 恢复)
    ///
    /// <c>_0023Bi()</c> 不带玩家参数, 给不出按等级的数; 玩家描述的正是"其余"那一支的形状。
    /// 所以十字路口和 Scout (同样落在 <c>_0023Bi()</c> 的 0 上) 改走那一支 —— 玩家: "全部按原版设定来设置"。数全是原厂的 (<c>_0023sB = 5</c>、<c>_0023rB = 2</c> 在 Player.Reset,
    /// 残酷命运 <c>_0023GB</c> 是杀手武器的升级、恢复是自己武器的 <c>WeaponUpgrade_Recuperate</c>):
    /// 1 级 7 秒, 10 级 25 秒, 20 级 45 秒。客户端同步时封顶 60 (Player.cs:1694)。
    ///
    /// 挂在这里是因为顺序: <c>Character.Kill</c> 先调 <c>OnDeathCharacter</c> (那里 SetRespawnTime 得出 0),
    /// 再调 <c>GameManager.CharacterDied</c>。在这之后改写, 死亡面板和 RespawnPolicy 读到的就是新数。
    /// 团灭罚时 (CrossroadMissionDirector.WatchForWipe) 在这之上再加。
    /// </summary>
    private static void ApplyRaidRespawnTime(Player player)
    {
        GameMode mode = player.GameManager?.GameMode;
        if (mode == null || !(mode.IsRaidMission || mode.IsScoutMission) || player.IsSoulClaimed)
        {
            return;
        }

        float perLevel = ReadPlayerFloat(player, "_0023rB", 2f);
        float baseSeconds = ReadPlayerFloat(player, "_0023sB", 5f);
        float cruelFate = ReadPlayerFloat(player, "_0023GB", 0f);

        float recuperate = 0f;
        Upgrade upgrade = _0023Oq._0023LZb._0023KZb(player, UpgradeType.WeaponUpgrade_Recuperate, _0023By: true);
        if (upgrade.Exists)
        {
            recuperate = upgrade.Value;
        }

        float seconds = (baseSeconds + player.GetLevel() * perLevel) * (1f + (cruelFate - recuperate));
        player.SetRespawnTime(Math.Max(0f, seconds));
    }

    private static readonly Dictionary<string, System.Reflection.FieldInfo> PlayerFields =
        new Dictionary<string, System.Reflection.FieldInfo>();

    /// <summary>读 Player 上的私有浮点字段 (原厂公式用的那几个); 找不到用原厂默认值。</summary>
    private static float ReadPlayerFloat(Player player, string name, float fallback)
    {
        if (!PlayerFields.TryGetValue(name, out System.Reflection.FieldInfo field))
        {
            field = typeof(Player).GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            PlayerFields[name] = field;
        }

        return field?.GetValue(player) is float value ? value : fallback;
    }

    /// <summary>
    /// 拾荒: 玩家死了, 身上背的补给全掉在地上 (5 个一包抛开, 零头原地一堆) —— 谁都能捡, 这是抢补给的玩法核心。
    ///
    /// 实现是原厂的 <c>Supplies._00236j(死者)</c> (Supplies.cs:365): 抛包 + 零头 + 把身上清零。
    /// 全树**零调用方**, 调它的是服务端。只在拾荒里调: Scout / 十字路口原厂有没有这条没有线索, 不擅自加。
    /// </summary>
    private void DropCarriedSupplies(Player player)
    {
        if (!GameMode.IsScavengerHunt)
        {
            return;
        }

        Supplies supplies = GameMode.AsISupplies?._00239c();
        if (supplies != null && supplies._0023Fj(player.IndexPlayer) > 0)
        {
            supplies._00236j(player);
        }
    }

    /// <summary>
    /// 死亡掉落补给箱。
    ///
    /// ## 又一个"完整实现但零调用方"
    ///
    /// <c>Supplies._0023Xj(死者, 杀手)</c> (Supplies.cs:315) 规则全在, 而且
    /// 全代码树搜下来**没有任何调用方** —— 调它的是服务端, 随服务端丢失。
    ///
    /// ## 掉落规则是原厂写死的, 不是我们定的
    ///
    ///   IsLooter        -> 3 个 (BalanceData._0023Ntb)
    ///   IsSpecialZombie -> 1 个
    ///   IsElite         -> 3 个
    ///   **普通 Walker   -> 不掉**
    ///
    /// 概率参数传的是 3 (<c>Random.NextDouble() &lt;= 3</c> 恒真), 所以上面三类
    /// 是 100% 掉落。
    ///
    /// 玩家报告"打死普通僵尸也会掉小资源箱" —— 但代码里普通 Walker 明确不掉。
    /// 他自己说记不清了, 所以按代码来。教程里的补给来源应该是别处
    /// (地图上的 <c>SupplySpawnPoint</c>), 那条另查。
    ///
    /// ## 杀手必须有 Owner
    ///
    /// <c>_0023Xj</c> 第一句判 <c>_0023Rg.Owner != null</c> —— 真机上 killer 是
    /// 技能生成的判定对象, 它的 Owner 是玩家。僵尸互殴或环境伤害没有 Owner,
    /// 不掉, 这也是原厂的判定。
    /// </summary>
    private void DropSupplies(Character character, GameObjectBase killer)
    {
        if (GameMode is not { ImpSupplies: true } || GameMode.AsISupplies?._00239c() == null)
        {
            return;
        }

        // BOSS: 原厂囤积者那一支 —— 死了就抛一大把, 不看是谁打死的
        if (IsBossLike(character))
        {
            // 拾荒模式里囤积者那一把原厂自己抛了 (Character.cs:2911, 共享代码, 双端都跑), 再抛就是两份
            if (GameMode.IsScavengerHunt && character.IsHoarderZombie && !character.IsLooter)
            {
                return;
            }

            ThrowBossSupplies(character);
            return;
        }

        // 掉不掉: 原厂那句 (玩家的出手打死的), 十字路口再加"玩家打过、炮塔补刀"
        bool byPlayer = killer?.Owner != null && !killer.Owner.IsSpawnTurret;
        if (!byPlayer && !TurretFinishedWhatAPlayerStarted(character, killer))
        {
            return;
        }

        // 掉多少: 基础量 (宿主可以改) × 感染等级曲线
        int baseAmount = SupplyDropOverride?.Invoke(character) ?? BaseSupplies(character);
        if (baseAmount <= 0)
        {
            return;
        }

        // 拾荒不乘感染曲线: 胜利线 / 卡车线是写死的 (-j7f 的 500/800 等), 掉落跟着涨会把节奏打乱。
        // 玩家实测 (2026-09-17): "普通僵尸随机掉落的应该是 1 点物资, 现在掉 3 点" —— 感染 10 级曲线把 1 乘成了 3。
        // 地上一堆的数量是 byte。十字路口伏击任务的具名目标一只能分到 75 (见
        // CrossroadMissionDirector.SupplyDropFor), 乘上高感染曲线会过 255 —— 过了就分几堆, 不截掉。
        int amount = GameMode.IsScavengerHunt ? baseAmount : ScaleSuppliesUncapped(baseAmount);

        while (amount > 0)
        {
            int pile = Math.Min(amount, byte.MaxValue);
            SpawnSupplyPickup(character.Position, pile);
            amount -= pile;
        }
    }

    /// <summary>
    /// 一只僵尸的**基础**掉落量 (感染 1 级时掉多少)。
    ///
    /// 原厂 <c>Supplies._0023Xj</c> (Supplies.cs:319-330), 顺序照抄:
    ///
    ///   IsLooter        -&gt; <c>_0023Ntb</c> = 3
    ///   IsSpecialZombie -&gt; 1 (09-18 起改 3, 感染者 2, 见方法里)
    ///   IsElite         -&gt; <c>_0023Ntb</c> = 3
    ///   其余 (普通行尸)  -&gt; 原厂不掉; 我们按玩家回忆加了 <see cref="WalkerSupplyChance"/> 的几率掉 1
    ///
    /// BOSS (囤积者里除 Looter 以外的, 和巨型) 不走这里, 见 <see cref="ThrowBossSupplies"/>。
    /// </summary>
    private int BaseSupplies(Character character)
    {
        if (character.IsLooter)
        {
            return BalanceData._0023j7f._0023Ntb;
        }

        // 特殊僵尸 3、感染者 2: 原厂补给设置 _0023lj 里那张没有调用方的掉落表 (_0023dj[Specials] = 3、[Infected] = 2,
        // 几率 _0023cj[Zombie] = 1 即必掉)。共享代码 _0023Xj 里特殊是 1、感染者不掉; 玩家定用那张表 (2026-09-18)。
        if (character.IsSpecialZombie)
        {
            return SpecialSupplies;
        }

        if (character.IsZombie && TypeInCategories(character, ObjCat.Infected))
        {
            return InfectedSupplies;
        }

        if (character.IsElite)
        {
            return BalanceData._0023j7f._0023Ntb;
        }

        // 普通僵尸也有几率掉 1。玩家对原版的回忆 (2026-09-17): "普通僵尸有几率掉 1 点物资, 精英怪掉 3 点" ——
        // 原厂 _0023Xj 里没有这一支 (那份代码没有调用方, 原版服务端可能另有一份)。几率没有数据, **我们定的**。
        // 先在拾荒上, 同一天玩家要求十字路口和 Scout 也一样。教程不加: 它的引导步骤看"身上有没有补给", 多掉会乱序。
        if (character.IsZombie && !GameMode.IsTutorialGameMode && Random.NextDouble() < WalkerSupplyChance)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// 普通僵尸掉 1 点补给的几率, 见 <see cref="BaseSupplies"/>。0.25 取自原厂补给设置 <c>_0023lj</c> 里那张
    /// 没有调用方的掉落表 (<c>_0023cj[ObjCat.Walker] = 0.25</c>、<c>_0023dj[ObjCat.Walker] = 1</c>); 玩家定用它 (2026-09-18, 之前是我们定的 0.2)。
    /// </summary>
    public static double WalkerSupplyChance { get; set; } = 0.25;

    /// <summary>特殊僵尸 (屠夫 / 飘浮者 / Puller / Ram / 尖叫者 / 自爆者) 必掉的补给。见 <see cref="BaseSupplies"/>。</summary>
    public const int SpecialSupplies = 3;

    /// <summary>感染者 (Runner / Veteran / Spitter) 必掉的补给。见 <see cref="BaseSupplies"/>。</summary>
    public const int InfectedSupplies = 2;

    /// <summary>
    /// BOSS 死亡: 抛 <see cref="BossSupplyPacks"/> 包、每包 <see cref="BossSuppliesPerPack"/> × 感染曲线。
    ///
    /// ## 原厂有这一支, 只是写在拾荒模式里
    ///
    /// 玩家 (2026-09-14): "你说的掉落 3 似乎是 scavenger 模式下的吧, crossroad 模式下击杀 boss 应该能获得大量物资吧。"
    ///
    /// 查下来: "Looter/精英 3、特殊 1" (<c>Supplies._0023Xj</c>) 是所有带补给的模式通用的, 十字路口也是;
    /// 但 BOSS 另有一支 —— <c>Character</c> 死亡处理 (Character.cs:2911-2921):
    ///
    ///     if (GameMode.IsScavengerHunt &amp;&amp; IsHoarderZombie &amp;&amp; !IsLooter)
    ///         抛撒器 _0023cyc { _00237q = _0023Qtb / 5 (=30 包), _0023Pec = 5 (每包 5), 中心 = 死亡位置 }
    ///
    /// <c>_0023Qtb</c> = 150 (-cL/-j7f.cs)。客户端这段只认拾荒模式, 十字路口那段在服务端丢了。按玩家的判断
    /// 给十字路口 (以及任何带补给的模式, 拾荒模式客户端自己会抛, 这里跳过免得双份) 也补上, 数照抄原厂;
    /// 覆盖面扩到巨型 (原厂那句只写了囤积者, 巨型挂的是 <c>ObjCat.Bosses</c> 不是囤积者)。
    /// 每包量再乘感染等级曲线 (玩家要的挂钩, 见 <see cref="ScaleSupplies"/>)。
    /// </summary>
    private void ThrowBossSupplies(Character boss)
    {
        if (GameMode.IsScavengerHunt)
        {
            return;     // 客户端那句自己会抛 (Character.cs:2911)
        }

        SupplyThrower.Throw(this, boss.Position, BossSupplyPacks, ScaleSupplies(BossSuppliesPerPack),
            SupplyThrower.DefaultScatter, boss);
    }

    /// <summary>BOSS 抛几包: 原厂 <c>_0023Qtb / 5</c> = 150 / 5 = 30。</summary>
    public static int BossSupplyPacks => BalanceDataQtb / 5;

    /// <summary>BOSS 每包多少 (感染 1 级): 原厂 5。</summary>
    public const int BossSuppliesPerPack = 5;

    private static int BalanceDataQtb => BalanceData._0023j7f._0023Qtb;

    /// <summary>囤积者 (不含 Looter, 它原厂就掉) 和巨型 (<c>ObjCat.Bosses</c>)。</summary>
    private bool IsBossLike(Character character) =>
        (character.IsHoarderZombie && !character.IsLooter)
        || TypeInCategories(character, ObjCat.Bosses);

    /// <summary>
    /// 掉落量 × 感染等级曲线。
    ///
    /// 玩家 (2026-09-14): "掉落物资数量和感染等级应该也挂钩" -&gt; 选"跟僵尸血量同曲线"。
    /// 曲线就是 <c>ServerModifier</c>: 宿主开局按 <c>CraftingManager.GetInfectionModifier</c> 设
    /// (GameCraftingData.xml 的 InfectionLevelIncrease, 1 级 1.0 ... 17 级 7.52), 僵尸基础属性乘的也是它。
    /// 1 级就是原数; 至少 1; 地上那堆的数量是 byte, 封顶 255 (30 × 7.52 = 225, 放得下)。
    /// 原厂掉落量是写死的, 不随感染等级变 —— 这是玩家要的改动。
    /// </summary>
    public int ScaleSupplies(int baseAmount) =>
        Math.Min(ScaleSuppliesUncapped(baseAmount), byte.MaxValue);

    /// <summary>同 <see cref="ScaleSupplies"/>, 但不封顶 255 —— 调用方自己分堆。</summary>
    public int ScaleSuppliesUncapped(int baseAmount)
    {
        float curve = ServerModifier > 0f ? ServerModifier : 1f;
        return Math.Max(1, (int)Math.Round(baseAmount * curve));
    }

    /// <summary>
    /// 宿主改某些死者的**基础**掉落量 (返回 null = 照 <see cref="BaseSupplies"/>)。只改"掉多少", 不改"掉不掉";
    /// 结果照样乘感染等级曲线。
    ///
    /// 现在只有十字路口夜袭的 Looter 用 (<see cref="CrossroadMissionDirector.SupplyDropFor"/>)。
    /// </summary>
    public Func<Character, int?> SupplyDropOverride { get; set; }

    /// <summary>
    /// 十字路口: 玩家打过的僵尸被基地炮塔补了最后一下 —— 照样算玩家的。
    ///
    /// ## 原厂规则是"炮塔打死的不掉"
    ///
    /// <c>Supplies._0023Xj</c> (Supplies.cs:317) 开头 <c>!killer.Owner.IsSpawnTurret</c>。玩家实测
    /// (2026-09-13 双人): "夜袭时刷的 looter 打死不掉物资箱" —— 夜袭就在基地炮塔底下打, 最后一下多半是炮塔的。
    ///
    /// 这里放宽成"**有玩家打过**就算玩家的" (玩家的伤害走 <see cref="NotifyDamaged"/>, 记在
    /// <see cref="LastPlayerToHit"/>)。纯炮塔打死、玩家一下没碰的, 仍按原厂不掉。
    /// </summary>
    private bool TurretFinishedWhatAPlayerStarted(Character character, GameObjectBase killer) =>
        GameMode is ConductorGameLogic.GameModes.RaidMission
        && killer?.Owner is Entity owner && owner.IsSpawnTurret
        && LastPlayerToHit(character, character.NumberOfGlobalCacheUses) != null;

    /// <summary>地上放一堆补给 —— 原厂 <c>Supplies._0023Yj</c> 那几行 (私有, 照抄)。</summary>
    private void SpawnSupplyPickup(FarseerPhysics.Common.Vector2 position, int amount)
    {
        _0023aX._0023yGc pickup = GetGameObjectFromPool<_0023aX._0023yGc>();
        if (pickup == null)
        {
            return;
        }

        pickup._0023B = position;
        pickup._0023sj = (byte)amount;
        SpawnGameObject(pickup, null, null);
    }

    /// <summary>
    /// 经验发出去之后的回调, 供宿主打日志。
    ///
    /// 参数: (被杀的角色, 本次获得, 累计经验)。
    /// </summary>
    public System.Action<Character, float, float> OnExperienceGranted { get; set; }

    /// <summary>
    /// 第一个活动玩家的累计经验。
    ///
    /// 只用于诊断 —— 单人局够用, 多人局要按玩家逐个记时再改。
    /// 经验是**静默**发放的 (走 ChangeStats, 没有任何日志), 所以不这样取差值
    /// 就完全看不出"到底发了没有"。
    /// </summary>
    private float LocalExperienceForDiagnostics()
    {
        if (ActivePlayerList == null || ActivePlayerList.Count == 0)
        {
            return 0f;
        }

        return ActivePlayerList[0].Stats.GetValue(StatType.Experience);
    }

    /// <summary>
    /// 把一个从对象池取出来的对象放进世界。
    ///
    /// ## 基类是空的, 而且这是最要命的一处
    ///
    /// <c>GameManager.SpawnGameObject</c> 是个空 virtual (GameManager.cs:1835) ——
    /// 紧挨着的 <c>ReturnGameObjectToPool</c> 却有完整实现 (:1844)。这个不对称
    /// 说明得很清楚: 归还是两端都要做的事, 而**生成是服务端独占的**, 所以实现
    /// 在没发布的那半边。
    ///
    /// 后果的覆盖面比之前任何一个缺口都大 —— 全代码树都经它生成东西:
    ///
    ///   HordeMode._00234fc()          地图上的 Checkpoint (决定 Horde 的出生点)
    ///   HordeMode._00237fc()          酸液清除器
    ///   GameManager.GetAndSpawnGameObjectFromPool<T>()  以及 GameManager 内部 5 处
    ///   ScavengerHunt / RaidMission / ...
    ///
    /// 实测: 不实现它, Horde 图加载完 <c>ActiveGameObjectList</c> 是 **0**,
    /// 地图里那 4 个 <c>Event_Checkpoint</c> 一个都没变成实体, 于是
    /// <c>HordeMode.GetSpawnPosition</c> 返回 <c>Vector2.Zero</c> —— 玩家会生在地图外。
    /// 而且全程零异常零日志。
    ///
    /// ## 契约是从调用点反推的
    ///
    /// 调用方**在调用之前**就把配置写好了, 例如 HordeMode.cs:126:
    ///
    ///     obj.TriggerPos = ...; obj.SpawnPos = ...;
    ///     obj.TriggerRadius = ...; obj.TriggerID = ...;
    ///     GameManager.SpawnGameObject(obj, null, null);
    ///
    /// 所以这里不该碰位置或任何配置字段, 只负责"接上关系 + 激活"。激活用
    /// <c>GameObjectBase.Spawn()</c> —— 那是原厂的真入口, 幸存且完整
    /// (GameObjectBase.cs:1122): 它管 GlobalSpawnID、ActiveGameObjectList、
    /// 分类索引、每客户端同步位、以及子类的 <c>SpawnGameObject()</c> 钩子。
    ///
    /// 佐证: <c>-bd/-99.cs:126</c> 和 <c>RaidMission.cs:493</c> 这两处幸存的代码
    /// 就是直接调 <c>Spawn()</c>。
    ///
    /// ## spawner: 技能派生物继承"哪个技能、几级、多大威力"
    ///
    /// 技能本体生成的东西由 <c>Ability</c> 自己填 <c>OwnerAbility</c> + <c>AbilityCastData</c>
    /// (Ability.cs:1493/1628, spawner 传 null)。而**技能对象再生成的东西** —— 炮塔打出的火箭
    /// (-EK/-mHc.cs:469)、大招 buff 连射的火箭 (-Sq/-tWb.cs:103)、飞艇弹落地变成的飞艇
    /// (-EF/-gK.cs:80)、投射物命中挂的 buff —— 调用方只传 <c>SpawnGameObject(子, Owner, this)</c>,
    /// 什么都不填, 子对象却照读 <c>AbilityCastData.Power / AbilityLevel</c> 和 <c>OwnerAbility.Level</c>。
    /// 所以这层继承只能在这里做 (原厂服务端那半丢了)。
    ///
    /// 佐证: <c>GameObjectBase.CallOwnerAbilityGetter</c> 默认 true、客户端树里**没有一处**置 false,
    /// 而客户端收到 OwnerAbility 且它为 true 时会拿技能的"造对象"回调去配这个对象 (GameObjectBase.cs:1441)
    /// —— 派生物显然不该走那条, 这个开关只能是服务端给派生物关的。
    ///
    /// 玩家实测 (2026-09-15) 不继承的后果:
    ///   - Power 取默认 1 -> Isys 大招火箭 / 炮塔升级火箭 / Bryce 连射等伤害只剩个位数, 等级也恒为 1
    ///   - OwnerAbility 为 null -> Herrmann 飞艇生成和 Update 都 NRE, 被主循环标坏 (模型消失、不打人);
    ///     Amber 信息素 debuff 生成时 NRE
    /// </summary>
    public override void SpawnGameObject(GameObjectBase gameObject, Entity owner, GameObjectBase spawner)
    {
        if (gameObject == null)
        {
            return;
        }

        if (owner != null)
        {
            gameObject.SetOwner(owner);

            // **队伍必须跟着 owner 传下去。**
            //
            // GameObjectBase.Reset() 把 TeamId 清成 Neutral (:1102 那段), 而
            // 对象每次从池里出来都会被 Reset。所以生成时不设, 它就是中立的。
            //
            // 后果不是"少了个标记", 而是**技能会打到自己人**:
            // IsEnemy 最后比的是 **this.TeamId**, 不是 owner 的 (:1023) ——
            //
            //     if (TeamId == gameObject.TeamId) { ... return false; }
            //     return true;
            //
            // 普通攻击的伤害对象 (_00233wc = Heavy/Fists/Light/RiflePrimary) 走
            // CollisionManager 的 _00235sc 过滤器, 那个过滤器就是
            // `_0023Vg.IsEnemy(候选)` (`-6/-jtc.cs:36`)。攻击对象是 Neutral、
            // 玩家是 Team1, 于是**玩家成了自己普攻的合法目标**。
            //
            // 真机表现: 站在空地上普攻, 自己掉血。伤害日志里
            // `来源=_00233wc#N 自伤=False` —— 因为 Changer 记的是技能对象而不是玩家,
            // 所以连"自伤"都标不出来。
            //
            // 判据: 单元测试里 attack.IsEnemy(owner) 从 True 变 False。
            gameObject.TeamId = owner.TeamId;
        }

        InheritAbility(gameObject, spawner);
        gameObject.Spawn();
    }

    /// <summary>派生物继承生成者的技能来源和施法数据 (见 <see cref="SpawnGameObject"/>)。自己已经有的不动。</summary>
    public static void InheritAbility(GameObjectBase child, GameObjectBase spawner)
    {
        // 池空拿到的 dummy 没绑模拟层 (GameManager 为 null), OwnerAbility 的 getter 会 NRE
        if (spawner == null || spawner == child || spawner.GameManager == null || child.GameManager == null)
        {
            return;
        }

        if (child.OwnerAbility == null && spawner.OwnerAbility is ConductorGameLogic.Abilities.Ability ability)
        {
            child.OwnerAbility = ability;
            child.CallOwnerAbilityGetter = false;
        }

        if (!child.AbilityCastData.HasData && spawner.AbilityCastData.HasData)
        {
            child.AbilityCastData = spawner.AbilityCastData;
        }
    }

    /// <summary>
    /// 属性变更 ID 的发号器。
    ///
    /// 每次 <see cref="ChangeStats"/> 都要给这次变更一个 id, 之后靠它把变更撤销
    /// (<c>_0023tK.Remove()</c> -> <c>StatContainer._0023Rac(id)</c>) 或改值
    /// (<c>_0023yld(id, value)</c>)。
    ///
    /// **0 是保留值** —— <c>_0023tK.IsDummy</c> 的定义就是 <c>id == 0</c>
    /// (`-0K/-tK.cs:28`)。所以从 1 开始。
    ///
    /// 原厂用什么发号已经无从考证 (GameManager 上没有对应的计数器字段, 那部分
    /// 随服务端丢失)。从读取侧能确定的唯一要求是**唯一性** —— 单调递增满足它。
    /// </summary>
    private uint _nextStatChangeId = 1;

    /// <summary>诊断: 目标属性表里没有、被跳过的属性变更次数 (见 <see cref="ChangeStats"/>)。</summary>
    public int MissingStatChanges;

    private readonly HashSet<(Type, StatType)> _missingStatsLogged = new HashSet<(Type, StatType)>();

    /// <summary>
    /// 应用一次属性变更。
    ///
    /// ## 基类是桩, 而这是战斗的总开关
    ///
    /// <c>GameManager.ChangeStats</c> 原本是 <c>return _0023Gfd;</c>
    /// (GameManager.cs:1131) —— 返回一个空壳句柄, 什么都不做。相邻的
    /// <c>ChangeResistanceStats</c> 同样 (:1136)。
    ///
    /// 后果远不止某一个数值不对:
    ///
    /// - 角色类在初始化时把基础属性放进 <c>BaseStats</c> (例如 `-Mk/-0bb.cs:42`
    ///   的 MaxHealth / Power / MaxRage), 然后在 <c>_00238N()</c> 里逐条调本方法
    ///   来应用。应用是空的, 于是 <c>Stats</c> 里根本没有 MaxHealth, 而
    ///   <c>Character.MaxHealth</c> 在查不到时返回 0 (Character.cs:583)。
    ///   真机表现: 血条画不出来 (Health/MaxHealth 除零), 玩家 HUD 显示 0100/0000。
    /// - 伤害、治疗、buff、护甲、暴击 **全部**经这一个方法。它是桩就意味着
    ///   谁也打不死谁。
    ///
    /// ## 实现只是转发, 计算全在原厂代码里
    ///
    /// <c>StatContainer._0023aBc</c> (:345) 是完整的 —— 它算值、记录这次变更、
    /// 标脏、必要时发通知。我们要做的只有三件事: 发个 id、调它、把返回值和
    /// out 参数组装成 <c>_0023tK</c> 句柄。
    ///
    /// 参数对应关系从两侧的签名和 <c>_0023tK</c> 的构造函数反推:
    ///
    ///   _0023aBc(statType, id, stackType, reason, changeType, changerID,
    ///            pure, isPermanent, value, changer, out 五个 bool) -> float
    ///   new _0023tK(statType, entityID, id, changerID, isPermanent,
    ///               permanentChange, isTranced, isCrit, isPunctured, isBlocked)
    ///
    /// 返回的 float 正是句柄要的 <c>permanentChange</c>, 四个 out bool 也一一对上。
    ///
    /// ## sendSCT: 属性重算不该飘字
    ///
    /// <c>_0023aBc</c> 的签名里没有 <c>sendSCT</c> 的位置 —— 它管的不是计算而是
    /// **表现**: 这次变更要不要在屏幕上飘一个数字。原厂在一堆地方显式传 false,
    /// 最典型的是升级 (<c>Player.OnLevelUp</c>:2119):
    ///
    ///     ChangeStats(this, StatType.Health, ..., num, StatChangeReason._0023fjc,
    ///                 ..., sendSCT: false, includeInScoreboard: false);
    ///
    /// 升级时血上限涨了, 引擎把差值补进当前血量 —— 那是**账面调整**, 不是有人
    /// 治疗了你。同一次升级里 <c>SetWeaponAndEquipGlobals</c> 还会把武器 globals
    /// 先撤后加, 又是一减一加。玩家实测看到的"升级的时候出现加血减血"就是这些。
    ///
    /// ## 为什么要用一个字段而不是参数传下去
    ///
    /// 飘字是在 <see cref="OnCharacterDamageTaken"/> 里发的, 而那个回调是
    /// <c>StatContainer</c> 的**监听器**转过来的 (Character.cs:2344-2347),
    /// 中间隔着原厂代码, 加不了参数。
    ///
    /// 但它是**同步**触发的 —— <c>_0023aBc</c> 内部直接调监听器, 没有排队。
    /// 所以在调用它前后包一个标志就够了。用计数器而不是 bool: 监听器里再次
    /// 调 <c>ChangeStats</c> 是常见的 (吸血、反伤), 嵌套时 bool 会被内层提前清掉。
    ///
    /// <c>order</c> 和 <c>includeInScoreboard</c> 都已经接上了: 前者见下方那段注释,
    /// 后者在本方法末尾 —— 为真才调 <see cref="RecordScoreboardStats"/>, 所以升级补血那种
    /// 账面调整 (Player.cs:2119 传 false) 不会被算进治疗量。计分板本身由
    /// <see cref="RecordScoreboardStats"/> + <see cref="PlayerDataSender"/> 驱动。
    /// </summary>
    public override _00230k._0023tK ChangeStats(
        Entity entity, StatType statType, _00230k._0023UIc stackType, float value,
        StatChangeReason statChangeReason, GameObjectBase changer,
        StatChangeType changeType = StatChangeType._0023lIc, bool pure = false,
        bool isPermanent = true, _0023z._0023h9b order = _0023z._0023h9b._0023tab,
        bool sendSCT = true, bool includeInScoreboard = true)
    {
        if (entity == null)
        {
            return _00230k._0023tK._0023Eld();
        }

        if (IsDamageBlocked(entity, statType, stackType, value, changer))
        {
            return _00230k._0023tK._0023Eld();
        }

        // 这个实体的属性表里**没有**这条属性 -> 原厂 StatSystem.GetIndex 直接抛 (下标 -1)。
        //
        // 玩家实测 (2026-09-14): "BOSS 突然对着别的一个方向一直前进和攻击……技能不能造成伤害和施加 debuff。"
        // Puller 囤积者的吸血钩舌命中 -> 挂吸血 buff (-Sq/-NVb.cs), buff 初始化里给施法者/目标加
        // MassMultiplier, 两边的属性表都没这一条 -> 抛异常。后果一串:
        //   - buff 初始化断在一半: 定身/控制挂上了, 吸血、拖拽、外力都没挂 (技能"没伤害没 debuff")
        //   - 钩舌判定体的 Update 抛异常 -> 原厂主循环把它标成 IsBroken, 从此不更新也不销毁
        //     (GameManager.Update :1596), 它挂在 BOSS 身上的外力朝向永远不摘 ->
        //     AimDirection 取的就是这条外力 (Character.cs:541), BOSS 从此朝一个方向走、朝一个方向出招
        // 原厂服务端怎么处理这条属性已无从考证; 属性表是客户端同一份, 没有就是没有, 这里当"改不了"跳过。
        if (!entity.Stats._0023fmd(statType))
        {
            MissingStatChanges++;
            if (_missingStatsLogged.Add((entity.GetType(), statType)))
            {
                Serilog.Log.Debug("属性表里没有 {Stat}, 跳过这次变更: 实体 {Entity}, 来源 {Changer}",
                    statType, entity.GetType().Name, changer?.GetType().Name);
            }

            return _00230k._0023tK._0023Eld();
        }

        uint statChangeId = _nextStatChangeId++;
        if (statChangeId == 0)
        {
            // 回绕到 0 会让句柄变成 dummy, 静默失效。四十亿次之后才会发生,
            // 但代价只有一次判断。
            statChangeId = _nextStatChangeId++;
        }

        int changerId = changer?.IndexGlobal ?? -1;
        bool isTranced, isCrit, isPunctured, isBlocked;

        // 第六个参数是 **order**, 不是 changerID —— 判据是 _00233ld 的构造函数
        // 直接把它命名为 order (`-0K/-3ld.cs:5`), 而 _00237zb 拿它做插入排序的
        // 比较键 (`-0K/-89b.cs:262`)。
        //
        // 第一版这里传的是 changer.IndexGlobal, 那是个几百的数字, 会把每次变更
        // 都排到极端优先级上, 堆叠顺序全乱。changerID 只进句柄, 不进这里。
        float permanentChange;

        if (!sendSCT)
        {
            _suppressSCTDepth++;
        }

        try
        {
            permanentChange = entity.Stats._0023aBc(
                statType, statChangeId, stackType, statChangeReason, changeType,
                (int)order, pure, isPermanent, value, changer,
                out isTranced, out isCrit, out isPunctured,
                out isBlocked, out _);
        }
        finally
        {
            if (!sendSCT)
            {
                _suppressSCTDepth--;
            }
        }

        if (includeInScoreboard)
        {
            RecordScoreboardStats(entity, statType, stackType, permanentChange, changer);
        }

        NotifyDamaged(entity, statType, stackType, permanentChange, changer);

        return new _00230k._0023tK(
            statType, entity.IndexEntity, statChangeId, changerId,
            isPermanent, permanentChange,
            isTranced, isCrit, isPunctured, isBlocked);
    }

    /// <summary>
    /// 这一下伤害要不要整个丢掉。
    ///
    /// 目前只有一个用户: Scout 图上那种**单向路障** (只有一面打得动) ——
    /// 见 <c>ScoutStoryDirector.IsDamageBlocked</c>。订阅方在宿主那侧接,
    /// 这里不认识具体规则。
    ///
    /// 挂在 <see cref="ChangeStats"/> 最前面, 因为一旦
    /// <c>Stats._0023aBc</c> 跑过就已经改完了 —— 拦必须在那之前。
    /// </summary>
    public System.Func<Entity, Entity, bool> BlockDamage;

    /// <summary>
    /// 把一次属性变更认成"伤害", 再问 <see cref="BlockDamage"/> 该不该丢。
    ///
    /// 判据和 <see cref="NotifyDamaged"/> 那边同一套, 只差一点: 这里拿到的是
    /// **请求的 value**(还没经过属性系统), 所以用 <c>value &lt; 0</c> 判掉血。
    /// </summary>
    private bool IsDamageBlocked(
        Entity entity, StatType statType, _00230k._0023UIc stackType,
        float value, GameObjectBase changer)
    {
        if (BlockDamage == null || entity == null || changer == null)
        {
            return false;
        }

        if (statType != StatType.Health || value >= 0f
            || stackType != _00230k._0023UIc._0023RIc)
        {
            return false;
        }

        return FindEntityInOwners(changer) is Entity attacker && BlockDamage(entity, attacker);
    }

    /// <summary>
    /// 有东西挨打了 —— 谁挨的、谁打的。
    ///
    /// 用来做"被打就拉仇恨"。挂在这里而不是各个 director 里, 是因为
    /// <see cref="ChangeStats"/> 是所有伤害的**唯一**汇合点: 近战、技能、
    /// 投射物、持续伤害走的都是它。
    ///
    /// 订阅方 (<c>LidgrenHost</c>) 负责转给 <c>ZombieBrain</c>。这里不直接引用
    /// 脑子, 免得模拟层反过来依赖战斗服的编排层。
    ///
    /// 第三个参数是**这一下掉了多少血** (正数) —— 威胁表要按伤害大小加权。
    /// </summary>
    public Action<Entity, Entity, float> Damaged;

    /// <summary>
    /// 从一次属性变更里认出"这是一次伤害", 然后通知 <see cref="Damaged"/>。
    ///
    /// 判据和 <see cref="RecordScoreboardStats"/> 那边同一套:
    ///
    ///   StatType.Health          只有血量算
    ///   permanentChange &lt; 0      掉血才算, 治疗不算
    ///   stackType == _0023RIc    正常的伤害/治疗档 —— 出生拉满血走的是
    ///                            _0023vk, 不排掉的话每只僵尸一出生就"被打"
    ///   changer 顺 Owner 链找人   技能和投射物才是 changer, 出手的人在上游
    /// </summary>
    private void NotifyDamaged(
        Entity entity, StatType statType, _00230k._0023UIc stackType,
        float permanentChange, GameObjectBase changer)
    {
        if (entity == null || changer == null)
        {
            return;
        }

        if (statType != StatType.Health || permanentChange >= 0f
            || stackType != _00230k._0023UIc._0023RIc)
        {
            return;
        }

        if (FindEntityInOwners(changer) is not Entity attacker || attacker == entity)
        {
            return;
        }

        if (attacker is Player player && (entity is Destructible || entity is NPC))
        {
            _lastPlayerHit[entity] = (player, entity.NumberOfGlobalCacheUses);
        }

        Damaged?.Invoke(entity, attacker, -permanentChange);
    }

    /// <summary>
    /// 拾荒里死掉的角色是谁打死的 (死亡那一刻记下)。键带对象池使用次数。
    ///
    /// <see cref="LastPlayerToHit"/> 的记录在 <see cref="CharacterDied"/> 末尾就清掉了 ——
    /// 囤积者 / Looter 导演下一帧才发现它死了, 再去查只能查到 null, 客户端于是总播"敌队打掉了囤积者"。
    /// </summary>
    private readonly Dictionary<Character, (Player Player, int Uses)> _killedBy =
        new Dictionary<Character, (Player, int)>();

    /// <summary>这个角色 (登记时使用次数 <paramref name="uses"/>) 是被哪个玩家打死的; 不知道返回 null。</summary>
    public Player KillerOf(Character character, int uses) =>
        character != null && _killedBy.TryGetValue(character, out (Player Player, int Uses) entry) && entry.Uses == uses
            ? entry.Player
            : null;

    /// <summary>
    /// 最后一击归属的玩家: 顺着 Owner 链找 (子弹、召唤物算主人的); 找不到 (流血、炮塔补刀) 就用最后打过它的玩家。
    /// </summary>
    private Player KillingPlayer(Character died, GameObjectBase killer)
    {
        if (killer != null && FindEntityInOwners(killer) is Player player)
        {
            return player;
        }

        return died != null && _lastPlayerHit.TryGetValue(died, out (Player Player, int Uses) hit)
               && hit.Uses == died.NumberOfGlobalCacheUses
            ? hit.Player
            : null;
    }

    /// <summary>
    /// 可破坏物最后挨了哪个玩家的打。键带着对象池使用次数 —— 回池再取出来的是"另一个"东西。
    /// </summary>
    private readonly Dictionary<Entity, (Player Player, int Uses)> _lastPlayerHit =
        new Dictionary<Entity, (Player, int)>();

    /// <summary>
    /// 这个可破坏物 (茧、路障) 最后是被哪个玩家打的 —— 打碎它的人。没挨过玩家的打返回 null。
    ///
    /// 玩家实测 (2026-09-13): "茧里面救出的幸存者会跟着打破茧的那个玩家走。"
    /// 茧掉到 80% 自己碎 (-EK/-iL.cs:82), 碎之前的最后一下就是打破它的那一下。
    /// </summary>
    /// <param name="uses">登记这个可破坏物时它的 <c>NumberOfGlobalCacheUses</c> —— 对不上说明已经回池换人了。</param>
    public Player LastPlayerToHit(Entity destructible, int uses)
    {
        if (destructible != null
            && _lastPlayerHit.TryGetValue(destructible, out (Player Player, int Uses) hit)
            && hit.Uses == uses)
        {
            return hit.Player;
        }

        return null;
    }

    /// <summary>
    /// 把这次血量变化记到计分板上 —— 掉血记"造成伤害", 加血记"治疗"。
    ///
    /// 玩家实测: "计分板上现在有等级和击杀僵尸数目, 但是没有造成伤害和治疗的
    /// 数据。"
    ///
    /// ## 又是"方法完整但零调用方"
    ///
    /// 计分板读的是 <c>PlayerScoreStats.Damage</c> / <c>.Healing</c>, 两个都是
    /// 只读属性, 把各自的分类数组求和 (PlayerScoreStats.cs:61/74)。
    /// 往里加的是 <c>AddDamage</c> (:199) 和 <c>_0023Zcj</c> (:204)。
    ///
    ///   AddDamage   全代码树**零调用者**
    ///   _0023Zcj    只有护盾那条路上有 (Character.cs:4273/4277), 也就是说
    ///               **真正的加血一次都没被记过**
    ///
    /// 我一开始以为治疗那半是好的, 看清那两处都在 <c>_ShieldList</c> 分支里
    /// 才发现不是 —— 普通治疗和护盾是两条路。
    ///
    /// ## 为什么挂在 ChangeStats 上
    ///
    /// <c>includeInScoreboard</c> 这个参数一直在签名里 —— 原厂就是拿它区分
    /// "这次变更算不算进计分板"的 (升级补血那种账面调整传 false, 见上面
    /// <c>sendSCT</c> 那段)。参数在、语义清楚、只是没人消费它。
    /// </summary>
    private void RecordScoreboardStats(
        Entity entity, StatType statType, _00230k._0023UIc stackType,
        float permanentChange, GameObjectBase changer)
    {
        if (statType != StatType.Health || permanentChange == 0f || changer == null)
        {
            return;
        }

        // ## 只认正常的伤害/治疗那一档堆叠类型
        //
        // 出生那一下把血拉满走的是 <c>_0023vk</c> ("直接设值",
        // Character.cs:2512 `ChangeStats(this, Health, _0023vk, MaxHealth, ...)`),
        // 不加这个判据的话每个人一出生就先记 200 点治疗 —— 测试里当场撞到。
        //
        // 真正的伤害和治疗走的都是 <c>_0023RIc</c>。
        if (stackType != _00230k._0023UIc._0023RIc)
        {
            return;
        }

        // 出手的是谁 —— changer 常常是技能/投射物而不是人本身,
        // FindEntityInOwners 顺着 Owner 链往上找, 那是原厂在别处认归属
        // 用的同一个方法 (Character.cs:4290 发飘字时就这么找)。
        if (FindEntityInOwners(changer) is not Player actor)
        {
            return;
        }

        int amount = (int)System.Math.Abs(System.Math.Ceiling(permanentChange));
        if (amount <= 0)
        {
            return;
        }

        if (permanentChange > 0f)
        {
            // 治疗。自愈也算 —— 原厂护盾那条路同样不排除自己 (Character.cs:4268
            // 只判 player != null)。
            //
            // 分类给"技能治疗"那一档: 我们这条路上的加血基本都来自技能或 buff,
            // 而计分板显示的总额 (Healing 属性) 是各档求和, 分档只影响细分面板。
            actor.PlayerScoreStats._0023Zcj(amount, _00230k._0023Ocj._0023B8b);
            return;
        }

        // 伤害。自伤不算 —— 反伤/毒圈打到自己不该算进"造成伤害"。
        if (ReferenceEquals(entity, actor))
        {
            return;
        }

        // ## 路障整组共享血量, 一击会记三次
        //
        // 打路障组里的一段, 引擎会把这次变更传导到同组另外两段, 每段各走一次
        // ChangeStats —— 伤害记成三倍 (测试实测 25 -> 75)。
        //
        // 三次的 (出手人, 数值, 游戏时刻) 完全相同, 而且发生在同一个调用栈里,
        // 所以按这三样去重就够了: 同一时刻同一个人对**路障**打出同样的数,
        // 只认第一次。
        //
        // 只对路障去重, 不动别的目标 —— 群体技能同帧打中两只血量相同的僵尸
        // 是正常的两份伤害, 不该被吃掉。
        if (entity is _0023EK._0023Yh)
        {
            var key = (actor.IndexGlobal, amount, Time);

            if (_lastBarricadeHit == key)
            {
                return;
            }

            _lastBarricadeHit = key;
        }

        actor.PlayerScoreStats.AddDamage(amount, _00230k._0023Ncj._0023sg);
    }

    /// <summary>
    /// 上一次记进计分板的路障伤害 —— 用来吃掉同组传导那两次。
    ///
    /// 见 <see cref="RecordScoreboardStats"/> 里那段说明。只在模拟线程上读写。
    /// </summary>
    private (int Attacker, int Amount, float Time) _lastBarricadeHit = (-1, 0, -1f);

    /// <summary>
    /// 抗性变更用的 List 池。
    ///
    /// <c>_0023rac.Remove()</c> 在撤销后会把那个 List **还回** <c>_0023qac</c>
    /// (`-0k/-rac.cs:26`), 而它是 <c>Stack&lt;List&lt;...&gt;&gt;</c> 类型, 为 null 会
    /// 直接 NRE。原厂那个 <c>ResistanceStatChangeListPool</c> 是 <c>ListPool</c>,
    /// 内部的 Stack 是 private 拿不到 —— 所以自备一个。
    ///
    /// 只有 <see cref="ChangeResistanceStats"/> 一条路进出, 全在模拟线程上。
    /// </summary>
    private readonly Stack<List<SimpleStatChangeInternal<ResistanceStatTypes>>>
        _resistanceChangeLists = new();

    /// <summary>
    /// <c>ResistanceStatTypes</c> 里真实抗性类型的个数。
    ///
    /// 枚举的最后一项 <c>_0023o2b</c> = 0x20000 是**哨兵**而不是类型 ——
    /// 原厂用 <c>_0023Rb(_0023o2b)</c> 求它的位序 (17) 当作容器长度
    /// (`-0k/-mac.cs:108`)。所以有效类型是 bit 0..16。
    /// </summary>
    private const int ResistanceStatTypeCount = 17;

    /// <summary>
    /// 临时改一个实体的抗性。
    ///
    /// ## 这一半是原厂现成的, 缺的只有入口
    ///
    /// 容器 <c>_0023mac</c> (`-0k/-mac.cs`) 是**完整实现**, 每个实体的抗性数值
    /// 也都在原厂初始化代码里 (玩家 <c>Player.cs:416</c> 逐类型 3, 教程 NPC
    /// <c>-Mk/-YO.cs:81</c> 常量 3, 僵尸各类 2~6)。丢的只有 GameManager 上这个
    /// 方法 —— 基类 <c>return default;</c> (GameManager.cs:1136)。
    ///
    /// 后果: 一切"技能期间免疫某类控制"的原厂设计都不生效。例如
    /// <c>-EF/-4wb.cs:152</c> 给 Owner 上 5 点击退抗性, <c>-EF/-1H.cs:59</c>
    /// 上 4 点(眩晕|恐惧)抗性 —— 全都静默失效, 交了技能照样被控。
    ///
    /// ## statType 是位掩码, 一次可以改好几类
    ///
    /// 调用方常传组合值, 例如
    /// <c>_0023sab | _0023s9b | _0023x9b</c> (`-EF/-1yb.cs:86`)。每一位对应容器里
    /// 一个独立的 <c>_0023Sac</c>, 都要挂上同一次变更, 于是句柄里是一个 List。
    ///
    /// ## AllValuesConst 的实体改不动 —— 这是原厂设计, 不是缺陷
    ///
    /// 容器的索引器在 <c>AllValuesConst || IsDummy</c> 时返回一个 **static 共享**
    /// 的常量 stat (`-0k/-mac.cs:87` 的 <c>_0023W9b</c>)。往它上面挂变更会污染
    /// 场上**所有**同样状态的实体, 所以这里直接不做。
    ///
    /// 语义上也对得上: <c>AllValuesConst</c> 就是"这个实体的抗性是个不可变常量"。
    /// 僵尸和教程 NPC 属于这一类; 玩家走 <c>_0023eac(false, 3)</c>, 逐类型可变 ——
    /// 而调本方法的本来就全是玩家技能。
    ///
    /// ## 不走对象池
    ///
    /// <c>SimpleStatChangeInternal</c> 的构造函数允许池为 null, <c>Remove</c> 里
    /// 判了 (`SimpleStatChangeInternal.cs:148`)。抗性变更只在施放技能时发生,
    /// 频率远低于伤害, 省掉一层池子换取"少一个能出错的地方"。
    /// </summary>
    public override _00230k._0023rac<ResistanceStatTypes> ChangeResistanceStats(
        Entity entity, float value, GameObjectBase changer,
        ResistanceStatTypes statType,
        _00230k._0023UIc statStackType = _00230k._0023UIc._0023vk)
    {
        _00230k._0023mac container = entity?.ResistanceStats;
        if (container == null || container.IsDummy || container.AllValuesConst)
        {
            return default;
        }

        List<SimpleStatChangeInternal<ResistanceStatTypes>> changes = null;

        for (int bit = 0; bit < ResistanceStatTypeCount; bit++)
        {
            ResistanceStatTypes one = (ResistanceStatTypes)(1 << bit);
            if ((statType & one) != one)
            {
                continue;
            }

            _00230k._0023Sac<ResistanceStatTypes> stat = container[one];
            if (stat == null)
            {
                continue;
            }

            SimpleStatChangeInternal<ResistanceStatTypes> change = new(null);
            change.Initialize(stat, statStackType, value, changer);
            stat._00237zb(change);

            changes ??= _resistanceChangeLists.Count > 0
                ? _resistanceChangeLists.Pop()
                : new List<SimpleStatChangeInternal<ResistanceStatTypes>>();
            changes.Add(change);
        }

        if (changes == null)
        {
            return default;
        }

        ResistanceChanges++;
        return new _00230k._0023rac<ResistanceStatTypes>
        {
            _0023BI = changes,
            _0023qac = _resistanceChangeLists,
        };
    }

    /// <summary>
    /// 成功挂上的抗性变更数。诊断用。
    /// </summary>
    public int ResistanceChanges;

    /// <summary>
    /// "这个目标抗住了某类控制"的监听器。
    ///
    /// 原厂 <c>AddResistedBuffListener</c> 是空桩 (GameManager.cs:2671), 返回一个
    /// 共享 dummy (<c>_DummyResBuffListener</c>, :187) —— 注册进去石沉大海。
    /// </summary>
    private readonly List<ResistedBuffListener> _resistedBuffListeners = new();

    /// <summary>
    /// 注册"被抗住"的回调。
    ///
    /// ## 和 <see cref="ChangeResistanceStats"/> 是一对, 少一个就半拉子
    ///
    /// 原厂调用点只有一处, 而它把两件事连着做 (<c>-EF/-dbb.cs:130-131</c>,
    /// Charlie 的 Blitz):
    ///
    ///     ChangeResistanceStats(Target, 5f, this, 眩晕|恐惧|缴械|定身);
    ///     AddResistedBuffListener(Target, 同样那四类, 回调);
    ///
    /// 语义是"我给你上抗性, 而且**当它真的挡下一次控制时告诉我**" —— 技能靠
    /// 这个回调播破控特效 / 结束冲锋。上一轮只补了前半句, 于是抗性生效了、
    /// 通知没有, 比两个都不做更难查。
    ///
    /// ## 触发点在 ApplyBuff 里
    ///
    /// 判据是 <see cref="ResistsByStats"/> 返回 true —— 那正是"抗性挡下了一个
    /// buff"的定义。近战击退那条路 (<see cref="ResistsMeleeKnockback"/>)
    /// **不触发**: 它压根不经抗性, 通知一个抗性监听器没有道理。
    ///
    /// ## 生命周期
    ///
    /// <c>ResistedBuffListener.Remove()</c> 只是置 <c>ShouldBeRemoved</c>
    /// (它没有指回容器的引用), 真正摘除得由持有列表的一方做 —— 所以在每次
    /// 派发时顺手清掉已标记的。目标失效 (<c>_0023nk</c> 死了或回池) 也一并清,
    /// 否则一局下来会攒住一堆过期实体。
    /// </summary>
    public override ResistedBuffListener AddResistedBuffListener(
        Entity entity, ResistanceStatTypes resStats, Action<ResistanceStatTypes> listenerFuncton)
    {
        if (entity == null || listenerFuncton == null || resStats == ResistanceStatTypes._0023Yc)
        {
            return base.AddResistedBuffListener(entity, resStats, listenerFuncton);
        }

        ResistedBuffListener listener = new ResistedBuffListener
        {
            _0023nk = entity,
            _0023K9b = resStats,
            _0023L9b = listenerFuncton,
        };

        _resistedBuffListeners.Add(listener);
        return listener;
    }

    /// <summary>
    /// 抗性挡下了一个 buff —— 通知关心这一类的监听器。
    /// 顺手清掉已注销的和目标已失效的。
    /// </summary>
    private void NotifyBuffResisted(Entity target, ResistanceStatTypes type)
    {
        for (int i = _resistedBuffListeners.Count - 1; i >= 0; i--)
        {
            ResistedBuffListener listener = _resistedBuffListeners[i];

            if (listener.ShouldBeRemoved || listener._0023nk == null
                || !listener._0023nk.IsActive)
            {
                _resistedBuffListeners.RemoveAt(i);
                continue;
            }

            if (!ReferenceEquals(listener._0023nk, target))
            {
                continue;
            }

            // 位掩码取交集 —— 注册时给的是一组类型, 挡下的是其中一类
            if ((listener._0023K9b & type) == 0)
            {
                continue;
            }

            listener._0023L9b(type);
            ResistNotifications++;
        }
    }

    /// <summary>派发出去的"被抗住"通知数。诊断用。</summary>
    public int ResistNotifications;

    /// <summary>
    /// 角色受到伤害时的回调。目前只用于诊断。
    ///
    /// ## 为什么值得接上
    ///
    /// 真机上玩家的血在掉, 但看不出是谁打的 —— 僵尸不播动画也不移动
    /// (<c>ConnectToAIUnit</c> 还是空的), 所以现象很像"自己空击扣自己血"。
    ///
    /// 这个钩子带着 <c>_0023Nh.Changer</c> 和 <c>Target</c>, 一行日志就能把
    /// "僵尸在打你" 和 "你在打自己" 分开。基类是空 virtual (GameManager.cs:3095),
    /// 原本是服务端用来发战斗日志和记分板的。
    ///
    /// 只记扣血 (负变化), 且限流到每秒一条 —— 19 只僵尸围着打的话, 不限流
    /// 会把日志淹掉。
    /// </summary>
    public override void OnCharacterDamageTaken(Character character, _00230k._0023Nh statChangeData)
    {
        // 战斗飘字和击中特效。放在最前面且不受下面的日志限流影响 ——
        // 限流是为了日志好读, 而玩家的每一次打击都该有反馈。
        //
        // 但**账面调整不飘字**: 调用方传 `sendSCT: false`, 或者原因是零值
        // (装备/重算那一类) 时这里要闭嘴。判据见 _suppressSCTDepth 和
        // IsBookkeeping 上面那两段说明。
        DamageHookCalls++;

        if (_suppressSCTDepth == 0 && !IsBookkeeping(statChangeData))
        {
            CombatTextSender.SendDamage(this, _SCTSynchronizer, character, statChangeData);
        }
        else
        {
            SuppressedSCTCount++;
        }

        LogPlayerHealthChange(character, statChangeData);

        if (character == null || statChangeData._00237zb >= 0f)
        {
            return;
        }

        // ⚠️ 血量要读 statChangeData.NewValue, **不能读 character.Health**。
        //
        // 这个回调是在属性变更**落地之前**跑的, 所以 character.Health 还是旧值。
        // _0023Nh.NewValue 才是变更后的值 (-0K/-Nh.cs:199):
        //
        //     public float NewValue => Clamp(OriginalValue + _00237zb, Min, Max);
        //
        // 先前这里读的是 character.Health, 于是日志整体**滞后一击** ——
        // 第一击打完显示的是满血。我因此把"血量滞后"误判成同步问题, 绕了一圈。
        // 一条读错时机的日志比没有日志更糟。
        float healthAfter = statChangeData.NewValue;

        // 血量低于三成时不限流 —— 那正是"最后一击打不死"要看的区间。
        bool nearDeath = character.MaxHealth > 0f && healthAfter <= character.MaxHealth * 0.3f;

        double now = _damageLogClock.Elapsed.TotalSeconds;
        if (!nearDeath && now - _lastDamageLog < 1.0)
        {
            return;
        }

        _lastDamageLog = now;

        GameObjectBase changer = statChangeData.Changer;
        string attacker = changer == null
            ? "(无)"
            : $"{changer.GetType().Name}#{changer.IndexGlobal}";
        bool selfInflicted = ReferenceEquals(changer, character);

        // Debug 级: 伤害管线的排查已经收工 (击中特效、经验结算、最后一击掉血
        // 全部定位并修好)。留着代码是因为下次碰伤害问题还要用, 但常驻 Information
        // 会让每局多出上千行 —— 真正在找问题时把日志级别调到 Debug 即可。
        Serilog.Log.Debug(
            "伤害: {Victim}#{VictimIdx} 掉 {Amount:F1} -> HP {HP:F1}/{MaxHP:F1} 死亡={Dead} 激活={Active} | " +
            "来源={Attacker} 自伤={Self} 原因={Reason}",
            character.GetType().Name, character.IndexGlobal,
            -statChangeData._00237zb, healthAfter, character.MaxHealth,
            character.IsDead, character.IsActive,
            attacker, selfInflicted, statChangeData.ChangeReason);

        // 击中特效的三种成因诊断 —— 见 CombatTextSender 里那两个字段的说明。
        // 客户端要求 ownerChanger 必须等于本地玩家, 否则伤害数字照出但特效不播。
        Serilog.Log.Debug("  击中特效判据: ownerChanger 为空={Null} 是玩家={IsPlayer}",
            CombatTextSender.LastOwnerChangerWasNull, CombatTextSender.LastOwnerChangerWasPlayer);
    }

    private readonly System.Diagnostics.Stopwatch _damageLogClock =
        System.Diagnostics.Stopwatch.StartNew();
    private double _lastDamageLog;

    // ---------------------------------------------------------------------
    // 战斗飘字 (SCT) —— 全部只是把数据丢进现成的 SCTSynchronizer。
    //
    // 基类那两个 SendSCT (GameManager.cs:2889/:2893) 和下面这一排 SendXxxText
    // 都是空 virtual。同步器本身、它的注册、每帧 Update、客户端的接收端
    // (GameManagerClient.GotSCT:1065) **全是现成的** —— 唯一断掉的就是这层转发。
    //
    // 断掉的表现: 伤害数字、暴击、闪避、免疫、升级提示、击中特效全部无声消失,
    // 零报错。详见 CombatTextSender 的说明。
    // ---------------------------------------------------------------------

    public override void SendSCT(IClient client, Entity entity, Entity ownerChanger,
                                 GameObjectBase gameObjectChanger, SCTData data)
    {
        if (client == null || entity == null)
        {
            return;
        }

        _SCTSynchronizer.SendSCT(client, entity, ownerChanger, gameObjectChanger, data);
    }

    public override void SendSCT(Player player, Entity entity, Entity owner,
                                 GameObjectBase changer, SCTData data)
        => SendSCT(player?.Client, entity, owner, changer, data);

    public override void SendMissText(Player player, Entity target, Entity owner, GameObjectBase changer)
        => Flag(player, target, owner, changer, CombatTextSender.SCTFlag.Miss);

    public override void SendEvadeText(Player player, Entity target, Entity owner, GameObjectBase changer)
        => Flag(player, target, owner, changer, CombatTextSender.SCTFlag.Evade);

    public override void SendImmuneText(Player player, Entity target, Entity owner, GameObjectBase changer)
        => Flag(player, target, owner, changer, CombatTextSender.SCTFlag.Immune);

    public override void SendAbsorbText(Player player, Entity target, Entity owner, GameObjectBase changer)
        => Flag(player, target, owner, changer, CombatTextSender.SCTFlag.Absorb);

    public override void SendInterruptText(Player player, Entity target, Entity owner, GameObjectBase changer)
        => Flag(player, target, owner, changer, CombatTextSender.SCTFlag.Interrupt);

    public override void SendLevelUpText(Player player, Entity target, Entity owner, GameObjectBase changer)
        => Flag(player, target, owner, changer, CombatTextSender.SCTFlag.LevelUp);

    public override void SendSupplyText(Player player, Entity target, Entity owner,
                                        GameObjectBase changer, byte value)
        => Flag(player, target, owner, changer, CombatTextSender.SCTFlag.Supply, value);

    public override void SendCombatText(Player player, Entity target, Entity owner,
                                        GameObjectBase changer, byte combat)
        => Flag(player, target, owner, changer, CombatTextSender.SCTFlag.Combat, combat);

    private void Flag(Player player, Entity target, Entity owner, GameObjectBase changer,
                      CombatTextSender.SCTFlag flag, byte value = 0)
        => CombatTextSender.SendFlag(_SCTSynchronizer, player, target, owner, changer, flag, value);

    /// <summary>
    /// 测试用: 基类那个字段是 protected, 而测试要判断飘字有没有真的进队列。
    ///
    /// 判据用 <c>ShouldBeSynchronized</c> —— 网络层自己就是拿它决定这一帧要不要
    /// 写 SCT 段的, 比在测试里解析位流可靠得多。
    /// </summary>
    /// <summary>
    /// 位移类效果 (击退、击飞、拉拽、冲锋) —— 把一条外力挂到实体的力向量上。
    ///
    /// ## 这是"打不出击退"的原因
    ///
    /// <c>Character.ChangeForceDirection</c> (Character.cs:1327) 是完整的, 它把
    /// 活交给本方法。而基类是
    ///
    ///     public virtual _0023H6b GetVector2Change(...) { return _0023Ifd; }
    ///
    /// —— <c>_0023Ifd</c> 是 <c>new _0023H6b(null)</c> (GameManager.cs:191), 一个
    /// **空壳句柄**。调用方拿到它, 往它身上写值全部落空 (<c>_0023H6b.Value</c>
    /// 的 setter 判了 <c>_0023E6b != null</c>), 于是所有位移效果静默失效。
    ///
    /// 影响面比"击退"大: 普攻击退、Ram 冲锋、Puller 钩爪、爆炸击飞、玩家翻滚
    /// 的位移全走这一条。
    ///
    /// ## 为什么是优先级列表而不是简单相加
    ///
    /// <c>PrioritizedVector2</c> (:70) 排序后取**第一个非零**的值, 不是求和。
    /// 也就是说同时受多个位移影响时, 高优先级的那个独占 —— 被钩爪拉住的时候
    /// 普通击退不该把人推开。这是原厂的设计, 我们只是把入口接回去。
    ///
    /// ## 字段赋值必须在 Initialize 之后
    ///
    /// <c>_0023P6b.Initialize</c> (-z/-P6b.cs:73) 结尾会把 owner/changer/debugID
    /// 全部清成 null/-1。先赋值再 Initialize 的话会被它抹掉 —— 那三个字段只在
    /// <c>PrioritizedVector2.Reset</c> 的告警里用到, 抹掉了不影响功能, 但排查
    /// "谁留下了没清的位移"时就没线索了。
    ///
    /// ## 没有用对象池
    ///
    /// 原厂给 <c>_0023P6b</c> 配了池 (构造函数第一个参数), 我们传 null ——
    /// <c>Remove()</c> 里对它判了空, 所以功能完全正常, 只是每次位移多一次分配。
    /// 位移不是每帧发生的事件, 先按简单的来; 真成为热点再补池。
    /// </summary>
    public override _0023z._0023H6b GetVector2Change(
        PrioritizedVector2 prioritizedVector2, FarseerPhysics.Common.Vector2 direction,
        GameObjectBase owner, GameObjectBase changer, int debugChangerID, _0023z._0023Q6b priority)
    {
        if (prioritizedVector2 == null)
        {
            return base.GetVector2Change(prioritizedVector2, direction, owner, changer,
                                         debugChangerID, priority);
        }

        _0023z._0023P6b change = new _0023z._0023P6b(null);
        change.Initialize(prioritizedVector2, direction, priority);

        // 诊断信息, 必须在 Initialize 之后 —— 它会把这三个清掉
        change._0023M6b = owner;
        change._0023L6b = changer;
        change._0023px = debugChangerID;

        prioritizedVector2.Apply(change);
        return new _0023z._0023H6b(change);
    }

    /// <summary>
    /// 把一个 buff 挂到目标身上。
    ///
    /// ## 为什么服务端必须自己做
    ///
    /// <c>Buff.SpawnGameObject</c> (Buff.cs:225) 里那句是关键:
    ///
    ///     if (!base.GameManager.IsServer &amp;&amp; !Target.HasBuff(_0023iq))
    ///         Target.AddBuff(this);
    ///
    /// **只在客户端** AddBuff —— 服务端那一侧由本方法负责, 而基类是
    /// <c>return _00236cj._00231cj;</c>, 什么都不做。
    ///
    /// 后果: 服务端上没有任何 buff 生效。教程里 Walker 出生自带的那个 buff
    /// (-rPb.cs 的 _00238N 在教程模式下 ApplyBuff) 、技能的增益减益、
    /// 中毒灼烧、护盾, 全部静默失效。
    ///
    /// ## 顺序
    ///
    /// Target 要在 <c>Spawn</c> **之前**设 —— <c>SpawnGameObject</c> 里读
    /// <c>Target.Position</c> 存初始位置 (Buff.cs:237), 而且 <c>UpdateGameObject</c>
    /// (:248) 每帧都解引用 Target, 为 null 会直接 NRE。
    ///
    /// ## 简化之处 (没有假装做全)
    ///
    /// 原厂对"目标已有同类 buff"应该有一套叠加/刷新规则 ——
    /// <c>Buff._0023Eq(Buff)</c> (:216) 就是那个合并入口, <c>Stacks</c>、
    /// <c>_0023sq</c> (上限?) 也都留着。但规则本身随服务端丢失, 从字段名推不出来。
    ///
    /// 现在的做法是直接挂上去, 同类 buff 会并存。<c>AddBuff</c> 自己判了重复引用
    /// (Entity.cs:783), 所以不会重复添加**同一个实例**, 但两次施放会产生两个实例。
    /// 表现上是效果叠加而不是刷新时长 —— 等真机上看到不对再按观察修。
    ///
    /// 冷却是有的: <c>AddBuff</c> 会调 <c>SetBuffFullCooldown</c>,
    /// <c>HasBuffCooldown</c> (:816) 现成, 施法侧自己会查。
    ///
    /// ## 返回值
    ///
    /// <c>_00236cj</c> 六个枚举值**没有任何消费者** —— 全代码树搜下来只有基类
    /// 返回 <c>_00231cj</c>。语义无从考证, 所以这里只区分"挂上了"和"没挂上",
    /// 用 0 号值表示成功。改它不影响任何现有逻辑。
    /// </summary>
    public override _0023Oq._00236cj ApplyBuff(Buff buff, Entity target, Entity owner,
                                               GameObjectBase spawner)
    {
        if (buff == null || target == null)
        {
            ReturnUnappliedBuff(buff);
            return base.ApplyBuff(buff, target, owner, spawner);
        }

        // 池空时原厂 GetGameObjectFromPool 返回**全类型共用的 dummy** (GameManager.cs:1970)。
        // 挂上去它不 Spawn、不 Update、永远不摘 —— 目标从此"永远挂着"这个 buff, 还和别的目标共用。
        // 玩家实测 (2026-09-14): Isys 的 Q (SentryGun, InterruptOnKnockback) 一开始读条就断 ——
        // 集束手雷/地雷一炸几十只僵尸, 击退 buff 池 (40) 被拿空, dummy 击退挂在了玩家身上。
        if (buff.IsDummyObject)
        {
            DummyBuffsDropped++;
            return base.ApplyBuff(buff, target, owner, spawner);
        }

        // 抗住了就不挂。两道判据互相独立, 都要过:
        //   ResistsByStats           原厂抗性系统 (强度 vs 抗性值)
        //   ResistsMeleeKnockback    近战击退, 走的是另一条原厂路
        //
        // 只有前者派发"被抗住"通知 —— 那个监听器问的是"我给的抗性挡住东西了吗",
        // 而近战击退压根不经抗性 (见 ResistsMeleeKnockback 的说明)。
        if (ResistsByStats(buff, target))
        {
            ResistedBuffs++;
            NotifyBuffResisted(target, buff._0023rq);
            ReturnUnappliedBuff(buff);
            return base.ApplyBuff(buff, target, owner, spawner);
        }

        if (ResistsMeleeKnockback(buff, target, spawner, owner))
        {
            ResistedBuffs++;
            ReturnUnappliedBuff(buff);
            return base.ApplyBuff(buff, target, owner, spawner);
        }

        if (DeflectedByDesign(buff, target))
        {
            ResistedBuffs++;
            ReturnUnappliedBuff(buff);
            return base.ApplyBuff(buff, target, owner, spawner);
        }

        MergeStacks(buff, target);

        buff.Target = target;
        target.AddBuff(buff);
        SpawnGameObject(buff, owner, spawner);
        ShortenByDesigns(buff, target);

        LogCrowdControl(buff, target);

        // 专属控制 buff (Puller 钩舌 / 屠夫扑倒) 客户端画不出状态条, 挂一个通用镜像。
        // 见 CrowdControlMirror。
        CrowdControl.OnApplied(this, buff, target);

        return _0023Oq._00236cj._0023Wc;
    }

    // ---- 抗控制改装: 原厂代码里一处引用都没有 (服务端那半丢了), 按描述补 ----
    //
    // 玩家要求审核改装是否按描述生效 (2026-09-15)。这四个 UpgradeType 在全树**零引用**, 装了什么都不发生:
    //   Titanium Deflector  "有几率抵抗眩晕、失去行动能力和沉默"
    //   Stun Resister       "缩短眩晕和失去行动能力的持续时间"
    //   Mind Resister       "缩短恐惧、Panic Shot 和嘲讽的持续时间"   (Panic Shot 挂的就是恐惧类; 嘲讽没有独立类别, 管不到)
    //   Slow Resister       "缩短减速和定身的持续时间"
    // 数值 (几率 / 缩短比例) 用改装自带的 AttackBonusValue (0.25 / 0.4)。类别标志按各通用 buff 的提示键认的:
    //   _0023sab 眩晕 (Buffbar.Stun)  _0023zBc 失去行动能力  _0023q9b 沉默  _0023tZb 恐惧  _0023Qcb 减速  _0023GZb 定身

    private const _0023Oq._0023fGc StunLike = _0023Oq._0023fGc._0023sab | _0023Oq._0023fGc._0023zBc;
    private const _0023Oq._0023fGc SlowLike = _0023Oq._0023fGc._0023Qcb | _0023Oq._0023fGc._0023GZb;

    /// <summary>Titanium Deflector: 几率整个抵抗掉眩晕 / 失去行动能力 / 沉默。</summary>
    private bool DeflectedByDesign(Buff buff, Entity target)
    {
        if (!buff.IsDebuff || (buff._0023hq & (StunLike | _0023Oq._0023fGc._0023q9b)) == 0)
        {
            return false;
        }

        ConductorGameLogic.Upgrade deflector = _0023Oq._0023LZb._0023KZb(target, UpgradeType.WeaponUpgrade_TitaniumDeflector);
        return deflector.Exists && _0023z._00235Cc._0023hRf(Random, 0f, 1f) < deflector.Value;
    }

    /// <summary>Stun / Mind / Slow Resister: 按比例缩短持续时间。生成之后再缩 —— 不少 buff 在生成时才定时长。</summary>
    private void ShortenByDesigns(Buff buff, Entity target)
    {
        if (!buff.IsDebuff || buff._0023fq <= 0f || buff._0023fq >= 9000f)
        {
            return;
        }

        float factor = 1f;
        void Apply(_0023Oq._0023fGc flags, UpgradeType design)
        {
            if ((buff._0023hq & flags) != 0)
            {
                ConductorGameLogic.Upgrade upgrade = _0023Oq._0023LZb._0023KZb(target, design);
                if (upgrade.Exists)
                {
                    factor *= 1f - Math.Clamp(upgrade.Value, 0f, 1f);
                }
            }
        }

        Apply(StunLike, UpgradeType.WeaponUpgrade_StunResister);
        Apply(_0023Oq._0023fGc._0023tZb, UpgradeType.WeaponUpgrade_MindResister);
        Apply(SlowLike, UpgradeType.WeaponUpgrade_SlowResister);
        if (factor < 1f)
        {
            buff._0023fq *= factor;
            ShortenedBuffs++;
        }
    }

    /// <summary>诊断: 被抗控制改装缩短的 debuff 数。</summary>
    public int ShortenedBuffs;

    /// <summary>
    /// 可叠层的 buff 再挂一次 = 叠一层: 新的接过旧的层数 +1 (不超过上限), 旧的摘掉。
    ///
    /// ## 为什么要有
    ///
    /// 原厂"目标已有同类 buff"的合并规则随服务端丢失 (<c>Buff._0023Eq</c> 是空入口, 全树没有实现),
    /// 我们以前直接挂上去, 同类 buff 并存。玩家要求审核改装是否生效 (2026-09-15), 全改装探针查出:
    /// 8 个 Boost 改装 (每级 +属性) 的写法是每帧 `if (buff.Stacks != 等级) 再挂一个` (Player.cs:1300 起) ——
    /// 层数永远是 1, 于是**每帧多挂一个**, 几秒后属性变更表溢出, 玩家自己的 Update 抛异常被主循环标坏
    /// (人物卡死)。Charge 改装 (击杀叠属性) 同理叠不上去, 永远只有一层。
    ///
    /// ## 规则从哪来的
    ///
    /// 叠层 buff 都在**生成时**按 <c>Stacks</c> 算效果 (例 -Sq/-5nb.cs: 属性 = 每层值 × Stacks, 满层放特效),
    /// 上限在 <c>_0023qq</c> (Boost 15、Charge 读平衡表)。所以"再挂一次"只能是新实例带着累计层数生效、旧的退场。
    /// 只处理上限 &gt; 1 的; 不叠层的 (控制、击退等) 维持原来的行为, 免得牵连已经调好的手感。
    /// 同一个 buff 标识不分来源合并, 这点是**我们定的**。
    /// </summary>
    private void MergeStacks(Buff buff, Entity target)
    {
        if (buff._0023qq <= 1 || buff._0023iq == _0023Oq._0023aD._0023Yc)
        {
            return;
        }

        foreach (Buff existing in target.BuffList)
        {
            if (existing != buff && existing.IsActive && !existing.IsBeingDeactivated && existing._0023iq == buff._0023iq)
            {
                buff.Stacks = existing.Stacks + 1;
                existing.Destroy();
                StackedBuffs++;
                return;
            }
        }
    }

    /// <summary>诊断: 叠层合并次数。</summary>
    public int StackedBuffs;

    /// <summary>
    /// 没挂上的 buff 还回对象池。
    ///
    /// 调用方都是"从池里取一个 -> ApplyBuff", 挂不上时谁都不还 —— 不还就是**永久少一个**。
    /// 实测 (2026-09-15): Berg (变异) 的 Bulwark 给自己加抗性, 脱战回血 buff (Character.cs:2690)
    /// 每帧取一个被抗住一个, 12 个的池一秒多就空, 之后全场取到的都是 dummy。
    /// 被抗住的击退 buff 也一样在漏 (僵尸抗性 6、玩家免疫近战击退), 击退池 40 个就是这么慢慢见底的。
    /// 取出时原厂会 Reset (GameManager.cs:2029), 还回去再取是干净的。
    /// </summary>
    private void ReturnUnappliedBuff(Buff buff)
    {
        if (buff != null && !buff.IsDummyObject && !buff.IsActive && !buff.IsInPool)
        {
            UnappliedBuffsReturned++;
            ReturnGameObjectToPool(buff);
        }
    }

    /// <summary>诊断: 没挂上、还回池里的 buff 数。</summary>
    public int UnappliedBuffsReturned;

    /// <summary>
    /// 控制状态条的镜像 —— 宿主每帧调 <see cref="CrowdControlMirror.Update"/>。
    /// </summary>
    public CrowdControlMirror CrowdControl { get; } = new CrowdControlMirror();

    /// <summary>
    /// 因为目标抗住了而没挂上的 buff 数。诊断用。
    /// </summary>
    public int ResistedBuffs;

    /// <summary>池空拿到 dummy、没挂上的 buff 数。诊断用 (见 <see cref="ApplyBuff"/>)。</summary>
    public int DummyBuffsDropped;

    /// <summary>
    /// 玩家头顶那条"状态名 + 剩余时间"的控制条, 客户端**只为这七类 buff** 画
    /// (<c>UI_CharacterHUDsBinding.cs:402-439</c> 逐条抄下来的):
    ///
    ///   <c>_0023PDc</c> 被控制   <c>_0023sab</c> 眩晕   <c>_0023p9b</c> 技能封禁
    ///   <c>_0023q9b</c> 沉默     <c>_0023tZb</c> 恐惧   <c>_0023zBc</c> 失去行动能力
    ///   <c>_0023GZb</c> 定身
    ///
    /// **击退 (<c>_0023nE</c>) 不在其中** —— 原版就不给击退画条。这一点很要紧:
    /// 屠夫横扫 / 跳劈、公羊跺地、飘浮者冲撞挂的都是击退, 所以被它们打翻在地
    /// **本来就没有状态条**, 那不是我们的 bug。会画条的只有公羊冲撞 (眩晕 1 秒,
    /// -EF/-r3b.cs:203) 和尖叫者的恐惧 (-EF/-W2b.cs, 但尖叫者在 Scout 图刷不出来)。
    ///
    /// 玩家 2026-09-12 报"被控制时头上没有状态名和时间条"。客户端那一半是完整的、
    /// 数据全靠 buff 实例自己 (名字查本地化表, 时长读 <c>_0023fq</c>, 进度用 <c>Age</c>),
    /// 服务端能做的只有"确实挂上了一个会画条的 buff 并且带着时长"。所以这里打一条日志
    /// 把它变成可判定的: 真机上再来一次, 日志有这条而头顶没条 = 同步那一环有问题;
    /// 日志压根不出现 = 当时挨的是击退, 原版也没有条。
    /// </summary>
    private static readonly Dictionary<_0023Oq._0023aD, string> CrowdControlNames =
        new Dictionary<_0023Oq._0023aD, string>
        {
            [_0023Oq._0023aD._0023PDc] = "被控制",
            [_0023Oq._0023aD._0023sab] = "眩晕",
            [_0023Oq._0023aD._0023p9b] = "技能封禁",
            [_0023Oq._0023aD._0023q9b] = "沉默",
            [_0023Oq._0023aD._0023tZb] = "恐惧",
            [_0023Oq._0023aD._0023zBc] = "失去行动能力",
            [_0023Oq._0023aD._0023GZb] = "定身",
        };

    /// <summary>挂在玩家身上的控制 buff 打一条日志, 见 <see cref="CrowdControlNames"/>。</summary>
    private static void LogCrowdControl(Buff buff, Entity target)
    {
        if (!target.IsPlayer || !CrowdControlNames.TryGetValue(buff._0023iq, out string name))
        {
            return;
        }

        Serilog.Log.Information("玩家 {Player} 中了控制: {Kind} ({Buff}), 时长 {Duration:F2} 秒 —— "
                        + "客户端头顶应该出现状态条",
            target.AsPlayer?.Name ?? target.IndexGlobal.ToString(), name,
            buff.GetType().Name, buff._0023fq);
    }

    /// <summary>
    /// 目标的抗性挡得住这个 buff 吗 —— 原厂抗性系统。
    ///
    /// ## 判据来自原厂调用点, 不是推测
    ///
    /// 每个 buff 自带两个字段 (Buff.cs:67-69):
    ///
    ///   <c>_0023rq</c>  <c>ResistanceStatTypes</c>, 这是哪一类控制
    ///   <c>_0023sq</c>  控制强度, 基类默认 4 (:172)
    ///
    /// 而容器的 <c>_0023cac(类型, 强度)</c> 返回**能不能生效**
    /// (`-0k/-mac.cs:318`), 内部就一句
    /// <c>Math.Round(抗性值) &lt; 强度</c> —— 抗性值追平强度就免疫。
    ///
    /// 原厂在别处是这么用的 (`-EF/-3I.cs:261`):
    ///
    ///     if (target.ResistanceStats == null ||
    ///         target.ResistanceStats._0023cac(ResistanceStatTypes._0023sab, 4))
    ///
    /// 注意 <c>null 容器 = 不抗</c>, 这里照抄。
    ///
    /// ## 数值全是原厂的, 我们一个都没定
    ///
    ///   玩家       <c>Player.cs:416</c>   <c>_0023eac(false, 3)</c>  逐类型 3
    ///   教程 NPC   <c>-Mk/-YO.cs:81</c>   <c>_0023kac(3)</c>         常量 3
    ///   普通 NPC   <c>NPC.cs:301</c>      <c>_0023jac(1)</c>         常量 1
    ///   僵尸各类   <c>-Mk/-02.cs:76</c> 等  <c>_0023gac(6)</c> + <c>_0023hac(3,…)</c>
    ///   Entity 兜底 <c>Entity.cs:417</c>  <c>_0023eac(true)</c>       常量 0
    ///
    /// 于是默认 4 强度的控制对谁都生效 (玩家 3 &lt; 4), 而强度 ≤3 的对玩家无效。
    /// 补上这道判据**不会**大改现有手感 —— 它放行的是绝大多数, 拦掉的是原厂
    /// 本来就该拦的那一小撮。
    ///
    /// ## <c>_0023y9b</c> 是"不归任何一类"
    ///
    /// 大量 buff 声明 <c>_0023rq = _0023y9b</c> (0x4000), 而 <c>_0023cac</c> 开头
    /// 就 <c>if (_0023iu == _0023y9b) return true;</c> (:329) —— 那类永远生效。
    /// 不用在这里特判。
    /// </summary>
    private static bool ResistsByStats(Buff buff, Entity target)
    {
        _00230k._0023mac resistance = target.ResistanceStats;
        if (resistance == null)
        {
            return false;
        }

        return !resistance._0023cac(buff._0023rq, buff._0023sq);
    }

    /// <summary>
    /// 目标免疫近战击退, 而这正是一个近战击退 buff 吗。
    ///
    /// ## 这一条和抗性系统是**两条独立的路**, 都要过
    ///
    /// 曾经以为这是"抗性系统的临时切片", 补完
    /// <see cref="ResistsByStats"/> 就能删掉 —— **不是**。算一遍就清楚:
    /// 两个击退 buff 都只声明了 <c>_0023rq = ResistanceStatTypes._0023nE</c>
    /// (`-Sq/-nE.cs:76`, `-Sq/-1E.cs:69`), 没有覆盖 <c>_0023sq</c>, 于是取基类
    /// 默认 4; 玩家抗性是 3, <c>3 &lt; 4</c> —— 抗性系统**放行**。
    ///
    /// 而玩家实测 (对着原版视频) 是: "近战攻击对僵尸有击退效果, 对玩家角色没有
    /// 击退效果", 被敌对 NPC 近战打中也不会眩晕。所以原厂拦它的不是抗性, 是
    /// 另一个现成开关: <c>Entity.CanBeMeleeKnockBacked</c> (读
    /// <c>PermanentMeleeKnockbackImmunity</c>) —— 原厂自己在
    /// <c>-EF/-3I.cs</c> 的近战判定里也查它。
    ///
    /// 不拦的后果是定身 1 秒 (<c>_0023nE</c> -&gt; <c>WeakKnockbackStun</c> -&gt;
    /// <c>Character.IsAllowedToMove()</c> :2814 直接查它)。
    ///
    /// ## 规则是"打人不击退, 打僵尸击退" —— 不是"打玩家不击退"
    ///
    /// 玩家实测第二轮: "PVP 和 PVE 的战斗不一样, 要分开来" —— 第一版只给
    /// 玩家上了免疫, 于是 Amber 砍 Charlie/Fuse 时把他们打得又退又晕。
    ///
    /// 现在两处置这个字段, 覆盖所有**人形角色**:
    ///
    ///   <c>LidgrenHost.TrySpawnPlayer</c>        玩家
    ///   <c>TutorialEntitySpawner.Spawn</c>       教程 NPC (基类 _0023Mk._0023YO)
    ///
    /// 僵尸走的是另一个基类 (<c>_0023iO</c>), 不置 —— 它们照样被打退。
    ///
    /// ## 为什么判类型而不是判 <c>_0023sq</c>
    ///
    /// <c>_0023sq</c> 是个裸整数, 语义要靠抗性表才能解释 —— 而那张表没有。
    /// 直接认那两个击退 buff 类反而是**能说清楚**的判据:
    ///
    ///   <c>_0023Sq._0023nE</c>   近战/技能击退 (近战判定对象施加的就是它)
    ///   <c>_0023Sq._00231E</c>   另一个击退 buff (Benthos 的 AimedShot 用)
    ///
    /// 两个的 <c>_0023Hq()</c> 结构一模一样, 都是
    /// "<c>_0023PE</c> 为真 -> 挂 KnockbackStun / WeakKnockbackStun"。
    ///
    /// 僵尸不受影响: 它们的 <c>CanBeMeleeKnockBacked</c> 是 true,
    /// 近战照样把它们打退 —— 那也是玩家实测要的。
    ///
    /// ## 例外: 拖拽不是击退
    ///
    /// 玩家实测 (2026-09-13): "Puller 吐舌头牵引到玩家时应该会把玩家拖向他, 而不是玩家停留在原地。"
    ///
    /// Puller 的拖拽是**钉住 buff 自己**每 1.2 秒往玩家身上挂一个 <c>_0023nE</c>,
    /// 方向朝 Puller、位移 15 (-Sq/-xBc.cs 的 <c>_0023Iq</c>: <c>_0023RE = _0023zF</c>,
    /// <c>_0023PE / _0023OE / _0023gq = true</c>)。它借的是击退的**位移**, 不是"被打退"。
    /// 上面这条规矩把它一起拦了 -> 玩家被钉在原地, 舌头拉不动。
    ///
    /// 判据: 施加者 (<paramref name="spawner"/>) 是个 **buff** 的击退放行。近战打击、技能判定体
    /// 施加的击退 (施加者是法术/判定对象) 照拦。
    ///
    /// ## 例外: 公羊原地重踏
    ///
    /// 玩家实测 (2026-09-13): "Ram 原地重踏应该有击退效果。"
    ///
    /// 重踏 (<c>AbilityIdentifier.RamStomp</c>, 普通/精英公羊的槽 2) 的判定体是
    /// <c>_0023EF._0023U2b</c>: 以公羊为圆心一圈 AOE, 每个命中的挂一个 <c>_0023nE</c>
    /// (<c>_0023PE = true</c>, 位移 <c>_0023zF</c>、时长 <c>_0023AF</c> 取平衡数据, 方向背离公羊,
    /// -EF/-U2b.cs:126-136)。它不是"被近战打中", 是一记范围震地 —— 原版被踩到就是往外飞。
    /// 施加者是这个判定体的击退放行; 公羊的普通挥击 (RamStrike) 走的是近战判定, 照拦。
    /// </summary>
    private bool ResistsMeleeKnockback(Buff buff, Entity target, GameObjectBase spawner = null, Entity owner = null)
    {
        if (target.CanBeMeleeKnockBacked)
        {
            return false;
        }

        // 巨型 BOSS 的招 (冲撞、捡人弹、震地、扇巴掌 ...) 本来就是把人撞飞的, 不是"被近战打中" (2026-09-14)
        if (owner != null && TypeInCategories(owner, ObjCat.Bosses))
        {
            return false;
        }

        if (spawner is Buff)
        {
            return false;       // 拖拽 (钉住 buff 施加的位移), 见上
        }

        if (spawner is _0023EF._0023U2b)
        {
            return false;       // 公羊重踏的震地 AOE, 见上
        }

        return buff is _0023Sq._0023nE or _0023Sq._00231E;
    }

    /// <summary>
    /// 施加一个状态效果 (眩晕、沉默、无敌、隐身、免疫击退 ...)。
    ///
    /// ## 基类给的是空壳
    ///
    ///     public virtual void ApplyStatEffect(..., out StatEffectChange change)
    ///     { change = _0023Hfd; }
    ///
    /// <c>_0023Hfd</c> 是个 dummy (<c>IsDummy =&gt; _0023tld == 0</c>)。调用方拿到它
    /// 存起来, 之后 <c>Remove()</c> 判 <c>IsDummy</c> 直接跳过 —— 施加和移除
    /// **都**是空操作, 而且完全静默。
    ///
    /// 影响面: <c>Entity</c> 里一整排判定读的都是 <c>StatEffects</c> ——
    /// <c>IsInvulnerable</c>、<c>IsStealthed</c>、<c>IsMeleeKnockbackImmune</c>、
    /// <c>IsNonTargetable</c> (僵尸 AI 用它挑目标)、伤害结算里的
    /// <c>Invulnerable</c> 判定 (Entity.cs:538)。全部恒为 false。
    ///
    /// ## 容器本身是完整的
    ///
    /// <c>StatEffectContainer._0023Lld</c> (:105) 施加、<c>_0023Mld</c> (:120) 移除,
    /// 都在。<c>StatEffectChange.Remove()</c> (:30) 也现成 —— 它自己会找回实体
    /// 并调 <c>_0023Mld</c>。我们只要造一个**非 dummy** 的句柄。
    ///
    /// ## 句柄 ID 和 ChangeStats 同源
    ///
    /// 用同一个自增计数器, 并且跳过 0 —— 0 是 dummy 的标志
    /// (<c>IsDummy =&gt; _0023tld == 0</c>), 发出去会让调用方以为失败。
    /// 这和 <see cref="ChangeStats"/> 里的处理一致。
    /// </summary>
    public override void ApplyStatEffect(object changer, Entity entity,
                                         StatEffectType statEffectType,
                                         out StatEffectChange statEffectChange)
    {
        if (entity == null)
        {
            base.ApplyStatEffect(changer, entity, statEffectType, out statEffectChange);
            return;
        }

        uint id = _nextStatChangeId++;
        if (id == 0)
        {
            id = _nextStatChangeId++;
        }

        int changerId = (changer as GameObjectBase)?.IndexGlobal ?? -1;

        statEffectChange = new StatEffectChange(statEffectType, entity.IndexEntity, id, changerId);

        // StatEffects 是**字段**而不是属性 (Entity.cs:98), 所以对这个 struct
        // 调方法能真的改到它。要是属性就会改到副本上 —— 那种坑我们在
        // GameModeData 上踩过一次。
        entity.StatEffects._0023Lld(ref statEffectChange);
    }

    public ConductorGameLogic.Abilities.SCTSynchronizer SCTSynchronizerForTests => _SCTSynchronizer;

    /// <summary>
    /// <c>OnCharacterDamageTaken</c> 被调了多少次。
    ///
    /// 诊断用: 飘字不出现时, 它能把"钩子没被调"和"调了但没进队列"分开 ——
    /// 两者的修法完全不同。
    /// </summary>
    public int DamageHookCalls;

    /// <summary>
    /// 因为调用方传了 <c>sendSCT: false</c> 而被压掉的飘字次数。
    ///
    /// 诊断用: 玩家说"某个数字没飘出来"时, 它能立刻分清是"压掉了"还是
    /// "根本没走到这里" —— 两者的修法完全不同。
    /// </summary>
    public int SuppressedSCTCount;

    /// <summary>
    /// **玩家**的每一次血量变更都记一条 —— 含正变化, 不限流。
    ///
    /// ## 为什么单独开一条
    ///
    /// 上面那条伤害日志只记负变化且限流到每秒一条, 查"玩家挨了谁的打"够用,
    /// 但查不了玩家实测那个"升级的时候出现加血减血": 那是**一减一加**,
    /// 加的那半边被过滤掉了, 而且两条挨得极近, 限流会吃掉其中一条。
    ///
    /// 我为那个现象推断过一次成因 (装备 globals 撤/加), 被测试证伪 ——
    /// 见 <see cref="IsBookkeeping"/> 上面那段。所以这次不再推断,
    /// 先把事实记下来: 谁改的、改了多少、什么原因、改完血量和上限各是多少。
    ///
    /// 只记玩家 —— 一局里玩家的血量变更是几十条的量级, 僵尸是几千条。
    /// </summary>
    private void LogPlayerHealthChange(Character character, _00230k._0023Nh statChangeData)
    {
        if (character == null || !character.IsPlayer || statChangeData == null)
        {
            return;
        }

        if (statChangeData.StatType != StatType.Health)
        {
            return;
        }

        Serilog.Log.Debug(
            "玩家血量: {Delta:+0.0;-0.0} -> {After:F1}/{Max:F1} 原因={Reason} 类型={ChangeType} " +
            "改动者={Changer} 飘字={Sct}",
            statChangeData._00237zb, statChangeData.NewValue, character.MaxHealth,
            statChangeData.ChangeReason, statChangeData.ChangeType,
            statChangeData.Changer?.GetType().Name ?? "(无)",
            _suppressSCTDepth == 0 && !IsBookkeeping(statChangeData));
    }

    /// <summary>
    /// 这次属性变更是**账面调整**吗 —— 账面调整不飘字。
    ///
    /// ## 判据二: <c>StatChangeReason._0023mJc</c> —— **这才是升级那串数字**
    ///
    /// 真机观测 (2026-08-06 12:39:20, 4 毫秒内):
    ///
    ///     +14.8 -> 471.7/573.0  原因=_0023mJc 改动者=玩家自己
    ///     -45.7 -> 426.0/517.5  原因=_0023mJc
    ///     +47.5 -> 473.5/575.2  原因=_0023mJc
    ///     -45.7 -> 427.8/519.8  原因=_0023mJc
    ///     +47.5 -> 475.4/577.5  原因=_0023mJc
    ///     +22.5 -> 497.9/577.5  原因=_0023fjc  (升级补差值, 已被 sendSCT 压住)
    ///
    /// 注意 MaxHealth 在 573→517→575→519→577 之间来回跳 —— 那是
    /// <c>SetWeaponAndEquipGlobals</c> 撤/装两个武器槽的 globals。血量上限一变,
    /// 当前血量就被**按比例重算**, 于是一减一加。玩家说"取决于当时是否满血"
    /// 也对上了: 满血时比例重算的绝对值最大。
    ///
    /// <c>_0023mJc</c> 的语义是"重算", 不是伤害 —— 交叉验证很硬:
    ///
    ///   - 角色类自己调 <c>ChangeStats(this, Health, _0023vk, **0f**, _0023mJc, this)</c>
    ///     (`-Mk/-E3.cs:187`, `-Mk/-dWb.cs:159`) —— 传 0 的"变更", 只为触发重算
    ///   - 几十个 buff 的伤害监听器**逐个显式排除**它 (`-Sq/-3jc.cs:160`、
    ///     `-Sq/-6hb.cs:95`、`-Sq/-9yb.cs:156` …)
    ///   - `-Sq/-8Cc.cs:96` 和 `-Sq/-cib.cs:91` 把它和 <c>_0023Yc</c>
    ///     **并排**排除 —— 和下面那条判据同一组
    ///
    /// 只加这一个, **不能**整个用原厂的 <c>_0023qJc()</c>: 那个还排除
    /// <c>_0023kJc</c>(技能伤害) 和 <c>_0023nJc</c>(伤害分摊), 那两个是真伤害,
    /// 排掉就没有伤害数字了。
    ///
    /// ## ⚠️ 装备 globals 那条路本身是干净的 —— 我推断错过一次
    ///
    /// 我原本推断: 升级时 <c>Player.OnLevelUp</c> 两次调
    /// <c>SetWeaponAndEquipGlobals</c> (:2104/:2109), 落到 <c>Item._0023aBc</c>
    /// (Crafting/Item.cs:91) 的 <c>StatChangeReason._0023Yc</c> 上, 一撤一装
    /// 就是玩家看到的一减一加。
    ///
    /// **错在最后一环**: 那一行传的是 <c>isPermanent: false</c>, 而实测
    /// (<c>CombatTextTests.ChangeStatsWithNoReason_QueuesNothing</c> 用 false
    /// 跑时 <c>DamageHookCalls</c> 恒为 0) 表明**非永久变更根本不触发伤害钩子**,
    /// 也就压根不会飘字。装备 globals 这条路是干净的。
    ///
    /// 那次的教训: **"看起来是这条路"不等于"走的是这条路"**, 得让观测说话。
    /// 上面那六行日志是加了 <see cref="LogPlayerHealthChange"/> 之后一局跑出来的,
    /// 一眼就定位了 —— 比连着两轮推断划算得多。
    ///
    /// ## 判据一: <c>StatChangeReason._0023Yc</c>
    ///
    /// 那是这个枚举的零值 (None)。装备 globals、<c>_0023tK.Remove()</c>、
    /// 以及 <c>StatContainer</c> 内部重算 (:187/:290/:316 都显式传它) 用的都是它,
    /// 而**没有任何一条真实伤害走这个原因** —— 普攻是 <c>_0023DH</c>,
    /// 技能是 <c>_0023kJc</c>/<c>_0023tF</c>, 流血自伤是 <c>_0023mJc</c>。
    ///
    /// 交叉验证: 原厂 <c>-Sq/-VT.cs:103</c> 判"这是不是一次真实伤害"时,
    /// 把 <c>_0023Yc</c> 和那几个非战斗原因并排排除掉了 —— 同一个语义。
    /// </summary>
    private static bool IsBookkeeping(_00230k._0023Nh statChangeData) =>
        statChangeData != null
        && (statChangeData.ChangeReason == StatChangeReason._0023Yc
            || statChangeData.ChangeReason == StatChangeReason._0023mJc);

    /// <summary>
    /// 大于 0 表示当前这层 <see cref="ChangeStats"/> 明确要求不飘字。
    ///
    /// 用计数器而不是 bool —— 监听器里再次调 <c>ChangeStats</c> 是常见的
    /// (吸血、反伤), 嵌套时 bool 会被内层提前清掉。
    /// </summary>
    private int _suppressSCTDepth;

    /// <summary>
    /// 告诉一个客户端: 某个游戏对象被销毁了。
    ///
    /// ## 不发的后果
    ///
    /// 僵尸被打死时服务端一切正常 —— <c>Character._0023jt</c> 里的
    /// <c>Health &lt;= 0 -&gt; Die(killer)</c> 判定是幸存且完整的 (Character.cs:2829)。
    /// 但客户端**从没被告知**, 于是:
    ///
    ///   - 不播死亡动画 (它不知道这东西死了)
    ///   - 模型定在最后一帧
    ///   - 过一会儿对象回收、同步位清掉, 模型**凭空消失**
    ///
    /// 这正是玩家看到的现象。
    ///
    /// ## 帧格式: 判据是客户端的读法
    ///
    /// <c>GameManagerClient.DeserializeGameMessage</c> 的
    /// <c>case GameMessageType.GameObjectDestroyed</c> (:656):
    ///
    ///     int spawnId     = ReadRanged(0, 3);
    ///     int globalIndex = ReadRanged(0, NumberOfGameObjects);
    ///     GetGameObject(globalIndex).DeserializeDestroy(buf);
    ///     if (gameObject.SpawnID == spawnId) gameObject.Destroy();
    ///
    /// 那个 <c>SpawnID</c> 比对是**防串号**的: 对象池会复用同一个 globalIndex,
    /// 如果销毁消息晚到而该槽位已经被新对象占用, SpawnID 对不上就不会误杀。
    /// 所以必须发**销毁那一刻**的 SpawnID。
    ///
    /// 消息头两字节和别处一样: 类别 0 (GameMessage) + 具体类型。
    ///
    /// ## 调用它的循环是现成的
    ///
    /// <c>GameManager</c> 在回收对象前就会遍历 <c>ConnectedClientsList</c> 逐个调
    /// 本方法 (:1625)。我们只需要实现方法本身 —— 以及**维护那个列表**,
    /// 它和 <c>ActiveClientsList</c> 一样只被 new 过, 见 <see cref="ClientVisibility"/>。
    /// </summary>
    /// <summary>
    /// 销毁通知分散到几个序列通道上。
    ///
    /// 上限是 32 (<c>-rye/-9ze.cs:387</c> 那个 ArgumentOutOfRange 检查),
    /// 0 号留给其它游戏消息, 所以最多能用 31 个。取 16 —— 每帧 16 条、
    /// 30fps 就是 480 条/秒, 远超一场混战的死亡速率, 又不至于把通道占满。
    /// </summary>
    private const int DestroyChannels = 16;

    public override void SendGameObjectDestroyed(Client client, GameObjectBase gameObject)
    {
        if (client == null || gameObject == null || NetworkManager == null)
        {
            return;
        }

        _00237ze buffer = NetworkManager.CreateGameMessage(32);
        if (buffer == null)
        {
            return;
        }

        buffer.Write((byte)ConductorGameMessageType.GameMessage);
        buffer.Write((byte)GameMessageType.GameObjectDestroyed);

        // **必须用逐客户端的 spawn id, 不能用 gameObject.SpawnID。**
        //
        // GameObjectBase.SpawnID 的 getter 里留着一段被裁掉的分支残骸:
        //
        //     if (GameManager != null) { _ = GameManager.IsServer; }
        //     return _00232k;
        //
        // 那个空读说明原本是 `IsServer ? 服务端算法 : _00232k`, 而服务端那半边
        // 被裁了。_00232k 是**客户端**从网络收来的字段, 服务端读它没有意义。
        //
        // 正确来源是 GameObjectBase.GetSpawnIdForClient(client) ->
        // GameManager.SpawnDatas.GetSpawnIdForClient(IndexGlobal, client.Index)。
        // 对象自己的 Serialize 写给客户端的也正是这个值 (GameObjectBase.cs:1364),
        // 所以客户端手里的 SpawnID 必然与它一致 —— 这样那个防串号的比对才成立。
        WorldStateWriter.WriteRanged(buffer, gameObject.GetSpawnIdForClient(client), 0, 3);
        WorldStateWriter.WriteRanged(buffer, gameObject.IndexGlobal, 0, NumberOfGameObjects);
        gameObject.SerializeDestroy(buffer, client);

        // **通道要分散, 否则死亡动画会延迟好几秒。**
        //
        // 组包时每个序列通道**每帧只发一条**消息 (-rye/-9ze.cs:199 那个
        // `if (num3 == 1) break`), 而 SendGameMessage 的 channel 默认是 0 ——
        // 所有游戏消息挤在一个通道里排队, 30fps 就是每秒 30 条。
        //
        // 客户端播死亡动画的**唯一入口**是这条销毁通知
        // (NPC.DeserializeDestroyCharacter -> Die(killer), NPC.cs:932-958),
        // 状态同步里的 IsDead 不触发动画。所以一次死一群僵尸时, 通知排队 =
        // 尸体站着不动, 轮到它才突然倒下。玩家实测: "我普通攻击三下打死一个
        // 僵尸会延迟一下才播放死亡动画, 但是我打两下, 停一下, 再打一下,
        // 僵尸就能在受到伤害的瞬间播放死亡动画" —— 停顿正好让队列排空。
        //
        // 按 IndexGlobal 取模分散: 同一个对象的消息始终落在同一个通道 (保序),
        // 不同对象并行发。留 0 号给别的游戏消息。
        int channel = 1 + gameObject.IndexGlobal % DestroyChannels;
        NetworkManager.SendGameMessage(buffer, client, channel);

        // IsDead 决定客户端会不会播死亡动画: NPC.SerializeDestroyCharacter (:901)
        // 在 UseNewOptimizedDestroy 为真时写 isDead + 击杀者, 客户端读到 isDead
        // 才调 Die(killer) -> CharacterModel.OnDeath (NPC.cs:938 -> :944)。
        //
        // 所以"销毁了但 IsDead=False"和"IsDead=True"是两种完全不同的情况:
        //   True  -> 消息带着死亡信息, 动画该播而没播 -> 问题在客户端或传输
        //   False -> 对象是被别的原因回收的, 根本不是死亡 -> 问题在死亡判定
        // 角色的死亡通知留在 Information (每局一两百行, 而且是死亡动画那类问题
        // 唯一的服务端证据); 非角色的销毁 (特效、投射物) 一局三千多行, 降到 Debug。
        Character victim = gameObject as Character;
        Serilog.Log.Write(
            victim == null ? Serilog.Events.LogEventLevel.Debug
                           : Serilog.Events.LogEventLevel.Information,
            "销毁通知 -> slot {Slot}: {Type}#{Index} spawnId={SpawnId} 死亡={Dead} HP={HP}",
            client.Index, gameObject.GetType().Name, gameObject.IndexGlobal,
            gameObject.GetSpawnIdForClient(client),
            victim?.IsDead.ToString() ?? "(非角色)",
            victim == null ? "-" : $"{victim.Health:F1}/{victim.MaxHealth:F1}");
    }

    /// <summary>
    /// 角色语音 —— 重生、被队友救起、击杀播报那一类。
    ///
    /// ## 为什么教程也需要
    ///
    /// 两个原厂调用点在教程里都够得着:
    ///
    ///   <c>-EF/-clb.cs:110</c>   玩家重生       <c>CharacterVoiceClip.Respawn</c>
    ///   <c>-EF/-Gpc.cs:92</c>    被队友救起     <c>CharacterVoiceClip.Resurrected</c>
    ///
    /// 空着的后果不是报错, 是**安静** —— 玩家死了再站起来, 角色一声不吭。
    ///
    /// ## 报文格式逐字段对着客户端的读侧
    ///
    /// <c>GameManagerClient.cs:756-763</c>:
    ///
    ///     Player p = GetPlayer(_00237d._0023Pae(0, NumberOfPlayers));
    ///     CharacterVoiceClip clip = (CharacterVoiceClip)_00237d._0023jae();
    ///
    /// 即"范围编码的玩家序号 + 一字节剪辑号"。<c>_0023Pae(0, max)</c> 的写侧
    /// 是 <c>WorldStateWriter.WriteRanged</c>, 和销毁通知里用的是同一个。
    ///
    /// ## 发给所有人 (方法名的 ToAll 就是这个意思)
    ///
    /// 语音是**位置音效**, 队友那边也该听见。客户端拿到的是玩家序号而不是
    /// "本地玩家", 说明它本来就打算给别人播 —— 单人局里就是自己发给自己。
    ///
    /// 走 0 号通道: 语音是低频事件 (一局几次), 不值得占用销毁通知那 16 个
    /// 分散通道。理由见 <see cref="SendGameObjectDestroyed"/> 里那段。
    /// </summary>
    public override void SendSoundToAll(Player player, CharacterVoiceClip clipToPlay)
    {
        if (player == null || NetworkManager == null)
        {
            return;
        }

        foreach (Client recipient in NetworkManager.ConnectedClientsList)
        {
            _00237ze buffer = NetworkManager.CreateGameMessage(16);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.SoundToAll);
            WorldStateWriter.WriteRanged(buffer, player.IndexPlayer, 0, NumberOfPlayers);
            buffer.Write((byte)clipToPlay);

            NetworkManager.SendGameMessage(buffer, recipient);
        }

        SentVoiceClips++;
    }

    /// <summary>发出去的角色语音条数。诊断用。</summary>
    public int SentVoiceClips;

    /// <summary>
    /// 击杀公告 —— 现在只有**首杀 (FirstBlood)** 会走到这里。
    ///
    /// ## 又是"共享代码在调、服务端那半是空的"
    ///
    /// <c>GameMode.OnCharacterDied</c> (GameMode.cs:1494) 在本局第一次"玩家杀玩家"时调
    /// <c>SendKillAnnouncement(击杀者, -1, 1, FirstBlood)</c>, 而基类
    /// <c>GameManager.SendKillAnnouncement</c> (:3177) 是**空方法** —— 2026-09-18 审校前
    /// 我们没覆盖它, 所以拾荒 4v4v4 里第一滴血既不播语音也不弹公告。
    ///
    /// 其余几种 (KillingSpree 连杀 / KillingSpreeShutdown 终结连杀 / MultiKill 多杀) 共享代码
    /// **一处都没调** —— 那是原厂服务端自己统计的, 随服务端丢了。这里只把通道接上,
    /// 不编那几种的判定规则。
    ///
    /// ## 报文格式 (对着客户端读侧 GameManagerClient.cs:772)
    ///
    ///     ranged(0, NumberOfPlayers)  击杀者 IndexPlayer
    ///     byte                        KillAnnouncementType
    ///     [byte kills]                只有 MultiKill 才写
    ///     [ranged 被终结者]            只有 KillingSpreeShutdown 才写
    ///
    /// 客户端读完把它排进 <c>MultiKillQue</c>, FirstBlood 那一支直接播
    /// <c>CharacterVoiceClip.FirstBlood</c>。
    /// </summary>
    public override void SendKillAnnouncement(int killerIndex, int killedIndex, byte kills,
                                              KillAnnouncementType killEvent)
    {
        if (NetworkManager == null || killEvent == KillAnnouncementType.None)
        {
            return;
        }

        foreach (Client recipient in NetworkManager.ConnectedClientsList)
        {
            _00237ze buffer = NetworkManager.CreateGameMessage(16);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.KillAnnouncement);
            WorldStateWriter.WriteRanged(buffer, killerIndex, 0, NumberOfPlayers);
            buffer.Write((byte)killEvent);

            if (killEvent == KillAnnouncementType.MultiKill)
            {
                buffer.Write(kills);
            }
            else if (killEvent == KillAnnouncementType.KillingSpreeShutdown)
            {
                WorldStateWriter.WriteRanged(buffer, killedIndex, 0, NumberOfPlayers);
            }

            NetworkManager.SendGameMessage(buffer, recipient);
        }

        SentKillAnnouncements++;
    }

    /// <summary>发出去的击杀公告条数。诊断用。</summary>
    public int SentKillAnnouncements;

    /// <summary>
    /// 开局前的系统公告 —— 倒计时报秒、"还有多久强制开局"、"对局开始"。
    ///
    /// ## 这是原厂自己的挂钩点 (2026-09-19 交还)
    ///
    /// <c>GameMode.UpdateGameCountDown</c> (GameMode.cs:1890) 里有 17 处
    /// <c>_GameManager.SendChat(...)</c>, 基类是空方法 —— 那一整段开局倒计时原厂写好了,
    /// 只差服务端把这三个"怎么发出去"的钩子填上 (另两个是
    /// <see cref="SendCountdownStarted"/> 和 <see cref="SendMissionStartCharacterSound"/>)。
    ///
    /// 先前我们没填, 于是自己在宿主里重写了一遍那段逻辑 (MatchStartGate)。现在改成填钩子,
    /// 让原厂代码自己跑 —— 报秒的时机、公告的种类、强制开局的夹取全部回到原厂那一份。
    ///
    /// ## 报文格式 (对着客户端读侧 GameManagerClient.cs:449)
    ///
    ///     bool hasClient=false        不是某个玩家说的
    ///     ChatType (3 位)
    ///     bool isRawText=false        走本地化编号
    ///     int  ServerLocalizedMessages
    ///     bool hasExtraText=false
    ///
    /// 玩家打字聊天是**另一条路** (<see cref="SocialRelay.WriteChat"/>), 那边 hasClient 为真、
    /// 走自由文本。两条写的是同一个 <c>GameMessageType.Chat</c>, 但载荷分叉。
    /// </summary>
    /// <param name="fromClient">原厂签名带着, 系统公告用不到 —— 有发信人的走 SocialRelay。</param>
    /// <param name="toClient">非 null 就只发给这一个人; 原厂的私聊余地, 目前没有调用方用到。</param>
    public override void SendChat(ServerLocalizedMessages serverLocalizedMessages, ChatType chatType,
                                  IClient fromClient = default, IClient toClient = default)
    {
        if (NetworkManager == null)
        {
            return;
        }

        foreach (Client recipient in NetworkManager.ConnectedClientsList)
        {
            if (toClient != null && !ReferenceEquals(recipient, toClient))
            {
                continue;
            }

            _00237ze buffer = NetworkManager.CreateGameMessage(16);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.Chat);
            buffer.Write(false);              // 不是某个玩家说的
            buffer.Write((uint)chatType, 3);
            buffer.Write(false);              // 不是自由文本, 走本地化编号
            buffer.Write((int)serverLocalizedMessages);
            buffer.Write(false);              // 没有附带文本

            NetworkManager.SendGameMessage(buffer, recipient);
        }

        SentSystemChats++;
    }

    /// <summary>发出去的系统公告条数。诊断用。</summary>
    public int SentSystemChats;

    /// <summary>
    /// <c>GameMessageType.CountdownStarted</c> —— 客户端弹开局倒计时那个圈
    /// (GameManagerClient.cs:612, 无载荷)。
    ///
    /// 原厂 <c>GameMode.StartCountDown</c> (:2028) 末尾调它。
    /// </summary>
    public override void SendCountdownStarted()
    {
        if (NetworkManager == null)
        {
            return;
        }

        foreach (Client recipient in NetworkManager.ConnectedClientsList)
        {
            _00237ze buffer = NetworkManager.CreateGameMessage(4);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.CountdownStarted);
            NetworkManager.SendGameMessage(buffer, recipient);
        }

        SentCountdownStarts++;
    }

    /// <summary>发出去的倒计时开始通知条数。诊断用。</summary>
    public int SentCountdownStarts;

    /// <summary>
    /// <c>GameMessageType.MissionStartCharacterSound</c> —— 开局前的角色语音。
    ///
    /// 客户端读一个 0..4 的数, 拿它对本队人数取模挑由谁开口 (GameManagerClient.cs:737)。
    /// 原厂在倒计时剩 10 秒时调 (强制开局支和人满倒计时支各一处)。
    /// </summary>
    public override void SendMissionStartCharacterSound()
    {
        if (NetworkManager == null)
        {
            return;
        }

        // 用模拟层自己的 RNG —— 服务端所有随机都走同一个源, 回放才重现得出来。
        int pick = Random._0023Otc(0, 5);

        foreach (Client recipient in NetworkManager.ConnectedClientsList)
        {
            _00237ze buffer = NetworkManager.CreateGameMessage(4);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.MissionStartCharacterSound);
            buffer._0023Yae(0, 4, pick);
            NetworkManager.SendGameMessage(buffer, recipient);
        }

        SentMissionStartSounds++;
    }

    /// <summary>发出去的开场语音条数。诊断用。</summary>
    public int SentMissionStartSounds;

    // ---- SendIsLeader 为什么**不**覆盖 (2026-09-18 查清, 别再当空实现捡起来) ----
    //
    // 上一轮审校把 GameManager.SendIsLeader 当成"队长标记没下发"记了一笔, 那是错的。
    // 把三件事查到底之后, 结论是这条通道对我们**没有任何作用**:
    //
    //   1. 它不是"队伍队长", 是 **bot 的跟随目标**。
    //      调用方 Player._0023uD() (Player.cs:1614) 第一行就是 `if (!IsBot) return;` ——
    //      整套只在**机器人玩家**身上跑: bot 找出同队的真人 (`!connectedPlayer.IsBot`)
    //      列成名单发下去, 客户端把那些人标 IsLeader = true, 供 bot 的 AI 决定跟谁走。
    //
    //   2. 我们没有 bot, 所以它**一次都不会被调用**。
    //      `Player.IsBot` 全代码树只在 Reset() 里被置 false, 没有任何地方置 true ——
    //      置真的是丢掉的那半服务端 (或走 GameMessageType.IsBot)。私服不造 bot。
    //
    //   3. 就算发了也**没有任何表现**。
    //      客户端的接收分支在 DebugMessageType.BotLeader (GameManagerClient.cs:358,
    //      注意是**调试消息通道**不是 GameMessageType, 上一轮就是因此没找到),
    //      它只做一件事: 给同队玩家设 Player.IsLeader。而 IsLeader 在整个客户端
    //      (含 Unity 层) **没有任何读取方** —— 唯一读它的是 _0023uD() 自己那个
    //      "这队已经有队长了吗"的循环。没有图标、没有 UI。
    //
    // 客户端里其它叫 leader 的东西都无关: GUI_TopBar.UpdateLeader 是拾荒顶栏
    // "哪一队补给领先" (参数是 TeamId), Leadership 是武器改装词条, Leaderboard 是
    // Steam 排行榜。**原版根本没有"队伍队长"这个显示概念。**
    //
    // 所以这里保持基类的空实现。以后真要做 bot, 连同 GameMessageType.IsBot 一起做。

    /// <summary>
    /// 得分飘字 —— "+N" 从世界里某个位置飘起来那个。
    ///
    /// ## 教程里必走的一条
    ///
    /// 交补给时 <c>-Sq/-ajc.cs:250</c> 调它。同一段里经验是发的
    /// (<c>ChangeStats(Experience)</c>), 记分统计也进了
    /// <c>PlayerScoreStats._0023Xrh</c> —— **只有屏幕上那个数字没出来**。
    ///
    /// ## 报文格式对着客户端读侧 (<c>GameManagerClient.cs:644-652</c>)
    ///
    ///     _0023Kcj type = (_0023Kcj)_00237d._0023nae();     // ushort
    ///     ushort value  = _00237d._0023nae();               // ushort
    ///     Vector2 pos   = _00237d._0023gIc();               // ±5000 定点, 16 位/轴
    ///     _0023Rlm(value, type, pos.X * 0.1f, height * 0.1f, pos.Y * 0.1f);
    ///
    /// **那三个 0.1f 是客户端的显示换算, 不是编码的一部分** —— 世界坐标直接写。
    /// 教程图的坐标量级是几千 (卡车在 (-181, 3353)), 落在 ±5000 内。
    ///
    /// 写侧用 <c>_0023eIc</c>, 它和 <c>_0023gIc</c> 是同一个文件里的一对,
    /// 默认参数 (-5000, 5000, 16) 完全一致 (`-z/-jIc.cs:21` 和 :27) —— 这是
    /// 对称性的判据, 不是我们挑的参数。
    ///
    /// ## 只发给得分的那个人
    ///
    /// 签名最后一个参数就是得分者。别人的 "+N" 飘在自己屏幕上没有意义 ——
    /// 客户端那个回调 <c>_0023Rlm</c> 是本地 HUD 的飘字入口, 不带归属判断。
    /// </summary>
    public override void SendPlayerScoreData(_00230k._0023Kcj type, ushort value,
                                             FarseerPhysics.Common.Vector2 worldPos,
                                             Player player)
    {
        if (player?.Client == null || NetworkManager == null)
        {
            return;
        }

        _00237ze buffer = NetworkManager.CreateGameMessage(32);
        if (buffer == null)
        {
            return;
        }

        buffer.Write((byte)ConductorGameMessageType.GameMessage);
        buffer.Write((byte)GameMessageType.PlayerScoreData);
        buffer.Write((ushort)type);
        buffer.Write(value);
        // 扩展方法, 但用全限定静态调用 —— `using _0023z;` 会把那个命名空间下
        // 一大堆混淆短名拉进来, 而这个文件里到处是 `_0023z.xxx` 的全限定引用。
        _0023z._0023jIc._0023eIc(buffer, worldPos);

        NetworkManager.SendGameMessage(buffer, player.Client);
        SentScorePopups++;
    }

    /// <summary>发出去的得分飘字条数。诊断用。</summary>
    public int SentScorePopups;

    /// <summary>
    /// 解锁一个 Steam 成就: <c>GameMessageType.Achievement</c> + 一个字节的成就编号。
    /// 客户端读侧 GameManagerClient.cs:486 按编号调 Steam 的解锁 (CapPoints5 / QuickScavenger / ...)。只发给那个玩家。
    /// </summary>
    public bool SendAchievement(Player player, _0023shd achievement)
    {
        if (player?.Client == null || NetworkManager == null)
        {
            return false;
        }

        _00237ze buffer = NetworkManager.CreateGameMessage(8);
        if (buffer == null)
        {
            return false;
        }

        buffer.Write((byte)ConductorGameMessageType.GameMessage);
        buffer.Write((byte)GameMessageType.Achievement);
        buffer.Write((byte)achievement);
        NetworkManager.SendGameMessage(buffer, player.Client);
        SentAchievements++;
        return true;
    }

    /// <summary>发出去的成就条数。诊断用。</summary>
    public int SentAchievements;

    /// <summary>收到的游戏消息按类型计数, 用来确认链路通没通。</summary>
    public System.Collections.Generic.Dictionary<GameMessageType, int> ReceivedMessages { get; }
        = new System.Collections.Generic.Dictionary<GameMessageType, int>();

    /// <summary>
    /// 客户端发来的游戏消息入口。
    ///
    /// 基类里这是个**空的 virtual** —— 因为原厂服务端那半边从没发布。
    /// 结构逐行对齐客户端那侧的单参重载 <c>GameManagerClient.DeserializeGameMessage</c>
    /// (:314): **先读一字节类别, 再读一字节具体类型**。
    ///
    ///   ConductorGameMessageType.GameMessage(0)  -> GameMessageType
    ///   ConductorGameMessageType.DebugMessage(1) -> DebugMessageType
    ///
    /// 发送侧的对应写法在 <c>_0023lid</c> (:1235, 写 0) 和 <c>_0023jid</c> (:1223, 写 1)。
    ///
    /// ## 少读这一字节的后果
    ///
    /// 第一版漏了类别字节, 直接把它当 GameMessageType 读 —— 而 <c>Chat</c> 恰好 = 0,
    /// 于是每条上行消息都被解成 "收到 Chat", 且**不会报任何错**。
    /// 位流解析错位就是这个样子: 看着像合法数据, 实际全是错的。
    /// </summary>
    public override void DeserializeGameMessage(INetBuffer netBuffer, IClient fromClient)
    {
        ConductorGameMessageType category = (ConductorGameMessageType)netBuffer._0023jae();

        switch (category)
        {
            case ConductorGameMessageType.GameMessage:
            {
                GameMessageType type = (GameMessageType)netBuffer._0023jae();
                ReceivedMessages.TryGetValue(type, out int seen);
                ReceivedMessages[type] = seen + 1;
                OnGameMessage?.Invoke(type, netBuffer, fromClient);
                break;
            }

            case ConductorGameMessageType.DebugMessage:
            {
                DebugMessageType type = (DebugMessageType)netBuffer._0023jae();
                OnDebugMessage?.Invoke(type, netBuffer, fromClient);
                break;
            }

            default:
                // 走到这里说明类别字节不是 0 也不是 1 —— 几乎肯定是帧解析错位了,
                // 而不是客户端发了新类别。值得当作错误看。
                OnUnknownCategory?.Invoke((byte)category);
                break;
        }
    }

    /// <summary>调试消息钩子 (DebugMessageType)。</summary>
    public System.Action<DebugMessageType, INetBuffer, IClient> OnDebugMessage { get; set; }

    /// <summary>类别字节既不是 0 也不是 1 —— 帧解析错位的信号。</summary>
    public System.Action<byte> OnUnknownCategory { get; set; }

    /// <summary>
    /// 收到游戏消息时的钩子。
    ///
    /// 让宿主 (LidgrenHost) 能观察和处理消息, 而不必在这里塞一个巨大的 switch ——
    /// 消息处理会牵扯到出生、复活、聊天等一堆子系统, 分散在宿主侧更清楚。
    /// </summary>
    public System.Action<GameMessageType, INetBuffer, IClient> OnGameMessage { get; set; }

    /// <summary>
    /// 注册全部游戏模式。
    ///
    /// <c>LoadMap</c> 最后要 <c>_GameModeDictionary[gameModeSettings.GameModeType]</c>,
    /// 而 GameModeType 是**从地图文件读出来的** —— 也就是说服务端支持哪些模式不由我们
    /// 决定, 由地图决定。少注册一个, 表现就是开那张图时 KeyNotFoundException。
    ///
    /// 所以这里逐条对齐 <c>GameManagerClient.GameManagerInitialize()</c> (:270 起) 的
    /// 七条注册, 一条不少。GameMode 本身是双端共享的 (ScavengerHunt / HordeMode /
    /// RaidMission … 都在 ConductorGameLogic.GameModes 里), 不需要各写一份。
    /// </summary>
    protected override void GameManagerInitialize()
    {
        // **实例上的 AbilityManager 和静态的 BalanceData 不是同一个东西。**
        //
        // 平衡数据有两条取法, 散落在原厂各处:
        //   GameManager.BalanceData      静态, GameDataLoader 已经填了
        //   gameManager.AbilityManager   实例属性 (:840), 给它赋值的地方在客户端树里
        //                                **一处都没有** —— 那是服务端做的
        //
        // 不填的结果是走实例那条路的代码全部 NRE, 而且藏得很深: 玩家捡起补给时
        // 原厂会挂一个"背着补给"的 buff (Supplies.cs:188), 那个 buff 的 _0023Hq
        // 第一行就读 GameManager.AbilityManager._0023j7f._0023Etb
        // (-EF/-YRk.cs:60) —— 当场炸在拾取链里。
        //
        // 之前没暴露, 是因为玩家压根没被登记进补给表, 拾取在更早的一步就静默失败了
        // (见 SupplyRegistrar)。修好登记之后这个才浮出来。
        AbilityManager = GameManager.BalanceData;

        // 拾荒用我们的子类 —— 只为了把原厂的开局倒计时接上, 见 ScavengerHuntServer
        AddGameMode(GameModeType.ScavengerHunt, new ScavengerHuntServer());
        AddGameMode(GameModeType.Horde, new HordeMode());
        AddGameMode(GameModeType.Tutorial, new _00235cc());
        AddGameMode(GameModeType.EndlessHorde, new _002399());
        // 十字路口用我们的子类 —— 只为了能设难度档, 见 RaidMissionServer
        AddGameMode(GameModeType.RaidMission, new RaidMissionServer());
        AddGameMode(GameModeType.ScoutMission, new ScoutMission());
        AddGameMode(GameModeType.TeamvsTeam, new TeamvsTeam());
    }
}
