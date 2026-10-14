using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using ConductorShop;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.Stats;

namespace DIE.Server.Game;

/// <summary>
/// 局末下发 <c>GameMessageType.Rewards</c> —— 让大厅立刻看到这局挣的账号经验。
///
/// 玩家实测: "账号有经验了, 只是没有实时更新。"
///
/// ## 断在哪一环
///
/// 经验本身早就落库了 (<see cref="Data.AccountStore.AddStoryMapXp"/>), 日志里
/// 有"账号 2 入账经验 573"。问题是**大厅不知道**: 它握着登录那一刻的
/// <c>LoginDataMessage</c> 快照, 而日志显示整局下来 <c>GetAccountData</c> 被请求
/// **0 次** —— 它压根没回头问过。
///
/// 原厂靠的是这条消息。顺着大厅那棵树读:
///
///   GameManagerClient.cs:933  OnRewardsReceived: 读 int 长度 + 裸字节,
///                             原样写进 "lastRewards.data" (**不解析**)
///   CribMain.cs:1737          大厅启动时发现这个文件 -&gt; PostMatchStatusCheck()
///   CribMain.cs:1994          RewardSerialization.ReadRewardFile()
///                             -&gt; PlayerRewards.Deserialize
///   CribMain.cs:2037          _CribWindows.PostMatchReward.SetData(rewards)
///
/// 也就是说这条消息就是"结算画面"的全部输入。我们不发, 大厅就永远走不到
/// 那一步, 自然也没有任何刷新时机。
///
/// ## 报文由原厂的 Serialize 写
///
/// <c>PlayerRewards</c> / <c>RewardCollection</c> 只在大厅树里有 (游戏客户端
/// 不解析它, 所以游戏树里没编进去)。副本放在
/// <c>src/DIE.GameLogic/_Fixups/</c>, 只改了缓冲区接口的别名 ——
/// 详见那里的 README-rewards.md。报文格式仍是原厂 Version 9 的
/// <c>_0023NKs</c> 写的, 我们一个字节都不手写。
///
/// ## 哪些字段填了, 哪些没填
///
/// 填的是我们**手上确实有**的:
///
///   AccountExperience  这局挣的经验 (玩家要看的就是它)
///   Currencies         这局挣的银币(Gear Points) 和角色点
///   ZombieKills / Deaths / Damage / Healing / Assists   来自 PlayerScoreStats
///   Supplies / TeamSupplies                            来自 Supplies
///   MapIndex / Character / Team / Placement / 时长
///
/// ## 两条 Send
///
/// 正路是 <see cref="Send(GameManager, GameServer, IReadOnlyList{PlayerMatchReward})"/>:
/// <see cref="MatchRewards.Compute"/> 已经跑过原厂 <c>RewardManager.GetRewards</c>,
/// 掉落物品、货币、计分箱得分全在那份数据里, 这里只序列化。
///
/// 另一条重载自己拼 <c>PlayerRewardData</c> —— 那是掉落接上原厂引擎之前的走法,
/// 现在只剩测试在用 (不依赖掉落表和账号库, 测报文格式最省事)。
/// </summary>
public static class RewardSender
{
    /// <summary>
    /// 最后一次发出去的结算载荷有多少字节 —— 诊断用。
    ///
    /// 玩家那边"消息发了但 lastRewards.data 没出现"时, 这个数能分开
    /// "我们发了个空壳"和"发对了但客户端没落盘"。
    /// </summary>
    public static int LastPayloadBytes { get; private set; }

    /// <summary>
    /// 把**原厂算好**的那份结算发出去。
    ///
    /// 这是正路: <see cref="MatchRewards.Compute"/> 已经跑过
    /// <c>RewardManager.GetRewards</c>, 这里只负责序列化和发送。
    /// </summary>
    /// <returns>发出去几份。</returns>
    public static int Send(GameManager simulation, GameServer network,
                           IReadOnlyList<PlayerMatchReward> rewards)
    {
        if (simulation == null || network == null || rewards == null)
        {
            return 0;
        }

        int sent = 0;

        foreach (PlayerMatchReward reward in rewards)
        {
            Client recipient = reward.Player?.Client as Client;
            if (recipient == null)
            {
                continue;
            }

            byte[] blob = Serialize(reward.Data, PlayersInMatch(simulation));

            LastPayloadBytes = blob.Length;

            _0023rye._00237ze buffer = network.CreateGameMessage(blob.Length + 64);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.Rewards);
            buffer.Write(blob.Length);
            buffer.Write(blob);

            network.SendGameMessage(buffer, recipient);
            sent++;
        }

        return sent;
    }

    /// <summary>
    /// 自己拼一份结算发出去 —— **只剩测试在用**。
    ///
    /// 掉落接上原厂引擎之前, 局末走的是这条: 手填 PlayerRewardData, 货币来自
    /// 我们自己写的一套简单规则 (已删)。留着这条重载是因为它不依赖掉落表和
    /// 账号库, 测"报文格式对不对"最省事。
    /// </summary>
    /// <param name="accountExperience">这局挣的账号经验。</param>
    /// <returns>发出去几份。</returns>
    public static int Send(GameManager simulation, GameServer network,
                           uint accountExperience, float matchSeconds,
                           long startTimeTicks, int silver = 0,
                           int characterPoints = 0)
    {
        if (simulation == null || network == null)
        {
            return 0;
        }

        int sent = 0;

        foreach (Client recipient in network.ConnectedClientsList.OfType<Client>())
        {
            Player player = recipient.LocalPlayer;
            if (player == null)
            {
                continue;
            }

            byte[] blob = Build(simulation, player, accountExperience,
                                matchSeconds, startTimeTicks, silver, characterPoints);

            LastPayloadBytes = blob.Length;

            // 客户端读的是 int 长度 + 裸字节 (GameManagerClient.cs:937)。
            _0023rye._00237ze buffer = network.CreateGameMessage(blob.Length + 64);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.Rewards);
            buffer.Write(blob.Length);
            buffer.Write(blob);

            network.SendGameMessage(buffer, recipient);
            sent++;
        }

        return sent;
    }

    /// <summary>
    /// 拼一份 <see cref="PlayerRewards"/> 并让原厂的 <c>Serialize</c> 写成字节。
    /// </summary>
    private static byte[] Build(GameManager simulation, Player player,
                                uint accountExperience,
                                float matchSeconds, long startTimeTicks, int silver,
                                int characterPoints)
    {
        PlayerScoreStatsSnapshot score = PlayerScoreStatsSnapshot.Of(player);

        PlayerRewardData data = new PlayerRewardData
        {
            PlayerKills = score.PlayerKills,
            ZombieKills = score.ZombieKills,
            Deaths = score.Deaths,
            Assists = score.Assists,
            Damage = score.Damage,
            Healing = score.Healing,

            Supplies = DeliveredByTeam(simulation, player.TeamId),
            TeamSupplies = DeliveredByTeam(simulation, player.TeamId),

            StartTimeTicks = startTimeTicks,
            MatchTimeSeconds = (uint)Math.Max(0f, matchSeconds),

            Team = (byte)player.TeamId,
            Placement = Placement.First,
            Character = player.CharacterEnum,
            GameMode = QueueTypeOf(simulation),
            MapIndex = simulation.MapIndex,
            LevelReached = player.GetLevel(),

            // 经验挂在 Rewards 里 —— 大厅那边读的就是
            // rewards.Data.Rewards.AccountExperience (RewardManager.cs:421)。
            Rewards = new RewardCollectionData
            {
                AccountExperience = (int)Math.Min(accountExperience, int.MaxValue),

                // 银币 (= Gear Points) + 角色点。金币是充值货币, 不给。
                // 数额由调用方给 —— 正路上是原厂 RewardManager 算的。
                Currencies = new CurrencyCollection(
                    gold: 0, silver: silver,
                    characterPoints: characterPoints, researchPoints: 0),

                // Items 里那三个 List 必须是实例 —— RewardCollection.SerializeVersion8
                // (行 111 起) 直接 foreach 它们, 留 null 会当场 NRE。
                // NewItemCollection() 是原厂的工厂, 建的就是三个空表。
                Items = ItemCollection.NewItemCollection(),
            },

            // 首胜奖励同理: 结构要在, 内容为空。私服还没做掉落经济。
            FirstWinRewards = new RewardCollectionData
            {
                Items = ItemCollection.NewItemCollection(),
            },
        };

        return Serialize(data, PlayersInMatch(simulation));
    }

    /// <summary>
    /// 让原厂的 <c>Serialize</c> 把一份结算写成字节。
    /// </summary>
    /// <remarks>
    /// 放 public 是为了让测试能拿真实数值直接往返 (报文里有定宽字段 ——
    /// 堆叠物种类数是 byte, 每种数量是 ushort —— 数值大了会不会被撑坏
    /// 只能测出来, 看不出来)。同 <c>SocialRelay</c> 那几个 Write*。
    /// 见 <c>RewardSenderTests.TheTenfoldPayoutOfSeptember20_StillRoundTrips</c>。
    /// </remarks>
    public static byte[] Serialize(PlayerRewardData data, PlayerRewards.Player[] players)
    {
        PlayerRewards rewards = new PlayerRewards(data, players);

        // ## 用一张干净的草稿纸, 不借出站缓冲
        //
        // 第一版借的是 network.CreateGameMessage() —— 那是**网络层**的缓冲,
        // 它会先写一个通道字节, 而且拿了不发的副作用不明 (池子记账? 序号?)。
        // 为了取几个字节去动网络层是没必要的风险。
        //
        // LidgrenNetBuffer + _00237d 是纯内存缓冲, 测试里回读也用的它,
        // 两边同一个东西。
        StunGameNetwork.LidgrenNetBuffer buffer =
            new StunGameNetwork.LidgrenNetBuffer(new _0023iae._00237d());

        rewards.Serialize(buffer);

        return Slice(buffer, 0, buffer._0023Bae());
    }

    /// <summary>本局在场的所有人 —— 结算画面上要列出来。</summary>
    private static PlayerRewards.Player[] PlayersInMatch(GameManager simulation)
    {
        return simulation.ActivePlayerList
            .Where(p => p != null && p.IsActive && p.Client != null)
            .Select(p => new PlayerRewards.Player(p.Client.SteamID, p.TeamId))
            .ToArray();
    }


    /// <summary>队伍交到卡车上多少。</summary>
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
    /// 结算画面上显示的模式。
    ///
    /// <c>QueueType</c> 和 <c>GameModeType</c> 是两套枚举 (一套是排队用的,
    /// 一套是地图里写的), 这里按语义对一遍。
    ///
    /// ## 不只是挑个标题
    ///
    /// 原来这里的注释写着"挑错不影响别的" —— 那是在还没有掉落表的时候。
    /// 现在它决定两件事:
    ///
    ///   1. 掉落表按 <c>group.DropsInGameTypes.Contains(GameType)</c> 过滤,
    ///      不在列表里的队列**一件都不掉** (<see cref="Data.MatchDropTable"/>);
    ///   2. <c>RewardManager.GetRelevantBoxes</c> 按队列挑计分箱, 挑错了分数
    ///      算不出来, 掉落倍率就是 0。
    /// </summary>
    private static QueueType QueueTypeOf(GameManager simulation) =>
        simulation.GameMode?.Type switch
        {
            GameModeType.Tutorial => QueueType.Tutorial,
            GameModeType.RaidMission => QueueType.RaidMission,
            GameModeType.ScavengerHunt => QueueType.Scavenger,
            GameModeType.Horde => QueueType.ResistanceNormal,
            GameModeType.TeamvsTeam => QueueType.TeamvsTeam,

            // Scout 任务 -> ResistanceEasy。
            //
            // 这一档不是猜的: RewardManager.GetRelevantBoxes 里 ResistanceEasy
            // 那条分支取的正是 HordeScoutMissionBox / HordeScoutTimeBox ——
            // 原厂就是拿这个队列装 Scout 的。
            //
            // (原来归的是 RaidMission。那条路会走 GetCrossroadLevelInformation
            //  按十字路口难度换算 TierLimit, 和 Scout 没关系。)
            GameModeType.ScoutMission => QueueType.ResistanceEasy,

            _ => QueueType.ResistanceEasy,
        };

    /// <summary>
    /// 把 <paramref name="startBits"/> 到 <paramref name="endBits"/> 那一段字节取出来。
    ///
    /// 起点是字节对齐的 —— 前面只有 <c>CreateGameMessage</c> 写的整字节头部。
    /// 不对齐的话这里切不出来, 所以顺手断言一下: 与其发一段错位的数据让大厅
    /// 解析出垃圾, 不如空手而归 (调用方会记日志)。
    /// </summary>
    private static byte[] Slice(StunGameNetwork.LidgrenNetBuffer buffer,
                                int startBits, int endBits)
    {
        byte[] all = buffer._0023OXf();

        if (all == null || startBits % 8 != 0 || endBits <= startBits)
        {
            return Array.Empty<byte>();
        }

        int start = startBits / 8;
        int length = (endBits - startBits + 7) / 8;

        if (start + length > all.Length)
        {
            length = all.Length - start;
        }

        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        byte[] slice = new byte[length];
        Buffer.BlockCopy(all, start, slice, 0, length);
        return slice;
    }

    /// <summary>
    /// 从 <see cref="PlayerScoreStats"/> 上取几个数。
    ///
    /// 单独一层是因为那些字段是混淆名, 散在调用处会看不懂 ——
    /// 对应关系是从大厅的计分板绑定反查的
    /// (UI_ScoreboardCrossroadsBinding.cs:330 起)。
    /// </summary>
    private readonly struct PlayerScoreStatsSnapshot
    {
        public int PlayerKills { get; private init; }
        public int ZombieKills { get; private init; }
        public int Deaths { get; private init; }
        public int Assists { get; private init; }
        public int Damage { get; private init; }
        public int Healing { get; private init; }

        public static PlayerScoreStatsSnapshot Of(Player player) => new()
        {
            PlayerKills = player.PlayerScoreStats._0023k9b,
            ZombieKills = player.PlayerScoreStats._0023l9b,
            Deaths = player.PlayerScoreStats._0023m9b,
            Assists = player.PlayerScoreStats._0023Vrh,
            Damage = player.PlayerScoreStats.Damage,
            Healing = player.PlayerScoreStats.Healing,
        };
    }
}
