using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 埋伏刷怪 —— 玩家开始打某组路障时, 僵尸从旁边持续涌出来。
///
/// ## 三层结构靠 ID 串起来, 和 <see cref="EventSpawnDirector"/> 同一套
///
///     Event_Ambush(ID)          同时最多几只、多久补一只
///     Spawn_EventEntities(ID)   从哪冒出来
///     Spawn_Destructible        GroupID == ID 的那组路障, 就是触发器
///
/// 教程图只有一组 (ID **10103**), 而 10103 正是那三段
/// <c>WeakWoodenBarricade</c> 的 <c>GroupID</c> —— ID 相等就是绑定关系,
/// 不是巧合:
///
///     Event_Ambush 10103        MaxActiveWalkers 7, RespawnRate 0.5~1 秒
///     摆位点 10103 × 9          (1239,3676) (1171,3656) (1210,3639) …
///     木栅栏 GroupID 10103 × 3  x≈1130~1167, y 3681~3724
///
/// 9 个摆位点全在木栅栏**东侧几十单位**, 也就是玩家打栅栏时背后/侧面。
/// 客户端在这一步显示的引导正是 <c>TutorialGuideText.ZOMBIEAMBUSH</c>
/// (-5cc.cs:412) —— "僵尸埋伏"。
///
/// ## 我先前跳过它是因为把配置坐标当成了位置
///
/// <c>Event_Ambush</c> 本体的坐标是 (-2853, 53202), 在地图外几万单位。
/// 我据此判断"数据不可用"就停手了 —— **错了**: 事件本体是个配置容器,
/// 位置本来就没意义 (<see cref="EventSpawnDirector"/> 早就踩过同样的坑,
/// 那边的注释写着"不能按距离关联, 要按 ID")。真正的坐标在摆位点上。
///
/// 另一个停手理由是"RespawnRate 单位不明"。类的默认值给了答案:
/// <c>RespawnRateMin = 0.3f</c> / <c>MaxActiveWalkers = 20</c> ——
/// 0.3 只可能是**秒**(补一只的间隔), 不可能是速率。
///
/// ## 触发和停止
///
/// 触发: 那组路障**掉血了**。玩家反馈的原话是"攻击 barricade 时应该会自动刷怪",
/// 而不是"走近就刷" —— 所以判据是受伤, 不是距离。
///
/// 停止: 那组路障没了 (打破或被清场)。已经刷出来的不回收, 玩家得打完。
/// </summary>
public sealed class AmbushDirector
{
    private sealed class AmbushState
    {
        public int Id;
        public Event_Ambush Ambush;
        public readonly List<Spawn_EventEntities> Spawns = new List<Spawn_EventEntities>();

        /// <summary>这组路障的位置。用来在场上认出它们 —— 见 GroupDamaged。</summary>
        public readonly List<Vector2> BarricadePositions = new List<Vector2>();

        public readonly List<Character> Spawned = new List<Character>();
        public bool Active;
        public float NextSpawnIn;
        public int NextSpawnPoint;

        /// <summary>上一次看到的这组路障总血量, 用来发现"又挨了一下"。</summary>
        public float LastSeenHealth = -1f;

        /// <summary>最近在打这组路障的人 —— 埋伏怪的优先目标。</summary>
        public Entity Attacker;

        /// <summary>上一次报进日志的总血量, 用来只在变化时报一行。</summary>
        public float ReportedHealth = float.NaN;
    }

    private readonly List<AmbushState> _ambushes = new List<AmbushState>();
    private bool _scanned;

    /// <summary>
    /// 埋伏怪要不要播地图上写的出场动画。
    ///
    /// **默认开** —— 上一轮我关掉它去治"看不见的僵尸", 那是误诊:
    /// <see cref="RoamingZombieSpawner"/> 一直用 <c>Digging</c> 出场,
    /// 那些僵尸玩家看得见。所以钻地动画本身没问题, 关掉也没治好。
    /// </summary>
    public static bool UseSpawnAnimation { get; set; } = true;

    /// <summary>
    /// 埋伏怪在**路障旁边**生成, 而不是用地图给的 9 个摆位点。默认开。
    ///
    /// ## 看不见的僵尸: 位置问题, 不是动画问题
    ///
    /// 玩家两轮都报告 "barricade 附近会刷出来看不见的僵尸, 看不到在哪但是
    /// 可以攻击到"。排查掉的可能:
    ///
    ///   - 出场动画: 漫游刷怪一直用 Digging, 那些看得见 —— 排除
    ///   - 客户端对象池: Walker 的池是 260 (`[_0023had(260, 260, …)]`),
    ///     场上撑死几十只 —— 排除
    ///
    /// 剩下的是**位置**。那 9 个摆位点在木栅栏东侧的木质栈道上, 而客户端
    /// 决定模型垂直位置用的是 <c>ViewManager.GetHeight(x, y)</c> —— 采的是
    /// **地形**高度。栈道是建筑不是地形, 采样会拿到底下的地面或水面,
    /// 模型就沉到板子下面去了。服务端只认 2D 坐标, 碰撞和攻击照常在板子上,
    /// 于是"看不见却能打到你"。
    ///
    /// 那个高度是客户端算的, 服务端改不了。但可以换个生成位置 ——
    /// 玩家正在打路障, 说明路障那一带的地面是可见的。
    ///
    /// ## 那个假设**已经被证伪了**
    ///
    /// 换到路障旁边之后玩家再测: "现在我能确认的绝对有隐身僵尸的地方就是
    /// barricade 那里刷出的僵尸"。位置不是根因。
    ///
    /// 开关留着 —— 路障旁边涌怪本身是合理的行为 (玩家在打路障, 怪从周围围上来),
    /// 只是它治不了隐身。真正的差异见 <see cref="EmergeState"/>。
    /// </summary>
    public static bool SpawnNextToBarricade { get; set; } = true;

    /// <summary>
    /// 埋伏怪出场时用哪个动画。
    ///
    /// ## 为什么不用地图点自带的那个
    ///
    /// 地图给埋伏点写的是 <c>_0023Oed</c> = <c>Spawn_Jump</c>(跳出来,
    /// -Mk/-rPb.cs:57 注册的)。而 <see cref="RoamingZombieSpawner"/> 用的是
    /// <c>Digging</c>(<c>_0023eCc</c>, 钻地) —— **那些僵尸玩家一直看得见**。
    ///
    /// 排查到这一步, 两者只剩这一个差异:
    ///
    ///   位置    换到路障旁边了, 玩家实测仍然隐身 —— 排除
    ///   同步    生成时同步位为 True, 而且 FinishEmerge 清出场状态时现在会
    ///           强制下发 (ZombieBrain) —— 排除
    ///   对象池  Walker 的池是 260, 场上撑死几十只 —— 排除
    ///   出场动画 漫游用 Digging 看得见, 埋伏用 Spawn_Jump 看不见 ← 只剩这个
    ///
    /// Spawn_Jump 是"从高处跳下来", 落点的垂直位置由客户端算; Digging 是
    /// "从地里爬出来", 一直贴着地面。这能解释为什么同一套刷怪代码、同一种僵尸,
    /// 换个动画就看不见了 —— 但**服务端拿不到客户端的高度图, 证实不了**。
    ///
    /// 所以这里改成和漫游刷怪用同一个出场动画。观感上都是"从地里冒出来",
    /// 而且那条路径已经被玩家验证过很多轮。要是这样还隐身, 那说明问题根本不在
    /// 出场动画上, 得换个方向 —— 别再在这条线上加第四个假设了。
    /// </summary>
    public static _0023Oq._0023WM EmergeState { get; set; } = _0023Oq._0023WM._0023eCc;

    /// <summary>埋伏怪离路障多远出现。够近保证在可见区域, 又不至于贴脸。</summary>
    public static float BarricadeSpawnOffset { get; set; } = 45f;

    /// <summary>已登记的埋伏组数。</summary>
    public int ArmedAmbushes => _ambushes.Count;

    /// <summary>当前正在刷怪的埋伏组数。</summary>
    public int ActiveAmbushes { get; private set; }

    /// <summary>本次 <see cref="Update"/> 新刷出来的只数, 供宿主打日志。</summary>
    public int SpawnedThisTick { get; private set; }

    /// <summary>
    /// 本次 <see cref="Update"/> 新刷出来的僵尸。
    ///
    /// 宿主拿它去调 <c>ZombieBrain.Provoke</c> —— 埋伏怪要**一出生就追玩家**,
    /// 而不是等玩家走进仇恨半径。埋伏点离战场常常超过脱战半径,
    /// 光靠普通仇恨它们会站在原地不动。
    ///
    /// 由 AmbushDirector 暴露、宿主转发, 而不是这里直接引用 ZombieBrain ——
    /// 刷怪和 AI 是两回事, 让它们互相认识只会把依赖搞乱。
    /// </summary>
    public readonly List<Character> SpawnedThisTickList = new List<Character>();

    /// <summary>
    /// 和 <see cref="SpawnedThisTickList"/> 一一对应: 每只新怪该优先打谁。
    /// 也就是"正在打那组路障的人"。可能是 null (还没归因到)。
    /// </summary>
    public readonly List<Entity> SpawnedThisTickTargets = new List<Entity>();

    /// <summary>本次 <see cref="Update"/> 刚被触发的埋伏组 ID, 供宿主打日志。</summary>
    public readonly List<int> TriggeredThisTick = new List<int>();

    /// <summary>
    /// 本次 <see cref="Update"/> 要报给日志的路障组状况。
    ///
    /// 只在血量变化和埋伏结束时产生, 所以不会刷屏。
    /// </summary>
    public readonly List<string> GroupReports = new List<string>();

    /// <summary>把这组路障每一段的血量列出来 —— 卡住时要知道是哪一段没死。</summary>
    private static string DescribeGroup(GameManager simulation, AmbushState state)
    {
        List<string> parts = new List<string>();
        List<GameObjectBase> objects = simulation.ActiveGameObjectList;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is _0023EK._0023Yh barricade
                && barricade.IsActive
                && BelongsToGroup(state, barricade.Position))
            {
                parts.Add($"({barricade.Position.X:F0},{barricade.Position.Y:F0})" +
                          $" {barricade.Health:F0}/{barricade.MaxHealth:F0}" +
                          $" {barricade._00238Zb}");
            }
        }

        return parts.Count == 0 ? "场上一段都不剩" : string.Join("  ", parts);
    }

    /// <summary>
    /// 扫一遍地图, 按 ID 把三层拼起来。幂等。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        Dictionary<int, AmbushState> byId = new Dictionary<int, AmbushState>();
        List<Spawn_EventEntities> allSpawns = new List<Spawn_EventEntities>();
        List<Spawn_Destructible> allBarricades = new List<Spawn_Destructible>();

        foreach (MapObject mapObject in simulation.MapObjects)
        {
            switch (mapObject)
            {
                case Event_Ambush ambush:
                    Slot(byId, ambush.ID).Ambush = ambush;
                    break;

                case Spawn_EventEntities spawn:
                    allSpawns.Add(spawn);
                    break;

                case Spawn_Destructible barricade:
                    allBarricades.Add(barricade);
                    break;
            }
        }

        foreach (Spawn_EventEntities spawn in allSpawns)
        {
            if (byId.TryGetValue(spawn.ID, out AmbushState state))
            {
                state.Spawns.Add(spawn);
            }
        }

        // GroupID == 埋伏 ID 的那组路障就是触发器。
        foreach (Spawn_Destructible barricade in allBarricades)
        {
            if (byId.TryGetValue(barricade.GroupID, out AmbushState state))
            {
                state.BarricadePositions.Add(barricade.Position);
            }
        }

        // 三样齐了才留下 —— 缺任何一样都刷不出东西, 留着只会每帧空转。
        foreach (KeyValuePair<int, AmbushState> entry in byId)
        {
            AmbushState state = entry.Value;
            if (state.Ambush != null && state.Spawns.Count > 0 && state.BarricadePositions.Count > 0)
            {
                _ambushes.Add(state);
            }
        }

        _scanned = true;
    }

    private static AmbushState Slot(Dictionary<int, AmbushState> byId, int id)
    {
        if (!byId.TryGetValue(id, out AmbushState state))
        {
            state = new AmbushState { Id = id };
            byId[id] = state;
        }

        return state;
    }

    /// <summary>每帧一次。</summary>
    public void Update(GameManager simulation, float deltaTime)
    {
        SpawnedThisTick = 0;
        SpawnedThisTickList.Clear();
        SpawnedThisTickTargets.Clear();
        TriggeredThisTick.Clear();
        GroupReports.Clear();
        ActiveAmbushes = 0;

        if (simulation == null)
        {
            return;
        }

        for (int i = 0; i < _ambushes.Count; i++)
        {
            UpdateOne(simulation, _ambushes[i], deltaTime);
        }
    }

    /// <summary>
    /// 谁在打这组路障 —— 埋伏怪的优先目标。
    ///
    /// ## 这是个近似, 说清楚为什么
    ///
    /// 拿不到真正的伤害来源: <c>GameManagerServer.OnCharacterDamageTaken</c>
    /// 的第一个参数是 <c>Character</c>, 而路障继承 <c>Destructible</c> ——
    /// 那个回调根本不会为路障触发, 也就拿不到 <c>statChangeData.Changer</c>。
    ///
    /// 所以用"血掉了的那一帧, 离这组路障最近的玩家"来归因。打路障得靠近它
    /// (近战贴脸, 教程里的枪也是几十单位内), 所以这个近似在教程场景下是准的。
    /// 真要精确归因得给路障挂 <c>ActivateStatListener</c>, 那需要一个
    /// <c>GameObjectBase</c> 当宿主, 结构上要大改 —— 现在不值得。
    /// </summary>
    private static void UpdateAttacker(GameManager simulation, AmbushState state, float health)
    {
        // 只在**掉血的那一帧**重新归因 —— 每帧都算的话, 玩家打完站着不动,
        // 仇恨也会跟着谁离得近而乱跳。
        if (state.LastSeenHealth < 0f || health >= state.LastSeenHealth)
        {
            state.LastSeenHealth = health;
            return;
        }

        state.LastSeenHealth = health;

        Vector2 center = state.BarricadePositions[0];
        Player nearest = null;
        float nearestDistanceSquared = float.MaxValue;

        List<Player> players = simulation.ActivePlayerList;
        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            float distanceSquared = Vector2.DistanceSquared(player.Position, center);
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestDistanceSquared = distanceSquared;
                nearest = player;
            }
        }

        if (nearest != null)
        {
            state.Attacker = nearest;
        }
    }

    private void UpdateOne(GameManager simulation, AmbushState state, float deltaTime)
    {
        GroupStatus status = InspectGroup(simulation, state, out float health);
        UpdateAttacker(simulation, state, health);

        // 血量变了就报一次。玩家实测"栅栏打破了刷怪还在继续", 而停止判据就是
        // "这组一段都不剩了" —— 和教程状态机推进用的是同一个判据
        // (DestructibleSpawner.CountAlive)。两个一起卡住, 说明场上还有一段
        // Health > 0。是哪一段、剩多少, 只能这样看。
        if (state.Active && health != state.ReportedHealth)
        {
            state.ReportedHealth = health;
            GroupReports.Add($"埋伏 {state.Id} 路障组: {DescribeGroup(simulation, state)}");
        }

        // 路障没了 —— 埋伏结束。已经刷出来的不回收, 玩家得打完。
        if (status == GroupStatus.Gone)
        {
            if (state.Active)
            {
                GroupReports.Add($"埋伏 {state.Id} 结束: 路障组已清空");
            }

            state.Active = false;
            return;
        }

        if (!state.Active)
        {
            if (status != GroupStatus.Damaged)
            {
                return;
            }

            state.Active = true;
            state.NextSpawnIn = 0f;   // 第一只立刻出来, 不然玩家会觉得"打了半天没反应"
            TriggeredThisTick.Add(state.Id);
        }

        ActiveAmbushes++;

        state.NextSpawnIn -= deltaTime;
        if (state.NextSpawnIn > 0f)
        {
            return;
        }

        state.NextSpawnIn = NextInterval(state.Ambush);

        if (CountAlive(state) >= state.Ambush.MaxActiveWalkers)
        {
            return;
        }

        SpawnOne(simulation, state);
    }

    /// <summary>
    /// 补怪间隔。原厂给的是一个区间 (0.5~1 秒), 取中点。
    ///
    /// **不随机** —— 服务端要能重放, 而 <c>Math.Random</c> 会让同样的输入
    /// 产生不同的结果, 排查问题时对不上。区间中点在观感上和随机没有区别
    /// (玩家分不出 0.75 秒和 0.5~1 秒随机)。
    /// </summary>
    private static float NextInterval(Event_Ambush ambush) =>
        (ambush.RespawnRateMin + ambush.RespawnRateMax) * 0.5f;

    private enum GroupStatus
    {
        /// <summary>满血, 玩家还没开打。</summary>
        Intact,

        /// <summary>掉血了 —— 玩家正在打。</summary>
        Damaged,

        /// <summary>场上已经没有这组路障了。</summary>
        Gone,
    }

    /// <summary>
    /// 这组路障现在什么状态。
    ///
    /// 按**位置**在场上认路障 —— <c>DestructibleSpawner.Spawn</c> 里
    /// `barricade.Position = spawn.Position` 是精确复制, 所以浮点相等是安全的
    /// (同一个值搬过去, 没有经过任何运算)。
    ///
    /// 走位置而不是给实体加 GroupID 字段, 是为了不碰反编译树。
    /// </summary>
    private static GroupStatus InspectGroup(GameManager simulation, AmbushState state,
                                            out float totalHealth)
    {
        List<GameObjectBase> objects = simulation.ActiveGameObjectList;
        bool anyAlive = false;
        bool anyDamaged = false;
        totalHealth = 0f;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not _0023EK._0023Yh barricade || !barricade.IsActive)
            {
                continue;
            }

            if (!BelongsToGroup(state, barricade.Position))
            {
                continue;
            }

            // 判据是血量, 不是 IsDead —— _0023Yh 重写了 IsDead 去看状态枚举,
            // 而那个枚举每帧才刷 (理由见 DestructibleSpawner.CountAlive)。
            if (barricade.Health <= 0f)
            {
                continue;
            }

            anyAlive = true;
            totalHealth += barricade.Health;

            if (barricade.Health < barricade.MaxHealth)
            {
                anyDamaged = true;
            }
        }

        if (!anyAlive)
        {
            return GroupStatus.Gone;
        }

        return anyDamaged ? GroupStatus.Damaged : GroupStatus.Intact;
    }

    private static bool BelongsToGroup(AmbushState state, Vector2 position)
    {
        for (int i = 0; i < state.BarricadePositions.Count; i++)
        {
            if (state.BarricadePositions[i] == position)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>这组埋伏刷出来的怪还活着几只。</summary>
    private static int CountAlive(AmbushState state)
    {
        int alive = 0;

        for (int i = state.Spawned.Count - 1; i >= 0; i--)
        {
            Character zombie = state.Spawned[i];

            if (zombie == null || !zombie.IsActive || zombie.IsDead)
            {
                state.Spawned.RemoveAt(i);   // 顺手清表, 免得无限增长
                continue;
            }

            alive++;
        }

        return alive;
    }

    /// <summary>
    /// 从下一个摆位点放一只。
    ///
    /// 轮流用那 9 个点而不是随机挑 —— 同上, 服务端要能重放; 而且轮流能保证
    /// 僵尸是从四面涌来而不是挤在一个点上。
    /// </summary>
    private void SpawnOne(GameManager simulation, AmbushState state)
    {
        Type walker = ResolveWalker();
        if (walker == null)
        {
            return;
        }

        Spawn_EventEntities point = state.Spawns[state.NextSpawnPoint % state.Spawns.Count];
        int index = state.NextSpawnPoint;
        state.NextSpawnPoint++;

        Vector2 position = SpawnNextToBarricade
            ? BesideBarricade(simulation, state, index)
            : point.Position;

        Character zombie = ZombieFactory.Spawn(
            simulation, walker, position,
            UseSpawnAnimation ? EmergeState : _0023Oq._0023WM._0023Yc,
            point.Direction);

        if (zombie == null)
        {
            return;
        }

        state.Spawned.Add(zombie);
        SpawnedThisTickList.Add(zombie);
        SpawnedThisTickTargets.Add(state.Attacker);
        SpawnedThisTick++;
    }

    /// <summary>
    /// 在路障**远离玩家的那一侧**找个位置。
    ///
    /// ## 只能刷在一边
    ///
    /// 沿着路障排列的垂直方向往外撇 —— 三段路障连成一条线, 垂线就是"两侧"。
    /// 先前偶数一边、奇数另一边地轮流, 结果一半僵尸刷到了玩家背后:
    /// 玩家实测"有一部分僵尸刷到 barricade 的另外一边去了"。
    ///
    /// 埋伏的语义是"栅栏那边有东西冲过来", 从玩家背后冒出来既不合理也不公平。
    /// 所以按玩家在哪, 取**背面**那一侧。
    ///
    /// 沿着路障连线错开 (不同 index 落在不同段旁边), 僵尸就从栅栏外侧一字排开
    /// 涌过来, 而不是全挤在一个点。
    ///
    /// 拿不到玩家时 (理论上不会发生) 退化成正向的那一侧, 不抛异常。
    /// </summary>
    private static Vector2 BesideBarricade(GameManager simulation, AmbushState state, int index)
    {
        Vector2 anchor = state.BarricadePositions[index % state.BarricadePositions.Count];

        Vector2 along = state.BarricadePositions.Count > 1
            ? state.BarricadePositions[^1] - state.BarricadePositions[0]
            : Vector2.Zero;

        Vector2 outward = new Vector2(-along.Y, along.X);   // 垂线
        if (outward == Vector2.Zero)
        {
            outward = new Vector2(1f, 0f);
        }
        else
        {
            outward.Normalize();
        }

        // 取**玩家所在**的那一侧。
        //
        // 上一轮我取的是背面, 玩家实测: "Barricade 的僵尸全部刷到对侧了,
        // 应该在玩家同侧。" 栅栏那一头是玩家还没去过的区域, 僵尸刷在那边
        // 要么过不来 (栅栏挡着), 要么根本走不到玩家跟前。
        //
        // 埋伏的实际效果是"你正在砸栅栏, 背后/侧面涌上来一批" —— 同侧才对。
        Vector2 reference = state.Attacker?.Position ?? NearestPlayerPosition(simulation, anchor);
        if (Vector2.Dot(reference - anchor, outward) < 0f)
        {
            outward = -outward;
        }

        return anchor + outward * BarricadeSpawnOffset;
    }

    /// <summary>最近的玩家在哪。一个都没有时返回锚点本身 (等于不翻转)。</summary>
    private static Vector2 NearestPlayerPosition(GameManager simulation, Vector2 anchor)
    {
        Vector2 best = anchor;
        float bestDistanceSquared = float.MaxValue;

        List<Player> players = simulation.ActivePlayerList;
        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            float distanceSquared = Vector2.DistanceSquared(player.Position, anchor);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = player.Position;
            }
        }

        return best;
    }

    /// <summary>
    /// 埋伏只刷 Walker。
    ///
    /// <c>Event_Ambush</c> 有 <c>MaxActiveSpecials</c> / <c>RespawnSpecials</c>,
    /// 但教程这组是 <c>MaxActiveSpecials = 0</c> —— 没有特殊怪。
    /// 别的图用到特殊怪时再补, 现在写死 Walker 而不是猜一套特殊怪的选取规则。
    /// </summary>
    private static Type ResolveWalker()
    {
        string obfuscated = ZombieFactory.ObfuscatedNameFor(ZombieFactory.DefaultKind);
        if (obfuscated == null)
        {
            return null;
        }

        return ZombieFactory.ResolveKindTypes().TryGetValue(obfuscated, out Type type) ? type : null;
    }
}
