using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 事件刷怪: 玩家踩到触发点, 就在预先摆好的位置上按事件规定的种类和数量刷怪。
///
/// ## 三层结构 (从地图数据里读出来的, 不是猜的)
///
///     Event_TriggerPosition(ID)          玩家踩这里
///              ↓ 同 ID
///     Event_Assault(ID)                  刷什么、多少只、冷却、是否重刷
///              ↓ 同 ID
///     Spawn_EventEntities(ID) × N        在哪、什么出场动画
///
/// **关联键是 <c>SceneMapObject.ID</c>**, 不是距离。教程地图的实测数据:
///
///   Event_TriggerPosition  6 个: 10016 10020 10038 10067 10085 10103
///   Event_Assault          4 个: 10016 10020 10067 10085
///   Event_Ambush           1 个: 10103
///   Event_StorageRoom      1 个: 10038
///   Spawn_EventEntities   66 个, ID 只取上面那 6 个值, 分组后 37+2+9+1+8+9 = 66
///
/// 六个触发点和六个事件严丝合缝一一对应, 66 个摆位点全部落在这 6 组里 ——
/// 这个吻合度排除了巧合, 也排除了"按距离就近关联"的可能 (10020 的事件本体在
/// (3238,2146), 而它的触发点在 (135,3522), 相距三千多单位)。
///
/// ## 只实现 Assault
///
/// <see cref="Event_Ambush"/> (10103) 和 <see cref="Event_StorageRoom"/> (10038)
/// 的语义还没确定, **不实现**, 日志会把它们和它们的摆位点数报出来。
///
///   - Ambush 的字段是 <c>MaxActiveWalkers</c> / <c>RespawnRateMin|Max</c> ——
///     是"持续补怪"而不是"一次刷一批", 而 RespawnRate 的单位 (秒? 每秒?) 无从确定。
///     另外它本体坐标是 (-2853, 53202), 远在地图外 —— 那是编辑器里"禁用"的常见做法。
///   - StorageRoom 只有 <c>Name</c> 和 <c>ConstID</c>, 没有任何数量字段, 怪从哪来
///     完全看不出。
///
/// 宁可停在这儿也不编 —— 编错了玩家会看到不该出现的怪, 比没有更糟, 而且以后很难查。
///
/// ## 哪些是原厂数据, 哪些是我们的
///
/// 原厂: 触发半径 (<c>Assault.TriggerRadius</c>, 默认 45)、各类僵尸 min/max、
///       冷却 (<c>Cooldown</c>, 默认 40)、是否重刷 (<c>Respawns</c>, 默认 true)、
///       出场延迟 (<c>RandomSpawnDelay</c>, 默认 1)、每个摆位点的位置和出场动画。
///
/// 我们的: 只有"重刷还要求怪清完了"这一条 —— 地图只给了 Cooldown, 没说清完才算。
///         不加这条的话玩家站在触发点里会被每 40 秒无限刷。
///
/// ## 没实现的原厂字段
///
///   WalkingBombs_Min/Max  找不到对应实体类 (七个已确认的类里没有), 跳过并记日志
///   SpecialZombieType / SpecialZombieChance   (漫游刷怪点上的已实现, 见 RoamingZombieSpawner; 事件上的语义未定)
///
/// <c>Elites</c> 已实现 (2026-09-17): 勾上的事件把特殊僵尸换成精英版 (<see cref="ZombieFactory.EliteKinds"/>),
/// 等级走原厂 <c>CheckIngameLevelForEliteSpawn</c>。五张拾荒图每队基地的夜袭都勾着。
///   ShowOnMinimap / HeroicOnly   (五张拾荒图 + 39 张十字路口图上一个都没勾, 现在不影响任何东西)
///   AttachedObjective            拾荒 Expedition 那 3 个 = 1。这个 1 指哪个目标**对不上号**:
///                                <c>GamemodeObjective</c> 里按序号 1 是 AMBUSH1, 按名字是 ASSAULT_OBJECTIVE1,
///                                而拾荒的目标栏两个都不用 (只用 GATHER / BARRICADE_HEALTH / BOSS)。
///                                没证据就不编 —— 反正那 3 个同时勾着 HighLevelBarricadeSpawn, 已经被门住了
///
/// <c>EndGameCinematic</c> 已实现 (2026-09-18): 它就是"基地夜袭"的明文标志,
/// 见 <see cref="ScavengerDirector.IsNightAssault"/> 和宿主里十字路口那个闸门。
/// <c>DelayEventWhenTriggeredBy</c> / <c>HighLevelBarricadeSpawn</c> 也是 09-18 接上的。
/// </summary>
public sealed class EventSpawnDirector
{
    /// <summary>
    /// 一只等着出场的僵尸。<c>Assault.RandomSpawnDelay</c> 让同一批怪错开出场,
    /// 而不是一瞬间全冒出来。
    /// </summary>
    private sealed class Pending
    {
        public float Delay;
        public Type Type;
        public Spawn_EventEntities At;
        public bool Elite;
    }

    private sealed class EventState
    {
        public int Id;
        public Event_TriggerPosition Trigger;
        public Event_Assault Assault;
        public readonly List<Spawn_EventEntities> Spawns = new List<Spawn_EventEntities>();
        public readonly List<Character> Spawned = new List<Character>();
        public readonly List<Pending> Queue = new List<Pending>();
        public bool Fired;
        public float CooldownRemaining;

        /// <summary>踩上了还要等几秒才刷 (<c>Assault.DelayEventWhenTriggeredBy</c>); &lt; 0 = 没在等。</summary>
        public float TriggerDelayRemaining = -1f;

        /// <summary>这一批已经激怒过了 —— 见 <see cref="ProvokeRadius"/>。</summary>
        public bool Provoked;

        /// <summary>被 <see cref="ForceFire"/> 强制触发的: 每只一出场就交给宿主激怒 (没人在旁边等它们)。</summary>
        public bool ProvokeOnSpawn;

        /// <summary>
        /// 触发位置。优先用触发点 —— 事件本体的坐标可能远在地图外
        /// (10020 相距三千多单位), 拿它判距离等于永不触发。
        /// </summary>
        public Vector2 TriggerPosition =>
            Trigger != null ? Trigger.Position : Assault.Position;
    }

    private readonly List<EventState> _events = new List<EventState>();
    private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
    private bool _scanned;

    /// <summary>已登记的 Assault 事件数。</summary>
    public int ArmedEvents => _events.Count;

    /// <summary>已触发过的事件数。</summary>
    public int FiredEvents { get; private set; }

    /// <summary>还在排队等出场的只数。</summary>
    public int PendingCount { get; private set; }

    /// <summary>
    /// 累计触发过多少波 (含重刷)。
    ///
    /// 和 <see cref="FiredEvents"/> 不同 —— 后者是"当前处于已触发状态的事件数",
    /// 会因为重刷而反复升降。这个只增不减, 用来验证"怪没清完就不重刷"那条规则:
    /// 波数应当恰好等于触发过的事件个数。
    /// </summary>
    public int TotalWaves { get; private set; }

    /// <summary>本次 <see cref="Update"/> 新刷出来的只数, 供宿主打日志。</summary>
    public int SpawnedThisTick { get; private set; }

    /// <summary>
    /// 本次 <see cref="Update"/> 新刷出来的那些怪, 供宿主激怒它们。
    ///
    /// ## 为什么要激怒
    ///
    /// 玩家实测: "卡车场景刷的僵尸很多都不会主动攻击, 因为距离太远,
    /// 这个问题和补给点和 barricade 刷的僵尸一样, 应该改成攻击给卡车输送
    /// 物资的队伍, 然后才是靠仇恨距离。"
    ///
    /// 被动仇恨 (<see cref="ZombieBrain.AggroRadius"/> = 55) 是给**场景里
    /// 本来就站着的**怪用的 —— 玩家走近才动, 免得整张图的怪一起冲过来。
    /// 但**事件刷出来的**不一样: 它们是冲着玩家来的, 摆位点又常常在
    /// <see cref="ZombieBrain.LeashRadius"/>(90) 之外, 于是刷出来就站着不动。
    ///
    /// 真机日志印证: 终局刷了 23 波共 72 只, 场上一直顶着 24/24 上限 ——
    /// 玩家杀不动, 因为大部分根本没过来。
    ///
    /// 埋伏刷怪 (<see cref="AmbushDirector"/>) 早就这么做了, 这里是把同一条
    /// 规则铺到另外三个刷怪源上。
    ///
    /// ## ⚠️ 不是一刷出来就激怒 —— 见 <see cref="ProvokeRadius"/>
    ///
    /// 这个列表只在玩家**真的走进这个事件的地盘**之后才有内容。
    /// </summary>
    public readonly List<Character> SpawnedThisTickList = new List<Character>();

    /// <summary>
    /// 刚刷出一只, 以及它来自哪个 <c>Event_Assault</c>。宿主拿它给某些事件的怪挂额外规则
    /// (拾荒夜袭只打装满卡车的那一队, 见 LidgrenHost)。延迟出场的也会在真正刷出来那一刻回调。
    /// </summary>
    public Action<Character, Event_Assault> OnSpawned { get; set; }

    /// <summary>
    /// 触发半径的上限 —— 地图给的值再大也不超过它。
    ///
    /// ## 为什么要盖住原厂数据
    ///
    /// 10016 那个事件 (卡车那边, 45 只) 在**玩家还站在遭遇战地点**时就触发了
    /// (真机日志 12:36:26.163 触发 / 12:36:26.574 才推进到遭遇战), 说明它的
    /// <c>TriggerRadius</c> 至少 306 —— 触发点 (-218.9,3407) 到遭遇战地点
    /// (60.7,3533) 正好那么远。
    ///
    /// 后果是玩家走到卡车时那 45 只早就站好了, 而玩家要的是:
    /// "刚进去的时候里面只有几只摆放好的僵尸, 以及走到附近才会刷出来的僵尸"。
    ///
    /// 160 略大于 <see cref="TutorialNpcAI.TruckAreaRadius"/>(130) ——
    /// 玩家踏进卡车这片场地的**前一刻**开始刷, 一路走进去的过程中它们陆续出场,
    /// 而不是隔着大半张图凭空出现。
    ///
    /// **这是我们定的**, 盖掉的是原厂数据。地图那个大半径多半服务于别的设计
    /// (比如让怪提前就位好埋伏), 但和玩家要的节奏冲突。
    /// </summary>
    public float MaxTriggerRadius { get; set; } = 160f;

    /// <summary>地图给的触发半径和我们的上限取小。</summary>
    private float TriggerRadiusFor(Event_Assault assault) =>
        Math.Min(assault.TriggerRadius, MaxTriggerRadius);

    /// <summary>
    /// 玩家离事件触发点这么近, 这一批怪才会被激怒。
    ///
    /// ## 为什么不能一刷出来就激怒
    ///
    /// 玩家实测: 10016 那一批 **45 只**在遭遇战那一刻就触发了 (触发点在卡车那边,
    /// 离玩家 306 单位), 一激怒就是 45 只横穿半张图冲过来 ——
    /// 玩家原话: "给那45只怪的仇恨加个条件, 进入卡车事件区域后才会攻击玩家,
    /// 避免半路就被僵尸围。"
    ///
    /// 所以激怒的门槛不是"刷出来了", 而是"**玩家进了这片地**"。进去之前它们
    /// 按普通的被动仇恨 (<see cref="ZombieBrain.AggroRadius"/> = 55) 待着,
    /// 也就是原本守在那儿的样子。
    ///
    /// 150 是"进了卡车这片区域": 玩家站在卡车 (-181,3353) 时离 10016 的触发点
    /// (-218.9,3407) 约 66, 稳稳在内; 而遭遇战地点离它 306, 稳稳在外。
    ///
    /// **这是我们定的。**
    /// </summary>
    public float ProvokeRadius { get; set; } = 150f;

    /// <summary>本次 <see cref="Update"/> 触发的事件描述, 供宿主打日志。</summary>
    public readonly List<string> TriggeredThisTick = new List<string>();

    /// <summary>没实现的事件类型 -> 它们的摆位点数, 供宿主打日志。</summary>
    public readonly Dictionary<string, int> SkippedEvents = new Dictionary<string, int>();

    /// <summary>
    /// 额外的开火条件。null = 不设限 (教程和 Horde 图就是这样)。
    ///
    /// ## 为什么要这个闸门
    ///
    /// 玩家实测 (十字路口): "怪都刷在出生点基地附近。" 一半原因是摆位点没归属
    /// (见 <see cref="CrossroadEventCatalog"/>), 另一半就在这里 ——
    /// 十字路口那张图**唯一**的 <see cref="Event_Assault"/> 是**基地夜袭**
    /// (实测 map26: ID 90019, 30 只行尸, <c>Respawns=false</c>, 触发半径 25,
    /// 自带 18 个摆位点), 而它的位置就在基地里, 离玩家出生点几十单位 ——
    /// 于是开局第一帧玩家一落地它就炸了, 30 只怪直接堆在基地门口。
    ///
    /// 它本来是**最后一幕**: 三轮任务做完、天黑之后守基地。所以宿主给它挂上
    /// "只有进了收尾阶段才准刷"这个条件 (见 LidgrenHost 里那句)。
    ///
    /// 判据按事件本体传进来, 而不是一刀切全局开关 —— 一张图上可能既有夜袭
    /// 也有别的 Assault。
    /// </summary>
    public Func<Event_Assault, bool> Allow { get; set; }

    /// <summary>
    /// 勾了 <c>HighLevelBarricadeSpawn</c> 的事件什么时候准刷。<c>null</c> = 不设限。
    ///
    /// 原厂字段, 客户端一处不读 (= 服务端用的)。拾荒 Resort / Jungle / Expedition
    /// 各 3 个勾着它 (都是 <c>cd=5 / r=50~60 / 6 只行尸 / Respawns=false / 延迟 20 秒</c>
    /// 那个模子, 摆在出生区往外的路上), 另有 14 个漫游刷怪点也勾着。
    ///
    /// **语义是推断**: 字面意思是"高级路障的刷怪", 拾荒每队两道路障,
    /// 所以读成"这队打到最后一道路障 (或已经冲出去了) 才准刷"。
    /// 实现在 <see cref="ScavengerDirector.AllowHighLevelBarricadeSpawn"/>。
    /// 不设这个钩子时行为和以前一样 (踩进圈就刷)。
    /// </summary>
    public Func<Event_Assault, bool> AllowHighLevelBarricade { get; set; }

    /// <summary>
    /// 扫一遍地图, 按 ID 把三层结构拼起来。幂等 —— 重复调用不会重复登记。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        Dictionary<int, EventState> byId = new Dictionary<int, EventState>();
        List<Spawn_EventEntities> allSpawns = new List<Spawn_EventEntities>();
        Dictionary<int, string> unimplemented = new Dictionary<int, string>();

        foreach (MapObject mapObject in simulation.MapObjects)
        {
            switch (mapObject)
            {
                case Event_Assault assault:
                    Slot(byId, assault.ID).Assault = assault;
                    break;

                case Event_TriggerPosition trigger:
                    Slot(byId, trigger.ID).Trigger = trigger;
                    break;

                case Spawn_EventEntities spawn:
                    allSpawns.Add(spawn);
                    break;

                case Event_Ambush ambush:
                    unimplemented[ambush.ID] = "Event_Ambush";
                    break;

                case Event_StorageRoom storage:
                    unimplemented[storage.ID] = "Event_StorageRoom";
                    break;
            }
        }

        // 摆位点归到自己的事件下。落到没实现的事件上的, 只记数不生成。
        foreach (Spawn_EventEntities spawn in allSpawns)
        {
            if (byId.TryGetValue(spawn.ID, out EventState state) && state.Assault != null)
            {
                state.Spawns.Add(spawn);
                continue;
            }

            string kind = unimplemented.TryGetValue(spawn.ID, out string name)
                ? name
                : $"未知事件ID={spawn.ID}";

            SkippedEvents.TryGetValue(kind, out int seen);
            SkippedEvents[kind] = seen + 1;
        }

        // 只留下"有 Assault 且有摆位点"的 —— 缺任何一半都刷不出东西。
        foreach (KeyValuePair<int, EventState> entry in byId)
        {
            if (entry.Value.Assault != null && entry.Value.Spawns.Count > 0)
            {
                _events.Add(entry.Value);
            }
        }

        foreach (KeyValuePair<string, Type> entry in ZombieFactory.ResolveKindTypes())
        {
            _types[entry.Key] = entry.Value;
        }

        _scanned = true;
    }

    private static EventState Slot(Dictionary<int, EventState> byId, int id)
    {
        if (!byId.TryGetValue(id, out EventState state))
        {
            state = new EventState { Id = id };
            byId[id] = state;
        }

        return state;
    }

    /// <summary>
    /// 不管玩家在不在触发圈里, 立刻触发符合条件、还没触发过的事件。刷出来的每只一出场就进
    /// <see cref="SpawnedThisTickList"/> 让宿主激怒。返回触发了几个。
    ///
    /// 用在拾荒的夜袭: 每队卡车旁那个 30 只的事件 (冷却 99999, 触发半径 25) 要在入夜那一刻来,
    /// 而不是等玩家走进 25 单位的圈 —— 见 <see cref="ScavengerDirector.IsNightAssault"/>。
    /// </summary>
    public int ForceFire(GameManager simulation, Func<Event_Assault, bool> which)
    {
        if (simulation == null || which == null)
        {
            return 0;
        }

        Scan(simulation);

        int count = 0;
        foreach (EventState state in _events)
        {
            if (state.Fired || !which(state.Assault))
            {
                continue;
            }

            Enqueue(simulation, state);
            state.Fired = true;
            state.ProvokeOnSpawn = true;
            state.CooldownRemaining = state.Assault.Cooldown;
            TotalWaves++;
            count++;
            TriggeredThisTick.Add($"ID={state.Id}×{state.Queue.Count} (强制)");
        }

        return count;
    }

    /// <summary>
    /// 每帧一次。<paramref name="deltaTime"/> 用来走出场延迟和冷却。
    /// </summary>
    /// <summary>
    /// 选中的事件里还没打完的僵尸: 排队没出场的 + 出场了还活着的 (只算已经刷过的事件)。
    /// 拾荒用它判"这队基地的夜袭清完了没有" (<see cref="ScavengerLockdown"/>)。
    /// </summary>
    public int RemainingFor(Func<Event_Assault, bool> which)
    {
        int remaining = 0;
        foreach (EventState state in _events)
        {
            if (!state.Fired || which == null || !which(state.Assault))
            {
                continue;
            }

            remaining += state.Queue.Count;
            for (int i = 0; i < state.Spawned.Count; i++)
            {
                Character zombie = state.Spawned[i];
                if (zombie != null && zombie.IsActive && !zombie.IsDead)
                {
                    remaining++;
                }
            }
        }

        return remaining;
    }

    public void Update(GameManager simulation, float deltaTime)
    {
        SpawnedThisTick = 0;
        PendingCount = 0;
        TriggeredThisTick.Clear();
        SpawnedThisTickList.Clear();

        if (simulation == null)
        {
            return;
        }

        Scan(simulation);

        List<Player> players = simulation.ActivePlayerList;
        int fired = 0;

        foreach (EventState state in _events)
        {
            DrainQueue(simulation, state, deltaTime);
            ProvokeIfPlayerArrived(state, players);

            if (state.Fired)
            {
                if (!ReadyToRefire(state, deltaTime))
                {
                    fired++;
                    PendingCount += state.Queue.Count;
                    continue;
                }

                state.Fired = false;
                state.Provoked = false;   // 下一批重新按距离判, 见 ProvokeRadius
                state.Spawned.Clear();
            }

            float nearest = NearestPlayerDistance(players, state.TriggerPosition);
            if (nearest > TriggerRadiusFor(state.Assault))
            {
                continue;
            }

            if (Allow != null && !Allow(state.Assault))
            {
                state.TriggerDelayRemaining = -1f;
                continue;       // 时候没到 —— 见 Allow
            }

            if (state.Assault.HighLevelBarricadeSpawn && AllowHighLevelBarricade != null
                && !AllowHighLevelBarricade(state.Assault))
            {
                state.TriggerDelayRemaining = -1f;
                continue;       // 这队还在打前面的路障 —— 见 AllowHighLevelBarricade
            }

            // 原厂 DelayEventWhenTriggeredBy: 踩进圈里不是立刻刷, 而是等这么久
            if (state.Assault.DelayEventWhenTriggeredBy > 0f)
            {
                if (state.TriggerDelayRemaining < 0f)
                {
                    state.TriggerDelayRemaining = state.Assault.DelayEventWhenTriggeredBy;
                    TriggeredThisTick.Add($"ID={state.Id} 等 {state.Assault.DelayEventWhenTriggeredBy:F0} 秒");
                }

                state.TriggerDelayRemaining -= deltaTime;
                if (state.TriggerDelayRemaining > 0f)
                {
                    continue;
                }
            }

            state.TriggerDelayRemaining = -1f;
            Enqueue(simulation, state);
            state.Fired = true;
            state.CooldownRemaining = state.Assault.Cooldown;
            fired++;
            TotalWaves++;

            // PendingCount 必须在 Enqueue **之后**统计 —— 先前放在循环开头,
            // 于是触发那一帧报的永远是 0, 掩盖了"这波到底排了几只"。
            PendingCount += state.Queue.Count;

            TriggeredThisTick.Add($"ID={state.Id}×{state.Queue.Count}");
        }

        FiredEvents = fired;
    }

    /// <summary>
    /// 玩家走进这个事件的地盘了 -> 把这一批还活着的怪吐给宿主去激怒。
    ///
    /// 只做一次 (<see cref="EventState.Provoked"/>)。重刷时
    /// <see cref="ReadyToRefire"/> 会把它放下, 下一批重新按距离判。
    ///
    /// 理由见 <see cref="ProvokeRadius"/> —— 一刷出来就激怒的话, 卡车那 45 只
    /// 会在玩家还在遭遇战地点时就横穿半张图冲过来。
    /// </summary>
    private void ProvokeIfPlayerArrived(EventState state, List<Player> players)
    {
        if (state.Provoked || !state.Fired || state.Spawned.Count == 0)
        {
            return;
        }

        if (NearestPlayerDistance(players, state.TriggerPosition) > ProvokeRadius)
        {
            return;
        }

        state.Provoked = true;

        for (int i = 0; i < state.Spawned.Count; i++)
        {
            Character zombie = state.Spawned[i];
            if (zombie != null && zombie.IsActive && !zombie.IsDead)
            {
                SpawnedThisTickList.Add(zombie);
            }
        }
    }

    /// <summary>
    /// 能不能重刷。两个条件都要满足: 冷却走完了, 而且上一批怪清完了。
    ///
    /// "清完了"这条是**我们加的** —— 地图只给了 Cooldown。不加的话玩家站在触发点
    /// 里会被每 <c>Cooldown</c> 秒无限刷。
    /// </summary>
    /// <summary>
    /// 重刷冷却走得多快。1 = 原样; 拾荒入夜后宿主设成地图的 <c>EndGameSpawnRateIncrease</c> (五张图都是 1.5),
    /// 冷却缩短到 1/1.5。那个字段客户端没人读, "刷怪速率"落到冷却上是我们的读法。
    /// </summary>
    public float SpawnRateMultiplier { get; set; } = 1f;

    private bool ReadyToRefire(EventState state, float deltaTime)
    {
        if (!state.Assault.Respawns)
        {
            return false;
        }

        if (state.CooldownRemaining > 0f)
        {
            state.CooldownRemaining -= deltaTime * SpawnRateMultiplier;
            return false;
        }

        return state.Queue.Count == 0 && AllDead(state.Spawned);
    }

    /// <summary>
    /// 按事件的 min/max 摇数量, 把每只怪排进出场队列并分配一个摆位点。
    ///
    /// 摆位点用轮转分配: 怪比点多时会有两只用同一个点, 那是原版也会有的情况
    /// (37 个点对 4~8 只 Walker, 通常点比怪多)。
    /// </summary>
    private void Enqueue(GameManager simulation, EventState state)
    {
        int next = 0;

        foreach ((string obfuscated, string readable) in ZombieFactory.Kinds)
        {
            (int min, int max) = CountsFor(state.Assault, readable);
            if (max <= 0 || !_types.TryGetValue(obfuscated, out Type type))
            {
                continue;
            }

            Type elite = state.Assault.Elites ? ZombieFactory.EliteTypeFor(readable) : null;

            // 用模拟层自己的 RNG —— 它参与回放的确定性重放, 服务端所有随机都走同一个源。
            int count = simulation.Random._0023Otc(min, max + 1);

            for (int i = 0; i < count; i++)
            {
                Spawn_EventEntities at = state.Spawns[next % state.Spawns.Count];
                next++;

                state.Queue.Add(new Pending
                {
                    Delay = RandomDelay(simulation, state.Assault.RandomSpawnDelay),
                    Type = elite ?? type,
                    At = at,
                    Elite = elite != null,
                });
            }
        }
    }

    /// <summary>0 到 <paramref name="max"/> 秒之间。max &lt;= 0 就立刻出场。</summary>
    private static float RandomDelay(GameManager simulation, float max)
    {
        if (max <= 0f)
        {
            return 0f;
        }

        return simulation.Random._0023Otc(0, 1001) / 1000f * max;
    }

    /// <summary>
    /// 一帧最多让几只出场。
    ///
    /// ## 为什么需要这个闸门
    ///
    /// 真机日志: <c>触发事件 ID=10016×45</c> —— 一次排了 **45 只**。
    /// 数量本身是原厂数据 (Assault 的各类 min/max), 摆位点也有 37 个,
    /// 所以"该刷这么多"没错。错的是它们**挤在同一帧生成**:
    /// <c>RandomSpawnDelay</c> 默认 1 秒, 45 只摊到 30 帧里, 峰值那几帧
    /// 每帧要生成好几只并同时下发。
    ///
    /// 玩家实测: "碰到敌对NPC时会有几秒无法操控角色" —— 时间戳正好对上
    /// (01:42:27 触发, 01:42:28 才推进到遭遇战那一步)。
    ///
    /// ## 3 -> 1: 半秒还是太挤
    ///
    /// 第一版取 3 只/帧, 45 只摊 15 帧 = 0.5 秒。玩家实测**照样卡**
    /// ("正在播放对话时无法控制角色, 有一小段时间这两个 NPC 也不动了" ——
    /// 全场停顿, 那是客户端在一口气建 45 个模型)。
    ///
    /// 时间戳咬得很死: 12:36:26.163 触发 45 只, 12:36:26.574 推进到遭遇战。
    /// 而 10016 的触发点在卡车那边, 离玩家 306 单位 —— 在同步剔除半径 400 以内,
    /// 所以这 45 只**全都会下发给客户端**, 哪怕玩家一只都看不见。
    ///
    /// 1 只/帧 = 30 只/秒, 45 只摊 1.5 秒。总数一只不少, 只是不再挤成一坨。
    ///
    /// **这是我们加的**, 地图数据里没有这个概念。
    /// </summary>
    public int MaxSpawnsPerFrame { get; set; } = 1;

    /// <summary>延迟到了就把僵尸放到场上。</summary>
    private void DrainQueue(GameManager simulation, EventState state, float deltaTime)
    {
        int spawnedThisFrame = 0;

        for (int i = state.Queue.Count - 1; i >= 0; i--)
        {
            Pending pending = state.Queue[i];
            pending.Delay -= deltaTime;
            if (pending.Delay > 0f)
            {
                continue;
            }

            // 削峰。留在队列里, 下一帧再出 —— 延迟已经归零, 所以不会再往后拖。
            if (spawnedThisFrame >= MaxSpawnsPerFrame)
            {
                continue;
            }

            spawnedThisFrame++;
            state.Queue.RemoveAt(i);

            Character zombie = ZombieFactory.Spawn(
                simulation, pending.Type, pending.At.Position,
                pending.At.SpawnState, pending.At.Direction,
                pending.Elite ? (Action<Character>)ZombieFactory.ApplyEliteLevel : null);

            if (zombie == null)
            {
                continue;
            }

            state.Spawned.Add(zombie);
            SpawnedThisTick++;
            OnSpawned?.Invoke(zombie, state.Assault);

            if (state.ProvokeOnSpawn)
            {
                SpawnedThisTickList.Add(zombie);
            }
        }
    }

    /// <summary>
    /// 事件上某一类僵尸的 min/max。字段名是明文的 (Event_Assault 没被混淆),
    /// 所以直接按名字取 —— 上游改字段名会编译失败而不是静默取到 0。
    /// </summary>
    private static (int Min, int Max) CountsFor(Event_Assault assault, string kind) => kind switch
    {
        "Walker" => (assault.Walkers_Min, assault.Walkers_Max),
        "Butcher" => (assault.Butchers_Min, assault.Butchers_Max),
        "Floater" => (assault.Floaters_Min, assault.Floaters_Max),
        "Puller" => (assault.Pullers_Min, assault.Pullers_Max),
        "Ram" => (assault.Rams_Min, assault.Rams_Max),
        "Screamer" => (assault.Screamers_Min, assault.Screamers_Max),
        "Suicider" => (assault.Suiciders_Min, assault.Suiciders_Max),
        _ => (0, 0),
    };

    /// <summary>
    /// 到最近玩家的距离。没有活着的玩家时返回 <see cref="float.MaxValue"/> ——
    /// 全员阵亡时不该继续触发事件。
    /// </summary>
    private static float NearestPlayerDistance(List<Player> players, Vector2 position)
    {
        float best = float.MaxValue;

        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            float distanceSquared = Vector2.DistanceSquared(player.Position, position);
            if (distanceSquared < best)
            {
                best = distanceSquared;
            }
        }

        return best == float.MaxValue ? float.MaxValue : (float)Math.Sqrt(best);
    }

    /// <summary>
    /// 这批怪是不是清完了。已知不精确之处和
    /// <see cref="RoamingZombieDirector"/> 里那份相同 (对象池复用会误判成"还活着"),
    /// 后果是"少刷怪"而不是错乱, 等做正经重生时一并处理。
    /// </summary>
    private static bool AllDead(List<Character> zombies)
    {
        for (int i = 0; i < zombies.Count; i++)
        {
            Character zombie = zombies[i];
            if (zombie != null && zombie.IsActive && !zombie.IsDead)
            {
                return false;
            }
        }

        return true;
    }
}
