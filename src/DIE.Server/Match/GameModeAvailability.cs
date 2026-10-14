using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Match;

/// <summary>
/// 私服现在开放哪些模式 —— 一处定, 三处用: 下发给客户端的禁用功能表、匹配服收不收这个队列、
/// 自定义房间收不收这张图。
///
/// ## 用的是原厂自己的开关
///
/// 客户端的模式开关全在 <c>FeatureLocker</c> 里, 由服务端下发的禁用功能表控制
/// (<c>FeatureLocker.IsGameModeLocked</c>: 拾荒 -&gt; ScavengerHunt, Horde -&gt; HoardMode,
/// 英雄 Horde -&gt; HoardHeroicMode, 练习 -&gt; PracticeGameMode, 十字路口 -&gt; Crossroads)。
/// 锁住的模式客户端会自己弹"该模式已被锁定", 这是原版停一个模式的正规做法。
///
/// ## 十字路口这个开关是一个**二选一**, 现在选的是"保留原版的开始游戏界面"
///
/// <c>Features.Crossroads</c> 一个标志同时管两处界面, 而且两处的取舍相反:
///
/// | 这个标志 | 开始游戏界面 (<c>GUI_PlaySummaryWindow.cs:236-244</c>) | 组队窗口的下拉框 (<c>GUI_PlayLobbyInfo.cs:179</c>) |
/// |---|---|---|
/// | 开着 (现在) | 显示**十字路口**那张卡 —— 和原版一样 | 只有"拾荒 + 十字路口"两个 |
/// | 锁掉 | 那张卡换成 **Horde** 的总卡 | 变成"拾荒 + **练习** + Horde + 英雄 Horde" |
///
/// 练习 = <c>QueueType.ResistanceEasy</c> = 三张 Scout 图, 正是战斗服现在跑得起来的模式。
///
/// **玩家的选择** (2026-09-12 实测反馈): "原本显示 crossroad 模式的现在显示为 horde mode 了,
/// 改回去。" 所以现在**不锁**十字路口, 开始游戏界面恢复原版模样。
///
/// 代价说清楚: 组队窗口现在选不到练习 —— **组队排队没法从界面上发起**
/// (单人打 Scout 不受影响, 那条路走的是"开始游戏 → 单人 → 三张图", 由
/// <c>Features.PracticeGameMode</c> 管, 和这个标志无关)。要测组队排队,
/// 把 <see cref="LockCrossroadsToExposePractice"/> 翻成 true 再重编一次。
///
/// ## 以后怎么开
///
/// 战斗服能跑某个模式了, 把它从 <see cref="LockedFeatures"/> 挪掉、往 <see cref="QueueCapacity"/>
/// 里加上人数就行。改了之后 <see cref="DisabledFeaturesRevision"/> 要 +1 (客户端按它判断要不要重拉)。
/// </summary>
public static class GameModeAvailability
{
    /// <summary>
    /// 禁用功能表的版本号。**非零且稳定** —— 给 0 或每次都变, 客户端会反复重拉 (ProtocolDefaults 的注释)。
    /// 1 = 什么都不锁, 2 = 连十字路口一起锁, 3 = 十字路口放开 (见类说明的二选一), 4 = 拾荒放开。
    /// </summary>
    public const int DisabledFeaturesRevision = 4;

    /// <summary>
    /// 锁十字路口吗 —— 见类说明那张表。
    ///
    /// <c>false</c>: 开始游戏界面保留原版的十字路口卡 (玩家要的), 但组队窗口选不到练习。
    /// <c>true</c>: 组队窗口出现练习 (组队排队才测得了), 但那张卡变成 Horde。
    ///
    /// 翻这一个 bool 就够, <see cref="LockedFeatures"/> 跟着走。
    /// </summary>
    /// <remarks>
    /// 故意用 <c>static readonly</c> 而不是 <c>const</c> —— 后者会让下面那个 if 变成
    /// 编译期常量, 编译器报 CS0162 (无法到达的代码), 而本仓库把警告当错误。
    /// </remarks>
    public static readonly bool LockCrossroadsToExposePractice = false;

    /// <summary>战斗服还跑不起来的模式。</summary>
    public static readonly IReadOnlyList<Features> LockedFeatures = BuildLockedFeatures();

    private static IReadOnlyList<Features> BuildLockedFeatures()
    {
        List<Features> locked = new List<Features>
        {
            Features.HoardMode,         // Horde: 只有占领点实体, 整局跑不起来
            Features.HoardHeroicMode,   // 英雄 Horde: 同上
        };

        // 十字路口 (RaidMission) 现在**开放**了 —— 队列收它, 地图拼得出来。
        // 锁不锁它只剩界面上的二选一 (见类说明那张表), 和"做完了没有"无关。
        if (LockCrossroadsToExposePractice)
        {
            locked.Add(Features.Crossroads);
        }

        return locked;
    }

    /// <summary>
    /// 排队队列一局最多几人。不在表里的队列不收。
    ///
    /// 两个队列都是 4 人 —— 大厅一队上限就是 4 (<c>RevisedMatchmakingClient(.., 4)</c>,
    /// <c>SteamLobbyData.cs:191</c>), 十字路口的图也是按 4 人做的。
    /// </summary>
    public static int QueueCapacity(QueueType queue) => queue switch
    {
        QueueType.ResistanceEasy => 4,     // 练习 = 三张 Scout 图
        QueueType.RaidMission => 4,        // 十字路口 = 33 张分块拼出来的图
        QueueType.Scavenger => ScavengerTeams.TeamCount * ScavengerTeams.TeamSize,   // 拾荒 4v4v4 = 12
        _ => 0,
    };

    /// <summary>这个队列收不收。</summary>
    public static bool IsQueueOpen(QueueType queue) => QueueCapacity(queue) > 0;

    /// <summary>
    /// 这张图战斗服跑不跑得起来 (自定义房间 / 调试开局窗口用)。按图的原厂属性归到队列,
    /// 再看那一类模式有没有做: 教程、三张 Scout 图、以及十字路口那 33 张。
    ///
    /// ## 十字路口为什么只认"进匹配"的那些
    ///
    /// <c>QueueType.RaidMission</c> 名下还挂着 5 张实验图
    /// (<c>EndlessHordeExperiment_01</c> / <c>RaidMission_Experiment_01..03</c> /
    /// <c>ChunkWorld_Scene</c>) 和 <c>Crossroad_debugScene</c>, 它们的
    /// <c>InMatchmaking</c> 是 false —— 原厂自己都不往里送人。只认 true 的那 33 张。
    /// </summary>
    public static bool IsMapRunnable(int mapIndex)
    {
        if (mapIndex < 0 || mapIndex >= MapManagerData.MapsInEnum.Length)
        {
            return false;
        }

        return MapManagerData.GetMaps(QueueType.Tutorial).Contains(mapIndex)
               || MapManagerData.GetMaps(QueueType.ResistanceEasy).Contains(mapIndex)
               || MapManagerData.GetMaps(QueueType.RaidMission, inMatchMaking: true).Contains(mapIndex)
               || IsScavengerMap(mapIndex);
    }

    /// <summary>
    /// 拾荒的图。五张全认 (Resort / Jungle / Expedition 进匹配, Town / Village 原厂没进匹配但地图数据完整,
    /// 自定义房间可以开) —— 排队挑图仍只挑进匹配的三张 (<see cref="PickMap"/>)。
    /// </summary>
    public static bool IsScavengerMap(int mapIndex) =>
        mapIndex >= 0 && mapIndex < MapManagerData.MapsInEnum.Length
        && MapManagerData.GetMaps(QueueType.Scavenger).Contains(mapIndex);

    /// <summary>
    /// 给排队出来的一局挑图: 该队列里标了"进匹配"的图随机一张 (<c>MapManagerData.GetMaps</c>,
    /// 原厂的队列 -&gt; 地图表)。排队请求里没有图, 原版也是服务端挑。
    /// </summary>
    /// <param name="difficulty">
    /// 十字路口的难度档 (<c>LobbyDifficulty</c>: 0 简单 … 4 极难), 决定能挑哪一批图 ——
    /// 每张图的 <c>MapAttribute.Difficulty</c> 列着它属于哪几档:
    ///
    ///   0       -&gt; <c>Crossroad_map26..33_small</c>   (8 张小图)
    ///   1, 2, 3 -&gt; <c>Crossroad_map16..25_medium</c>  (10 张中图)
    ///   4       -&gt; <c>Crossroad_map01..15</c>         (15 张大图)
    ///
    /// 别的队列不看这个参数 (Scout 三张图没标难度)。
    /// </param>
    public static int PickMap(QueueType queue, Random random, int difficulty = 0)
    {
        int[] maps = queue == QueueType.RaidMission
            ? MapManagerData.GetMaps(queue, inMatchMaking: true, difficulty: ClampDifficulty(difficulty))
            : MapManagerData.GetMaps(queue, inMatchMaking: true);

        if (maps.Length == 0)
        {
            throw new InvalidOperationException($"队列 {queue} (难度 {difficulty}) 没有可用的地图");
        }

        return maps[(random ?? Random.Shared).Next(maps.Length)];
    }

    /// <summary>十字路口的难度档数 —— <c>LobbyDifficulty</c> 是 简单/普通/困难/不可能/极难 五档。</summary>
    public const int CrossroadDifficultyCount = 5;

    /// <summary>
    /// 把客户端给的难度档夹进 0..4。
    ///
    /// 客户端那个字段是 <c>ushort</c> (<c>PlayerQueueData.QueuedCrossroadDifficulty</c>),
    /// 而且是大厅里选的, 没有理由越界 —— 夹一下纯粹是不让一个坏数字把选图炸掉。
    /// </summary>
    public static int ClampDifficulty(int difficulty) =>
        difficulty < 0 ? 0 : (difficulty >= CrossroadDifficultyCount ? CrossroadDifficultyCount - 1 : difficulty);

    /// <summary>下发给客户端的禁用功能表 (登录推送、GetDisabledFeatures、重连认证都用它)。</summary>
    public static DisabledFeaturesData DisabledFeatures() => new DisabledFeaturesData(
        version: DisabledFeaturesRevision,
        features: LockedFeatures.ToArray(),
        characters: Array.Empty<CharacterEnum>(),
        languages: Array.Empty<Languages>(),
        maps: Array.Empty<MapIndex>());
}
