using System;
using ConductorGameLogic.GameModes;

namespace DIE.Server.Game;

/// <summary>
/// 十字路口 (Crossroads) 模式的服务端壳子 —— 只为了能设难度档。
///
/// ## 为什么需要一个子类
///
/// <c>RaidMission.DifficultyLevel</c> 的 setter 是 <c>protected</c>
/// (RaidMission.cs:172)。客户端那边只在反序列化时写它 (:480), 整棵树里**没有任何
/// 一处算它** —— 决定难度的是服务端, 那段逻辑随服务端丢了。派生一个类是唯一
/// 不碰反编译树就能设它的办法 (那棵树改了下次 sanitize 就没了)。
///
/// ## 难度档从哪来
///
/// 玩家在大厅的十字路口界面选的档 (<c>LobbyDifficulty</c>: 简单/普通/困难/不可能/极难),
/// 经 <c>PlayerQueueData.QueuedCrossroadDifficulty</c> 进票据, 再经匹配服传到这里。
/// 完整链路见 <c>QueueRosterCodec.Encode(seats, crossroadDifficulty)</c>。
///
/// 它有两个用处: 挑哪一批地图 (<c>GameModeAvailability.PickMap</c>), 以及这里 ——
/// 下发给客户端显示 (序列化在 RaidMission.cs:468)。
///
/// ## 同步表不受影响
///
/// <c>ReserveSynchronizables</c> 的数量由 <c>RaidMission.InitializeGameMode</c> 决定
/// (两个 <c>GameModeDataSynchronizer</c>), 这个子类**不加任何同步对象**, 所以
/// 客户端那份 <c>RaidMission</c> 和服务端这份数出来一样多 —— 数不对客户端会打
/// "Client-Server synchronizable mismatch!"。加字段可以, 加同步对象不行。
/// </summary>
public sealed class RaidMissionServer : RaidMission
{
    /// <summary>
    /// 这一局的难度档 (0 简单 … 4 极难)。开局后设一次, 之后随模式数据下发给客户端。
    /// </summary>
    public int Difficulty
    {
        get => DifficultyLevel;
        set => DifficultyLevel = value;
    }

    // ------------------------------------------------------------------
    // 开局前在基地等人 (2026-09-23 玩家定: 十字路口按原厂公开匹配凑满 4 人)
    //
    // 和拾荒完全同一套, 见 ScavengerHuntServer 的类说明: 原厂 GameMode 的
    // UpdateGameCountDown / StartCountDown 让它自己跑, 我们只接"开局那一下"。
    //
    //   人满 -> 20 秒倒计时
    //   人不满 -> 等到 ForceStartTime (默认 120 秒, 从战斗服起算) 强制开,
    //            45 / 30 / 15 秒各报一次, 最后五秒报秒
    //
    // 开局前那圈白墙和"把人按在出生区"是 GameMode.Update 的基类逻辑 (GameMode.cs:1376),
    // 任何模式只要没置 _Started 客户端就会放, 不用我们管。客户端的十字路口开场镜头
    // (RaidMission._0023C9) 也是等 _Started 才播 —— 所以它自然落在倒计时走完之后。
    //
    // 等人期间匹配服会把后来排队的人补进这一局, 见 LidgrenHost.TryAddLateJoiners。
    // ------------------------------------------------------------------

    /// <summary>开局那一下 —— 宿主接它去置 <c>_Started</c> 并把世界布置起来。</summary>
    public Action OnStart { get; set; }

    /// <summary>
    /// 十字路口一局最多几个人 —— 满了就起 20 秒倒计时。
    ///
    /// **不用原厂的 <c>ShouldStartCountdown</c>**: 它拿地图设置的
    /// <c>NumOfPlayersPerTeam × NumOfTeams</c> 当满员, 而十字路口的图上写的是 12
    /// (跟拾荒一样, 实测), 可十字路口一局最多 4 人 (大厅组队上限, 客户端也按
    /// <c>LobbyMemberCount != 4</c> 判断没满, GUI_PlayPartyWindow.cs:699)。照原厂那个数,
    /// 4 人到齐了也得干等 120 秒。玩家定的是"凑满 4 人", 所以这里按 4。
    /// </summary>
    public const int FullTeam = 4;

    /// <summary>现在在 20 秒倒计时里吗 (人满那一支)。</summary>
    public bool CountdownRunning { get; private set; }

    public override void Start()
    {
        base.Start();
        CountdownRunning = false;
        OnStart?.Invoke();
    }

    protected override void UpdateGameMode(float deltaTime, float totalTime)
    {
        base.UpdateGameMode(deltaTime, totalTime);

        if (_Started)
        {
            return;
        }

        if (_GameManager.ActivePlayerList.Count >= FullTeam && !CountdownRunning)
        {
            CountdownRunning = true;
            StartCountDown();
        }

        UpdateGameCountDown(deltaTime, totalTime);
    }
}
