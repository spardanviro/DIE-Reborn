using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 教程最后一段: 补给交到卡车上之后, 围着卡车不停刷怪, 直到状态机收工。
///
/// 玩家原话: "教程无法正常结束, 在把物资放上卡车后应该不停刷怪, 然后自动结束。"
///
/// ## 刷怪点是原厂的
///
/// 地图在卡车周围铺了三组 <see cref="Spawn_EventEntities"/> (实测 dump,
/// 距卡车 (-181,3353) 的距离):
///
///   ID=10016  37 个点  平均 (-221.6, 3406.2)  d=67
///   ID=10085   8 个点  平均 (-220.3, 3386.4)  d=52
///   ID=10067   1 个点        (-251.9, 3396.0)  d=83
///
/// 46 个摆位点全部围在卡车边上 —— 那正是"守着卡车打退最后一波"的形状。
/// 这三组同时也挂在 <see cref="Event_Assault"/> 上, 由
/// <see cref="EventSpawnDirector"/> 在玩家踩到触发点时刷一批; 本类是在那之上
/// **持续**补怪, 两者不冲突 (刷的是各自记账的怪)。
///
/// ## 哪些数是我们定的
///
/// 间隔 / 每波只数 / 场上上限 / 撑多久 —— 全是我们定的, 地图数据里没有
/// "不停刷怪"这个概念 (<see cref="Event_Assault.Cooldown"/> 是单个事件的重刷
/// 冷却, 语义不同)。手感不对调这几个常量。
///
/// Floater 是例外: 客户端状态 22 (<c>_0023occ._0023iPb</c>) 明确要一只
/// (-bd/-5cc.cs:523-537 显示 FLOATER 引导 + 播 TutorialFloater01 + 把箭头
/// 挂到 <c>IsFloater</c> 的实体上), 所以它不是我们加的戏。
/// </summary>
public sealed class TutorialFinaleDirector
{
    /// <summary>
    /// 卡车位置。客户端状态 17/20/21 的箭头目标都是这个硬编码坐标
    /// (-bd/-5cc.cs:466/507/517)。
    /// </summary>
    public static readonly Vector2 TruckPosition = new Vector2(-181f, 3353f);

    /// <summary>离卡车这么近的 <see cref="Spawn_EventEntities"/> 算"卡车周边"。</summary>
    public float SpawnRadius { get; set; } = 250f;

    /// <summary>
    /// 在原厂事件之上**额外**持续补怪。默认 <b>关</b>。
    ///
    /// ## 为什么默认关
    ///
    /// 玩家 2026-09-21 报"tutorial 难度过高"。查原厂地图数据才发现卡车那一圈
    /// 本来就有三个 <c>Event_Assault</c>:
    ///
    ///     10016  Walker 45-50, 冷却 10s   (受刷怪位数限制, 实测刷出 37 只)
    ///     10085  Walker 4-4,   冷却 2s
    ///     10067  Floater 1-1,  冷却 10000s
    ///
    /// 实测把玩家放到卡车旁只跑原厂事件: 5 秒内 37 只, 之后稳住
    /// (<see cref="EventSpawnDirector"/> 的"清完才重刷"闸)。清完一批
    /// 10085 每 2 秒补 4 只 —— 原厂自己就是"一大波 + 持续小批量"的节奏。
    ///
    /// 我们这套再往上叠 6 波 (4→14, 上限 24), 场上能到 37 + 24 = 61 只。
    /// 而地图数据里**根本没有"持续补怪"这个概念** (<c>Cooldown</c> 是单个事件的
    /// 重刷冷却, 语义不同) —— 也就是说这一整套是我们凭空加的难度。
    ///
    /// 开关留着: 哪天觉得原厂太松, 打开它再调下面那几个数。
    /// </summary>
    public bool ExtraWaves { get; set; }

    /// <summary>两波之间隔多久。只在 <see cref="ExtraWaves"/> 打开时有意义。</summary>
    public float WaveInterval { get; set; } = 5f;

    /// <summary>第一波几只。</summary>
    public int ZombiesPerWave { get; set; } = 4;

    /// <summary>
    /// 每过一波多刷几只 —— 玩家要求"在卡车放物资时应该会刷越来越多的僵尸"。
    ///
    /// 配 <see cref="WaveInterval"/>(5) 和
    /// <see cref="TutorialDirector.FinaleHoldSeconds"/>(30), 这一段大约六波:
    /// 4 → 6 → 8 → 10 → 12 → 14。压力是往上走的, 而 <see cref="MaxAlive"/>
    /// 仍然兜着底, 不会真的淹掉玩家。
    ///
    /// **这是我们定的。**
    /// </summary>
    public int ZombiesAddedPerWave { get; set; } = 2;

    /// <summary>
    /// 场上还剩这么多只就不再补 —— 免得越堆越多把玩家淹了。
    ///
    /// 波次递增之后这个上限更重要: 它才是"越刷越多"的真正天花板。
    /// </summary>
    public int MaxAlive { get; set; } = 24;

    /// <summary>已经刷了几波, 递增用。</summary>
    private int _wavesFired;

    /// <summary>
    /// Floater 生成时离玩家至少这么远。
    ///
    /// 不设的话它可能直接在玩家脚下冒出来 —— 客户端那一步要把引导箭头指向它
    /// (半径 30 内箭头就收起来), 贴脸生成等于引导白做。
    /// </summary>
    public float FloaterMinDistanceFromPlayer { get; set; } = 80f;

    /// <summary>正在刷怪吗。</summary>
    public bool Running { get; private set; }

    /// <summary>累计刷了多少只, 诊断用。</summary>
    public int SpawnedTotal { get; private set; }

    /// <summary>Floater 放出去了没有。</summary>
    public bool FloaterSpawned { get; private set; }

    /// <summary>
    /// **我们放出来的那一只** Floater —— 教程状态 22 的推进判据只认它。
    ///
    /// ## 为什么不能判"场上没有 Floater 了"
    ///
    /// 玩家实测: "场上有两只 floater, 杀掉一只后教程提示还在, 箭头还指向那只的
    /// 位置, 要杀掉另外一只才行"。卡车周边那三组 <see cref="Event_Assault"/>
    /// 自己也会刷 Floater (它们有 <c>Floaters_Min/Max</c> 字段), 所以场上不止一只。
    ///
    /// 客户端那边把箭头挂到**它遍历到的第一个** <c>IsFloater</c> 实体上
    /// (-bd/-5cc.cs:529-537), 指的未必是我们这只 —— 那个错位没法从服务端修。
    /// 但至少推进判据要认准人: 剧情要求的是"打掉那只堵路的", 不是"清干净所有 Floater"。
    /// </summary>
    public Character TutorialFloater { get; private set; }

    /// <summary>剧情那只 Floater 死了没有。没生成出来时返回 false (卡住比悄悄推进好)。</summary>
    public bool TutorialFloaterIsDead =>
        FloaterSpawned && (TutorialFloater == null
                           || !TutorialFloater.IsActive
                           || TutorialFloater.IsDead);

    /// <summary>场上还活着的本类刷出来的怪, 诊断用。</summary>
    public int AliveCount { get; private set; }

    /// <summary>本次 <see cref="Update"/> 要报给宿主的事件。宿主读完自行清空。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>
    /// 本次 <see cref="Update"/> 刷出来的怪, 供宿主激怒它们。
    ///
    /// 理由见 <see cref="EventSpawnDirector.SpawnedThisTickList"/> ——
    /// 守卡车这一段尤其需要: 玩家站在卡车边不动, 而摆位点散在周围 50~83 单位处,
    /// 被动仇恨半径 55 根本罩不全, 刷出来的怪一大半会站在原地。
    /// </summary>
    public readonly List<Character> SpawnedThisTickList = new List<Character>();

    private readonly List<Vector2> _points = new List<Vector2>();
    private readonly List<_0023Oq._0023WM> _pointStates = new List<_0023Oq._0023WM>();
    private readonly List<Character> _spawned = new List<Character>();
    private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();

    private bool _scanned;
    private float _untilNextWave;
    private int _nextPoint;

    /// <summary>
    /// 扫一遍地图, 把卡车周边的刷怪点收进来。幂等。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        foreach (MapObject mapObject in simulation.MapObjects)
        {
            if (mapObject is not Spawn_EventEntities spawn)
            {
                continue;
            }

            if (Vector2.Distance(spawn.Position, TruckPosition) > SpawnRadius)
            {
                continue;
            }

            _points.Add(spawn.Position);

            // 出场动画用地图给这个点标的那个 —— 和别的刷怪源一样, 不自己发明。
            _pointStates.Add(spawn.SpawnState);
        }

        foreach (KeyValuePair<string, Type> entry in ZombieFactory.ResolveKindTypes())
        {
            _types[entry.Key] = entry.Value;
        }

        _scanned = true;
    }

    /// <summary>卡车周边收到了几个刷怪点, 诊断用。</summary>
    public int PointCount => _points.Count;

    /// <summary>
    /// 开始按波次补怪。
    ///
    /// 由 <see cref="TutorialDirector"/> 在玩家**开始**往卡车上搬补给时调
    /// (状态 21 且身上挂着交付 buff), 不是交付完成时 —— 玩家要求:
    /// "开始按X给卡车输送物资后才会像补给点那样按波次开始刷僵尸"。
    ///
    /// 那和补给点占领是同一个节奏: 读条一开始怪就来, 逼玩家一边守一边打。
    ///
    /// 幂等 —— 重复调只会重设计时器。
    /// </summary>
    public void StartWaves(GameManager simulation)
    {
        Scan(simulation);

        Running = true;
        _untilNextWave = 0f;   // 立刻来第一波
    }

    /// <summary>
    /// 放出剧情那只 Floater。
    ///
    /// 单独一步 —— 它是**状态 22 的引导目标**, 交付完成之前放出来的话,
    /// 客户端还没进状态 22, 箭头不会指它, 玩家也不知道该打谁。
    ///
    /// 幂等: 只会放一只。
    /// </summary>
    public void SpawnStoryFloater(GameManager simulation)
    {
        Scan(simulation);

        if (FloaterSpawned)
        {
            return;
        }

        // ## 先认原厂 10067 刷出来的那只
        //
        // 卡车边上有个专门的 Event_Assault (id=10067, Floater 1-1, 触发半径 100),
        // 玩家走到卡车那一步它就刷了。我们再放一只的话场上就是两只 ——
        // 玩家实测报过: "场上有两只 floater, 杀掉一只后教程提示还在, 箭头还指向
        // 那只的位置, 要杀掉另外一只才行"。根源就在这。
        //
        // 认它而不是再放一只, 那个问题自然没了; 客户端把箭头挂到"遍历到的第一个
        // IsFloater 实体"也就不会指错人 —— 场上只有一只。
        Character existing = NearestAliveFloater(simulation);

        if (existing != null)
        {
            TutorialFloater = existing;
            FloaterSpawned = true;
            Events.Add($"剧情 Floater: 认原厂事件刷的那只 @ "
                       + $"({existing.Position.X:F0},{existing.Position.Y:F0})");
            return;
        }

        // ## 兜底: 原厂那只没出来就自己放
        //
        // 10067 要玩家进 100 半径才触发。万一玩家的路线没罩到 (或者那个事件
        // 因为别的原因没刷), 状态 22 的推进判据就永远等不到 Floater, 教程卡死。
        // 宁可多放一只也不能卡住。
        if (SpawnFloater(simulation))
        {
            FloaterSpawned = true;
            Events.Add("剧情 Floater: 原厂那只没找到, 自己放了一只");
        }
    }

    /// <summary>场上活着的、离卡车最近的 Floater。没有就返回 null。</summary>
    private static Character NearestAliveFloater(GameManager simulation)
    {
        Character best = null;
        float bestDistance = float.MaxValue;

        List<GameObjectBase> objects = simulation?.ActiveGameObjectList;

        for (int i = 0; objects != null && i < objects.Count; i++)
        {
            if (objects[i] is not Character candidate
                || !candidate.IsFloater || !candidate.IsActive || candidate.IsDead)
            {
                continue;
            }

            float distance = Vector2.Distance(candidate.Position, TruckPosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// 收工。由 <see cref="TutorialDirector"/> 在进入结束状态 (23 -> 24) 时调。
    ///
    /// **只停止补怪, 不清场** —— 客户端进状态 24 就开始淡出并在 5 秒后
    /// <c>ShutDownApplication()</c> (-bd/-5cc.cs:162-175), 玩家看不了几帧,
    /// 强行把怪抹掉反而会在淡出前闪一下。
    /// </summary>
    public void Stop()
    {
        Running = false;
    }

    /// <summary>每帧一次。</summary>
    public void Update(GameManager simulation, float deltaTime)
    {
        SpawnedThisTickList.Clear();
        AliveCount = PruneAndCount();

        if (!Running || !ExtraWaves || simulation == null || _points.Count == 0)
        {
            return;
        }

        _untilNextWave -= deltaTime;
        if (_untilNextWave > 0f)
        {
            return;
        }

        _untilNextWave = WaveInterval;

        if (AliveCount >= MaxAlive)
        {
            return;
        }

        int wanted = ZombiesPerWave + _wavesFired * ZombiesAddedPerWave;
        _wavesFired++;

        // 别一波就顶穿上限 —— 只补到天花板为止。
        int room = MaxAlive - AliveCount;
        if (wanted > room)
        {
            wanted = room;
        }

        int spawned = SpawnWave(simulation, wanted);
        if (spawned > 0)
        {
            SpawnedTotal += spawned;
            Events.Add($"终局第 {_wavesFired} 波: 刷出 {spawned} 只 " +
                       $"(场上 {AliveCount + spawned}/{MaxAlive})");
        }
    }

    /// <summary>
    /// 刷一波 Walker。
    ///
    /// 刷 Walker 是**我们定的** —— 这三组 <see cref="Event_Assault"/> 自己的
    /// min/max 由 <see cref="EventSpawnDirector"/> 消费, 本类是额外的持续补怪,
    /// 没有对应的地图字段。教程全程出现的也都是 Walker。
    /// </summary>
    private int SpawnWave(GameManager simulation, int count)
    {
        Type walker = TypeOf("Walker");
        if (walker == null || count <= 0)
        {
            return 0;
        }

        int spawned = 0;

        for (int i = 0; i < count; i++)
        {
            int index = _nextPoint % _points.Count;
            _nextPoint++;

            Character zombie = ZombieFactory.Spawn(
                simulation, walker, _points[index], _pointStates[index], Vector2.Zero);

            if (zombie == null)
            {
                continue;
            }

            _spawned.Add(zombie);
            SpawnedThisTickList.Add(zombie);
            spawned++;
        }

        return spawned;
    }

    /// <summary>
    /// 放一只 Floater 出来 —— 客户端状态 22 要拿它当引导目标。
    ///
    /// 挑离玩家最远的那个刷怪点, 理由见 <see cref="FloaterMinDistanceFromPlayer"/>。
    /// 一个点都不满足距离要求时仍然用最远的那个 (总比不生成强 —— 不生成的话
    /// 状态 22 的推进判据永远不成立, 教程卡死在最后一步)。
    /// </summary>
    private bool SpawnFloater(GameManager simulation)
    {
        Type floater = TypeOf("Floater");
        if (floater == null || _points.Count == 0)
        {
            return false;
        }

        Vector2 player = NearestPlayerPosition(simulation);

        int best = 0;
        float bestDistance = -1f;

        for (int i = 0; i < _points.Count; i++)
        {
            float distance = Vector2.Distance(_points[i], player);
            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = i;
            }

            if (distance >= FloaterMinDistanceFromPlayer)
            {
                best = i;
                break;
            }
        }

        Character spawned = ZombieFactory.Spawn(
            simulation, floater, _points[best], _pointStates[best], Vector2.Zero);

        if (spawned == null)
        {
            return false;
        }

        _spawned.Add(spawned);
        TutorialFloater = spawned;
        Events.Add($"终局 Floater 出场 @({_points[best].X:F0},{_points[best].Y:F0}) " +
                   $"#{spawned.IndexGlobal}");
        return true;
    }

    /// <summary>
    /// 没有活着的玩家时返回卡车位置 —— 那时"离玩家多远"没有意义,
    /// 用卡车当参照至少不会算出 NaN。
    /// </summary>
    private static Vector2 NearestPlayerPosition(GameManager simulation)
    {
        List<Player> players = simulation.ActivePlayerList;

        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player != null && player.IsActive && !player.IsDead)
            {
                return player.Position;
            }
        }

        return TruckPosition;
    }

    /// <summary>
    /// 清掉死的, 数还活着的。
    ///
    /// 已知不精确之处和 <see cref="EventSpawnDirector"/> 那份相同: 对象池复用会
    /// 让"这只还是不是我刷的那只"变得不可靠。后果是**少刷怪**而不是错乱,
    /// 而这里还有 <see cref="WaveInterval"/> 兜底, 不会卡死。
    /// </summary>
    private int PruneAndCount()
    {
        for (int i = _spawned.Count - 1; i >= 0; i--)
        {
            Character zombie = _spawned[i];
            if (zombie == null || !zombie.IsActive || zombie.IsDead)
            {
                _spawned.RemoveAt(i);
            }
        }

        return _spawned.Count;
    }

    /// <summary>
    /// 按**可读名**从 <see cref="ZombieFactory.Kinds"/> 反查运行时类型。
    /// 不把混淆名再抄一份 —— 那张表是唯一的真相来源。
    /// </summary>
    private Type TypeOf(string readable)
    {
        string obfuscated = ZombieFactory.ObfuscatedNameFor(readable);
        if (obfuscated == null)
        {
            return null;
        }

        return _types.TryGetValue(obfuscated, out Type type) ? type : null;
    }
}
