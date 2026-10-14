using System;
using System.Collections.Generic;
using System.Linq;
using _0023z;
using ConductorCrafting;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.MapObjects;
using ConductorGameLogic.Stats;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 拾荒 (Scavenger, 4v4v4) 的局内流程 —— 推进 <c>ScavengerHunt._0023l9</c> (模式状态数据)。
///
/// ## 原厂留下了什么
///
/// <c>ScavengerHunt</c> 本身双端共享, **客户端那一半是完整的**: 五个同步器把 <c>_0023l9</c> 发下去,
/// 客户端 <c>_0023E9</c> 拿新旧两份比较, 按差异加/完成任务目标、播 Sinamoi 的语音、切昼夜、放公告。
/// 捡补给 / 交卡车 (-Sq/-ajc) / 偷别队卡车 (-Sq/-fdb) / 卡车炮塔 (-EK/-Zuc) / 补给点读条也都在共享代码里。
/// 缺的是**推进状态**的服务端: 那几个空 virtual (<c>_0023Wub/_0023Xub/_0023Yub/_0023kvb/_0023si</c>) 的实现。
///
/// ## 状态数据的语义 (-z/-Ctb.cs, 从客户端的反应倒推)
///
///   每队 _00239sb[队下标]:
///     _00230sb  阶段  0 等待 / 1 清出生区 (目标 ClearArea, 播开场语音) / 2 冲出去了 (目标 CaptureIncome + DeliverTruck, 转白天)
///               GetSpawnPosition 按它挑复活点: 没到 2 用起点, 到 2 用卡车旁那个
///     _00231sb  第一道路障  0 没开始 / 1 在打 (目标 DestroyBarricade) / 2 打破
///     _00232sb  第二道路障  同上
///     _00233sb  当前路障剩余血量百分比 (0..100, 目标栏上的数字)
///     _00234sb  音乐档 (1 = SetMusicLevel, 2 = SetMusicNightFallLevel)
///   _0023ctb    入夜 (新版终局): 卡车炮塔断电 (-EK/-Zuc.cs:248)、僵尸等级顶到 15 (GameManager.cs:2951)、转夜晚
///   _0023dtb    快赢了的队 (变成某队 = 公告 + "CloseSupply" 语音; 从某队变回 Neutral = "XX Denied")
///   _0023L7     GameOver
///   _0023atb 及五个数组   补给点 (见 <see cref="ScavengerStorageRooms"/>)
///   _0023btb / _0023ftb  动态事件 (囤积者), 见 <see cref="ScavengerHoarders"/>
///
/// ## 流程 (阶段语义是原厂的; 触发条件和数值标了"我们定的")
///
///   开局  每队进阶段 1, 第一道路障进 1。
///   路障  打破一道开下一道; 最后一道破了 -&gt; 阶段 2 (冲出去)。每道路障的埋伏刷怪由 <see cref="AmbushDirector"/> 管。
///         冲出去的名次: 第一名全队挂 <c>_0023Kuc</c> (原厂 "BarricadeBuff", 攻击 + 最大生命) 加
///         <c>BalanceData._0023j7f._00231tb</c> (0.2), 第二名加 <c>_00233tb</c> (0.1) —— 这两个数就是客户端公告
///         "CoreStatsBuff {percent}" 显示的, 所以 buff 的量照它给。
///   入夜  (新版终局 <c>UseNewEndGameState</c>, 五张图全是 true) 第一支交满卡车 (<c>_0023Fub</c>) 的队出现时入夜。
///         超出 <c>_0023Fub</c> 的部分别的队可以偷 (共享代码 -Sq/-fdb.cs 自己判)。**入夜的触发是我们定的**。
///   胜负  先交到 <c>_0023jj</c> 的队赢; 其余按已交数量排第二、第三。
///
/// 每张图的两个数是原厂的 (<c>ScavengerHunt._0023Zub</c>): Resort 500/800, Jungle 600/900, Expedition 500/800,
/// Town 500/800, Village 450/700。
/// </summary>
public sealed class ScavengerDirector
{
    /// <summary>
    /// 冲出去第一名 / 第二名的 buff 持续多久。原厂平衡数据
    /// <c>_00230tb</c> / <c>_00232tb</c> (都是 120 秒) —— 和加成的那两个
    /// <c>_00231tb</c>(0.2) / <c>_00233tb</c>(0.1) 是成对的, 所以一起读平衡数据。
    /// buff 类 <c>_0023Kuc</c> 自己的默认值也是 120 (-Sq/-Kuc.cs:50)。
    /// </summary>
    public float RaceBuffSeconds
    {
        // 懒取: 平衡数据在 GameManager 起来之后才有, 构造这个导演时可能还没有。
        get => _raceBuffSeconds ?? GameManager.BalanceData?._0023j7f?._00230tb ?? 120f;
        set => _raceBuffSeconds = value;
    }

    private float? _raceBuffSeconds;

    private sealed class Barricade
    {
        public int GroupId;
        public BarricadeType Type;
        public readonly List<Vector2> Pieces = new List<Vector2>();
        public float Distance;
        public _0023EK._00235xc Group;
        public bool Scaled;

        /// <summary>改成拾荒血量之后的满血值 (判"开打了没有"用, 见 <see cref="BarricadeFightStarted"/>)。</summary>
        public float FullHealth;
    }

    private sealed class TeamState
    {
        public TeamId Team;
        public Vector2 Start;
        public readonly List<Barricade> Barricades = new List<Barricade>();
        public int Current;
        public bool Out;

        /// <summary>最后一道路障破了, 还剩几秒才切到"冲出去" (见 <see cref="BreakOutDelaySeconds"/>); &lt; 0 = 没在等。</summary>
        public float BreakOutIn = -1f;

        /// <summary>冲出去之后过了几秒 (入夜要等这个过了同步余量, 见 UpdateNight)。</summary>
        public float SinceOut;
    }

    /// <summary>
    /// 最后一道路障破了之后, 隔多久才把阶段切到"冲出去"。
    ///
    /// ## 为什么不能同一帧切
    ///
    /// 模式数据的序列化 (-z/-Ctb.cs <c>_0023otb</c>) 对"冲出去了"的队**不写路障状态**, 客户端也就不读 ——
    /// 同一帧把路障置 2、阶段置"冲出去", 客户端永远收不到"路障打破", 目标栏上那条
    /// "摧毁路障 1%" 就完成不了。玩家实测 (2026-09-17): "我把路障打破了, 结果右边还是显示完成 1%"。
    /// 同步器每秒发一次, 留 2 秒保证至少发出去一帧"路障 2 / 阶段还是清出生区"。
    /// </summary>
    public const float BreakOutDelaySeconds = 2f;

    private readonly List<TeamState> _teams = new List<TeamState>();
    private readonly List<(TeamId Team, Vector2 Position)> _trucks = new List<(TeamId, Vector2)>();
    private float _closeTimer;
    private readonly List<TeamId> _raceOrder = new List<TeamId>();
    private bool _scanned;
    private bool _started;

    /// <summary>补给点 (占领 / 收入 / 奖励补给)。</summary>
    public ScavengerStorageRooms StorageRooms { get; } = new ScavengerStorageRooms();

    /// <summary>囤积者 Boss 事件。</summary>
    public ScavengerHoarders Hoarders { get; } = new ScavengerHoarders();

    /// <summary>Looter (小型囤积者) 事件, 和囤积者共用动态事件点。</summary>
    public ScavengerLooters Looters { get; } = new ScavengerLooters();

    /// <summary>入夜的基地封锁 (LockDownGauntlet)。</summary>
    public ScavengerLockdown Lockdown { get; } = new ScavengerLockdown();

    /// <summary>拾荒的 Steam 成就。</summary>
    public ScavengerAchievements Achievements { get; } = new ScavengerAchievements();

    /// <summary>某队基地夜袭还剩几只没打完 (宿主接 <see cref="EventSpawnDirector.RemainingFor"/>; 封锁据此开门)。</summary>
    public Func<TeamId, int> NightAssaultRemaining { get; set; }

    /// <summary>这一局是拾荒。</summary>
    public bool IsArmed => _teams.Count > 0;

    /// <summary>冲出去的先后 (第一个就是路障竞速的赢家)。</summary>
    public IReadOnlyList<TeamId> RaceOrder => _raceOrder;

    /// <summary>第一支交满卡车的队 (入夜的那一刻); 还没有是 Neutral。</summary>
    public TeamId FillTruckTeam { get; private set; } = TeamId.Neutral;

    /// <summary>赢家; 没分出胜负是 Neutral。</summary>
    public TeamId Winner { get; private set; } = TeamId.Neutral;

    /// <summary>各队名次 (分出胜负后才有)。</summary>
    public IReadOnlyDictionary<TeamId, Placement> Placements => _placements;

    private readonly Dictionary<TeamId, Placement> _placements = new Dictionary<TeamId, Placement>();

    /// <summary>
    /// 入夜的那一刻 —— 宿主接这个强制触发各队基地的夜袭 (见 <see cref="IsNightAssault"/>)。参数是有玩家的队。
    /// </summary>
    public Action<GameManager, IReadOnlyCollection<TeamId>> OnNightFall { get; set; }

    /// <summary>
    /// 这个 <c>Event_Assault</c> 是不是某队基地的夜袭。
    ///
    /// 认原厂自己的标志 <c>Event_Assault.EndGameCinematic</c> —— 客户端一处都不读它
    /// (= 服务端用的)。五张图共 71 个 Assault 里只有 14 个勾了它, 全是摆在卡车旁的
    /// <c>cd=99999 / r=25 / 30 行尸 + Elites</c> 那个模子 (Resort/Jungle/Expedition/Town 各 3,
    /// Village 2); 十字路口 39 张图每张那一个基地夜袭也全勾着它。
    ///
    /// 之前用的是"<c>Cooldown &gt;= 9999</c> + 离卡车 250 以内"这个推断 (09-18 审校前),
    /// 实测两者在五张图上集合完全重合 —— 换成明文标志后行为不变, 只是不再依赖两个魔法数。
    ///
    /// 卡车距离还留着, 但只用来定**属于哪一队**的基地 (实测 40~190), 不再当认定条件。
    ///
    /// 玩家实测 (2026-09-17): "带着物资回基地, 突然把夜袭的大量僵尸刷出来了, 但是到夜袭的时候又没有刷"
    /// —— 以前它按普通事件走, 玩家踩进 25 的圈就炸。
    /// </summary>
    public bool IsNightAssault(Event_Assault assault) => assault != null && assault.EndGameCinematic;

    /// <summary>夜袭属于哪一队的基地 (按离哪辆卡车最近); 不是夜袭返回 Neutral。</summary>
    public TeamId NightAssaultTeam(Event_Assault assault)
    {
        if (!IsNightAssault(assault) || _trucks.Count == 0)
        {
            return TeamId.Neutral;
        }

        return _trucks.OrderBy(t => Vector2.DistanceSquared(t.Position, assault.Position)).First().Team;
    }

    /// <summary>事件刷怪的闸门 (宿主挂到 EventSpawnDirector.Allow): 拾荒的夜袭不走"走近触发", 只在入夜时强制刷。</summary>
    public bool AllowAssault(Event_Assault assault) => !IsArmed || !IsNightAssault(assault);

    /// <summary>
    /// 勾了 <c>HighLevelBarricadeSpawn</c> 的事件 / 漫游点现在准不准刷
    /// (宿主挂到 <see cref="EventSpawnDirector.AllowHighLevelBarricade"/> 和
    /// <see cref="RoamingZombieDirector.AllowHighLevelBarricade"/>)。
    ///
    /// **语义是推断**: 字面意思"高级路障的刷怪", 拾荒每队两道路障,
    /// 读成"离它最近的那队打到最后一道路障, 或者已经冲出去了"。
    /// 这批事件就摆在出生区往外的路上 (离卡车 193~468), 早了等于在第一道路障
    /// 还没破时就往出生区门口塞怪。
    ///
    /// 地图上没有队伍信息时 (没扫到队) 一律放行 —— 别的模式不接这个钩子。
    /// </summary>
    public bool AllowHighLevelBarricadeSpawn(Vector2 position)
    {
        if (_teams.Count == 0)
        {
            return true;
        }

        TeamState team = _teams.OrderBy(t => Vector2.DistanceSquared(t.Start, position)).First();
        return team.Out || team.Barricades.Count == 0 || team.Current >= team.Barricades.Count - 1;
    }

    /// <summary>
    /// 路障旁的刷怪点 (<c>Spawn_RoamingZombies.BarricadeSpawn</c>) 现在该不该刷:
    /// 离它最近的那道开局路障**开始掉血了, 或者已经破了**。
    ///
    /// ## 为什么 (玩家 2026-09-23 定)
    ///
    /// "现在野外刷怪是大片的同时刷怪, 一大群僵尸从地里面钻出来。" 五张图上 1/4 到一半的漫游点勾着
    /// <c>BarricadeSpawn</c>, 刷怪量是固定的大数 (一个点 5~12 只), 我们一直把它们当普通野外点、
    /// 玩家走近就整批钻出来。原厂字段客户端一处不读 (= 服务端用的); 字面是"路障的刷怪", 于是定为:
    /// 只在附近那道路障开打时刷一次, 刷出来冲打路障的人。
    ///
    /// ## 怎么配对 (实测)
    ///
    /// 大部分点离某道开局路障 40~90 单位; Jungle / Expedition / Town / Village 每队另有一个点在
    /// 第一道路障外 170~410 的路上 (不靠近封锁门, 也不靠近别的路障) —— 读成"路障打开后从外面冲进来的"。
    /// 一律按"离哪道开局路障最近"配对, 也就定了是哪一队的。
    /// </summary>
    public bool BarricadeFightStarted(Vector2 point)
    {
        if (!_barricadeOfPoint.TryGetValue(point, out (TeamState Team, int Index) owner))
        {
            owner = (null, -1);
            float best = float.MaxValue;
            foreach (TeamState team in _teams)
            {
                for (int i = 0; i < team.Barricades.Count; i++)
                {
                    foreach (Vector2 piece in team.Barricades[i].Pieces)
                    {
                        float distance = Vector2.DistanceSquared(piece, point);
                        if (distance < best)
                        {
                            best = distance;
                            owner = (team, i);
                        }
                    }
                }
            }

            _barricadeOfPoint[point] = owner;
        }

        if (owner.Team == null)
        {
            return false;
        }

        if (owner.Team.Out || owner.Index < owner.Team.Current)
        {
            return true;     // 这道已经破了
        }

        Barricade barricade = owner.Team.Barricades[owner.Index];
        return barricade.Group != null && barricade.FullHealth > 0f
               && barricade.Group.Health < barricade.FullHealth - 0.5f;
    }

    private readonly Dictionary<Vector2, (TeamState Team, int Index)> _barricadeOfPoint =
        new Dictionary<Vector2, (TeamState, int)>();

    /// <summary>
    /// 快赢了之后的倒计时 (秒)。原厂 <c>BalanceData._0023j7f._0023qub</c> = 15 ——
    /// 客户端在"CloseSupply"公告上显示的就是它 (ScavengerHunt._00236ub 里 <c>item._00231Ks = _0023qub</c>)。
    /// 倒计时走完那队还在"快赢了"的状态就判它赢 (中途被杀、补给被抢, 状态掉了就是 "Denied")。**判胜这一步是推断。**
    /// </summary>
    public static float CloseToWinSeconds => GameManager.BalanceData._0023j7f._0023qub;

    /// <summary>一局打完 —— 宿主接这个发结算 (在 TeamFinished 之前)。</summary>
    public Action<GameManager> OnMatchOver { get; set; }

    /// <summary>本 tick 发生的事, 供宿主打日志。每次 Update 开头清空。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>队伍在地图上的数量 (有起点的队)。</summary>
    public int TeamCount => _teams.Count;

    /// <summary>某队现在打到第几道路障 (0 起); 冲出去了等于路障数。测试 / 诊断用。</summary>
    public int CurrentBarricade(TeamId team) => _teams.FirstOrDefault(t => t.Team == team)?.Current ?? -1;

    /// <summary>某队第 <paramref name="index"/> 道路障的所有段 (测试用)。</summary>
    public IReadOnlyList<Vector2> BarricadePieces(TeamId team, int index)
    {
        TeamState state = _teams.FirstOrDefault(t => t.Team == team);
        return state == null || index < 0 || index >= state.Barricades.Count
            ? Array.Empty<Vector2>()
            : state.Barricades[index].Pieces;
    }

    /// <summary>
    /// 扫地图: 每队的起点和自己那几道路障 (按离起点由近到远排, 近的先打)。开局调一次。
    ///
    /// 路障认 <c>Spawn_Destructible.Team</c> 和 <c>GroupID</c>; 地图上每组路障配一个同 ID 的 <c>Event_Ambush</c>
    /// (探针实测五张图都是)。<c>LockDownGauntletBlockage</c> (GroupID 101..103, DynamiclySpawned) 不是要打的路障。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.GameMode is not ScavengerHunt || simulation.MapObjects == null)
        {
            return;
        }

        _scanned = true;

        foreach (PlayerSpawnPoint point in simulation.MapObjects.OfType<PlayerSpawnPoint>())
        {
            if (point.IsStart && point.Team != TeamId.Neutral && _teams.All(t => t.Team != point.Team))
            {
                _teams.Add(new TeamState { Team = point.Team, Start = point.Position });
            }
        }

        _teams.Sort((a, b) => a.Team.CompareTo(b.Team));

        foreach (TruckerSpawnPoint truck in simulation.MapObjects.OfType<TruckerSpawnPoint>())
        {
            _trucks.Add((truck.Team, truck.Position));
        }

        foreach (IGrouping<int, Spawn_Destructible> group in simulation.MapObjects
                     .OfType<Spawn_Destructible>()
                     .Where(d => !d.DynamiclySpawned && IsRaceBarricade(d.BarricadeType))
                     .GroupBy(d => d.GroupID))
        {
            Spawn_Destructible first = group.First();
            TeamState team = _teams.FirstOrDefault(t => t.Team == first.Team);
            if (team == null)
            {
                continue;
            }

            Barricade barricade = new Barricade { GroupId = group.Key, Type = first.BarricadeType };
            barricade.Pieces.AddRange(group.Select(d => d.Position));
            barricade.Distance = Vector2.Distance(Center(barricade.Pieces), team.Start);
            team.Barricades.Add(barricade);
        }

        foreach (TeamState team in _teams)
        {
            team.Barricades.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        StorageRooms.Scan(simulation);
        Hoarders.Scan(simulation);
        Lockdown.Scan(simulation, _trucks);
        Hoarders.IsPointBusy = id => id == Looters.OccupiedPointId;

        Events.Add("拾荒已登记: " + string.Join(", ", _teams.Select(t =>
            $"{t.Team} 路障 {t.Barricades.Count} 道 ({string.Join("/", t.Barricades.Select(b => $"{b.GroupId}:{b.Type}"))})"))
            + $", 补给点 {StorageRooms.ArmedRooms} 个, 囤积者点 {Hoarders.ArmedPoints} 个, 基地封锁门 {Lockdown.Gates.Count} 道");
    }

    /// <summary>生成路障时跳过哪些 (宿主传给 <see cref="DestructibleSpawner.SpawnAll"/>): 拾荒图上的 LockDownGauntletBlockage。</summary>
    /// <remarks>
    /// 它们标着 <c>DynamiclySpawned</c>, 开局不该在场 —— 那种路障只挡玩家不挡僵尸 (Entity.cs:358), 开局就摆出来会把
    /// 玩家堵在起点。什么时候该放下来 (原厂叫 "LockDown Gauntlet") 没有线索, 先不放。
    /// </remarks>
    public static bool SkipAtStart(GameManager simulation, Spawn_Destructible spawn) =>
        simulation?.GameMode is ScavengerHunt && spawn.DynamiclySpawned;

    public void Update(GameManager simulation, GameServer network, float deltaTime)
    {
        Events.Clear();

        if (!_scanned || _teams.Count == 0 || simulation?.GameMode is not ScavengerHunt hunt)
        {
            return;
        }

        // 分出胜负之后只推结束过场, 放完再结算 (见 RunEnding)
        if (Winner != TeamId.Neutral)
        {
            if (_ending)
            {
                RunEnding(simulation, network, hunt, deltaTime);
            }

            return;
        }

        if (!_started)
        {
            Begin(simulation, hunt);
        }

        BindBarricades(simulation, hunt);

        foreach (TeamState team in _teams)
        {
            if (team.Out)
            {
                team.SinceOut += deltaTime;
            }

            AdvanceTeam(simulation, hunt, team, deltaTime);
        }

        StorageRooms.Update(simulation, hunt, deltaTime);
        Events.AddRange(StorageRooms.Events);

        if (simulation is GameManagerServer server)
        {
            Hoarders.Update(server, hunt, _raceOrder.Count > 0, deltaTime);
            Events.AddRange(Hoarders.Events);
            Looters.Update(server, hunt, Hoarders.Points, Hoarders.OccupiedPointId, _raceOrder.Count > 0, deltaTime);
            Events.AddRange(Looters.Events);
        }

        Lockdown.Update(deltaTime, NightAssaultRemaining);
        Events.AddRange(Lockdown.Events);
        Achievements.WatchControlledPoints(simulation, StorageRooms, _teams.Select(t => t.Team));
        Events.AddRange(Achievements.Events);
        Achievements.Events.Clear();

        RewardFullTrucks(simulation, hunt);
        UpdateNight(simulation, hunt, deltaTime);
        if (UpdateCloseToWin(simulation, network, hunt, deltaTime))
        {
            return;
        }

        CheckWinner(simulation, network, hunt);
    }

    private void Begin(GameManager simulation, ScavengerHunt hunt)
    {
        _started = true;

        foreach (TeamState team in _teams)
        {
            ref _00238sb data = ref TeamData(hunt, team.Team);
            if (team.Barricades.Count == 0)
            {
                data._00230sb = _0023AKs._0023zKs;
                team.Out = true;
                continue;
            }

            data._00230sb = _0023AKs._0023yKs;
            data._00231sb = 1;
            data._00233sb = 100;
        }

        Events.Add($"拾荒开局: {_teams.Count} 队进入出生区, 卡车满 {hunt._0023Fub} / 胜利 {hunt._0023jj}");
    }

    /// <summary>
    /// 把地图上的路障组认到场上的实体 (按段的位置配), 顺便把血量改成拾荒的值。
    /// 实体是开局时 <see cref="DestructibleSpawner"/> 放的, 组对象 (<c>_0023Exc</c>) 管共享血量。
    /// </summary>
    private void BindBarricades(GameManager simulation, ScavengerHunt hunt)
    {
        foreach (TeamState team in _teams)
        {
            foreach (Barricade barricade in team.Barricades)
            {
                if (barricade.Group != null)
                {
                    continue;
                }

                foreach (Entity entity in simulation.ActiveEntityList)
                {
                    if (entity is _0023EK._0023Yh piece && piece.IsActive && piece._0023Exc != null
                        && barricade.Pieces.Any(p => Vector2.DistanceSquared(p, piece.Position) < 1f))
                    {
                        barricade.Group = piece._0023Exc;
                        KeepSides(simulation, piece, team);
                    }
                }

                if (barricade.Group != null && !barricade.Scaled)
                {
                    barricade.Scaled = true;
                    float target = BarricadeHealth(simulation);
                    barricade.FullHealth = target;
                    float current = barricade.Group._0023oK;
                    if (current > 0f && Math.Abs(target - current) > 0.5f)
                    {
                        barricade.Group._00233xc(target / current);
                    }

                    // 开局生成那一行日志报的是通用表的血量, 这里才是拾荒的真值 —— 报出来免得看日志被误导
                    Events.Add($"拾荒 {team.Team} 路障 (组 {barricade.GroupId}) 血量 {current:F0} -> {target:F0} "
                               + $"(木门 {CrossroadStash.WoodenDoorBaseHealth:F0} × 感染乘数 {(simulation.ServerModifier > 0f ? simulation.ServerModifier : 1f):F2})");
                }
            }
        }
    }

    /// <summary>
    /// 路障的"哪边站哪队" (<c>_0023Yh._00238f</c>)。
    ///
    /// 原厂每帧对路障 10 单位内的玩家做一次穿模回拉 (-EK/-Yh.cs:545): 队伍 == <c>_00238f</c> 的要待在 -朝向 那一侧,
    /// 其余的待在 +朝向 一侧, 站错就拽回上一个合法位置。服务端从没设过它 (默认 Neutral) —— 等于"所有人都该在 +朝向 一侧"。
    ///
    /// 五张图实测: Resort / Jungle / Expedition / Village 的比赛路障朝向都背离出生点 (出生点在 -朝向 一侧),
    /// 默认值会把**本队**贴着自家路障的人往外拽; Town 的三道反过来 (出生点在 +朝向 一侧), 默认值恰好对。
    /// 所以按出生点实际在哪一侧定: -侧 = 本队, +侧 = 保持 Neutral (规则只能单列一队, 那时外面的敌人不受保护, 只在 10 单位内, 影响很小)。
    /// </summary>
    private static void KeepSides(GameManager simulation, _0023EK._0023Yh piece, TeamState team)
    {
        TeamId side = Vector2.Dot(team.Start - piece.Position, piece._0023Gt) < 0f ? team.Team : TeamId.Neutral;
        if (piece._00238f == side)
        {
            return;
        }

        piece._00238f = side;
        simulation.NetworkManager?.GetSyncHelper(piece.SyncHelperIndex)?._00234Ce();
    }

    /// <summary>
    /// 拾荒开局路障**一整道**的血量 = 十字路口补给设施木门的血量, 随感染等级走。
    ///
    /// ## 为什么不再查按类型的表
    ///
    /// 原厂的拾荒路障血量查不到 (客户端里那八个常量零读取方, 见 <see cref="DestructibleSpawner"/>
    /// 的类说明)。以前拾荒吃的是全模式公用表里的 Wooden = 147 / MilitaryFence = 420 ——
    /// 那是从 Scout 图视频里数出来的, 四个人几刀就破。玩家实测 (2026-09-23): "初始的路障血量太低",
    /// 定下: "路障的血量应该随着感染等级变动, 调整为和 crossroad 中 supply facility 的木门一样。"
    ///
    /// 于是 = <see cref="CrossroadStash.WoodenDoorBaseHealth"/> (800) × 感染等级乘数
    /// (<c>ServerModifier</c>, GameCraftingData 的 InfectionLevelIncrease: 1 级 1.0 … 17 级 7.52,
    /// 和补给设施的门同一个乘数) × 地图的 <c>BarricadeHitpointsModifier</c> (五张图都是 1)。
    /// 两种路障类型 (木栅栏 / 丛林的军用栅栏) 一样。
    /// </summary>
    public static float BarricadeHealth(GameManager simulation)
    {
        float infection = simulation?.ServerModifier > 0f ? simulation.ServerModifier : 1f;
        float map = Math.Max(0.01f, simulation?.GameModeSettings?.BarricadeHitpointsModifier ?? 1f);
        return CrossroadStash.WoodenDoorBaseHealth * infection * map;
    }

    private void AdvanceTeam(GameManager simulation, ScavengerHunt hunt, TeamState team, float deltaTime)
    {
        if (team.Out)
        {
            return;
        }

        ref _00238sb data = ref TeamData(hunt, team.Team);

        if (team.BreakOutIn >= 0f)
        {
            team.BreakOutIn -= deltaTime;
            if (team.BreakOutIn <= 0f)
            {
                BreakOut(simulation, hunt, team, ref data);
            }

            return;
        }

        Barricade current = team.Barricades[team.Current];
        if (current.Group == null)
        {
            return;
        }

        float max = Math.Max(1f, current.Group._0023oK);
        float health = current.Group.Health;
        data._00233sb = Math.Clamp((int)Math.Ceiling(health / max * 100f), 0, 100);

        if (health > 1f)
        {
            return;
        }

        // 这一道破了
        SetBarricadeState(ref data, team.Current, 2);
        data._00233sb = 0;
        Events.Add($"拾荒 {team.Team} 打破第 {team.Current + 1}/{team.Barricades.Count} 道路障 (组 {current.GroupId})");
        team.Current++;

        if (team.Current < team.Barricades.Count)
        {
            SetBarricadeState(ref data, team.Current, 1);
            data._00233sb = 100;
            return;
        }

        team.BreakOutIn = BreakOutDelaySeconds;
    }

    /// <summary>
    /// 两道路障的状态只有两个槽 (<c>_00231sb</c> / <c>_00232sb</c>); 地图上一队最多两道 (Resort), 多出来的算进第二槽。
    /// </summary>
    private static void SetBarricadeState(ref _00238sb data, int index, int state)
    {
        if (index == 0)
        {
            data._00231sb = state;
        }
        else
        {
            data._00232sb = state;
        }
    }

    private void BreakOut(GameManager simulation, ScavengerHunt hunt, TeamState team, ref _00238sb data)
    {
        team.Out = true;
        data._00230sb = _0023AKs._0023zKs;
        data._00233sb = 0;
        data._00234sb = 1;

        _raceOrder.Add(team.Team);
        int place = _raceOrder.Count;
        if (place == 1)
        {
            Achievements.OnBarricadeRaceWon(simulation, team.Team);
        }

        float bonus = place switch
        {
            1 => GameManager.BalanceData._0023j7f._00231tb,
            2 => GameManager.BalanceData._0023j7f._00233tb,
            _ => 0f,
        };

        int buffed = bonus > 0f ? ApplyRaceBuff(simulation, team.Team, bonus, place == 1) : 0;
        Events.Add($"拾荒 {team.Team} 冲出出生区, 第 {place} 名" + (buffed > 0 ? $" (全队 {buffed} 人 +{bonus * 100f:F0}% 攻击/生命 {RaceBuffSeconds:F0} 秒)" : string.Empty));
    }

    private int ApplyRaceBuff(GameManager simulation, TeamId team, float bonus, bool first)
    {
        int count = 0;
        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead || player.TeamId != team)
            {
                continue;
            }

            _0023Sq._0023Kuc buff = simulation.GetGameObjectFromPool<_0023Sq._0023Kuc>();
            if (buff == null || buff.IsDummyObject)
            {
                continue;
            }

            // 取出池子时已经 Reset 过 (默认 25% / 120 秒), 挂上去 (Spawn -> _0023Hq 按这几个数加属性) 之前改
            buff._0023tG = bonus;
            buff._0023lab = bonus;
            buff._0023mab = first;
            buff._0023fq = RaceBuffSeconds;
            simulation.ApplyBuff(buff, player, player, player);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 入夜: 第一支交满卡车 (<c>_0023Fub</c>) 的队出现时。只在新版终局 (<c>_0023Od</c>) 下有夜晚。
    /// </summary>
    /// <summary>
    /// 入夜最多等几秒 "还没冲出出生区的队" (见 <see cref="UpdateNight"/>)。**我们定的。**
    /// </summary>
    public float NightWaitForTeamsSeconds { get; set; } = 90f;

    private float _nightWait = -1f;

    /// <summary>已经入夜。</summary>
    public bool IsNight { get; private set; }

    /// <summary>
    /// 第一支交满卡车 (<c>_0023Fub</c>) 的队出现 -&gt; 记"先装满卡车"; 然后入夜。只在新版终局 (<c>_0023Od</c>) 下有夜晚。
    ///
    /// ## 入夜要等有人的队都冲出出生区
    ///
    /// 模式数据一入夜就只序列化 GameOver / 快赢的队 (-z/-Ctb.cs <c>_0023otb</c> 的 <c>if (_0023ctb)</c> 分支),
    /// **各队的阶段和路障状态不再发**。那时还在出生区的队, 之后打破路障、冲出去, 客户端一概收不到 ——
    /// 任务栏停在"摧毁路障", 和 09-17 "打破了还显示 1%" 同一个病。所以等有人的队都冲出去
    /// (含 <see cref="BreakOutDelaySeconds"/> 的同步余量) 再入夜; 最多等 <see cref="NightWaitForTeamsSeconds"/> 秒,
    /// 免得一队挂机把全场的终局拖住。
    /// </summary>
    private void UpdateNight(GameManager simulation, ScavengerHunt hunt, float deltaTime)
    {
        if (IsNight)
        {
            return;
        }

        if (FillTruckTeam == TeamId.Neutral)
        {
            TeamState full = _teams.FirstOrDefault(t => hunt.SupplyData._0023Rj(t.Team) >= hunt._0023Fub);
            if (full == null)
            {
                return;
            }

            FillTruckTeam = full.Team;
            _nightWait = NightWaitForTeamsSeconds;
            Events.Add($"拾荒 {full.Team} 卡车装满 ({hunt.SupplyData._0023Rj(full.Team)}/{hunt._0023Fub})");
        }

        if (!hunt._0023Od)
        {
            return;
        }

        List<TeamId> manned = MannedTeams(simulation);
        // 刚冲出去不到 BreakOutDelaySeconds 的也算: "冲出去了"那一帧得先发出去, 入夜之后就不发了
        List<TeamId> stillInside = _teams
            .Where(t => manned.Contains(t.Team) && (!t.Out || t.SinceOut < BreakOutDelaySeconds))
            .Select(t => t.Team)
            .ToList();
        _nightWait -= deltaTime;
        if (stillInside.Count > 0 && _nightWait > 0f)
        {
            return;
        }

        IsNight = true;
        hunt._0023l9._0023ctb = true;
        foreach (TeamState each in _teams)
        {
            TeamData(hunt, each.Team)._00234sb = 2;
        }

        // 夜袭只落在装满卡车的那一队 (玩家 2026-09-23 定) —— 封门也只封这一队。
        // 这队要是一个人都不在, 就没有夜袭。
        List<TeamId> raided = manned.Contains(FillTruckTeam) ? new List<TeamId> { FillTruckTeam } : new List<TeamId>();
        Lockdown.Engage(simulation, raided);
        Events.AddRange(Lockdown.Events);
        Events.Add("拾荒入夜 —— 超出卡车线的部分可以被偷"
                   + (stillInside.Count > 0 ? $" (等不到 {string.Join(",", stillInside)} 冲出去, 它们的任务栏之后不会再更新)" : string.Empty));
        OnNightFall?.Invoke(simulation, raided);
    }

    /// <summary>卡车先后装满的队 (经验奖励按这个先后给)。</summary>
    public IReadOnlyList<TeamId> TruckFullOrder => _truckFullOrder;

    private readonly List<TeamId> _truckFullOrder = new List<TeamId>();

    /// <summary>
    /// 卡车装满 (交到卡车线 <c>_0023Fub</c>) 的经验奖励: 第一个装满的队每人 <c>_0023nub</c> = 150, 第二个
    /// <c>_0023oub</c> = 125, 第三个 <c>_0023pub</c> = 100。
    ///
    /// 客户端每队装满时放公告 "TruckFull" + "XP Bonus {bonus}" (ScavengerHunt._00232ub / _00235ub), bonus 就是这三个数 ——
    /// 公告只是显示, 发经验的服务端丢了。按"之前已经装满了几队"挑档是照公告那段的意图
    /// (它的计数写在循环里每次归零, 实际永远显示 150, 那是客户端的 bug, 不照抄)。
    /// 只给装满那一刻在场的本队玩家。
    /// </summary>
    private void RewardFullTrucks(GameManager simulation, ScavengerHunt hunt)
    {
        if (hunt.IsCompleted)
        {
            return;
        }

        foreach (TeamState team in _teams)
        {
            if (_truckFullOrder.Contains(team.Team) || !hunt._0023svb(team.Team))
            {
                continue;
            }

            _truckFullOrder.Add(team.Team);
            float bonus = _truckFullOrder.Count switch
            {
                1 => GameManager.BalanceData._0023j7f._0023nub,
                2 => GameManager.BalanceData._0023j7f._0023oub,
                _ => GameManager.BalanceData._0023j7f._0023pub,
            };

            int rewarded = 0;
            foreach (Player player in simulation.ActivePlayerList)
            {
                if (player == null || !player.IsActive || player.TeamId != team.Team)
                {
                    continue;
                }

                simulation.ChangeStats(player, StatType.Experience, _00230k._0023UIc._0023RIc,
                    bonus, StatChangeReason._0023Zt, player);
                rewarded++;
            }

            Events.Add($"拾荒 {team.Team} 第 {_truckFullOrder.Count} 个装满卡车, {rewarded} 人各得经验 {bonus:F0}");
        }
    }

    private static List<TeamId> MannedTeams(GameManager simulation) =>
        simulation.ActivePlayerList
            .Where(p => p != null && p.IsActive && p.TeamId != TeamId.Neutral)
            .Select(p => p.TeamId)
            .Distinct()
            .ToList();

    /// <summary>
    /// "快赢了"的队: 原厂判据 <c>ScavengerHunt._0023mvb</c> (身上 + 已交 &gt;= 胜利线, 或离胜利线不到 30)。
    /// 客户端看它变化放公告 —— 变成某队 "CloseSupply", 从某队变回没有 "XX Denied"。
    /// </summary>
    /// <returns>倒计时走完判了胜负。</returns>
    private bool UpdateCloseToWin(GameManager simulation, GameServer network, ScavengerHunt hunt, float deltaTime)
    {
        // "快赢了"只在夜里序列化 (_0023otb 的 if (_0023ctb) 分支) —— 白天设了客户端也看不到倒计时, 不设
        TeamId close = !IsNight ? TeamId.Neutral : _teams
            .Where(t => hunt._0023mvb(t.Team))
            .OrderByDescending(t => hunt.SupplyData._0023Rj(t.Team))
            .Select(t => t.Team)
            .FirstOrDefault();

        if (hunt._0023l9._0023dtb != close)
        {
            Events.Add(close == TeamId.Neutral
                ? $"拾荒 {hunt._0023l9._0023dtb} 不再接近胜利"
                : $"拾荒 {close} 接近胜利 (已交 {hunt.SupplyData._0023Rj(close)}/{hunt._0023jj})");
            hunt._0023l9._0023dtb = close;
            _closeTimer = CloseToWinSeconds;
        }

        if (close == TeamId.Neutral)
        {
            return false;
        }

        _closeTimer -= deltaTime;
        if (_closeTimer > 0f)
        {
            return false;
        }

        Events.Add($"拾荒 {close} 接近胜利的倒计时 {CloseToWinSeconds:F0} 秒走完, 没被拦下");
        Finish(simulation, network, hunt, close);
        return true;
    }

    private void CheckWinner(GameManager simulation, GameServer network, ScavengerHunt hunt)
    {
        TeamState winner = _teams.FirstOrDefault(t => hunt._0023rvb(t.Team));
        if (winner == null)
        {
            return;
        }

        Finish(simulation, network, hunt, winner.Team);
    }

    /// <summary>
    /// 分出胜负: 定名次 -&gt; 放原厂结束过场 (<see cref="RunEnding"/>) -&gt; 放完由 <see cref="Complete"/>
    /// 标记结束 -&gt; 奖牌信息 -&gt; 结算 -&gt; 每队一条 TeamFinished。
    ///
    /// 结算要赶在 TeamFinished 之前, 顺序照 Scout 踩出来的 (<see cref="ScoutFinaleDirector.CompleteNow"/>)。
    /// 客户端 <c>GameHUD.TeamFinished</c> 对拾荒只在"是自己队 (或第三名) 且 finishNow"时 0.4 秒后收尾。
    /// </summary>
    private void Finish(GameManager simulation, GameServer network, ScavengerHunt hunt, TeamId winner)
    {
        Winner = winner;

        List<TeamId> order = _teams
            .Select(t => t.Team)
            .OrderByDescending(t => t == winner)
            .ThenByDescending(t => hunt.SupplyData._0023Rj(t))
            .ThenBy(t => t)
            .ToList();

        for (int i = 0; i < order.Count; i++)
        {
            _placements[order[i]] = (Placement)Math.Min(i, (int)Placement.Third);
        }

        // GameOver 只在入夜分支里序列化 (-z/-Ctb.cs _0023otb)。万一白天就分出胜负 (夜里等人冲出去那 90 秒内交满),
        // 顺手转夜, 不然客户端收不到结束。
        //
        // 不设 _0023etb: 它为 true 时客户端打开 / 更新设置菜单会走 resume_Game (UnityClient.cs:2937/2950), 把菜单面板收掉、
        // 重置进游戏状态。玩家实测 (2026-09-17): "结算界面弹出来后马上消失, 然后所有 UI 消失, 需要按两下 ESC 才出现结算面板" ——
        // 加它之前 (60e3252 以前) 结算是正常的。
        if (!hunt._0023l9._0023ctb)
        {
            IsNight = true;
            hunt._0023l9._0023ctb = true;
        }

        // ## 先放原厂的结束过场, 放完再结算 (2026-09-23)
        //
        // 玩家实测: "游戏结束后会弹出来结算面板, 但是瞬间消失, 然后所有 UI 消失, 不能正确结束游戏。"
        // 客户端日志: 三条 TeamFinished 都到了、结算也到了、IsCompleted 也同步到了 —— 没有异常。
        //
        // 病根在客户端收尾的先后 (全是原厂代码):
        //   ViewManager.TeamFinished (本队)  -> HudBinding.ToggleHUD(VICTORY)        胜利界面弹出来
        //   0.4 秒后 GameHUD.DoFinishGUI     -> HideGUI -> ViewManager.ToggleHUD(false)
        //                                    -> HudBinding.ToggleHUD(NONE)           整个 HUD 切到"无"
        // ToggleHUD(false) 开头有一句: 旧 HUD 的血条区 (HUDBars) **已经是隐藏的**就直接返回。原厂结束时
        // 它必然已经隐藏了 —— 因为 TeamFinished 之前先放了**结束过场** (Cinematic._00238i 第一阶段就调
        // ToggleHUD(false) + 黑屏, Cinematic.cs:77), 拾荒那一支还专门放 Sinamoi 离开的语音 (:97)。
        // 我们以前跳过了过场直接发 TeamFinished, 于是胜利界面刚弹出来就被 NONE 切掉。十字路口能正常结束,
        // 正是因为那边一直先放过场 (CrossroadMissionDirector.RunLeaving)。
        //
        // GameOver 这一刻就置: 客户端每帧重设黑屏透明度, 拾荒 GameOver 为真才跳过 (UnityClient.cs:1230),
        // 晚了过场的黑屏会被抹掉 (十字路口 09-14 踩过)。
        hunt.GameOver = true;
        hunt.CinematicObject._00235i = _0023bd._0023Ni._0023Gi;
        _ending = true;
        _endingFor = 0f;
        _endingOrder = order;
        Lockdown.ReleaseAll();
        Events.AddRange(Lockdown.Events);
        Events.Add($"拾荒分出胜负: {winner} —— 先放结束过场, 放完再结算");
    }

    /// <summary>结束过场最长等多久 (原厂各段加起来约 12 秒)。**我们定的**, 同十字路口。</summary>
    public float EndingTimeout { get; set; } = 20f;

    /// <summary>黑屏开始后多久才开始推原厂过场 (网络晚到一点也保证传送时已经全黑)。**我们定的**, 同十字路口。</summary>
    public float EndingFadeLead { get; set; } = 1.5f;

    /// <summary>正在放结束过场。</summary>
    public bool Ending => _ending;

    private bool _ending;
    private float _endingFor;
    private List<TeamId> _endingOrder;

    /// <summary>
    /// 推原厂结束过场 (<c>Cinematic._0023aj</c>, 服务端那一半: 复活、清 buff、送到本队过场点、无敌/锁操作、
    /// 跳上车 ……)。客户端那一半 (<c>_00238i</c>) 在 ScavengerHunt._0023E9 末尾, 每收到一次模式数据就推一次
    /// (ScavengerHunt.cs:520, 不在"数据变了"的判断里), 而我们每帧都发, 所以不用像十字路口那样抖模式数据。
    /// 放完 (或超时) 才结算。
    /// </summary>
    private void RunEnding(GameManager simulation, GameServer network, ScavengerHunt hunt, float deltaTime)
    {
        _endingFor += deltaTime;
        if (_endingFor >= EndingFadeLead)
        {
            hunt.CinematicObject._0023aj(deltaTime, simulation);
        }

        bool done = hunt.CinematicObject._00234i;
        if (!done && _endingFor < EndingTimeout + EndingFadeLead)
        {
            return;
        }

        _ending = false;
        Events.Add(done
            ? $"拾荒结束过场放完 ({_endingFor:F1} 秒) —— 结算"
            : $"拾荒结束过场 {EndingTimeout:F0} 秒没放完, 直接结算");
        Complete(simulation, network, hunt, Winner, _endingOrder);
    }

    /// <summary>过场放完: 标记结束 -&gt; 奖牌信息 -&gt; 结算 -&gt; 每队一条 TeamFinished。</summary>
    private void Complete(GameManager simulation, GameServer network, ScavengerHunt hunt, TeamId winner, List<TeamId> order)
    {
        hunt.IsCompleted = true;
        foreach (TeamId team in order)
        {
            if (hunt.TeamHasFinished != null)
            {
                hunt.TeamHasFinished[(int)team] = true;
            }

            if (hunt.TeamFinishedOrder != null)
            {
                hunt.TeamFinishedOrder[(int)team] = (byte)(_placements[team] + 1);
            }
        }

        TeamId race = _raceOrder.Count > 0 ? _raceOrder[0] : TeamId.Neutral;
        int medals = network == null ? 0 : BroadcastMedals(network, race, FillTruckTeam, winner);

        Achievements.OnMatchWon(simulation, hunt, winner, _teams.Select(t => t.Team));
        Events.AddRange(Achievements.Events);
        Achievements.Events.Clear();
        OnMatchOver?.Invoke(simulation);

        int finished = 0;
        if (network != null)
        {
            foreach (TeamId team in order)
            {
                finished += ScoutFinaleDirector.BroadcastTeamFinished(network, team, _placements[team]);
            }
        }

        Events.Add($"拾荒结束: {winner} 获胜 ({hunt.SupplyData._0023Rj(winner)}/{hunt._0023jj}); 名次 "
                   + string.Join(" > ", order.Select(t => $"{t}:{hunt.SupplyData._0023Rj(t)}"))
                   + $"; 路障竞速 {race}, 先装满卡车 {FillTruckTeam}; 奖牌信息 {medals} 份, TeamFinished {finished} 条");
    }

    /// <summary>这一局里某队的名次 (结算用); 没分出胜负时按"放弃"算。</summary>
    public Placement PlacementOf(TeamId team) =>
        _placements.TryGetValue(team, out Placement place) ? place : Placement.Conceded;

    /// <summary>某队路障竞速的名次: 第一个冲出去的 First, 其余 Third (原厂奖励只看是不是第一)。</summary>
    public Placement RacePlacementOf(TeamId team) =>
        _raceOrder.Count > 0 && _raceOrder[0] == team ? Placement.First : Placement.Third;

    /// <summary>某队"中局" (先装满卡车) 的名次: 同上, 只有第一个是 First。</summary>
    public Placement MidGamePlacementOf(TeamId team) =>
        FillTruckTeam == team ? Placement.First : Placement.Third;

    /// <summary><c>GameMessageType.ScavengerMedalInformation</c>: 路障竞速 / 先装满卡车 / 胜利 三个队 (GameManagerClient.cs:706)。</summary>
    private static int BroadcastMedals(GameServer network, TeamId race, TeamId fillTruck, TeamId winner)
    {
        int sent = 0;
        foreach (Client recipient in network.ConnectedClientsList.OfType<Client>())
        {
            _0023rye._00237ze buffer = network.CreateGameMessage(8);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.ScavengerMedalInformation);
            buffer.Write((byte)race);
            buffer.Write((byte)fillTruck);
            buffer.Write((byte)winner);

            network.SendGameMessage(buffer, recipient);
            sent++;
        }

        return sent;
    }

    private static ref _00238sb TeamData(ScavengerHunt hunt, TeamId team) =>
        ref hunt._0023l9._00239sb[GameMode.TeamToIndex(team)];

    private static bool IsRaceBarricade(BarricadeType type) =>
        type != BarricadeType.LockDownGauntletBlockage && type != BarricadeType.Blockage && type != BarricadeType.FogBarricade;

    private static Vector2 Center(IReadOnlyList<Vector2> points)
    {
        Vector2 sum = Vector2.Zero;
        foreach (Vector2 point in points)
        {
            sum += point;
        }

        return points.Count == 0 ? sum : sum / points.Count;
    }
}
