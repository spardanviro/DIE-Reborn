using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.MapObjects;
using ConductorGameLogic.View;
using FarseerPhysics.Common;
using _0023Sq;
using _0023t5;

namespace DIE.Server.Game;

/// <summary>
/// Scout 任务的**主线交互点** —— 敌方卡车 (偷物资) / 补给点 (占领)。
///
/// ## 玩家看到的
///
/// "Camp by Night 本来应该出现敌方卡车的地方现在是一只 looter"。
/// 补充描述: "那里确实有一辆蓝方卡车, 卡车旁边还有个炮塔, 你要从卡车里面偷
/// 物资, 偷的时候卡车的警报会响, 然后会像补给点那样按波次刷僵尸, 直到把物资
/// 偷完, 然后会触发下一步路径点。"
///
/// ## 那辆卡车是**特效**, 不是实体
///
/// 地图数据里没有第二个 <c>TruckerSpawnPoint</c>, 也没有第二个
/// <c>Spawn_TruckTurret</c> —— 卡车不是靠地图物件摆出来的。
///
/// 它来自 <c>Maps/raideventsettings.data</c> (<c>RaidEventSettingsData</c>,
/// <see cref="GameDataLoader"/> 已经在加载)。那份文件按
/// <see cref="RaidEventType"/> 给出每种事件长什么样:
///
///   ScoutStealSupplies  'Steal'      交互 GrapSupplies  特效 Event_Scout_StealSupplies_Truck
///   ScoutCaptureFlag    'Scout Flag' 交互 Flag          特效 Vfx_Event_StorageRoom_FlagCapture_Scout_01
///   ScoutKillLooter     'KillLooter' (没有交互 —— 那张图是杀 Boss)
///
/// 每份还带 5 条文案, 一一对应 <see cref="InteractionState"/> 的五个状态
/// (Inactive / Allowed / Disallowed / Progress / Finished)。
///
/// ## 整条链, 缺的只有服务端填数据这一头
///
/// <code>
/// 服务端往 ScoutMission._0023gi._0023Kmd 放一个 InteractionPoint
///   │
///   ├─ 同步: ScoutMission.LoadGameMode (:123) 已经做好了
///   │       _0023hi._0023Imd = _0023ei;  NetworkManager.AddSynchronizable(_0023hi);
///   ▼
/// UnityRaidEventView.Update
///   │  VisualID % options.Length 挑出那份 EventViewData
///   │  用 interactionViewData.Effect 画出卡车 / 旗子
///   │  按 point.State 从 5 条文案里挑一条
///   ▼
/// ViewManager.PushRaidEventInteraction
///   -> PushActionInteraction(GenericInteraction, pos, text, abilityId: point.ID)
///   ▼
/// 玩家按交互键 -> GameManager.SendStartInteraction(charIndex, point.ID)
///   ▼
/// GameMessageType.StartInteraction        -> LidgrenHost.HandleStartInteraction
///   -> <see cref="StartInteraction"/>
/// </code>
///
/// 又是"机制完整、只缺服务端那一半"的形状 —— 和补给登记、僵尸步态、
/// 局末奖励是同一类。
///
/// 地基那一版 (卡车/旗子画出来、走近有提示、小地图有标记) 玩家已经实测通过。
/// 这一版补上状态机: 按键开始 -> 读条 -> 警报波次 -> 完成发物资 -> 推进路径点。
///
/// ## 偷和占领是**同一套流程**
///
/// 两者只差三样: 读条时长、完成后给什么、文案 (文案是客户端按
/// <see cref="InteractionState"/> 自己挑的, 我们只管改状态)。所以一套状态机
/// 两种目标共用 —— 玩家原话"补给点的占领流程之前已经完整做过, 只要在这里
/// 实装即可"。
///
/// 注意**不能**直接复用 <see cref="StorageRoomCaptureDirector"/>: 那一条走的是
/// <c>StorageRoom</c> 实体 + 教程 GameModeData 上的四个数组 + 技能槽 19 的
/// <c>CaptureFlag</c>; Scout 这条走的是 <c>InteractionPoint.Progress</c> +
/// <c>GenericInteraction</c>。数据通道和触发方式都不一样, 能复用的是**做法**
/// (读条 / 数在场的人 / 分段刷波 / 完成抛补给), 不是代码。
///
/// ## ⚠️ 圆环底下那行百分比在 ScoutMission 上**画不出来**
///
/// 教程补给点那个"圆环 + 19% + x1.5"是 <c>StorageRoomHUD</c> 画的, 而它
/// (StorageRoomHUD.cs:102) 只在三种模式下取数:
///
///     IsScavengerHunt || IsTutorialGameMode || IsTeamvsTeam
///
/// <c>ScoutMission</c> 一个都不是 —— 就算我们在图上摆一个真的
/// <c>StorageRoom</c>, 那圈 HUD 也不会更新。Scout 这边的进度是画在**交互点
/// 特效自己**身上的 (<c>UnityRaidEventView</c> 把 <c>point.Progress</c> 喂给
/// 特效的 "Progress" 参数, :116), 另外还有一份在头顶 —— 见
/// <see cref="StartChannel"/> 里那个 <c>Vfx_Progress02</c>。
///
/// 也就是说"占领百分比文字"这一条不是服务端漏填, 是这个模式的客户端里根本
/// 没有那块 UI。
///
/// ## 按键怎么回来
///
/// 客户端那一下是 <c>GetKeyDown</c> (UnityClient.cs:2455), **一次性**的:
///
///     SendStartInteraction(charIndex, point.ID)  ->  GameMessageType.StartInteraction
///
/// 之后没有任何"我还按着"的上行 —— 读条完全由服务端推, 玩家只要**留在圈里**。
/// 走开就停 (退回 Allowed), 和补给点那句 "REMAIN CLOSE UNTIL FULLY SECURED"
/// 一个意思。
/// </summary>
public sealed class ScoutObjectiveDirector
{
    /// <summary>
    /// 读条要多久 (一个人独占时)。
    ///
    /// **我们定的。** 原厂这个数是真的没有 —— <c>InteractionViewData</c> 只有
    /// Type / Texts / Effect 三个字段, <c>ObjectiveViewData</c> 只有 Objective / Text,
    /// 整个 <c>raideventsettings.data</c> 里一个时长字段都没有 (09-21 复查)。
    ///
    /// 能拿来对照的只有那几个交互 buff 的原厂时长: 占领补给点 15 秒、偷物资 12 秒、
    /// 交付 10 秒 (<c>ActionName</c> 注册时带的技能 ID -&gt; buff 的 <c>_0023fq</c>)。
    /// 但那是**卡车和拾荒补给点**那几条, 不是十字路口的任务点。
    ///
    /// 这里 22 秒是 Scout (单人) 用的。十字路口另给, 见
    /// <see cref="CrossroadMissionDirector.SecondsToChannel"/>。
    /// </summary>
    public float SecondsToComplete { get; set; } = 22f;

    /// <summary>
    /// 站多远之内算"在场"(进度继续涨)。
    ///
    /// ## 30 太小, 玩家在卡车上撞到了
    ///
    /// 真机日志: Camp by Night 上按 X 一路 "交互被拒: 离交互点太远", 而
    /// Purple Moon Club 的旗子同样是 30 却好好的。差别在**交互体的大小**:
    ///
    /// <c>ViewManager.PushRaidEventInteraction</c> 判"能不能按"用的**不是**距离,
    /// 而是那个视觉自带的多边形:
    ///
    ///     if (settings?.GameplayPolygon != null)
    ///         flag = 点在多边形里;          // 卡车走这条
    ///     else
    ///         flag = Distance &lt; 20;        // 没多边形才退回距离
    ///
    /// 而且 <c>UnityRaidEventView</c> 还把那个多边形注册成了**碰撞体**
    /// (<c>ActivateCollision</c>) —— 卡车是个实心障碍, 玩家根本站不到它的原点上,
    /// 只能贴在车身外侧。旗子小, 站得进 30; 卡车大, 站不进。
    ///
    /// 所以这个数必须罩得住整个交互体。给 80 —— 比一辆卡车宽裕, 又远小于
    /// "在地图另一头也算在场"。真机上会把实际距离打进日志, 照那个再收紧。
    /// </summary>
    public float HoldRadius { get; set; } = 80f;

    /// <summary>
    /// **占领**时站多远之内算"在圈里" —— 和偷卡车那个 <see cref="HoldRadius"/> 分开。
    ///
    /// ## 玩家实测
    ///
    /// "如果玩家走到圈外, 进度会停止 …… 现在的表现是玩家即便走出补给点范围
    /// 进度仍然在前进。"
    ///
    /// 真机日志印证: Club 那一局从摇完旗到占完整整 22 秒, 一条"占领暂停"
    /// 都没有。原因是两种目标共用了 80 —— 那是给卡车定的 (卡车是实心碰撞体,
    /// 人只能贴在车身外侧), 比旗子那个圈大得多, 走出圈了服务端还当你在里面。
    ///
    /// ## 这个数现在是**我们定的**, 不再跟原厂那个 25
    ///
    /// 曾经从 <c>BalanceData._0023D4f._0023b3</c> (= 25) 读, 理由是"摇旗技能判
    /// 人还在不在点边上用的就是它" (-Sq/-OZb.cs:158)。但那 25 管的是**引导会不会断**,
    /// 不是地上那个圆圈画多大 —— 两件事撞巧同名而已。
    ///
    /// 玩家两轮实测: 80 太大 -> 换成 25 -> "还是太大, 减小到和占领进度圆圈 UI
    /// 一样的大小"。所以按圆圈来。
    ///
    /// ## 圆圈的真实半径读不到, 只能靠实测收
    ///
    /// 那个圈是特效 (<c>Vfx_Event_StorageRoom_FlagCapture_Scout_01</c>) 画的,
    /// 客户端只往它身上写一个 <c>Progress</c> 浮点 (<c>UnityRaidEventView.cs:116</c>),
    /// 尺寸烘在 Unity 预制体里 —— 源码和 <c>raideventsettings.data</c> 里都没有。
    ///
    /// 15 是按"比 25 明显小一圈"取的; 玩家实测 (2026-09-13) "又有点太小了, 增加到 20"。
    /// 再一轮 (同日) "再改大点, 到 30"。**现在是 30, 玩家定的数。**校准办法: 占领时慢慢往外走,
    /// 进度一停服务端就打一条"占领暂停 … 最近的玩家离点 N" (见 <c>Tick</c>),
    /// 那个 N 就是当时的半径; 和眼睛看到的圈边对不上就报给我, 照差值调。
    /// </summary>
    public float CaptureRadius { get; set; } = 30f;

    /// <summary>
    /// 圈里人数对应的速度倍率 —— 走 <see cref="CaptureSpeed"/> 那张**原版表**
    /// (1 / 1.5 / 1.8 / 2.0), 三处占领共用。只管占领, 偷卡车是各偷各的引导。
    ///
    /// 09-21 之前这里是线性的"每多一人 +50%", 三人四人比原版快 11% 和 25%。
    /// </summary>
    public static float[] SpeedByHolders => CaptureSpeed.ByHolders;

    /// <summary>
    /// 按键那一下最远认到多远。**这是反作弊下限, 不是手感参数。**
    ///
    /// 能不能按是**客户端的多边形说了算**的 (见 <see cref="HoldRadius"/>), 那个
    /// 形状服务端拿不到。所以这里只做一件事: 挡住"人在地图另一头也能按"。
    /// 给得比 HoldRadius 松, 免得又出现"客户端给了提示、服务端不认"那种死局。
    /// </summary>
    public float StartRadius { get; set; } = 160f;

    /// <summary>
    /// 按下去之后先摇多久旗, 才开始涨进度。
    ///
    /// 玩家原话: "按 X 后应该先播放摇旗动画和进度条, 然后才是开始占领"。
    /// 这一段的动画和读条圈是**原厂技能自己放的** ——
    /// <see cref="WindUpAbility"/>, 我们只负责让玩家去放它, 以及在它放完之前
    /// 不涨占领进度。
    ///
    /// 时长取自那个技能的引导时间。等真机对不上再调。
    /// </summary>
    public float WindUpSeconds { get; set; } = 5f;

    /// <summary>
    /// 读条期间刷几波。玩家原话: "偷的时候卡车的警报会响, 然后会像补给点那样
    /// 按波次刷僵尸"。
    ///
    /// 数和 <see cref="StorageRoomCaptureDirector.Waves"/> 对齐 —— 那个是实测
    /// 校准过的。
    /// </summary>
    public int Waves { get; set; } = 3;

    /// <summary>每波几只。同上, 和补给点那边对齐。</summary>
    public int ZombiesPerWave { get; set; } = 9;

    /// <summary>完成时抛几包补给。</summary>
    public int CrateCount { get; set; } = SupplyThrower.DefaultCrateCount;

    /// <summary>每包多少补给。</summary>
    public int SuppliesPerCrate { get; set; } = SupplyThrower.DefaultSuppliesPerCrate;

    /// <summary>摆出来的交互点。空 = 这张图不需要 (比如杀 Looter 那张)。</summary>
    public IReadOnlyList<InteractionPoint> Points => _points;

    /// <summary>这一帧发生的事, 由宿主打进日志。宿主读完自行清空。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>
    /// 这一帧刷出来的怪 —— 宿主拿去激怒 (LidgrenHost.ProvokeSpawned)。
    ///
    /// 警报刷出来的怪就该扑上来, 不是站在原地等玩家走进仇恨半径 ——
    /// 和埋伏怪同一个道理。
    /// </summary>
    public readonly List<Character> SpawnedThisTickList = new List<Character>();

    /// <summary>完成了没有。</summary>
    public bool Completed { get; private set; }

    /// <summary>当前进度 0~1。诊断用。</summary>
    public float Progress => _points.Count == 0 ? 0f : _points[0].Progress;

    /// <summary>当前状态。诊断用。</summary>
    public InteractionState State =>
        _points.Count == 0 ? InteractionState.Inactive : _points[0].State;

    /// <summary>这张图的主线目标 —— 由 <see cref="ScoutStoryDirector.Objective"/> 传进来。</summary>
    public RaidEventType Objective { get; private set; } = RaidEventType.None;

    /// <summary>摆好了没有。幂等的判据。</summary>
    public bool IsArmed { get; private set; }

    private readonly List<InteractionPoint> _points = new List<InteractionPoint>();

    /// <summary>
    /// 摆交互点。幂等 —— 重复调直接返回。
    /// </summary>
    /// <returns>摆了几个。</returns>
    public int Arm(GameManager simulation, RaidEventType objective)
    {
        if (IsArmed || simulation?.MapObjects == null)
        {
            return 0;
        }

        IsArmed = true;
        Objective = objective;

        // ⚠️ 这里曾经用 BalanceData._0023D4f._0023b3 (=25) 覆盖 CaptureRadius。
        // 已去掉 —— 那个数管的是摇旗引导断不断, 不是地上圆圈的大小, 而玩家实测
        // 25 仍然比圆圈大。详见 CaptureRadius 的说明。加回去等于无视实测反馈。

        if (!TypeFor(objective, out InteractionPointType type))
        {
            return 0;   // 这张图的目标不靠交互点 (ScoutKillLooter)
        }

        if (simulation.GameMode is not ScoutMission scout)
        {
            return 0;
        }

        Event_RaidEvent raid = simulation.MapObjects.OfType<Event_RaidEvent>().FirstOrDefault();
        if (raid == null)
        {
            return 0;
        }

        InteractionPoint point = new InteractionPoint
        {
            ID = FirstPointId,

            // 客户端拿它挑 EventViewData: `options[point.VisualID % options.Length]`
            // (UnityRaidEventView.cs:70)。ScoutStealSupplies 和 ScoutCaptureFlag
            // 各只有 1 份选项, 所以 0 就是唯一那份。
            VisualID = 0,

            Position = raid.Position,
            Direction = raid.Direction,
            Event = objective,
            Type = type,

            // Allowed = "走近就能按"。给 Inactive 的话客户端连按键提示都不显示
            // (PushRaidEventInteraction 的 denyInteraction 参数是
            //  `State != Allowed`), 那就验证不了这一步。
            State = InteractionState.Allowed,
            Progress = 0f,
        };

        _points.Add(point);
        scout._0023gi._0023Kmd.Add(point);

        // 小地图上指一下它 —— 玩家得知道往哪走。
        //
        // _0023qmd 的语义从客户端 GameManagerClient._0023ujd 里读出来:
        //   _0023mmd  -> MiniMapIconType.RaidEvent + RaidEventFlash   (主线目标)
        //   _0023nmd  -> Cross
        //   _00235ar  -> StorageRoomFlash
        //   _00236ar  -> **StealingTruckFlash**   (正在偷卡车时那个闪烁)
        //
        // 读条期间会换成 _00236ar (StealingTruckFlash) —— 见 SetMinimap。
        scout._0023gi._0023Lmd.Add(new _0023Hmd
        {
            _0023Gmd = _0023qmd._0023mmd,
            _0023B = raid.Position,
        });

        _spawns.AddRange(CollectSpawnPoints(simulation, raid));

        // 类型表 —— 刷 Walker 要用。和补给点那边同一个来源。
        foreach (KeyValuePair<string, Type> entry in ZombieFactory.ResolveKindTypes())
        {
            _types[entry.Key] = entry.Value;
        }

        Events.Add($"主线交互点就位: {type} @ ({raid.Position.X:F0},{raid.Position.Y:F0}), "
                   + $"警报刷怪位 {_spawns.Count} 个");

        return 1;
    }

    /// <summary>
    /// 在**指定位置**摆一个交互点 —— 十字路口用这条。
    ///
    /// ## 为什么复用这个类
    ///
    /// 玩家实测 (十字路口, 2026-09-13): "占领补给点没有播放摇旗动画和引导读条,
    /// 从卡车偷物资也没有实装, 按 X 后过一会儿物资箱和成功占领补给点一样掉落
    /// 而不是偷取, 这两点在 tutorial 和 scout mission 里面已经实装了, 在这里复现一下。"
    ///
    /// 那一整套 (起手摇旗 5 秒 + 引导 buff 的动画/齿轮/定身、占领走出圈暂停、
    /// 偷物资全程引导 + 边偷边往身上装、断了停在原地再按接着偷) 每一条都是
    /// 实测校准过的。十字路口第一版自己写了个简化读条, 于是这些全丢了。
    ///
    /// 能直接复用是因为它依赖的东西两个模式**都有**: 交互点/小地图表在
    /// <see cref="IRaidEventGameMode"/> 上、补给表在 <c>AsISupplies</c> 上、
    /// 引导 buff <c>_0023Eab</c> 两个模式都注册了 (ScoutMission.cs:62 /
    /// RaidMission.cs:230)。原来绑死 ScoutMission 的只是取这几张表的写法。
    /// </summary>
    /// <param name="objective">事件类型。<c>CaptureFlag</c>/<c>ScoutCaptureFlag</c> 走摇旗占领, 其余走偷物资。</param>
    /// <param name="visualId">客户端挑视觉用的变体号, 必须和目标 UI 那条一致。</param>
    /// <param name="spawns">警报波次用的刷怪位; 波次数由 <see cref="Waves"/> 决定, 给 0 就不刷。</param>
    /// <returns>摆了几个 (0 或 1)。</returns>
    public int ArmAt(GameManager simulation, RaidEventType objective, InteractionPointType type,
                     int visualId, Vector2 position, Vector2 direction,
                     IEnumerable<Spawn_EventEntities> spawns = null)
    {
        if (IsArmed || simulation?.GameMode is not IRaidEventGameMode)
        {
            return 0;
        }

        IsArmed = true;
        Objective = objective;

        InteractionPoint point = new InteractionPoint
        {
            ID = FirstPointId,
            VisualID = (ushort)visualId,
            Position = position,
            Direction = direction,
            Event = objective,
            Type = type,
            State = InteractionState.Allowed,
            Progress = 0f,
        };

        _points.Add(point);
        Tables(simulation)._0023Kmd.Add(point);
        Tables(simulation)._0023Lmd.Add(new _0023Hmd
        {
            _0023Gmd = _0023qmd._0023mmd,
            _0023B = position,
        });

        if (spawns != null)
        {
            _spawns.AddRange(spawns);
        }

        foreach (KeyValuePair<string, Type> entry in ZombieFactory.ResolveKindTypes())
        {
            _types[entry.Key] = entry.Value;
        }

        Events.Add($"交互点就位: {objective}/{visualId} {type} @ ({position.X:F0},{position.Y:F0})");
        return 1;
    }

    /// <summary>
    /// 这一车有多少补给。null = 按原厂 Scout 那条 (<c>Supplies.Settings._0023jj</c>)。
    ///
    /// 十字路口**不能**用那条: RaidMission 的 <c>_0023jj</c> 是整局的目标 1000
    /// (RaidMission.cs LoadGameMode), 不是一车的量。
    /// </summary>
    public int? TruckLoadOverride { get; set; }

    /// <summary>
    /// 做完之后要不要把跳台打开 (见 <see cref="OpenTheLedge"/>)。
    /// Scout 要; 十字路口不要 —— 那边的跳台和这个目标没关系。
    /// </summary>
    public bool OpensLedges { get; set; } = true;

    /// <summary>
    /// 做完之后要不要把小地图指回卡车。Scout 要 (那是下一步);
    /// 十字路口由 <see cref="CrossroadMissionDirector"/> 自己指路。
    /// </summary>
    public bool PointsBackToTruck { get; set; } = true;

    /// <summary>三张同步表 (交互点 / 小地图 / 目标)。两个模式都挂在同一个接口上。</summary>
    private static _0023Nmd Tables(GameManager simulation) =>
        ((IRaidEventGameMode)simulation.GameMode)._0023ei(null);

    /// <summary>
    /// 警报波次从哪刷 —— **用地图上现成的固定刷怪位**, 不围着目标铺圈。
    ///
    /// ## 玩家实测
    ///
    /// "现在刷怪时怪物为环形刷出, 原游戏是有几个刷怪点, 在附近的建筑旁边。"
    ///
    /// ## 关联键换了
    ///
    /// 先前照搬补给点那边的约定, 按 <c>SceneMapObject.ID</c> 找同组的
    /// <c>Spawn_EventEntities</c>。教程图上 <c>Event_StorageRoom</c> 是那么组织的
    /// (一个 ID 挂 9 个点), **但 Scout 这两张图不是** —— 探针实测:
    ///
    ///     Scout_01_Outpost: 113 个 Spawn_EventEntities, ID 几乎两两不同
    ///     Scout_03_Club:    110 个, 同样
    ///
    /// 所以按 ID 一个都配不上, 日志里那句 "警报刷怪位 0 个" 就是这么来的,
    /// 于是一路走兜底的圆环。
    ///
    /// 这些点本身是好的 —— 它们贴着建筑摆, 而且各自带着出场动画
    /// (<c>_0023eCc</c> 挖地而出 / <c>_0023cCc</c> / <c>_0023Llc</c> / <c>_0023Oed</c>),
    /// 正是玩家说的"从附近环境中的固定刷怪点刷出来"。缺的只是**怎么挑**。
    ///
    /// 判据换成距离: 取离目标最近的那一撮。实测目标周围本来就密集地摆着一圈:
    ///
    ///     Outpost 目标 (903,16):  16 / 18 / 28 / 30 / 34 / 41 / 46 / 57 单位各一个
    ///     Club    目标 (-331,-415): 48 / 58 / 66 / 71 / 79 / 87 / 91 / 100 / 102
    ///
    /// <see cref="SpawnSearchRadius"/> 150 把这两撮都收全, 又够不到隔壁区域。
    /// </summary>
    private List<Spawn_EventEntities> CollectSpawnPoints(
        GameManager simulation, Event_RaidEvent raid)
    {
        List<Spawn_EventEntities> all = simulation.MapObjects
            .OfType<Spawn_EventEntities>()
            .ToList();

        // 先试原来那条: 真有同 ID 分组的图 (比如教程) 照旧走它, 那是作者的分组。
        List<Spawn_EventEntities> grouped = all.Where(e => e.ID == raid.ID).ToList();
        if (grouped.Count > 0)
        {
            return grouped;
        }

        return all
            .Select(e => (Point: e, Distance: Vector2.Distance(e.Position, raid.Position)))
            .Where(x => x.Distance <= SpawnSearchRadius)
            .OrderBy(x => x.Distance)
            .Take(MaxSpawnPoints)
            .Select(x => x.Point)
            .ToList();
    }

    /// <summary>
    /// 就近找刷怪位时, 最远认到多远。见 <see cref="CollectSpawnPoints"/>。
    ///
    /// **我们定的**, 但不是拍的: 两张图目标周围最近那一撮点分别落在 16~57 和
    /// 48~102, 再往外就跳到 149+ (已经是隔壁区域了)。150 收得住这一撮。
    /// </summary>
    public float SpawnSearchRadius { get; set; } = 150f;

    /// <summary>就近最多认几个点。够一波轮转就行, 多了会摸到别的区域去。</summary>
    public int MaxSpawnPoints { get; set; } = 12;

    /// <summary>
    /// 玩家按了交互键。
    ///
    /// 客户端发的是 <c>SendStartInteraction(charIndex, point.ID)</c>, 一次性
    /// (GetKeyDown)。这里只负责**起头**, 之后的读条在 <see cref="Update"/> 里推。
    /// </summary>
    /// <returns>true = 这一下被接受了 (开始读条)。</returns>
    public bool StartInteraction(GameManager simulation, ushort pointId, Character who)
    {
        if (_points.Count == 0 || _points[0].ID != pointId || Completed || who == null)
        {
            return false;
        }

        if (ChannelAlive)
        {
            return true;   // 正在引导, 重复按无害
        }

        // 摇完旗之后占领是自己在跑的 (人可以走动), 这时再按一下什么也不用做。
        if (_windUpDone && _points[0].State == InteractionState.Progress)
        {
            return true;
        }

        float distance = Vector2.Distance(who.Position, _points[0].Position);

        if (distance > StartRadius)
        {
            Events.Add($"交互被拒: 离交互点 {distance:F0} (上限 {StartRadius:F0})");
            return false;
        }

        SetState(simulation, InteractionState.Progress);
        StartChannel(simulation, who);

        Events.Add((Objective == RaidEventType.ScoutStealSupplies
                       ? $"开始偷物资 —— 卡车警报响起 (从 {_points[0].Progress:P0} 接着偷)"
                       : "按下占领 —— 先摇旗")
                   + $" (玩家离交互点 {distance:F0})");

        return true;
    }

    /// <summary>
    /// 给玩家挂上**原厂那个通用交互引导 buff** —— 动画、头顶齿轮、定身
    /// 全是它自己带的。
    ///
    /// ## 为什么是这一个
    ///
    /// 玩家实测两条:
    ///
    ///   "按 X 后没有出现角色摇旗动画加读条"
    ///   "按 X 后并没有播放偷物资动画, 头上也没有齿轮 UI, 人还能正常移动"
    ///
    /// 上一版让玩家去放 <c>AbilityIdentifier.CaptureFlag</c> (槽 19)。那条走不通:
    /// 它的 buff <c>_0023Sq._0023OZb</c> 第一件事是找半径内的 <c>StorageRoom</c>,
    /// 找不到就 <c>Destroy()</c> (-Sq/-OZb.cs:112) —— 而 Scout 三张图上
    /// **一个 <c>Event_StorageRoom</c> 都没有** (探针实测)。所以它挂上就死,
    /// 玩家什么也看不到。
    ///
    /// <c>AbilityIdentifier.StealSupplies</c> (槽 28) 同样走不通, 而且更彻底:
    /// 它的 buff <c>_0023fdb</c> 每 tick 算"卡车里还剩多少可偷", 那个数只有
    /// <c>IsScavengerHunt</c> 和 <c>IsRaidMission</c> 两条分支
    /// (-Sq/-fdb.cs:201-208)。ScoutMission 两条都不是 -&gt; 恒为 0 -&gt; 当场自我了断。
    ///
    /// 对的那一个是 <c>AbilityIdentifier.EnviromentInter</c> 的 buff
    /// <c>_0023Sq._0023Eab</c>:
    ///
    ///   - <b>ScoutMission 自己注册了它</b> (ScoutMission.cs:62, 技能槽 29),
    ///     RaidMission 也注册了 (:230) —— 这就是 Scout/Raid 交互目标用的那个
    ///   - <b>没有任何前置检查</b>: 不找 StorageRoom, 不看 GameMode 类型,
    ///     <c>_0023fq</c> 默认 <c>float.MaxValue</c> (永不自灭), 全由挂它的人说了算
    ///   - <c>StartChanneling(canMove: false, canRotate: false,
    ///     interruptedByMovement: true, interruptedByDamage)</c> -&gt; <b>定身</b>,
    ///     人一动就断
    ///   - <c>PlayAnimation(_00236G, isLooping: true)</c> -&gt; <b>动画随便挑</b>
    ///   - <c>Vfx_Progress02</c> 挂在 <c>HeightOverHead + 11.5</c> -&gt;
    ///     <b>头顶那个齿轮</b>
    ///   - <c>_0023Bab</c> 为真时再加一个 <c>Vfx_Capture_Flag_01</c> 绑在右手骨骼
    ///     -&gt; <b>手里那面旗</b>
    ///   - <c>_0023Dab</c> 是**结束回调**, 断了/走完都会叫一次
    ///
    /// 全部字段带 <c>[_0023qGc]</c>, 走原厂 buff 同步通道 —— 不需要新协议。
    ///
    /// ## 两种目标挂法不同
    ///
    ///   占领 (Club): 只在**起手**挂 5 秒, 动画 <c>CapturePoint</c> + 手里那面旗。
    ///                摇完就摘掉, 之后占领进度自己涨, 人可以在圈里走动 ——
    ///                和教程一模一样 (教程那 5 秒也只是起手, 见
    ///                <c>StorageRoomCaptureDirector</c>)。
    ///
    ///   偷物资 (卡车): **整个过程都挂着**, 动画 <c>DeliverSupplies</c> (就是教程
    ///                  最后往卡车上搬物资那个循环动画)。人一动就断, 断了进度
    ///                  停在原地, 再按一次接着偷 —— 和教程交付一模一样。
    /// </summary>
    private void StartChannel(GameManager simulation, Character who)
    {
        bool windUp = IsCapture && !_windUpDone;

        // 剩下要引导多久。偷物资是"把剩下的偷完", 所以按剩余进度折算。
        // 偷物资那条给一秒余量: 引导和进度是两条独立的计时, 差一帧就会在 99%
        // 上报"被打断"。进度先满, 我们自己 StopChannel (见 Complete)。
        float seconds = windUp
            ? WindUpSeconds
            : Math.Max(0.1f, SecondsToComplete * (1f - _points[0].Progress)) + ChannelSlack;

        _0023Eab channel = simulation.GetGameObjectFromPool<_0023Eab>();

        if (channel == null)
        {
            // 池子空了就别卡住流程 —— 宁可少个动画。
            _windUpDone = true;
            _noChannel = true;
            _channelOwner = null;
            _channelLeft = 0f;
            Events.Add("引导 buff 没拿到 (对象池空?), 这次没有动画");
            return;
        }

        channel._00236G = windUp ? AnimationEnum.CapturePoint : AnimationEnum.DeliverSupplies;
        channel._0023zab = _points[0].Position;      // 脸朝目标
        // 摇旗那 5 秒要显示引导条 —— 玩家实测"除了摇旗时的进度条没有出现"。
        // 偷物资那条不显示: 它的引导时长是按剩余进度折算的 (还多留了
        // ChannelSlack), 条走完的时刻和真正偷完对不上, 反而误导。
        channel._0023Aab = windUp;
        channel._0023Bab = windUp;                   // 手里那面旗只有摇旗时有
        channel._0023fq = seconds;                   // 走满 (Age > _0023fq) 就自己结束

        simulation.ApplyBuff(channel, who, who, who);

        _noChannel = false;
        _channelOwner = who;
        _channelLeft = seconds;
    }

    /// <summary>
    /// 引导还挂在人身上吗 —— **直接看 buff 在不在**。
    ///
    /// 不用 <c>_0023Eab._0023Dab</c> 那个结束回调: buff 是**池化**的, 摘掉之后
    /// 对象立刻回池子, 手上留的那个引用状态不可信 (实测 <c>IsActive</c> 仍为真)。
    /// 问玩家身上有没有这个 buff 是唯一不会骗人的判据。
    ///
    /// <c>_0023aD._0023XFc</c> 是它自己在 <c>_0023Cq</c> 里认领的枚举
    /// (-Sq/-Eab.cs:115)。
    /// </summary>
    private bool ChannelAlive =>
        !_noChannel && _channelOwner != null && _channelOwner.IsActive
        && _channelOwner.HasBuff(_0023Oq._0023aD._0023XFc);

    /// <summary>把引导摘掉 (完成 / 放弃时)。</summary>
    private void StopChannel()
    {
        Character owner = _channelOwner;
        _channelOwner = null;
        _channelLeft = 0f;

        if (owner != null && owner.IsActive)
        {
            owner.GetBuff(_0023Oq._0023aD._0023XFc)?.Destroy();
        }
    }

    /// <summary>
    /// 放弃这个目标 (十字路口超时了): 摘掉人身上的引导 buff, 不然人被定在原地。
    /// 已经完成的不受影响。
    /// </summary>
    public void Abort()
    {
        if (!Completed)
        {
            StopChannel();
        }
    }

    /// <summary>判"引导走满了"时给多少余量 —— 一帧的量级。</summary>
    private const float ChannelDoneSlack = 0.15f;

    /// <summary>偷物资的引导比进度多留这么久, 免得差一帧被判成打断。</summary>
    private const float ChannelSlack = 1f;

    /// <summary>这张图的目标是占领 (要先摇旗), 还是偷物资 (全程引导)。</summary>
    private bool IsCapture =>
        Objective == RaidEventType.ScoutCaptureFlag || Objective == RaidEventType.CaptureFlag;

    /// <summary>
    /// 每帧一次: 推读条、按段刷波、满了就收尾。
    /// </summary>
    public void Update(GameManager simulation, float deltaTime)
    {
        SpawnedThisTickList.Clear();

        if (_points.Count == 0 || Completed || simulation == null)
        {
            return;
        }

        if (_points[0].State != InteractionState.Progress)
        {
            return;
        }

        // ## 起手那 5 秒: 摇旗 (只有占领那张图)
        //
        // 引导还挂着 -> 进度不涨, 玩家被定在原地放动画。
        // 引导没了 -> 走满了就开始占领; 被打断 (人动了 / 挨打了) 就退回可按。
        bool channeling = ChannelAlive;

        if (channeling)
        {
            _channelLeft = Math.Max(0f, _channelLeft - deltaTime);
        }

        if (IsCapture && !_windUpDone)
        {
            if (channeling)
            {
                return;
            }

            if (_channelLeft > ChannelDoneSlack)
            {
                SetState(simulation, InteractionState.Allowed);
                Events.Add("摇旗被打断 (人动了或挨打了) —— 重新按一次");
                return;
            }

            _windUpDone = true;
            _channelOwner = null;
            Events.Add("摇旗完成 —— 开始占领, 留在圈里就行");
        }

        // ## 偷物资: **全程引导**, 断了就停在原地
        //
        // 和教程往卡车上搬物资一模一样 —— 那一段也是人一动引导就断,
        // 走回去再按一次接着搬。
        if (!IsCapture && !channeling && !_noChannel)
        {
            _channelOwner = null;
            SetState(simulation, InteractionState.Allowed);
            Events.Add($"偷物资被打断 (人动了或挨打了), 停在 {_points[0].Progress:P0} "
                       + "—— 再按一次接着偷");
            return;
        }

        int holders = CountHolders(simulation);

        // ## 占领时走出圈只是**暂停**, 不退回"要重新按 X"
        //
        // 玩家实测: "走到补给点圈外占领进度只是暂停, 不用再进圈重新按 X"。
        //
        // 状态留在 Progress 有两个好处: 客户端继续显示占领 UI, 而且
        // `denyInteraction = State != Allowed` 为真 -> 不会再冒出按键提示。
        // 玩家走回来就自己接着涨。
        if (holders <= 0)
        {
            if (!_paused)
            {
                _paused = true;
                Events.Add($"占领暂停 (半径 {ActiveRadius:F0} 内没人, 最近的玩家离点 "
                           + $"{NearestPlayerDistance(simulation):F0}), 停在 {_points[0].Progress:P0} —— "
                           + "走回去自己接着涨, 不用再按");
            }

            return;
        }

        if (_paused || (IsCapture && holders != _lastHolders))
        {
            Events.Add($"占领{(_paused ? "继续" : "人数变了")}: 圈里 {holders} 人, "
                       + $"速度 x{SpeedFor(holders):F1}, 从 {_points[0].Progress:P0}");
        }

        _paused = false;
        _lastHolders = holders;

        // 占领: 圈里人越多越快 (一人 x1.0, 每多一人 +0.5)。
        // 偷卡车: 固定速度 —— 引导时长是按 SecondsToComplete 折算的, 两边得对得上。
        float speed = SpeedFor(holders);

        float before = _points[0].Progress;
        float after = Math.Min(1f,
            before + speed * deltaTime / Math.Max(0.01f, SecondsToComplete));

        SetProgress(simulation, after);
        HandOverSupplies(simulation, after);
        FireWavesUpTo(simulation, before);

        if (after >= 1f)
        {
            Complete(simulation);
        }
    }

    /// <summary>
    /// **边偷边往身上装** —— 进度涨到几成, 身上就有几成。
    ///
    /// ## 玩家实测
    ///
    /// "按 X 无法真正偷取物资, 身上的物资数量没有变多。"
    ///
    /// ## 一车有多少是原厂写死的
    ///
    /// <c>ScoutMission.LoadGameMode</c> (:106-117) 按图名从 BalanceData 取一个数
    /// 塞进 <c>SupplyData.Settings._0023jj</c>:
    ///
    ///     Scout_01_Outpost -> _0023Xd  实测 60
    ///     Scout_03_Club    -> _0023Vd  实测 30
    ///     Scout_02_Lab     -> _0023Wd
    ///
    /// 那就是这张图的补给目标 —— 也就是这一车该有多少。不用我们再定一个数。
    ///
    /// ## 入账走原厂那一个方法
    ///
    /// <c>Supplies._0023Ij(IndexPlayer, 数量)</c> (Supplies.cs:175) 干三件事:
    /// 加到那个人名下、挂上/更新携带 buff <c>_0023aD._0023AA</c> 的层数
    /// (那就是屏幕上的补给计数)、通知 GameMode。<c>SupplyRegistrar</c> 已经把人
    /// 登记进表了, 否则这个方法会静默地什么都不做。
    ///
    /// 身上有上限 (<c>BalanceData._0023j7f._0023Etb</c>, 实测 100), 超了就别塞 ——
    /// 原厂的偷取 buff 也是这么判的 (-Sq/-fdb.cs:209)。
    ///
    /// ## 为什么不用原厂那个偷取 buff
    ///
    /// <c>_0023Sq._0023fdb</c> 每 tick 算"车上还剩多少", 而那个数只有
    /// ScavengerHunt 和 RaidMission 两条分支 (-Sq/-fdb.cs:201-208);
    /// ScoutMission 两条都不是, 恒为 0, 挂上就自我了断。见
    /// <see cref="StartChannel"/> 里的说明。
    /// </summary>
    private void HandOverSupplies(GameManager simulation, float progress)
    {
        if (IsCapture || _channelOwner is not Player thief || !thief.IsActive)
        {
            return;
        }

        int total = TruckLoad(simulation);
        int want = (int)(progress * total);
        int give = want - _handedOver;

        if (give <= 0)
        {
            return;
        }

        Supplies supplies = simulation.GameMode?.AsISupplies?._00239c();
        if (supplies == null)
        {
            return;
        }

        int room = GameManager.BalanceData._0023j7f._0023Etb
                   - supplies._0023Fj(thief.IndexPlayer);

        if (room <= 0)
        {
            return;   // 背满了, 剩下的留在车上
        }

        give = Math.Min(give, room);
        supplies._0023Ij(thief.IndexPlayer, give);
        _handedOver += give;

        Events.Add($"偷到 {give} 补给 (累计 {_handedOver}/{total}, "
                   + $"身上 {supplies._0023Fj(thief.IndexPlayer)})");
    }

    /// <summary>
    /// 这一车有多少 —— 原厂按图名写死的那个数, 见 <see cref="HandOverSupplies"/>。
    /// </summary>
    private int TruckLoad(GameManager simulation)
    {
        if (TruckLoadOverride.HasValue)
        {
            return TruckLoadOverride.Value;
        }

        int load = (simulation.GameMode as ScoutMission)?.SupplyData.Settings._0023jj ?? 0;

        // 取不到就退回一个能让流程走完的数 —— 空车比卡住好。
        return load > 0 ? load : DefaultTruckLoad;
    }

    /// <summary>地图数据没给补给目标时用这个。**我们定的**。</summary>
    private const int DefaultTruckLoad = 60;

    /// <summary>
    /// 偷完之后**把跳下高台那个口子打开**。
    ///
    /// ## 玩家实测
    ///
    /// "偷取全部物资后, 应该出现蓝色路径点, 然后可以跳到下一个区域去。"
    ///
    /// ## 那个口子本来就摆好了, 只是关着
    ///
    /// <c>Trigger_CharacterPush</c> 有个 <c>StartActive</c> 字段,
    /// <c>GameMode.SpawnPushPlayerTriggers</c> (:997) 照抄进
    /// <c>PushPlayerOverLedge._0023j0b</c>。探针实测:
    ///
    ///     Scout_01_Outpost  ID=1 @(876,24)   StartActive=**False**  地面箭头=True
    ///     Scout_03_Club     ID=1 @(-154,-421) StartActive=True
    ///
    /// Outpost 那个就在卡车 (903,16) 旁边 30 单位, 而且**开局是关的** ——
    /// 谁来开它, 那一半逻辑随服务端一起丢了。这就是"偷完才能跳下去"。
    /// Club 那个开局就是开的, 所以那张图不受影响。
    ///
    /// 地面那个箭头 (<c>DisplayArrowOnGround</c>) 是它自己画的, 也就是玩家说的
    /// "蓝色路径点"。
    /// </summary>
    private void OpenTheLedge(GameManager simulation)
    {
        List<PushPlayerOverLedge> ledges = simulation.GameMode?.GeneralPushPlayerTriggers;

        if (ledges == null)
        {
            return;
        }

        int opened = 0;

        for (int i = 0; i < ledges.Count; i++)
        {
            if (ledges[i] == null || ledges[i]._0023j0b)
            {
                continue;   // 开局就开着的不动它
            }

            ledges[i]._0023j0b = true;
            opened++;

            Events.Add($"跳台已开 @ ({ledges[i]._0023B.X:F0},{ledges[i]._0023B.Y:F0})");
        }

        if (opened == 0 && ledges.Count == 0)
        {
            Events.Add("这张图没有跳台");
        }
    }

    /// <summary>
    /// 收尾: 状态翻成完成、抛补给、把小地图路径点挪到下一步 (回卡车)。
    /// </summary>
    private void Complete(GameManager simulation)
    {
        Completed = true;
        StopChannel();
        SetState(simulation, InteractionState.Finished);

        // ## 偷卡车不抛箱子, 占领才抛
        //
        // 偷的那条现在是**边偷边入账**的 (见 HandOverSupplies), 车上那一车
        // 已经在玩家身上了; 再抛十包等于凭空多出一车。
        //
        // 占领那条照旧 —— 玩家实测"占领成功掉落大量物资箱"。
        bool thrown = IsCapture && SupplyThrower.Throw(
            simulation, _points[0].Position, CrateCount, SuppliesPerCrate);

        // 最后一次入账: 进度按帧涨, 最后那一小截取整会掉几个。
        HandOverSupplies(simulation, 1f);

        // 偷完才开跳台 —— 那是通往下一片区域的口子, 见 OpenTheLedge。
        if (OpensLedges)
        {
            OpenTheLedge(simulation);
        }

        // 下一步路径点: 回自己的卡车。
        //
        // 三张图的对话点里都另有一组 ScoutReturnToTruck —— 那是原厂标出来的
        // 收尾目标。位置取地图上玩家那一侧的 TruckerSpawnPoint。
        Vector2? truck = simulation.MapObjects
            .OfType<TruckerSpawnPoint>()
            .Select(t => (Vector2?)t.Position)
            .FirstOrDefault();

        if (truck.HasValue && PointsBackToTruck && simulation.GameMode is ScoutMission scout)
        {
            scout._0023gi._0023Lmd.Clear();
            scout._0023gi._0023Lmd.Add(new _0023Hmd
            {
                _0023Gmd = _0023qmd._0023mmd,
                _0023B = truck.Value,
            });
        }

        Events.Add((IsCapture ? "补给点占领完成" : $"物资偷完 (到手 {_handedOver})")
                   + " —— "
                   + (IsCapture
                       ? (thrown ? $"抛出补给 {CrateCount} 包 × {SuppliesPerCrate}" : "抛撒器没拿到 (池空?)")
                       : "补给已装在身上")
                   + (truck.HasValue ? ", 路径点已指向卡车" : ", **找不到卡车位置**"));
    }

    /// <summary>最近的活玩家离点多远 —— 只给日志用, 真机上照这个数校准半径。</summary>
    private float NearestPlayerDistance(GameManager simulation)
    {
        float best = float.MaxValue;

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player != null && player.IsActive && !player.IsDead)
            {
                best = Math.Min(best, Vector2.Distance(player.Position, _points[0].Position));
            }
        }

        return best;
    }

    /// <summary>上一帧圈里几个人。只用来在人数变化时打一条日志。</summary>
    private int _lastHolders;

    /// <summary>圈里有几个活着的玩家。</summary>
    private int CountHolders(GameManager simulation)
    {
        int count = 0;

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player != null && player.IsActive && !player.IsDead
                && InRange(player, _points[0].Position))
            {
                count++;
            }
        }

        return count;
    }

    private bool InRange(Entity who, Vector2 point)
    {
        float radius = ActiveRadius;
        return Vector2.DistanceSquared(who.Position, point) <= radius * radius;
    }

    /// <summary>这张图"在圈里"按哪个半径算。见 <see cref="CaptureRadius"/>。</summary>
    public float ActiveRadius => IsCapture ? CaptureRadius : HoldRadius;

    /// <summary>
    /// 圈里有这么多人时的速度倍率。偷卡车恒为 1。
    ///
    /// 公开是为了测试: Scout 是单人任务, 测试夹具在 Scout 图上取不到第二个
    /// 真玩家 (第二次取池拿到的是 dummy, Spawn 了也不上场), 两人那条只能按公式测。
    /// </summary>
    public float SpeedFor(int holders) =>
        IsCapture ? CaptureSpeed.For(holders) : 1f;

    /// <summary>
    /// 按进度门槛刷波 —— 门槛均分, 3 波就是 0% / 33% / 66%。
    ///
    /// 和补给点那边同一个做法 (StorageRoomCaptureDirector.FireWavesUpTo),
    /// 这样"刷怪和圆环对得上"的手感一致。
    /// </summary>
    private void FireWavesUpTo(GameManager simulation, float before)
    {
        while (_wavesFired < Waves)
        {
            float threshold = (float)_wavesFired / Waves;

            if (before > threshold || _points[0].Progress < threshold)
            {
                break;
            }

            _wavesFired++;
            int spawned = SpawnWave(simulation);

            Events.Add($"警报第 {_wavesFired}/{Waves} 波: 刷出 {spawned} 只 "
                       + $"(进度 {_points[0].Progress * 100f:F0}%)");
        }
    }

    private int SpawnWave(GameManager simulation)
    {
        Type walker = WalkerType();
        if (walker == null)
        {
            return 0;
        }

        int spawned = 0;
        int start = (_wavesFired - 1) * ZombiesPerWave;

        for (int i = 0; i < ZombiesPerWave; i++)
        {
            Vector2 at;
            _0023Oq._0023WM state = _0023Oq._0023WM._0023Yc;
            Vector2 facing = Vector2.UnitX;

            if (_spawns.Count > 0)
            {
                Spawn_EventEntities point = _spawns[(start + i) % _spawns.Count];
                at = point.Position;
                state = point.SpawnState;
                facing = point.Direction;
            }
            else
            {
                // 地图没给刷怪位就围着交互点铺一圈 —— 警报响了得有怪来,
                // 空着比位置不完美糟得多。
                //
                // 实测这两张图**一个刷怪位都没有** (日志: "警报刷怪位 0 个"),
                // 所以走的一直是这条。
                double angle = 2.0 * Math.PI * i / Math.Max(1, ZombiesPerWave);
                at = _points[0].Position
                     + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * FallbackSpawnRadius;

                // **从地里爬出来**, 不是凭空出现。
                //
                // 玩家实测: "刷怪也不是凭空出现, 而是从附近的地里爬出来的"。
                // 上一版这里给的是 _0023Yc ("没有出场动画"), 于是怪直接闪现。
                //
                // _0023eCc 就是挖地而出那个状态 —— <c>ZombieBrain.FinishEmerge</c>
                // 会等 <c>SpawnAnimationDuration</c> 放完才放它们行动, 那一段
                // 正好是爬出来的过程。
                state = _0023Oq._0023WM._0023eCc;

                // 脸朝交互点 —— 爬出来就该冲着玩家。
                facing = (_points[0].Position - at);
                if (facing != Vector2.Zero)
                {
                    facing.Normalize();
                }
            }

            Character zombie = ZombieFactory.Spawn(simulation, walker, at, state, facing);
            if (zombie == null)
            {
                continue;
            }

            SpawnedThisTickList.Add(zombie);
            spawned++;
        }

        return spawned;
    }

    /// <summary>地图没给刷怪位时, 围着交互点多远铺一圈。**我们定的**。</summary>
    private const float FallbackSpawnRadius = 45f;

    /// <summary>
    /// Walker 的运行时类型 —— 按可读名从 <see cref="ZombieFactory.Kinds"/> 反查。
    /// </summary>
    private Type WalkerType()
    {
        foreach ((string obfuscated, string readable) in ZombieFactory.Kinds)
        {
            if (readable == "Walker" && _types.TryGetValue(obfuscated, out Type type))
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>
    /// 改状态并写回 <c>ScoutMission</c> 那份 —— 同步走的是它。
    ///
    /// <c>InteractionPoint</c> 是 <b>struct</b>, 列表里存的是副本。改本地那份
    /// 不会影响发出去的东西 —— 必须整个写回去。
    /// </summary>
    private void SetState(GameManager simulation, InteractionState state)
    {
        InteractionPoint point = _points[0];
        point.State = state;
        _points[0] = point;

        Push(simulation, point);
        SetMinimap(simulation, state);
    }

    /// <summary>
    /// 写进度。**两个参数都喂** —— 特效那边到底读哪一个我们看不到。
    ///
    /// <c>UnityRaidEventView.Update</c> (:116-120) 把
    /// <c>point.Progress</c> 喂给特效的 "Progress" 参数, 把
    /// <c>point.Timer</c> 喂给 "Timer"。两个参数分别控制什么, 只有那个
    /// 特效预制体自己知道 —— 反编译树里拿不到。
    ///
    /// 玩家实测: "偷取物资时车上的物资模型也应该会相应的减少", 目前没减。
    /// Progress 一直在喂 (进度圈是它画的), 所以剩下的可能性是车上那堆箱子读的
    /// 是 Timer。<c>Timer</c> 先前一直是空的 (<c>Optional</c> 没值 ->
    /// <c>TrySetFloatParameter</c> 压根不调), 那它就停在预制体的默认值上,
    /// 也就是"满车"。
    ///
    /// 喂**剩余比例** (1 -&gt; 0): "还剩多少" 是箱子数量最自然的语义,
    /// 而且收尾时 <c>UnityRaidEventView</c> 自己会把 Progress 和 Timer 都置 1
    /// (:143-144), 说明这两个数在它眼里同向。⚠️ **这是猜的** ——
    /// 读的不是 Timer 的话, 那堆箱子就不是服务端能驱动的。
    /// </summary>
    private void SetProgress(GameManager simulation, float progress)
    {
        InteractionPoint point = _points[0];
        point.Progress = progress;
        point.Timer.SetValue(1f - progress);
        _points[0] = point;

        Push(simulation, point);
    }

    private static void Push(GameManager simulation, InteractionPoint point)
    {
        if (simulation.GameMode is not IRaidEventGameMode)
        {
            return;
        }

        List<InteractionPoint> synced = Tables(simulation)._0023Kmd;

        for (int i = 0; i < synced.Count; i++)
        {
            if (synced[i].ID == point.ID)
            {
                synced[i] = point;
                return;
            }
        }
    }

    /// <summary>
    /// 读条期间把小地图图标换成"正在偷卡车"那个闪烁。
    ///
    /// <c>_00236ar</c> 在客户端 (<c>GameManagerClient._0023ujd</c>) 翻成
    /// <c>MiniMapIconType.StealingTruckFlash</c> —— 名字就是为这一刻起的。
    /// 占领那张图没有对应的专用图标, 用 <c>_00235ar</c>(StorageRoomFlash)。
    /// </summary>
    private void SetMinimap(GameManager simulation, InteractionState state)
    {
        if (simulation.GameMode is not IRaidEventGameMode || Tables(simulation)._0023Lmd.Count == 0)
        {
            return;
        }

        _0023qmd icon = state != InteractionState.Progress
            ? _0023qmd._0023mmd
            : (IsCapture ? _0023qmd._00235ar : _0023qmd._00236ar);

        List<_0023Hmd> markers = Tables(simulation)._0023Lmd;
        _0023Hmd marker = markers[0];
        marker._0023Gmd = icon;
        markers[0] = marker;
    }

    private readonly List<Spawn_EventEntities> _spawns = new List<Spawn_EventEntities>();
    private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
    private int _wavesFired;

    /// <summary>身上挂着引导的那个人。见 <see cref="StartChannel"/>。</summary>
    private Character _channelOwner;

    /// <summary>这次引导还剩多久走满 —— 用来分辨"走满"和"被打断"。</summary>
    private float _channelLeft;

    /// <summary>这次压根没挂上引导 (对象池空)。那就别按引导那套判。</summary>
    private bool _noChannel;

    /// <summary>已经从车上搬到玩家身上多少。见 <see cref="HandOverSupplies"/>。</summary>
    private int _handedOver;

    /// <summary>摇旗那 5 秒过去了吗 (只有占领那张图有这一段)。</summary>
    private bool _windUpDone;

    /// <summary>正停着 (没人在圈里)。只用来让日志不刷屏。</summary>
    private bool _paused;

    /// <summary>
    /// 这种目标该摆什么交互点。
    ///
    /// 对应关系不是我们编的, 是 <c>raideventsettings.data</c> 里每种
    /// <see cref="RaidEventType"/> 自己声明的 <c>Interactions[].Type</c>。
    /// </summary>
    /// <returns>false = 这种目标没有交互点 (ScoutKillLooter 那张靠杀 Boss)。</returns>
    private static bool TypeFor(RaidEventType objective, out InteractionPointType type)
    {
        switch (objective)
        {
            case RaidEventType.ScoutStealSupplies:
                type = InteractionPointType.GrapSupplies;
                return true;

            case RaidEventType.ScoutCaptureFlag:
                type = InteractionPointType.Flag;
                return true;

            default:
                type = default;
                return false;
        }
    }

    /// <summary>
    /// 第一个交互点的 ID。
    ///
    /// 这个数会被客户端当成 <c>abilityId</c> 回传
    /// (<c>SendStartInteraction(charIndex, point.ID)</c>), 所以只要在这一局里
    /// 唯一就行 —— 不和任何技能 ID 冲突, 那两条路是分开的
    /// (<c>ActionName.GenericInteraction</c> 走 SendStartInteraction,
    ///  其余走 AbilityBar.SetAbilityPressed)。
    ///
    /// 从 1 起 —— 0 留给"没有"。
    /// </summary>
    private const ushort FirstPointId = 1;
}
