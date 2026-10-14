using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.MapObjects;
using ConductorGameLogic.View;
using FarseerPhysics.Common;
using StunGameNetwork;
using _0023t5;

namespace DIE.Server.Game;

/// <summary>
/// 十字路口的**局内流程**: 领任务 -> 跑到事件点 -> 做完 -> 回去交 -> 三轮之后夜袭收尾。
///
/// ## 玩家实测的两条病 (2026-09-12)
///
/// "能进图, 正常走动打怪, **目标 UI 空着**, 出生点位置合理, **怪都刷在出生点基地附近**。"
///
/// 两条是同一件事: 十字路口的局内玩法一句都没接。
///
///   - 目标 UI 空着 —— 客户端每帧读 <c>GameMode._0023ei(角色)._0023Mmd[0]</c>
///     (UI_ObjectivesCrossroadsBindings.cs:73), 那张表我们一直没填。**客户端那半
///     是完整的**: 文案、奖牌、计时、任务槽都在, 就等服务端把
///     <see cref="RaidEventObjectiveState"/> 放进去。
///   - 怪都刷在基地附近 —— 全图 390 个僵尸摆位点里, 只有基地夜袭那 18 个
///     (<see cref="Event_Assault"/>, ID 相同) 被 <see cref="EventSpawnDirector"/>
///     认领了, 其余 370 多个按 ID 一个都配不上, 全落进"未知事件ID"的日志。
///     归属判据见 <see cref="CrossroadEventCatalog"/>。
///
/// ## 一局长什么样
///
/// <code>
/// Briefing  领任务      目标 UI: FetchNewQuest / MoveTo     小地图指任务发布点
///     │ 走到任务发布点 (QuestGiverSpawnPoint)
///     ▼
/// Travel    赶路        目标 UI: 这次的事件 / MoveTo        小地图指事件区域
///     │ 走进事件区域 (Event_RaidEvent)
///     ▼
/// Active    干活        目标 UI: 这次的事件 / Objective1 + 计时 + 奖牌线
///     │           偷物资/摇旗 -> 按交互键读条; 清场 -> 打光这一片的怪
///     │           期间按刷怪组分波补怪 (用这个区域自己的摆位点)
///     ▼
/// Debrief   回交        目标 UI: ReturnToQuestGiver / MoveTo
///     │ 三轮做完
///     ▼
/// Finale    夜袭        入夜 + 目标 UI: EndEvent / MoveTo, 回卡车
///     │ 回到基地 (基地夜袭那 30 只由 EventSpawnDirector 按原厂数据刷)
///     ▼
/// Over      发结算 + TeamFinished (走宿主的回调, 和 Scout 一样)
/// </code>
///
/// ## 哪些是原厂的, 哪些是我们定的
///
/// 原厂: 事件位和它们的标签、每种事件的变体和交互体类型、按难度分的刷怪组、
///       时限 (<c>EventMedalTimeData.Start</c> 实测 180~200 秒)、奖牌线
///       (0/50/100)、任务槽是 3 格 (客户端 <c>MissionScores</c> 数组长度)、
///       基地夜袭的 30 只和它的 18 个摆位点。
///
/// 我们定的 (都在下面各自的属性注释里写了理由):
///   - 到达判定的半径 (<see cref="ArriveRadius"/> / <see cref="QuestRadius"/>)
///   - 读条时长 (<see cref="SecondsToChannel"/>) —— 原厂数据里只有视觉没有时长
///   - 分波节奏和每波预算 (<see cref="WaveSeconds"/> / <see cref="WaveBudget"/>)
///   - 领任务/交任务靠**走过去**而不是按键 (见 <see cref="QuestRadius"/>)
///
/// ## 没做的
///
///   - 护送 (<c>EscortCivilian</c>) 和解救平民 (<c>FreeCivilians</c>): 要平民实体,
///     而十字路口的客户端对象池里连 Trucker 都是 0。<see cref="CrossroadEventCatalog.Playable"/>
///     把它们排除在选题之外 —— 宁可少两类, 也别让目标指着一块空地。
///   - 任务之间的无线电对话 (<c>AddTransmissionData</c>): 通道是通的
///     (<see cref="TransmissionDirector"/> 已经在用), 但哪一段配哪句话要对着
///     <c>raideventsettings.data</c> 的 Transmissions 表核, 实测里那张表是空的。
/// </summary>
public sealed class CrossroadMissionDirector
{
    /// <summary>一局走到哪一步了。</summary>
    public enum Stage
    {
        /// <summary>还没开局 (地图没扫、场上没人)。</summary>
        Idle,

        /// <summary>去领任务。</summary>
        Briefing,

        /// <summary>赶往事件区域。</summary>
        Travel,

        /// <summary>正在做这个事件。</summary>
        Active,

        /// <summary>回去交任务。</summary>
        Debrief,

        /// <summary>夜袭收尾。</summary>
        Finale,

        /// <summary>这一局结束了。</summary>
        Over,
    }

    private readonly CrossroadEventCatalog _catalog = new CrossroadEventCatalog();
    private readonly List<CrossroadEventCatalog.Mission> _missions =
        new List<CrossroadEventCatalog.Mission>();
    private readonly List<Character> _spawned = new List<Character>();

    /// <summary>大厅选的难度档 0..4。宿主在 <c>LoadMap</c> 之后设进来。</summary>
    public int Difficulty { get; set; }

    /// <summary>
    /// 一局几个任务 —— **固定 3 个**, 和客户端任务栏的 3 颗星一一对应。
    ///
    /// ## 来回改过两次
    ///
    /// 先按玩家说的"随感染等级增加"做成 1/2/3 个, 双人实测后改成 3/4/5 个。玩家随后看了原版视频,
    /// 确认这版客户端 (v0.8.5) 的任务栏写死 3 颗星 (<c>UI_ObjectivesCrossroadsBindings</c> 评分数组长 3、
    /// 上色循环 <c>i&lt;3</c>; 第 4 颗评分会越界、每帧重试), 视频里 4 颗星的是更早的界面 ——
    /// 定下来"保持三颗星, 任务数不再随感染等级增加" (2026-09-13)。
    /// </summary>
    public int MissionCount { get; set; } = StarSlots;

    /// <summary>客户端星星槽的格数 (任务栏上的 3 颗星 + 结算面板的 3 颗星)。超过的任务不发星。</summary>
    public const int StarSlots = 3;

    /// <summary>
    /// 走进事件区域算"到了"的半径。
    ///
    /// **我们定的。** 事件区域本身是个多边形 (<c>Event_Trigger</c>), 实测边长
    /// 125~310 单位, 中心到边缘一两百。半径 120 大致等于"踏进这片地",
    /// 而且比 <see cref="CrossroadEventCatalog"/> 给这个区域的刷怪位分布还小一点 ——
    /// 玩家到了才开始刷, 不会隔着半张图凭空冒怪 (教程图上那个坑的教训, 见
    /// <see cref="EventSpawnDirector.MaxTriggerRadius"/>)。
    /// </summary>
    public float ArriveRadius { get; set; } = 120f;

    /// <summary>
    /// 走到任务发布点 / 卡车算"到了"的半径。
    ///
    /// ## 为什么领任务不用按键
    ///
    /// 按键那条路走不通: 客户端的交互提示由 <c>UnityRaidEventView</c> 按
    /// <c>raideventsettings.data</c> 里该变体的 <c>Interactions</c> 画出来,
    /// 而 <c>FetchNewQuest</c> / <c>ReturnToQuestGiver</c> 这两条**一个交互体都没有**
    /// (实测: 交互表为空)。没有交互体就没有按键提示, 玩家按什么都不会上行。
    ///
    /// 所以领任务/交任务只能靠**走过去**。60 单位 = 站到任务发布点跟前
    /// (那个点的 <c>QuestArea</c> 区实测只有 25×28 单位, 比这个还小)。
    ///
    /// **这是我们定的。**
    /// </summary>
    public float QuestRadius { get; set; } = 60f;

    /// <summary>
    /// 偷物资时要留在卡车多近。交给 <see cref="ScoutObjectiveDirector.HoldRadius"/>
    /// (80, 实测校准过: 卡车交互体很大, 站不到原点上)。
    /// </summary>
    public float HoldRadius { get; set; } = 80f;

    /// <summary>
    /// 占领时要留在圈里多近。交给 <see cref="ScoutObjectiveDirector.CaptureRadius"/>
    /// (玩家实测四轮: 25 太大 -&gt; 15 -&gt; 20 -&gt; "再改大点, 到 30")。
    /// </summary>
    public float CaptureRadius { get; set; } = 30f;

    /// <summary>
    /// 偷物资 / 占领读满要多久 (占领不含起手摇旗那 5 秒), 一个人独占时。
    ///
    /// **玩家定的数 (2026-09-21)。** 原厂这个时长是真的没有 ——
    /// <c>InteractionViewData</c> 只有 Type / Texts / Effect, <c>ObjectiveViewData</c>
    /// 只有 Objective / Text, 整个 <c>raideventsettings.data</c> 里一个时长字段都没有。
    ///
    /// 玩家实测 "补给点占领速度太快": 上一版基准 22 秒配线性加速, 四人只要 8.8 秒。
    /// 加速表换成原版的 (<see cref="CaptureSpeed"/>, 四人 ×2.0) 之后四人是 11 秒,
    /// 玩家把基准定到 60 —— 四人 30 秒, 单人 60 秒。
    ///
    /// 能拿来对照的原厂数只有那几个交互 buff: 占领补给点 15 秒、偷物资 12 秒、
    /// 交付 10 秒。但那是卡车和拾荒补给点那几条, 不是十字路口的任务点,
    /// 而且十字路口的事件比那些重得多 (带警报波次), 长一些说得通。
    /// </summary>
    public float SecondsToChannel { get; set; } = 60f;

    /// <summary>
    /// 事件期间每隔多久补一波怪。**留空 = 按这个变体自己那一档**
    /// (原厂曲线名 -> <see cref="SpawnIntensity"/>)。给了值就无条件盖掉分档。
    /// </summary>
    /// <remarks>
    /// 这四个 (<see cref="WaveSeconds"/> / <see cref="WaveBudget"/> /
    /// <see cref="LiveCap"/> / <see cref="MaxPerWave"/>) 原来是所有事件共用的固定数,
    /// 2026-09-22 起改成"覆盖"语义: 平时一个都不设, 让 <see cref="SpawnIntensity"/>
    /// 按原厂曲线名分档。测试里把 <see cref="MaxPerWave"/> 设 0 就能关掉杂兵。
    /// </remarks>
    public float? WaveSeconds { get; set; }

    /// <summary>
    /// 一波的刷怪预算。配合原厂的单只成本 (Walker 0.01 / 特殊 0.2) 用。
    /// 留空 = 按分档, 见 <see cref="WaveSeconds"/>。
    /// </summary>
    public float? WaveBudget { get; set; }

    /// <summary>
    /// 一个事件最多同时留几只活的。留空 = 按分档, 见 <see cref="WaveSeconds"/>。
    ///
    /// 分档里的上限有依据: 客户端的模型缓存 (Crossroads 栏) 给 Walker 100 / 各特殊 9,
    /// 而 <see cref="ZombieFactory"/> 会挡住超额的生成 —— 所以行尸那一头放得开,
    /// 特殊怪那一头挡在工厂里。
    /// </summary>
    public int? LiveCap { get; set; }

    /// <summary>
    /// 一波最多几只 —— 预算再宽也不一口气放这么多以上。留空 = 按分档。
    /// 设 0 = 这一局不刷杂兵 (测试用)。
    /// </summary>
    public int? MaxPerWave { get; set; }

    /// <summary>
    /// 本局感染等级 (1..17)。宿主开局设进来, 刷怪量按它和难度档缩放, 见 <see cref="Scale"/>。
    /// </summary>
    public int InfectionLevel { get; set; } = 1;

    /// <summary>
    /// 刷怪量倍率: 感染等级每高 1 级 +6%, 再乘难度档的**原厂**强度倍率。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "路上的僵尸刷新频率和数量是和感染等级挂钩的吗? 现在看路上刷新的僵尸数量很少, 感染等级为 1。"
    ///
    /// 上一版不挂钩, 全是固定数。感染等级那一档 (+6%/级) 仍是我们定的 ——
    /// 原厂的强度曲线在 Unity 资源里拿不到。
    ///
    /// ## 难度档那一维已经换成原厂的数 (2026-09-21)
    ///
    /// 原来是"每档 +30%" (1.0 / 1.3 / 1.6 / 1.9 / 2.2), 也是我们编的占位。
    /// 后来在 <c>StaticCrossroadsGUIData</c> 里找到了原厂自己的
    /// <c>ZombiePowerPercentage</c>: **100 / 180 / 330 / 620 / 860**,
    /// 于是换成它 (见 <see cref="CrossroadDifficulty.PowerMultiplier"/>)。
    ///
    /// 落差很大 —— Endemic 从 2.2 倍变成 8.6 倍。原厂那一档本来就要求满队
    /// 4 人 + 16 阶装备, 陡是应该的; 手感要是过头, 调
    /// <c>CrossroadDifficulty.PowerScale</c> 而不是改这里, 那样"原厂的数"和
    /// "我们的调整"还能分得清。
    ///
    /// 1 级 Normal = 1.0, 17 级 Endemic ≈ 16.8。
    /// </summary>
    public float Scale => (1f + 0.06f * Math.Max(0, InfectionLevel - 1))
                          * CrossroadDifficulty.PowerMultiplier(Difficulty);

    /// <summary>
    /// 收尾倒计时 (秒)。客户端那句 "Caravan is leaving in {timeleft}..." 从它 (<c>_0023l9._0023M7</c>) 开始倒数。
    ///
    /// 原厂模式数据默认 90 (-z/-U7.cs)。玩家实测 (2026-09-14): 先定 30 秒, 同日又改 "夜袭倒计时改到一分钟"。
    /// **玩家定的数。**
    /// </summary>
    public float FinaleSeconds { get; set; } = 60f;

    /// <summary>诊断/测试: 夜袭里一共被激怒 + 改成奔跑的僵尸有几只 (开场那一批 + 后面刷出来的)。</summary>
    public int RalliedAtFinale { get; private set; }

    /// <summary>夜袭里已经激怒过的僵尸 (IndexGlobal, 回池次数)。</summary>
    private readonly HashSet<(int Index, int Uses)> _engaged = new HashSet<(int, int)>();

    /// <summary>
    /// 夜袭 (收尾倒计时) 里打死的 Looter 掉多少补给。
    ///
    /// 玩家实测 (2026-09-14): "夜袭的时候刷的 looter 死亡现在只掉 3 点物资, 改为掉 30 点。"
    /// 原厂是平衡数据 <c>_0023j7f._0023Ntb</c> = 3 (Supplies.cs:321), 夜袭里的改成 30;
    /// 任务期间的 Looter (比如 "Looter Bros") 照原厂。**玩家定的数。**
    /// 这是**基础量** (感染 1 级), 结果照样乘感染等级曲线 (<see cref="GameManagerServer.ScaleSupplies"/>)。
    /// </summary>
    public int FinaleLooterSupplies { get; set; } = 30;

    /// <summary>
    /// 没有 BOSS 的击杀任务 (伏击 / Looter 那 21 个变体), 具名目标**合计**掉多少补给。
    ///
    /// 玩家实测 (2026-10-04): "crossroads 中还是会出现那种不给奖励的任务, 刷几次精英怪就结束了。"
    /// 这类任务 2~7 只目标, 30 秒上下就杀完结算, 每只照原厂掉 3 —— 一共十来点;
    /// 09-22 加重的伴随波次 (WaveHeavy) 在这半分钟里也只来得及刷两三波。
    /// 原厂数据里没有"任务完成奖励"这个字段, 囤积者 / 巨型任务的物资是 BOSS 自己抛的 150
    /// (<c>_0023Qtb</c>)。**玩家定**: 这类任务也给 150, 按目标数平摊 (5 只每只 30, 2 只每只 75)。
    /// 这是**基础量**, 照样乘感染等级曲线。
    ///
    /// 用到时才读: 那个 150 在原厂平衡数据里, 而本类在战斗服构造时就建好了 —— 那会儿
    /// 平衡数据还没加载, 写成字段初始值会当场空引用, 战斗服起不来。
    /// </summary>
    public int NamedTargetSupplyPool
    {
        get => _namedTargetSupplyPool
               ?? GameManagerServer.BossSupplyPacks * GameManagerServer.BossSuppliesPerPack;
        set => _namedTargetSupplyPool = value;
    }

    private int? _namedTargetSupplyPool;

    /// <summary>
    /// 给 <see cref="GameManagerServer.SupplyDropOverride"/> 用:
    /// 夜袭里的 Looter -&gt; <see cref="FinaleLooterSupplies"/>;
    /// 没有 BOSS 的击杀任务的具名目标 -&gt; <see cref="NamedTargetSupplyPool"/> 平摊的那一份。
    /// </summary>
    public int? SupplyDropFor(Character dead)
    {
        if (dead == null)
        {
            return null;
        }

        if (Step == Stage.Finale)
        {
            return dead.IsLooter ? FinaleLooterSupplies : null;
        }

        if (Step == Stage.Active
            && Current is { } mission
            && mission.Choice.Type == RaidEventType.KillEnemy
            && !mission.HasBoss)
        {
            return _targets.ShareOf(dead, NamedTargetSupplyPool);
        }

        return null;
    }

    /// <summary>收尾: 原厂夜袭的刷怪位 (具名目标 End Event Looters 也刷在这里)。诊断/测试。</summary>
    public IReadOnlyList<Spawn_EventEntities> FinaleSpawns => _finaleSpawns;

    /// <summary>收尾还剩多少秒 (不在收尾时为 0)。</summary>
    public float FinaleLeft { get; private set; }

    /// <summary>
    /// 只从这几类任务里抽 —— 测试用, 也方便实测时盯住某一类 (比如只刷护送)。null = 全池。
    /// </summary>
    public RaidEventType[] OnlyTypes { get; set; }

    /// <summary>现在走到哪一步。</summary>
    public Stage Step { get; private set; } = Stage.Idle;

    /// <summary>做完 (含失败) 的任务数。</summary>
    public int MissionsDone { get; private set; }

    /// <summary>拿到奖牌的任务数 (没超时的)。</summary>
    public int MissionsWon { get; private set; }

    /// <summary>这一局排了几个任务。</summary>
    public int Planned => _missions.Count;

    /// <summary>
    /// 这一局排了哪几类任务, 每个任务一格 (<c>RaidEventType</c> 转 byte)。
    ///
    /// ## 结算报文要它, 而且**不能是空的**
    ///
    /// 客户端 <c>AchievementManager.OnGameCompleted</c> 在
    /// <c>QueueType.RaidMission</c> 那一支直接:
    ///
    ///     byte[] types = rewards.Data.CrossroadMissionTypes;
    ///     foreach (byte b in types) { }          // ← 没有 null 检查
    ///
    /// 旁边那个 <c>CrossroadCompleteScores</c> 倒是判了 null, 唯独这个没判。
    /// 而原厂的反序列化是"长度为 0 就不赋值"
    /// (<c>PlayerRewards</c>:888), 所以**发空数组也救不了** —— 字段会留在
    /// struct 的默认值 null 上, 那个 foreach 当场 NRE, 把整条
    /// <c>OnGameProcessEnd</c> 打断: 奖励界面弹不出来, 大厅的钱和经验也不刷新。
    ///
    /// 玩家 2026-09-21 在云服务器上打完一局十字路口撞到的就是这个。
    /// </summary>
    public IReadOnlyList<byte> MissionTypes =>
        _missions.Select(m => (byte)m.Choice.Type).ToList();

    /// <summary>当前任务剩余时间 (秒)。不在 <see cref="Stage.Active"/> 时为 0。</summary>
    public float SecondsLeft { get; private set; }

    /// <summary>当前任务的进度 0..1 (读条型是读条, 清场型是"这波清了多少")。</summary>
    public float Progress { get; private set; }

    /// <summary>诊断: 本次 Update 刷出来的怪, 交给宿主激怒 (同 EventSpawnDirector)。</summary>
    public readonly List<Character> SpawnedThisTickList = new List<Character>();

    /// <summary>诊断: 累计刷了多少只。</summary>
    public int Spawned { get; private set; }

    /// <summary>诊断: 累计沿途刷了多少只 (不算在 <see cref="Spawned"/> 里)。</summary>
    public int RoadSpawned { get; private set; }

    /// <summary>
    /// 赶路 / 回交的路上多久刷一小撮。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "前往任务点的路上也应该刷僵尸。"
    ///
    /// 上一版只在事件区域里刷 —— 于是赶路那一两百单位是空街, 而且原来"怪都刷在
    /// 基地附近"那个病一修, 地图上除了开局摆好的那批就再没有怪了。
    ///
    /// **节奏是我们定的** (原厂那条强度曲线拿不到, 见 <see cref="WaveSeconds"/>)。
    /// 12 秒大约是"走过一条街"的时间。
    /// </summary>
    public float RoadWaveSeconds { get; set; } = 9f;

    /// <summary>
    /// 沿途一撮几只 (感染等级 1、简单档的底数, 再乘 <see cref="Scale"/>)。
    /// 玩家实测 "路上刷新的僵尸数量很少, 感染等级为 1" —— 3 只/12 秒 -&gt; 5 只/9 秒。
    /// </summary>
    public int RoadPackSize { get; set; } = 5;

    /// <summary>沿途刷的怪同时最多留几只活的 (免得一路走一路攒)。</summary>
    public int RoadLiveCap { get; set; } = 20;

    /// <summary>
    /// 沿途刷怪位离玩家最近多远。**看不见的地方才刷** —— 55 是僵尸的仇恨半径,
    /// 在那里面冒出来等于脸上刷怪; 再加上视野判定 (刷怪位和玩家之间有墙) 就更稳。
    /// </summary>
    public float RoadMinDistance { get; set; } = 70f;

    /// <summary>沿途刷怪位离玩家最远多远 —— 太远等于没刷, 玩家根本走不到。</summary>
    public float RoadMaxDistance { get; set; } = 200f;

    /// <summary>
    /// 退而用"看得见的刷怪位"时, 比 <see cref="RoadMinDistance"/> 再远多少。
    /// **我们定的**: 100 单位外爬出来的怪, 在 95 单位的相机视野边上。
    /// </summary>
    public float RoadVisibleExtra { get; set; } = 30f;

    /// <summary>
    /// 离基地 (卡车) 多近的刷怪位不用 —— 基地是安全区, 原来那个病就是怪堆在
    /// 出生点。150 罩住基地那一块 (实测卡车到基地边缘的 Event_Trigger 区约 200 见方)。
    /// </summary>
    public float BaseSafeRadius { get; set; } = 150f;

    /// <summary>宿主读完打日志再清。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>
    /// 一局跑完 (夜袭收尾了)。宿主接这个发结算和 <c>TeamFinished</c> ——
    /// 和 <see cref="ScoutFinaleDirector.OnMissionComplete"/> 同一个形状:
    /// 战斗服自己不碰账号库。
    /// </summary>
    public Action<GameManager> OnRunComplete { get; set; }

    private CrossroadEventCatalog.Mission Current =>
        MissionsDone < _missions.Count ? _missions[MissionsDone] : null;

    /// <summary>当前任务 (没有时 null)。诊断/测试用。</summary>
    public CrossroadEventCatalog.Mission CurrentMission => Current;

    /// <summary>当前任务是哪一类 (没有任务时是 <see cref="RaidEventType.None"/>)。诊断/测试用。</summary>
    public RaidEventType CurrentObjective => Current?.Choice.Type ?? RaidEventType.None;

    /// <summary>当前任务要不要玩家按交互键读条 (false = 清场型)。</summary>
    public bool CurrentNeedsInteraction => Current?.Choice.Interaction.HasValue == true;

    /// <summary>当前该去的地方 —— 和小地图标记指的是同一个点。</summary>
    public Vector2 CurrentTarget => Step switch
    {
        Stage.Briefing or Stage.Debrief => QuestPoint,
        Stage.Travel or Stage.Active => Current?.Area.Position ?? QuestPoint,
        Stage.Finale => TruckPoint,
        _ => Vector2.Zero,
    };

    private Vector2 _questGiver;
    private Vector2 _truck;
    private bool _hasQuestGiver;
    private bool _hasTruck;
    private bool _planned;

    /// <summary>
    /// 读条型目标的执行者 —— 每个任务一个新的。
    ///
    /// ## 为什么不自己写读条
    ///
    /// 第一版自己写了个"按了就涨"的读条, 玩家实测立刻指出:
    /// "占领补给点没有播放摇旗动画和引导读条, 从卡车偷物资也没有实装, 按 X 后
    /// 过一会儿物资箱和成功占领补给点一样掉落而不是偷取, 这两点在 tutorial 和
    /// scout mission 里面已经实装了, 在这里复现一下。"
    ///
    /// Scout 那一套 (<see cref="ScoutObjectiveDirector"/>) 每一条都是实测校准过的:
    /// 起手摇旗 5 秒 (原厂引导 buff 的动画/头顶齿轮/手里那面旗/定身)、
    /// 走出圈只暂停、偷物资全程引导 + 边偷边往身上装、断了停在原地再按接着偷、
    /// 偷的不抛箱子占领才抛。复用而不是再抄一份。
    /// </summary>
    private ScoutObjectiveDirector _runner;

    /// <summary>具名目标 (Boss / 带词条的硬茬) 按原厂时间表出场。见 <see cref="CrossroadTargetSpawner"/>。</summary>
    private readonly CrossroadTargetSpawner _targets = new CrossroadTargetSpawner();

    /// <summary>解救 / 护送平民。见 <see cref="CrossroadCivilians"/>。</summary>
    private readonly CrossroadCivilians _civilians = new CrossroadCivilians();

    /// <summary>补给设施: 门口路障 + 屋里的补给。见 <see cref="CrossroadStash"/>。</summary>
    private readonly CrossroadStash _stash = new CrossroadStash();

    /// <summary>补给设施状态 (测试/诊断)。</summary>
    public CrossroadStash Stash => _stash;

    /// <summary>当前事件的具名目标 (测试/诊断)。</summary>
    public CrossroadTargetSpawner Targets => _targets;

    /// <summary>平民任务的状态 (测试/诊断)。</summary>
    public CrossroadCivilians Civilians => _civilians;

    private Vector2 _escortGoal;
    private bool _hasEscortGoal;

    /// <summary>当前读条型目标的执行者。清场型任务或不在干活时为 null。测试/诊断用。</summary>
    public ScoutObjectiveDirector Runner => _runner;

    private float _waveTimer;
    private float _roadTimer;
    private readonly List<Spawn_EventEntities> _finaleSpawns = new List<Spawn_EventEntities>();
    private readonly List<Character> _roadSpawned = new List<Character>();
    private int _wavesFired;

    private bool _finaleAnnounced;
    private bool _leaving;
    private float _leavingFor;

    // ---- HUD 提示排队 (见 Announce)
    private readonly Queue<(CrossroadsGUIUpdate Update, float After)> _hud = new Queue<(CrossroadsGUIUpdate, float)>();
    private float _hudTimer;
    private RaidEventObjectiveState _shownObjective;
    private bool _hasShownObjective;

    /// <summary>
    /// "NEW MISSION" / "MAIN OBJECTIVE" 横幅放多久 —— 客户端 <c>ShowNewMission/ShowMainObjective</c> 的返回值
    /// (GUI_RaidMissionProgress.cs:237), 原厂的数。
    /// </summary>
    public const float BannerSeconds = 3f;

    /// <summary>
    /// 发出"目标栏换新"之后, 服务端再等多久才真的把目标换掉。**我们定的**: 要让这条可靠消息
    /// 比每帧同步的目标先到客户端 (见 Announce)。客户端收到后本来就要冻结目标栏 1 秒, 0.5 秒在那里面。
    /// </summary>
    public const float ObjectiveLeadSeconds = 0.5f;

    /// <summary>HUD 提示还在排队 / 刚发出去 —— 这段时间目标栏保持上一个目标不动。</summary>
    public bool HoldingObjective => _hud.Count > 0 || _hudTimer > 0f;


    /// <summary>
    /// 每帧一次。
    /// </summary>
    /// <param name="network">
    /// 用来推"新任务 / 任务评分"那些一次性的 HUD 事件 (<see cref="CrossroadsGUIUpdate"/>)。
    /// 目标文本本身不走这里 —— 那是每帧同步的 <c>_0023Mmd</c>。
    /// </param>
    public void Update(GameManager simulation, GameServer network, float deltaTime)
    {
        SpawnedThisTickList.Clear();

        if (simulation?.GameMode is not RaidMission raid || Step == Stage.Over)
        {
            return;
        }

        WatchForWipe(simulation, raid);
        PumpHud(network, deltaTime);

        // 平民在任务结束之后还要跑回基地再收走, 所以不管在哪个阶段都推一下。
        _civilians.Update(simulation, deltaTime);
        DrainCivilianEvents();

        if (!_planned && !Plan(simulation))
        {
            return;
        }

        // ## 阶段换了就在**同一帧**把新阶段也跑一遍
        //
        // 不这样的话目标 UI 永远慢一帧: 比如 RunBriefing 先把"去领任务"写进表,
        // 然后才翻到 Travel —— 那一帧玩家已经领到任务了, 屏幕上还写着"去领任务"。
        // 玩家走到任务发布点和走到事件区域这两件事在真机上常常连着发生 (基地里
        // 那两个点只隔一百多单位), 所以这不是个理论问题。
        //
        // 第二趟起 deltaTime 给 0 —— 时限和读条只该按真实帧长走一次。
        for (int guard = 0; guard < 4; guard++)
        {
            Stage before = Step;

            switch (Step)
            {
                case Stage.Briefing:
                    RunBriefing(simulation, network);
                    break;

                case Stage.Travel:
                    Road(simulation, deltaTime);
                    RunTravel(simulation, network);
                    break;

                case Stage.Active:
                    RunActive(simulation, network, deltaTime);
                    break;

                case Stage.Debrief:
                    Road(simulation, deltaTime);
                    RunDebrief(simulation, network);
                    break;

                case Stage.Finale:
                    RunFinale(simulation, network, raid, deltaTime);
                    break;
            }

            if (Step == before)
            {
                break;
            }

            deltaTime = 0f;
        }
    }

    /// <summary>
    /// **团灭**: 全队都倒了 -&gt; 拨团灭标志, 每人复活时间加罚时; 有人站起来了 -&gt; 标志收回。
    ///
    /// ## 客户端那一半是现成的
    ///
    ///   标志    <c>RaidMission._0023g9</c> (同步字段, SerializeGameMode 第四个 bool)
    ///   表现    <c>GUI_DeathPanel</c> (:258): 死亡面板读秒时看到它 -&gt; <c>ShowWipeGUI</c> 弹
    ///           "+{time} sec (Wipe Penalty)", time = 平衡数据 <c>_0023c7f._002328</c> (原厂 10 秒)
    ///
    /// 标志全树零赋值, 罚时也没人加。现在按原厂的数加: 团灭那一刻每个倒下的人
    /// <c>SetRespawnTime(原来的 + 10)</c> —— 死亡面板的读秒和服务端放行 (RespawnPolicy)
    /// 读的是同一个数, 两边自然对上。
    ///
    /// ⚠️ 十字路口的**基础**复活时间是 <c>RaidMission._0023Bi()</c>, 原厂基类返回 0 (服务端子类覆盖的那个数
    /// 丢了), 所以平时是按下就复活, 团灭才等 10 秒。
    /// </summary>
    private void WatchForWipe(GameManager simulation, RaidMission raid)
    {
        if (_leaving)
        {
            return;     // 结束动画里 Cinematic 自己会把人全复活
        }

        int alive = 0;
        int dead = 0;

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive)
            {
                continue;
            }

            if (player.IsDead)
            {
                dead++;
            }
            else
            {
                alive++;
            }
        }

        if (alive > 0 || dead == 0)
        {
            if (raid._0023g9)
            {
                raid._0023g9 = false;
                Events.Add("十字路口: 有人复活了, 团灭标志收回");
            }

            return;
        }

        if (raid._0023g9)
        {
            return;
        }

        raid._0023g9 = true;
        float penalty = GameManager.BalanceData._0023c7f._002328;

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player != null && player.IsActive && player.IsDead)
            {
                player.SetRespawnTime(player.GetRespawnTime() + penalty);
            }
        }

        Wipes++;
        Events.Add($"十字路口: 团灭 ({dead} 人全倒), 每人复活时间 +{penalty:F0} 秒");
    }

    /// <summary>团灭了几次 (测试/诊断)。</summary>
    public int Wipes { get; private set; }

    /// <summary>
    /// 开局摆一次: 扫地图、排任务、记下任务发布点和卡车的位置。
    /// </summary>
    /// <returns>false = 这张图排不出任务 (不是十字路口的图, 或者没有事件位)。</returns>
    private bool Plan(GameManager simulation)
    {
        if (simulation.MapObjects == null || simulation.ActivePlayerList.Count == 0)
        {
            return false;   // 等人进来再排 —— 摇号要用模拟层的 RNG, 也要有人来判距离
        }

        _planned = true;

        foreach (MapObject mapObject in simulation.MapObjects)
        {
            if (mapObject is QuestGiverSpawnPoint quest)
            {
                _questGiver = quest.Position;
                _hasQuestGiver = true;
            }
            else if (mapObject is TruckerSpawnPoint truck)
            {
                _truck = truck.Position;
                _hasTruck = true;
            }
            else if (mapObject is Trigger_EscortGoal escort)
            {
                // 护送终点 —— 原厂摆在基地里任务发布点那个位置 (实测 map26: (72,-74))。
                _escortGoal = escort.Position;
                _hasEscortGoal = true;
            }
        }

        _missions.AddRange(_catalog.Plan(simulation, Difficulty, MissionCount, OnlyTypes));

        for (int i = 0; i < _catalog.Notes.Count; i++)
        {
            Events.Add(_catalog.Notes[i]);
        }

        _catalog.Notes.Clear();

        if (_missions.Count == 0)
        {
            Events.Add("十字路口: 这张图排不出任务 (没有能办的事件位), 局内流程不开");
            Step = Stage.Over;
            return false;
        }

        foreach (CrossroadEventCatalog.Mission mission in _missions)
        {
            Events.Add($"十字路口任务 {mission.Choice.Type}/{mission.Choice.VisualId} "
                       + $"({mission.Choice.Name}) @ 区域 {mission.Area.Id} "
                       + $"变体 \"{mission.Variation}\" 时限 {mission.Seconds:F0} 秒 "
                       + $"刷怪位 {mission.Area.Spawns.Count} 个 怪种 {mission.Mix.Count} 类");
        }

        Step = Stage.Briefing;
        return true;
    }

    // ------------------------------------------------------------------ 各阶段

    private void RunBriefing(GameManager simulation, GameServer network)
    {
        ShowObjective(simulation, RaidEventType.FetchNewQuest, 0, ObjectiveType.MoveTo);
        Mark(simulation, QuestPoint);

        if (NearestPlayerDistance(simulation, QuestPoint) > QuestRadius)
        {
            return;
        }

        Step = Stage.Travel;
        Events.Add($"十字路口: 领到第 {MissionsDone + 1} 个任务 "
                   + $"({Current?.Choice.Type}), 目标区域 {Current?.Area.Id}");
        PlaceScenery(simulation, Current);

        // "NEW MISSION" 横幅放完, 再"新目标"大字 + 目标栏刷新 (见 Announce)
        Announce(network, update => update.AddNewMissionData());
    }

    private void RunTravel(GameManager simulation, GameServer network)
    {
        CrossroadEventCatalog.Mission mission = Current;
        if (mission == null)
        {
            Step = Stage.Finale;
            return;
        }

        ShowObjective(simulation, mission.Choice.Type, mission.Choice.VisualId,
            ObjectiveType.MoveTo);
        Mark(simulation, mission.Area.Position);

        if (NearestPlayerDistance(simulation, mission.Area.Position) > ArriveRadius)
        {
            return;
        }

        Step = Stage.Active;
        SecondsLeft = mission.Seconds;
        Progress = 0f;
        _waveTimer = 0f;
        _wavesFired = 0;
        _spawned.Clear();

        PlaceScenery(simulation, mission);
        _targets.Arm(mission, mission.Area.Spawns);

        if (mission.Choice.Type == RaidEventType.FreeCivilians)
        {
            _civilians.CocoonCount = mission.CivilianCount;     // 原厂人数, 见 CivilianSeconds
            _civilians.ArmFree(simulation, mission.Area.Spawns, mission.Area.Position, EscortGoal);
        }
        else if (mission.Choice.Type == RaidEventType.EscortCivilian)
        {
            _civilians.EscortCount = mission.CivilianCount;
            _civilians.ArmEscort(simulation, mission.Area.Spawns, mission.Area.Position, EscortGoal);
        }

        DrainCivilianEvents();

        if (_targets.Total > 0)
        {
            Events.Add($"十字路口: 这个事件有具名目标 {_targets.Total} 只 (变体 \"{mission.Variation}\")");
        }

        Events.Add($"十字路口: 到达区域 {mission.Area.Id}, 开始 {mission.Choice.Type} "
                   + $"(时限 {mission.Seconds:F0} 秒"
                   + (mission.Choice.Interaction.HasValue
                       ? $", 交互 {mission.Choice.Interaction.Value})"
                       : $", {KindOf(mission.Choice.Type)})"));

        Announce(network, banner: null);
    }

    private void RunActive(GameManager simulation, GameServer network, float deltaTime)
    {
        CrossroadEventCatalog.Mission mission = Current;
        if (mission == null)
        {
            Step = Stage.Finale;
            return;
        }

        // 解救平民: 时限管的是茧 ("before they turn") —— 到点没打开的茧里的人变僵尸,
        // 任务随即按救出人数结算, 不走下面那条"超时失败"。
        bool freeing = mission.Choice.Type == RaidEventType.FreeCivilians;
        SecondsLeft = Math.Max(0f, SecondsLeft - deltaTime);

        if (freeing && SecondsLeft <= 0f && !_civilians.CocoonsDone)
        {
            _civilians.ExpireCocoons(simulation);
            SpawnedThisTickList.AddRange(_civilians.TurnedThisTick);
            _civilians.TurnedThisTick.Clear();
            DrainCivilianEvents();
        }

        Waves(simulation, mission, deltaTime);

        // 具名目标按时间表出场; 护送时冲着平民来
        bool escort = mission.Choice.Type == RaidEventType.EscortCivilian;
        _targets.Update(simulation, deltaTime, escort ? _civilians.EscortAnchor : null);
        SpawnedThisTickList.AddRange(_targets.SpawnedThisTick);
        DrainTargetEvents();

        if (escort)
        {
            Mark(simulation, EscortGoal);       // 护送: 小地图指回基地。解救就在原地打茧, 不指
        }

        if (mission.Choice.Type == RaidEventType.FreeCivilians
            || mission.Choice.Type == RaidEventType.EscortCivilian)
        {
            if (_civilians.Failed)
            {
                Finish(simulation, network, mission, failed: true);
                return;
            }

            Progress = _civilians.Completed ? 1f : Math.Min(0.99f, _civilians.Progress);
        }
        else if (mission.Choice.Type == RaidEventType.SupplyFacility && _stash.Armed)
        {
            // 补给设施: 打破门口路障 -> 进屋把补给捡完 = 完成 (见 CrossroadStash)
            _stash.Update(simulation);
            DrainStashEvents();
            Progress = _stash.Opened ? 1f : Math.Min(0.99f, _stash.Progress);
        }
        else if (mission.Choice.Type == RaidEventType.KillEnemy && _targets.Total > 0)
        {
            // 杀具名目标: 全部出场而且全死 = 完成
            Progress = _targets.AllDown
                ? 1f
                : Math.Min(0.99f, (float)_targets.Killed / _targets.Total);
        }
        else if (_runner != null)
        {
            // 执行者自己推读条、刷警报波次 (这边给了 0 波, 波次归本类)、
            // 偷物资时往身上装、占领完抛箱子。
            _runner.Update(simulation, deltaTime);
            DrainRunnerEvents();
            SpawnedThisTickList.AddRange(_runner.SpawnedThisTickList);
            Progress = _runner.Completed ? 1f : _runner.Progress;
        }
        else
        {
            Progress = ClearProgress();
        }

        // 解救任务全程是自己那条文案 ("Free the civilians ... before they turn!") ——
        // 上一版在茧打开后换成护送那条, 两个任务因此看起来一模一样 (玩家 2026-09-22)。
        ShowObjective(simulation, mission.Choice.Type, mission.Choice.VisualId,
            ObjectiveType.Objective1, mission);

        if (Progress >= 1f)
        {
            Finish(simulation, network, mission, failed: false);
            return;
        }

        if (SecondsLeft <= 0f && !freeing)
        {
            Finish(simulation, network, mission, failed: true);
        }
    }

    private void RunDebrief(GameManager simulation, GameServer network)
    {
        ShowObjective(simulation, RaidEventType.ReturnToQuestGiver, 0, ObjectiveType.MoveTo);
        Mark(simulation, QuestPoint);

        if (NearestPlayerDistance(simulation, QuestPoint) > QuestRadius)
        {
            return;
        }

        if (MissionsDone >= _missions.Count)
        {
            Step = Stage.Finale;
            return;
        }

        Step = Stage.Briefing;
    }

    /// <summary>
    /// 收尾: **车队出发倒计时** —— 倒计时里把物资全装上车, 基地周围大批僵尸来袭; 到点车开走, 结算。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "三个任务完成后, 应该进行倒计时, 提示卡车马上就要开始了, 要在倒计时结束前把物资全部装上车,
    /// 且基地附近会刷新大量僵尸。"
    ///
    /// ## 客户端那一整套是现成的, 缺的是服务端把开关拨上
    ///
    ///   开关    RaidMission 模式数据 <c>_0023l9._0023I7</c> (同步字段, -z/-U7.cs)
    ///   时长    <c>_0023l9._0023M7</c>, 默认 90
    ///   表现    客户端 <c>RaidMission._0023E9</c> 看到 <c>_0023I7</c> 从 false 变 true
    ///           (RaidMission.cs:419) -&gt; <c>ShowEscapeAnnouncement(_0023M7, Vfx_NewQuest_01)</c>
    ///           -&gt; <c>GUI_Announcer</c> 屏幕中间大字倒数 —— 文案
    ///           <c>PCStrings.GUI.Crossroads.HUD.EscapeText</c> = **"Caravan is leaving in {timeleft}..."**,
    ///           带滴答声 (<c>_TickSoundTimer</c>)
    ///   结束    <c>_0023J7</c> = 车开走了: 客户端停掉装车交互 (-7P/-edb.cs:31)、停止瞄准 (AbilityAim.cs:162)
    ///
    /// 这三个字段全树零赋值 (服务端那一半)。
    ///
    /// ## 基地来袭
    ///
    ///   - 原厂夜袭 <see cref="Event_Assault"/> (30 只行尸) 由 <see cref="EventSpawnDirector"/>
    ///     按原厂数据刷, 闸门在这一幕打开 (见 LidgrenHost 里的 Allow)
    ///   - 原厂 <c>End Event Looters</c> 的 5 只 Looter 按时间表出场 (25/50/75... 秒)
    ///   - 不再持续补怪, 只有上面那一波; 夜袭期间全图僵尸都激怒、改奔跑 (<see cref="EngageEveryZombie"/>)
    ///
    /// 倒计时里目标 UI 写 EndEvent / Objective1 + 剩余时间, 小地图指卡车。
    /// </summary>
    private void RunFinale(GameManager simulation, GameServer network, RaidMission raid, float deltaTime)
    {
        if (_leaving)
        {
            RunLeaving(simulation, raid, deltaTime);
            return;
        }

        if (!_finaleAnnounced)
        {
            StartFinale(simulation, network, raid);
        }

        FinaleLeft = Math.Max(0f, FinaleLeft - deltaTime);

        ShowObjective(simulation, RaidEventType.EndEvent, 0, ObjectiveType.Objective1);
        List<RaidEventObjectiveState> states = Objectives(simulation);
        if (states != null && states.Count > 0 && !HoldingObjective)
        {
            RaidEventObjectiveState state = states[0];
            state.TimeLeft.SetValue(FinaleLeft);
            states[0] = state;
        }

        Mark(simulation, TruckPoint);

        _targets.Update(simulation, deltaTime, TruckPoint);
        SpawnedThisTickList.AddRange(_targets.SpawnedThisTick);
        DrainTargetEvents();
        EngageEveryZombie(simulation);

        if (FinaleLeft > 0f)
        {
            return;
        }

        // 车开走了: 停掉装车交互, 放结束动画, 动画放完再结算 (RunLeaving)
        BeginLeaving(raid);

        Events.Add("十字路口: 倒计时结束, 车队出发 —— 放结束动画");
    }

    /// <summary>
    /// 夜袭 —— 基地的太阳能炮塔**威力降低** (射得慢), 不是停火。
    ///
    /// ## 玩家实测
    ///
    /// 2026-09-22 先报"crossroad 夜袭时机枪炮塔没有停电停止工作", 当时做成了整个停火;
    /// 2026-09-23 纠正: "夜袭时基地炮塔应该是降低伤害而不是彻底停止运作, 这点在 starter mission
    /// 里面有讲到。" 大厅指引原文: "Solar powered turrets are less powerful during night"
    /// (PCStrings.GUI.ScoutGuideText.SolarPoweredTurrets); 炮塔悬停说明也是 "Powered by sunlight"。
    ///
    /// ## 倍率照原厂拾荒那一支
    ///
    /// 原厂炮塔自己就有"夜里射得慢"的逻辑 (-EK/-Zuc.cs:293-298):
    ///
    ///     float num = _0023SJ;  if (!flag) num = _0023SJ * _0023Uuc;   // _0023Uuc = 1.5
    ///
    /// 但 flag 只在"拾荒 + 新版终局 + 已入夜"时为 false, 十字路口判不到。这里照同一个倍率把
    /// 间隔拉长 (字段由 sanitizer 放开, 见那里的说明) —— 效果就是射速降到白天的 2/3。
    ///
    /// **头顶那个"需要电力"的图标 (Vfx_Turret_OutOfPower_01) 十字路口出不来**: 客户端只在同一个
    /// 拾荒判断里创建它 (:252), 服务端没有办法替它显示。
    ///
    /// 不用恢复: 夜袭之后这一局就结束了。
    /// </summary>
    /// <returns>放慢了几座。</returns>
    private static int SlowBaseTurretsForNight(GameManager simulation)
    {
        int slowed = 0;

        foreach (Entity entity in simulation.GetActiveObjectsFromCategory(ObjCat.Stationary))
        {
            if (entity == null || !entity.IsSpawnTurret)
            {
                continue;
            }

            _0023EK._0023Zuc turret = entity.AsSpawnTurret;
            turret._0023SJ = turret._0023yJ * turret._0023Uuc;
            slowed++;
        }

        return slowed;
    }

    /// <summary>
    /// 结束动画 (众人跳上卡车、Bauer 喊出发、黑屏), 放完才弹结算面板。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "三个任务完成后的倒计时结束后播放结束动画, 然后才弹出结算面板。"
    ///
    /// ## 原厂的过场是现成的, 驱动它的那一行在服务端
    ///
    /// <c>GameMode.CinematicObject</c> (<c>Cinematic</c>, ConductorGameLogic.GameModes/Cinematic.cs) 分两半:
    ///
    ///   服务端 <c>_0023aj(dt, gm)</c>   按时间推进: 1 秒 -&gt; 2 秒后把人都复活、清 buff、送到
    ///            <c>CinematicTeleportPoint</c>、上无敌/锁操作 -&gt; 1 秒后挂 <c>_00237cb</c>
    ///            (HideModel + <c>Vfx_JumpIntoTruck_01</c>, 就是"跳上车") -&gt; ... 共约 12 秒,
    ///            末了 <c>_00234i = true</c>
    ///   客户端 <c>_00238i</c>            读同步过来的阶段 <c>_00235i</c>: 关 HUD + 黑屏 -&gt; 亮 -&gt;
    ///            <c>Vfx_Crossroads_Leaving_Bauer</c> 语音 -&gt; 黑屏 -&gt; 结束音
    ///            (RaidMission._0023E9 每帧调它, RaidMission.cs:437)
    ///
    /// <c>_0023aj</c> 全树零调用者 —— 那是服务端的一半, 丢了。它从不自己进第一阶段
    /// (<c>_0023Gi</c> 是外部置的), 所以这里置一次, 然后每帧推。
    ///
    /// 过场期间不再刷怪; 超过 <see cref="LeavingTimeout"/> 还没放完就直接结算, 别把人卡在黑屏里。
    /// </summary>
    private void RunLeaving(GameManager simulation, RaidMission raid, float deltaTime)
    {
        _leavingFor += deltaTime;

        // ## 每帧碰一下模式数据
        //
        // 客户端推过场表现 (<c>Cinematic._00238i</c>: 黑屏/亮起/语音) 的那一行在 <c>RaidMission._0023E9</c>
        // 末尾 (RaidMission.cs:437), 而 _0023E9 只在**模式数据同步器反序列化**时才跑 (:106)。
        // 过场各阶段本身存在 GameMode 对象上, 不在模式数据里 —— 模式数据要是那几秒没变, 客户端就可能
        // 看不到"该黑屏了", 直接看到人被拖过去 (玩家实测 2026-09-13 双人)。
        // 倒计时时长 _0023M7 只在"倒计时开关刚拨上"那一下被读 (:419), 开关早就是开着的, 拿它来回抖一点
        // 不会有任何表现, 只保证同步器每帧都在发。
        raid._0023l9._0023M7 = FinaleSeconds + (_leavingNudge ? 0.01f : 0f);
        _leavingNudge = !_leavingNudge;

        // ## 黑透了再开始推
        //
        // 原厂过场第 3 秒把人抛到 CinematicTeleportPoint (0.5 秒的抛物线), 客户端 1 秒黑屏。这里再往后
        // 压 <see cref="LeavingFadeLead"/> 秒才开始推, 网络晚到一点也保证抛的时候已经全黑。
        if (_leavingFor >= LeavingFadeLead)
        {
            raid.CinematicObject._0023aj(deltaTime, simulation);
        }

        bool done = raid.CinematicObject._00234i;
        if (!done && _leavingFor < LeavingTimeout + LeavingFadeLead)
        {
            return;
        }

        Step = Stage.Over;

        Events.Add(done
            ? $"十字路口: 结束动画放完 ({_leavingFor:F1} 秒) —— 一局结束, 结算"
            : $"十字路口: 结束动画 {LeavingTimeout:F0} 秒没放完, 直接结算");
        OnRunComplete?.Invoke(simulation);
    }

    /// <summary>
    /// 认输投票通过 (<see cref="ConcedeVote"/>): 手上的任务收掉、不再刷怪, 直接放结束动画
    /// (<c>_00239i = true</c> 让客户端放 Bauer 的 <c>Vfx_Crossroads_Concede_Bauer</c> 那句, 不是 "出发"),
    /// 放完照常结算 —— 奖励那边看 <c>GameMode.WasConceded</c> 记成 Conceded。
    /// </summary>
    public void Concede(GameManager simulation)
    {
        if (simulation?.GameMode is not RaidMission raid || Step == Stage.Over || _leaving)
        {
            return;
        }

        DisarmRunner(simulation);
        _stash.Disarm(simulation);
        _civilians.Disarm();

        raid._00239i = true;

        Step = Stage.Finale;
        _finaleAnnounced = true;
        BeginLeaving(raid);

        Events.Add($"十字路口: 认输通过 (做完 {MissionsDone}/{_missions.Count} 个任务) —— 放结束动画后结算");
    }

    /// <summary>
    /// 车开走 (或认输通过): 放结束动画的第一步。
    ///
    /// ## GameOver 必须在这一刻就置上 —— 不然黑屏出不来
    ///
    /// 玩家实测 (2026-09-14, 上一版每帧抖模式数据之后): "成功撤离后还是没有出现黑屏把玩家角色拖走这一段挡住。"
    ///
    /// 客户端 <c>UnityClient.Update</c> (UnityClient.cs:1230) **每帧**把黑屏的透明度重设成"连上服务器后
    /// 淡入完"的值 (0), 只有三种情况跳过, 其中一种是 <c>GameMode.AsRaidMission.GameOver</c>。
    /// 过场的 <c>Fade(FADE_TO_BLACK)</c> 刚起头, 下一帧就被抹回全透明 —— 服务端再怎么推阶段也白搭。
    /// 以前 GameOver 等动画放完才置, 整段过场都在被抹。
    ///
    /// <c>GameOver</c> 就是模式数据的 <c>_0023L7</c> (RaidMission.cs:186), 随同步器下发; 全树再没有
    /// 别处读它 (服务端结算走的是 <see cref="Stage.Over"/> + <see cref="OnRunComplete"/>), 提前置不影响收尾。
    /// </summary>
    private void BeginLeaving(RaidMission raid)
    {
        raid.GameOver = true;
        raid._0023l9._0023J7 = true;
        raid._0023p9 = false;       // 来袭结束, 音乐退回入夜那一档
        raid.CinematicObject._00235i = _0023bd._0023Ni._0023Gi;
        _leaving = true;
        _leavingFor = 0f;
    }

    /// <summary>
    /// 结束动画最长等多久。原厂过场各段加起来 1+2+1+1+2.5+2+2.5 = 12 秒, 给 20 秒兜底。**我们定的。**
    /// </summary>
    public float LeavingTimeout { get; set; } = 20f;

    /// <summary>正在放结束动画 (测试/诊断)。</summary>
    public bool Leaving => _leaving;

    /// <summary>黑屏开始后多久才开始推原厂过场。**我们定的。**</summary>
    public float LeavingFadeLead { get; set; } = 1.5f;

    private bool _leavingNudge;

    private void StartFinale(GameManager simulation, GameServer network, RaidMission raid)
    {
        _finaleAnnounced = true;
        FinaleLeft = FinaleSeconds;

        // 入夜 —— 客户端收到状态变化就过渡昼夜、切音乐 (RaidMission._0023E9 -> TransitionDayCycle)
        raid._0023F9(DayAndNightState.NIGHT);

        // 拨开关: 客户端看到 _0023I7 翻成 true 就放 "Caravan is leaving in {timeleft}..." 倒数
        raid._0023l9._0023M7 = FinaleSeconds;
        raid._0023l9._0023I7 = true;

        // 夜袭进行中 —— 客户端音乐状态机 (RaidMission.UpdateGameMode :342-356) 靠它从"入夜"切到
        // "来袭" (音乐强度 9), 结束时切回。同步字段 (SerializeGameMode 第一个 bool), 全树零赋值。
        raid._0023p9 = true;

        PickFinaleSpawns(simulation);

        CrossroadEventCatalog.Mission finale = _catalog.FinaleMission(simulation, Difficulty,
            TruckPoint, _finaleSpawns);
        _targets.Arm(finale, _finaleSpawns);

        _engaged.Clear();
        RalliedAtFinale = 0;
        EngageEveryZombie(simulation);

        int turrets = SlowBaseTurretsForNight(simulation);

        Events.Add($"十字路口: 任务做完 ({MissionsWon}/{_missions.Count} 拿到奖牌), 入夜, "
                   + $"车队 {FinaleSeconds:F0} 秒后出发 —— 原厂夜袭刷怪位 {_finaleSpawns.Count} 个, "
                   + $"具名目标 {_targets.Total} 只, 全图 {RalliedAtFinale} 只僵尸往基地来, 倍率 x{Scale:F2}, "
                   + $"基地炮塔 {turrets} 座入夜射速降到 2/3 (太阳能, 原厂 ×1.5 间隔)");

        Announce(network, update => update.AddMainObjectiveData());
    }

    /// <summary>
    /// 夜袭里每一帧: 场上所有活着的僵尸 —— 开场就在的、夜袭刷出来的、别处冒出来的 —— 都
    /// (1) 交给宿主激怒 (<see cref="SpawnedThisTickList"/> -&gt; <c>ZombieBrain.Provoke</c>), (2) 切成奔跑。
    ///
    /// ## 激怒: 不靠仇恨半径
    ///
    /// 玩家实测 (2026-09-14): "交最后一个任务后……全图在这局游戏中刷出的僵尸都会往基地涌", 随后
    /// "夜袭时刷的远的怪物会站在原地不动, 改为夜袭时的怪物自动获得玩家仇恨, 不靠原来的仇恨范围"。
    /// 激怒的僵尸无视仇恨半径和视线直接追玩家。上一版只在开场叫一次, 之后刷出来的靠各自的刷怪器激怒 ——
    /// 现在夜袭期间每帧扫一遍, 谁漏了都补上 (激怒是幂等的, 这里只交新面孔)。
    /// "站着不动"的另一半是寻路射线漏了一类挡路几何, 修在 <see cref="ZombiePathing"/>。
    ///
    /// ## 奔跑: 原厂的冲刺移动模式
    ///
    /// 玩家: "夜袭时所有僵尸变为奔跑而不是行走"。僵尸基类 <c>_0023iO</c> 有三档移动模式 (-Mk/-iO.cs):
    /// <c>_0023dO</c> 走 / <c>_0023eO</c> 冲刺 / <c>_0023fO</c> 冲锋, 冲刺把 <c>IsSprinting</c> 置真、加速度/摩擦/
    /// 目标速度换成该类的冲刺那组 (行尸 <c>_0023JGc</c>: 走 7、冲刺 20)。<c>IsSprinting</c> 随 <c>SerializeNPC</c>
    /// 下发, 客户端 <c>ZombieModel.Update</c> 拿它设动画参数 "Chasing" —— 拆资源看行尸的动画机:
    /// Chasing 0 = Walk01..06 (蹒跚), 1 = NEW_Chase01.. (追人跑)。服务端从来没人调过这三个, 所以一直是走路动画。
    /// 置完标记同步脏, 下一帧就发 (<c>_0023eO</c> 自己那句脏标记写在置真之后, 永远不触发)。
    /// </summary>
    private void EngageEveryZombie(GameManager simulation)
    {
        List<GameObjectBase> objects = simulation.ActiveGameObjectList;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not Character zombie || !zombie.IsZombie || !zombie.IsActive || zombie.IsDead)
            {
                continue;
            }

            if (zombie is _0023Mk._0023iO walker && !walker.IsSprinting)
            {
                walker._0023eO();
                simulation.NetworkManager?.GetSyncHelper(walker.SyncHelperIndex)?._00234Ce();
            }

            if (_engaged.Add((zombie.IndexGlobal, zombie.NumberOfGlobalCacheUses)))
            {
                SpawnedThisTickList.Add(zombie);
                RalliedAtFinale++;
            }
        }
    }

    /// <summary>
    /// 收尾的刷怪位 = **原厂夜袭** (<see cref="Event_Assault"/>) 自己的那批点 (ID 和夜袭相同)。
    ///
    /// ## 回归原版
    ///
    /// 玩家 (2026-09-13, 看过原版视频): "夜袭的僵尸刷新回归原版。"
    ///
    /// 夜袭的僵尸本身由 <see cref="EventSpawnDirector"/> 按原厂 <c>Event_Assault</c> 的数据刷
    /// (map26/map16: 30 只行尸, 18 个点, 全在基地里、大多是钻地出场), 闸门在收尾这一幕打开
    /// (LidgrenHost 的 <c>_eventSpawns.Allow</c>)。这边只让原厂 <c>End Event Looters</c> 也从这批点出来。
    ///
    /// 上一版 (同日) 自己加的"基地外圈每 6 秒 10 只 + 沿网格行军"已经删了。
    /// </summary>
    private void PickFinaleSpawns(GameManager simulation)
    {
        _finaleSpawns.Clear();

        HashSet<int> assaultIds = new HashSet<int>(
            simulation.MapObjects.OfType<Event_Assault>().Select(a => a.ID));

        _finaleSpawns.AddRange(simulation.MapObjects.OfType<Spawn_EventEntities>()
            .Where(s => assaultIds.Contains(s.ID)));

        if (_finaleSpawns.Count == 0)
        {
            // 这张图没有夜袭点 —— Looter 退回卡车 260 以内的刷怪位, 免得一只都出不来
            _finaleSpawns.AddRange(simulation.MapObjects.OfType<Spawn_EventEntities>()
                .Where(s => Vector2.Distance(s.Position, TruckPoint) is >= 60f and <= 260f));
            Events.Add("十字路口收尾: 这张图没有原厂夜袭刷怪位, Looter 退回卡车 260 以内");
        }
    }

    // ------------------------------------------------------------------ 干活那一段

    /// <summary>
    /// 按刷怪组分波补怪, 刷在**这个区域自己的**摆位点上。
    ///
    /// 摆位点带着原厂的出场动画 (挖地而出那些), 所以直接把它的
    /// <c>SpawnState</c> / <c>Direction</c> 递给 <see cref="ZombieFactory"/>。
    /// </summary>
    private void Waves(GameManager simulation, CrossroadEventCatalog.Mission mission,
                       float deltaTime)
    {
        _waveTimer -= deltaTime;

        if (_wavesFired > 0 && _waveTimer > 0f)
        {
            return;
        }

        // 这个变体自己那一档 (原厂曲线名 -> 四个数), 见 SpawnIntensity
        SpawnIntensity.Profile intensity = SpawnIntensity.For(mission.Curve);

        float seconds = WaveSeconds ?? intensity.Seconds;
        float budget = WaveBudget ?? intensity.Budget;
        int liveCap = LiveCap ?? intensity.LiveCap;
        int maxPerWave = MaxPerWave ?? intensity.MaxPerWave;

        if (mission.Area.Spawns.Count == 0 || Live() >= liveCap)
        {
            return;
        }

        _waveTimer = seconds;
        _wavesFired++;

        List<Type> batch = mission.Draw(simulation, budget * Scale,
            Math.Min((int)Math.Round(maxPerWave * Scale),
                (int)Math.Round(liveCap * Scale) - Live()));

        int placed = 0;

        foreach (Type type in batch)
        {
            // 优先挑这种怪会那个出场动画的点 (特殊怪大多只会"跃出"), 见 ZombieFactory.PreferAnimated
            IReadOnlyList<Spawn_EventEntities> points = ZombieFactory.PreferAnimated(type, mission.Area.Spawns);
            Spawn_EventEntities at = points[simulation.Random._0023Otc(0, points.Count)];

            Character zombie = ZombieFactory.Spawn(simulation, type, at.Position,
                at.SpawnState, at.Direction);

            if (zombie == null)
            {
                continue;
            }

            _spawned.Add(zombie);
            SpawnedThisTickList.Add(zombie);
            placed++;
        }

        Spawned += placed;

        if (placed > 0)
        {
            Events.Add($"十字路口区域 {mission.Area.Id} 第 {_wavesFired} 波: "
                       + $"{placed} 只 (场上 {Live()} 只)");
        }
    }

    /// <summary>
    /// 沿途刷怪: 在玩家**前方**、看不见的刷怪位上放一小撮, 不激怒。
    ///
    /// ## 挑哪几个点
    ///
    ///   - 离最近的玩家 <see cref="RoadMinDistance"/>~<see cref="RoadMaxDistance"/>
    ///   - **比玩家更靠近目的地** —— 刷在去的路上, 不是刷在身后
    ///   - 玩家看不见 (原厂那句几何射线, 同 <see cref="ZombieBrain.CanSee"/>)
    ///   - 不在目的地的事件区域里 (那批留给事件本身)、不在基地安全区里
    ///
    /// ## 为什么不激怒
    ///
    /// 事件刷的怪是冲着玩家来的, 所以激怒; 路上的是"街上本来就有的", 按正常的
    /// 视野+仇恨半径来 —— 玩家走过去撞上才打, 绕开也行。激怒的话一撮怪隔着两条街
    /// 冲过来, 那就不是"路上有怪"而是"被追着跑"了。
    /// </summary>
    private void Road(GameManager simulation, float deltaTime)
    {
        _roadTimer -= deltaTime;
        if (_roadTimer > 0f || deltaTime <= 0f)
        {
            return;
        }

        // 感染等级 / 难度档越高, 来得越勤、一撮越多 (见 Scale)
        _roadTimer = Math.Max(4f, RoadWaveSeconds / (float)Math.Sqrt(Scale));

        _roadSpawned.RemoveAll(z => z == null || !z.IsActive || z.IsDead);
        int room = Math.Min((int)Math.Round(RoadPackSize * Scale),
            (int)Math.Round(RoadLiveCap * Scale) - _roadSpawned.Count);
        if (room <= 0)
        {
            return;
        }

        Player player = NearestLivingPlayer(simulation, CurrentTarget);
        Type walker = _catalog.WalkerType;
        if (player == null || walker == null)
        {
            return;
        }

        Vector2 destination = CurrentTarget;
        float playerToGoal = Vector2.Distance(player.Position, destination);

        List<Spawn_EventEntities> candidates = new List<Spawn_EventEntities>();
        List<Spawn_EventEntities> inView = new List<Spawn_EventEntities>();

        foreach (Spawn_EventEntities point in _catalog.AllSpawns)
        {
            float toPlayer = Vector2.Distance(point.Position, player.Position);
            if (toPlayer < RoadMinDistance || toPlayer > RoadMaxDistance)
            {
                continue;
            }

            if (Vector2.Distance(point.Position, destination) >= playerToGoal)
            {
                continue;   // 在身后或侧后方
            }

            // 已激活的任务区域只归任务 (见 IsReservedForMissions)
            if (IsReservedForMissions(point) || Vector2.Distance(point.Position, TruckPoint) <= BaseSafeRadius)
            {
                continue;
            }

            // 看不见的优先。开阔的街上前方一个墙后的点都没有 (测试里撞到过) ——
            // 那就退而用看得见但**够远**的: 刷怪位自带挖地/爬出的出场动画,
            // 远处爬出来不算"凭空冒出来"。
            if (Visible(simulation, player.Position, point.Position))
            {
                if (toPlayer >= RoadMinDistance + RoadVisibleExtra)
                {
                    inView.Add(point);
                }

                continue;
            }

            candidates.Add(point);
        }

        if (candidates.Count == 0)
        {
            candidates = inView;
        }

        int placed = 0;

        for (int i = 0; i < room && candidates.Count > 0; i++)
        {
            int index = simulation.Random._0023Otc(0, candidates.Count);
            Spawn_EventEntities at = candidates[index];
            candidates.RemoveAt(index);

            Character zombie = ZombieFactory.Spawn(simulation, walker, at.Position,
                at.SpawnState, at.Direction);

            if (zombie == null)
            {
                continue;
            }

            _roadSpawned.Add(zombie);
            placed++;
        }

        RoadSpawned += placed;

        if (placed > 0)
        {
            Events.Add($"十字路口沿途刷怪: {placed} 只 (路上活着 {_roadSpawned.Count} 只, "
                       + $"去往 ({destination.X:F0},{destination.Y:F0}))");
        }
    }

    /// <summary>
    /// 沿途刷怪不用的刷怪位 (留给任务): 落在**已激活的**任务区域多边形里 (外扩 <see cref="RoadAreaPadding"/>) 的。
    /// 已激活 = 领了这个任务、还没交 (赶路 / 进行中)。
    ///
    /// 玩家实测 (2026-09-15): "路上持续刷的那种僵尸不会在任务区域刷, 非任务区域刷怪和任务区域刷怪要分开。"
    /// 以前只避开"当前目的地中心 <see cref="ArriveRadius"/> 以内", 事件的摆位点散得比那远 ——
    /// 赶路时怪就刷进了目的地, 到了之后和任务波次混在一起。
    ///
    /// 第一版把**所有**事件区域都避开了, 玩家纠正 (同日): "只有已经激活的任务区域不会刷非任务区域僵尸,
    /// 未激活的任务区域还是能正常刷。"
    ///
    /// ## 为什么按多边形而不是按"归哪个事件位"
    ///
    /// 区域多边形是地图数据 (<c>Event_RaidEvent.MainArea</c>), 就是任务区域本身。"归属"是我们为了给波次挑点
    /// 定的 (同一块拼图、离事件位 200 以内), 会一直伸到路上 —— 按归属排除的话 map26 从基地到第一个任务整条路
    /// 一个能用的点都不剩 (测试撞到过)。外扩量是我们定的。
    /// </summary>
    public bool IsReservedForMissions(Spawn_EventEntities point) =>
        (Step == Stage.Travel || Step == Stage.Active) && Current != null
        && _catalog.InsideArea(Current.Area, point.Position, RoadAreaPadding);

    /// <summary>事件区域多边形往外扩多少也不许沿途刷怪。**我们定的。**</summary>
    public float RoadAreaPadding { get; set; } = 40f;

    /// <summary>两点之间有没有挡视线的地图几何 —— 同 <see cref="ZombieBrain.CanSee"/> 那句。</summary>
    private static bool Visible(GameManager simulation, Vector2 from, Vector2 to)
    {
        _00236._0023Cc geometry = simulation.CollisionManager?._0023c8;
        if (geometry == null)
        {
            return false;
        }

        return !geometry._00233b(from, to,
            ConductorGameLogic.Collision.GeometryCollisionFlags._00237jd
            | ConductorGameLogic.Collision.GeometryCollisionFlags._00239jd,
            _00237b: false, _00238b: false);
    }

    private static Player NearestLivingPlayer(GameManager simulation, Vector2 position)
    {
        Player best = null;
        float bestDistance = float.MaxValue;

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            float distance = Vector2.DistanceSquared(player.Position, position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = player;
            }
        }

        return best;
    }

    /// <summary>
    /// 清场型目标的进度: 这一片刷出来的怪打掉了多少。
    ///
    /// 至少刷过一波才算 —— 不然开局那一帧"场上零只怪"就直接算完成了。
    /// </summary>
    private float ClearProgress()
    {
        if (_wavesFired == 0 || _spawned.Count == 0)
        {
            return 0f;
        }

        int dead = _spawned.Count(z => z == null || !z.IsActive || z.IsDead);
        return _wavesFired >= WavesToClear ? (float)dead / _spawned.Count : 0f;
    }

    /// <summary>
    /// 清场型目标要挨过几波才算"这一片清完了"。
    ///
    /// **我们定的。** 只要"当前场上没活怪"就算完的话, 玩家打掉第一波的最后一只
    /// 就结束了 —— 太短。挨过 3 波 (配 <see cref="WaveSeconds"/> 20 秒约 60 秒)
    /// 再看清没清完, 和 180 秒的时限也配得上。
    /// </summary>
    public int WavesToClear { get; set; } = 3;

    /// <summary>
    /// 把这一格的评分写进模式数据的**每任务评分表** <c>_0023l9._0023G7</c>。
    ///
    /// ## 结算面板的星星读的是它, 服务端从来没写
    ///
    /// <c>UI_VictoryScreenBindings</c> -&gt; <c>InitializeCrossroads(RaidMission._0023JGr)</c> -&gt;
    /// <c>StarResults[i] = _0023G7[i] - 1</c> (RaidMission.cs:569-576)。表长 3
    /// (<c>_002398._002388()</c>), 构造时全填 None, 靠 <c>GameModeDataSynchronizer</c>
    /// (PlayerInfoSynchronizer 那一路, <c>_0023P7/_0023R7</c>) 同步 —— 全树除了初始化没有一处写它。
    /// 不写的话结算面板上三颗星永远是空的, 不管任务做得怎样。
    ///
    /// HUD 任务栏那几颗星走的是另一条 (<c>CrossroadsGUIUpdate.AddResultData</c>), 两边都要。
    /// </summary>
    /// <param name="missionNr">第几个任务, 从 1 起。</param>
    private void RecordScore(GameManager simulation, int missionNr, CompleteScore score)
    {
        if (simulation.GameMode is not RaidMission raid || raid._0023l9._0023G7 == null)
        {
            return;
        }

        int index = missionNr - 1;
        if (index >= 0 && index < raid._0023l9._0023G7.Count)
        {
            raid._0023l9._0023G7[index] = score;
        }
    }

    private void Finish(GameManager simulation, GameServer network,
                        CrossroadEventCatalog.Mission mission, bool failed)
    {
        MissionsDone++;

        CompleteScore score = failed ? CompleteScore.Fail : ScoreFor(mission);
        if (!failed)
        {
            MissionsWon++;
        }

        RecordScore(simulation, MissionsDone, score);

        // 箱子/补给不在这里给 —— 执行者自己按 Scout 那条规矩给过了
        // (占领抛箱子, 偷物资边偷边装在身上)。这里只把交互点收走。
        DisarmRunner(simulation);
        _stash.Disarm(simulation);

        // 平民: 失败/超时就连茧带人收走; 成功的让他们自己跑回去 (Update 里到点收)。
        if (failed)
        {
            _civilians.Disarm();
        }

        Events.Add($"十字路口: 任务 {MissionsDone}/{_missions.Count} "
                   + $"{mission.Choice.Type} {(failed ? "超时失败" : "完成")} "
                   + $"(评分 {score}, 剩余 {SecondsLeft:F0} 秒)");

        // 任务栏上那一格变成奖牌/失败。missionNr 从 1 起 —— 客户端拿它减 1 当下标, 只有 3 格 (StarSlots)
        if (MissionsDone <= StarSlots)
        {
            CrossroadsGUIUpdate update = new CrossroadsGUIUpdate();
            update.AddResultData(score, (byte)MissionsDone);
            Enqueue(network, update, after: 0f);
        }

        Progress = 0f;
        SecondsLeft = 0f;
        Step = Stage.Debrief;
    }

    /// <summary>
    /// 奖牌: 按**剩余时间的比例**评。
    ///
    /// 原厂给的是 <c>EventMedalTimeData</c> 的 Gold/Silver/Bronze = 100/50/0,
    /// 和 <c>Start</c> = 180 秒。三个数是 0..100 的刻度而不是秒数 (180 秒的任务
    /// 不可能"金牌 100 秒、银牌 50 秒、铜牌 0 秒"还能排出先后), 所以按比例折算:
    /// 剩下 100% 是金、50% 是银、其余铜。**折算方式是我们定的**, 三个刻度是原厂的。
    /// </summary>
    private CompleteScore ScoreFor(CrossroadEventCatalog.Mission mission)
    {
        if (mission.IsCivilian)
        {
            // 平民任务按人数评 —— 原厂奖牌线就是人数。解救看救出几个 (4/2/1),
            // 护送看送到几个 (3/2/1), 见 CrossroadCivilians.Scored。
            int saved = _civilians.Scored;
            if (saved >= mission.Gold)
            {
                return CompleteScore.Gold;
            }

            if (saved >= mission.Silver)
            {
                return CompleteScore.Silver;
            }

            return saved >= mission.Bronze ? CompleteScore.Bronze : CompleteScore.Fail;
        }

        float left = mission.Seconds <= 0f ? 0f : SecondsLeft / mission.Seconds * 100f;

        if (left >= mission.Gold)
        {
            return CompleteScore.Gold;
        }

        return left >= mission.Silver ? CompleteScore.Silver : CompleteScore.Bronze;
    }

    // ------------------------------------------------------------------ 交互点

    /// <summary>
    /// 玩家按了交互键 (<c>GameMessageType.StartInteraction</c>) —— 转给执行者。
    /// </summary>
    /// <returns>true = 这一下被接受了。</returns>
    public bool StartInteraction(GameManager simulation, ushort pointId, Character who)
    {
        if (Step != Stage.Active || _runner == null)
        {
            return false;
        }

        bool accepted = _runner.StartInteraction(simulation, pointId, who);
        DrainRunnerEvents();
        return accepted;
    }

    /// <summary>
    /// 摆任务的"布景": 交互点 (偷物资的车、限时抢的箱子/直升机、旗子 —— 客户端按交互点画特效) 和补给设施 (房子 + 门)。
    /// 每个任务只摆一次。
    ///
    /// 玩家实测 (2026-09-14): "任务建筑在走到附近才 spawn, 应该提前出现。" 以前在"到达区域"那一刻才摆,
    /// 人走到跟前房子才凭空冒出来。现在领到任务 (Briefing -&gt; Travel) 就摆好; 刷怪、计时、平民/茧仍然等到达才开始。
    /// </summary>
    private void PlaceScenery(GameManager simulation, CrossroadEventCatalog.Mission mission)
    {
        if (mission == null || ReferenceEquals(_sceneryFor, mission))
        {
            return;
        }

        _sceneryFor = mission;
        ArmRunner(simulation, mission);

        if (mission.Choice.Type == RaidEventType.SupplyFacility)
        {
            _stash.Arm(simulation, mission);
            DrainStashEvents();
        }
    }

    /// <summary>布景已经摆过的任务 (<see cref="PlaceScenery"/>)。</summary>
    private CrossroadEventCatalog.Mission _sceneryFor;

    /// <summary>给读条型任务配一个执行者, 在事件位上摆交互点。</summary>
    private void ArmRunner(GameManager simulation, CrossroadEventCatalog.Mission mission)
    {
        _runner = null;

        if (!mission.Choice.Interaction.HasValue)
        {
            return;
        }

        ScoutObjectiveDirector runner = new ScoutObjectiveDirector
        {
            HoldRadius = HoldRadius,
            CaptureRadius = CaptureRadius,
            SecondsToComplete = SecondsToChannel,

            // 波次由本类按时间分 (Waves), 执行者那套按进度门槛的警报波次关掉,
            // 不然同一个区域两套刷怪叠在一起。
            Waves = 0,

            // 一车的量: 任务的金牌线 (原厂 100) —— 奖牌刻度和目标 UI 里
            // "补给 当前/上限" 用的是同一个数, 身上的上限也正好 100。
            // 不能用 RaidMission 的 Settings._0023jj, 那是整局目标 1000。
            TruckLoadOverride = Math.Max(1, (int)mission.Gold),

            OpensLedges = false,
            PointsBackToTruck = false,
        };

        // 小地图那个标记交给执行者 (它读条时会换成闪烁图标), 这边先清掉自己的。
        Markers(simulation)?.Clear();

        if (runner.ArmAt(simulation, mission.Choice.Type, mission.Choice.Interaction.Value,
                mission.Choice.VisualId, mission.Area.Position, mission.Area.Direction) == 0)
        {
            Events.Add("十字路口: 交互点没摆上 (模式不对?), 这个任务退回清场");
            Mark(simulation, mission.Area.Position);
            return;
        }

        _runner = runner;
        DrainRunnerEvents();
    }

    /// <summary>任务结束: 把执行者的交互点从同步表里摘掉。</summary>
    private void DisarmRunner(GameManager simulation)
    {
        if (_runner == null)
        {
            return;
        }

        List<InteractionPoint> points = Points(simulation);
        if (points != null)
        {
            for (int i = points.Count - 1; i >= 0; i--)
            {
                for (int j = 0; j < _runner.Points.Count; j++)
                {
                    if (points[i].ID == _runner.Points[j].ID)
                    {
                        points.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        // 执行者读条时可能换过小地图图标 —— 下一阶段 Mark 会覆盖, 这里先清干净。
        Markers(simulation)?.Clear();

        // 没读完就超时的: 把人身上的引导 buff 也摘掉, 不然人被定在原地。
        _runner.Abort();

        DrainRunnerEvents();
        _runner = null;
    }

    private void DrainRunnerEvents()
    {
        if (_runner == null)
        {
            return;
        }

        for (int i = 0; i < _runner.Events.Count; i++)
        {
            Events.Add("十字路口: " + _runner.Events[i]);
        }

        _runner.Events.Clear();
    }

    // ------------------------------------------------------------------ 目标 UI

    /// <summary>
    /// 填客户端每帧读的那张表。
    ///
    /// 客户端只看 <c>_0023Mmd[0]</c> (UI_ObjectivesCrossroadsBindings.cs:73),
    /// 所以这里始终只放一条 —— 当前目标。
    /// </summary>
    private void ShowObjective(GameManager simulation, RaidEventType type, int visualId,
                               ObjectiveType objective,
                               CrossroadEventCatalog.Mission mission = null)
    {
        List<RaidEventObjectiveState> states = Objectives(simulation);
        if (states == null)
        {
            return;
        }

        RaidEventObjectiveState state = new RaidEventObjectiveState
        {
            EventType = type,
            VisualID = (ushort)visualId,
            Objective = objective,
        };

        if (mission != null)
        {
            state.TimeLeft.SetValue(SecondsLeft);
            state.FirstStarValue.SetValue(mission.Bronze);
            state.SecondStarValue.SetValue(mission.Silver);
            state.ThirdStarValue.SetValue(mission.Gold);

            // 读条型的进度按"金牌刻度"折算 —— 客户端把 ProgressValue 显示成
            // "当前/上限", 上限取的是第三颗星 (ThirdStarValue)。
            if (mission.Choice.Interaction.HasValue)
            {
                state.ProgressUnit.SetValue(ObjectiveProgressUnit.Supplies);
                state.ProgressValue.SetValue((ushort)(Progress * mission.Gold));
            }
            else if (_civilians.Total > 0
                     && (type == RaidEventType.FreeCivilians || type == RaidEventType.EscortCivilian))
            {
                // 平民: "救出 当前/总数"。星线也换成人数 (客户端上限读第三颗星)。
                state.ProgressUnit.SetValue(ObjectiveProgressUnit.Civilians);
                state.ProgressValue.SetValue((ushort)_civilians.Scored);
                // 星线就是原厂的人数线 (1/2/4、1/2/3)
                state.FirstStarValue.SetValue(mission.Bronze);
                state.SecondStarValue.SetValue(mission.Silver);
                state.ThirdStarValue.SetValue(mission.Gold);
            }
        }

        // 提示还在排队: 目标栏保持上一个 (见 Announce)。只冻结"换成别的目标", 同一个目标的倒计时/进度照常刷。
        if (HoldingObjective && _hasShownObjective
            && (state.EventType != _shownObjective.EventType || state.VisualID != _shownObjective.VisualID
                || state.Objective != _shownObjective.Objective))
        {
            state = _shownObjective;
        }
        else
        {
            _shownObjective = state;
            _hasShownObjective = true;
        }

        if (states.Count == 0)
        {
            states.Add(state);
        }
        else
        {
            states[0] = state;
        }
    }

    /// <summary>小地图上指一下当前该去的地方。只留一个标记。</summary>
    private void Mark(GameManager simulation, Vector2 where)
    {
        List<_0023Hmd> markers = Markers(simulation);
        if (markers == null)
        {
            return;
        }

        _0023Hmd marker = new _0023Hmd
        {
            // _0023mmd 在客户端翻成 MiniMapIconType.RaidEvent + RaidEventFlash
            // (GameManagerClient._0023ujd) —— 主线目标那个闪烁。
            _0023Gmd = _0023qmd._0023mmd,
            _0023B = where,
        };

        if (markers.Count == 0)
        {
            markers.Add(marker);
        }
        else
        {
            markers[0] = marker;
        }
    }

    // ------------------------------------------------------------------ 杂活

    /// <summary>
    /// 三张同步表都挂在模式的 <c>_0023gi</c> 上, 通过
    /// <see cref="IRaidEventGameMode"/> 取 —— 这样 <see cref="RaidMission"/> 和
    /// <c>ScoutMission</c> 是同一条路 (那个接口就是为此存在的)。
    ///
    /// 返回的是 struct 的**副本**, 但三个字段是 List 引用, 所以往里加/改都算数。
    /// </summary>
    private static List<InteractionPoint> Points(GameManager simulation) =>
        (simulation.GameMode as IRaidEventGameMode)?._0023ei(null)._0023Kmd;

    private static List<_0023Hmd> Markers(GameManager simulation) =>
        (simulation.GameMode as IRaidEventGameMode)?._0023ei(null)._0023Lmd;

    private static List<RaidEventObjectiveState> Objectives(GameManager simulation) =>
        (simulation.GameMode as IRaidEventGameMode)?._0023ei(null)._0023Mmd;

    /// <summary>任务发布点。没有的话退回卡车, 再没有退回原点。</summary>
    private Vector2 QuestPoint =>
        _hasQuestGiver ? _questGiver : (_hasTruck ? _truck : Vector2.Zero);

    /// <summary>卡车。没有的话退回任务发布点。</summary>
    private Vector2 TruckPoint =>
        _hasTruck ? _truck : (_hasQuestGiver ? _questGiver : Vector2.Zero);

    /// <summary>护送终点。没有 Trigger_EscortGoal 就退回任务发布点。</summary>
    private Vector2 EscortGoal => _hasEscortGoal ? _escortGoal : QuestPoint;

    /// <summary>日志里怎么称呼这类非读条的事件。</summary>
    private static string KindOf(RaidEventType type) => type switch
    {
        RaidEventType.KillEnemy => "杀具名目标",
        RaidEventType.FreeCivilians => "打碎 Goo 茧救平民, 带回基地",
        RaidEventType.SupplyFacility => "打破门口路障, 进屋捡补给",
        RaidEventType.EscortCivilian => "护送平民回基地",
        _ => "清场",
    };

    private void DrainStashEvents()
    {
        for (int i = 0; i < _stash.Events.Count; i++)
        {
            Events.Add(_stash.Events[i]);
        }

        _stash.Events.Clear();
    }

    private void DrainTargetEvents()
    {
        for (int i = 0; i < _targets.Events.Count; i++)
        {
            Events.Add(_targets.Events[i]);
        }

        _targets.Events.Clear();
    }

    private void DrainCivilianEvents()
    {
        for (int i = 0; i < _civilians.Events.Count; i++)
        {
            Events.Add(_civilians.Events[i]);
        }

        _civilians.Events.Clear();
    }

    /// <summary>这一片还有几只活的。</summary>
    private int Live() => _spawned.Count(z => z != null && z.IsActive && !z.IsDead);

    private static float NearestPlayerDistance(GameManager simulation, Vector2 position)
    {
        List<Player> players = simulation.ActivePlayerList;
        float best = float.MaxValue;

        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            float distance = Vector2.DistanceSquared(player.Position, position);
            if (distance < best)
            {
                best = distance;
            }
        }

        return best == float.MaxValue ? float.MaxValue : (float)Math.Sqrt(best);
    }

    /// <summary>
    /// 目标换了: (可选) 横幅 -> "新目标"大字 + 目标栏刷新 —— **排队发, 并且目标晚一点才真换**。
    ///
    /// ## 玩家实测 (2026-09-15)
    ///
    /// "接任务时弹出的 New Mission 和 New Objective ... 会同时出现然后重叠在一起, 有时候还带红底;
    /// 接最后一个任务时出现的位置还不对, 现在是凭空出现。"
    ///
    /// ## 客户端怎么播 (GUI_ObjectiveManager / GUI_RaidEventObjectiveEntry)
    ///
    ///   横幅 (NewMission/MainObjective)  收到立刻播, 播 3 秒
    ///   目标栏刷新 (ObjectiveLogUpdate)   收到后等 1 秒; 这 1 秒里目标栏**冻结**在旧目标,
    ///                                    到点把"下一次目标变化"标成大字过场 (ScreenSplash), 然后解冻
    ///
    /// 以前两样塞在同一条消息里、目标同一帧就换:
    ///
    ///   1. 横幅 0~3 秒, 大字 1 秒就起 —— 两个叠在一起。
    ///   2. 目标是每帧同步的, 常常比这条消息**先到**。客户端没冻结, 当成普通滑动换掉了; 1 秒后那个
    ///      "大字过场"标记没用上, 一直挂着, 等到**下一次**目标变化才用 (比如到达区域、领下一个任务) ——
    ///      于是大字在不该出现的时候冒出来, 又和那时的横幅叠在一起。
    ///
    /// 现在: 横幅先发, 放完 (<see cref="BannerSeconds"/>) 再发目标栏刷新; 刷新发出去
    /// <see cref="ObjectiveLeadSeconds"/> 之后服务端才把目标换掉。期间目标栏保持旧的 (<see cref="HoldingObjective"/>)。
    /// 原厂服务端怎么排的随服务端丢了, 这个节奏是照客户端的时长倒推的。
    /// </summary>
    private void Announce(GameServer network, Action<CrossroadsGUIUpdate> banner)
    {
        if (banner != null)
        {
            CrossroadsGUIUpdate bannerUpdate = new CrossroadsGUIUpdate();
            banner(bannerUpdate);
            Enqueue(network, bannerUpdate, BannerSeconds);
        }

        CrossroadsGUIUpdate log = new CrossroadsGUIUpdate();
        log.AddObjectiveLogUpdateDelay(waitForTransmission: false);
        Enqueue(network, log, ObjectiveLeadSeconds);
    }

    /// <summary>排进 HUD 队列; 队列空着就当场发。<paramref name="after"/> = 发完之后下一条要隔多久。</summary>
    private void Enqueue(GameServer network, CrossroadsGUIUpdate update, float after)
    {
        _hud.Enqueue((update, after));
        PumpHud(network, 0f);
    }

    private void PumpHud(GameServer network, float deltaTime)
    {
        _hudTimer = Math.Max(0f, _hudTimer - deltaTime);
        while (_hudTimer <= 0f && _hud.Count > 0)
        {
            (CrossroadsGUIUpdate update, float after) = _hud.Dequeue();
            Broadcast(network, update);
            HudMessagesSent++;
            _hudTimer = after;
        }
    }

    /// <summary>诊断: 发出去的 HUD 消息条数。</summary>
    public int HudMessagesSent { get; private set; }

    /// <summary>
    /// 推一次性的 HUD 事件。载荷由原厂的 <see cref="CrossroadsGUIUpdate"/> 写,
    /// 我们只拼外面那两个字节 (同 <see cref="TransmissionDirector"/>)。
    /// </summary>
    private static void Broadcast(GameServer network, CrossroadsGUIUpdate update)
    {
        if (network == null || update.Events.Count == 0)
        {
            return;
        }

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
        }
    }
}
