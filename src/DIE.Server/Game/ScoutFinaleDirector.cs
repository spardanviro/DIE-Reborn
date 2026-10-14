using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.View;

namespace DIE.Server.Game;

/// <summary>
/// Scout 任务的收尾: 补给交够了 -&gt; 弹计分板 -&gt; 结束这一局。
///
/// 玩家实测: "物资输送完毕时也没自动弹出计分板然后结束游戏。"
///
/// ## 计分板是客户端自己弹的, 我们只发一条消息
///
/// 顺着客户端往回读:
///
///   GameManagerClient.cs:527   case GameMessageType.TeamFinished:
///                                  TeamId  = _0023jae(3)      // 3 位
///                                  Placement = _0023jae(3)    // 3 位
///                                  finishNow = _0023Fae()     // 1 位
///                              -&gt; ViewManager.TeamFinished(...)
///   ViewManager.cs:2211        -&gt; _GameHUD.TeamFinished(...)
///   GameHUD.cs:162             -&gt; 非 ScavengerHunt 一律 _VictoryDelayTimer = 2f
///   GameHUD.cs:259             DoFinishGUI() 的 IsScoutMission 分支:
///                                  ToggleScoreboard() (新版计分板)
///                                  或 ScoreBoardRaidMission + FinishTeamState()
///
/// 也就是说 **Scout 的计分板早就写好了**, 缺的只是服务端那一句 TeamFinished。
/// 这又是"调用点丢了但功能完整"那一类 (和补给登记、僵尸步态同一个形状)。
///
/// 注意 ViewManager.cs:2221 特意把 Scout 排除在 <c>HUD.VICTORY</c> 之外 ——
/// Scout 不走胜利横幅那一套, 直接进计分板。所以我们除了这条消息不该再补别的。
///
/// ## 完成的判据是原厂的目标量
///
/// <c>ScoutMission.LoadGameMode</c> (:103) 按地图名从 BalanceData 取目标:
///
///   Scout_01_Outpost -&gt; _0023a7f._0023Xd
///   Scout_02_Lab     -&gt; _0023a7f._0023Wd
///   Scout_03_Club    -&gt; _0023a7f._0023Vd
///
/// 存进 <c>Supplies.Settings._0023jj</c>。已经交到卡车上的量是
/// <c>Supplies._0023Rj(team)</c> (Supplies.cs:256 读 <c>_0023mj</c>,
/// 而 <c>_0023Uj</c> 在交付时往那里加)。交够了就完事。
///
/// **我们没有自己定任何数** —— 目标量、队伍、名次全是从原厂数据里读的。
///
/// ## 顺带把任务评分发出去
///
/// <c>CrossroadsGUIUpdate.AddResultData(CompleteScore, missionNr)</c> 走的是
/// 和路径点语音同一条 <c>ObjectiveUpdate</c> 消息, 客户端收到会走
/// <c>GUI_ObjectiveManager.SendMissionResult</c> -&gt;
/// <c>ViewManager.ShowCompleteScore</c>, 点亮任务栏的完成评分。
///
/// 评分给 <see cref="CompleteScore.Gold"/> 是**我们定的** —— 原厂按什么算
/// 金银铜 (用时? 补给量? 死亡次数?) 查不到, 而 Starter Mission 是新手教学,
/// 给个失败评分不合适。等玩家实测反馈再校准。
/// </summary>
public sealed class ScoutFinaleDirector
{
    /// <summary>
    /// 发完 <c>TeamFinished</c> 之后隔多久关掉这一局。
    ///
    /// **这个数是我们定的**, 但不是随便挑的: 客户端那边
    /// <c>GameHUD.TeamFinished</c> 把 <c>_VictoryDelayTimer</c> 设成 2 秒才
    /// 弹计分板 (GameHUD.cs:191)。太早关服玩家连计分板都看不到。
    ///
    /// 给 60 秒 —— 让玩家自己看够了退出, 而不是被踢。
    /// </summary>
    public float ShutdownDelaySeconds { get; set; } = 60f;

    /// <summary>任务完成了没有。</summary>
    public bool MissionComplete { get; private set; }

    /// <summary>交够之后过了多久。</summary>
    public float SinceComplete { get; private set; }

    /// <summary>可以收摊了 —— 宿主拿它关这一局。</summary>
    public bool ReadyToShutDown => MissionComplete && SinceComplete >= ShutdownDelaySeconds;

    /// <summary>已经交上去多少 / 目标多少。诊断用。</summary>
    public int Delivered { get; private set; }

    /// <summary>目标补给量, 从原厂的 <c>Supplies.Settings._0023jj</c> 读。</summary>
    public int Goal { get; private set; }

    /// <summary>这一帧发生了什么 —— 宿主拿去打日志。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>任务完成那一刻的回调, 只触发一次。</summary>
    public Action<GameManager> OnMissionComplete { get; set; }

    /// <summary>
    /// 每帧一次。
    /// </summary>
    public void Update(GameManager simulation, GameServer network, float deltaTime)
    {
        Events.Clear();

        if (MissionComplete)
        {
            SinceComplete += deltaTime;
            return;
        }

        if (simulation?.GameMode is not ScoutMission scout)
        {
            return;
        }

        Supplies supplies = scout._00239c();
        if (supplies?.Settings == null)
        {
            return;
        }

        Goal = supplies.Settings._0023jj;
        if (Goal <= 0)
        {
            return;   // 这张图没设目标量 —— 判不了完成
        }

        // 队伍认玩家自己的, 不写死 Team1 —— 三张 Scout 图的出生队伍未必一样。
        TeamId team = PlayerTeam(simulation);
        if (team == TeamId.Neutral)
        {
            return;
        }

        Delivered = supplies._0023Rj(team);
        if (Delivered < Goal)
        {
            return;
        }

        CompleteNow(simulation, network, $"补给 {Delivered}/{Goal} 已交付");
    }

    /// <summary>
    /// 收尾: 标记局已结束 -> 发任务评分 -> 发结算 -> 发 <c>TeamFinished</c>。
    ///
    /// ## 为什么是 public
    ///
    /// 十字路口 (<see cref="CrossroadMissionDirector"/>) 的夜袭收尾要的是**同一套**
    /// 东西, 判据不同而已 (它是"三轮任务做完并回到卡车", 不是"补给交够")。
    /// 这里面每一条都是踩出来的 —— 三个 GameMode 字段一起设、结算必须赶在
    /// TeamFinished 之前 —— 复制一份到那边只会让其中一条迟早走散。
    /// </summary>
    /// <param name="why">写进日志的完成理由。</param>
    /// <param name="sendMissionScore">
    /// 要不要再发一次任务评分。十字路口不要 —— 三格任务栏每格评分在每个任务结束时
    /// 已经发过了, 这里再发会把第一格覆盖成金牌。
    /// </param>
    public void CompleteNow(GameManager simulation, GameServer network, string why,
                            bool sendMissionScore = true)
    {
        if (MissionComplete || simulation == null)
        {
            return;
        }

        TeamId team = PlayerTeam(simulation);
        if (team == TeamId.Neutral)
        {
            return;
        }

        MissionComplete = true;
        SinceComplete = 0f;

        MarkGameModeCompleted(simulation, team);

        // ## 结算 (Rewards) 要**赶在 TeamFinished 之前**发
        //
        // 玩家实测: 局末日志里"局末结算已下发 1 份"有了, 但游戏目录下
        // **没有 lastRewards.data** —— 而同一秒写下的 lastStats.data 有
        // (那是 ViewManager.TeamFinished 写的)。也就是说 TeamFinished 到了,
        // Rewards 没被处理。
        //
        // 第一版 Rewards 挂在 OnMissionComplete 上, 而那个回调在
        // BroadcastTeamFinished **之后**。客户端收到 TeamFinished 就进收尾
        // (GameHUD 起 2 秒计时、封技能、准备弹计分板), 之后还处不处理游戏消息
        // 是它的事 —— 我们不该把结算押在那之后。
        //
        // 现在顺序: 任务评分 -> **结算** -> TeamFinished。
        int scores = sendMissionScore ? BroadcastMissionScore(network, simulation) : 0;

        OnMissionComplete?.Invoke(simulation);

        int finished = BroadcastTeamFinished(network, team);

        Events.Add($"任务完成: {why} —— "
                   + $"任务评分 {scores} 份, 下发计分板 {finished} 份");
    }

    /// <summary>
    /// 把"这局完了"写进 GameMode —— EXIT TO CRIB 按钮和黑屏都挂在这上面。
    ///
    /// 玩家实测: "原版是背景变黑屏, 然后弹计分板, 计分板上还有个
    /// EXIT TO CRIB 按钮。"
    ///
    /// 光发 <c>TeamFinished</c> 只让客户端把计分板**弹出来**, 弹出来的是
    /// "比赛中"那一版。按钮的条件在别处:
    ///
    ///   UI_ScoreboardCrossroadsBinding.cs:268
    ///       _ScoreboardData.ShowExitButton = _GameManager.GameMode.IsCompleted;
    ///
    /// 而 <c>IsCompleted</c> **是同步字段** —— <c>GameMode.Serialize</c> (:1525)
    /// 第二个就写它, 客户端 <c>Deserialize</c> (:1554) 读回来。我们的服务端本来
    /// 就在跑那个 Serialize (GameMode 是 synchronizable), 所以这里只要把值设上,
    /// 报文那一步不用管。
    ///
    /// 又是"字段完整、赋值点随服务端丢了"那一类。
    ///
    /// ## 三个字段一起设, 少一个都不对
    ///
    ///   IsCompleted           EXIT TO CRIB 按钮 + 客户端各处的"局已结束"分支
    ///                         (UnityClient.cs:1176 断连提示、GUI_GameHUD.cs:1184)
    ///   TeamHasFinished[队]   封掉技能 (Ability.cs:1310 直接 return),
    ///                         死亡面板也靠它别再弹 (GUI_DeathPanel.cs:209)
    ///   TeamFinishedOrder[队] 名次。<c>GameHUD.TeamFinished</c> 收到消息时
    ///                         自己也会写一遍, 这里一并写上是为了让**后进来的**
    ///                         客户端从同步字段里也读得到。
    ///
    /// 名次 +1 是原厂的编码 (GameHUD.cs:171 <c>(byte)place + 1</c>) ——
    /// 0 表示"还没完成", 所以第一名是 1。
    /// </summary>
    private static void MarkGameModeCompleted(GameManager simulation, TeamId team)
    {
        GameMode mode = simulation.GameMode;

        mode.IsCompleted = true;

        if (mode.TeamHasFinished != null)
        {
            mode.TeamHasFinished[(int)team] = true;
        }

        if (mode.TeamFinishedOrder != null)
        {
            mode.TeamFinishedOrder[(int)team] = (byte)(Placement.First + 1);
        }
    }

    /// <summary>换局时清干净。</summary>
    public void Reset()
    {
        MissionComplete = false;
        SinceComplete = 0f;
        Delivered = 0;
        Goal = 0;
        Events.Clear();
    }

    /// <summary>场上第一个活人的队伍。</summary>
    private static TeamId PlayerTeam(GameManager simulation)
    {
        List<Player> players = simulation.ActivePlayerList;

        for (int i = 0; i < players.Count; i++)
        {
            if (players[i] != null && players[i].IsActive)
            {
                return players[i].TeamId;
            }
        }

        return TeamId.Neutral;
    }

    /// <summary>
    /// 发 <c>GameMessageType.TeamFinished</c> —— 客户端 2 秒后弹计分板。
    ///
    /// ## 位宽是按客户端的读侧配的
    ///
    /// 客户端读的是 <c>_0023jae(3)</c> (3 位读成 byte), 而写侧
    /// <see cref="StunGameNetwork.INetBuffer"/> 上**没有** "byte + 位宽"
    /// 这个重载 —— 只有 <c>Write(uint, int)</c>。底层写的是同样的 3 个裸位,
    /// 读回来一致 (有回读测试钉着)。
    /// </summary>
    private static int BroadcastTeamFinished(GameServer network, TeamId team) =>
        BroadcastTeamFinished(network, team, Placement.First);

    /// <summary>发一条 <c>TeamFinished</c> (队 3 位 + 名次 3 位 + finishNow)。拾荒按名次每队发一条。</summary>
    public static int BroadcastTeamFinished(GameServer network, TeamId team, Placement placement)
    {
        int sent = 0;

        foreach (Client recipient in network.ConnectedClientsList.OfType<Client>())
        {
            _0023rye._00237ze buffer = network.CreateGameMessage(16);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.TeamFinished);

            buffer.Write((uint)team, 3);
            buffer.Write((uint)placement, 3);
            buffer.Write(true);            // finishNow —— 立刻收尾, 不等别的队

            network.SendGameMessage(buffer, recipient);
            sent++;
        }

        return sent;
    }

    /// <summary>
    /// 发任务完成评分, 走和路径点语音同一条 <c>ObjectiveUpdate</c>。
    ///
    /// <c>missionNr</c> 用地图序号 —— 客户端拿它在任务栏上定位是第几关。
    /// </summary>
    private static int BroadcastMissionScore(GameServer network, GameManager simulation)
    {
        // ## missionNr 是任务栏的第几格 (1..3), 不是地图编号
        //
        // 客户端 UI_ObjectivesCrossroads.ShowMissionComplete (libgui.dll) 做的是
        // `MissionStars[missionNr - 1]`, 数组长度 3。以前这里发 simulation.MapIndex ——
        // 十字路口 map26 = 44 -> 下标 43 越界; 而 GUI_ObjectiveManager 在抛异常前没把这条
        // 事件移出队列, 于是**每帧重试** (玩家实测客户端日志: 同一个 IndexOutOfRange 611 次)。
        // Scout 一局一个任务, 就是第 1 格。
        CrossroadsGUIUpdate update = new CrossroadsGUIUpdate();
        update.AddResultData(CompleteScore.Gold, 1);

        int sent = 0;

        foreach (Client recipient in network.ConnectedClientsList.OfType<Client>())
        {
            _0023rye._00237ze buffer = network.CreateGameMessage(64);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.ObjectiveUpdate);
            update.Serialize(buffer);

            network.SendGameMessage(buffer, recipient);
            sent++;
        }

        return sent;
    }
}
