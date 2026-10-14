using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic.View;
using ConductorCrafting;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorShop;
using DIE.Server.Data;
using Serilog;

namespace DIE.Server.Game;

/// <summary>
/// 这一局每个人拿到什么 —— 把战场上的数据翻译成原厂
/// <c>RewardManager.GetRewards</c> 的入参, 再把结果拿回来。
///
/// ## 这个类不算掉落
///
/// 一行掉落逻辑都没有。品阶怎么夹、曲线怎么滚、蓝图怎么配平, 全在原厂的
/// <c>RewardManager</c> 里; 数在 <see cref="MatchDropTable"/> 里。
/// 这里只做**翻译**: 我们的 <c>Player</c> / <c>Supplies</c> / <c>GameMode</c>
/// -&gt; 原厂的 <c>RewardPlayerInputData</c> / <c>RewardTeamInputData</c>。
///
/// 上一版是自己写的 <c>CurrencyRewards</c> (基础 200 银币 + 每杀 2 + 每份补给 5,
/// 封顶 2000)。那是在还没找到原厂引擎的时候写的应急方案。
///
/// ## 三个字段原厂不填, 我们补
///
/// <c>RewardManager.GetPlayerRewardData</c> 不填 Damage / Healing / Assists ——
/// 原版这三个是战斗服另外一条路送的。而我们的计分板要它们
/// (玩家实测提过"没有造成伤害和治疗的数据"), 所以拿回结果后补上。
/// </summary>
public static class MatchRewards
{
    /// <summary>
    /// 算这一局的奖励。
    /// </summary>
    /// <param name="table">掉落表。</param>
    /// <param name="simulation">跑完的这一局。</param>
    /// <param name="accountOf">
    /// 按账号 id 取账号侧的输入 (经验、背包里已有的蓝图/改装图/装置蓝图)。
    /// 战斗服自己不连账号库, 所以走回调 —— 和 <c>AwardAccountXp</c> 同一个道理。
    /// </param>
    /// <param name="userOf">这个玩家是哪个账号 (战斗服凭会话票对号入座, 见 <c>MatchRoster</c>)。</param>
    /// <param name="experienceOf">这个玩家这一局挣的账号经验。</param>
    /// <param name="infectionLevel">这一局的感染等级。</param>
    /// <param name="random">
    /// 掉落的随机源。传进来而不是内部 new —— 测试要可复现。
    /// </param>
    /// <param name="consumablesOf">每人带进来的消耗品 (开局几个 / 现在几个)。</param>
    /// <param name="teamOutcomeOf">拾荒: 按队给名次, 每队各算一次; 别的模式传 null (一队, 打完就是第一)。</param>
    public static IReadOnlyList<PlayerMatchReward> Compute(
        DropTable table,
        GameManager simulation,
        Func<ulong, RewardAccountSnapshot> accountOf,
        Func<Player, ulong> userOf,
        Func<Player, uint> experienceOf,
        int infectionLevel,
        float matchSeconds,
        long startTimeTicks,
        Random random,
        Func<Player, RewardConsumableInputData[]> consumablesOf = null,
        Func<TeamId, TeamOutcome> teamOutcomeOf = null,
        int crossroadDifficulty = -1,
        IReadOnlyList<byte> crossroadMissionTypes = null)
    {
        if (table == null || simulation == null)
        {
            return Array.Empty<PlayerMatchReward>();
        }

        List<Player> players = simulation.ActivePlayerList
            .Where(p => p != null && p.IsActive && p.Client != null)
            .ToList();

        if (players.Count == 0)
        {
            return Array.Empty<PlayerMatchReward>();
        }

        // 认输通过的局也走收尾 (IsCompleted 会被置上), 但奖励算"中途放弃" —— 见 ConcedeVote
        bool completed = (simulation.GameMode?.IsCompleted ?? false) && !(simulation.GameMode?.WasConceded ?? false);

        RewardTeamInputData team = new RewardTeamInputData
        {
            GameType = QueueTypeOf(simulation),
            MapIndex = simulation.MapIndex,
            MatchTimeSeconds = matchSeconds,
            StartTimeTicks = startTimeTicks,
            Supplies = DeliveredByTeam(simulation, players[0].TeamId),
            ServerInfectionLevel = (byte)InfectionLevel.Clamp(infectionLevel),

            // 没打完就是"中途放弃" —— 原厂 Conceded 那一档会砍掉计分箱,
            // 也会清掉保底计数器的推进。
            VictoryPlacement = completed ? Placement.First : Placement.Conceded,
            // Placement 没有 "None" 这一档 (只有 First/Second/Third/Conceded)。
            // 这两项只在 Scavenger 模式被读 (拿第一才加分), Third 就是
            // "没拿到那个加分"。
            BarricadeRacePlacement = Placement.Third,
            MidGamePlacement = Placement.Third,

            // 掉落品阶不按账号等级夹。我们这边"能装备到几阶"是靠商店和合成
            // 卡住的, 再夹一道会让高阶玩家的掉落莫名其妙缩水。
            UseAccountLevelRestriction = false,
        };

        // ## 拾荒 (4v4v4) 每队一份队伍输入
        //
        // 原厂 GetRewards 一次只收一个 RewardTeamInputData (名次 / 路障竞速 / 中局 / 补给数都是"这一队的")。
        // 三队的名次不同, 所以按队分组各算一次。别的模式只有一队, 走原来那一次。
        if (teamOutcomeOf != null)
        {
            List<PlayerMatchReward> all = new List<PlayerMatchReward>();
            foreach (IGrouping<TeamId, Player> group in players.GroupBy(p => p.TeamId))
            {
                TeamOutcome outcome = teamOutcomeOf(group.Key);
                RewardTeamInputData teamInput = team;
                teamInput.Supplies = DeliveredByTeam(simulation, group.Key);
                teamInput.VictoryPlacement = (simulation.GameMode?.WasConceded ?? false) ? Placement.Conceded : outcome.Victory;
                teamInput.BarricadeRacePlacement = outcome.BarricadeRace;
                teamInput.MidGamePlacement = outcome.MidGame;
                all.AddRange(ComputeGroup(table, simulation, group.ToList(), teamInput, accountOf, userOf,
                    experienceOf, random, consumablesOf, crossroadDifficulty, crossroadMissionTypes));
            }

            return all;
        }

        return ComputeGroup(table, simulation, players, team, accountOf, userOf, experienceOf, random,
            consumablesOf, crossroadDifficulty, crossroadMissionTypes);
    }

    private static List<PlayerMatchReward> ComputeGroup(
        DropTable table,
        GameManager simulation,
        List<Player> players,
        RewardTeamInputData team,
        Func<ulong, RewardAccountSnapshot> accountOf,
        Func<Player, ulong> userOf,
        Func<Player, uint> experienceOf,
        Random random,
        Func<Player, RewardConsumableInputData[]> consumablesOf,
        int crossroadDifficulty,
        IReadOnlyList<byte> crossroadMissionTypes = null)
    {
        List<RewardPlayerInputData> inputs = new List<RewardPlayerInputData>();
        List<RewardAccountSnapshot> snapshots = new List<RewardAccountSnapshot>();

        // ## 身份由调用方给, 不能问 Client.SteamID
        //
        // 战斗服从来没给 <c>Client.SteamID</c> 赋过值, 一直是 **0**。拿 0 去查账号的后果
        // 不是报错, 是 SnapshotFor 退回 Empty (UserId = 0), 然后 SendMatchRewards 里那句
        // `if (reward.UserId == 0) continue;` 把整段落库跳掉 —— 货币和掉落物**全部丢掉**,
        // 日志里只有一句"读不到账号 0"。
        //
        // 正确的来源是战斗服的名单: 进场时凭会话票对号入座 (LidgrenHost.UserOf)。
        foreach (Player player in players)
        {
            RewardAccountSnapshot snapshot =
                accountOf?.Invoke(userOf?.Invoke(player) ?? 0) ?? RewardAccountSnapshot.Empty;
            snapshots.Add(snapshot);
            inputs.Add(InputFor(simulation, player, snapshot, consumablesOf?.Invoke(player), team));
        }

        // ## merged 那一份不能丢
        //
        // 原来这里是 `out _, out _`。第一个 out 里装着**这一局之后的保底计数器**
        // (MergedAccountCounterUpdates) —— 原厂 CalculateCounters 算完之后只能
        // 从这里拿, PlayerRewardData 上没有。丢掉的表现是保底永远停在 0:
        // 连续多少局没掉都不会变得更容易, 而且零报错。
        //
        // ## 一个人调一次
        //
        // GetRewards 逐人算, 某人算的时候抛了异常就被它自己的 try/catch 吞掉、**这个人不进结果列表**。
        // 以前一次传全队、再按下标把结果配回玩家 —— 中间少一个, 后面的人全部错位, 奖励落进别人的账号
        // (PlayerRewardData 上没有 UserID, 事后也对不回去)。各人之间没有共享计算 (保底计数器按人算),
        // 分开调结果一样。2026-09-17 审校发现。
        Random rng = random ?? new Random();
        List<PlayerMatchReward> rewards = new List<PlayerMatchReward>();

        for (int i = 0; i < players.Count; i++)
        {
            List<PlayerRewardData> results = RewardManager.GetRewards(
                table, new List<RewardPlayerInputData> { inputs[i] }, team, rng,
                out MergedModificationRewards merged, out _);

            if (results.Count == 0)
            {
                Log.Error("原厂结算没给账号 {UserId} 算出结果 (它自己的日志里有异常), 这个人这局没有奖励",
                    snapshots[i].UserId);
                continue;
            }

            rewards.Add(new PlayerMatchReward(
                players[i],
                snapshots[i].UserId,
                Patch(results[0], players[i], experienceOf?.Invoke(players[i]) ?? 0,
                      crossroadDifficulty, crossroadMissionTypes,
                      inputs[i].SuppliesDelivered),
                CountersOf(merged, snapshots[i].UserId),
                inputs[i].FirstScavengerWinBonusAvailable));
        }

        return rewards;
    }

    /// <summary>
    /// 补上原厂不填的三个字段, 以及这一局的账号经验。
    ///
    /// 经验不走掉落表 —— 它是按任务目标算的
    /// (<c>LidgrenHost.PlayerExperience</c>), 和"滚不滚得到"无关。
    /// </summary>
    private static PlayerRewardData Patch(PlayerRewardData data, Player player,
                                          uint accountExperience, int crossroadDifficulty,
                                          IReadOnlyList<byte> crossroadMissionTypes = null,
                                          int suppliesThisMatch = 0)
    {
        data.Damage = player.PlayerScoreStats.Damage;
        data.Healing = player.PlayerScoreStats.Healing;
        data.Assists = player.PlayerScoreStats._0023Vrh;

        // 补给也是原厂不填的一格 (实测局末恒为 0)。结算画面上那个数读的就是它,
        // 十字路口的难度解锁判据也读它 —— 留 0 的话两边都不对。
        data.Supplies = suppliesThisMatch;

        RewardCollectionData rewards = data.Rewards;
        rewards.AccountExperience += (int)Math.Min(accountExperience, int.MaxValue);

        // ## 十字路口的难度档给 GP 加成 (原厂 GearPointsBonus)
        //
        // 0 / 25 / 50 / 75 / 100 percent —— 越难给得越多, 这是玩家挑高档打的
        // 主要动机。数来自 StaticCrossroadsGUIData, 见 CrossroadDifficulty。
        //
        // -1 表示这局不是十字路口 (别的队列没有难度档), 原样不动。
        //
        // 只加**银币 (= Gear Points)**: 原厂那个字段就叫 GearPointsBonus,
        // 没说角色点和经验也跟着涨。
        if (crossroadDifficulty >= 0)
        {
            float bonus = CrossroadDifficulty.GearPointsMultiplier(crossroadDifficulty);
            CurrencyCollection currencies = rewards.Currencies;

            currencies.Silver = (int)Math.Min(
                (long)Math.Round(currencies.Silver * bonus), int.MaxValue);

            rewards.Currencies = currencies;

            // ## 这一格空着会让客户端在结算那一刻 NRE
            //
            // AchievementManager.OnGameCompleted 的 RaidMission 分支直接
            // `foreach (byte b in rewards.Data.CrossroadMissionTypes)`, **没有
            // null 检查** (旁边的 CrossroadCompleteScores 反倒判了)。而原厂的
            // 反序列化是"长度为 0 就不赋值" (PlayerRewards:888) —— 所以发空数组
            // 也救不了, 字段会停在 null 上。
            //
            // 炸了的后果不只是没有成就: 它把整条 OnGameProcessEnd 打断,
            // PostMatchRewardWindow.SetData 走不完 —— 奖励界面弹不出来,
            // 大厅的 GP / CP / 经验也跟着不刷新。玩家 2026-09-21 在云服务器上
            // 打完一局十字路口报的就是这个, 客户端日志里是:
            //
            //     Failed to execute OnGameProcessEnd: System.NullReferenceException
            //       at AchievementManager.OnGameCompleted (PlayerRewards rewards)
            //       at PostMatchRewardWindow.SetData (PlayerRewards rewards)
            //       at CribMain.SwitchToPostMatchRewardWindow (...)
            //
            // 兜底那一格: 任务列表拿不到时也要发一个字节, 宁可内容是
            // RaidEventType.None 也不能让长度是 0。
            data.CrossroadMissionTypes =
                crossroadMissionTypes != null && crossroadMissionTypes.Count > 0
                    ? crossroadMissionTypes.ToArray()
                    : new byte[] { (byte)RaidEventType.None };
        }

        data.Rewards = rewards;

        return data;
    }

    private static RewardPlayerInputData InputFor(GameManager simulation, Player player,
                                                  RewardAccountSnapshot snapshot,
                                                  RewardConsumableInputData[] consumables,
                                                  RewardTeamInputData team)
    {
        return new RewardPlayerInputData
        {
            UserID = snapshot.UserId,
            AccountExperience = snapshot.AccountExperience,
            Level = player.GetLevel(),
            Team = (byte)player.TeamId,
            Character = player.CharacterEnum,

            PlayerKillingBlows = player.PlayerScoreStats._0023k9b,
            ZombieKillingBlows = player.PlayerScoreStats._0023l9b,
            Deaths = player.PlayerScoreStats._0023m9b,
            SuppliesDelivered = SuppliesDeliveredFor(simulation, player),

            AverageWeaponTier = AverageWeaponTier(snapshot.WeaponSchematicIds),

            // ## 这四个不能是 null
            //
            // GetRewards 会 StunLog.Log 抱怨然后继续跑, 但下游
            // (inventoryBlueprints 的 LINQ、CalculateCounters) 会当场 NRE ——
            // 而那个异常被 GetRewards 自己的 try/catch 吞掉, 表现是
            // "这个人这局什么都没拿到", 零报错。
            Counters = snapshot.Counters,
            BlueprintsInInventory = snapshot.BlueprintIds,
            DesignsInInventory = snapshot.DesignIds,
            GadgetBlueprintsInInventory = snapshot.GadgetBlueprintIds,

            // 武器 GUID 只用于结算画面上"这局用的是哪两把", 内容不影响掉落。
            // 长度必须正好 2 —— 不是 2 的话 GetPlayerRewardData 会记一条错误日志。
            Weapons = new Unique[2],
            // 每格开局几个 / 现在几个 -> 原厂算出 Consumable1..3Uses, 客户端据此减本地背包 (见 ConsumableLedger)
            EquippedConsumables = consumables ?? Array.Empty<RewardConsumableInputData>(),

            // 拾荒每日首胜: 今天还没拿过, 这一局又是赢的那一队 —— 原厂据此加计分箱 + 开首胜箱 (MatchDropTable.ScavengerFirstWinBox)
            FirstScavengerWinBonusAvailable = snapshot.ScavengerFirstWinAvailable
                                              && team.GameType == QueueType.Scavenger
                                              && team.VictoryPlacement == Placement.First,
        };
    }

    /// <summary>
    /// 平均武器品阶 —— 掉落品阶的基准。
    ///
    /// 和 <see cref="InfectionLevel.FromWeapons"/> 同源, 但这里**不夹**到
    /// 感染等级的合法区间: 原厂拿它当 <c>CraftingManager.GetTier</c> 的输入,
    /// 0 阶是合法值 (只是 0 阶没有可掉的蓝图, GiveAutoGroup 自己会抬到 1)。
    /// </summary>
    private static int AverageWeaponTier(IReadOnlyList<ushort> weaponSchematicIds)
    {
        List<int> tiers = (weaponSchematicIds ?? Array.Empty<ushort>())
            .Where(id => id != 0)
            .Select(id => CraftingManager.GetWeaponSchematic(id))
            .Where(s => s != null)
            .Select(s => s.Tier)
            .ToList();

        return tiers.Count == 0 ? 1 : (int)Math.Round(tiers.Average());
    }

    /// <summary>
    /// 这一局算哪个队列 —— 决定用哪几个计分箱, 以及掉不掉东西。
    ///
    /// 和 <c>RewardSender.QueueTypeOf</c> 是同一张映射表。放两份是因为结算画面
    /// 那边只拿它挑标题, 而这里挑的是掉落规则 —— 万一以后要分开, 分得开。
    /// </summary>
    private static QueueType QueueTypeOf(GameManager simulation) =>
        simulation.GameMode?.Type switch
        {
            GameModeType.Tutorial => QueueType.Tutorial,
            GameModeType.RaidMission => QueueType.RaidMission,
            GameModeType.ScavengerHunt => QueueType.Scavenger,
            GameModeType.Horde => QueueType.ResistanceNormal,
            GameModeType.TeamvsTeam => QueueType.TeamvsTeam,
            GameModeType.ScoutMission => QueueType.ResistanceEasy,
            _ => QueueType.ResistanceEasy,
        };

    /// <summary>
    /// 这一局记到这个人头上的补给 = **这队交到卡车上的总量**。
    ///
    /// ## 不算身上没交的 (玩家 2026-09-22 定)
    ///
    /// 玩家原话: "应该是全队交付给卡车的总量, 不算身上未交付的, 这个数据在
    /// 结算面板上也会显示。"
    ///
    /// 也就是说结算面板那个补给数和难度解锁的判据是**同一个数**, 语义是
    /// "这一局交成了多少", 背在身上没交的不算成果。
    ///
    /// ## 之前错在只查身上那半
    ///
    /// 原来这里叫 <c>CarriedAndDelivered</c>, 查的却是
    /// <c>_0023Fj(playerIndex)</c> —— 身上**当前**背着的量。玩家交给卡车之后
    /// 身上就空了, 局末算出来恒为 0:
    ///
    ///     账号 2 这局不算解锁进度 (第 0 档, 补给 0/400), 进度保持 0
    ///
    /// 而那一局玩家偷了 100、交付了五六趟。难度解锁因此永远不动, 原厂奖励
    /// 计算拿到的 <c>SuppliesDelivered</c> 也一直是 0。
    ///
    /// 按队伍算也是原厂数据的形状: <c>Supplies</c> 里交付累计本来就只有
    /// 按队伍的那一格 (<c>_0023yj[team]._0023mj</c>, 见 <c>_0023Uj</c>),
    /// 按人存的只有"身上当前多少"。
    /// </summary>
    private static int SuppliesDeliveredFor(GameManager simulation, Player player) =>
        DeliveredByTeam(simulation, player.TeamId);

    private static int DeliveredByTeam(GameManager simulation, TeamId team)
    {
        if (simulation.GameMode is not _0023bd._0023ad withSupplies)
        {
            return 0;
        }

        Supplies supplies = withSupplies._00239c();
        return supplies?._0023Rj(team) ?? 0;
    }

    /// <summary>
    /// 这一局之后这个人的保底计数器。
    ///
    /// 原厂把它塞在 <c>MergedAccountCounterUpdates</c> 里而不是
    /// <c>PlayerRewardData</c> 上 —— 那份是给结算画面看的, 计数器是服务端的账。
    /// 找不到就返回 null, 调用方据此"保持库里原样"而不是抹成空。
    /// </summary>
    private static IReadOnlyList<byte> CountersOf(MergedModificationRewards merged, ulong userId)
    {
        if (userId == 0 || merged.MergedAccountCounterUpdates == null)
        {
            return null;
        }

        foreach (AccountCounterUpdate update in merged.MergedAccountCounterUpdates)
        {
            if (update.UserID == userId)
            {
                return update.Counters;
            }
        }

        return null;
    }
}

/// <summary>拾荒里一队的结果: 最终名次、路障竞速名次、中局 (先装满卡车) 名次。原厂奖励只看后两项是不是第一。</summary>
public readonly record struct TeamOutcome(Placement Victory, Placement BarricadeRace, Placement MidGame);

/// <summary>一个玩家这一局的结果。</summary>
public readonly record struct PlayerMatchReward(
    Player Player,
    ulong UserId,
    PlayerRewardData Data,
    IReadOnlyList<byte> Counters,
    bool ScavengerFirstWin = false);

/// <summary>
/// 算奖励要用到的账号侧数据。
///
/// 战斗服不连账号库, 所以由建它的人 (<c>MatchHostRegistry</c>) 查好传进来。
/// </summary>
public readonly record struct RewardAccountSnapshot(
    ulong UserId,
    uint AccountExperience,
    IReadOnlyList<ushort> WeaponSchematicIds,
    IReadOnlyList<ushort> BlueprintIds,
    IReadOnlyList<ushort> DesignIds,
    IReadOnlyList<ushort> GadgetBlueprintIds,
    IReadOnlyList<byte> Counters,
    bool ScavengerFirstWinAvailable = false)
{
    /// <summary>
    /// 查不到账号时的兜底。
    ///
    /// 每个列表都是**空实例而不是 null** —— 详见 <c>MatchRewards.InputFor</c>
    /// 里那段注释: null 会在原厂的 try/catch 里静默吃掉整份奖励。
    /// </summary>
    public static RewardAccountSnapshot Empty => new RewardAccountSnapshot(
        UserId: 0,
        AccountExperience: 0,
        WeaponSchematicIds: Array.Empty<ushort>(),
        BlueprintIds: Array.Empty<ushort>(),
        DesignIds: Array.Empty<ushort>(),
        GadgetBlueprintIds: Array.Empty<ushort>(),

        // 原厂的保底计数器有 AccountCounter 那么多档。长度不够时
        // CalculateCounters 会越界 —— 给满。
        Counters: new byte[Enum.GetValues<AccountCounter>().Length]);
}
