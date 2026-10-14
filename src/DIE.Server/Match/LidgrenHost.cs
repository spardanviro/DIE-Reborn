using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using ConductorCrafting;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;
using _0023rye;
using _0023bd;
using _0023z;
using StunGameNetwork;
using _0023iae;
using Lidgren.Network;
using Serilog;

namespace DIE.Server.Match;

/// <summary>
/// 一局对局的 UDP 端点 —— 游戏自带那份 Lidgren 的服务端薄壳。
///
/// ## 为什么要自己写这一层
///
/// 客户端只发布了 <c>NetClient</c> (<c>-iae\-lce.cs : -gce.cs</c>), 没有 <c>NetServer</c>。
/// 但 <c>NetServer</c> 在 Lidgren 里本来就只是 <c>NetPeer</c> 的薄壳 —— 真正的受理逻辑
/// 全在 NetPeer 中 (<c>-gce.cs:916</c> 的 <c>case NetMessageType.Connect</c>、
/// "Server full" 限流、<c>AcceptConnection</c>)。所以这里直接用 NetPeer。
///
/// ## 混淆名对照 (查过 -iae\ 的源码, 不是猜的)
///
///   _0023gce = NetPeer            _0023nfe = NetPeerConfiguration
///   _0023cee = NetIncomingMessage _0023tde = NetConnection
///   _0023oee = NetIncomingMessageType
///   peer.Start() / Update() / _0023Jbe()=ReadMessage / _0023cce()=Recycle
///   msg._00239de()=MessageType   msg._0023bee()=SenderConnection  msg._0023Qae()=ReadString
///   conn._0023oce=本条消息的状态   conn._00234ce()=RemoteHailMessage  conn._0023pce=RemoteEndPoint
///
/// ## 配置必须和客户端对齐
///
/// AppIdentifier 不一致的话 Lidgren 在握手第一步就静默丢包, 客户端只会超时,
/// 没有任何错误信息 —— 是最难查的一类故障。客户端那边的取值来自
/// <c>-rye\-QDe.cs:88</c> 起, 逐条抄在下面。
///
/// 一处**必须与客户端不同**: <c>AcceptIncomingConnections</c> 默认是 false,
/// 服务端要显式打开, 否则 NetPeer 收到 Connect 也不受理。
///
/// ## 3C: 打开 ConnectionApproval
///
/// 默认禁用掩码是 4230 = ConnectionApproval(4) | UnconnectedData(2) |
/// VerboseDebug(128) | ConnectionLatencyUpdated(4096)。禁用时 NetPeer 自动批准,
/// 3B 就是靠这个连上的。现在要读客户端的身份数据, 所以清掉 ConnectionApproval 那一位。
///
/// 这份 Lidgren **没有 <c>NetConnection.Approve()</c>** —— 客户端从不批准连接,
/// 所以那个方法被裁掉了。原版 Approve(hail) 做三件事:
///
///   m_localHailMessage = hail;  m_handshakeAttempts = 0;  SendConnectResponse(now, false);
///
/// 对应到混淆名就是 <c>_0023sce</c> / <c>_0023Fce</c> / <c>_00238ce</c> ——
/// 见 <c>-tde.cs:637</c> 那段 (审批禁用时它直接调 <c>_00238ce</c>, 审批开启时
/// 转入 RespondedAwaitingApproval 等我们调)。所以这里是**照抄原版 Approve 的实现**,
/// 不是自创流程。
/// </summary>
public sealed class LidgrenHost : IDisposable
{
    /// <summary>
    /// Lidgren 的应用标识。**必须**逐字等于客户端的 <c>new _0023nfe("StunNet")</c>
    /// (`-rye\-QDe.cs:88`), 否则握手包会被对端当成别的应用直接丢弃。
    /// </summary>
    private const string AppIdentifier = "StunNet";

    // 以下四项抄自客户端配置, 保持一致 —— 缓冲区大小影响单包上限, ping 间隔影响
    // 超时判定, 握手重试次数影响弱网下的连接成功率。
    private const int ReceiveBufferSize = 65536;
    private const int SendBufferSize = 65536;
    private const float PingInterval = 1f;
    private const int MaximumHandshakeAttempts = 8;

    /// <summary>
    /// 上限。一局最多 4 名玩家 (Scavenger 是 3 队 × 4 人 = 12), 另留观战位。
    /// Lidgren 默认 32, 这里收紧到 16 —— 与 SynchronizableData 的每客户端激活位图
    /// 宽度一致 (那是个 ushort, 即最多 16 个客户端)。
    /// </summary>
    private const int MaximumConnections = 16;

    /// <summary>逻辑帧率。传给 NetworkBase 当 TargetDelta = 1/30s。</summary>
    private const float LogicFps = 30f;

    /// <summary>客户端在握手里报的版本串, 目前只用于构造 GameManager。</summary>
    private const string GameHash = "0.8.5.38860";

    private readonly _0023gce _peer;
    private readonly Thread _pump;
    private volatile bool _running;

    /// <summary>别的线程要在泵线程上做的事 (见 <see cref="RunOnPump{T}"/>)。</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _pumpWork =
        new System.Collections.Concurrent.ConcurrentQueue<Action>();

    private int _pumpThreadId;

    /// <summary>
    /// 在泵线程上执行 <paramref name="work"/> 并等结果。泵没在跑、或者本来就在泵线程上时直接执行。
    /// 等超过 <paramref name="timeout"/> 就返回 <paramref name="fallback"/> (活儿仍然排在队里, 稍后照做)。
    /// </summary>
    private T RunOnPump<T>(Func<T> work, TimeSpan timeout, T fallback)
    {
        if (!_running || !SimulationReady || Environment.CurrentManagedThreadId == _pumpThreadId)
        {
            return work();
        }

        TaskCompletionSourceLite<T> done = new TaskCompletionSourceLite<T>();
        _pumpWork.Enqueue(() =>
        {
            try
            {
                done.Set(work());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "战斗服 UDP {Port} 泵线程上执行跨线程请求出错", Port);
                done.Set(fallback);
            }
        });

        if (done.Wait(timeout, out T result))
        {
            return result;
        }

        Log.Warning("战斗服 UDP {Port} 等泵线程超过 {Seconds} 秒, 先按默认值回应", Port, timeout.TotalSeconds);
        return fallback;
    }

    private sealed class TaskCompletionSourceLite<T>
    {
        private readonly ManualResetEventSlim _set = new ManualResetEventSlim(false);
        private T _value;

        public void Set(T value)
        {
            _value = value;
            _set.Set();
        }

        public bool Wait(TimeSpan timeout, out T value)
        {
            bool ok = _set.Wait(timeout);
            value = ok ? _value : default;
            return ok;
        }
    }

    private void DrainPumpWork()
    {
        while (_pumpWork.TryDequeue(out Action work))
        {
            work();
        }
    }

    /// <summary>连接 -> 客户端槽位。索引要稳定, 因为 hail 里发出去之后客户端就按它索引自己。</summary>
    private readonly Dictionary<_0023tde, int> _slots = new Dictionary<_0023tde, int>();

    /// <summary>
    /// 账号 -> 他在这一局里的角色。
    ///
    /// 掉线重连要的就是这张表: 人回来时**接回原来那个实体**, 而不是再生成一个
    /// (那样世界里会站着两个他, 装备和进度都在旧的那个身上)。
    /// 掉线后条目**留着** —— 正因为留着才能接回去。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, Player> _playersByUser = new();

    /// <summary>账号 -> 当前占着的槽位。匹配服要据此判断"他还在局里吗"。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, int> _connectedUsers = new();

    /// <summary>明确说了"我不打了"的账号 (重连弹窗上点了离开) —— 不再提示他重连。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _abandoned = new();

    /// <summary>
    /// 每个连接一对可靠消息通道 (有序 / 无序), 与客户端的 _0023aCe / _0023bCe 对称。
    /// 游戏消息不是裸跑在 Lidgren 上的 —— 它们经这一层做分片、排序和重传。
    /// </summary>
    private readonly Dictionary<_0023tde, ClientChannels> _channels =
        new Dictionary<_0023tde, ClientChannels>();

    private sealed class ClientChannels
    {
        public readonly _00238ze Ordered = new _00238ze();
        public readonly _0023mze Unordered = new _0023mze();

        public ClientChannels()
        {
            // 有序通道每发一条消息都会回调 _0023jze 通知"正在发送"。它是个
            // **公开字段而不是事件**, 而且底层调用点没做空判:
            //
            //   -9ze.cs:242  if (_0023jze != null) _0023jze(...)     <- 判的是外层
            //   -8ze.cs:23   _0023jze(P_0, P_2);                     <- 内层, 没判
            //
            // 外层不为 null (构造函数里就挂上了那个 lambda), 内层才是我们的。
            // 不挂就是每发一条消息抛一次 NullReferenceException —— 异常被
            // -9ze.cs:245 吞掉并打成 "Reliability.OnSendingMessage exception",
            // 消息照发, 所以功能不受影响, 但日志会被淹掉。
            //
            // 真机上僵尸死一次刷四五条, 正经的诊断信息全被冲走了。
            //
            // 我们不需要发送通知 (那是客户端做重传统计用的), 挂个空实现即可。
            Ordered._0023jze = (buffer, sequence) => { };
        }

        /// <summary>
        /// 客户端是否已经报过 <c>LoadingComplete</c>。
        ///
        /// **在此之前绝不能发世界状态。** 客户端的同步表要跑完 CacheGame/LoadMap
        /// 才建起来, 而那发生在连接之后 —— 一连上就灌数据的话, 它拿着空表去解,
        /// 索引全部越界。
        ///
        /// 实测过: 不等这个信号会刷出 243 条
        /// "Client-Server synchronizable mismatch! Server: 8192, Client: 0"
        /// 外加同样数量的 ArgumentOutOfRangeException。
        /// </summary>
        public bool LoadingComplete;
    }

    private readonly string _gameRoot;
    private readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();

    /// <summary>泵线程分段计时, 每 <see cref="ProfileWindowSeconds"/> 秒汇总一行进日志。见 <see cref="FrameProfiler"/>。</summary>
    public FrameProfiler Profiler { get; } = new FrameProfiler();

    /// <summary>性能汇总的间隔。</summary>
    public static double ProfileWindowSeconds { get; set; } = 30.0;

    private double _lastProfile;
    private readonly int _profNet;
    private readonly int _profHousekeeping;
    private readonly int _profSpawners;
    private readonly int _profStory;
    private readonly int _profCrossroads;
    private readonly int _profZombieAI;
    private readonly int _profOtherAI;
    private readonly int _profMisc;

    /// <summary>上次打模拟状态的时间, 用来把诊断限成每秒一条。</summary>
    private double _lastDiagnostic;

    /// <summary>本局的模拟层。**只能在泵线程上碰** —— 见 BuildSimulation 的说明。</summary>
    private DIE.Server.Game.GameManagerServer _sim;
    private DIE.Server.Game.GameServer _network;

    /// <summary>僵尸 AI 和刷怪触发。和 <c>_sim</c> 一样只在泵线程上碰。</summary>
    private readonly DIE.Server.Game.ZombieBrain _zombieBrain = new DIE.Server.Game.ZombieBrain();

    /// <summary>每个人带进来的消耗品和用掉了几个 (见 ConsumableLedger)。</summary>
    private readonly DIE.Server.Game.ConsumableLedger _consumables = new DIE.Server.Game.ConsumableLedger();
    private readonly DIE.Server.Game.CastInterruptProbe _castProbe = new DIE.Server.Game.CastInterruptProbe();
    private readonly DIE.Server.Game.RoamingZombieDirector _zombieDirector =
        new DIE.Server.Game.RoamingZombieDirector();

    private readonly DIE.Server.Game.EventSpawnDirector _eventSpawns =
        new DIE.Server.Game.EventSpawnDirector();

    /// <summary>路障打破的经验奖励。原厂那条路对非 Character 不通, 详见类说明。</summary>
    private readonly DIE.Server.Game.DestructibleRewards _destructibleRewards =
        new DIE.Server.Game.DestructibleRewards();
    private readonly DIE.Server.Game.MinionBrain _minionBrain = new DIE.Server.Game.MinionBrain();

    /// <summary>其余召唤物 (XianMei 分身、Allie 幽灵、Rico 的 Marco、Bryce 大招孢子) 的脑子。见 CompanionBrain。</summary>
    private readonly DIE.Server.Game.CompanionBrain _companionBrain = new DIE.Server.Game.CompanionBrain();

    /// <summary>基地机枪的眼睛 —— 十字路口每张图 8 座, Scout/教程 0 座。</summary>
    private readonly DIE.Server.Game.TurretBrain _turretBrain = new DIE.Server.Game.TurretBrain();

    private readonly DIE.Server.Game.TutorialNpcAI _tutorialNpcAI = new DIE.Server.Game.TutorialNpcAI();

    /// <summary>开场躺沙滩 + 醒来动画期间锁住操作 —— 顺带治了 SPACE 连带翻滚。</summary>
    private readonly DIE.Server.Game.TutorialIntroLock _introLock =
        new DIE.Server.Game.TutorialIntroLock();

    /// <summary>补给点占领: 读条读完之后的刷怪 → 清光 → 掉补给箱。</summary>
    private readonly DIE.Server.Game.StorageRoomCaptureDirector _capture =
        new DIE.Server.Game.StorageRoomCaptureDirector();

    /// <summary>
    /// Horde 图的占领点 (<c>Event_Horde</c> -&gt; <c>HordeSpawner</c>) 和它锁着的
    /// 路障。教程图上没有这类对象, Scan 会直接返回。
    /// </summary>
    private readonly DIE.Server.Game.HordeSpawnerDirector _hordeSpawners =
        new DIE.Server.Game.HordeSpawnerDirector();

    /// <summary>
    /// Starter Mission / Raid 图的推进式复活点。教程和 Horde 图上没有
    /// <c>Spawn_RaidRespawn</c>, Scan 会登记 0 个然后什么都不做。
    /// </summary>
    private readonly DIE.Server.Game.RaidRespawnDirector _raidRespawns =
        new DIE.Server.Game.RaidRespawnDirector();

    /// <summary>
    /// Scout 图上那些没有事件认领的刷怪点 —— 玩家走近才出场。
    /// 教程图和 Horde 图上一个都不碰 (那些归 EventSpawnDirector)。
    /// </summary>
    private readonly DIE.Server.Game.ProximitySpawnDirector _proximitySpawns =
        new DIE.Server.Game.ProximitySpawnDirector();

    /// <summary>
    /// Starter Mission 的剧情节点: Ram 撞门 + Boss Looter。
    /// 别的图上认不到触发点, Scan 之后 IsArmed 为 false, Update 什么都不做。
    /// </summary>
    private readonly DIE.Server.Game.ScoutStoryDirector _scoutStory =
        new DIE.Server.Game.ScoutStoryDirector();

    /// <summary>Scout 的主线交互点 (敌方卡车 / 补给点)。见 ScoutObjectiveDirector。</summary>
    private readonly DIE.Server.Game.ScoutObjectiveDirector _scoutObjective =
        new DIE.Server.Game.ScoutObjectiveDirector();

    /// <summary>
    /// Scout 收尾 —— 补给交够目标量就发 TeamFinished, 客户端自己弹计分板。
    /// 非 Scout 模式下 Update 第一句就返回。
    /// </summary>
    private readonly DIE.Server.Game.ScoutFinaleDirector _scoutFinale =
        new DIE.Server.Game.ScoutFinaleDirector();

    /// <summary>
    /// 十字路口的局内流程 —— 领任务 / 事件刷怪 / 回交 / 夜袭收尾。
    /// 别的模式下 Update 第一句就返回 (它认 RaidMission)。
    /// </summary>
    private readonly DIE.Server.Game.CrossroadMissionDirector _crossroads =
        new DIE.Server.Game.CrossroadMissionDirector();

    /// <summary>拾荒 (4v4v4) 局内流程: 出生区路障竞速 -> 补给点 -> 卡车 -> 入夜 -> 胜负。只在拾荒图上生效。</summary>
    private readonly DIE.Server.Game.ScavengerDirector _scavenger = new DIE.Server.Game.ScavengerDirector();

    /// <summary>十字路口的认输投票。见 <see cref="DIE.Server.Game.ConcedeVote"/>。</summary>
    private readonly DIE.Server.Game.ConcedeVote _concede = new DIE.Server.Game.ConcedeVote();

    /// <summary>
    /// 路径点语音 —— 玩家走进 TransmissionSpawnPoint 的区域就播一段。
    /// 教程图上没有这类点, Scan 登记 0 个。
    /// </summary>
    private readonly DIE.Server.Game.TransmissionDirector _transmissions =
        new DIE.Server.Game.TransmissionDirector();

    /// <summary>
    /// 教程走完之后过多久把玩家送走。<c>&lt; 0</c> 表示还没走完。
    /// 见 <see cref="TutorialFarewellSeconds"/>。
    /// </summary>
    private double _tutorialOverAt = -1d;

    /// <summary>
    /// 教程进入终态之后, 等这么久再断开客户端。
    ///
    /// ## 为什么服务端得管这件事
    ///
    /// 原厂是客户端自己退的 (-bd/-5cc.cs:162-175):
    ///
    ///     if (状态 >= _0023ncc &amp;&amp; _0023Fcc &amp;&amp; GetScreenFade() != null
    ///         &amp;&amp; GetScreenFade().GetAlpha() >= 1f)
    ///     { _0023Fcc = false; _0023Icc = 5f; 播 Sinamoi 离场音效 }
    ///     if (!_0023Fcc &amp;&amp; 状态 >= _0023ncc)
    ///     { _0023Icc -= dt; if (_0023Icc &lt;= 0) ShutDownApplication(); }
    ///
    /// 门槛是**淡黑要真的淡到底** (alpha ≥ 1)。玩家实测: "语音播报说结束了,
    /// UI 也消失了, 但是没有结束教程然后退出到主菜单" —— 也就是说
    /// <c>ToggleHUD(false)</c> 和音效那两条跑了 (状态 24 确实到了, 服务端日志
    /// 01:44:15 印证), 但 <c>GetScreenFade()</c> 那一环没成立, 于是
    /// <c>_0023Fcc</c> 一直是 true, 第二个 if 永远进不去。
    ///
    /// 淡入淡出是纯客户端的东西, 服务端看不见也修不了。所以由服务端兜底:
    /// 到点直接断开, 客户端离开对局回到主菜单。
    ///
    /// ## 20 -> 8 -> 35 -> 6 秒, 绕了一圈
    ///
    /// 2026-08-06 那局我读成"客户端自己退成功了, 我抢在了它前面", 于是
    /// 从 8 秒拉到 35 秒当兜底。**2026-08-07 的日志把这个结论推翻了**:
    ///
    ///     03:57:48.270  教程推进 -> "_0023ncc"      (终态)
    ///     03:58:23.272  教程结束, 已送走 1 个客户端   ← 35 秒后, 是我们踢的
    ///     03:58:23.288  客户端已断开
    ///
    /// **整整 35 秒客户端一动不动。** 它那条 <c>ShutDownApplication</c> 链确实
    /// 是断的 (要求 <c>GetScreenFade().GetAlpha() >= 1f</c>, 而画面从没黑过 ——
    /// 玩家全程只报"UI 消失", 没提过淡出)。8/6 那次 18 秒的断开是别的原因,
    /// 不是它自己退。
    ///
    /// 教训: **一次时序巧合不足以推翻一个结论。** 那次断开时间和原厂节奏
    /// (8+5=13 秒) 接近, 我就认定链路是通的; 这次拉到 35 秒才让"它根本不会自己退"
    /// 显形 —— 把等待拉长本身就是一次有效的对照实验。
    ///
    /// 既然只能靠服务端踢, 那就踢得干脆点: 6 秒够客户端把 Sinamoi 的离场音效
    /// 播完 (状态 24 那一句 <c>PlayGamemodeSound</c>)。玩家实测的"游戏结束语音后
    /// 没有马上结束, 拖很久"就是那 35 秒。
    ///
    /// 踢的**姿势**同样重要 —— 见 <see cref="MatchOverDisconnectReason"/>。
    /// </summary>
    public static double TutorialFarewellSeconds { get; set; } = 6d;

    /// <summary>
    /// 断开时告诉客户端"这局正常结束了", 而不是"你掉线了"。
    ///
    /// ## 那个"Connecting / Wait for reconnect or exit game"面板是我们自己招来的
    ///
    /// 客户端的断开原因走的是一个**带前缀数字**的字符串协议
    /// (<c>StunGameNetwork/GameClient.cs:505-520</c>):
    ///
    ///     取第一个空格前的那段 -> int.TryParse -> (DisconnectReasons)result
    ///     parse 失败就是 0 = UnknownDisconnectReason
    ///
    /// 我先前发的是 <c>"tutorial complete"</c> —— parse 失败, 落进
    /// <c>UnknownDisconnectReason</c>。而 <c>UnityClient.cs:1273</c> 恰恰把
    /// 这个值和 <c>AuthenticationTimeout</c> 一起当成"可能还能救回来":
    ///
    ///     if (!ReconnectTimeouted &amp;&amp; (原因 == AuthenticationTimeout
    ///                                || 原因 == UnknownDisconnectReason))
    ///         DisconnectedPanel.Show(enabled: true, _GameHUD);   // ← 等待重连
    ///     else
    ///         DisconnectedPanel.Show(..., isReconnecting: false, afk, matchOver);
    ///
    /// <c>MatchOverKicked</c> = **9** 走的是 else 那支: 不重连, 文案是
    /// <c>PCStrings.Crib.Warnings.MatchAlreadyEnded</c> (LoadingScreen.cs:650),
    /// 而且 <c>if (flag || flag2) { 隐藏连接状态栏; return; }</c> —— 连
    /// "Connecting" 那两行都不显示。
    ///
    /// 空格后面的文本会进 <c>GameDisconnectReason</c>, 只用于日志。
    /// </summary>
    public const string MatchOverDisconnectReason = "9 Tutorial complete";

    /// <summary>
    /// 票认得出来却不在这一局名单上时的断开原因。同样用 <c>MatchOverKicked</c> (9) 开头 ——
    /// 走"对局已结束"那支, 不会让客户端弹"等待重连" (上面那个常量的说明)。
    /// </summary>
    public const string NotInMatchDisconnectReason = "9 Not in this match";

    /// <summary>教程终局: 补给交到卡车上之后不停刷怪, 直到状态机收工。</summary>
    private readonly DIE.Server.Game.TutorialFinaleDirector _finale =
        new DIE.Server.Game.TutorialFinaleDirector();

    /// <summary>把玩家登记进补给表 —— 不登记他捡多少补给都是 0。</summary>
    private readonly DIE.Server.Game.SupplyRegistrar _supplies =
        new DIE.Server.Game.SupplyRegistrar();

    /// <summary>复活之后补发装备 —— 不补的话客户端手上是空的, 详见类说明。</summary>
    private readonly DIE.Server.Game.RespawnFinisher _respawnEquip =
        new DIE.Server.Game.RespawnFinisher();

    /// <summary>
    /// 兜底: 活着的普通玩家, 视野中心必须是他自己 —— 偏了就拨回来。
    /// 玩家实测过"刚进游戏就相机不跟人, 得退出重进"。见 <see cref="DIE.Server.Game.CameraTargetGuard"/>。
    /// </summary>
    private readonly DIE.Server.Game.CameraTargetGuard _cameraTarget =
        new DIE.Server.Game.CameraTargetGuard();

    private readonly DIE.Server.Game.AmbushDirector _ambushes = new DIE.Server.Game.AmbushDirector();
    private readonly DIE.Server.Game.TutorialDirector _tutorialDirector =
        new DIE.Server.Game.TutorialDirector();

    /// <summary>一人局 —— Solo 开局和单测走这个。</summary>
    public LidgrenHost(byte[] matchGuid, int port, int mapIndex = 0, ulong ownerUserId = 0,
                       string gameRoot = null, DIE.Server.Data.PlayerChoice? choice = null,
                       int accountLevel = 1, int infectionLevel = 0)
        : this(matchGuid, port, mapIndex, MatchRoster.Solo(ownerUserId, choice, accountLevel),
               gameRoot, infectionLevel)
    {
    }

    /// <param name="roster">这一局的名单。进场时凭会话票对号入座, 见 <see cref="MatchRoster"/>。</param>
    /// <param name="infectionLevel">感染等级; 0 = 按全员装的武器现算。</param>
    /// <param name="queueType">
    /// 这一局属于哪个队列。战斗服自己不用它, 是匹配服那侧要 (观战列表要报模式,
    /// 离局惩罚要放过教程)。地图其实也能推出模式, 但队列是开局时就确定的事实, 别再推一遍。
    /// </param>
    /// <param name="crossroadDifficulty">
    /// 十字路口的难度档 (0 简单 … 4 极难), 别的模式给 0。开局后设到模式上,
    /// 见 <see cref="DIE.Server.Game.RaidMissionServer"/>。
    /// </param>
    public LidgrenHost(byte[] matchGuid, int port, int mapIndex, MatchRoster roster,
                       string gameRoot = null, int infectionLevel = 0,
                       ConductorCrafting.QueueType queueType = ConductorCrafting.QueueType.None,
                       int crossroadDifficulty = 0)
    {
        QueueType = queueType;
        CrossroadDifficulty = crossroadDifficulty;
        Roster = roster ?? throw new ArgumentNullException(nameof(roster));

        // 没传就按全员装的武器现算 —— 测试和 FakeClient 都走这条路。
        int infection = infectionLevel > 0
            ? DIE.Server.Game.InfectionLevel.Clamp(infectionLevel)
            : DIE.Server.Game.InfectionLevel.FromWeapons(
                roster.Members.SelectMany(m => m.Choice?.WeaponSchematicIds ?? Array.Empty<ushort>()).ToList());

        // ## 十字路口: 感染等级必须落在玩家选的那一档里
        //
        // 原厂算掉落品阶上限是拿感染等级**反推**难度档的
        // (RewardManager.GetMaxTier -> CraftingManager.GetDifficulty)。我们的感染
        // 等级按装备品阶自动定, 和大厅里选的档没关系 —— 于是选 Normal 档却带着
        // 16 阶装备时, 原厂会反推成 Endemic, 那一档的掉落上限 (4 阶) 等于没有。
        //
        // 夹进区间之后两边就对上了, 而且同一档内仍然"装备越好僵尸越强"。
        // 区间来自原厂那张表, 见 DIE.Server.Game.CrossroadDifficulty.InfectionRange。
        InfectionLevel = queueType == QueueType.RaidMission
            ? DIE.Server.Game.CrossroadDifficulty.ClampInfection(infection, crossroadDifficulty)
            : infection;
        MatchGuid = matchGuid;
        Port = port;
        MapIndex = mapIndex;
        _gameRoot = gameRoot;

        _0023nfe config = new _0023nfe(AppIdentifier);
        config._0023Qee = port;                             // Port —— 只有字段, 没有公开 setter
        config._0023Iee = MaximumConnections;               // MaximumConnections, 同上
        config._0023ife(true);                              // AcceptIncomingConnections, 默认 false
        config._0023ffe(ReceiveBufferSize);
        config._0023hfe(SendBufferSize);
        config._0023jfe(PingInterval);
        config._0023kfe(MaximumHandshakeAttempts);

        // ## 不要 Lidgren 自带的网络线程 —— 它是个空转循环, 一局烧满一个核
        //
        // 这个开关 (原版叫 NetPeerConfiguration 里的"自带线程") 默认是 true
        // (-nfe.cs:64), 开着时 Start() 会起一个线程跑
        //
        //     do { Heartbeat(); } while (Status == Running);      // -gce.cs:552
        //
        // 而心跳里读 socket 用的是 Poll(0, SelectRead) (-gce.cs:717) —— **零超时**,
        // 不阻塞也不睡。原版 Lidgren 这里是 Poll(1000) 等 1ms, 游戏这份改成了 0,
        // 因为客户端就是这么用的 (-QDe.cs:93 也开着它), 玩家电脑多烧一个核没人在意。
        //
        // 放到服务器上就是: **每开一局, 一个核 100%**。打完的局要空 5 分钟才回收
        // (MatchHostRegistry.IdleTimeout), 这期间照烧。云上报的"平时 25%、三个人
        // 也会突然冲到 100% 卡顿", 在 4 核机器上正好是"一局 = 一个核"。
        //
        // 关掉之后改成手动泵: 我们的泵线程每轮调 _peer.Update(), 它在非线程模式下
        // 就是跑一次同样的心跳 (-gce.cs:585)。收发都在泵线程上, 也省掉了跨线程。
        // 压测 (tests\DIE.FakeClient, 3 个假客户端打一局十字路口) 实测: 修之前每个 Lidgren
        // 端点一个核 100%, 修之后整个进程平均约 3% 单核, 泵线程忙 1~2%。
        // 回归测试见 LidgrenHostCpuTests。
        config._00233ee = false;

        // 清掉禁用掩码里的 ConnectionApproval 位。config 没给 EnableMessageType 之类的
        // 公开方法 (客户端用不到), 只能直接改字段 —— 语义与原版 EnableMessageType 一致。
        config._0023Pee &= ~_0023oee._0023fee;

        _peer = new _0023gce(config);
        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = $"lidgren-{port}",
        };

        _profNet = Profiler.Register("收包");
        _profHousekeeping = Profiler.Register("诊断日志");
        _profSpawners = Profiler.Register("刷怪");
        _profStory = Profiler.Register("剧情目标");
        _profCrossroads = Profiler.Register("十字路口");
        _profZombieAI = Profiler.Register("僵尸AI");
        _profOtherAI = Profiler.Register("其余AI");
        _profMisc = Profiler.Register("杂项");
    }

    public byte[] MatchGuid { get; }

    public int Port { get; }

    /// <summary>
    /// 十字路口的难度档 (0 简单 … 4 极难)。别的模式恒 0。
    ///
    /// 它已经在挑图时用过一次 (每张十字路口的图只属于某几档), 这里再设到模式上
    /// 是为了下发给客户端显示。
    /// </summary>
    public int CrossroadDifficulty { get; }

    /// <summary>本局地图。决定游戏模式 —— 客户端拿它去 GameMode.ReadSettings 读地图文件。</summary>
    public int MapIndex { get; }

    /// <summary>这一局的名单 (谁能进、各自选了什么、坐在哪个槽位)。</summary>
    public MatchRoster Roster { get; }

    /// <summary>名单上第一位 (队长 / Solo 的本人) 的账号 —— 只用于日志。</summary>
    public ulong OwnerUserId => Roster.Owner.UserId;

    /// <summary>这个账号现在正连着这一局吗。</summary>
    public bool IsConnected(ulong userId) => userId != 0 && _connectedUsers.ContainsKey(userId);

    /// <summary>此刻连着这一局的账号 (控制台的玩家列表用)。</summary>
    public IReadOnlyCollection<ulong> ConnectedUserIds => _connectedUsers.Keys.ToArray();

    /// <summary>这一局的战斗服是什么时候建的 (UTC)。</summary>
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>
    /// 这个账号能不能重连回这一局。
    ///
    /// 条件: 在名单上 (或者他的角色还在场) + 这一局还没打完 + 他没说"我不打了" + 现在没连着。
    /// 匹配服拿它回答 <c>GetActiveMatchRequest</c> —— 回 true 客户端就弹重连框。
    /// </summary>
    public bool CanRejoin(ulong userId)
    {
        if (userId == 0 || MatchOver || !SimulationReady || _abandoned.ContainsKey(userId))
        {
            return false;
        }

        if (IsConnected(userId))
        {
            return false;
        }

        return _playersByUser.ContainsKey(userId)
               || Roster.Members.Any(m => m.UserId == userId);
    }

    /// <summary>
    /// 他点了"离开": 以后不再提示他重连, 角色也从经验均摊里摘掉。
    ///
    /// 到这时为止用掉的消耗品当场扣掉并返回 —— 原厂放弃对局的回应里带 <c>ConsumablesLost</c>,
    /// 客户端照着从背包里减 (GUI_RejoinGamePopup.cs:384)。
    /// </summary>
    public IReadOnlyList<(ushort Id, int Used)> MarkAbandoned(ulong userId)
    {
        if (userId == 0)
        {
            return Array.Empty<(ushort, int)>();
        }

        // 匹配服在自己的 TCP 线程上调这里, 而消耗品账挂在玩家技能栏上、模拟线程每帧在改 ——
        // 交给泵线程做, 等它做完拿结果 (2026-09-17 审校)。泵没在跑 (还没起来 / 已关) 就地做。
        return RunOnPump(() => MarkAbandonedNow(userId), AbandonWaitTimeout, Array.Empty<(ushort, int)>());
    }

    /// <summary>等泵线程处理"放弃对局"最多多久; 超时就先回空的消耗品清单 (那件事照样会在泵线程上做完)。</summary>
    private static readonly TimeSpan AbandonWaitTimeout = TimeSpan.FromSeconds(2);

    private IReadOnlyList<(ushort Id, int Used)> MarkAbandonedNow(ulong userId)
    {
        _playersByUser.TryGetValue(userId, out Player player);
        IReadOnlyList<(ushort Id, int Used)> used = SpendConsumablesOf(userId, player);

        _abandoned[userId] = 0;
        _playersByUser.TryRemove(userId, out _);
        Log.Information("战斗服 UDP {Port} 账号 {UserId} 明确放弃了这一局", Port, userId);
        return used;
    }

    /// <summary>把某人从上次扣账到现在用掉的消耗品落库, 返回扣了哪些。</summary>
    private IReadOnlyList<(ushort Id, int Used)> SpendConsumablesOf(ulong userId, Player player)
    {
        IReadOnlyList<(ushort Id, int Used)> used = _consumables.TakeUsed(userId, player);
        if (used.Count > 0)
        {
            SpendConsumables?.Invoke(userId, used);
            Log.Information("战斗服 UDP {Port} 账号 {UserId} 用掉消耗品 [{Used}], 已从背包扣除", Port, userId,
                string.Join(", ", used.Select(u => $"{u.Id}x{u.Used}")));
        }

        return used;
    }

    /// <summary>从某人背包里扣掉用掉的消耗品。由建战斗服的人接上 —— 战斗服自己不碰账号库。</summary>
    public Action<ulong, IReadOnlyList<(ushort Id, int Used)>> SpendConsumables { get; set; }

    /// <summary>这一局属于哪个队列 (匹配服那侧用, 见构造函数的参数说明)。</summary>
    public ConductorCrafting.QueueType QueueType { get; }

    /// <summary>
    /// 这一局已经打完了 (任务完成 / 教程终局结束)。
    ///
    /// 匹配服据此判断"还值不值得让人重连回来" —— 打完的局不该再弹重连框, 也不算中途放弃。
    /// 泵线程写、匹配线程读, 所以是 volatile。
    /// </summary>
    public bool MatchOver => _matchOver;

    private volatile bool _matchOver;

    /// <summary>
    /// 凭会话票查账号 (<c>AccountStore.ResolveByAuthTicket</c>)。
    ///
    /// 由建战斗服的人接上 —— 战斗服自己不碰账号库。没接就一律当"查不出",
    /// 一人局照旧按局绑定, 多人局只剩名单上的匿名位可坐。
    /// </summary>
    public Func<byte[], ulong?> ResolveUserByTicket { get; set; }

    /// <summary>
    /// 这个客户端是谁的。按槽位查名单; 没对上号 (观战位等) 就退回队长 ——
    /// 结算宁可记到队长名下, 也不能记到 0 上 (0 会被整段跳过, 见 SendMatchRewards)。
    /// </summary>
    private ulong UserOf(IClient client)
    {
        if (client == null)
        {
            return OwnerUserId;
        }

        MatchMember member = Roster.MemberAt(client.Index);
        return member?.UserId is ulong id && id != 0 ? id : OwnerUserId;
    }

    /// <summary>这个客户端的名单条目; 没对上号时退回队长那一条。</summary>
    private MatchMember MemberOf(IClient client) =>
        (client == null ? null : Roster.MemberAt(client.Index)) ?? Roster.Owner;

    /// <summary>
    /// 把本局挣到的经验交给账号库, 返回累计总量。
    ///
    /// 由建这个战斗服的人接上 (<see cref="MatchHostRegistry.TryCreate"/>) ——
    /// 战斗服自己不碰账号库。没接就是不落库, 不报错。
    /// </summary>
    public Func<ulong, uint, uint> AwardAccountXp { get; set; }

    /// <summary>
    /// 局末把结算数据发给客户端 —— 大厅那边的"打完一局"画面全靠它。
    ///
    /// 客户端收到只是原样存成 <c>lastRewards.data</c>
    /// (GameManagerClient.cs:933), 解析和展示都在大厅进程里
    /// (CribMain.cs:1737 -&gt; PostMatchStatusCheck)。详见
    /// <see cref="DIE.Server.Game.RewardSender"/>。
    ///
    /// 发失败不该影响别的收尾步骤, 所以整段包起来。
    /// </summary>
    private void SendMatchRewards(GameManager simulation, uint earned)
    {
        try
        {
            // ## 掉落全部由原厂算
            //
            // 我们只提供数据 (MatchDropTable) 和翻译 (MatchRewards), 一行掉落
            // 逻辑都不写。上一版走的是自己写的 CurrencyRewards, 那是在还没找到
            // 原厂引擎时的应急方案。
            IReadOnlyList<DIE.Server.Game.PlayerMatchReward> rewards =
                DIE.Server.Game.MatchRewards.Compute(
                    DropTable ?? new DIE.Server.Data.MatchDropTable().Build(),
                    simulation,
                    AccountSnapshot,
                    player => UserOf(player.Client),
                    PlayerExperience,
                    InfectionLevel,
                    simulation.GameMode?.MatchLength ?? 0f,
                    _matchStartUtcTicks,
                    _rewardRandom,
                    player => _consumables.RewardInputFor(UserOf(player.Client), player),
                    _scavenger.IsArmed
                        ? team => new DIE.Server.Game.TeamOutcome(_scavenger.PlacementOf(team),
                            _scavenger.RacePlacementOf(team), _scavenger.MidGamePlacementOf(team))
                        : null,

                    // 十字路口才有难度档 —— 别的队列传 -1, 结算那边就不加成
                    // (原厂 GearPointsBonus: 0/25/50/75/100 percent)。
                    _sim.GameMode is DIE.Server.Game.RaidMissionServer
                        ? CrossroadDifficulty
                        : -1,

                    // 这一局排了哪几类任务。**空着客户端会在结算那一刻 NRE**,
                    // 奖励界面弹不出来、大厅的钱和经验也不刷新 —— 见
                    // CrossroadMissionDirector.MissionTypes 的说明。
                    _crossroads.MissionTypes);

            // 先落库再下发: 结算画面上写着"+N", 那些数必须已经进账,
            // 否则玩家一退大厅就对不上。
            foreach (DIE.Server.Game.PlayerMatchReward reward in rewards)
            {
                if (GrantRewards == null || reward.UserId == 0)
                {
                    continue;
                }

                Log.Information("战斗服 UDP {Port} 账号 {UserId} 局末入账: {Summary}",
                    Port, reward.UserId,
                    GrantRewards(reward.UserId, reward.Data.Rewards, reward.Counters));

                // 拾荒每日首胜: 额外那一栏另外入账 (计数器不动), 记下今天拿过了
                if (reward.ScavengerFirstWin)
                {
                    Log.Information("战斗服 UDP {Port} 账号 {UserId} 拾荒每日首胜入账: {Summary}",
                        Port, reward.UserId,
                        GrantRewards(reward.UserId, reward.Data.FirstWinRewards, null));
                    MarkScavengerFirstWin?.Invoke(reward.UserId);
                }
            }

            // 用掉的消耗品。结算数据里的 Consumable1..3Uses 是同一份账 (客户端据此减本地背包)
            foreach (DIE.Server.Game.PlayerMatchReward reward in rewards)
            {
                if (reward.UserId != 0)
                {
                    SpendConsumablesOf(reward.UserId, reward.Player);
                }
            }

            // 新手任务里"服务端自己判"的那几项 —— Scout / Horde / 打三局。
            // 大厅那边没有任何代码会为它们发请求, 只能在这里推
            // (详见 StarterQuestProgress)。
            //
            // 放在推送刷新**之前** —— 不然大厅拉到的还是旧进度, 面板要等下一次
            // 重连才更新。
            List<ulong> users = PlayersInMatch(simulation)
                .Select(p => UserOf(p.Client))
                .Where(id => id != 0)
                .Distinct()
                .ToList();

            if (AdvanceStarterQuests != null && simulation.GameMode != null
                && simulation.GameMode.IsCompleted)
            {
                foreach (ulong user in users)
                {
                    AdvanceStarterQuests(user, simulation.GameMode.Type);
                }
            }

            rewards = AdvanceCrossroadUnlocks(simulation, rewards);

            int sent = DIE.Server.Game.RewardSender.Send(simulation, _network, rewards);

            Log.Information("战斗服 UDP {Port} 局末结算已下发 {Sent} 份 "
                            + "(账号经验 {Xp}, 感染等级 {Infection}, 载荷 {Bytes} 字节)",
                Port, sent, earned, InfectionLevel,
                DIE.Server.Game.RewardSender.LastPayloadBytes);

            // 落完账通知大厅重新拉一份 —— 它整局都活着, 握的是登录那一刻的
            // 快照, 局末没有任何地方会自己去刷新 (详见
            // LoginHandlers.PushAccountRefresh)。
            int refreshed = 0;
            foreach (ulong user in users)
            {
                refreshed += RefreshAccount?.Invoke(user) ?? 0;
            }

            // ## 推 0 条是**常态**, 不是故障
            //
            // 大厅是轮询式短连接 (连上来拉一次就断, 实测每条只活十几秒), 局末
            // 那一刻往往一条活的都没有。RequestServer.RefreshAccount 会记一笔,
            // 大厅下一轮重连时补推 (LoginHandlers.PushRefreshIfPending), 实测
            // 延迟 7 秒左右。
            //
            // 原来这句只写"已通知 0 条", 读起来像推送失败 —— 玩家 2026-09-22
            // 据此以为"大厅识别有问题"。把兜底写进日志里, 省得下次再照着它误判。
            Log.Information(
                refreshed > 0
                    ? "战斗服 UDP {Port} 已通知 {Count} 条大厅连接刷新账号数据 (账号 {Users})"
                    : "战斗服 UDP {Port} 局末没有活着的大厅连接 ({Count} 条) —— 已记账, "
                      + "等大厅下一轮重连时补推 (账号 {Users})",
                Port, refreshed, string.Join(",", users));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 下发局末结算失败", Port);
        }
    }

    /// <summary>
    /// 这一局用的掉落表。
    ///
    /// 由建战斗服的人传进来 —— 拼一张表要读整份图鉴, 每开一局重拼是浪费。
    /// 没传就现拼一张 (测试和 FakeClient 走这条)。
    /// </summary>
    public ConductorCrafting.DropTable DropTable { get; set; }

    /// <summary>
    /// 取账号侧的奖励输入 (经验、背包里已有的蓝图)。
    ///
    /// 和 <see cref="AwardAccountXp"/> 同一个道理 —— 战斗服不碰账号库,
    /// 由建它的人接上 (<see cref="MatchHostRegistry.TryCreate"/>)。
    /// </summary>
    public Func<ulong, DIE.Server.Game.RewardAccountSnapshot> AccountSnapshot { get; set; }

    /// <summary>
    /// 局末推进新手任务。由建战斗服的人接上 —— 战斗服自己不碰账号库。
    /// </summary>
    public Action<ulong, ConductorGameLogic.GameModes.GameModeType> AdvanceStarterQuests { get; set; }

    /// <summary>
    /// 局末推进十字路口难度解锁 (账号, 这局的难度档, 这个人收集的补给),
    /// **返回这一局之后的进度** —— 不够格就原样返回当前值。
    /// 由建战斗服的人接上 —— 战斗服自己不碰账号库。
    ///
    /// 返回值要塞进结算报文: 客户端只有在奖励界面里才会更新这个数
    /// (<c>GUI_RewardWindow</c>:512 <c>SetCrossroadProgress(rewards.Data.CrossroadProgress)</c>),
    /// 而且它是无条件赋值 —— 发默认值 0 会把玩家本地的进度**抹成 0**。
    /// </summary>
    public Func<ulong, int, int, byte> AdvanceCrossroadProgress { get; set; }

    /// <summary>记下某人今天的拾荒每日首胜已经拿了 (由建战斗服的人接上)。</summary>
    public Action<ulong> MarkScavengerFirstWin { get; set; }

    /// <summary>把原厂算出来的一份奖励落库, 返回一句给日志看的摘要。</summary>
    public Func<ulong, ConductorCrafting.RewardCollectionData, IReadOnlyList<byte>,
                DIE.Server.Data.RewardLedger.GrantSummary> GrantRewards { get; set; }

    /// <summary>
    /// 掉落的随机源。
    ///
    /// 每台战斗服一个, 而不是每局 new —— 连开几局时 new Random() 拿到的
    /// 种子可能相同 (时钟精度), 那会让连续几局掉出一模一样的东西。
    /// </summary>
    private readonly System.Random _rewardRandom = new System.Random();

    /// <summary>
    /// 通知这个账号的大厅重新拉账号数据, 返回推给了几条连接。
    ///
    /// 同样由建战斗服的人接 —— 战斗服够不着 Request 服的连接表。
    /// </summary>
    public Func<ulong, int> RefreshAccount { get; set; }

    /// <summary>全场玩家一共杀了多少僵尸 —— 算银币用。</summary>
    private static int ZombieKillsOf(GameManager simulation)
    {
        if (simulation?.ActivePlayerList == null)
        {
            return 0;
        }

        int kills = 0;

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player != null && player.IsActive)
            {
                kills += player.PlayerScoreStats._0023l9b;
            }
        }

        return kills;
    }

    /// <summary>队伍交到卡车上多少补给 —— 算银币用。</summary>
    private static int SuppliesDeliveredIn(GameManager simulation)
    {
        if (simulation?.GameMode is not _0023bd._0023ad withSupplies)
        {
            return 0;
        }

        ConductorGameLogic.GameModes.Supplies supplies = withSupplies._00239c();
        if (supplies == null || simulation.ActivePlayerList == null)
        {
            return 0;
        }

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player != null && player.IsActive)
            {
                return supplies._0023Rj(player.TeamId);
            }
        }

        return 0;
    }

    /// <summary>这一局是什么时候开的 —— 结算画面上要显示。</summary>
    private readonly long _matchStartUtcTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// 这个玩家本局挣了多少经验。每人一份 —— 组队时各记各的账号。
    ///
    /// ## 读的是原厂那个统计字段, 不自己记账
    ///
    /// 打怪、拆路障、交补给都会走
    /// <c>ChangeStats(player, StatType.Experience, ...)</c> —— 原厂的伤害/奖励
    /// 链路本来就在往那里加 (我们只补了拆路障那一份, 见
    /// <see cref="DIE.Server.Game.DestructibleRewards"/>)。一局下来它的值就是
    /// "这局挣了多少", 不需要我们再开一本账。
    ///
    /// 取向下取整: 经验统计是 float, 账号库那边是整数。
    ///
    /// ## 倍率加在这一层, 不加在局内
    ///
    /// 返回值过 <see cref="DIE.Server.Data.RewardBoost"/> —— 私服自定倍率。
    /// 加在这里而不是加在 <c>ChangeStats(StatType.Experience)</c> 那一侧, 是因为
    /// 局内那个统计还兼着**局内角色等级**的账 (<c>Player.GetLevel</c>): 在源头
    /// 放大会让人一进图就满级, 而玩家要的是**账号**升得快。
    ///
    /// 两条出口都过这个方法, 所以显示和落库是同一个数:
    /// 结算画面那份走 <c>MatchRewards.Compute</c> 的 <c>experienceOf</c> 委托,
    /// 落库那份走 <c>AwardAccountXp</c>。
    /// </summary>
    private static uint PlayerExperience(Player player)
    {
        float xp = player?.Stats.GetValue(StatType.Experience) ?? 0f;
        return xp <= 0f ? 0u : DIE.Server.Data.RewardBoost.Scale((uint)xp);
    }

    /// <summary>这一局在场的玩家 (有客户端的; 观战位和没生成出来的不算)。</summary>
    private static IEnumerable<Player> PlayersInMatch(GameManager simulation) =>
        simulation?.ActivePlayerList?.Where(p => p != null && p.IsActive && p.Client != null)
        ?? Enumerable.Empty<Player>();


    /// <summary>
    /// 这一局的感染等级 —— 难度档, 按玩家装的武器品阶自动定
    /// (详见 <see cref="DIE.Server.Game.InfectionLevel"/>)。
    ///
    /// 它同时是 <c>GameInfo.ServerLevel</c> 下发给客户端的值, 客户端拿它算
    /// 血条上显示的数 (<c>GameManagerClient.GetWalkerHealth</c>) —— 服务端
    /// 和客户端必须用同一个数, 否则显示和手感对不上。
    /// </summary>
    public int InfectionLevel { get; }

    public int ConnectedClients { get; private set; }

    /// <summary>
    /// 从什么时候起没人在线 (UTC); 有人在线时为 null。
    ///
    /// 给 <see cref="MatchHostRegistry"/> 回收空战斗服用 —— 以前一局开完端点永远不关,
    /// 每局漏一个线程 + 一个 UDP 端口 + 一整份模拟。刚起来还没人连也算"空",
    /// 从构造时开始计。泵线程写、回收定时器读, 所以走 Interlocked。
    /// </summary>
    public DateTime? EmptySinceUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _emptySinceTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    private long _emptySinceTicks = DateTime.UtcNow.Ticks;

    /// <summary>模拟层是否已就绪。构建在泵线程上异步进行, 所以 Start() 返回不代表已就绪。</summary>
    public bool SimulationReady { get; private set; }

    /// <summary>
    /// 模拟层构建失败的原因, 成功时为 null。
    ///
    /// 构建跑在泵线程上, 异常只能记日志 —— 而调用方 (测试进程) 未必配了 Serilog sink,
    /// 那样故障就是**静默**的: 表现只是客户端连不上, 完全指不到根因。所以这里留一份。
    /// </summary>
    public Exception SimulationError { get; private set; }

    public void Start()
    {
        _peer.Start();
        _running = true;
        _pump.Start();
        Log.Information("战斗服已监听 UDP {Port} (AppId={AppId})", Port, AppIdentifier);
    }

    /// <summary>
    /// 建本局的模拟层。
    ///
    /// ## 为什么必须在泵线程里建, 不能在 Start() 里建
    ///
    /// <c>GameObjectBase.GameManager</c> 走的是 <c>GameManager.ThreadStaticInstance</c>,
    /// 而且带一致性校验 (<c>GameObjectBase.cs:321</c>, 对不上直接抛)。那是个
    /// <c>[ThreadStatic]</c> 字段 —— 在哪个线程上构造 GameManager, 就只有那个线程能碰它。
    ///
    /// 所以构造和 tick 必须同线程。在 Start() 里建、在泵线程里 tick 的话,
    /// 泵线程上那个值是 null, 第一次碰到任何 GameObject 就炸。
    ///
    /// 反过来这也是个好性质: 多局并行时各自一个线程, 天然隔离, 不需要额外加锁。
    ///
    /// ## 顺序抄自客户端
    ///
    /// <c>GameManagerClient.cs:1793</c>: Difficulty -> CacheGame -> LoadMap。
    /// 同步表的绝大部分是 CacheGame 里对象池分配出来的, 顺序反了就只有 8 条。
    /// </summary>
    private void BuildSimulation()
    {
        string gameRoot = _gameRoot ?? DIE.Server.Game.GameDataLoader.DefaultGameRoot;
        DIE.Server.Game.GameDataLoader.EnsureLoaded(gameRoot);

        // 本线程自己的 BalanceData。它是 ThreadStatic 但在静态构造里赋值, 所以除了
        // 「第一个碰到 GameManager 的线程」以外都是 null。详见 EnsureThreadState 的说明。
        DIE.Server.Game.GameDataLoader.EnsureThreadState();

        string mapsDirectory = System.IO.Path.Combine(gameRoot, "Maps");
        MapManager._0023uh = mapsDirectory;
        MapManager.Load(MapIndex);

        string mapName = MapManager._0023GQf(MapIndex);
        GameModeSettings settings =
            ConductorGameLogic.GameModes.GameMode.ReadSettings(mapName, mapsDirectory);
        if (settings == null)
        {
            throw new InvalidOperationException($"读不出地图 {MapIndex} ({mapName}) 的 GameModeSettings");
        }

        using System.IO.FileStream crafting = System.IO.File.OpenRead(
            System.IO.Path.Combine(AppContext.BaseDirectory, "data", "GameCraftingData.xml"));

        _view = new DIE.Server.Game.HeadlessViewManager();
        _sim = new DIE.Server.Game.GameManagerServer(_view, mapsDirectory, GameHash, crafting);

        _network = new DIE.Server.Game.GameServer(
            maxPlayers: MaximumConnections - 4,
            maxObservers: 4,
            fpsLimit: LogicFps,
            createClient: index => new Client(index, _sim),
            createBuffer: size => new _0023TDe(_peer._0023dce(size)));

        _sim.Initialize(_network, null);

        // 挨打就加威胁 —— ChangeStats 是所有伤害的唯一汇合点, 脑子从那里收消息。
        // 见 ZombieBrain.OnDamaged。
        _sim.Damaged = _zombieBrain.OnDamaged;
        _sim.SupplyDropOverride = dead => _crossroads.SupplyDropFor(dead);

        // 单向路障: 只有"外面"那一侧打得动。见 ScoutStoryDirector.IsDamageBlocked。
        _sim.BlockDamage = _scoutStory.IsDamageBlocked;

        // **必须紧跟 Initialize** —— 客户端那侧就是这两行连着的
        // (GameClient.cs:243-244)。Reset() 把一批运行时字段设成初值, 其中
        // ServerModifier = 1f (GameManager.cs:2538) 是关键: 它的 C# 默认值是 0,
        // 而 GetInfectionLevelModifiers() 拿它当乘数 (:2595), 僵尸的基础属性又是
        //
        //     baseStat.Value * GetInfectionLevelModifiers() * GetZombieRating(...)
        //
        // 算出来的。漏掉 Reset 的后果是**所有僵尸属性恒为 0** —— MaxHealth 为 0,
        // 血条画不出来, 而且谁也打不死谁。全程零异常。
        _sim.Reset();

        // ## 必须在 Reset **之后**
        //
        // Reset() 会把 ServerModifier 打回 1f (GameManager.cs:2538), 放在前面
        // 设等于白设。
        //
        // ServerModifier 是僵尸基础属性的乘数
        // (GetInfectionLevelModifiers -> baseStat × 它 × ZombieRating), 原版由
        // 服务端按感染等级曲线设 —— 那条链在客户端是完整的
        // (GameManagerClient.GetWalkerHealth 同样调 GetInfectionModifier),
        // 缺的只有服务端这一次赋值。
        _sim.ServerLevel = InfectionLevel;
        _sim.ServerModifier = DIE.Server.Game.InfectionLevel.ZombieModifier(
            InfectionLevel, (MapIndex)MapIndex);

        Log.Information("战斗服 UDP {Port} 感染等级 {Level} (僵尸属性 ×{Modifier:0.00})",
            Port, InfectionLevel, _sim.ServerModifier);

        // 强制开局: 人不满时等到这个时刻 (从战斗服起算) 也开。原厂开关默认关 (GameManager.cs:2563),
        // 时间默认 120 秒 (:2564), 打开归服务端管。原厂那段倒计时见 ScavengerHuntServer。
        _sim.EnableForceStart = true;

        _network.GameManager = _sim;
        _network.QueueOutgoing = QueueOutgoing;
        _network.FlushFrames = FlushFrames;
        _network.BeforeSimulation = BeforeSimulation;
        _network.Profiler = Profiler;
        _sim.OnGameMessage = OnGameMessage;

        _tutorialDirector.OnAdvanced = (from, to, why) =>
            Log.Information("战斗服 UDP {Port} 教程推进 {From} -> {To} ({Why})", Port, from, to, why);

        // 任务完成 -> 把本局经验交给账号库。
        //
        // 战斗服自己不碰账号库 (MatchHostRegistry 里那条注释), 所以走回调 ——
        // 谁建的战斗服谁负责落库。
        _scoutFinale.OnMissionComplete = simulation =>
        {
            // 局末主动推一次计分板数据 —— 这时客户端是被 TeamFinished 逼着
            // 弹计分板的, 不走"按键打开"那条路, 不会自己来要。
            int rows = DIE.Server.Game.PlayerDataSender.Send(simulation, _network);
            Log.Information("战斗服 UDP {Port} 局末下发计分板数据 {Rows} 份", Port, rows);

            // 每人记自己挣的那份, 记到自己的账号上 (以前是全场加总记给开局者 —— 一局一人时没区别)。
            uint earned = 0;
            foreach (Player player in PlayersInMatch(simulation))
            {
                uint xp = PlayerExperience(player);
                ulong user = UserOf(player.Client);
                earned += xp;

                if (xp == 0 || AwardAccountXp == null)
                {
                    Log.Information("战斗服 UDP {Port} 任务完成, 账号 {UserId} 本局经验 {Xp} (未落库: {Why})",
                        Port, user, xp, AwardAccountXp == null ? "没接账号库" : "经验为 0");
                    continue;
                }

                uint total = AwardAccountXp(user, xp);
                Log.Information("战斗服 UDP {Port} 账号 {UserId} 入账经验 {Xp}, 累计 {Total}",
                    Port, user, xp, total);
            }

            // 结算画面照发 —— 没经验不代表这局不该有结算。
            SendMatchRewards(simulation, earned);

            // 打完了: 不再算"中途放弃", 也不再提示重连
            _matchOver = true;
        };

        // 十字路口那张图唯一的 Event_Assault 是**基地夜袭** —— 它就摆在基地里,
        // 不设限的话玩家一落地就炸, 30 只怪堆在出生点 (玩家实测报的正是这个)。
        // 只有进了收尾阶段 (三轮任务做完、天黑) 才准刷。别的模式不受影响。
        //
        // 夜袭按原厂 Event_Assault 刷 (玩家看过原版视频: "夜袭的僵尸刷新回归原版")。
        // 认的是原厂标志 EndGameCinematic (39 张图那 35 个 Assault 全勾着它, 客户端不读 = 服务端用的),
        // 而不是"RaidMission 里的任何 Assault" —— 以后哪张图加了普通 Assault 不会被一起关掉。
        _eventSpawns.Allow = assault =>
            (_sim?.GameMode is not ConductorGameLogic.GameModes.RaidMission
             || assault == null || !assault.EndGameCinematic
             || _crossroads.Step == DIE.Server.Game.CrossroadMissionDirector.Stage.Finale
             || _crossroads.Step == DIE.Server.Game.CrossroadMissionDirector.Stage.Over)
            && _scavenger.AllowAssault(assault);   // 拾荒各队基地的夜袭只在入夜时刷 (下面 OnNightFall)

        // 原厂 HighLevelBarricadeSpawn: 这批事件 / 漫游点要等那一队打到最后一道路障才刷
        // (拾荒 9 个 Assault + 14 个漫游点勾着; 判据见 ScavengerDirector.AllowHighLevelBarricadeSpawn)
        _eventSpawns.AllowHighLevelBarricade = assault =>
            assault == null || _scavenger.AllowHighLevelBarricadeSpawn(assault.Position);
        _zombieDirector.AllowHighLevelBarricade = point =>
            point == null || _scavenger.AllowHighLevelBarricadeSpawn(point.Position);

        // 拾荒基地封锁: 这队基地的夜袭打光了就开门
        _scavenger.NightAssaultRemaining = team =>
            _eventSpawns.RemainingFor(assault => _scavenger.NightAssaultTeam(assault) == team);

        // 拾荒夜袭的怪只打装满卡车的那一队 (延迟出场的也在刷出来那一刻挂上)
        _eventSpawns.OnSpawned = (zombie, assault) =>
        {
            if (_scavenger.IsNightAssault(assault) && _scavenger.FillTruckTeam != TeamId.Neutral
                && zombie is ConductorGameLogic.Entities.NPC npc)
            {
                _zombieBrain.RestrictToTeam(npc, _scavenger.FillTruckTeam);
            }
        };

        // 拾荒入夜 -> 装满卡车那一队的基地夜袭 (不等玩家走进 25 单位的触发圈)
        _scavenger.OnNightFall = (simulation, teams) =>
        {
            // 入夜刷怪加快: 地图的 EndGameSpawnRateIncrease (五张图都是 1.5), 客户端没人读 —— 漫游点加数量、事件缩冷却
            float rate = simulation.GameModeSettings?.EndGameSpawnRateIncrease ?? 1f;
            _zombieDirector.CountScale = rate;
            _eventSpawns.SpawnRateMultiplier = rate;

            int fired = _eventSpawns.ForceFire(simulation,
                assault => teams.Contains(_scavenger.NightAssaultTeam(assault)));
            Log.Information("战斗服 UDP {Port} 拾荒入夜: 基地夜袭 {Count} 处 (队伍 {Teams}), 刷怪倍率 {Rate}",
                Port, fired, string.Join(",", teams), rate);
        };

        // 十字路口跑完一局 -> 走和 Scout 一样的收尾 (标记局已结束 -> 任务评分 ->
        // 结算 -> TeamFinished)。那套顺序是踩出来的, 所以复用而不是再写一份 ——
        // 见 ScoutFinaleDirector.CompleteNow。
        // 认输投票通过 -> 十字路口直接放结束动画, 放完走同一个收尾
        _concede.OnPassed = simulation => _crossroads.Concede(simulation);

        // 拾荒分出胜负 -> 经验 / 结算 (和 Scout 同一个回调; TeamFinished 由拾荒自己按名次每队发)
        _scavenger.OnMatchOver = simulation => _scoutFinale.OnMissionComplete?.Invoke(simulation);

        _crossroads.OnRunComplete = simulation =>
            _scoutFinale.CompleteNow(simulation, _network,
                $"十字路口 {_crossroads.MissionsWon}/{_crossroads.Planned} 个任务拿到奖牌",
                sendMissionScore: false);

        _introLock.OnMobilityChanged = (canMove, locked, impaired, stunned, casting, channeling) =>
            Log.Information("战斗服 UDP {Port} 玩家行动能力: 能动={CanMove} " +
                            "技能锁={Locked} 移动压制={Impaired} 眩晕={Stunned} " +
                            "施法中={Casting} 引导中={Channeling} (教程状态 {State})",
                Port, canMove, locked, impaired, stunned, casting, channeling,
                (_sim?.GameMode as _00235cc)?._0023l9._0023pcc);

        // 状态 22 的推进判据认准**我们放的那一只** Floater ——
        // 事件刷怪自己也会刷 Floater, 判"场上清干净"会让玩家多杀一只。
        _tutorialDirector.StoryFloaterIsDead = () => _finale.TutorialFloaterIsDead;

        // **开始**往卡车上搬补给 -> 按波次刷怪 (和补给点占领同一个节奏)。
        _tutorialDirector.OnDeliveryStarted = simulation =>
        {
            _finale.StartWaves(simulation);
            Log.Information("战斗服 UDP {Port} 玩家开始交付, 终局波次启动: 刷怪点 {Points} 个",
                Port, _finale.PointCount);
        };

        // 补给交完 -> 放剧情那只 Floater (它是状态 22 的引导目标, 早放了
        // 客户端还没进 22, 箭头不会指它)。
        _tutorialDirector.OnDeliveryComplete = simulation =>
        {
            _finale.SpawnStoryFloater(simulation);

            // Floater 不在 SpawnedThisTickList 里 (那个列表下一帧才由
            // _finale.Update 填并清), 单独激怒 —— 它是剧情目标, 不该站在
            // 角落里等玩家过去找。
            if (_finale.TutorialFloater is ConductorGameLogic.Entities.NPC floater)
            {
                _zombieBrain.Provoke(floater);
            }

            Log.Information("战斗服 UDP {Port} 补给交付完成, Floater={Floater}",
                Port, _finale.FloaterSpawned);
        };

        // 收尾 -> 停止补怪。**不清场**, 理由见 TutorialFinaleDirector.Stop。
        _tutorialDirector.OnFinaleOver = () =>
        {
            _finale.Stop();
            _tutorialOverAt = _clock.Elapsed.TotalSeconds;
            _matchOver = true;
            Log.Information("战斗服 UDP {Port} 教程终局结束: 累计刷出 {Total} 只, " +
                            "{Seconds} 秒后送客", Port, _finale.SpawnedTotal, TutorialFarewellSeconds);

            MarkTutorialDone();
        };

        // 技能栏缓存表快照 —— 这一轮打开 GrantWeapons 的**目的**就是拿这两行。
        // 装备前后缓存表长度变了几个, 决定了"客户端插件是唯一出路"这个结论成不成立。
        // 详见 TutorialDirector.SnapshotAbilityBar。
        DIE.Server.Game.TutorialDirector.OnAbilityBarSnapshot = (phase, snapshot) =>
            Log.Information("战斗服 UDP {Port} 技能栏[{Phase}] {Snapshot}", Port, phase, snapshot);

        // 按 C 切武器的闸门打开了。这是玩家报告"按 C 没反应"的根因修复:
        // 教程模式下客户端的 SendSwapWeapon (GameManagerClient.cs:1495) 要求
        // GameMode.AsTutorialGameMode._0023Kcc 为真, 否则按键被整条吞掉。
        // 详见 TutorialDirector.AllowWeaponSwapInput。
        DIE.Server.Game.TutorialDirector.OnWeaponSwapUnlocked = () =>
            Log.Information("战斗服 UDP {Port} 已解锁按 C 切换武器 (教程闸门 _0023Kcc=true, " +
                            "随世界状态同步下发) —— 玩家现在应该能在船桨和霰弹枪之间切了", Port);

        // 服务端主动切武器的结果。**切完之后要看诊断行的"武器槽"**:
        //   保持在目标槽 -> 客户端接受了 (它的 _Weapons[槽] 非 null)
        //   被拉回 0     -> 客户端 GetWeapon(槽) 是 null, 守卫拦住, 它继续上报旧值,
        //                   服务端的 UpdateSwapWeapon 又把自己切回去
        // 详见 WeaponSwapper 的说明 —— 这是服务端可见地判断客户端武器状态的办法。
        DIE.Server.Game.TutorialDirector.OnWeaponSwapped = (slot, swapped) =>
        {
            if (swapped)
            {
                Log.Information("战斗服 UDP {Port} 服务端已请求切到武器槽 {Slot} " +
                                "(SwapWeapon 实体已生成) —— 看下一秒的诊断行: " +
                                "武器槽保持 {Slot} 说明客户端接受了, 回到 0 说明它那边槽是空的",
                    Port, slot);
            }
            else
            {
                Log.Warning("战斗服 UDP {Port} 切武器槽 {Slot} 失败 —— " +
                            "服务端自己的 GetWeapon({Slot}) 就是 null, 或 SwapWeapon 实体没进对象池",
                    Port, slot);
            }
        };

        DIE.Server.Game.TutorialDirector.OnWeaponGranted = (slot, id, landed) =>
        {
            if (!DIE.Server.Game.TutorialDirector.GrantWeapons)
            {
                Log.Information("战斗服 UDP {Port} 教程第 {Slot} 把武器 (图纸 {Id}) **按设计跳过** —— " +
                                "武器技能的槽位是客户端权威的 (AbilityBar.Deserialize:918 拒绝服务端赋值), " +
                                "服务端装上去只会让客户端继续发 FistsPrimary 的下标, " +
                                "而那个技能的 SlotIndex 已被改成 -1, 攻击被 Character._00232z 静默作废。" +
                                "玩家保持空手 —— 至少打得动。详见 TutorialDirector.GrantWeapons",
                    Port, slot, id);
                return;
            }

            if (landed)
            {
                // ⚠️ 必须发 **GameMessageType.ClientInfo 游戏消息**, 不是
                // _0023XCe 那个 ClientInfo 批。
                //
                // 我先前只发了批量那条 (ClientInfoSender.Broadcast), 而它的接收方
                // GameClient._0023KCe 只更新 ClientInfoData 字段, **不碰武器**。
                // 于是日志里"ClientInfo 已下发 1 份"看着像成功, 客户端却什么都没装 ——
                // 一条误导性的日志让我在这上面绕了好几轮。
                //
                // 真正装备武器的是 GameManagerClient.cs:569-587 那个 case:
                // 它 GetWeapon(...) 构造武器物品, 然后
                // EquipWeaponsAndAbilities(forceSetSlot: true) —— forceSetSlot
                // 正是绕过 Player.cs:752 教程判断的开关。
                // 详见 ClientInfoSender.SendEquipUpdate 的说明。
                int equipped = DIE.Server.Game.ClientInfoSender.BroadcastEquipUpdate(_network);
                int sent = DIE.Server.Game.ClientInfoSender.Broadcast(_network);

                Log.Information("战斗服 UDP {Port} 教程发武器: 已下发装备更新 {Equipped} 条 " +
                                "(GameMessageType.ClientInfo)", Port, equipped);

                Log.Information("战斗服 UDP {Port} 教程发武器: 槽位 {Slot} 图纸 {Id} 已到手 " +
                                "(ClientInfo 已下发 {Sent} 份)", Port, slot, id, sent);
            }
            else
            {
                Log.Error("战斗服 UDP {Port} 教程发武器**失败**: 槽位 {Slot} 图纸 {Id} —— " +
                          "玩家手上仍然是空的, 查物品图鉴里有没有这个 ID", Port, slot, id);
            }
        };

        // 经验是**静默**发放的 (走 ChangeStats, 原厂一行日志都没有), 所以不主动
        // 打点就完全看不出"到底发了没有" —— 而它有好几种安静失败的方式:
        // ConnectedPlayers 为空、离尸体超过 150 单位、队伍对不上。
        _sim.OnExperienceGranted = (victim, gained, total) =>
            Log.Information("战斗服 UDP {Port} 击杀 {Victim} 获得经验 {Gained:F0} (累计 {Total:F0}, 等级 {Level})",
                Port, victim.GetType().Name, gained, total,
                _sim.ActivePlayerList.Count > 0 ? _sim.ActivePlayerList[0].GetLevel() : -1);
        // ## 调试消息一律**只记不执行** (2026-09-19 查清并定案)
        //
        // DebugMessageType 是和 GameMessageType **并列的第二条通道** (类别字节 1 vs 0,
        // 见 GameManagerServer.DeserializeGameMessage)。客户端能发 29 种上来, 全是作弊指令:
        // KillAllZombies / IncreaseLevel / NoCooldown / SupplyVictory (直接判胜) /
        // SetPosition (传送) / EquipHaxWeapon ...
        //
        // 两层防线, 都已经在:
        //
        //   1. **服务端不执行** —— 就是这一行, 只打日志。这是真正的防线,
        //      因为客户端在玩家自己机器上, 改过的客户端可以无视第 2 层直接发。
        //   2. 未改动的客户端**连入口都没有** —— 那些键位分支由 GameManager.DebugMode 把关,
        //      而它是服务端在 GameInfo 里下发的 (GameManagerClient.cs:1821), 我们发的是
        //      DebugModeFlags.None (见下面 BuildGameInfo)。
        //
        // 所以这条**不需要再做什么**。别下次审校又把它当"空实现"捡起来 —— 空着是对的。
        //
        // 级别是 Warning 而不是 Information: DebugMode 既然是 None, 正常客户端根本发不出来,
        // 收到一条就说明对面的客户端被改过。这行日志顺便当作弊告警用。
        _sim.OnDebugMessage = (type, buf, from) =>
            Log.Warning("战斗服 UDP {Port} slot {Index} 发来调试消息 {Type} —— **不执行**, 只记录 "
                        + "(我们下发的 DebugMode 是 None, 正常客户端发不出这个)", Port, from.Index, type);
        _sim.OnUnknownCategory = raw =>
            Log.Error("战斗服 UDP {Port} 消息类别字节为 {Raw}, 既非 GameMessage(0) 也非 DebugMessage(1) —— "
                      + "这几乎肯定是数据帧解析错位, 而不是客户端发了新类别", Port, raw);

        // 打开原厂自带的技能失败诊断。
        //
        // AbilityBar.TryUseAbility (:571) 在服务端且这个开关为真时会打印
        //
        //     TryUseAbility failed on server: <技能> canUseResult: <原因>
        //
        // CanUseResult 有 18 个值 (Cooldown / InsufficientRage / Disabled /
        // NotValidTarget / Dead ...), 这一行能直接说出攻击为什么用不出来。
        //
        // "打僵尸不掉血"我已经推理错三轮了 (先怪技能栏错位, 再怪教程标志,
        // 再怪二次装备)。这个开关是原厂留的观测点, 用它取代猜。
        //
        // 它走 StunLog, 会进服务端控制台。噪声可控 —— 只在**失败**时打印。
        ConductorGameLogic.Abilities.AbilityBar.DebugInterruptLogs = true;

        _sim.Difficulty = HordeModeType.Easy;

        // ## 必须和发给客户端的 GameInfo.CharactersToCache 是同一张表
        //
        // CacheGame(地图, 名单) 按名单决定每类对象预建几个 (_0023y._0023j), **对象的编号就是按这个排的**;
        // 客户端收到 GameInfo 之后用 GameInfo.CharactersToCache 跑同一个函数。两边名单不同 = 编号错位 ——
        // 服务端说"你是 71 号玩家", 客户端 71 号那格根本不是玩家 -> Client.OnGameManagerCreated 里
        // GetPlayer 返回 null -> NRE -> 进任何一局都黑屏 (玩家实测 2026-09-13, 客户端日志)。
        //
        // 第五轮给平民加模型时只改了发给客户端的那张, 服务端这里一直传空表 (= 全部预建)。
        _sim.CacheGame(MapIndex, CachedCharacters);
        _sim.LoadMap(settings, MapIndex);

        // 十字路口的难度档 —— 必须在 LoadMap 之后 (那时模式实例才被选中并 Reset 过,
        // ResetGameMode 会把模式数据清一遍)。别的模式没有这个概念。
        if (_sim.GameMode is DIE.Server.Game.RaidMissionServer raid)
        {
            raid.Difficulty = CrossroadDifficulty;

            // 选题要用它: 每种事件按 DifficultyMask 分档 (见 CrossroadEventCatalog)。
            _crossroads.Difficulty = CrossroadDifficulty;

            // 刷怪量跟感染等级走 (见 CrossroadMissionDirector.Scale)
            _crossroads.InfectionLevel = InfectionLevel;
        }

        // 拾荒: 原厂的开局倒计时走完时调 GameMode.Start() —— 那是个空虚方法,
        // 真正"开局要做什么"由服务端决定。接到 BeginMatch 上。
        // 等人那一段 (报秒 / 强制开局 / 把人按在出生区) 全在原厂代码里, 见 ScavengerHuntServer。
        if (_sim.GameMode is DIE.Server.Game.ScavengerHuntServer scavenger)
        {
            scavenger.OnStart = () => BeginMatch("原厂倒计时走完");
        }

        // 十字路口同一套 (玩家 2026-09-23 定: 按原厂公开匹配凑满 4 人, 开局前在基地等人)
        if (_sim.GameMode is DIE.Server.Game.RaidMissionServer raidMission)
        {
            raidMission.OnStart = () => BeginMatch("原厂倒计时走完");
        }

        _forceStartAt = _sim.ForceStartTime;

        Log.Information("战斗服 UDP {Port} 模拟层就绪: 地图={Map}({Name}) 模式={Mode} 同步对象={Sync}{Difficulty}",
            Port, MapIndex, mapName, settings.GameModeType, _network.NumOfSynchronizables,
            _sim.GameMode is DIE.Server.Game.RaidMissionServer ? $" 难度档={CrossroadDifficulty}" : string.Empty);
    }

    /// <summary>
    /// 泵 + 定步长循环。
    ///
    /// 定步长本身是现成的: <c>NetworkBase.Update(totalSeconds)</c> 累积 delta,
    /// 按 <c>TargetDelta</c> (= 1/FpsLimit) 切帧, 每帧走 DoFrame -> UpdateGame。
    /// 我们只要按足够高的频率喂它墙钟就行, 不能自己再写一套时间累积 ——
    /// 那样会和客户端的节奏对不上。
    ///
    /// 10ms 一轮 = 100Hz, 高于 30Hz 的逻辑帧率, 保证每个逻辑帧都能准时切出来。
    /// </summary>
    private void Pump()
    {
        try
        {
            BuildSimulation();
        }
        catch (Exception ex)
        {
            SimulationError = ex;
            Log.Error(ex, "战斗服 UDP {Port} 模拟层构建失败, 本局无法进行", Port);
            return;
        }

        _pumpThreadId = Environment.CurrentManagedThreadId;
        SimulationReady = true;
        _clock.Start();

        // ## 顺序: 先模拟, 再收发
        //
        // Lidgren 是手动泵的 (见构造函数里 _00233ee 那段), 发送也挂在 _peer.Update()
        // 的心跳上。要是先收包再模拟, 这一轮组好的帧得等到**下一轮**才发出去,
        // 平白晚一个 Sleep (Windows 上约 15ms)。反过来排, 帧一组好就随紧接着的
        // Update 发走 —— 和原来自带网络线程时"入队即发"一样。
        //
        // 心跳自己有节流 (两次间隔不足约 0.8ms 就跳过发送, -gce.cs:678), 所以
        // 一轮只调一次 Update, 不在组帧后面再补一次。
        while (_running)
        {
            try
            {
                _network.Update((float)_clock.Elapsed.TotalSeconds);

                long lap = System.Diagnostics.Stopwatch.GetTimestamp();
                _peer.Update();

                _0023cee message;
                while ((message = _peer._0023Jbe()) != null)
                {
                    Handle(message);
                    _peer._0023cce(message);
                }

                DrainPumpWork();
                Profiler.Lap(_profNet, ref lap);

                LogSimulationState();
                SayGoodbyeIfTutorialIsOver();
                Profiler.Lap(_profHousekeeping, ref lap);
                LogProfile();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "战斗服 UDP {Port} 泵循环异常", Port);
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// 把一批刚刷出来的怪设成"一出生就追着玩家打"。
    ///
    /// 不指定优先目标 —— 交给 <c>ZombieBrain</c> 自己挑最近的玩家。埋伏刷怪
    /// 那边指定了目标 (谁在打路障就打谁), 那是那一段特有的剧情要求。
    /// </summary>
    /// <summary>
    /// 对象 Update 抛异常时, 原厂主循环把它标 <c>IsBroken</c>、从此不更新也不销毁 (GameManager.Update :1596),
    /// 错误只写 StunLog, 我们的日志里看不到 —— BOSS 钩舌卡死那次 (2026-09-14) 就是这样查了半天。
    /// 新坏掉的对象打一条警告 (每个对象一次)。
    /// </summary>
    private void ReportBrokenObjects()
    {
        System.Collections.Generic.List<ConductorGameLogic.Entities.GameObjectBase> objects = _sim.ActiveGameObjectList;
        for (int i = 0; i < objects.Count; i++)
        {
            ConductorGameLogic.Entities.GameObjectBase obj = objects[i];
            if (!obj.IsBroken || !_brokenReported.Add((obj.IndexGlobal, obj.NumberOfGlobalCacheUses)))
            {
                continue;
            }

            Log.Warning("战斗服 UDP {Port} 对象 {Type}#{Index} Update 抛异常, 被原厂主循环标成坏的 (不再更新也不销毁)",
                Port, obj.GetType().Name, obj.IndexGlobal);
        }
    }

    private readonly System.Collections.Generic.HashSet<(int, int)> _brokenReported =
        new System.Collections.Generic.HashSet<(int, int)>();

    private void ProvokeSpawned(System.Collections.Generic.List<ConductorGameLogic.Entities.Character> spawned)
    {
        for (int i = 0; i < spawned.Count; i++)
        {
            if (spawned[i] is ConductorGameLogic.Entities.NPC npc)
            {
                _zombieBrain.Provoke(npc);
            }
        }
    }

    /// <summary>
    /// 十字路口难度解锁往前推一步 —— 大厅难度选择那一排锁。
    ///
    /// ## 玩家撞到的
    ///
    /// 悬停在 INFECTIOUS 的锁上写着 "Unlock by completing 3 more Normal games
    /// with at least 400 Supplies collected each game. (0/3 Completed)",
    /// 打完一局 Normal 那个数还是 0。
    ///
    /// <c>crossroad_progress</c> 这一列一直都在, 登录也下发了, 但全代码树
    /// **一处 UPDATE 都没有** —— 局末推进的那一半在服务端, 随服务端丢了。
    ///
    /// ## 判据
    ///
    /// 难度要正好等于当前档、这局收集的补给够那一档的线 (Normal 400、
    /// Infectious 500、往上 600), 都是原厂表里的数。细节见
    /// <see cref="DIE.Server.Game.CrossroadUnlock"/>。
    ///
    /// 补给取**这个人这局的量** (结算画面上那个数, 和界面文案的
    /// "Supplies collected each game" 对得上), 不是全队的。
    /// </summary>
    private IReadOnlyList<DIE.Server.Game.PlayerMatchReward> AdvanceCrossroadUnlocks(
        ConductorGameLogic.GameManager simulation,
        IReadOnlyList<DIE.Server.Game.PlayerMatchReward> rewards)
    {
        if (AdvanceCrossroadProgress == null
            || simulation?.GameMode is not DIE.Server.Game.RaidMissionServer
            || !simulation.GameMode.IsCompleted)
        {
            return rewards;
        }

        List<DIE.Server.Game.PlayerMatchReward> updated =
            new List<DIE.Server.Game.PlayerMatchReward>(rewards.Count);

        foreach (DIE.Server.Game.PlayerMatchReward reward in rewards)
        {
            if (reward.UserId == 0)
            {
                updated.Add(reward);
                continue;
            }

            byte progress = AdvanceCrossroadProgress(
                reward.UserId, CrossroadDifficulty, reward.Data.Supplies);

            // PlayerRewardData 是 struct, 这是副本 —— 改完塞回去才算数。
            ConductorCrafting.PlayerRewardData data = reward.Data;
            data.CrossroadProgress = progress;

            updated.Add(reward with { Data = data });
        }

        return updated;
    }

    /// <summary>
    /// 教程打完 —— 解锁新手任务里那个"做过教程了"的节点。
    ///
    /// ## 不推这一下, 玩家会被永远锁在教程里
    ///
    /// 大厅每次选完角色都查一次 (<c>CribMain.PreCheckPickCharacters</c>):
    /// 节点 3 没解锁就 <c>StartTutorial(cancelable: <b>false</b>)</c>。
    /// 玩家 2026-09-21 报的"卡在完成 tutorial 后 starter quest 没有继续推进"
    /// 就是这个 —— 打完教程回大厅又被塞回教程, 面板那五项一步都走不到。
    ///
    /// ## 为什么不走 <see cref="SendMatchRewards"/> 那条
    ///
    /// 那一条挂在 <c>_scoutFinale.OnMissionComplete</c> 上, 是 Scout / 十字路口 /
    /// 拾荒共用的收尾 (结算画面 + 奖励入账)。教程从来不走它: 它自己的收尾是
    /// "等 6 秒然后把人断开" (<see cref="SayGoodbyeIfTutorialIsOver"/>),
    /// 没有结算画面也没有奖励。
    ///
    /// 所以这里单推一下, 而且**只推这一个节点** —— <c>StarterQuestProgress</c>
    /// 认得 <c>Tutorial</c>, 不会顺手把"打三局"也加一格 (教程不是 match)。
    /// </summary>
    private void MarkTutorialDone()
    {
        if (AdvanceStarterQuests == null || _sim == null)
        {
            return;
        }

        try
        {
            List<ulong> users = PlayersInMatch(_sim)
                .Select(p => UserOf(p.Client))
                .Where(id => id != 0)
                .Distinct()
                .ToList();

            foreach (ulong user in users)
            {
                AdvanceStarterQuests(user,
                    ConductorGameLogic.GameModes.GameModeType.Tutorial);
            }

            // 和局末结算一样: 推完要通知大厅重拉, 否则面板要等下次登录才更新。
            int refreshed = 0;
            foreach (ulong user in users)
            {
                refreshed += RefreshAccount?.Invoke(user) ?? 0;
            }

            Log.Information("战斗服 UDP {Port} 教程完成, 已推进 {Count} 个账号的教程节点, "
                            + "通知 {Refreshed} 条大厅连接刷新{Note}",
                Port, users.Count, refreshed,
                refreshed > 0 ? string.Empty : " (没有活着的连接, 已记账等重连补推)");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 推进教程节点失败", Port);
        }
    }

    /// <summary>
    /// 教程走完并等够 <see cref="TutorialFarewellSeconds"/> 之后, 把还连着的
    /// 客户端断开 —— 它自己那条退出链断了, 见那个常量的说明。
    ///
    /// 只做一次 (做完把 <see cref="_tutorialOverAt"/> 复位成 -1)。断开之后
    /// <c>StatusChanged</c> 会照常走 Disconnected 分支还槽位, 和玩家自己退出
    /// 走的是同一条路。
    /// </summary>
    private void SayGoodbyeIfTutorialIsOver()
    {
        if (_tutorialOverAt < 0d
            || _clock.Elapsed.TotalSeconds - _tutorialOverAt < TutorialFarewellSeconds)
        {
            return;
        }

        _tutorialOverAt = -1d;

        // 快照一份 —— Disconnect 会触发 StatusChanged, 那边会改 _slots。
        var connections = new List<_0023iae._0023tde>(_slots.Keys);

        foreach (_0023iae._0023tde connection in connections)
        {
            // 原因串必须带那个前缀数字, 否则客户端会当成掉线并弹重连面板 ——
            // 见 MatchOverDisconnectReason 的说明。
            connection.Disconnect(MatchOverDisconnectReason);
        }

        Log.Information("战斗服 UDP {Port} 教程结束, 已送走 {Count} 个客户端 (原因串 \"{Reason}\")",
            Port, connections.Count, MatchOverDisconnectReason);
    }

    private void Handle(_0023cee message)
    {
        switch (message._00239de())
        {
            case _0023oee._0023fee:      // ConnectionApproval
                HandleConnectionApproval(message);
                break;

            case _0023oee._0023dee:      // StatusChanged
                HandleStatusChanged(message);
                break;

            case _0023oee._0023nh:       // Data
                HandleData(message);
                break;

            case _0023oee._0023q4d:      // ErrorMessage
                Log.Error("Lidgren: {Text}", message._0023Qae());
                break;

            case _0023oee._0023lee:      // WarningMessage
                Log.Warning("Lidgren: {Text}", message._0023Qae());
                break;

            case _0023oee._0023kee:      // DebugMessage
                Log.Debug("Lidgren: {Text}", message._0023Qae());
                break;

            default:
                Log.Debug("战斗服 UDP {Port} 未处理的消息类型 {Type}", Port, message._00239de());
                break;
        }
    }

    /// <summary>
    /// 受理入场申请: 读客户端握手数据, 分配槽位, 用回程 hail 批准。
    ///
    /// ## 客户端发来什么
    ///
    /// 结构在 <c>GameClient.cs:341</c> (<c>_0023LCe</c>) 里写死:
    ///
    ///   bool hasAuthData;   if (hasAuthData) AuthenticationData.Serialize(buf);
    ///   bool isObserver;
    ///   bool isReconnecting;
    ///   bool hasConnectionData;  if (...) connectionData.Serialize(buf);
    ///
    /// <see cref="AuthenticationData"/> = { string gameHash, uint ticketLength, byte[] ticket }。
    /// 那个 ticket 正是 Phase 2 通过 <c>+authticket</c> 下发给客户端的东西 —— 身份链是闭合的。
    ///
    /// ## 我们回什么
    ///
    /// 结构在 <c>GameClient.cs:482</c> (Connected 分支) 里:
    ///
    ///   bool NetDebuggingEnabled;
    ///   byte clientIndex;
    ///   client.ClientInfoServerToClientDeserialize(buf);
    ///   bool hasGameInfo;   if (hasGameInfo) gameInfo.Deserialize(buf);
    ///
    /// **两侧都不手写编解码**: 读用 <see cref="AuthenticationData.Deserialize"/>,
    /// 写用 <see cref="Client.ClientInfoServerToClientSerialize"/> —— 都是游戏自己的实现,
    /// 逐字节对齐有保证。Phase 1 那个 ushort 定长坑就是手写解码器踩出来的。
    ///
    /// ## 为什么 LocalPlayer 留空
    ///
    /// 批准阶段玩家实体还不存在。<c>ClientInfoServerToClientSerialize</c> 在
    /// <c>LocalPlayer == null</c> 时写 false 就收尾, 客户端相应地把 PlayerIndex 置 -1 ——
    /// 这是原厂就支持的合法状态, 不是残缺数据。完整的 ClientInfo 走对局内的
    /// 那条路径下发 (<c>GameManagerClient.cs:573</c>)。
    /// </summary>
    private void HandleConnectionApproval(_0023cee message)
    {
        _0023tde connection = message._0023bee();

        // 不写 `AuthenticationData?` —— 本项目没开 nullable 上下文, 那个注解
        // 只会换来一条 CS8632, 而且它是**引用类型**, 本来就可以为 null。
        AuthenticationData auth = null;
        ClientConnectionData connectionData = null;
        bool isObserver;
        bool isReconnecting;
        try
        {
            LidgrenNetBuffer reader = new LidgrenNetBuffer(message);
            if (reader._0023Fae())
            {
                auth = new AuthenticationData();
                auth.Deserialize(reader);
            }

            isObserver = reader._0023Fae();
            isReconnecting = reader._0023Fae();

            // 客户端**总是**带这段 (UnityClient.cs:2793 构造, GameClient.cs:353 写入),
            // 里面有玩家的相机朝向和装备 GUID。之前这里只读标志位不读载荷 ——
            // 反正后面没东西要读, 所以一直没暴露出问题, 直到需要相机朝向为止。
            if (reader._0023Fae())
            {
                connectionData = new ClientConnectionData();
                connectionData.Deserialize(reader);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "战斗服 UDP {Port} 握手数据解析失败, 拒绝 {EndPoint}", Port, connection._0023pce);
            connection.Disconnect("bad hail");
            return;
        }

        int index = AllocateClientIndex(connection);
        if (index < 0)
        {
            Log.Warning("战斗服 UDP {Port} 满员, 拒绝 {EndPoint}", Port, connection._0023pce);
            connection.Disconnect("server full");
            return;
        }

        // 身份绑定: 对号入座。
        //
        // 握手里那张票是 +authticket 传进来的 GameLinkClient.AuthTicket —— **我们登录时
        // 签发的会话票** (sessions.auth_ticket), 不是 Steam 的加密票。所以查得出是谁,
        // 再对照这一局的名单 (组队排队时一个端点上会进来好几个人)。规则见 MatchRoster。
        byte[] ticket = auth?._0023Zzb;
        ulong? ticketUser = ticket != null && ticket.Length > 0 ? ResolveUserByTicket?.Invoke(ticket) : null;

        // ## 观战者不进名单
        //
        // 观战位是**谁都能进**的 (客户端那侧是调试开局窗口里的观战列表 ->
        // "+observer +connect ip port", 见 DebugPlayWindow.JoinGame)。所以不查名单 ——
        // 查了反而会把观战者挡在门外 (他不在这一局的名单上)。
        //
        // 观战者不生成角色, 也就不进 ActivePlayerList, 因此不参与结算, 拿不到奖励。
        MatchMember member = null;

        // 接了账号库 = 正式服: 不带查得出账号的会话票一律不放 (入场和观战都是)。
        // 战斗服端口段 50000-50099 可以扫, 以前没票也能顶进名单空位 / 一人局主人的位子。2026-09-17 审校。
        bool requireTicket = ResolveUserByTicket != null;
        Roster.RequireTicket = requireTicket;
        if (isObserver && requireTicket && ticketUser == null)
        {
            Log.Warning("战斗服 UDP {Port} 拒绝观战 {EndPoint}: 没有查得出账号的会话票", Port, connection._0023pce);
            _slots.Remove(connection);
            connection.Disconnect(NotInMatchDisconnectReason);
            return;
        }

        if (!isObserver)
        {
            RosterBinding binding = Roster.Bind(index, ticketUser);
            if (binding.Member == null)
            {
                Log.Warning("战斗服 UDP {Port} 拒绝 {EndPoint}: {Why} (名单 {Roster})",
                    Port, connection._0023pce, binding.Rule,
                    string.Join(",", Roster.Members.Select(m => m.UserId)));
                _slots.Remove(connection);
                connection.Disconnect(NotInMatchDisconnectReason);
                return;
            }

            member = binding.Member;
            Log.Information(
                "战斗服 UDP {Port} 入场申请 {EndPoint}: slot={Index} user={UserId} ({Rule}) " +
                "reconnect={Reconnect} gameHash={Hash} ticket={TicketLen}B 角色={Character}",
                Port, connection._0023pce, index, member.UserId, binding.Rule, isReconnecting,
                auth?._0023Xzb ?? "(无)", auth?._0023Yzb ?? 0, member.Choice?.Character);
        }
        else
        {
            Log.Information(
                "战斗服 UDP {Port} 观战申请 {EndPoint}: slot={Index} user={UserId} " +
                "gameHash={Hash} ticket={TicketLen}B (不占名单位子, 不生成角色)",
                Port, connection._0023pce, index, ticketUser ?? 0,
                auth?._0023Xzb ?? "(无)", auth?._0023Yzb ?? 0);
        }

        // **用模拟层自己的 Client 实例, 不要现造一个。**
        //
        // NetworkBase 在构造时就按 MaxClients 建好了整张客户端表 (走我们传进去的
        // createClient 工厂)。同步表、GameMode、Player 都是按这些实例来关联的 ——
        // 另造一个发出去, 客户端看到的身份就和服务端模拟里的那个对不上。
        Client client = (Client)_network.GetClient(index);
        client.IsObserver = isObserver;

        // 记下**入场时**的身份 —— SetObserveMode 只认这个, 不认会被改写的 client.IsObserver。
        // 否则任何玩家发一条就能变自由镜头飞遍全图 (见 SpectatorPolicy 的第 2 条安全线)。
        if (isObserver)
        {
            _observerSlots.Add(index);
        }
        else
        {
            _observerSlots.Remove(index);
        }
        client.IsConnected = true;
        client.IsReconnected = isReconnecting;
        client.UserID = member?.UserId ?? ticketUser ?? 0;
        client.ClientConnectionData = connectionData;

        // 必须在 TrySpawnPlayer 之前 —— Spawn() 靠遍历这个列表来置每客户端同步位。
        ActivateClient(client);

        if (!isObserver)
        {
            _connectedUsers[member.UserId] = index;

            // 他的角色还在场 (掉线重连) -> 接回原来那个; 否则新生成一个。
            if (TryReattachPlayer(client, member))
            {
                Log.Information("战斗服 UDP {Port} slot {Index} 账号 {UserId} 重连, 接回原来的角色 " +
                                "(playerIndex={PlayerIndex})",
                    Port, index, member.UserId, client.LocalPlayer?.IndexPlayer);
            }
            else if (!TrySpawnPlayer(client, out string spawnFailure))
            {
                Log.Warning("战斗服 UDP {Port} 未能为 slot {Index} 生成玩家实体: {Reason} —— " +
                            "仍然放行, 客户端会以观战状态进入 (PlayerIndex = -1)",
                    Port, index, spawnFailure);
            }
        }

        _0023yee hail = _peer._0023dce();
        LidgrenNetBuffer writer = new LidgrenNetBuffer(hail);
        writer.Write(false);                                    // NetDebuggingEnabled
        writer.Write((byte)index);
        client.ClientInfoServerToClientSerialize(writer, localPlayer: true);
        writer.Write(true);                                     // 有 GameInfo
        BuildGameInfo().Serialize(writer);

        Approve(connection, hail);
    }

    /// <summary>
    /// 本局的 GameInfo。
    ///
    /// ## MapIndex 是这里唯一真正要紧的字段
    ///
    /// 客户端拿它去读地图文件, **游戏模式是从地图里读出来的**, 不是我们指定的:
    ///
    ///   GameModeSettings s = GameMode.ReadSettings(MapManager._0023GQf(_GameInfo.MapIndex), ...);
    ///   LoadMap(s, _GameInfo.MapIndex);                       // GameManagerClient.cs:1846
    ///
    /// 3C 没发 GameInfo 时 MapIndex 默认 0, 于是客户端日志报
    /// GameModeType=6 / NumOfTeams=3 (Scavenger) —— 那是地图 0 的属性, 不是我们设的。
    ///
    /// ## 其余字段为什么这么填
    ///
    /// - <c>CharactersToCache</c>: **不只是特效预热, 还决定角色模型预建几个** —— 见
    ///   <see cref="CharactersToCache"/>。以前发空的, 平民模型不够用。
    /// - <c>MaxMatchLength = 0</c>: 模拟层只存不用 (GameManager.cs:309 赋值后无读取点),
    ///   0 也正是 <c>GameInfo.Reset()</c> 的取值。
    /// - <c>DifficultyLevel</c>: 只在地图是 Horde/Resistance 类时被取用
    ///   (客户端那边 <c>gameModeSettings is _0023Rfc</c> 才赋值)。
    /// - <c>GameplayChanges</c> 空: 运营下发的数值热更, 私服不需要。
    /// - <c>SuppliesToWinModifier</c> / <c>UseNewEndGameState</c>: 拾荒专用, 照抄服务端读到的地图设置 (见字段处)。
    /// </summary>
    private GameInfo BuildGameInfo()
    {
        GameInfo info = new GameInfo
        {
            MapIndex = MapIndex,
            ServerID = Port,

            // 客户端拿它算血条上显示的僵尸血量
            // (GameManagerClient.GetWalkerHealth 调的是同一条曲线), 所以必须和
            // 服务端 _sim.ServerLevel 是同一个数, 否则显示和手感对不上。
            ServerLevel = InfectionLevel,

            DifficultyLevel = HordeModeType.Easy,
            MaxMatchLength = 0f,
            SuppliesToWinModifier = _sim?.GameModeSettings?.SuppliesToWinModifier ?? 1f,
            HighestWeaponStrengthOnServer = 0f,
            DebugMode = DebugModeFlags.None,
            IsReadyMode = false,

            // 拾荒: 客户端拿这两个**覆盖**地图设置 (GameManagerClient.cs:1856-1857), 再据此算卡车线 / 胜利线
            // (ScavengerHunt._0023Zub) 和有没有夜晚。必须和服务端读的地图设置一致 —— 以前恒发 false,
            // 服务端按新版终局 (Resort 500/800) 算, 客户端按旧版 (1000) 算, 两边的胜利线对不上。
            UseNewEndGameState = _sim?.GameModeSettings?.UseNewEndGameState ?? false,
            GameplayChanges = Array.Empty<GameplayChange>(),
            CharactersToCache = CachedCharacters,
        };

        return info;
    }

    /// <summary>
    /// 十字路口一局最多同时有几个平民要画 —— 解救 4 个 + 护送 3 个, 救出来的还要跑 25 秒才收走,
    /// 两个任务不会同时进行, 但上一个任务的平民可能还在跑。给 8。**我们定的。**
    /// </summary>
    private const int CrossroadCivilianModels = 8;

    /// <summary>
    /// 告诉客户端本局要预建哪些角色模型。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "四个茧只有两个打破出现了幸存者。"
    ///
    /// 服务端日志里四个平民都生成了; 客户端日志里是两行
    /// <c>Failed to get model for #Mk.#7Y. Increase cache count!</c> —— 平民 (<c>_00237Y</c>)
    /// 的模型不够。
    ///
    /// ## 客户端的角色模型缓存按这张表建
    ///
    /// <c>ViewManager.SetupCacheCount</c> (ViewManager.cs:3524-3548): 每个角色类预建几个模型 =
    /// <c>GameInfo.CharactersToCache</c> 里这个角色出现几次。**平民借的是 RoyBecker 那一格**
    /// (:3548 <c>typeof(_00237Y) -&gt; CharacterEnum.RoyBecker</c>)。
    ///
    /// 这张表以前一直发空的 (上面那条注释以为它只管特效预热), 于是客户端退回
    /// "BergMutated 1 个" —— 连玩家自己的角色都没模型 (同一份日志里还有一行
    /// <c>Failed to get model for #Mk.#osb</c>, 那是 IsysArmored), 靠现场 Instantiate 兜着。
    ///
    /// 现在填: 名单里每个人的角色各一次; 十字路口的图再加 8 个 RoyBecker 给平民。
    /// </summary>
    private CharacterEnum[] CachedCharacters => _cachedCharacters ??= CharactersToCache();

    private CharacterEnum[] _cachedCharacters;

    /// <remarks>
    /// **服务端 CacheGame 和 GameInfo 共用这一张** (<see cref="CachedCharacters"/>, 算一次缓存住) ——
    /// 两边不一致就是编号错位、进局黑屏, 见 BuildSimulation 里的说明。
    ///
    /// 名单里有人没带角色选择 (解不出票据) 时整张退回空表 = 两边都全部预建: 他进场会拿 BergSurvivor
    /// (TrySpawnPlayer 的兜底), 只按名单预建的话池里没有这个角色。代价是平民模型又退回不够用。
    /// </remarks>
    private CharacterEnum[] CharactersToCache()
    {
        List<CharacterEnum> characters = new List<CharacterEnum>();

        foreach (MatchMember member in Roster.Members)
        {
            if (!member.Choice.HasValue)
            {
                return Array.Empty<CharacterEnum>();
            }

            characters.Add(member.Choice.Value.Character);
        }

        if (characters.Count == 0)
        {
            return Array.Empty<CharacterEnum>();
        }

        bool crossroads;
        try
        {
            crossroads = MapManager._0023O4b(MapIndex, HordeModeType.Normal) == QueueType.RaidMission;
        }
        catch (Exception)
        {
            crossroads = false;     // 地图枚举上没有属性 (实验图) —— 不加平民
        }

        if (crossroads)
        {
            characters.AddRange(Enumerable.Repeat(CharacterEnum.RoyBecker, CrossroadCivilianModels));
        }

        // ## 等人的模式: 每个可选角色再多留一份空位
        //
        // 开局前还会补人进来 (TryAddLateJoiners), 而这张表**建图时就定死了** —— 两边的对象池和
        // 同步表都按它编号, 之后改不了。补进来的人用的角色要是不在表里, 服务端给他的角色对象
        // 是残缺的 (GetInputData / MousePosition 空引用, 泵循环每帧抛, 整局停止下发 ——
        // 压测 tests\DIE.FakeClient --late 实测过), 先进场的客户端池里也没有这个角色。
        //
        // 所以每个可选角色各多留一份。代价很小: 服务端多几十个闲置的池对象; 客户端的角色
        // 预制体本来就全部加载 (ViewManager.CacheCharacterModels 遍历所有类型), 这张表只决定
        // 预先实例化几份模型, 缺了也会临时现建 (只打一条 "Increase cache count" 警告)。
        // 同一个角色被两支补进来的队伍都选了、空位不够时, TryAddLateJoiners 会拒绝, 匹配服改开新局。
        if (WaitsForPlayers)
        {
            characters.AddRange(DIE.Server.Data.CharacterCatalog.Playable);
        }

        return characters.ToArray();
    }

    /// <summary>
    /// 处理一条客户端游戏消息。
    ///
    /// 目前只记录 —— 每条消息的载荷格式都得对着客户端的发送方逐字段读, 猜不得。
    /// 先把"哪些消息真的会来"看清楚, 再按优先级逐条实现。
    ///
    /// <c>SkipTutorialIntro</c> / <c>TutorialCameraComplete</c> 已经实现 ——
    /// 两者都推进教程状态到 <c>_0023ecc</c>, 见 <see cref="AdvanceTutorialPastIntro"/>。
    /// </summary>
    /// <summary>
    /// 玩家按了主线交互键 (偷卡车 / 占补给点)。
    ///
    /// ## 报文
    ///
    /// <c>GameManagerClient.SendStartInteraction</c> (:1541) 写两个 ushort:
    ///
    ///     characterID     发起的角色
    ///     interactionID   交互点的 ID —— 就是我们塞进 InteractionPoint.ID 的那个
    ///
    /// 两个都用 <c>Write(ushort)</c>, 所以读回来也用同一对
    /// <c>_0023nae()</c>。**不要**自己拆字节 —— 那是这个项目反复吃过亏的地方。
    ///
    /// ## 为什么角色要从消息里读
    ///
    /// 一个客户端可以控制不止一个角色 (观战/宠物), 消息自带 characterID 就是
    /// 为这个。但仍然要**校验它属于这条连接** —— 否则改个包就能替别人偷。
    /// </summary>
    private void HandleStartInteraction(INetBuffer buffer, IClient from)
    {
        try
        {
            ushort characterId = buffer._0023nae();
            ushort interactionId = buffer._0023nae();

            Character actor = _sim.GetCharacter(characterId);

            if (actor == null || actor.Client != from)
            {
                Log.Warning("战斗服 UDP {Port} slot {Index} 想用角色 {Character} 交互, "
                            + "但那个角色不属于它 —— 丢弃",
                    Port, from.Index, characterId);
                return;
            }

            // 两条路互不相干: Scout 的图上只有 _scoutObjective 摆点,
            // 十字路口只有 _crossroads 摆点, 各自认自己的点 ID。
            bool accepted = _scoutObjective.StartInteraction(_sim, interactionId, actor)
                            || _crossroads.StartInteraction(_sim, interactionId, actor);

            Log.Information("战斗服 UDP {Port} slot {Index} 交互点 {Point}: {Result}",
                Port, from.Index, interactionId, accepted ? "开始" : "被拒");

            DrainObjectiveEvents();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 处理 StartInteraction 失败", Port);
        }
    }

    /// <summary>主线交互点这一帧的事件, 逐条打进日志。</summary>
    private void DrainObjectiveEvents()
    {
        for (int i = 0; i < _scoutObjective.Events.Count; i++)
        {
            Log.Information("战斗服 UDP {Port} {Event}", Port, _scoutObjective.Events[i]);
        }

        _scoutObjective.Events.Clear();

        for (int i = 0; i < _concede.Events.Count; i++)
        {
            Log.Information("战斗服 UDP {Port} {Event}", Port, _concede.Events[i]);
        }

        _concede.Events.Clear();

        for (int i = 0; i < _crossroads.Events.Count; i++)
        {
            Log.Information("战斗服 UDP {Port} {Event}", Port, _crossroads.Events[i]);
        }

        _crossroads.Events.Clear();
    }

    private void OnGameMessage(GameMessageType type, INetBuffer buffer, IClient from)
    {
        // Debug 级: 消息路由早就通了, 而 ClientStats 每局刷五百行。
        Log.Debug("战斗服 UDP {Port} slot {Index} 收到游戏消息 {Type}",
            Port, from.Index, type);

        // 两条消息含义不同, 但下一步状态相同:
        //   SkipTutorialIntro     —— 玩家按了 SPACE, 想跳过
        //   TutorialCameraComplete —— 开场镜头自然放完了
        // 都是"开场结束, 该醒了"。
        if (type == GameMessageType.SkipTutorialIntro || type == GameMessageType.TutorialCameraComplete)
        {
            AdvanceTutorialPastIntro();
        }

        if (type == GameMessageType.Respawn)
        {
            HandleRespawn(from);
        }

        // 客户端打开计分板时会发一条**空的** PlayerData 上来要数据
        // (GameManagerClient.SendRequestPlayerData:1424)。不应答的话计分板
        // 那一整块数字全是 0 —— 玩家实测报的就是这个。
        if (type == GameMessageType.PlayerData && from is Client asking)
        {
            int sent = DIE.Server.Game.PlayerDataSender.Send(_sim, _network, asking);
            Log.Debug("战斗服 UDP {Port} slot {Index} 索取计分板数据, 已下发 {Sent} 份",
                Port, from.Index, sent);
        }

        if (type == GameMessageType.UpgradeAbility)
        {
            HandleUpgradeAbility(buffer, from);
        }

        if (type == GameMessageType.StartInteraction)
        {
            HandleStartInteraction(buffer, from);
        }

        // 认输投票 (Options 菜单的 Concede / 投票框的 Yes No)。见 ConcedeVote。
        if (type == GameMessageType.Concede)
        {
            try
            {
                _concede.Receive(_sim, _network, from, (ConcedeMessageType)buffer._0023jae());
                DrainObjectiveEvents();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "战斗服 UDP {Port} 处理 Concede 失败", Port);
            }
        }

        // ---- 局内的三条"人对人"消息: 纯转发 (见 SocialRelay) ----
        if (type == GameMessageType.Chat)
        {
            try
            {
                int sent = DIE.Server.Game.SocialRelay.RelayChat(_sim, _network, from, buffer, _lastChat);
                Log.Debug("战斗服 UDP {Port} slot {Index} 聊天, 转发给 {Sent} 人", Port, from.Index, sent);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "战斗服 UDP {Port} 转发聊天失败", Port);
            }
        }

        if (type == GameMessageType.Emote)
        {
            try
            {
                DIE.Server.Game.SocialRelay.RelayEmote(_network, from, buffer);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "战斗服 UDP {Port} 转发表情失败", Port);
            }
        }

        if (type == GameMessageType.MiniMapPlingData)
        {
            try
            {
                DIE.Server.Game.SocialRelay.RelayPing(_network, from, buffer);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "战斗服 UDP {Port} 转发小地图标记失败", Port);
            }
        }

        // ---- 观战 (见 SpectatorPolicy) ----
        if (type == GameMessageType.DeadSpectator)
        {
            HandleSpectate(buffer, from);
        }

        if (type == GameMessageType.SetObserveMode)
        {
            HandleSetObserveMode(buffer, from);
        }

        WarnIfUnhandled(type, from);

        if (type == GameMessageType.LoadingComplete)
        {
            foreach (KeyValuePair<_0023tde, int> slot in _slots)
            {
                if (slot.Value == from.Index && _channels.TryGetValue(slot.Key, out ClientChannels ch))
                {
                    ch.LoadingComplete = true;

                    // 游戏逻辑层自己也有一份标记。客户端里它只被置 false (Client.cs:290),
                    // 说明原厂是服务端负责置真的。目前只有 AFK 计时读它
                    // (Player.UpdateAFK:1549) —— 不设的话 AFK 永远不计时。
                    if (from is Client gameClient)
                    {
                        gameClient.HasLoadedGame = true;
                    }

                    Log.Information("战斗服 UDP {Port} slot {Index} 已就绪, 开始下发世界状态 (同步对象 {Count})",
                        Port, from.Index, _network.NumOfSynchronizables);

                    // 客户端加载完了才发 ClientInfo —— 它带着装备、队伍、显示名,
                    // 而那些要等客户端的表建起来才解得了。
                    int infoSent = DIE.Server.Game.ClientInfoSender.Broadcast(_network);
                    Log.Information("战斗服 UDP {Port} 已下发 ClientInfo {Count} 份 (装备/队伍/相机朝向)",
                        Port, infoSent);

                    StartMatchIfReady();
                }
            }
        }
    }

    /// <summary>
    /// 死了之后观战队友 (<c>GameMessageType.DeadSpectator</c>)。
    ///
    /// 客户端发一个 <c>int</c> 的 <c>IndexPlayer</c> (<c>SendSpectate</c>,
    /// GameManagerClient.cs:1273), 它自己在 <c>teamMembers</c> 里轮换找下一个活人
    /// (UnityClient.cs:2510)。服务端要把视野中心跟着挪过去 —— 不挪的话客户端镜头是挪了,
    /// 那边却一个实体都没下发, 观战画面是空地。
    ///
    /// 判定和安全线都在 <see cref="DIE.Server.Game.SpectatorPolicy"/>。
    /// </summary>
    private void HandleSpectate(INetBuffer buffer, IClient from)
    {
        if (from is not Client client)
        {
            return;
        }

        try
        {
            int playerIndex = buffer._0023Hae();

            DIE.Server.Game.SpectatorPolicy.Result result =
                DIE.Server.Game.SpectatorPolicy.TryFocus(
                    _sim, client, playerIndex, _observerSlots.Contains(from.Index), out Player target);

            if (result == DIE.Server.Game.SpectatorPolicy.Result.Focused)
            {
                Log.Debug("战斗服 UDP {Port} slot {Index} 观战 {Target} ({Team})",
                    Port, from.Index, target.Name, target.TeamId);
                return;
            }

            // 被拒是**正常**的: 客户端轮换时会撞上刚死的队友, 连点也会重复请求。
            // 所以走 Debug, 不是 Warning。
            Log.Debug("战斗服 UDP {Port} slot {Index} 观战请求被拒: {Why} (目标 {Index2})",
                Port, from.Index, result, playerIndex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 处理观战请求失败", Port);
        }
    }

    /// <summary>
    /// 观察者切"自由镜头 / 跟随角色" (<c>GameMessageType.SetObserveMode</c>)。
    ///
    /// 只有命令行 <c>+observer</c> 启动的客户端会发 (UnityClient.cs:1785/2088/2973),
    /// 而且只对入场时就是观众的连接生效 —— 见 <see cref="DIE.Server.Game.SpectatorPolicy"/>。
    ///
    /// 切完要重发 ClientInfo: <c>IsObserver</c> 在那份数据里同步 (Client.cs:496),
    /// 而且它决定控制器数据的线格式, 双端对不上就会解析错位。
    /// </summary>
    private void HandleSetObserveMode(INetBuffer buffer, IClient from)
    {
        if (from is not Client client)
        {
            return;
        }

        try
        {
            byte mode = buffer._0023jae();
            bool isObserver = _observerSlots.Contains(from.Index);

            if (!DIE.Server.Game.SpectatorPolicy.TrySetObserveMode(client, mode, isObserver))
            {
                Log.Warning("战斗服 UDP {Port} slot {Index} 想切观察模式 {Mode}, 但它入场时不是观众 —— 拒绝",
                    Port, from.Index, mode);
                return;
            }

            int sent = DIE.Server.Game.ClientInfoSender.Broadcast(_network);
            Log.Information("战斗服 UDP {Port} slot {Index} 观察模式 -> {Mode} (已重发 ClientInfo {Sent} 份)",
                Port, from.Index, client.IsObserver ? "自由镜头" : "跟随角色", sent);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 处理观察模式切换失败", Port);
        }
    }

    /// <summary>
    /// 握手时以**观众**身份进来的槽位。
    ///
    /// 和会被改写的 <c>Client.IsObserver</c> 分开记 —— 后者是"当前在不在自由镜头",
    /// 这个是"有没有资格进自由镜头"。见 <see cref="DIE.Server.Game.SpectatorPolicy"/>。
    /// </summary>
    private readonly HashSet<int> _observerSlots = new HashSet<int>();

    /// <summary>
    /// 客户端发上来但我们没接的游戏消息 —— 每种每局只报一次。
    ///
    /// ## 为什么要这条
    ///
    /// 战斗服的分发是一串 <c>if</c>, 没接的消息**静默丢掉**, 日志里一行都没有 ——
    /// 菜单服和匹配服都有兜底告警 (<c>RequestServer.OnUnhandledRequest</c>),
    /// 只有这里没有。2026-09-18 审校时才发现聊天 / 表情 / 小地图标记三条一直在被丢,
    /// 就是因为这里不出声。
    ///
    /// ## 已知不接的 (不报警, 免得刷屏)
    ///
    ///   BuyConsumable / UpgradeWeapon   局内商店, 只在 TvT 生效
    ///                                   (<c>GetWeaponUpgradeHudDatas</c> 开头就是 !IsTeamvsTeam 返回空),
    ///                                   而 TvT 我们没做
    ///   ClientStats / StatisticsData    遥测
    ///   Ready                           Horde 的准备窗口 (GUI_EventReadyWindow), 没开 Horde
    ///   PrepairRespawn                  客户端**从不发**: SendPrepairRespawn 零调用方,
    ///                                   UnityClient 里那个 _PrepairRespawnSent 是个死字段。
    ///                                   (GameMode.PrepairRespawn(Player) 是另一回事 —— 那是
    ///                                   服务端该调的相机钩子, ScoutMission 覆盖了它)
    /// </summary>
    private void WarnIfUnhandled(GameMessageType type, IClient from)
    {
        switch (type)
        {
            // 接了的
            case GameMessageType.SkipTutorialIntro:
            case GameMessageType.TutorialCameraComplete:
            case GameMessageType.Respawn:
            case GameMessageType.PlayerData:
            case GameMessageType.UpgradeAbility:
            case GameMessageType.StartInteraction:
            case GameMessageType.Concede:
            case GameMessageType.LoadingComplete:
            case GameMessageType.Chat:
            case GameMessageType.Emote:
            case GameMessageType.MiniMapPlingData:

            case GameMessageType.DeadSpectator:
            case GameMessageType.SetObserveMode:

            // 明确不接的 (理由见方法说明)
            case GameMessageType.BuyConsumable:
            case GameMessageType.UpgradeWeapon:
            case GameMessageType.ClientStats:
            case GameMessageType.StatisticsData:
            case GameMessageType.Ready:
            case GameMessageType.PrepairRespawn:
                return;
        }

        if (_warnedMessageTypes.Add(type))
        {
            Log.Warning("战斗服 UDP {Port} slot {Index} 发来没接的游戏消息 {Type}({Id}) —— 本局只报这一次",
                Port, from.Index, type, (int)type);
        }
    }

    /// <summary>已经报过一次的消息类型 (只在泵线程上读写)。</summary>
    private readonly HashSet<GameMessageType> _warnedMessageTypes = new HashSet<GameMessageType>();

    /// <summary>聊天限流用: 每个槽位上一条的时刻 (见 <see cref="DIE.Server.Game.SocialRelay.RelayChat"/>)。</summary>
    private readonly Dictionary<int, float> _lastChat = new Dictionary<int, float>();

    /// <summary>
    /// 玩家按左键要求复活。
    ///
    /// 客户端**无条件**发这条消息 (UnityClient.cs:2331 只判了"处于死亡态"),
    /// 玩家还会连点, 所以判定全在服务端 —— 见 <see cref="RespawnPolicy"/>。
    ///
    /// 日志按结果分级: 真正复活了才打 Information, 冷却中打 Debug
    /// (连点会刷屏), "没死"直接不打 —— 那是连点时最常见的情况, 完全正常。
    /// </summary>
    private void HandleRespawn(IClient from)
    {
        if (from is not Client client)
        {
            return;
        }

        DIE.Server.Game.RespawnPolicy.Result result =
            DIE.Server.Game.RespawnPolicy.TryRespawn(_sim, client, PickRespawnPoint(client),
                                                     out float remaining);

        switch (result)
        {
            case DIE.Server.Game.RespawnPolicy.Result.Respawned:
                Log.Information("战斗服 UDP {Port} slot {Index} 已复活 @ ({X:F1},{Y:F1})",
                    Port, client.Index, client.LocalPlayer.Position.X, client.LocalPlayer.Position.Y);
                break;

            case DIE.Server.Game.RespawnPolicy.Result.StillOnCooldown:
                Log.Debug("战斗服 UDP {Port} slot {Index} 复活冷却中, 还差 {Remaining:F1} 秒",
                    Port, client.Index, remaining);
                break;

            case DIE.Server.Game.RespawnPolicy.Result.NoPlayer:
                Log.Warning("战斗服 UDP {Port} slot {Index} 请求复活但没有玩家实体", Port, client.Index);
                break;
        }
    }

    /// <summary>
    /// 技能加点。
    ///
    /// 教程里客户端**每帧自动**发这条消息 (GameManagerClient.cs:1611 起),
    /// 玩家什么都不用按。服务端不回 <c>UpgradeAbilityDone</c> 的话它会卡在
    /// 1 秒节流上反复重发 —— 表现正是"技能显示能升级但加不上去"。
    ///
    /// 被拒是**正常**的: 点数不够时自动加点每秒撞一次。所以那条走 Verbose,
    /// 否则日志会被淹掉 —— 这个坑上一轮已经踩过一次了。
    /// </summary>
    private void HandleUpgradeAbility(INetBuffer buffer, IClient from)
    {
        if (from is not Client client)
        {
            return;
        }

        DIE.Server.Game.AbilityUpgradeHandler.Result result =
            DIE.Server.Game.AbilityUpgradeHandler.Handle(_network, client, buffer,
                                                         out int slotId, out int level);

        switch (result)
        {
            case DIE.Server.Game.AbilityUpgradeHandler.Result.Upgraded:
                Log.Information("战斗服 UDP {Port} slot {Index} 技能加点: 槽位 {Slot} -> {Level} 级 (剩余点数 {Left})",
                    Port, client.Index, slotId, level,
                    (client.LocalPlayer as Player)?.GetUnspentAbilityUpgrades() ?? -1);
                break;

            case DIE.Server.Game.AbilityUpgradeHandler.Result.Rejected:
                Log.Verbose("战斗服 UDP {Port} slot {Index} 加点被拒 (槽位 {Slot}, 点数不够或已满级)",
                    Port, client.Index, slotId);
                break;

            case DIE.Server.Game.AbilityUpgradeHandler.Result.NoPlayer:
                Log.Warning("战斗服 UDP {Port} slot {Index} 请求加点但没有玩家实体", Port, client.Index);
                break;
        }
    }

    /// <summary>
    /// 每秒打一次模拟状态。
    ///
    /// 这几个值决定了客户端能不能按 SPACE 跳过开场动画:
    /// <c>MatchLength</c> 只在 <c>IsStarted</c> 为真时累加 (GameMode.cs:1372),
    /// 经同步表下发后, 客户端要 <c>MatchLength &gt; 1f</c> 才会发 SkipTutorialIntro
    /// (UnityClient.cs:2324)。
    ///
    /// 服务端这份数字是**判断故障在哪一侧**的依据: 它不涨 = 服务端没在推进;
    /// 它涨了而客户端仍无反应 = 复制或客户端侧的问题。没有这条日志就只能靠猜。
    /// </summary>
    /// <summary>
    /// 每 <see cref="ProfileWindowSeconds"/> 秒把分段计时汇总成一行。有慢帧 (超过一个
    /// 逻辑帧的预算) 时升成警告, 并带上最近那一帧是哪几段慢。
    /// </summary>
    private void LogProfile()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastProfile < ProfileWindowSeconds)
        {
            return;
        }

        double window = now - _lastProfile;
        _lastProfile = now;
        int slow = Profiler.SlowFrames;
        string lastSlow = Profiler.LastSlowFrame;
        string summary = Profiler.Flush(window);
        if (summary == null || _slots.Count == 0)
        {
            return;
        }

        if (slow > 0)
        {
            Log.Warning("战斗服 UDP {Port} 性能 ({Window:F0}s): {Summary} —— 最近一次慢帧 {Slow}",
                Port, window, summary, lastSlow);
        }
        else
        {
            Log.Information("战斗服 UDP {Port} 性能 ({Window:F0}s): {Summary}", Port, window, summary);
        }
    }

    private void LogSimulationState()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastDiagnostic < 5.0 || _slots.Count == 0)
        {
            return;
        }

        _lastDiagnostic = now;

        GameMode mode = _sim?.GameMode;
        if (mode == null)
        {
            return;
        }

        // 玩家的生命/怒气。
        //
        // 玩家报告"空击也扣自己血"。单元测试里属性系统是正常的
        // (BergArmored: Health 300/300, Rage 0/3, 施加 Rage 变更不影响 Health),
        // 所以要么扣血发生在真机独有的路径上, 要么客户端显示的根本不是服务端的值。
        //
        // 这一行把判断变成观测: 服务端的数字在掉 = 确实有东西在扣血, 去查扣血源;
        // 服务端不掉而客户端在掉 = 客户端本地预测或 HUD 的问题, 与服务端无关。
        string vitals = "无玩家";
        if (_sim.ActivePlayerList.Count > 0)
        {
            ConductorGameLogic.Entities.Player p = _sim.ActivePlayerList[0];
            vitals = $"HP {p.Health:F0}/{p.MaxHealth:F0} 怒气 {p.Rage:F1} 死亡={p.IsDead} " +
                     $"经验 {p.Stats.GetValue(ConductorGameLogic.StatType.Experience):F0} 等级 {p.GetLevel()}";

            // 装备状态 —— 和客户端的表现对照用。
            //
            // "打僵尸不掉血"排查了三轮, 每轮都在推测客户端和服务端谁不一致。
            // 把服务端这边的事实摊出来: 选中的槽位、那个槽位有没有物品、
            // 槽 0/1 上挂的是哪个技能。
            //
            // 客户端如果是空手, 它发的技能是 FistsPrimary/FistsSecondary;
            // 服务端要是 HeavyPrimary, 两边就对不上 —— 那时这一行会直接显示出来。
            ConductorGameLogic.Crafting.Item w0 = p.GetWeapon(0);
            ConductorGameLogic.Abilities.Ability a0 = p.AbilityBar.GetAbility(0);
            ConductorGameLogic.Abilities.Ability a1 = p.AbilityBar.GetAbility(1);

            vitals += $" | 武器槽={p.EquippedWeaponSlot} 槽0物品={(w0?.ItemID ?? 0)} " +
                      $"技能0={(a0 == null ? "空" : a0.AbilityIdentifier.ToString())} " +
                      $"技能1={(a1 == null ? "空" : a1.AbilityIdentifier.ToString())} " +
                      $"船桨标志={p.ReceivedTutorialPaddle}";
        }

        // 僵尸侧的三个数字, 用来把"僵尸不动"拆成可区分的几种故障:
        //
        //   刷怪点 已激活/总数  —— 0 = 玩家还没走到任何点的触发半径内
        //   在场僵尸 / 追击中    —— 有怪但追击=0 = 选目标那步没过 (距离? 阵营? 无敌?)
        //   出手                 —— 追击>0 而出手=0 = 走不到近战距离, 或技能用不出来
        //
        // 没有这三个数, "僵尸不攻击"只能靠猜是 AI、移动还是技能的问题。
        //   出场中             —— 还卡在出场状态里的僵尸。这些是**动不了**的
        //                          (Character.cs:2598 / NPC.cs:1012 的移动闸门),
        //                          常驻不为 0 就说明 ZombieBrain.FinishEmerge 没放行
        //   未同步               —— 活着但同步位为 false 的僵尸。
        //
        // ⚠️ **"未同步"大于 0 是正常的, 不要当成故障信号。**
        // Entity.ShouldBeSynchronized (Entity.cs:1060) 是原厂的距离剔除:
        // 除了 Player/Trucker/StorageRoom 直接放行, 其余实体只在离观察点
        // (LookAtCharacter.Position) **400 单位**以内才同步。玩家走远之后,
        // 留在原地的僵尸自然全部落到剔除范围外。
        //
        // 我第一次看到"僵尸 8 未同步 7"时以为抓到了隐形怪的根因, 其实那是
        // 玩家已经走到补给点、而僵尸还在六百单位外的木栅栏那边。
        // 有用的读法是**和距离一起看**: 玩家脚边的僵尸却未同步才是问题。
        int zombiesAlive = 0;
        int inSpawnState = 0;
        int notSynced = 0;

        // 玩家**看得见的范围内**却没同步的 —— 这才是隐形怪的判据。
        // 总数那个 notSynced 光看没用: 玩家走到下一段之后, 上一段留下的怪
        // 全在 400 单位剔除范围外, 二三十只未同步是正常的。
        int nearby = 0;
        int nearbyNotSynced = 0;
        float closestNotSynced = float.MaxValue;

        var syncTable = _network.Synchronizables;
        FarseerPhysics.Common.Vector2 eye = FarseerPhysics.Common.Vector2.Zero;
        bool haveEye = false;

        foreach (ConductorGameLogic.Entities.Player player in _sim.ActivePlayerList)
        {
            if (player != null && player.IsActive && !player.IsDead)
            {
                eye = player.Position;
                haveEye = true;
                break;
            }
        }

        foreach (ConductorGameLogic.Entities.GameObjectBase obj in _sim.ActiveGameObjectList)
        {
            if (obj is not ConductorGameLogic.Entities.NPC npc || !npc.IsZombie || npc.IsDead)
            {
                continue;
            }

            zombiesAlive++;

            if (npc.SpawnState_Current != _0023Oq._0023WM._0023Yc)
            {
                inSpawnState++;
            }

            int idx = npc.SyncHelperIndex;
            bool synced = true;
            if (_slots.Count > 0 && idx >= 0 && idx < syncTable.Count
                && syncTable[idx]?._0023pDe(0) == false)
            {
                notSynced++;
                synced = false;
            }

            if (!haveEye)
            {
                continue;
            }

            float distance = FarseerPhysics.Common.Vector2.Distance(npc.Position, eye);
            if (distance > VisibleRadius)
            {
                continue;
            }

            nearby++;
            if (!synced)
            {
                nearbyNotSynced++;
                closestNotSynced = System.Math.Min(closestNotSynced, distance);
            }
        }

        string nearbyReport = nearbyNotSynced > 0
            ? $"{nearbyNotSynced}/{nearby} (最近 {closestNotSynced:F0})"
            : $"0/{nearby}";

        int attacks = _zombieBrain.AttacksIssued;
        _zombieBrain.AttacksIssued = 0;

        int hooks = _zombieBrain.HooksIssued;
        _zombieBrain.HooksIssued = 0;

        int specials = _zombieBrain.SpecialsIssued;
        _zombieBrain.SpecialsIssued = 0;

        Log.Information("战斗服 UDP {Port} 模拟: frame={Frame} started={Started} matchLength={Match:F2} " +
                        "本帧同步条目={Sync} 含GameMode={HasMode} | {Vitals} | " +
                        "刷怪点 {ActivePoints}/{ArmedPoints} 事件 {FiredEvents}/{ArmedEvents} 波次 {Waves} " +
                        "待出场 {Pending} 僵尸 {Zombies} 出场中 {InSpawn} 未同步 {NotSynced} " +
                        "眼前未同步 {NearbyNotSynced} 峰值包 {PeakBytes}B " +
                        "追击 {Chasing} 回家 {GoingHome} 绕墙 {Detouring} 伤害拉仇恨 {Pulls} " +
                        "出手 {Attacks} 钩舌 {Hooks} 技能 {Specials} 召唤物 {Minions} 炮塔咬住 {Turrets}",
            Port, _network.CurrentFrame, mode.IsStarted, mode.MatchLength,
            DIE.Server.Game.WorldStateWriter.LastWritten,
            DIE.Server.Game.WorldStateWriter.LastIncludedGameMode,
            vitals,
            _zombieDirector.ActivePoints, _zombieDirector.ArmedPoints,
            _eventSpawns.FiredEvents, _eventSpawns.ArmedEvents, _eventSpawns.TotalWaves,
            _eventSpawns.PendingCount,
            zombiesAlive, inSpawnState, notSynced, nearbyReport, _peakPacketBytes,
            _zombieBrain.LastChasing, _zombieBrain.LastGoingHome, _zombieBrain.Pathing.Detouring,
            _zombieBrain.DamagePulls,
            attacks, hooks, specials, _minionBrain.LastActive, _turretBrain.LastEngaged);

        _peakPacketBytes = 0;
    }

    /// <summary>
    /// 所有已连接客户端都就绪之后, 把对局标记为已开始。
    ///
    /// ## 为什么要我们自己做
    ///
    /// <c>GameMode._Started</c> 在整个客户端程序集里**没有任何地方置为 true**:
    /// 只有 Reset 里置 false, 和 Deserialize 里从服务端读。相关钩子
    /// (<c>Start()</c> / <c>EndMatch()</c> / <c>ClientReady()</c>) 全是空的 virtual ——
    /// 实现在从未发布的服务端那半边。
    ///
    /// 倒计时逻辑 <c>UpdateGameCountDown</c> 倒是幸存了 (开头就是
    /// <c>if (!IsServer) return false</c>), 但它归零时调的正是那个空的 <c>Start()</c>,
    /// 而且**全树没有调用者** —— 调它的也是丢失的那部分。
    ///
    /// ## 为什么这一步要紧
    ///
    /// <c>MatchLength</c> 只在 <c>IsStarted</c> 为真时累加 (GameMode.cs:1372),
    /// 而它经同步表下发给客户端。客户端按 SPACE 跳过开场动画的条件正是
    /// <c>MatchLength &gt; 1f</c> (UnityClient.cs:2324) —— 不开局, 那个键永远没反应。
    ///
    /// ## 目前的策略
    ///
    /// 直接置位, 不走倒计时。教程是单人局, 没有等人的必要;
    /// 多人对局要不要复刻 20 秒倒计时, 等做到那里再说。
    /// </summary>
    private void StartMatchIfReady()
    {
        GameMode mode = _sim?.GameMode;
        if (mode == null || mode._Started || !AllClientsLoaded())
        {
            return;
        }

        // 拾荒和十字路口不走这里 —— 它们要在出生区等人, 由原厂的 GameMode.UpdateGameCountDown
        // 收尾 (接线见 ScavengerHuntServer / RaidMissionServer, 开局那一下回调到 BeginMatch)。
        if (WaitsForPlayers)
        {
            return;
        }

        BeginMatch("全部客户端就绪");
    }

    /// <summary>
    /// 这一局开局前要在出生区等人吗 (拾荒 / 十字路口)。等人期间匹配服可以往里补人。
    /// 按队列判, 不按模式对象 —— 匹配服那边的线程要读它, 模拟层只能在泵线程上碰。
    /// </summary>
    public bool WaitsForPlayers =>
        QueueType is ConductorCrafting.QueueType.Scavenger or ConductorCrafting.QueueType.RaidMission
        || (_sim?.GameMode is DIE.Server.Game.ScavengerHuntServer or DIE.Server.Game.RaidMissionServer);

    /// <summary>开局了吗 (倒计时走完 / 非等人模式加载完)。别的线程读。</summary>
    public bool MatchBegun => _begun;

    private volatile bool _begun;

    /// <summary>强制开局的时刻 (模拟时间, 秒)。原厂 <c>GameManager.ForceStartTime</c>, 默认 120。</summary>
    private float _forceStartAt = 120f;

    /// <summary>
    /// 离强制开局还有几秒。模拟时间和泵线程的时钟同起点 (Pump 里 _clock.Start 之后才开始
    /// 喂 NetworkBase.Update), 差的只是建图那零点几秒。
    /// </summary>
    public double SecondsUntilForceStart => _forceStartAt - _clock.Elapsed.TotalSeconds;

    /// <summary>
    /// 离强制开局少于这么多秒就不再往里补人 —— 补进来的人要加载 (真机十几秒),
    /// 加载完已经开打、白墙也撤了, 体验和开一局新的差不多, 不如开新的。**我们定的。**
    /// </summary>
    public static double LateJoinMinSeconds { get; set; } = 30.0;

    private readonly object _lateJoinLock = new();

    /// <summary>
    /// 把后来排队的队伍补进这一局 (开局前在出生区等人的那一段)。
    ///
    /// ## 为什么要补 (2026-09-23 玩家实测)
    ///
    /// "我和一个朋友从两个房间分别开始匹配拾荒, 服务器里总共就我们两个人, 结果分别进了
    /// 两局独立的游戏, 我和他都是橙队。" —— 匹配服凑人只等 5 秒 (<c>GatherWindow</c>),
    /// 点匹配的时间差一超过就各开一局。
    ///
    /// 原厂是**先进地图, 在出生区里等** (最多到 ForceStartTime), 所以等人的那一两分钟里
    /// 后来的人应该进同一局。十字路口按原厂公开匹配凑满 4 人, 同样处理。
    ///
    /// ## 分队 (拾荒)
    ///
    /// 每支队伍整队进同一队 (小队不拆, 同 <see cref="ScavengerTeams"/>), 放进**人最少**的那一队;
    /// 哪一队都放不下就整批拒绝。别的模式都是一队 (Team = 0)。
    /// </summary>
    /// <param name="parties">要补进来的队伍, 每支队伍是一串位子。</param>
    /// <param name="why">拒绝时的原因 (日志用)。</param>
    public bool TryAddLateJoiners(IReadOnlyList<IReadOnlyList<MatchMember>> parties, out string why)
    {
        lock (_lateJoinLock)
        {
            int incoming = parties.Sum(p => p.Count);
            int capacity = GameModeAvailability.QueueCapacity(QueueType);

            why = !_running ? "战斗服已关"
                : !SimulationReady || SimulationError != null ? "模拟层没就绪"
                : !WaitsForPlayers ? "这个模式开局不等人"
                : _begun || MatchOver ? "已经开局"
                : SecondsUntilForceStart < LateJoinMinSeconds ? $"离强制开局只剩 {SecondsUntilForceStart:F0} 秒"
                : Roster.Count + incoming > capacity ? $"人满 ({Roster.Count}+{incoming}>{capacity})"
                : null;
            if (why != null)
            {
                return false;
            }

            // 补进来的人用的角色, 建图时的缓存表里得还有空位 (见 CharactersToCache 末尾那段)
            why = MissingCharacterSlot(parties.SelectMany(p => p));
            if (why != null)
            {
                return false;
            }

            List<MatchMember> placed = new List<MatchMember>();
            if (QueueType == ConductorCrafting.QueueType.Scavenger)
            {
                int[] sizes = new int[ScavengerTeams.TeamCount + 1];
                foreach (MatchMember member in Roster.Members)
                {
                    if (member.Team >= 1 && member.Team <= ScavengerTeams.TeamCount)
                    {
                        sizes[member.Team]++;
                    }
                }

                foreach (IReadOnlyList<MatchMember> party in parties.OrderByDescending(p => p.Count))
                {
                    int team = Enumerable.Range(1, ScavengerTeams.TeamCount)
                        .Where(t => sizes[t] + party.Count <= ScavengerTeams.TeamSize)
                        .OrderBy(t => sizes[t])
                        .ThenBy(t => t)
                        .DefaultIfEmpty(0)
                        .First();
                    if (team == 0)
                    {
                        why = $"{party.Count} 人的队伍哪一队都放不下";
                        return false;
                    }

                    sizes[team] += party.Count;
                    placed.AddRange(party.Select(m => m with { Team = (byte)team }));
                }
            }
            else
            {
                placed.AddRange(parties.SelectMany(p => p));
            }

            Roster.Add(placed);
            Log.Information("战斗服 UDP {Port} 开局前补进 {Count} 人 (名单 {Total}/{Capacity}, 离强制开局 {Seconds:F0} 秒): {Who}",
                Port, placed.Count, Roster.Count, capacity, SecondsUntilForceStart,
                string.Join(", ", placed.Select(m => $"{m.UserId}:{m.Choice?.Character}(队{m.Team})")));
            return true;
        }
    }

    /// <summary>
    /// 这些人的角色在建图时的缓存表里还有没有空位。有就返回 null, 没有返回原因。
    /// 缓存表为空 (= 两边都全部预建, 名单里有人没带角色选择时) 视为都有。
    /// 没带角色选择的人进场拿 BergSurvivor (TrySpawnPlayer 的兜底), 按它算。
    /// </summary>
    private string MissingCharacterSlot(IEnumerable<MatchMember> joiners)
    {
        CharacterEnum[] cached = CachedCharacters;
        if (cached.Length == 0)
        {
            return null;
        }

        static CharacterEnum CharacterOf(MatchMember m) => m.Choice?.Character ?? CharacterEnum.BergSurvivor;

        foreach (IGrouping<CharacterEnum, MatchMember> wanted in joiners.GroupBy(CharacterOf))
        {
            int slots = cached.Count(c => c == wanted.Key);
            int taken = Roster.Members.Count(m => CharacterOf(m) == wanted.Key);
            if (taken + wanted.Count() > slots)
            {
                return $"角色 {wanted.Key} 在这一局的缓存表里没有空位了 ({taken}+{wanted.Count()}>{slots})";
            }
        }

        return null;
    }

    /// <summary>已连进来的客户端是不是都加载完了。</summary>
    private bool AllClientsLoaded()
    {
        foreach (KeyValuePair<_0023tde, int> slot in _slots)
        {
            if (_channels.TryGetValue(slot.Key, out ClientChannels ch) && !ch.LoadingComplete)
            {
                return false;
            }
        }

        return true;
    }

    private void BeginMatch(string why)
    {
        GameMode mode = _sim.GameMode;
        mode._Started = true;
        _begun = true;
        Log.Information("战斗服 UDP {Port} 对局开始 ({Why})", Port, why);

        ArmRoamingZombies();
        ArmEventSpawns();
        SpawnStorageRooms();
        PlaceStaticZombies();
        SpawnSupplyPoints();
        SpawnTutorialEntities();
        SpawnDestructibles();
        SpawnTutorialWorld();
        PrimeTutorialNpc();
        ArmStorageRoomCapture();
    }

    /// <summary>
    /// 认一遍补给点和它的刷怪位。要在 <c>SpawnStorageRooms</c> 之后 ——
    /// 虽然 Scan 只读地图对象, 但日志里报的点数得和实体数对得上才有意义。
    /// </summary>
    private void ArmStorageRoomCapture()
    {
        try
        {
            _capture.Scan(_sim);
            Log.Information("战斗服 UDP {Port} 补给点占领已登记 {Count} 个 " +
                            "(独占 {Seconds:F0} 秒, {Waves} 波, 完成掉 {Crates}×{Each} 补给)",
                Port, _capture.ArmedPoints, _capture.SecondsToCapture,
                _capture.Waves, _capture.CrateCount, _capture.SuppliesPerCrate);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 登记补给点占领失败", Port);
        }

        ArmHordeSpawners();
        ArmRaidRespawns();
        ArmProximitySpawns();
        ArmScoutStory();
        ArmTransmissions();
        ArmScavenger();
    }

    /// <summary>认拾荒的队伍、路障和补给点 (要在路障生成之后)。非拾荒图什么都不做。</summary>
    private void ArmScavenger()
    {
        try
        {
            _scavenger.Scan(_sim);
            LogScavengerEvents();

            // 路障旁的漫游点 (BarricadeSpawn) 只在附近那道路障开打时刷 —— 见 ScavengerDirector.BarricadeFightStarted
            if (_scavenger.TeamCount > 0)
            {
                _zombieDirector.AllowBarricadeSpawn = point => _scavenger.BarricadeFightStarted(point.Position);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 登记拾荒流程失败", Port);
        }
    }

    private void LogScavengerEvents()
    {
        for (int i = 0; i < _scavenger.Events.Count; i++)
        {
            Log.Information("战斗服 UDP {Port} {Event}", Port, _scavenger.Events[i]);
        }

        _scavenger.Events.Clear();
    }

    /// <summary>
    /// 认一遍路径点语音。
    ///
    /// 客户端自己去 Unity 资源找语音包, 我们只发三个枚举值 ——
    /// 详见 <see cref="DIE.Server.Game.TransmissionDirector"/>。
    /// </summary>
    private void ArmTransmissions()
    {
        try
        {
            _transmissions.Scan(_sim);

            if (_transmissions.ArmedPoints == 0)
            {
                return;
            }

            Log.Information("战斗服 UDP {Port} 路径点语音已登记 {Count} 条 (走进各自区域时播)",
                Port, _transmissions.ArmedPoints);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 登记路径点语音失败", Port);
        }
    }

    /// <summary>
    /// 认一遍 Starter Mission 的剧情节点。
    ///
    /// Ram 的触发点是地图数据给的 (TransmissionType.SpecialRamScoutTrigger),
    /// Boss 位置取唯一那个 Event_RaidEvent —— 详见
    /// <see cref="DIE.Server.Game.ScoutStoryDirector"/>。
    /// </summary>
    private void ArmScoutStory()
    {
        try
        {
            _scoutStory.Scan(_sim);

            if (_scoutStory.DoorsReleasedAtScan > 0)
            {
                Log.Information("战斗服 UDP {Port} 地图 {Map} 没有 Ram 撞门, "
                                + "{Count} 段 IsDisabledFromStart 的路障已解除无敌 "
                                + "—— 交给玩家自己打 (见 ReleaseDoorsNobodyWillOpen)",
                    Port, MapIndex, _scoutStory.DoorsReleasedAtScan);
            }


            // 主线交互点 (敌方卡车 / 补给点) —— 用剧情扫描认出来的目标类型。
            // 必须在 Scan 之后: 目标类型是它读出来的。
            int points = _scoutObjective.Arm(_sim, _scoutStory.Objective);

            // 非 Scout 图上这三样全是空的 —— 别为每张图都打一行。
            if (!_scoutStory.IsArmed && points == 0
                && _scoutStory.Objective == ConductorGameLogic.View.RaidEventType.None)
            {
                return;
            }

            Log.Information("战斗服 UDP {Port} Starter Mission 剧情节点已登记: "
                            + "主线目标 {Objective}, Ram 撞门 {HasRam}, Boss Looter {HasBoss}, "
                            + "交互点 {Points} 个{Detail}",
                Port, _scoutStory.Objective,
                _scoutStory.HasRamTrigger, _scoutStory.HasBoss, points,
                points == 0 ? string.Empty
                    : $" ({_scoutObjective.Points[0].Type} @ "
                      + $"{_scoutObjective.Points[0].Position.X:F0},"
                      + $"{_scoutObjective.Points[0].Position.Y:F0})");

            DrainObjectiveEvents();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 登记 Starter Mission 剧情节点失败", Port);
        }
    }

    /// <summary>
    /// 登记"走近才出场"的刷怪点。
    ///
    /// Scout 图上没有 Event_Assault, 那 73 个 Spawn_EventEntities 归这里 ——
    /// 详见 <see cref="DIE.Server.Game.ProximitySpawnDirector"/>。
    /// </summary>
    private void ArmProximitySpawns()
    {
        try
        {
            _proximitySpawns.Scan(_sim);

            if (_proximitySpawns.ArmedPoints == 0)
            {
                return;
            }

            Log.Information("战斗服 UDP {Port} 走近出场的刷怪点已登记 {Count} 个 " +
                            "(半径 {Radius:F0}, 因间距 {Spacing:F0} 稀释掉 {Thinned} 个)",
                Port, _proximitySpawns.ArmedPoints, _proximitySpawns.TriggerRadius,
                _proximitySpawns.MinimumSpacing, _proximitySpawns.ThinnedOut);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 登记走近出场刷怪点失败", Port);
        }
    }

    /// <summary>
    /// 认一遍推进式复活点。
    ///
    /// 不认的话玩家死了永远回图头 —— <c>Spawn_RaidRespawn</c> 原厂零消费者,
    /// 详见 <see cref="DIE.Server.Game.RaidRespawnDirector"/>。
    /// </summary>
    private void ArmRaidRespawns()
    {
        try
        {
            _raidRespawns.Scan(_sim);

            if (_raidRespawns.ArmedPoints == 0)
            {
                // 教程图和 Horde 图上没有这类对象, 正常
                return;
            }

            Log.Information("战斗服 UDP {Port} 推进式复活点已登记 {Count} 个 —— " +
                            "玩家踏进关联区域后死亡就在那里复活, 不再回图头",
                Port, _raidRespawns.ArmedPoints);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 登记推进式复活点失败", Port);
        }
    }

    /// <summary>
    /// 建 Horde 图的占领点实体并挂上路障。
    ///
    /// **不建的话玩家一按占领就 NRE** —— <c>HordeMode.cs:205</c> 是
    /// <c>_0023Sob2?._0023Oob.StartCapture()</c>, 而 <c>_0023Oob</c> 要有
    /// <c>HordeSpawner</c> 实体才填得上, 原厂那半边随服务端丢失。
    /// 详见 <see cref="DIE.Server.Game.HordeSpawnerDirector"/>。
    /// </summary>
    private void ArmHordeSpawners()
    {
        try
        {
            _hordeSpawners.Scan(_sim);

            if (_hordeSpawners.SpawnersCreated == 0)
            {
                // 教程图没有 Event_Horde, 这是正常的
                return;
            }

            Log.Information("战斗服 UDP {Port} Horde 占领点已登记 {Count} 个, " +
                            "路障 {Blockages} 段 (占领后 {Seconds:F0} 秒解锁)",
                Port, _hordeSpawners.SpawnersCreated, _hordeSpawners.BlockagesCreated,
                _hordeSpawners.CaptureSeconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 登记 Horde 占领点失败", Port);
        }
    }

    /// <summary>玩家离 Amber 多近就让 Puller 起手 —— 见 <see cref="DIE.Server.Game.TutorialEntitySpawner.HookDistance"/>。</summary>
    private static float HookDistance => DIE.Server.Game.TutorialEntitySpawner.HookDistance;

    /// <summary>
    /// 生成变压器和卡车司机 —— 那两个不来自 Spawn_Tutorial, 而是各自的专用标记。
    ///
    /// 类名是从原厂的类型↔明文名对照表查出来的 (tools/resolve_entity_names.py),
    /// 不是猜的: TutorialLightingGenerator = _0023p0b, Trucker = Trucker (没混淆)。
    /// </summary>
    private void SpawnTutorialWorld()
    {
        try
        {
            DIE.Server.Game.TutorialWorldSpawner.Result result =
                DIE.Server.Game.TutorialWorldSpawner.SpawnAll(_sim);

            Log.Information("战斗服 UDP {Port} 生成变压器 {Generators} 个, 卡车司机 {Truckers} 个",
                Port, result.Generators, result.Truckers);

            if (result.Skipped.Count > 0)
            {
                Log.Warning("战斗服 UDP {Port} 有实体没生成出来: {Skipped}",
                    Port, string.Join(", ", result.Skipped));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 生成变压器/司机失败", Port);
        }
    }

    /// <summary>
    /// 给教程 NPC 装移动参数。**必须做**, 否则 _Acceleration 是 0, 她被救了也动不了 ——
    /// <c>_0023vO()</c> 在客户端树里零调用者, 调它的是丢失的那半个服务端。
    /// 详见 TutorialNpcAI 的说明。
    /// </summary>
    private void PrimeTutorialNpc()
    {
        try
        {
            int primed = DIE.Server.Game.TutorialNpcAI.Prime(_sim);
            if (primed > 0)
            {
                Log.Information("战斗服 UDP {Port} 教程 NPC 移动参数已装上 {Count} 个 " +
                                "(_0023vO: 走 900/100/35, 跑 900/100/45)", Port, primed);
            }
            else
            {
                Log.Warning("战斗服 UDP {Port} 没有教程 NPC 可装移动参数 —— " +
                            "她们会站着不动 (加速度为 0)", Port);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 装教程 NPC 移动参数失败", Port);
        }
    }

    /// <summary>
    /// 算"玩家眼前"用的半径。
    ///
    /// 相机可视范围大约 60~90 单位 (见 ZombieBrain.AggroRadius 的推算),
    /// 取 150 留一档余量 —— 宁可把边缘的也算进来, 也不要漏掉真正的隐形怪。
    /// 远小于 Entity.ShouldBeSynchronized 的 400 剔除半径, 所以这个范围内
    /// **不该有任何未同步的僵尸**。
    /// </summary>
    private const float VisibleRadius = 150f;

    /// <summary>
    /// 这一秒里最大的一个世界状态包有多少字节。宿主打完日志自行清零。
    ///
    /// Lidgren 的 MTU 默认 1408 —— 超了就要分片, 而分片重组要等所有片到齐。
    /// 那正好长得像玩家报的"僵尸冻在原地, 过一会儿突然补上"。
    /// </summary>
    private int _peakPacketBytes;

    /// <summary>上一次报告过的携带补给量, 只在变化时打日志。</summary>
    private int _lastCarriedSupplies = -1;

    /// <summary>
    /// 报告玩家身上背了多少补给。
    ///
    /// 教程状态 16 -> 17 的判据就是这个数 &gt; 0 (过了那一步箭头才会从补给点挪到
    /// 下一个事件)。玩家实测停在 16 没动, 而服务端这边分不出是"他没捡"还是
    /// "我们读错了地方" —— 先把数字打出来, 别猜。
    /// </summary>
    private void LogCarriedSupplies()
    {
        if (_sim?.GameMode is not _00235cc tutorial || tutorial.SupplyData == null)
        {
            return;
        }

        int carried = 0;
        foreach (ConductorGameLogic.Entities.Player player in _sim.ActivePlayerList)
        {
            if (player != null && player.IsActive)
            {
                carried += tutorial.SupplyData._0023Fj(player.IndexPlayer);
            }
        }

        if (carried == _lastCarriedSupplies)
        {
            return;
        }

        _lastCarriedSupplies = carried;
        Log.Information("战斗服 UDP {Port} 玩家携带补给 {Carried} (教程状态 {State})",
            Port, carried, tutorial._0023l9._0023pcc);
    }

    /// <summary>Amber 已经被钩住了 (确认到 buff 在她身上)。之后不再插手。</summary>
    private bool _npcHooked;

    /// <summary>朝 Amber 放过多少次 PullerHook。见 <see cref="MaxHookAttempts"/>。</summary>
    private int _hookAttempts;

    /// <summary>
    /// 放几次技能还钩不上就回退到直接挂 buff。
    ///
    /// 一次完整的起手是 1.5 秒吟唱 + 0.7 秒冷却, 而吟唱/出手期间这个方法直接
    /// 返回不计数, 所以 12 次已经覆盖了二十几秒。**教程绝不能因为一个视觉问题
    /// 卡住** —— 钩不上就退回老办法, Amber 照样被钩着, 只是可能仍然没舌头。
    /// </summary>
    private const int MaxHookAttempts = 12;

    /// <summary>
    /// 让 Puller 真的对 Amber 吐一次舌头, 而不是我们凭空塞给她一个 buff。
    ///
    /// ## 前两次尝试都失败了, 这是第三次
    ///
    /// 玩家报的症状是"Puller 拉着 Amber 的那条舌头看不见"。
    /// 舌头 (<c>Vfx_Puller_Tongue_Drag_01</c>) 由 buff 的 <c>_0023Hq()</c> 播,
    /// 那是**客户端本地**在 <c>Deserialize</c> 判出"新生成"后调 <c>Spawn()</c>
    /// 才跑的 (GameObjectBase.cs:1454), 一辈子只跑一次。
    ///
    ///   1. 对局开始就 <c>ApplyBuff</c> —— 不显示
    ///   2. 玩家走近 300 单位再 <c>ApplyBuff</c> (排除"buff 诞生在视野外")
    ///      —— 仍然不显示, 时序那个推断是错的
    ///
    /// 服务端这一侧已经排干净了, ClientVisibilityTests 三条钉住:
    /// buff 进得了下发列表、Owner(Puller) 也在同一批、玩家走近时同步位会翻。
    ///
    /// 前两次的共同点是**都在凭空 <c>ApplyBuff</c>**。原厂根本没有这条路 ——
    /// 这个 buff 只可能由投射物命中时的 <c>_0023CF</c> 生成 (-EF/-QBc.cs:131)。
    /// 所以这次改成走完整技能链: Puller 起手 → 投射物 <c>_0023QBc</c> 生成并
    /// 同步 (它自己画飞行段舌头 <c>Vfx_Puller_Tongue_01</c>, -EF/-QBc.cs:51)
    /// → 命中 Amber → 原厂代码挂 buff。
    ///
    /// 如果这次还不显示, 就不要再从服务端查了 —— 记进 PROJECT_STATUS 的未完成,
    /// 和"看不见的僵尸"归成同一类。
    ///
    /// ## 不会打断教程
    ///
    /// 状态推进条件是"Puller 死"加 <c>_sawPullerAlive</c>, 从来不看这个 buff。
    /// 而且放不上去还有 <see cref="MaxHookAttempts"/> 次之后的回退兜底。
    /// </summary>
    private void TryHookTutorialNpc()
    {
        if (_npcHooked)
        {
            return;
        }

        try
        {
            ConductorGameLogic.Entities.Entity amber = null;
            ConductorGameLogic.Entities.NPC puller = null;

            foreach (ConductorGameLogic.Entities.Entity entity in _sim.ActiveEntityList)
            {
                if (!entity.IsActive || entity.IsDead)
                {
                    continue;
                }

                if (amber == null && entity is _0023Mk._0023s3b)
                {
                    amber = entity;
                }
                else if (puller == null && entity.IsPuller)
                {
                    puller = entity as ConductorGameLogic.Entities.NPC;
                }
            }

            if (amber == null)
            {
                return;
            }

            // 钩上了 —— 技能命中了, 或者回退挂上了。收工。
            if (amber.HasBuff(_0023Oq._0023aD._0023QBc))
            {
                _npcHooked = true;
                Log.Information("战斗服 UDP {Port} Puller 钩住教程 NPC (Amber), 起手 {Attempts} 次",
                    Port, _hookAttempts);
                return;
            }

            bool anyoneClose = false;
            foreach (ConductorGameLogic.Entities.Player player in _sim.ActivePlayerList)
            {
                if (player.IsActive && !player.IsDead
                    && FarseerPhysics.Common.Vector2.Distance(player.Position, amber.Position) <= HookDistance)
                {
                    anyoneClose = true;
                    break;
                }
            }

            if (!anyoneClose)
            {
                return;   // 玩家还没走到, 让 Amber 先自由着
            }

            if (puller == null)
            {
                _npcHooked = true;
                Log.Warning("战斗服 UDP {Port} 场上没有活着的 Puller, 没法钩住 Amber —— " +
                            "状态推进不依赖它, 但 TutorialPuller01 语音不会播", Port);
                return;
            }

            // 正在吟唱或出手中, 等这一发走完。不计数。
            if (puller.IsPerformingAnAttack || puller.IsChanneling)
            {
                return;
            }

            if (_hookAttempts >= MaxHookAttempts)
            {
                _npcHooked = true;
                bool fallback = DIE.Server.Game.TutorialEntitySpawner.HookNpcWithPuller(_sim);
                Log.Warning("战斗服 UDP {Port} Puller 起手 {Attempts} 次都没钩上 Amber, " +
                            "回退到直接挂 buff (结果 {Result}) —— 舌头大概率仍然不显示, " +
                            "但教程不会卡", Port, _hookAttempts, fallback);
                return;
            }

            _hookAttempts++;

            FarseerPhysics.Common.Vector2 toAmber = amber.Position - puller.Position;
            if (toAmber != FarseerPhysics.Common.Vector2.Zero)
            {
                toAmber.Normalize();
            }

            puller.ChangeAimDirection(toAmber);
            bool cast = puller.Attack(toAmber, ConductorGameLogic.Abilities.AbilityIdentifier.PullerHook);

            Log.Information("战斗服 UDP {Port} 玩家已靠近, Puller 朝 Amber 起手钩舌 " +
                            "(第 {Attempts} 次, 起手{Result}, 距 {Distance:F0})",
                Port, _hookAttempts, cast ? "成功" : "失败",
                FarseerPhysics.Common.Vector2.Distance(puller.Position, amber.Position));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 让 Puller 钩 Amber 失败", Port);
        }
    }

    /// <summary>
    /// 摆放场景静态僵尸 —— 坐着的、在啃东西的那些。
    ///
    /// 这是"僵尸位置和原版对不上"的一半原因: 教程地图有 29 个
    /// <c>Spawn_Static</c> 手摆点 (Sitting 20 / Eating 9), 我们先前一个都没读。
    /// 另一半是 66 个 <c>Spawn_EventEntities</c>, 归 <see cref="ArmEventSpawns"/>。
    /// </summary>
    private void PlaceStaticZombies()
    {
        try
        {
            DIE.Server.Game.StaticZombiePlacer.Result result =
                DIE.Server.Game.StaticZombiePlacer.Place(_sim);

            if (result.Markers == 0)
            {
                Log.Information("战斗服 UDP {Port} 地图 {Map} 没有静态摆位僵尸", Port, MapIndex);
                return;
            }

            Log.Information("战斗服 UDP {Port} 摆放静态僵尸 {Placed}/{Markers} 只 " +
                            "(可重生 {Respawnable}): {ByPose}",
                Port, result.Placed, result.Markers, result.Respawnable,
                string.Join(", ", result.ByPose.Select(kv => $"{kv.Key}×{kv.Value}")));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 摆放静态僵尸失败", Port);
        }
    }

    /// <summary>
    /// 登记事件刷怪点 (<c>Event_TriggerPosition</c> → <c>Event_Assault</c> →
    /// <c>Spawn_EventEntities</c>, 靠 ID 三层关联)。真正刷怪在
    /// <see cref="DIE.Server.Game.EventSpawnDirector"/> 里等玩家踩触发点。
    ///
    /// 日志会把**没实现的事件类型**和它们占掉的摆位点数打出来 —— 那是下一步
    /// 要补的清单, 不是错误。(Ambush 和 StorageRoom 后来都做了, 见
    /// <see cref="DIE.Server.Game.AmbushDirector"/> 和
    /// <see cref="DIE.Server.Game.StorageRoomCaptureDirector"/>。)
    /// </summary>
    /// <summary>
    /// 生成补给点实体 (教程那个 lifeguard station, 玩家叫它"旗帜")。
    ///
    /// 不生成的话客户端教程状态 14 的箭头会指向 **(0, 0)** —— 那是
    /// <c>StorageRoom.Position</c> 在没配上地图点时的返回值, 落在地图外。
    /// 详见 <see cref="DIE.Server.Game.StorageRoomSpawner"/>。
    /// </summary>
    private void SpawnStorageRooms()
    {
        try
        {
            var result = DIE.Server.Game.StorageRoomSpawner.SpawnAll(_sim);

            if (result.Points == 0)
            {
                return;
            }

            Log.Information("战斗服 UDP {Port} 补给点已生成 {Spawned}/{Points} 个 " +
                            "(教程的 lifeguard station; 位置靠 ID 和 StorageRoomPoint 配对, " +
                            "配不上时 Position 会是 (0,0) 把箭头带到地图外)",
                Port, result.Spawned, result.Points);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 生成补给点失败", Port);
        }
    }

    private void ArmEventSpawns()
    {
        try
        {
            _eventSpawns.Scan(_sim);

            // 埋伏刷怪单独一个 —— 语义和 Assault 完全不同 (持续补怪 vs 一次一批),
            // 触发条件也不同 (路障掉血 vs 玩家走进半径)。
            _ambushes.Scan(_sim);
            if (_ambushes.ArmedAmbushes > 0)
            {
                Log.Information("战斗服 UDP {Port} 登记埋伏刷怪 {Count} 组 " +
                                "(打那组路障就会持续涌怪)", Port, _ambushes.ArmedAmbushes);
            }

            if (_eventSpawns.ArmedEvents == 0 && _eventSpawns.SkippedEvents.Count == 0)
            {
                Log.Information("战斗服 UDP {Port} 地图 {Map} 没有事件刷怪点", Port, MapIndex);
                return;
            }

            Log.Information("战斗服 UDP {Port} 登记事件刷怪 {Armed} 组", Port, _eventSpawns.ArmedEvents);

            if (_eventSpawns.SkippedEvents.Count > 0)
            {
                Log.Warning("战斗服 UDP {Port} 未实现的事件类型占用了摆位点: {Skipped}",
                    Port, string.Join(", ", _eventSpawns.SkippedEvents.Select(kv => $"{kv.Key}×{kv.Value}")));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 登记事件刷怪失败", Port);
        }
    }

    /// <summary>
    /// 按地图数据生成路障 / 栅栏。
    ///
    /// 实体类 <c>_0023EK._0023Yh</c> 一直都在 (668 行, 完整), 只是在混淆
    /// 命名空间里 —— 我先前按 <c>ConductorGameLogic.Entities.Destructibles</c>
    /// 命名空间去找, 里面只剩个枚举, 就误判成"被链接器裁了"。
    ///
    /// 血量是**我们定的** (地图数据里没有这个字段), 见 DestructibleSpawner。
    /// </summary>
    private void SpawnDestructibles()
    {
        try
        {
            DIE.Server.Game.DestructibleSpawner.Result result =
                DIE.Server.Game.DestructibleSpawner.SpawnAll(_sim, _destructibleRewards,
                    skip: spawn => DIE.Server.Game.ScavengerDirector.SkipAtStart(_sim, spawn));

            if (result.Spawned == 0)
            {
                Log.Information("战斗服 UDP {Port} 地图 {Map} 没有路障", Port, MapIndex);
                return;
            }

            // 血量按类型不同, 所以逐类打 —— 先前统一打 DefaultHealth,
            // 木栅栏明明是 1400 却显示 200, 一条误导性的日志。
            Log.Information("战斗服 UDP {Port} 生成路障 {Total} 段: {ByType} | 队伍 {ByTeam}",
                Port, result.Spawned,
                string.Join(", ", result.ByType.Select(kv =>
                    $"{kv.Key}×{kv.Value}@{DIE.Server.Game.DestructibleSpawner.HealthFor(kv.Key):F0}血")),
                string.Join(", ", result.ByTeam.Select(kv => $"{kv.Key}×{kv.Value}")));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 生成路障失败", Port);
        }
    }

    /// <summary>
    /// 生成教程用的实体 (Puller / Floater 等)。
    ///
    /// <c>Spawn_Tutorial</c> 是教程的"演员名单" —— 客户端只消费 PADDLE 和
    /// PREBARRICADE 两个纯位置标记, 其余七个类型一次都没读过, 那是给服务端的
    /// 生成指令。不生成的话教程要杀的 Puller、Floater 全都不在场上, 对应的
    /// 步骤永远完不成。
    ///
    /// 日志会把**没能识别的类型**也打出来 —— 那是下一步要补的清单, 不是错误。
    /// </summary>
    private void SpawnTutorialEntities()
    {
        try
        {
            DIE.Server.Game.TutorialEntitySpawner.Result result =
                DIE.Server.Game.TutorialEntitySpawner.SpawnAll(_sim);

            if (result.Spawned > 0)
            {
                Log.Information("战斗服 UDP {Port} 教程实体已生成 {Total} 个: {ByType} | {Placed}",
                    Port, result.Spawned,
                    string.Join(", ", result.ByType.Select(kv => $"{kv.Key}×{kv.Value}")),
                    string.Join(" ", result.Placed));
            }

            if (result.SkippedByType.Count > 0)
            {
                Log.Warning("战斗服 UDP {Port} 教程里有认不出实体类的标记点, 已跳过: {Skipped} " +
                            "—— 这些位置上不会有东西, 相关步骤会卡住",
                    Port, string.Join(", ", result.SkippedByType.Select(kv => $"{kv.Key}×{kv.Value}")));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 生成教程实体失败", Port);
        }
    }

    /// <summary>
    /// 按地图数据铺补给箱。
    ///
    /// 教程的胜利条件是收集 1000 补给 (<c>TutorialGameModeSettings.SuppliesToWin</c>),
    /// 而死亡掉落只有 Looter / 特殊僵尸 / 精英才产出 —— 教程刷怪点只生成 Walker,
    /// 所以**地图上预铺的补给点才是教程补给的主来源**。
    ///
    /// 和刷怪不同, 这个不做就近触发: 补给箱是静态收集物, 玩家看到才知道要去捡。
    /// </summary>
    private void SpawnSupplyPoints()
    {
        try
        {
            DIE.Server.Game.SupplyPointSpawner.Result result =
                DIE.Server.Game.SupplyPointSpawner.SpawnAll(_sim);

            if (result.Points == 0)
            {
                Log.Information("战斗服 UDP {Port} 地图 {Map} 没有补给点", Port, MapIndex);
                return;
            }

            Log.Information("战斗服 UDP {Port} 从 {Points} 个补给点铺了 {Crates} 个补给箱, 共 {Total} 补给",
                Port, result.Points, result.Spawned, result.TotalSupplies);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 铺补给箱失败", Port);
        }
    }

    /// <summary>
    /// 把一条游戏消息排进目标客户端的可靠通道。
    ///
    /// <paramref name="client"/> 为 null = 广播, 与客户端那侧
    /// <c>NetworkBase.SendGameMessage</c> 的默认值语义一致。
    /// </summary>
    private void QueueOutgoing(_00237ze netBuffer, IClient client, int channel,
                               bool inOrder, float resendRate)
    {
        foreach (KeyValuePair<_0023tde, int> slot in _slots)
        {
            if (client != null && slot.Value != client.Index)
            {
                continue;
            }

            if (!_channels.TryGetValue(slot.Key, out ClientChannels channels))
            {
                continue;
            }

            // 第四个参数是"通道满了要立刻发一包"的回调。我们按帧统一发,
            // 所以给个空实现 —— 消息会留在通道里, 下一帧组帧时被 Send 带出去。
            if (inOrder)
            {
                channels.Ordered._0023lze(_network.Time, netBuffer, channel, _ => { }, resendRate);
            }
            else
            {
                channels.Unordered._0023lze(_network.Time, netBuffer, channel, _ => { }, resendRate);
            }
        }
    }

    /// <summary>
    /// 每逻辑帧给每个已连接客户端组一帧并发出去。
    ///
    /// ## 帧结构
    ///
    /// 逐行对应客户端的读取方 <c>GameClient._0023Yue</c> (:642):
    ///
    ///   ordered.Send(Time, buf)      // 有序可靠通道 (含 ack 与待发消息)
    ///   unordered.Send(Time, buf)    // 无序可靠通道
    ///   Write(bool)  -> 控制器段: 对**整张**客户端表逐个写 bool + 可选数据
    ///   Write(bool)  -> 同步段: 见 WorldStateWriter
    ///
    /// 控制器段那个循环遍历的是 <c>_ClientList</c> 全长 (= MaxClients), 不是
    /// 已连接数 —— 客户端那边 <c>_0023QCe</c> 就是这么读的, 少写一个 bool
    /// 后面全错位。
    /// </summary>
    private void FlushFrames()
    {
        foreach (KeyValuePair<_0023tde, int> slot in _slots)
        {
            _0023tde connection = slot.Key;
            if (connection._0023Ebe() != NetConnectionStatus.Connected)
            {
                continue;
            }

            if (!_channels.TryGetValue(connection, out ClientChannels channels))
            {
                continue;
            }

            // 客户端还没建好同步表就发世界状态 = 它拿空表解, 索引全越界。
            // 等它自己报 LoadingComplete。
            if (!channels.LoadingComplete)
            {
                continue;
            }

            IClient viewer = _network.GetClient(slot.Value);
            _0023yee raw = _peer._0023dce();
            _0023TDe buffer = new _0023TDe(raw);

            channels.Ordered.Send(_network.Time, buffer);
            channels.Unordered.Send(_network.Time, buffer);

            // 控制器段
            buffer.Write(true);
            foreach (IClient other in _network.Clients)
            {
                bool hasData = other != null && other.IsConnected;
                buffer.Write(hasData);
                if (hasData)
                {
                    other.ControllerServerToClientSerialize(buffer, other.Index == viewer.Index);
                }
            }

            // 同步段
            buffer.Write(true);
            DIE.Server.Game.WorldStateWriter.Write(buffer, viewer, _network.Synchronizables);

            // 记一下这一帧发了多大。Lidgren 的 MTU 默认 1408 字节, 超了要分片,
            // 而分片重组要等所有片到齐 —— 那正好长得像玩家报的"僵尸冻在原地,
            // 过一会儿突然补上"。僵尸多的时候(埋伏、占领刷怪)包最大, 症状也
            // 恰好出现在那些场合, 所以这个数值得量一量。
            int bytes = raw._0023Bae() / 8;
            if (bytes > _peakPacketBytes)
            {
                _peakPacketBytes = bytes;
            }

            connection.SendMessage(raw, _0023Rde._0023Nde, 0);
        }
    }

    /// <summary>
    /// 解客户端发来的数据帧。
    ///
    /// ## 帧结构
    ///
    /// 逐行对应客户端的写入方 <c>GameClient._0023Mye()</c> (:624):
    ///
    ///   LocalClient.ControllerClientToServerSerialize(buf)   // 输入 / 控制器状态
    ///   _0023aCe.Send(Time, buf)                             // 有序可靠通道
    ///   _0023bCe.Send(Time, buf)                             // 无序可靠通道
    ///
    /// 顺序不能错: 这是紧凑的位流, 读错一个字段后面全是垃圾, 而且**不会报错** ——
    /// 只会解出一堆看似合法的随机值。
    ///
    /// ## 为什么没有 NetDebug 分支
    ///
    /// 客户端在每段前后会插 <c>EnableNetDebugging</c>/<c>DisableNetDebugging</c>,
    /// 但那只在 <c>NetDebuggingEnabled</c> 为真时写东西 —— 而那个值是**我们**在
    /// hail 里发的, 我们发的是 false。所以这里不需要对应的读取。
    /// (反过来说: 哪天 hail 里改发 true, 这里必须同步加上。)
    ///
    /// ## 可靠通道
    ///
    /// 游戏消息不是裸跑在 Lidgren 上的。<c>_00238ze</c>/<c>_0023mze</c> 这一层负责
    /// 分片、排序和重传, 每个连接一对, 与客户端那对对称。解出完整消息后回调到
    /// <c>GameManager.DeserializeGameMessage</c>。
    /// </summary>
    private void HandleData(_0023cee message)
    {
        _0023tde connection = message._0023bee();
        if (connection == null || !_slots.TryGetValue(connection, out int index))
        {
            Log.Debug("战斗服 UDP {Port} 收到未知连接的数据包, 丢弃", Port);
            return;
        }

        if (!_channels.TryGetValue(connection, out ClientChannels channels))
        {
            channels = new ClientChannels();
            _channels[connection] = channels;
        }

        Client client = (Client)_network.GetClient(index);
        LidgrenNetBuffer buffer = new LidgrenNetBuffer(message);

        try
        {
            client.ControllerClientToServerDeserialize(buffer);
            channels.Ordered._0023kze(buffer, b => _sim.DeserializeGameMessage(b, client));
            channels.Unordered._0023kze(buffer, b => _sim.DeserializeGameMessage(b, client));
        }
        catch (Exception ex)
        {
            // 位流解错不会自己报错, 只会读出垃圾。真抛异常说明读得太过分了
            // (越界), 那已经是帧结构对不上的确证 —— 值得单独记一条。
            Log.Error(ex, "战斗服 UDP {Port} slot {Index} 数据帧解析失败, 帧结构可能与客户端不一致",
                Port, index);
        }
    }

    /// <summary>
    /// 把客户端登记进 <c>NetworkBase.ActiveClientsList</c>, 并刷新它对现有对象的可见性。
    ///
    /// ## 为什么必须我们自己来
    ///
    /// 整个 <c>NetworkBase</c> 里, <c>ActiveClientsList</c> 只在构造时被 new 出来
    /// (:461) 和被遍历 (:750) —— **没有任何一处往里加东西**。维护它是服务端的活,
    /// 属于随服务端一起丢失的那部分代码。
    ///
    /// 不维护的后果是静默的: <c>GameObjectBase.Spawn()</c> 用
    ///
    ///     foreach (Client c in NetworkManager.get_ActiveClientsList())
    ///         _SyncHelper._00235Ce(c.Index, ShouldBeSynchronized(c));   // :1163
    ///
    /// 来置每客户端同步位。列表是空的, 循环体一次都不执行, 位永远是 0,
    /// <c>WorldStateWriter</c> 里的 <c>_0023pDe(viewer.Index)</c> 就永远为假 ——
    /// 实体明明活着、在服务端正常模拟, 却一个字节都不下发。
    ///
    /// 之前恒定"本帧同步条目=3"就是这么来的: 那 3 个是走
    /// <c>AddSynchronizable(..., isActiveFromStart: true)</c> 加进去的
    /// (NetworkBase.cs:668 直接给所有客户端置位), 绕过了 Spawn 这条路。
    ///
    /// ## 为什么加完还要刷一遍
    ///
    /// <c>GameObjectBase</c> 只在 Spawn (:1163) 和 Destroy (:1222) 时算可见性,
    /// 没有逐帧重算。所以在这个客户端连上**之前**就已经 Spawn 的东西 (地图上的
    /// 实体、先进来的其他玩家) 不会自己变可见 —— 必须在客户端加入时补算一次。
    ///
    /// <c>AbilityBar</c> 不用管, 它在 <c>Update</c> 里逐帧重算 (AbilityBar.cs:351),
    /// 列表一有内容就会自愈。
    /// </summary>
    /// <summary>
    /// 把教程从"开场动画"推进到"醒来"(<c>_0023occ._0023ecc</c>)。
    ///
    /// ## 客户端拿到这个状态之后做什么
    ///
    /// 教程状态 <c>_0023pcc</c> 随 <c>SerializeGameMode</c> 下发 (-bd/-5cc.cs:735)。
    /// 客户端在 <c>DeserializeGameMode</c> 里发现它变了就调 <c>_0023E9()</c>,
    /// 那里按新值分派 (:302):
    ///
    ///     ViewManager.SkipTutorialIntro()                    // 把 _TutorialCameraDuration 归零
    ///     PlayTransmissionPackage(TutorialWakeUp)            // 无线电语音
    ///     player.CharacterModel.PlayAnimation(Tutorial_WakeUp)
    ///     ViewManager.SetMusicLevel(5f)
    ///
    /// 归零之后 ViewManager 的更新走到 <c>_TutorialCameraDuration &lt;= 0f</c> 分支
    /// (ViewManager.cs:1378): 重新启用 TopDownCamera、显示 HUD、发
    /// <c>TutorialCameraComplete</c>。
    ///
    /// ## 为什么直接写字段
    ///
    /// <c>GameModeData</c> 属性是 <c>=&gt; _0023l9</c>, 而 <c>_0023wcc</c> 是
    /// **struct** —— 属性返回的是副本, 改副本对状态没有任何影响。字段的可见性
    /// 由 sanitizer 放开 (protected -&gt; public), 理由见那里的注释。
    ///
    /// ## 这只是第一步
    ///
    /// 完整的教程状态机有约 20 个状态 (`-z/-occ.cs`), 每步都带引导文字、语音、
    /// 怪物投放和目标判定, 全部随服务端丢失。这里只做 0 -&gt; 1 这一跳, 目的是
    /// 让开场动画可以跳过 —— 否则每次真机验证都要等两分钟。
    ///
    /// 后续状态还卡着教程里的武器发放 (Player.cs:1867 按 _0023pcc 的阈值给),
    /// 所以现在技能栏仍然是空的。
    /// </summary>
    /// <summary>
    /// 登记地图上的漫游刷怪点。**不在这里刷怪** —— 刷怪由
    /// <see cref="DIE.Server.Game.RoamingZombieDirector"/> 在玩家走近时触发。
    ///
    /// 上一版是在这里一次性把全图铺满的, 结果玩家走到刷怪点时僵尸早就站好了,
    /// 从地里爬出来的出场动画在开局那一瞬就播完了 (那时玩家还在几百单位外)。
    ///
    /// Scout / Raid 系列地图上没有漫游刷怪点 (它们纯事件驱动), 那里会登记 0 个,
    /// 这是正常的, 不是故障。
    /// </summary>
    private void ArmRoamingZombies()
    {
        try
        {
            // 教程全程脚本化 —— 场上只该有那 95 只手摆僵尸。地图里那 9 个漫游点
            // 大概是复制场景时带过来的, 原版从不启用。理由见
            // RoamingZombieDirector.Enabled 的说明。
            if (_sim?.GameMode != null && _sim.GameMode.IsTutorialGameMode)
            {
                _zombieDirector.Enabled = false;
                Log.Information("战斗服 UDP {Port} 教程地图: 关闭漫游刷怪 (只用手摆僵尸)", Port);
                return;
            }

            _zombieDirector.Scan(_sim);

            if (_zombieDirector.ArmedPoints == 0)
            {
                Log.Information("战斗服 UDP {Port} 地图 {Map} 没有漫游刷怪点 (纯事件驱动地图)",
                    Port, MapIndex);
                return;
            }

            Log.Information("战斗服 UDP {Port} 已登记 {Points} 个漫游刷怪点 —— 玩家走进 {Radius:F0} 单位内才刷",
                Port, _zombieDirector.ArmedPoints, _zombieDirector.TriggerRadius);
        }
        catch (Exception ex)
        {
            // 刷怪失败不该让整局崩掉 —— 没怪的对局仍然能进去看别的东西。
            Log.Error(ex, "战斗服 UDP {Port} 登记漫游刷怪点失败", Port);
        }
    }

    /// <summary>
    /// 每个逻辑帧、在模拟推进**之前**跑的服务端逻辑。
    ///
    /// 顺序有讲究: 先刷怪再跑 AI, 这样当帧刷出来的僵尸立刻就有脑子;
    /// 反过来的话新怪要空转一帧。
    ///
    /// 异常不外抛 —— 这是在 <c>NetworkBase.DoFrame</c> 里面, 抛出去会打断整个
    /// 定步长循环, 后果是全场卡死。
    /// </summary>
    private void BeforeSimulation(float deltaTime, float totalTime)
    {
        if (_sim?.GameMode == null)
        {
            return;
        }

        // 开局前什么都不推进。拾荒这时正在出生区等人 —— 那一段由原厂
        // GameMode.UpdateGameCountDown 自己跑 (见 ScavengerHuntServer), 走的是模拟层
        // 的 Update, 不在这条宿主回调上。
        if (!_sim.GameMode.IsStarted)
        {
            return;
        }

        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            // 开场那段锁住操作。要在 AI / 教程推进**之前** —— 状态一到 2
            // 就该同帧解锁, 晚一帧玩家会觉得"按了没反应"。
            _introLock.Update(_sim);

            // 玩家"能不能动"变了就记一条 —— 查"对话时无法控制角色"用。
            // 详见 TutorialIntroLock.OnMobilityChanged 的说明。
            _introLock.WatchMobility(_sim);
            if (_introLock.LockedThisTick > 0)
            {
                Log.Information("战斗服 UDP {Port} 开场锁住操作 {Count} 人 (躺沙滩 + 醒来动画期间)",
                    Port, _introLock.LockedThisTick);
                _introLock.LockedThisTick = 0;
            }

            if (_introLock.ReleasedThisTick > 0)
            {
                Log.Information("战斗服 UDP {Port} 开场结束, 解锁操作 {Count} 人",
                    Port, _introLock.ReleasedThisTick);
                _introLock.ReleasedThisTick = 0;
            }

            // 玩家走近营救现场才挂 PullerHook —— 时序的理由见方法注释。
            TryHookTutorialNpc();

            // 把玩家登记进补给表。要在占领之前 —— 箱子一落地玩家就可能踩上去,
            // 那一帧还没登记的话这一箱就白掉了 (拾取失败是静默的)。
            _supplies.Update(_sim);
            if (_supplies.RegisteredThisTick > 0)
            {
                Log.Information("战斗服 UDP {Port} 补给表登记 {Count} 人 (共 {Total} 人) —— " +
                                "不登记的话拾取补给会静默失败",
                    Port, _supplies.RegisteredThisTick, _supplies.Registered);
                _supplies.RegisteredThisTick = 0;
            }

            // 推进式复活点: 看玩家踩进哪个区域了。
            _raidRespawns.Update(_sim);
            for (int i = 0; i < _raidRespawns.Events.Count; i++)
            {
                Log.Information("战斗服 UDP {Port} {Event}", Port, _raidRespawns.Events[i]);
            }

            // 补给点占领。读条那一段是原厂的, 这里接的是它之后的流程。
            _capture.Update(_sim, deltaTime);

            // Horde 占领点同理: StartCapture 只把状态推到 1, 推进到 2
            // (以及解锁路障) 那一步随服务端丢失。
            int capturedBefore = _hordeSpawners.CapturesCompleted;
            _hordeSpawners.Update(_sim, deltaTime);
            if (_hordeSpawners.CapturesCompleted != capturedBefore)
            {
                Log.Information("战斗服 UDP {Port} Horde 占领点拿下 (累计 {Count} 个)",
                    Port, _hordeSpawners.CapturesCompleted);
            }

            LogCarriedSupplies();
            for (int i = 0; i < _capture.Events.Count; i++)
            {
                Log.Information("战斗服 UDP {Port} {Event}", Port, _capture.Events[i]);
            }

            _capture.Events.Clear();
            Profiler.Lap(_profMisc, ref lap);

            _zombieDirector.Update(_sim);
            if (_zombieDirector.SpawnedThisTick > 0)
            {
                Log.Information("战斗服 UDP {Port} 玩家靠近, 刷出僵尸 {Count} 只 @ {Points}",
                    Port, _zombieDirector.SpawnedThisTick,
                    string.Join(" ", _zombieDirector.TriggeredThisTick));
            }

            // 事件刷怪: 玩家踩到 Event_TriggerPosition 就按 Event_Assault 的
            // min/max 在该事件的 Spawn_EventEntities 摆位点上刷怪。
            // 要 deltaTime —— 出场延迟 (RandomSpawnDelay) 和冷却 (Cooldown) 都是原厂数据。
            _eventSpawns.Update(_sim, deltaTime);

            _ambushes.Update(_sim, deltaTime);
            Profiler.Lap(_profSpawners, ref lap);

            // 路径点语音。放在剧情之前 —— Ram 那个点同时是语音点,
            // 玩家实测的顺序就是"对话开始播放时 Ram 冲出来"。
            _transmissions.Update(_sim, _network);
            for (int i = 0; i < _transmissions.Events.Count; i++)
            {
                Log.Information("战斗服 UDP {Port} {Event}", Port, _transmissions.Events[i]);
            }

            // Starter Mission 剧情: Ram 撞门 / Boss Looter
            _scoutObjective.Update(_sim, deltaTime);
            DrainObjectiveEvents();
            ProvokeSpawned(_scoutObjective.SpawnedThisTickList);

            _scoutStory.Update(_sim, deltaTime);
            for (int i = 0; i < _scoutStory.Events.Count; i++)
            {
                Log.Information("战斗服 UDP {Port} {Event}", Port, _scoutStory.Events[i]);
            }

            // 十字路口局内流程。放在 Scout 收尾**之前** —— 它的收尾也走
            // _scoutFinale.CompleteNow (回调在 OnRunComplete 上), 那一条要在
            // 同一帧里被 _scoutFinale 自己的 Update 看到 (它只是走计时)。
            _concede.Update(_sim, _network, deltaTime);
            Profiler.Lap(_profStory, ref lap);
            _crossroads.Update(_sim, _network, deltaTime);
            Profiler.Lap(_profCrossroads, ref lap);

            _scavenger.Update(_sim, _network, deltaTime);
            LogScavengerEvents();
            ProvokeSpawned(_scavenger.StorageRooms.SpawnedThisTickList);

            // 控制状态条镜像: 专属控制结束了就摘, 碎了就补。见 CrowdControlMirror。
            _sim.CrowdControl.Update(_sim, deltaTime);
            DrainObjectiveEvents();
            ProvokeSpawned(_crossroads.SpawnedThisTickList);

            // Scout 收尾: 补给交够了就发 TeamFinished, 客户端 2 秒后弹计分板。
            _scoutFinale.Update(_sim, _network, deltaTime);
            for (int i = 0; i < _scoutFinale.Events.Count; i++)
            {
                Log.Information("战斗服 UDP {Port} {Event}", Port, _scoutFinale.Events[i]);
            }

            // Scout 图: 玩家走近才出场的那批。
            _proximitySpawns.Update(_sim);
            if (_proximitySpawns.SpawnedThisTickList.Count > 0)
            {
                Log.Information("战斗服 UDP {Port} 走近出场 {Count} 只 (累计 {Total}, 还剩 {Left} 个点)",
                    Port, _proximitySpawns.SpawnedThisTickList.Count,
                    _proximitySpawns.Spawned, _proximitySpawns.ArmedPoints);
            }

            // 教程终局: 补给交到卡车上之后围着卡车持续补怪。
            // 由 TutorialDirector 的 OnDeliveryComplete / OnFinaleOver 开关。
            _finale.Update(_sim, deltaTime);
            for (int i = 0; i < _finale.Events.Count; i++)
            {
                Log.Information("战斗服 UDP {Port} {Event}", Port, _finale.Events[i]);
            }

            _finale.Events.Clear();

            // ## 刷出来的怪一律主动去找玩家
            //
            // 玩家实测: "卡车场景刷的僵尸很多都不会主动攻击, 因为距离太远,
            // 这个问题和补给点和 barricade 刷的僵尸一样, 应该改成攻击给卡车
            // 输送物资的队伍, 然后才是靠仇恨距离。"
            //
            // 被动仇恨 (AggroRadius 55) 是给**场景里本来就站着的**怪用的。
            // 波次/事件刷出来的是冲着玩家来的, 而摆位点常常在脱战半径 (90) 之外,
            // 于是刷出来就站着不动 —— 真机日志里终局刷了 72 只而场上一直顶着
            // 24/24 上限, 就是这个。
            //
            // 埋伏刷怪早就走 Provoke 了 (下面那段), 这里把同一条规则铺到
            // 另外三个刷怪源。Provoke 是幂等的, 重复调无害。
            // 客户端模型缓存放不下而被拦掉的生成 —— 一有就报, 别让它悄悄发生。
            // 拦掉意味着"该刷的怪少了", 那是要能看见的。
            if (DIE.Server.Game.ZombieFactory.BlockedByModelCache > 0)
            {
                Log.Information("战斗服 UDP {Port} 刷怪被客户端模型缓存拦下 {Count} 次 " +
                                "(Walker 教程上限 40, 见 ZombieFactory.TutorialModelCacheLimit)",
                    Port, DIE.Server.Game.ZombieFactory.BlockedByModelCache);

                DIE.Server.Game.ZombieFactory.BlockedByModelCache = 0;
            }

            ProvokeSpawned(_eventSpawns.SpawnedThisTickList);
            ProvokeSpawned(_zombieDirector.ProvokeThisTick);
            ProvokeSpawned(_capture.SpawnedThisTickList);
            ProvokeSpawned(_finale.SpawnedThisTickList);
            ProvokeSpawned(_proximitySpawns.SpawnedThisTickList);
            ProvokeSpawned(_scoutStory.SpawnedThisTickList);

            // 埋伏怪一出生就追**正在打路障的那个人** —— 不等玩家走进仇恨半径。
            // 埋伏点离战场常常超过脱战半径, 光靠普通仇恨它们会站着不动。
            for (int i = 0; i < _ambushes.SpawnedThisTickList.Count; i++)
            {
                if (_ambushes.SpawnedThisTickList[i] is ConductorGameLogic.Entities.NPC npc)
                {
                    _zombieBrain.Provoke(npc, _ambushes.SpawnedThisTickTargets[i]);

                    // 每一只埋伏怪的出生记录。玩家三轮报告"看不见的僵尸, 但是能
                    // 打到我", 而我三次假设 (出场动画 / 栈道地形高度 / 出场状态
                    // 卡住移动) 全部落空。现在把生成那一刻的**事实**记下来:
                    // 类型、坐标、出场状态、以及它对该客户端的同步位 ——
                    // 同步位为 false 就是客户端压根没收到这只怪, 那是复制层的问题;
                    // 为 true 就说明客户端收到了却没渲染, 那是客户端侧, 得换招。
                    // 同步位读的是 WorldStateWriter.SelectActive 用的**同一个**
                    // 判据 (SynchronizableData._0023pDe), 不是另造一套 ——
                    // 诊断和被诊断的路径必须是同一条, 否则看到的是别的东西。
                    string synced = "(查不到)";
                    var sync = _network.Synchronizables;
                    int syncIndex = npc.SyncHelperIndex;
                    if (_slots.Count > 0 && syncIndex >= 0 && syncIndex < sync.Count)
                    {
                        synced = sync[syncIndex]?._0023pDe(0).ToString() ?? "(空条目)";
                    }

                    Log.Information("战斗服 UDP {Port} 埋伏刷怪: {Type}#{Index} @ ({X:F0},{Y:F0}) " +
                                    "出场状态={State} 同步位={Synced} 血 {HP:F0}/{MaxHP:F0}",
                        Port, npc.GetType().Name, npc.IndexGlobal,
                        npc.Position.X, npc.Position.Y,
                        npc.SpawnState_Current, synced,
                        npc.Health, npc.MaxHealth);
                }
            }

            foreach (int id in _ambushes.TriggeredThisTick)
            {
                Log.Information("战斗服 UDP {Port} 埋伏 {Id} 触发 —— 玩家开始打那组路障, " +
                                "僵尸开始涌出", Port, id);
            }

            // 路障组逐段血量。玩家实测"栅栏打破了刷怪还在继续", 而停止判据和
            // 教程状态机推进用的是同一个 —— 两个一起卡住说明还有一段没死。
            foreach (string report in _ambushes.GroupReports)
            {
                Log.Information("战斗服 UDP {Port} {Report}", Port, report);
            }
            if (_eventSpawns.SpawnedThisTick > 0)
            {
                Log.Information("战斗服 UDP {Port} 事件刷怪 {Count} 只 (待出场 {Pending})",
                    Port, _eventSpawns.SpawnedThisTick, _eventSpawns.PendingCount);
            }

            if (_eventSpawns.TriggeredThisTick.Count > 0)
            {
                Log.Information("战斗服 UDP {Port} 触发事件 {Events}",
                    Port, string.Join(" ", _eventSpawns.TriggeredThisTick));
            }

            // 路障打破的经验。放在 AI 之前 —— 这一帧打破的路障立刻结算,
            // 免得对象被池回收之后才发现 (那时 NumberOfGlobalCacheUses 已经变了,
            // 记录会被当作串号作废, 经验就静默丢了)。
            _destructibleRewards.Update(_sim);
            if (_destructibleRewards.DestroyedThisTick > 0)
            {
                Log.Information("战斗服 UDP {Port} 打破路障 {Count} 段 (每段 {Xp:F0} 经验, 累计发出 {Total:F0})",
                    Port, _destructibleRewards.DestroyedThisTick,
                    DIE.Server.Game.DestructibleRewards.ExperiencePerBarricade,
                    _destructibleRewards.TotalExperienceGranted);
            }

            Profiler.Lap(_profSpawners, ref lap);
            _zombieBrain.Update(_sim, deltaTime, totalTime);
            Profiler.Lap(_profZombieAI, ref lap);

            // 技能读条没读完就断了 -> 记下现场 (夜袭里 Isys 的 Q 放不出来, 见 CastInterruptProbe)
            _castProbe.Update(_sim);
            foreach (string report in _castProbe.Reports)
            {
                Log.Information("战斗服 UDP {Port} {Report}", Port, report);
            }

            ReportBrokenObjects();


            // 召唤物单独一个脑子: InfestedMinion 继承 NPC 而不是僵尸基类,
            // IsZombie 对它是 false, ZombieBrain 完全跳过它。而且职责也不同 ——
            // 一个是追杀玩家, 一个是跟随护主。
            _minionBrain.Update(_sim);

            // 其余召唤物: 分身/幽灵拿武器打, Marco 听 Rico 的指令, 大招孢子原地扎人 —— 见 CompanionBrain。
            _companionBrain.Update(_sim);

            // 炮塔再一个: 基地机枪 (_0023Zuc) 不是 NPC, 上面两个脑子都不看它。
            // 它自己会转向、开火、装弹, 缺的只是"看见谁" —— 见 TurretBrain。
            // 没有卡车炮塔的图 (教程 / Scout) 这一步一只都找不到, 开销可以忽略。
            _turretBrain.Update(_sim);

            // 教程 NPC (Amber) 再一个脑子: 她既不是僵尸也不是召唤物 ——
            // ZombieBrain 看 IsZombie, MinionBrain 只认 InfestedMinion,
            // 两个都跳过她。被救之前 Steer 会因为 Stun 直接返回。
            _tutorialNpcAI.Update(_sim, _sim.ActivePlayerList.Count > 0
                ? _sim.ActivePlayerList[0]
                : null, deltaTime);

            Profiler.Lap(_profOtherAI, ref lap);

            // 复活补发装备。放在 AI 之后 —— Amber 的引导是在 _tutorialNpcAI
            // 那一步里完成的, 同一帧就能看到玩家从死变活。
            int refreshed = _respawnEquip.Update(_sim, _network);
            foreach (ConductorGameLogic.Entities.Player revived in _respawnEquip.RespawnedThisTick)
            {
                // 服务端侧的同一组数字。玩家报的"血量 0/怒气 0/打不掉血"读的是
                // HUD, 也就是**客户端**那份 —— 两边一对照就知道故障在哪一侧。
                // RespawnServerStateTests 已经钉住"服务端侧应该是健康的"。
                Log.Information("战斗服 UDP {Port} 复活: 血 {Health:F0}/{MaxHealth:F0} " +
                                "怒气上限 {MaxRage:F0} 队伍 {Team} 武器 {Weapon} 技能0 {Ability} " +
                                "受控 {Controlled} 位置 ({X:F1},{Y:F1}) —— 已补发装备更新 {Count} 条",
                    Port, revived.Health, revived.MaxHealth,
                    revived.Stats.GetValue(ConductorGameLogic.StatType.MaxRage),
                    revived.TeamId, revived.EquippedWeaponSchematicId,
                    revived.AbilityBar.AbilitySlots[0]?.AbilityIdentifier,
                    ReferenceEquals((revived.Client as Client)?.ControlledCharacter, revived),
                    revived.Position.X, revived.Position.Y, refreshed);
            }

            // 兜底: 上面那些该设视野的路径万一漏了, 这里拨回来并喊一声。
            // 放在复活补发之后 —— 复活那一步本来就会设, 让它先跑, 免得白喊。
            _cameraTarget.Update(_sim, deltaTime, Port, _observerSlots);

            // 复活了却一条装备更新都没发出去 —— 那正是"复活后手上武器模型消失"
            // 的成因。真机日志里出现过两次 0, 静默混在正常行里很容易漏掉,
            // 所以明确喊出来。(这个项目已经被误导性日志坑过一轮。)
            if (_respawnEquip.RespawnedThisTick.Count > 0 && refreshed == 0)
            {
                Log.Warning("战斗服 UDP {Port} 有人复活但装备更新一条都没发出去 —— " +
                            "客户端手上会是空的", Port);
            }

            // 教程状态机。放在最后 —— 它可能发武器、改状态, 让那些在本帧的
            // 模拟推进之前落地。
            _tutorialDirector.Update(_sim, totalTime);
            Profiler.Lap(_profMisc, ref lap);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} 帧前逻辑异常 (刷怪/AI)", Port);
        }
    }

    private void AdvanceTutorialPastIntro()
    {
        if (_sim.GameMode is not _00235cc tutorial)
        {
            return;
        }

        // 幂等: SPACE 每按一次客户端就发一条, 而且自然放完还会再来一条
        // TutorialCameraComplete。只在还处于初始状态时推进。
        if (tutorial._0023l9._0023pcc != _0023occ._0023Yc)
        {
            return;
        }

        tutorial._0023l9._0023pcc = _0023occ._0023ecc;

        Log.Information("战斗服 UDP {Port} 教程状态 {From} -> {To} (开场结束)",
            Port, _0023occ._0023Yc, _0023occ._0023ecc);
    }

    private void ActivateClient(Client client)
    {
        int refreshed = DIE.Server.Game.ClientVisibility.Activate(_network, _sim, client);

        Log.Information("战斗服 UDP {Port} slot {Index} 已登记为活动客户端 (刷新了 {Count} 个已有对象的可见性)",
            Port, client.Index, refreshed);
    }

    /// <summary>
    /// 客户端离开时从活动列表摘除。
    ///
    /// 不摘的话 <c>Spawn()</c> 会继续给一个已经断开的槽位算可见性, 而该槽位
    /// 之后被新玩家复用时会带着上一局的位状态。
    /// </summary>
    private void DeactivateClient(int index)
    {
        DIE.Server.Game.ClientVisibility.Deactivate(_network, index);
    }

    /// <summary>
    /// 按客户端的算法读出相机朝向, 只在握手里没带这个值时用。
    ///
    /// <c>GameManagerClient.LoadCameraSettings</c> 是原厂的静态方法, 直接复用 ——
    /// 它读 <c>MapManager._0023uh\camera.cfg</c> 里的两个 float。客户端拿到
    /// offset 之后取反再归一化 (UnityClient.cs:2795), 这里照做。
    ///
    /// 读不到文件时它自己会退回 <c>-Vector2.UnitY</c>, 取反后是 (0,1) —— 那和
    /// 真实安装里的 (-0.707,-0.707) 差 135 度。所以读失败要留痕, 不能静默接受。
    /// </summary>
    private Vector2 LoadCameraDirection()
    {
        if (!GameManagerClient.LoadCameraSettings(out Vector2 offset))
        {
            Log.Warning("战斗服 UDP {Port} 读不到 camera.cfg —— 退回的朝向可能和真实安装差很远", Port);
        }

        Vector2 direction = -offset;
        if (direction == Vector2.Zero)
        {
            return new Vector2(0f, 1f);
        }

        direction.Normalize();
        return direction;
    }

    /// <summary>
    /// 给一个客户端生成玩家实体。
    ///
    /// ## 为什么必须在批准之前做完
    ///
    /// 客户端靠我们在 ClientInfo 里发的 <c>PlayerIndex</c> 去
    /// <c>GameManager.GetPlayer(...)</c> 找自己的角色 (<c>Client.cs:561</c>)。
    /// 而 ClientInfo 是**随 hail 一起发的**, hail 就是审批的回复 —— 所以实体必须
    /// 在那之前就存在。
    ///
    /// 时机上没有问题: 模拟层在客户端连上之前就建好了 (地图已加载、对象池已分配)。
    ///
    /// <c>Client.ClientInfoServerToClientSerialize</c> 只在 <c>LocalPlayer != null</c>
    /// 时才写 PlayerIndex 和 ClientInfoData; 为 null 就写个 false 收尾, 客户端
    /// 相应地把 PlayerIndex 置 -1 并打 "Did not get any player for client N"。
    /// 那是原厂支持的合法状态 (观战者就走这条), 所以这里失败了也照样放行。
    ///
    /// ## 角色从哪来
    ///
    /// 玩家排队时选的, 经匹配票据的 queue_data 带过来 (见 PlayerChoiceCodec)。
    /// 解不出来时退回 BergSurvivor —— 那是教程默认角色。
    /// </summary>
    private bool TrySpawnPlayer(Client client, out string failure)
    {
        MatchMember member = MemberOf(client);
        CharacterEnum character = member.Choice?.Character ?? CharacterEnum.BergSurvivor;

        // CharacterEnumToGameObjectTypeID 是在 CacheGame 里填的 (GameManager.cs:1437),
        // 只有被缓存过的角色才有条目。所以这个查找失败 = 该角色没进对象池。
        if (!GameManager.CharacterEnumToGameObjectTypeID.TryGetValue((int)character, out int typeId))
        {
            failure = $"角色 {character} 不在对象池里 (CacheGame 时没缓存它)";
            return false;
        }

        Player player = _sim.GetGameObjectFromPool<Player>(typeId);
        if (player == null)
        {
            failure = $"对象池取不出 {character} (typeId={typeId})";
            return false;
        }

        TeamId team = PickPlayerTeam(member);
        Vector2 spawn = PickSpawnPoint(team);

        player.Client = client;
        player.TeamId = team;
        player.Position = spawn;

        // ⚠️ 必须在 Spawn() **之前** —— 这是技能栏为什么一直是空的。
        //
        // Player.SpawnCharacter (:575) 拿账号等级去查奖励表, 再把结果装进技能栏:
        //
        //     _0023N2f data = _00231C._0023Nsb(CharacterEnum, _0023MB);  // _0023MB = 账号等级
        //     SetAccountLevelData(data);
        //     if (IsServer) { EquipWeaponsAndAbilities(); _0023RC(); }   // _0023RC 装技能
        //
        // 奖励表的第一档是 **LevelRequirement = 1** (AccountLevelHelpers.cs:14),
        // 而查表用的是 `TakeWhile(r => r.LevelRequirement <= level)` (:197)。
        // 我们从没调过 SetAccountLevel, _0023MB 停在 C# 默认值 **0** ->
        // TakeWhile 第一个就不满足 -> 返回空序列 -> 一个技能都没有。
        //
        // 等级对应关系 (AccountLevelHelpers.Rewards):
        //   1 -> Ability1 + Ability2 + 消耗品槽1
        //   2 -> + Ability3
        //   3 -> + 大招
        //
        // 全程零异常, 因为空序列本身是合法的。
        //
        // 最小值 1 —— 原厂根本不存在 0 级账号 (ExperienceToLevel 从 1 起算),
        // 0 只可能来自"服务端忘了设"。
        player.SetAccountLevel(Math.Max(1, member.AccountLevel));

        // ## 玩家免疫近战击退
        //
        // 玩家实测 (对着原版视频): "近战攻击对僵尸有击退效果, 对玩家角色没有
        // 击退效果", 而且被敌对 NPC 的近战打中**不会眩晕**。
        //
        // 位移那一半原厂代码里已经排除了玩家 (`-Sq/-nE.cs:188` 的
        // `if (!Target.IsPlayer && ...) ChangeForceDirection(...)`), 但**眩晕
        // 那一半没有** —— 近战判定对象 `-EF/-3wc.cs:284-296` 无条件挂
        // `_0023nE`, 它再 `ApplyStatEffect(WeakKnockbackStun)`,
        // 而 `Character.IsAllowedToMove()` (:2814) 明确查 `IsWeakKnockbackStunned`。
        //
        // 原厂拦在哪: **不是**抗性系统。击退 buff 没覆盖 `_0023sq`, 取基类默认
        // 4, 而玩家抗性是 3 —— `3 < 4`, 抗性那条路放行 (抗性系统现在是接上的,
        // 见 GameManagerServer.ChangeResistanceStats)。
        //
        // 拦它的是另一个现成开关: <c>Entity.PermanentMeleeKnockbackImmunity</c>
        // (Entity.cs:86) —— <c>CanBeMeleeKnockBacked</c> 读它 (:233), 原厂自己
        // 在 `-EF/-3I.cs` 的近战判定里也查它。配合 GameManagerServer.ApplyBuff
        // 里那道守卫, 就表达出了玩家实测的行为。
        player.PermanentMeleeKnockbackImmunity = true;

        // 激活实体。从池里取出来只是"拿到对象", 还没进 ActiveGameObjectList ——
        // 不 Spawn 的话它既不会被模拟, 也不满足 GameObjectBase.Serialize 的
        // IsActive 判定 (:1329), 客户端那边就是个不存在的东西。
        player.Spawn();

        client.LocalPlayer = player;

        // 记下"这个账号的角色是哪一个" —— 掉线重连要按它接回去 (见 _playersByUser)
        if (member.UserId != 0)
        {
            _playersByUser[member.UserId] = player;
        }

        // 登记进 ConnectedPlayers —— 击杀经验按这个列表分发, 不登记就一点经验都不给。
        // 必须在 LocalPlayer 赋值之后, 所以不能并进入场审批时的 ActivateClient。
        DIE.Server.Game.ClientVisibility.RegisterPlayer(_sim, client);

        // 告诉客户端"你控制这个角色, 相机也看这个角色"。
        //
        // 这两行是黑屏和 SPACE 无响应的共同解 —— 它们的值经
        // ControllerServerToClientSerialize (Client.cs:387) 每帧下发, 而客户端的
        // ControlledCharacter / LookAtCharacter **完全**由那条消息设定:
        //
        //   ControlledCharacter == null → UnityClient 的 character != null 守卫不过
        //                                 → UpdateGameplayInputs 整个不执行
        //                                 → SPACE 那一支 (UnityClient.cs:2324) 永远读不到
        //   LookAtCharacter    == null → 相机目标退回 _ObserverTransform (原点)
        //                                 → TopDownCamera 停在开场动画结束的位置 → 黑屏
        //
        // 注意必须走 SetControlledCharacter 而不是直接赋字段: 它还会回填
        // Character.Client (Client.cs:619)。
        client.SetControlledCharacter(player);
        client.SetLookAtCharacter(player);

        // _0023kYb 是**相机朝向**, 不是出生坐标 —— 之前填成了出生坐标, 表现是
        // WASD 四个方向全错。
        //
        // 判据: ClientConnectionData 的构造函数把同名字段的参数就叫 cameraDirection
        // (ClientConnectionData.cs:43), 而 Character.CameraDirection 直接返回
        // Client.ClientInfoData._0023kYb (Character.cs:979)。移动方向完全由它决定:
        //
        //     GetMoveDirection():  W → += cameraDirection
        //                          S → -= cameraDirection
        //                          A/D → ±(cameraDirection.Y, -cameraDirection.X)
        //
        // 填成 (1634.9, 3409.2) 之后四个键全被压到那条对角线的正负方向上。
        //
        // 正确的值由客户端在握手里报上来 —— 它读自己的 camera.cfg 算出来
        // (UnityClient.cs:2794-2797)。拿不到就我们自己按同一条路算一遍。
        //
        // **不要猜这个值。** 实测 Maps\camera.cfg 里是 (67.175, 67.175), 于是
        // 朝向是 (-0.707, -0.707) —— 一条 45 度对角线, 不是任何一个轴向。
        // 我一开始想当然写了 (0,1) 作为兜底, 那偏离真值 135 度。
        Vector2 cameraDirection = client.ClientConnectionData?._0023kYb ?? default;
        if (cameraDirection == Vector2.Zero)
        {
            cameraDirection = LoadCameraDirection();
            Log.Warning("战斗服 UDP {Port} slot {Index} 握手里没有相机朝向, 自行读取 camera.cfg 得 ({X:F3},{Y:F3})",
                Port, client.Index, cameraDirection.X, cameraDirection.Y);
        }

        client.ClientInfoData = new ClientInfoData
        {
            _0023kYb = cameraDirection,             // 相机朝向 (决定 WASD 的世界方向)
            _00238g = $"Player{client.Index}",      // 显示名
            _0023pg = player.TeamId,                // 队伍 —— 跟着实体走, 别再写死教程的
        };

        // 场外配的武器。必须在 ClientInfoData 建好**之后** ——
        // EquipHaxWeaponNew 往它的 _0023lYb/_0023mYb 里写。
        EquipLoadout(client, player, member.Choice);
        EquipConsumables(client, player, member);

        Log.Information("战斗服 UDP {Port} slot {Index} 生成玩家: {Character} @ ({X:F1},{Y:F1}) " +
                        "playerIndex={PlayerIndex} charIndex={CharIndex} active={Active}",
            Port, client.Index, character, spawn.X, spawn.Y,
            player.IndexPlayer, player.IndexCharacter, player.IsActive);

        failure = null;
        return true;
    }

    /// <summary>
    /// 掉线重连: 把他原来的角色接到新连接上。没有原来的角色就返回 false (照常新生成)。
    ///
    /// ## 为什么不能直接重新生成
    ///
    /// 旧实体还在场上 (我们断线时故意不销毁它) —— 再生成一个的话, 世界里站着两个他,
    /// 而装备、等级、技能加点、剧情进度都在旧的那个身上。
    ///
    /// ## 要重新接的几样
    ///
    /// 都是"客户端认得出自己"的那几条, 和首次生成同一套 (见 TrySpawnPlayer 里的长注释):
    /// <c>Player.Client</c> / <c>LocalPlayer</c> / 控制权 + 镜头 / <c>ClientInfoData</c> /
    /// <c>ConnectedPlayers</c>。武器要重新发一遍 —— 它们记在 <c>ClientInfoData</c> 里,
    /// 而那个是跟着连接新建的。
    /// </summary>
    private bool TryReattachPlayer(Client client, MatchMember member)
    {
        if (member.UserId == 0
            || !_playersByUser.TryGetValue(member.UserId, out Player player)
            || player == null
            || !player.IsActive)
        {
            return false;
        }

        player.Client = client;
        client.LocalPlayer = player;
        DIE.Server.Game.ClientVisibility.RegisterPlayer(_sim, client);
        client.SetControlledCharacter(player);
        client.SetLookAtCharacter(player);

        Vector2 cameraDirection = client.ClientConnectionData?._0023kYb ?? default;
        if (cameraDirection == Vector2.Zero)
        {
            cameraDirection = LoadCameraDirection();
        }

        client.ClientInfoData = new ClientInfoData
        {
            _0023kYb = cameraDirection,
            _00238g = $"Player{client.Index}",
            _0023pg = player.TeamId,
        };

        EquipLoadout(client, player, member.Choice);
        return true;
    }

    /// <summary>
    /// 挑一个出生点。
    ///
    /// ## 两条路都得留
    ///
    /// 不同游戏模式的出生点来源**不一样**, 而且没有哪一条能覆盖全部:
    ///
    /// | 模式 | GetSpawnPosition | PlayerSpawnPoints |
    /// |---|---|---|
    /// | Horde | 当前 SpawnId 的 Checkpoint (HordeMode.cs:316) | **空** |
    /// | Tutorial | **没覆写**, 基类返回 Zero (GameMode.cs:1448) | 有 |
    ///
    /// 所以先问模式多态的 <c>GetSpawnPosition</c>, 拿到零再退回地图上的
    /// <c>PlayerSpawnPoints</c>。
    ///
    /// 最初这里只读 <c>PlayerSpawnPoints</c> —— 在教程图上一直好用, 换到 Horde
    /// 就会静默退化成原点 (玩家生在地图外)。这是单元测试先抓到的, 不是真机。
    ///
    /// <c>IsStart</c> 区分开局出生点和复活点。
    /// </summary>
    /// <summary>
    /// 把玩家场外配好的武器 (带改装) 和装置装上。实现见 <c>LoadoutEquipper</c>。
    ///
    /// 玩家实测过两轮: "场外装备好的武器进游戏没有装备" (票据没带武器, 09-11 补图纸号),
    /// 以及 2026-09-15 审核改装/装置 —— 图纸号装出来的是没改装的调试武器, 装置压根没进局。
    /// 现在票据带完整的 Unique / Gadget (<c>PlayerChoiceCodec</c> v3)。
    ///
    /// 客户端真正装上靠 <c>GameMessageType.ClientInfo</c> 消息 (<c>ClientInfoSender.BroadcastEquipUpdate</c>),
    /// 光在服务端装好客户端那边仍然是空手。
    /// </summary>
    /// <summary>
    /// 场外装的消耗品放进局内三格 (数量签票时已按背包核好)。只在首次生成时放 —— 重连接回原角色不补满。
    /// </summary>
    private void EquipConsumables(Client client, Player player, MatchMember member)
    {
        IReadOnlyList<DIE.Server.Data.ConsumableSlot> slots = member.Choice?.Consumables;
        if (slots == null || slots.Count == 0 || member.UserId == 0)
        {
            return;
        }

        // 教程不带 (原厂进局装备也跳过教程图, Client.cs:574)
        if (_sim.MapIndex == ConductorCrafting.MapManagerData.TutorialMapIndex)
        {
            return;
        }

        Log.Information("战斗服 UDP {Port} slot {Index} 消耗品: {Summary}", Port, client.Index,
            _consumables.Equip(member.UserId, player, slots));
    }

    private void EquipLoadout(Client client, Player player, DIE.Server.Data.PlayerChoice? choice)
    {
        IReadOnlyList<ConductorCrafting.Unique> weapons = choice?.Weapons ?? Array.Empty<ConductorCrafting.Unique>();
        IReadOnlyList<ConductorCrafting.Gadget> gadgets = choice?.Gadgets ?? Array.Empty<ConductorCrafting.Gadget>();
        if (weapons.Count == 0 && gadgets.Count == 0)
        {
            Log.Information("战斗服 UDP {Port} slot {Index} 票据里没有武器和装置 —— " +
                            "玩家保持空手 (Solo 教程是正常的; 别的模式说明菜单链路那侧没解出装备)",
                Port, client.Index);
            return;
        }

        DIE.Server.Game.LoadoutEquipper.Result result;
        try
        {
            result = DIE.Server.Game.LoadoutEquipper.Equip(_sim, client, player, weapons, gadgets);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "战斗服 UDP {Port} slot {Index} 装备失败", Port, client.Index);
            return;
        }

        Log.Information("战斗服 UDP {Port} slot {Index} 装备: 武器 {Weapons} 把 (改装 {Designs} 个), 装置 {Gadgets} 个, " +
                        "手上 {Equipped} —— {Summary}",
            Port, client.Index, result.Weapons, result.Designs, result.Gadgets,
            player.EquippedWeaponSchematicId, result.Summary);

        // 客户端那一侧: 没有这条消息, 上面装的东西它一概不知道。
        int sent = DIE.Server.Game.ClientInfoSender.BroadcastEquipUpdate(_network);
        Log.Information("战斗服 UDP {Port} 装备更新已下发 {Count} 条 (GameMessageType.ClientInfo)",
            Port, sent);
    }

    /// <summary>
    /// 死了在哪复活 —— 和**开局出生点**是两件事。
    ///
    /// 玩家踏过 <c>Spawn_RaidRespawn</c> 关联的区域后, 就在那里复活;
    /// 一个都没踏过 (刚进图就死) 才退回开局出生点。
    ///
    /// 之前这里直接用 <see cref="PickSpawnPoint"/>, 于是**永远回图头** ——
    /// 那正是玩家问"地图里的复活点有没有实装"的由来。
    /// </summary>
    private Vector2 PickRespawnPoint(Client client)
    {
        if (client?.LocalPlayer != null
            && _raidRespawns.TryGetRespawnPoint(client.LocalPlayer, out Vector2 advanced))
        {
            return advanced;
        }

        // 拾荒: 回自己队的复活点 (没冲出起点前是起点, 之后是卡车旁)
        return PickSpawnPoint(client?.LocalPlayer?.TeamId ?? TeamId.Team1);
    }

    /// <summary>
    /// 玩家进哪个队 —— **按模式分, 不能写死**。
    ///
    /// ## 为什么不是一个常量
    ///
    /// 第一版写死 <c>TutorialEntitySpawner.PlayerTeam</c> (Team2 蓝队), 因为
    /// 当时只有教程。玩家在 Starter Mission (Scout_02_Lab) 实测报了两个症状,
    /// 其实是同一个根因:
    ///
    ///   "卡车是橙队而玩家是蓝队"      -> 颜色不对
    ///   "没有出生在自家卡车旁"        -> 位置也不对
    ///
    /// 地图数据 (探针实测, 见 <c>ScoutMissionProbeTests</c>):
    ///
    ///   Scout_02_Lab  PlayerSpawnPoint  team=Team1  pos=(-171.1, 481.4)
    ///                 TruckerSpawnPoint team=Team1  pos=(-141.6, 484.7)
    ///                 GetSpawnPosition(Team1) = (-171.1, 481.4)
    ///                 GetSpawnPosition(Team2) = **(0, 0)**
    ///
    /// 卡车本来就在出生点旁边 30 单位。队伍一错, 凡是拿玩家自己的 TeamId 去
    /// 查出生点的地方都会退化成原点 —— 位置和颜色是**同一个 bug 的两个面**。
    ///
    /// ## 教程是特例, 不是通例
    ///
    /// 教程图上玩家是 Team2 (蓝), 那是玩家实测认过的 (踩坑 18: 客户端一段代码
    /// "要求某个值"不等于"原版就是那个值", 颜色以实测为准)。而 <see cref="ScoutMission"/>
    /// 和 <c>HordeMode</c> 都按 <c>TeamId.Team1</c> 找出生点
    /// (ScoutMission.cs:126 / -bd/-5cc.cs:133 是各自的实现)。
    ///
    /// 所以判据是**模式**: 教程 Team2, 拾荒按分队, 其余 Team1。
    ///
    /// ## 拾荒 (4v4v4) 按分队
    ///
    /// 开局时匹配服已经把人分进三队 (<see cref="ScavengerTeams"/>, 名单上的 <c>MatchMember.Team</c>)。
    /// 没分到的 (重连换了号之类) 退回 1 队。
    /// </summary>
    private TeamId PickPlayerTeam(MatchMember member)
    {
        if (_sim?.GameMode?.Type == GameModeType.Tutorial)
        {
            // 唯一的真相来源 —— Amber 和卡车队友 NPC 都从那里取,
            // 改一处漏一处她就成敌人了。
            return DIE.Server.Game.TutorialEntitySpawner.PlayerTeam;
        }

        if (_sim?.GameMode is ConductorGameLogic.GameModes.ScavengerHunt
            && member?.Team is >= 1 and <= ScavengerTeams.TeamCount)
        {
            return (TeamId)member.Team;
        }

        return TeamId.Team1;
    }

    private Vector2 PickSpawnPoint(TeamId team)
    {
        // 拾荒: 每队有自己的出生角落 (还没冲出去时是起点, 冲出去后是卡车旁, 原厂 ScavengerHunt.GetSpawnPosition 按阶段挑)。
        if (_sim?.GameMode is ConductorGameLogic.GameModes.ScavengerHunt hunt)
        {
            Vector2 teamSpawn = hunt.GetSpawnPosition(team);
            if (teamSpawn != Vector2.Zero)
            {
                return teamSpawn;
            }
        }

        // Horde 走这条。注意它依赖 Checkpoint 实体已经生成 —— 那需要
        // GameManagerServer.SpawnGameObject (基类是空的, 见那里的说明)。
        //
        // 这里的 Team1 是**地图上出生点标记的编号**, 和玩家自己是哪队无关 ——
        // 原厂教程 LoadGameMode 找出生点用的也是 `playerSpawnPoint.Team ==
        // TeamId.Team1` (-bd/-5cc.cs:133)。玩家改成蓝队之后这一行不要跟着改,
        // 改了就找不到出生点了。
        Vector2 fromGameMode = _sim.GameMode?.GetSpawnPosition(TeamId.Team1) ?? new Vector2();
        if (fromGameMode != Vector2.Zero)
        {
            return fromGameMode;
        }

        // 教程走这条。
        System.Collections.Generic.List<PlayerSpawnPoint> points = _sim.GameMode?.PlayerSpawnPoints;
        if (points == null || points.Count == 0)
        {
            Log.Warning("战斗服 UDP {Port} 地图 {Map} 既没有 GetSpawnPosition 也没有 PlayerSpawnPoint, " +
                        "出生点退回原点 —— 玩家会生在地图外", Port, MapIndex);
            return new Vector2();
        }

        foreach (PlayerSpawnPoint point in points)
        {
            if (point.IsStart)
            {
                return point.Position;
            }
        }

        return points[0].Position;
    }

    /// <summary>
    /// 原版 <c>NetConnection.Approve(hail)</c> 的等价实现 —— 那个方法在这份 Lidgren 里
    /// 被裁掉了 (客户端从不批准连接)。三步照抄自 <c>-tde.cs</c> 里审批禁用时的分支。
    /// </summary>
    private static void Approve(_0023tde connection, _0023yee hail)
    {
        connection._0023sce = hail;                             // m_localHailMessage
        connection._0023Fce = 0;                                // m_handshakeAttempts
        // 第二个参数 false = 投递到待发队列, 由下一次 Update 走正常路径发出;
        // true 会当场直接发, 那要求调用方就在 Lidgren 的网络线程上。
        connection._00238ce((float)_0023nge._0023mge(), false);  // SendConnectResponse
    }

    private int AllocateClientIndex(_0023tde connection)
    {
        for (int i = 0; i < MaximumConnections; i++)
        {
            if (!_slots.ContainsValue(i))
            {
                _slots[connection] = i;
                return i;
            }
        }

        return -1;
    }

    private void HandleStatusChanged(_0023cee message)
    {
        _0023tde connection = message._0023bee();

        // 读 _0023oce 而不是 _0023cbe, 这个区别很要命:
        //   _0023cbe = 连接的**实时**握手状态, 会在 InitiatedConnect -> RespondedConnect
        //              -> Connected 之间连续变化
        //   _0023oce = **随本条消息投递**的状态, 由 ReadMessage 从载荷首字节读出并写入
        //              (见 -gce.cs:217 的 _0023Jbe)
        // 用 _0023cbe 的话, 握手期的多条 StatusChanged 在处理时字段可能都已经是 Connected,
        // 于是同一个客户端被算成连上好几次。原厂客户端读的也是 _0023oce。
        NetConnectionStatus status = connection._0023oce;

        switch (status)
        {
            case NetConnectionStatus.Connected:
                ConnectedClients++;
                Interlocked.Exchange(ref _emptySinceTicks, 0);
                // 握手数据里应该带着客户端身份 (authticket 之类)。3B 只记录长度,
                // 解析和身份关联留给 3C。
                _0023cee hail = connection._00234ce();
                Log.Information("战斗服 UDP {Port} 客户端已连接 {EndPoint} (握手数据 {HailBits} bit, 在线 {Count})",
                    Port, connection._0023pce, hail?._0023Bae() ?? 0, ConnectedClients);
                break;

            case NetConnectionStatus.Disconnected:
                ConnectedClients = Math.Max(0, ConnectedClients - 1);
                if (ConnectedClients == 0)
                {
                    Interlocked.Exchange(ref _emptySinceTicks, DateTime.UtcNow.Ticks);
                }
                // 槽位必须还回去 —— 否则重连一次占一个, MaximumConnections 个来回之后
                // AllocateClientIndex 永远返回 -1, 表现是"服务器满员"但实际没人在线。
                if (_slots.TryGetValue(connection, out int leavingIndex))
                {
                    MatchMember leavingMember = Roster.MemberAt(leavingIndex);
                    if (leavingMember != null)
                    {
                        _connectedUsers.TryRemove(leavingMember.UserId, out _);
                    }

                    Roster.Release(leavingIndex);
                    DeactivateClient(leavingIndex);
                    if (_network.GetClient(leavingIndex) is Client leaving)
                    {
                        // 角色**留在场上** (重连要接回它), 但要从 ConnectedPlayers 里摘掉 ——
                        // 经验是按那个列表**均摊**的 (-bd/-Zt.cs:130), 不摘的话在场玩家
                        // 拿到的经验会凭空变少, 而且一点报错都没有。
                        DIE.Server.Game.ClientVisibility.UnregisterPlayer(_sim, leaving);
                        leaving.IsConnected = false;
                    }
                }

                _slots.Remove(connection);
                _channels.Remove(connection);
                Log.Information("战斗服 UDP {Port} 客户端已断开 {EndPoint} (在线 {Count}, 占用槽位 {Slots})",
                    Port, connection._0023pce, ConnectedClients, _slots.Count);
                break;

            default:
                Log.Debug("战斗服 UDP {Port} 连接状态 {Status} ({EndPoint})", Port, status, connection._0023pce);
                break;
        }
    }

    /// <summary>
    /// 无头表现层 —— 它把每个被调用过的 <c>IViewManager</c> 成员都计了数,
    /// 收尾时打出来 (见 <see cref="DumpViewManagerCalls"/>)。
    /// </summary>
    private DIE.Server.Game.HeadlessViewManager _view;

    /// <summary>
    /// 把这一局里模拟层回调过表现层的成员打一遍。
    ///
    /// ## 这套仪表装好了但一直没人读 (2026-09-18 审校发现)
    ///
    /// <c>HeadlessViewManager</c> 那 83 个成员全是空转 + 计数, 类注释写得很清楚:
    /// "**返回 default 是'暂未实现', 不是'确定无害'**, 这个区别只能靠实跑消除" ——
    /// 可 <c>Dump()</c> 从来没有被调用过, 所以从没消除过。
    ///
    /// 现在每局收尾打一次。榜上有名又返回引用类型的成员, 就是"可能需要真语义"的候选
    /// (返回 null 会让上层 NRE)。日志级别是 Debug —— 平时不占地方, 要看就开 Debug 跑一局。
    /// </summary>
    private void DumpViewManagerCalls()
    {
        if (_view == null)
        {
            return;
        }

        try
        {
            Log.Debug("战斗服 UDP {Port} 本局回调过的表现层成员:", Port);
            _view.Dump();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "战斗服 UDP {Port} 打印表现层调用统计失败", Port);
        }
    }

    public void Dispose()
    {
        if (!_running)
        {
            return;
        }

        _running = false;

        DumpViewManagerCalls();

        try
        {
            _peer.Shutdown("match ended");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关闭战斗服 UDP {Port} 时出错", Port);
        }

        Log.Information("战斗服 UDP {Port} 已关闭", Port);
    }
}
