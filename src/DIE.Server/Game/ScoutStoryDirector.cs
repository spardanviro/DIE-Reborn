using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Entities.Destructibles;
using ConductorGameLogic.MapObjects;
using ConductorGameLogic.Stats;
using ConductorGameLogic.View;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// Starter Mission 的剧情节点: Ram 撞门 和 Boss Looter。
///
/// ## 玩家给的流程
///
/// "跟着路线走打怪 -&gt; 打破三个 barricade -&gt; 出现一个 boss -&gt;
///  打完 boss 搜集物资 -&gt; 回卡车搬上去结束"
/// "ram 在玩家走到门附近, 对话开始播放时冲出来"
/// "Boss Looter 也没刷出来"
///
/// ## Ram: 触发点是地图数据给的, 不是我们挑的
///
/// <c>TransmissionType</c> 枚举里有一个 <c>SpecialRamScoutTrigger</c>,
/// 而 Scout_02_Lab 上正好有一个对话点用它 (探针实测):
///
///   id=10124  type=SpecialRamScoutTrigger  area=10088  pos=(-297.5, 438.7)
///
/// 同位置还有 id=10026 (General_Transmission06) —— 对话点和 Ram 触发点
/// 在同一个地方, 和玩家说的"对话开始播放时冲出来"对得上。
///
/// 门是 <c>Spawn_Destructible</c> 组 3005 那 3 段 <c>IronLab</c> ——
/// 全图唯一 <c>IsDisabledFromStart</c> 的一组, 由
/// <see cref="DestructibleSpawner"/> 生成成"打不破"。撞开是把它们的血量清零
/// (**不是 Destroy**, 见 <see cref="SmashDoor"/> —— 破碎表现挂在血量那条路上)。
///
/// ## Boss: Looter, 位置取 Event_RaidEvent
///
/// 所有对话点的 <c>EventType</c> 都是 <c>ScoutKillLooter</c> —— 主线目标
/// 就是杀掉那个 Looter (<c>_0023Mk._0023nPb</c>,
/// <c>GameObjectBase.IsLooter</c> 认的就是这个类)。
///
/// 地图上唯一的 <c>Event_RaidEvent</c> 在 (-689.4, 370.9), 带一个
/// <c>MainArea</c> 区域 —— 那是全图最深处, 和"走到底出现 boss"对得上。
/// 玩家进那个区域就刷。
///
/// ## 哪些数是我们定的
///
/// <see cref="RamSpawnOffset"/> (Ram 在门外多远出生)、
/// <see cref="RamTriggerPadding"/> (触发区域外扩多少) 和
/// <see cref="DoorFallbackSeconds"/> (冲出来多久后门碎) —— 地图数据里没有
/// 任何关于 Ram 出生点和撞门的字段。等玩家实测校准。
/// </summary>
public sealed class ScoutStoryDirector
{
    /// <summary>
    /// Ram 在门外多远处出生。
    ///
    /// **这是我们定的。**
    ///
    /// ## 方向: 触发点那一侧, 不是门后
    ///
    /// 第一版取的是"背离触发点"那一侧 (门后, 西边)。玩家实测:
    /// "ram 应该是刷在房子**外面**的那一侧, 现在刷在房子**里面**的那一侧。"
    ///
    /// 门后 (西边, Transmission06 的区域 10041 所在) 是室内; 触发点这一侧
    /// (东边, 玩家走过来的方向) 才是外面。所以方向要取**朝向触发点**。
    ///
    /// 玩家走到门前 -> Ram 从他身后/侧面冲过来把门撞开, 这也和玩家描述的
    /// "门被撞飞掉到竖井里"对得上 —— 撞击力是从外往里的。
    ///
    /// ## 要够远, 玩家得看不见它出场
    ///
    /// 玩家实测: "ram 在视野外刷新, 然后冲刺撞飞大门一路冲到室内。"
    /// 出场那一下不能被看到, 否则就成了"凭空冒出来"。
    ///
    /// 距离由**第一段冲刺**负责跑完 (见 <see cref="UpdateRam"/>), 而 RamCharge
    /// 的终点是 <c>Position + TargetAimDirection * 100</c> (-V2b/-xNb.cs:172),
    /// 上限 <c>_0023RE</c> = 125 —— 也就是说一段冲刺顶多 100 单位。
    ///
    /// ## 45: 由"跑过去要多久"定的
    ///
    /// 触发区域挪到室内那一侧之后, 玩家和 Ram 隔着一道**没破的门**, 多远都
    /// 看不见 —— 距离不再由"藏起来"决定, 而是由那段跑动的节奏决定。
    ///
    /// 实测 Ram 的移动速度是 8/秒。从 45 跑到撞门判定的 25, 那 20 个单位
    /// 大约 2.5 秒 —— 玩家进屋、稍等一下门就炸开。给过 90, 跑完要 8 秒往上,
    /// 中间是一段没有任何事发生的空白。
    ///
    /// 冲刺预算也够: 从 25 处起手, 那一段 100 单位能穿门再进屋 75。
    /// </summary>
    public float RamSpawnOffset { get; set; } = 45f;

    /// <summary>
    /// Ram 跑到离门这么近时**起手冲刺** —— 门还立着, 终点会被裁剪在门外。
    ///
    /// **这个数是我们定的。** 它要留得下起手前摇: 太近的话前摇还没放完就
    /// 已经贴上门了, 观感又变成"先停一下再冲"。60 大约是 4 秒跑动 + 前摇的
    /// 余量。等玩家实测校准。
    /// </summary>
    public float ChargeStartDistance { get; set; } = 60f;

    /// <summary>
    /// 一次冲刺的射程。原厂起手回调里的系数 (-V2b/-xNb.cs:172)。
    ///
    /// 只在 <see cref="ExtendCharge"/> 重算终点时用 —— 和原厂同一个数,
    /// 不另编。
    /// </summary>
    public float ChargeReach { get; set; } = 100f;

    /// <summary>
    /// Ram 冲到离门这么近时, 门播撞飞表现 + 冲刺终点延长。
    ///
    /// **这个数是我们定的**, 但**有硬下限**: 门立着时冲刺终点被裁剪, Ram
    /// 最近只能贴到离门心约 19~22 处 (实测)。阈值设得比那更小的话, 等它
    /// "够得着"时冲刺早就结束了 —— <see cref="ExtendCharge"/> 找不到对象,
    /// 终点改不了, Ram 就真停在门口。
    ///
    /// 试过 14, 结果正是如此: f183 才触发, 日志报"没找到冲刺对象"。
    ///
    /// 26 留了几个单位余量。往下调之前先看日志里有没有那句报警。
    /// </summary>
    public float DoorImpactDistance { get; set; } = 26f;

    /// <summary>
    /// 跑向门这么久还没到就别等了, 直接撞门。
    ///
    /// **兜底用的**, 不是正常路径: 防它被地形卡住时整段剧情停在那儿。
    /// </summary>
    public float ApproachTimeoutSeconds { get; set; } = 12f;

    /// <summary>Ram 出场了没有。</summary>
    public bool RamSpawned { get; private set; }

    /// <summary>门被撞开了没有。</summary>
    public bool DoorSmashed { get; private set; }

    /// <summary>那一次冲刺起手了没有 (或者已经放弃)。</summary>
    public bool Charged { get; private set; }

    /// <summary>
    /// 门碎之后多久还起不了手就放弃, 交给普通 AI。
    ///
    /// **这个数是我们定的。** 卡在门口一直试比冲不成更糟。
    /// </summary>
    public float ChargeGiveUpSeconds { get; set; } = 6f;

    /// <summary>
    /// 这张图的主线目标类型 —— **地图数据自己声明的**, 不是我们推的。
    ///
    /// ## 三张 Scout 图是三个不同的任务
    ///
    /// <c>TransmissionSpawnPoint.EventType</c> 上写着 (探针实测):
    ///
    ///   Scout_01_Outpost (Camp by Night)     ScoutStealSupplies  x5
    ///   Scout_02_Lab     (Lab Raid)          ScoutKillLooter     x9
    ///   Scout_03_Club    (Purple Moon Club)  ScoutCaptureFlag    x6
    ///
    /// 三张都另有一组 <c>ScoutReturnToTruck</c>, 那是"拿够了回卡车"的收尾。
    ///
    /// 大厅里的任务描述也对得上 (LocalizationEntries.cs:4446-4450):
    ///
    ///   Outpost  "An enemy truck has been sighted... steal the supplies from their truck"
    ///   Lab      "a **Looter** has been seen. **Kill the Looter** and bring back the supplies!"
    ///   Club     "Dr. Bauer wants you to **secure a supply point** in the area."
    ///
    /// ## 我们原来错在哪
    ///
    /// 这个 director 是照着 Lab 摸出来的, 结论"唯一那个 Event_RaidEvent 就是
    /// Boss 位置"只对 Lab 成立。套到另外两张图上, 本该是敌方卡车 / 补给点的
    /// 地方站了一只 Looter —— 玩家在 Camp by Night 上撞到的就是这个。
    ///
    /// 现在按声明的类型走: 只有 <c>ScoutKillLooter</c> 的图刷 Boss。
    ///
    /// ## 另外两张图的补给从哪来
    ///
    /// 三张 Scout 图**都没有** <c>SupplySpawnPoint</c> —— 补给只有一个来源:
    /// 杀东西掉 (<c>Supplies._0023Xj</c>, Supplies.cs:315)。Looter / 特殊僵尸 /
    /// 精英才掉, 普通 Walker 不掉。
    ///
    /// 而 <c>Event_Assault</c> 和 <c>Event_Ambush</c> **只有 15 和 17 有, 16 没有** ——
    /// 正好是"没有 Boss 的那两张"。两者都能出特殊僵尸
    /// (Assault 的 <c>SpecialZombieChance</c> 0.2、Ambush 的 <c>MaxActiveSpecials</c>),
    /// 这两个 director 我们都已经在跑。所以摘掉 Looter 不会让补给断供。
    ///
    /// ⚠️ **敌方卡车本身我们还没做**。客户端那条"偷物资"走的是
    /// <c>_0023edb</c> (敌方队伍的交付点) + <c>AbilityIdentifier.StealSupplies</c>,
    /// 而地图上只有一个 <c>TruckerSpawnPoint</c> (Team1, 玩家自己的车), 且
    /// <c>ScoutMission</c> 建 <c>Supplies</c> 时传的 <c>teamCount</c> 是 **1</c> ——
    /// 敌方那一格根本不存在, <c>_0023Nj</c> 扣敌方物资时会直接下标越界。
    /// 也就是说原版 Scout 的"偷"多半不是这条路。等玩家实测确认那个位置有没有
    /// 卡车模型再定怎么补。
    /// </summary>
    public RaidEventType Objective { get; private set; } = RaidEventType.None;

    /// <summary>
    /// 从对话点上读这张图的主线目标。
    ///
    /// 取**出现次数最多**的那个 —— 每张图都混着一组 <c>ScoutReturnToTruck</c>
    /// (收尾), 直接取第一个会撞运气。
    /// </summary>
    private static RaidEventType MainObjectiveOf(GameManager simulation) =>
        simulation.MapObjects
            .OfType<TransmissionSpawnPoint>()
            .Where(p => p.EventType != RaidEventType.None
                        && p.EventType != RaidEventType.ScoutReturnToTruck)
            .GroupBy(p => p.EventType)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    /// <summary>Boss 出场了没有。</summary>
    public bool BossSpawned { get; private set; }

    /// <summary>场上那只 Ram。</summary>
    public Character Ram { get; private set; }

    /// <summary>场上那只 Boss Looter。</summary>
    public Character Boss { get; private set; }

    /// <summary>新出场的, 宿主拿去激怒。</summary>
    public readonly List<Character> SpawnedThisTickList = new();

    /// <summary>这一帧的剧情事件, 宿主拿去打日志。</summary>
    public readonly List<string> Events = new();

    private Event_Trigger _ramArea;
    private Vector2 _ramTriggerPos;
    private Event_Trigger _bossArea;
    private Vector2 _bossPos;

    /// <summary>门那几段的位置 —— 撞开时按位置找场上的实体。</summary>
    private readonly List<Vector2> _doorPositions = new();

    private float _sinceRam;
    private bool _scanned;

    public bool IsArmed => HasRamTrigger || HasBoss;

    /// <summary>这张图有没有 Ram 撞门那一段。</summary>
    public bool HasRamTrigger => _ramArea != null;

    /// <summary>这张图该不该刷 Boss Looter —— 见 <see cref="Objective"/>。</summary>
    public bool HasBoss => _bossArea != null;

    /// <summary>
    /// 认一遍剧情节点。幂等。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        _scanned = true;

        Dictionary<int, Event_Trigger> areas = simulation.MapObjects
            .OfType<Event_Trigger>()
            .GroupBy(t => t.ID)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (Event_Trigger area in areas.Values)
        {
            area.Polygon?.CalculateVerticesWorldSpace();
        }

        // Ram: 找那个 SpecialRamScoutTrigger 的对话点
        //
        // ## 触发区域取的是**同位置那个语音点**的, 不是它自己声明的
        //
        // 玩家实测: "ram 冲刺撞门动画的触发区域你给改成了门靠近室外的那一侧了,
        // 应该在室内的那一侧。"
        //
        // 把两个区域并排量出来 (Dump_RamTriggerAreaGeometry, 门在 x=-376.19):
        //
        //   SpecialRamScoutTrigger  area=10088  X[-378,-354]  门**东**侧, 24×73
        //   General_Transmission06  area=10041  X[-458,-392]  门**西**侧, 66×120
        //
        // 两个对话点的**位置完全相同** (-297.5, 438.7), 只是各带一个区域。
        // 东边那条窄带贴着门外, 西边那块是门后的房间 (Boss 在更西的 -689)。
        //
        // 玩家是**绕另一条路先进的房间**, 然后 Ram 从门外撞进来 —— 所以触发要
        // 在西边那块。这也解释了他一直报的"门先破, 然后 ram 才出现": 用东边
        // 那条带子时玩家正站在 Ram 的出生点旁边, 当然看得见它冒出来。
        //
        // 那 10088 是干什么的? 它的质心 (-365.7, 383.1) 正好贴在门外 ——
        // 更像是**给 Ram 用的出生位置**, 不是触发器。我们的出生点是按门的法线
        // 算的 (RamSpawnPosition), 结果落在同一带上, 对得起来。
        foreach (TransmissionSpawnPoint point in
                 simulation.MapObjects.OfType<TransmissionSpawnPoint>())
        {
            if (point.TransmissionType != TransmissionType.SpecialRamScoutTrigger)
            {
                continue;
            }

            _ramTriggerPos = point.Position;

            // 同位置的剧情语音点 —— 它的区域才是"玩家进了屋"。
            TransmissionSpawnPoint colocated = simulation.MapObjects
                .OfType<TransmissionSpawnPoint>()
                .FirstOrDefault(p => p != point
                                     && p.TransmissionType != TransmissionType.SpecialRamScoutTrigger
                                     && Vector2.Distance(p.Position, point.Position) < 1f);

            int areaId = colocated?.Area.ObjectID ?? point.Area.ObjectID;

            if (!areas.TryGetValue(areaId, out _ramArea))
            {
                areas.TryGetValue(point.Area.ObjectID, out _ramArea);
            }

            break;
        }

        // 门: 全图 IsDisabledFromStart 的那些
        foreach (Spawn_Destructible door in simulation.MapObjects
                     .OfType<Spawn_Destructible>()
                     .Where(d => d.IsDisabledFromStart))
        {
            _doorPositions.Add(door.Position);
        }

        // 这张图的主线目标是什么 —— 地图数据里写着, 别照搬 Lab 的结论
        Objective = MainObjectiveOf(simulation);

        // Boss: 唯一的 Event_RaidEvent 和它的 MainArea。
        // **只有杀 Looter 那种图才刷 Boss**, 见 Objective 的说明。
        Event_RaidEvent raid = simulation.MapObjects.OfType<Event_RaidEvent>().FirstOrDefault();
        if (raid != null && Objective == RaidEventType.ScoutKillLooter)
        {
            _bossPos = raid.Position;
            areas.TryGetValue(raid.MainArea.ObjectID, out _bossArea);
        }

        // 这两句各说一次就够 —— 定期诊断只报"还可能变"的东西, 见 ReportPending。
        if (_ramArea == null)
        {
            Events.Add("这张图没有 Ram 触发区域, 不等它");
        }

        if (_bossPos == Vector2.Zero)
        {
            Events.Add($"这张图主线目标是 {Objective}, 不刷 Boss Looter");
        }

        ReleaseDoorsNobodyWillOpen(simulation);
    }

    /// <summary>
    /// 没有 Ram 来撞的门, **当场解除无敌** —— 否则那是一堵谁也过不去的墙。
    ///
    /// ## 玩家撞上的
    ///
    /// Scout_03_Club (Purple Moon Club) 绕完一圈往卡车走的路上有一道
    /// <c>WoodenBarricade</c> (组 3010, 位置约 (174,339), 卡车在 (270,261)),
    /// 打上去**一点伤害都不掉**。
    ///
    /// ## 为什么会这样
    ///
    /// <c>Spawn_Destructible.IsDisabledFromStart</c> 到底是什么意思, 地图数据里
    /// 没有别的线索, 客户端也没有任何一处读它 —— 那半边逻辑在服务端, 丢了。
    /// 我们先后给过两个解释:
    ///
    ///   一版: "不生成"    -&gt; 玩家报"那道门根本没显示", 证伪
    ///   二版: "生成但无敌" -&gt; Lab 那道门对了 (有 Ram 来撞), 但**是我们编的**
    ///
    /// 二版在 Scout_02_Lab 上成立, 是因为那张图有个 <c>SpecialRamScoutTrigger</c>,
    /// 会放一只 Ram 出来撞开门 (见 <see cref="UpdateRam"/>) —— 也就是说那道门的
    /// 无敌**有人负责解除**。
    ///
    /// Club 上没有这个触发点。同一条规则套上去, 那道木栅栏就永远无敌。
    ///
    /// ## 判据: 有没有人来开
    ///
    /// 所以规则收窄成"**有 Ram 才锁**"。没 Ram 就说明这道门原本大概是让玩家
    /// 自己打破的 —— 佐证是它的类型: Lab 那道是 <c>IronLab</c> (专用门),
    /// Club 这道是 <c>WoodenBarricade</c>, 最普通的可破坏类型。
    ///
    /// ⚠️ 这仍然是**推断**。真相也可能是原厂另有一个我们没找到的开门条件
    /// (比如交够多少物资)。但"玩家能打破"比"谁也过不去"离对更近, 而且卡在
    /// 这里连任务都做不完。
    /// </summary>
    private void ReleaseDoorsNobodyWillOpen(GameManager simulation)
    {
        if (_ramArea != null || _doorPositions.Count == 0)
        {
            return;
        }

        int released = 0;

        foreach (_0023EK._0023Yh door in DoorParts(simulation))
        {
            door._0023Uxc();
            released++;
        }

        _doorUnlocked = true;
        DoorsReleasedAtScan = released;

        _oneWay.AddRange(DoorParts(simulation));
    }

    /// <summary>
    /// 解除了无敌、但**只有一面能打**的那几段路障。
    ///
    /// 见 <see cref="IsDamageBlocked"/>。
    /// </summary>
    private readonly List<_0023EK._0023Yh> _oneWay = new List<_0023EK._0023Yh>();

    /// <summary>受这条单向规则管的路障段数。诊断用。</summary>
    public int OneWayDoorCount => _oneWay.Count;

    /// <summary>
    /// 这一下伤害该不该被挡掉 —— **路障只有"外面"那一侧打得动**。
    ///
    /// ## 玩家实测
    ///
    /// "最后通向出生地路上的那个路障, 现在两面都能造成伤害, 应该只有按正常流程
    /// 走过来的那面能对其造成伤害, 出生地的那一面无法造成伤害。"
    ///
    /// ## 哪一面是"里面"由地图数据说了算
    ///
    /// <c>Spawn_Destructible.Direction</c> 就是那道门的朝向。探针实测
    /// (Scout_03_Club 组 3010, 三段):
    ///
    ///     出生点 (246,264)
    ///     路障   (174,339) / (164,321) / (183,357), 朝向全是 (0.86,-0.51)
    ///     朝向 · 指向出生点 = 0.96 / 1.00 / 0.90
    ///
    /// 也就是说**朝向正指着出生点**。所以规则是: 攻击者在朝向那一侧
    /// (点积 &gt; 0) = 站在出生地这边 -&gt; 打不动。
    ///
    /// 这和原厂用同一个字段做的**移动阻挡**是一致的
    /// (<c>-EK/-Yh.cs:545</c>): 那段把站在 <c>_0023Gt</c> 一侧的己方玩家推回去,
    /// 也就是"从出生地这边过不去"。我们这条是它的伤害版 —— 同一个朝向,
    /// 同一个"里外"。
    ///
    /// ## 只管这几段
    ///
    /// 只对"没人来开、被我们解除了无敌"的那些生效 (见
    /// <see cref="ReleaseDoorsNobodyWillOpen"/>) —— 那批正是剧情用的单向门。
    /// 教程的栅栏、Horde 的挡路墙、Lab 那道等 Ram 撞的门都不受影响。
    /// </summary>
    /// <returns>true = 这一下应该被丢掉。</returns>
    public bool IsDamageBlocked(Entity victim, Entity attacker)
    {
        if (_oneWay.Count == 0 || victim == null || attacker == null)
        {
            return false;
        }

        for (int i = 0; i < _oneWay.Count; i++)
        {
            if (!ReferenceEquals(_oneWay[i], victim))
            {
                continue;
            }

            Vector2 facing = _oneWay[i]._0023Gt;
            if (facing == Vector2.Zero)
            {
                return false;   // 没朝向就别猜, 照旧两面都能打
            }

            return Vector2.Dot(attacker.Position - victim.Position, facing) > 0f;
        }

        return false;
    }

    /// <summary>
    /// <see cref="Scan"/> 时因为"没人来开"而解除无敌的路障段数。
    ///
    /// 单独放一个字段而不是塞进 <c>Events</c>: 那个列表每帧开头就
    /// <c>Clear()</c>, 而 Scan 发生在第一帧之前, 加进去的消息谁也看不到。
    /// </summary>
    public int DoorsReleasedAtScan { get; private set; }

    public void Update(GameManager simulation, float deltaTime)
    {
        SpawnedThisTickList.Clear();
        Events.Clear();

        if (simulation == null)
        {
            return;
        }

        UpdateRam(simulation, deltaTime);
        UpdateBoss(simulation);
        UpdateBossLoot(simulation);
        ReportPending(simulation, deltaTime);
    }

    /// <summary>Boss 掉过物资了没有。</summary>
    public bool BossLootDropped { get; private set; }

    /// <summary>
    /// Boss 死了就抛撒物资。
    ///
    /// ## 为什么要我们做
    ///
    /// 原厂那段囤积者死亡掉落 (Character.cs:2911) 写着:
    ///
    ///     if (GameMode.IsScavengerHunt &amp;&amp; IsHoarderZombie &amp;&amp; !IsLooter)
    ///
    /// 两个条件都把我们排除在外 —— 这里是 ScoutMission 不是 ScavengerHunt,
    /// 而且 <c>!IsLooter</c> 明确把 Looter 摘了出去。也就是说 **Looter 的掉落
    /// 本来就走另一条路**, 那条路在服务端, 随服务端丢失。
    ///
    /// 玩家实测: "BOSS LOOTER 死亡掉落的物资箱只有一个, 应该掉落和占领补给点后
    /// 同样数量的物资, 且要播放物资箱掉落动画。" —— 所以直接复用占领补给点那套
    /// (<see cref="SupplyThrower"/>), 数量也用同一组默认值。
    ///
    /// 抛撒器 <c>_0023cyc</c> 在日志里就叫 BossSupplyAreaEffect, 名字里的 "Boss"
    /// 不是巧合 —— 它本来就是给这个场合用的, 掉落动画也由它播。
    ///
    /// ## 判据用血量, 不用 IsDead
    ///
    /// 和 <see cref="DestructibleSpawner.CountAlive"/> 同一个理由: 血刚归零的
    /// 那一帧 <c>IsDead</c> 还没翻过来。用血量是即时的, 少等一帧。
    /// </summary>
    private void UpdateBossLoot(GameManager simulation)
    {
        if (BossLootDropped || Boss == null || !BossSpawned)
        {
            return;
        }

        if (Boss.Health > 0f && !Boss.IsDead)
        {
            return;
        }

        BossLootDropped = true;

        bool thrown = SupplyThrower.Throw(simulation, Boss.Position, owner: Boss);

        Events.Add(thrown
            ? $"Boss 掉落物资 {SupplyThrower.DefaultCrateCount} 包 × "
              + $"{SupplyThrower.DefaultSuppliesPerCrate} @ "
              + $"({Boss.Position.X:F0},{Boss.Position.Y:F0})"
            : "Boss 掉落物资失败 —— 对象池里没有抛撒器?");
    }

    /// <summary>
    /// 还没触发的节点, 每隔一会儿报一次玩家离它有多远。
    ///
    /// ## 为什么需要
    ///
    /// 玩家实测: "Ram 没有撞破门出现。" 这句话有两种完全不同的原因,
    /// 服务端日志里却长得一样 (什么都没有):
    ///
    ///   a. 玩家压根没走进触发区域 —— 区域范围和实际路线对不上
    ///   b. 触发了、Ram 也生成了, 但玩家没看见 (生在墙里 / 门另一侧)
    ///
    /// b 会留下 "Ram 从门后冲出 @ (x,y)" 那条日志, a 什么都不留。所以只要
    /// 定期把"玩家在哪、离触发区域多远"打出来, 下次真机日志一看就分得清,
    /// 不用再猜。
    ///
    /// 间隔取 5 秒: 够密, 能看出玩家路过区域边缘; 又不至于把日志刷爆。
    /// </summary>
    private void ReportPending(GameManager simulation, float deltaTime)
    {
        // ## 只报**还可能变**的东西
        //
        // 真机日志实测 (2026-09-10, Outpost + Club 两张图各一局):
        //
        //     2477 条  "Ram 待触发: 这张图上没有它的触发区域"
        //     2477 条  "Boss 还没放上去: Event_RaidEvent 位置 (0,0)"
        //
        // 加起来占了那份 1.6MB 日志的一多半。两条报的都是**永远不会变的
        // 事实**: 这两张图没有 SpecialRamScoutTrigger, 主线目标也不是杀
        // Looter (只有 Lab 那张刷 Boss, 见 Objective)。
        //
        // 定期诊断的用处是"看玩家离触发点还有多远", 前提是那个触发点存在。
        // 不存在就该在 Scan 里说一次然后闭嘴 —— 每 5 秒复述一遍同一句话,
        // 淹掉的正是真正要看的那几行。
        bool ramPending = !RamSpawned && _ramArea != null;
        bool bossPending = !BossSpawned && _bossPos != Vector2.Zero;

        if (!ramPending && !bossPending)
        {
            return;
        }

        _sinceReport += deltaTime;
        if (_sinceReport < DiagnosticIntervalSeconds)
        {
            return;
        }

        _sinceReport = 0f;

        if (ramPending)
        {
            Events.Add("Ram 待触发: "
                       + DescribeDistance(simulation, _ramArea, RamTriggerPadding));
        }

        // Boss 现在是开局就摆上去的 (见 UpdateBoss), 没生成出来只可能是
        // 对象池里没有 Looter —— 报位置比报"离玩家多远"有用。
        if (bossPending)
        {
            Events.Add($"Boss 还没放上去: Event_RaidEvent 位置 "
                       + $"({_bossPos.X:F0},{_bossPos.Y:F0})");
        }
    }

    /// <summary>
    /// 最近的活玩家在哪, 离这个**区域**多远, 判定进没进。
    ///
    /// 量的是到区域**质心**的距离, 不是到那个 TransmissionSpawnPoint 的标记点 ——
    /// 标记点和它的 Area 是两回事, Ram 那个点就离自己的区域有 88 单位远,
    /// 拿它报距离只会误导。
    /// </summary>
    private static string DescribeDistance(GameManager simulation, Event_Trigger area,
                                           float padding = 0f)
    {
        if (area?.Polygon == null)
        {
            return "这张图上没有它的触发区域";
        }

        area.Polygon.CalculateVerticesWorldSpace();
        Vector2[] verts = area.Polygon.VerticesWorldSpace;

        Vector2 centroid = Vector2.Zero;
        foreach (Vector2 v in verts)
        {
            centroid += v;
        }

        centroid /= verts.Length;

        Player nearest = null;
        float best = float.MaxValue;

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            float distance = Vector2.Distance(player.Position, centroid);
            if (distance < best)
            {
                best = distance;
                nearest = player;
            }
        }

        if (nearest == null)
        {
            return "场上没有活着的玩家";
        }

        bool strict = _00236._0023huc._00237tc(area, nearest.Position, nearest.Radius);
        bool padded = padding > 0f
            && _00236._0023huc._00237tc(area, nearest.Position, nearest.Radius + padding);

        return $"最近玩家 @ ({nearest.Position.X:F0},{nearest.Position.Y:F0}) "
               + $"离区域 {area.ID} 中心 {best:F0}, 严格判定={strict}"
               + (padding > 0f ? $", 外扩 {padding:F0} 后={padded}" : string.Empty);
    }

    /// <summary>诊断日志的间隔。</summary>
    public float DiagnosticIntervalSeconds { get; set; } = 5f;

    private float _sinceReport;

    private void UpdateRam(GameManager simulation, float deltaTime)
    {
        if (!RamSpawned)
        {
            if (_ramArea == null
                || !AnyPlayerInside(simulation, _ramArea, RamTriggerPadding))
            {
                return;
            }

            _ramSpawnPos = RamSpawnPosition();
            Ram = SpawnStoryNpc(simulation, "Ram", _ramSpawnPos);
            RamSpawned = true;
            _sinceRam = 0f;

            // 门生成时被 _0023Txc() 设成 Defense=1 + NoSpellCollision。后者会让
            // RamCharge 的碰撞检测直接跳过它, 所以先用配套的 _0023Uxc() 解除。
            UnlockDoor(simulation);

            if (Ram != null)
            {
                // ## 立刻放行出场状态, 而且**先不交给普通 AI**
                //
                // NPC.ChangeDirection (NPC.cs:1010) 第一个条件就是
                // SpawnState_Current == _0023Yc, 不满足连 MoveDirection 都不写 ——
                // 不放行的话它跑不动, 只能站在原地等超时。
                //
                // 平时这一步由 ZombieBrain.FinishEmerge 按出场动画时长做, 这里
                // 提前做掉: Ram 在门外, 玩家隔着一道没破的门看不见它,
                // 出场动画本来就没人看。
                //
                // 不进 SpawnedThisTickList —— 那张表会让宿主当场 Provoke,
                // 被激怒的 NPC 由 ZombieBrain 拽着追玩家, 和我们每帧写的
                // "朝门跑"打架。等这段剧情演完 (Charged) 再交出去。
                if (Ram is NPC ramNpc)
                {
                    ramNpc.SpawnState_Current = _0023Oq._0023WM._0023Yc;
                }

                Events.Add($"Ram 出场 @ ({Ram.Position.X:F0},{Ram.Position.Y:F0})");
            }
            else
            {
                Events.Add("Ram 生成失败 —— 对象池里没有它?");
            }

            return;
        }

        if (Charged)
        {
            return;
        }

        _sinceRam += deltaTime;

        float toDoor = Vector2.Distance(Ram.Position, DoorCenter());

        // ## 顺序: 跑近 -> 起手冲刺 -> **接触瞬间**碎门并延长冲刺终点
        //
        // 玩家实测 (第五轮): "之前半路停住是因为被门的模型挡住了, 那只要在 ram
        // 接触到门前的瞬间让门销毁就行, 同时播放门被撞飞的动画, 然后 ram 正常
        // 冲入室内。现在的表现还是门先被破坏, 然后 ram 才开始冲刺。"
        //
        // 他这个判断是对的, 而且指出了上一版的毛病: 上一版是"跑到 25 处 ->
        // 碎门 -> 起手冲刺"。碎门和起手只差 2 帧, 但 RamCharge 有**起手前摇**,
        // 玩家看到的就是"门炸了, Ram 才开始蓄力冲"。
        //
        // 现在把冲刺**提前**到还没到门时起手 (ChargeStartDistance):
        //
        //   1. 起手时门还立着 -> 冲刺终点被静态几何裁剪顶在门外
        //      (_0023sH._0023qH, -Oq/-sH.cs:357)
        //   2. Ram 冲到 DoorImpactDistance 时: 碎门 + **改写冲刺终点**
        //   3. 冲刺继续, 一路进屋
        //
        // 第 2 步的改写是关键。终点是冲刺对象 _0023EF._0023r3b 上的公开字段
        // _0023Fc (-EF/-zX.cs:63), 而且它是同步字段 (_0023wX 里就写它),
        // 改了客户端跟着改。裁剪只在起手那一下算一次, 之后没人再管 ——
        // 门没了, 终点也就没有理由停在门外。
        //
        // 这样全程**只有一次冲刺**, 门在 Ram 真正贴上去那一刻才飞。
        if (!_chargeIssued)
        {
            if (toDoor > ChargeStartDistance && _sinceRam < ApproachTimeoutSeconds)
            {
                RunTowardDoor(simulation);
                return;
            }

            StopRunning();

            if (!Charge(InwardNormal()))
            {
                if (_sinceRam > ApproachTimeoutSeconds + ChargeGiveUpSeconds)
                {
                    Charged = true;
                    SpawnedThisTickList.Add(Ram);
                    Events.Add("Ram 冲刺起不了手, 交给普通 AI");
                }

                return;
            }

            _chargeIssued = true;
            Events.Add($"Ram 起手冲刺 (离门 {toDoor:F0}, 出场后 {_sinceRam:F1} 秒)");
            return;
        }

        if (DoorSmashed)
        {
            return;
        }

        _sinceSmash += deltaTime;

        // 还没贴上门就继续冲 —— 但别等太久 (冲刺可能提前结束)。
        if (toDoor > DoorImpactDistance && _sinceSmash < ChargeGiveUpSeconds)
        {
            return;
        }

        DoorSmashed = true;

        int smashed = SmashDoor(simulation);
        bool extended = ExtendCharge(simulation);

        Charged = true;
        SpawnedThisTickList.Add(Ram);   // 演完了, 交给普通 AI 追人

        Events.Add($"Ram 撞上大门 (离门 {toDoor:F0}): 门飞出去 {smashed} 段, "
                   + (extended ? "冲刺终点已延长进室内" : "**没找到冲刺对象, 终点没改**"));
        return;
    }

    /// 解除门的无敌, 让 RamCharge 打得动它。
    ///
    /// <c>_0023Txc()</c> / <c>_0023Uxc()</c> 是原厂配套的一对
    /// (-EK/-Yh.cs:640/648), <c>HordeSpawner</c> 给挡路路障用的也是它们。
    /// 幂等 —— 每帧调也没关系。
    /// </summary>
    private void UnlockDoor(GameManager simulation)
    {
        if (_doorUnlocked)
        {
            return;
        }

        _doorUnlocked = true;

        foreach (_0023EK._0023Yh door in DoorParts(simulation))
        {
            door._0023Uxc();
        }
    }

    /// <summary>
    /// 门那几段是不是都翻成"已破坏"了 —— 也就是 NoCollision 已经加上。
    ///
    /// 判 <c>IsDead</c> 而不是判血量: 血量归零和状态翻转差一帧, 而冲刺要等的
    /// 是**状态**那一步 (见 <see cref="UpdateRam"/> 里的说明)。
    /// </summary>
    private bool DoorFullyBroken(GameManager simulation)
    {
        bool any = false;

        foreach (_0023EK._0023Yh door in DoorParts(simulation))
        {
            any = true;

            if (!door.IsDead)
            {
                return false;
            }
        }

        return any;
    }

    /// <summary>门那几段还有活着的没有。</summary>
    private bool AnyDoorAlive(GameManager simulation)
    {
        foreach (_0023EK._0023Yh door in DoorParts(simulation))
        {
            if (door.Health > 0f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>场上属于那道门的路障段 —— 按位置认, 实体上没有组号。</summary>
    private IEnumerable<_0023EK._0023Yh> DoorParts(GameManager simulation)
    {
        foreach (_0023EK._0023Yh barricade in simulation.ActiveGameObjectList
                     .OfType<_0023EK._0023Yh>())
        {
            if (!barricade.IsActive)
            {
                continue;
            }

            foreach (Vector2 doorPosition in _doorPositions)
            {
                if (Vector2.Distance(barricade.Position, doorPosition) <= 1f)
                {
                    yield return barricade;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 让 Ram 朝门冲。
    ///
    /// <summary>
    /// 把正在进行的那次冲刺的终点改到室内 —— 门没了, 它就没理由停在门外。
    ///
    /// ## 为什么改得动
    ///
    /// 终点是冲刺对象 <c>_0023EF._0023r3b</c> 上的公开字段 <c>_0023Fc</c>
    /// (-EF/-zX.cs:63)。它在**起手那一下**由回调算出来
    /// (-V2b/-xNb.cs:172): <c>Position + TargetAimDirection * 100</c>, 再过一遍
    /// 静态几何裁剪 —— 门立着的时候就被顶回门外。
    ///
    /// 但那个裁剪**只算一次**, 之后没有任何代码再碰它。所以门碎的同一帧把
    /// 终点重写成"不裁剪的那个值", 冲刺就会一路走到底。
    ///
    /// <c>_0023Fc</c> 还是同步字段 (<c>_0023wX</c> 里就写它), 改了客户端
    /// 跟着改, 不会出现两边位置对不上。
    ///
    /// ## 怎么找到那个对象
    ///
    /// 冲刺对象在场上, <c>Owner</c> 是 Ram。场上同时可能有别的特效对象,
    /// 所以两个条件都要判。
    /// </summary>
    /// <returns>找到并改了返回 true。</returns>
    private bool ExtendCharge(GameManager simulation)
    {
        if (Ram == null)
        {
            return false;
        }

        // 100 是原厂起手回调里那个系数 (-V2b/-xNb.cs:172), 不另编一个数。
        Vector2 target = Ram.Position + InwardNormal() * ChargeReach;

        bool changed = false;

        foreach (_0023EF._0023r3b charge in simulation.ActiveGameObjectList
                     .OfType<_0023EF._0023r3b>())
        {
            if (!charge.IsActive || !ReferenceEquals(charge.Owner, Ram))
            {
                continue;
            }

            charge._0023Fc = target;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 让 Ram 朝门跑 —— 把门当成它的目标, 剩下交给 NPC 自己的 AI。
    ///
    /// ## 不能直接写 MoveDirection
    ///
    /// 第一版用的是 <c>ChangeDirection</c> (直接写 <c>MoveDirection</c>)。
    /// 实测 Ram 一步没挪, 探针显示 <c>MoveDirection</c> 每帧都是 0 ——
    /// NPC 自己的 AI 每帧会把它重写一遍, 我们在帧前写的当场被盖掉。
    ///
    /// <see cref="ZombieBrain"/> 走的是另一条路: 设 <c>GoalTarget</c> +
    /// <c>UseGoalTarget</c>, 让 NPC 的 AI **自己**去算方向和寻路。那才是
    /// 支持的用法, 顺带还能绕开障碍。
    ///
    /// 门那几段本身就是实体 (<c>_0023EK._0023Yh : Destructible</c>),
    /// 直接拿来当目标 —— 不用另造一个假目标。
    ///
    /// <c>ForceGoalTarget</c> 一起设: 不设的话 NPC 见到更近的敌人会自己换目标,
    /// 这段剧情就跑偏了。
    /// </summary>
    private void RunTowardDoor(GameManager simulation)
    {
        if (Ram is not NPC ram)
        {
            return;
        }

        if (ram.GoalTarget != null && ram.UseGoalTarget)
        {
            return;   // 已经在跑了
        }

        _0023EK._0023Yh door = DoorParts(simulation).FirstOrDefault();
        if (door == null)
        {
            return;
        }

        ram.GoalTarget = door;
        ram.UseGoalTarget = true;
        ram.ForceGoalTarget = true;
    }

    /// <summary>
    /// 撞门那一刻松开目标 —— 接着起手的冲刺自己接管位移。
    ///
    /// 不松的话 AI 还在往那个 (已经碎了的) 门跑, 和冲刺的位移叠在一起。
    ///
    /// 注意 <c>UseGoalTarget = true</c> 而 <c>GoalTarget == null</c> 是个坑
    /// (ZombieBrain:132 那条注释记着), 所以两个一起清。
    /// </summary>
    private void StopRunning()
    {
        if (Ram is not NPC ram)
        {
            return;
        }

        ram.ForceGoalTarget = false;
        ram.UseGoalTarget = false;
        ram.GoalTarget = null;
    }

    /// <summary>
    /// 门的法线, 朝室内那一侧 —— 冲刺要**垂直穿过**门洞。
    ///
    /// ## 斜着冲会被裁剪砍回门口
    ///
    /// 第一版方向取的是 "门心 - Ram 出生点", 那是条西南向的斜线。实测导航网格:
    ///
    ///   门线 (x=-376) 上可走区间 y=349..417, 净宽 68; Ram 半径只有 7
    ///
    /// 门洞宽得很, 塞得进去。但冲刺终点是 <c>Position + 方向 * 100</c>, 斜着
    /// 走 100 单位就撞进门后房间的西南角墙里, 起手时的碰撞裁剪
    /// (<c>_0023sH._0023qH</c>) 于是把终点砍回接触之前 —— 也就是门口。
    /// 实测 Ram 停在 x≈-368 来回震荡, 差 8 单位没过门线。
    ///
    /// 门那三段沿 Y 排成一条线 (x 都是 -376.19), 所以法线就是 X 轴。这里不写死,
    /// 按段与段的排列方向求垂线, 另外两张 Scout 图上的门朝哪边都算得对。
    ///
    /// 符号取朝内那一侧 —— 出生点在门外, 所以和 "门心 - 出生点" 同向的那个。
    /// </summary>
    private Vector2 InwardNormal() => -OutwardNormal();

    /// <summary>
    /// 门的法线, 朝门外那一侧 —— Ram 从这个方向出生, 也从这个方向冲进来。
    ///
    /// ## 出生点和冲刺必须同轴
    ///
    /// 上一版出生点走的是"门心 -&gt; 触发点"那条**斜线** (触发点在门的东北方),
    /// 而冲刺走的是门的法线 (正东西)。两者差了一个 Y 方向的偏移, 结果 Ram
    /// 出生在 y=406 —— 门洞可走区间是 y=349..417, 它贴着上沿。
    ///
    /// 从那里垂直往西冲, 一头撞在门框上, 起手裁剪当场把终点砍掉:
    /// 实测 Ram 只挪了 2 个单位。
    ///
    /// 现在出生点也用法线算, Ram 正对着门洞中心, 一条直线穿过去。
    ///
    /// ## 法线怎么求
    ///
    /// 门那几段沿一条线排列 (Scout_02_Lab 上三段的 x 都是 -376.19), 取相距
    /// 最远的两段连成走向, 垂线就是法线。不写死 X 轴 —— 另外两张 Scout 图上
    /// 门朝哪边都算得对。
    ///
    /// 符号取朝**触发点**那一侧: 玩家是从门外走过来才触发的, 所以触发点一定
    /// 在门外。(第一版拿"门心 - 出生点"定符号, 而出生点又要靠法线算 ——
    /// 循环依赖, 也是那一版只能用斜线的原因。)
    /// </summary>
    private Vector2 OutwardNormal()
    {
        Vector2 center = DoorCenter();
        Vector2 outward = _ramTriggerPos - center;

        if (_doorPositions.Count < 2)
        {
            return Normalized(outward);   // 单段门量不出走向, 只能用连线
        }

        // 相距最远的两段 —— 它们的连线就是门的走向
        Vector2 a = _doorPositions[0];
        Vector2 b = _doorPositions[0];
        float best = -1f;

        for (int i = 0; i < _doorPositions.Count; i++)
        {
            for (int j = i + 1; j < _doorPositions.Count; j++)
            {
                float d = Vector2.DistanceSquared(_doorPositions[i], _doorPositions[j]);
                if (d > best)
                {
                    best = d;
                    a = _doorPositions[i];
                    b = _doorPositions[j];
                }
            }
        }

        Vector2 axis = b - a;
        if (axis == Vector2.Zero)
        {
            return Normalized(outward);
        }

        axis.Normalize();

        Vector2 normal = new Vector2(-axis.Y, axis.X);
        return Vector2.Dot(normal, outward) < 0f ? -normal : normal;
    }

    private static Vector2 Normalized(Vector2 v)
    {
        if (v == Vector2.Zero)
        {
            return Vector2.UnitX;
        }

        v.Normalize();
        return v;
    }

    /// <summary>门那几段的质心。</summary>
    private Vector2 DoorCenter()
    {
        Vector2 center = Vector2.Zero;
        foreach (Vector2 p in _doorPositions)
        {
            center += p;
        }

        return center / _doorPositions.Count;
    }

    /// <summary>
    /// 让 Ram 朝 <paramref name="direction"/> 冲一次。
    /// </summary>
    private bool Charge(Vector2 direction)
    {
        if (Ram is not NPC ram || !ram.IsActive || ram.IsDead
            || direction == Vector2.Zero)
        {
            return false;
        }

        direction.Normalize();

        // **两个朝向都要设。** RamCharge 的起手回调 (-V2b/-xNb.cs:170) 同时读了
        // 它们, 而且读的是不同的:
        //
        //     _0023Gt = AimDirection                              (冲刺/特效方向)
        //     _0023Fc = Position + TargetAimDirection * 100       (冲刺终点)
        //
        // ChangeAimDirection 只写 TargetAimDirection —— AimDirection 要花几帧
        // 转过去。第一帧就起手的话方向还是出生朝向 (UnitY, 朝上), 终点却在门那边,
        // 冲刺当场走偏、撞不到门。
        //
        // AimDirection 的 setter 只写字段没有副作用 (Character.cs:547), 直接设是
        // 安全的 —— ZombieFactory.Spawn 设出生朝向时用的也是它。
        ram.AimDirection = direction;
        ram.ChangeAimDirection(direction);

        return ram.Attack(direction, AbilityIdentifier.RamCharge);
    }

    private bool _doorUnlocked;
    private bool _chargeIssued;
    private float _sinceSmash;
    private Vector2 _ramSpawnPos;

    /// <summary>
    /// Boss Looter 出场。
    ///
    /// ## 开局就摆在那里, 不等玩家走近
    ///
    /// 第一版是"玩家进 <c>Event_RaidEvent</c> 的 MainArea 才生成"。玩家实测:
    /// **"Looter 现在是走到附近凭空出现的, 原版是提前就在那里的。"**
    ///
    /// 想想也对 —— <see cref="Event_RaidEvent"/> 上那个 MainArea 是**任务区域**
    /// (客户端拿它画小地图圈、算"你是不是在打 boss"), 不是刷怪触发器。
    /// 地图上真正的"走近才刷"是另一套 (<see cref="ProximitySpawnDirector"/> 认的
    /// 那批点)。我把区域的用途认错了。
    ///
    /// 所以现在只用 <c>Event_RaidEvent</c> 的**位置**, 在
    /// <see cref="Scan"/> 之后第一帧就把它放上去。玩家一路打过去, 它本来就在
    /// 房间里等着 —— 和原版一致, 也和玩家说的"走到底出现 boss"不冲突
    /// (走到底才**看见**, 不是走到底才**生成**)。
    /// </summary>
    private void UpdateBoss(GameManager simulation)
    {
        if (BossSpawned || _bossPos == Vector2.Zero)
        {
            return;
        }

        Boss = SpawnStoryNpc(simulation, "Looter", _bossPos);
        BossSpawned = true;

        if (Boss != null)
        {
            // **不进 SpawnedThisTickList** —— 那张表是给宿主"出场即激怒"用的
            // (LidgrenHost.ProvokeSpawned), 而被激怒的 NPC 无视仇恨半径
            // (ZombieBrain:352 直接给 float.MaxValue), 从生成那一刻就一路走向玩家。
            //
            // 玩家实测: "Looter 现在一开始就会往玩家的方向走, 应该设置仇恨距离。"
            //
            // 它现在是开局就摆在房间里的, 不是"刷出来扑上去"的埋伏怪 ——
            // 该走普通的被动仇恨 (ZombieBrain.AggroRadius, 55), 玩家走进去才动。
            Events.Add($"Boss Looter 就位 @ ({Boss.Position.X:F0},{Boss.Position.Y:F0}) "
                       + "(被动仇恨, 不预先激怒)");
        }
        else
        {
            Events.Add("Boss Looter 生成失败 —— 对象池里没有它?");
        }
    }

    /// <summary>
    /// Ram 的出生点: 门的中心, 往**触发点那一侧**推开一段 —— 也就是门外。
    /// 找不到门就直接用触发点旁边。
    ///
    /// 方向见 <see cref="OutwardNormal"/>, 距离见 <see cref="RamSpawnOffset"/>。
    /// </summary>
    private Vector2 RamSpawnPosition()
    {
        if (_doorPositions.Count == 0)
        {
            return _ramTriggerPos;
        }

        // 沿**法线**往外推, 不是沿"门心 -> 触发点"那条斜线 ——
        // 出生点得和冲刺同轴, 理由见 OutwardNormal。
        return DoorCenter() + OutwardNormal() * RamSpawnOffset;
    }

    /// <summary>
    /// 把门打碎 —— **走血量, 不用 Destroy()**。
    ///
    /// ## 为什么这一步是关键
    ///
    /// 玩家实测: "ram 撞破门不是真的攻击门, 是播放一段动画, 门被撞飞掉到
    /// 竖井里了。" 而我们这边"门直接消失了"。
    ///
    /// 差别不在伤害, 在**怎么让它没的**。原厂的破坏表现挂在血量那条路上
    /// (-EK/-Yh.cs:493 `UpdateDestructible`, 服务端分支):
    ///
    ///     血量 &lt;= 1  ->  BarricadeStatus = _0023Xxc (已破坏)
    ///                  ->  加 NoCollision, 关 SuperCollision
    ///
    /// <c>BarricadeStatus</c> 是同步字段, 客户端收到它翻成"已破坏"才会播那段
    /// 破碎/撞飞的表现 (IronLab 的 CENTER 段配的是
    /// <c>Vfx_Barricade_Iron_breakdown_01</c>, -EK/-Yh.cs:333)。
    ///
    /// 我们原来直接 <c>Destroy()</c> —— 实体当场从场上摘掉, <c>UpdateDestructible</c>
    /// 再也不会跑, 状态永远停在"完好", 客户端那边就只是模型凭空不见了。
    /// **表现一直都在, 是我们绕过了触发它的那条路。**
    ///
    /// 上一轮我为了这个去解锁门、让 RamCharge 打它、查冲刺为什么停在门前
    /// 19 单位 —— 方向从一开始就偏了: 要的不是"谁来打", 是"怎么死"。
    ///
    /// ## changer 传 Ram
    ///
    /// 破坏后那段 (-EK/-Yh.cs:519 起) 会找最近的玩家算归属。伤害来源记成 Ram
    /// 更贴近"它撞的"这个事实, 也和原厂 RamCharge 命中时的写法一致
    /// (-EF/-r3b.cs:174 传的是技能自己)。
    /// </summary>
    private int SmashDoor(GameManager simulation)
    {
        int smashed = 0;

        foreach (_0023EK._0023Yh barricade in DoorParts(simulation).ToList())
        {
            if (barricade.Health <= 1f)
            {
                continue;   // 已经碎了
            }

            simulation.ChangeStats(
                barricade, StatType.Health, _00230k._0023UIc._0023RIc,
                -(barricade.Health + 1f), StatChangeReason._0023DH, Ram ?? (Entity)barricade);

            smashed++;
        }

        return smashed;
    }

    /// <summary>
    /// 生成一只剧情 NPC。名字走 <see cref="ZombieFactory.Kinds"/> 那张表
    /// (Ram 在里面); Looter 不在, 单独按类名解析。
    /// </summary>
    private static Character SpawnStoryNpc(GameManager simulation, string kind, Vector2 position)
    {
        Type type = ResolveType(kind);
        if (type == null)
        {
            return null;
        }

        // 吸附到可行走面 —— **加固, 不是修 bug**。
        //
        // 玩家报"Boss Looter 无法移动"时我先猜是出生点落在导航网格外, 加了这一步,
        // 然后写测试验证 —— 当场被证伪: Ram 和 Boss 的地图标记本来就可行走,
        // Boss 的坐标压根没被吸附。真正的根因是加速度从来没被赋值过, 见
        // <see cref="ZombieFactory.EnsureCanMove"/>。
        //
        // 这一步留着是因为地图标记**不保证**可行走 —— 换张图就可能踩到,
        // 而吸附对本来就合法的坐标是恒等的 (已验证)。但别再把它当成
        // "不动"的解释了。
        position = SnapToWalkable(simulation, position);

        // 出场状态取默认 —— 剧情 NPC 不走"从地里钻出来"那套
        return ZombieFactory.Spawn(simulation, type, position,
                                   default(_0023Oq._0023WM), Vector2.UnitY);
    }

    /// <summary>把出生点贴到导航网格上。拿不到网格就原样返回。</summary>
    private static Vector2 SnapToWalkable(GameManager simulation, Vector2 position)
    {
        ConductorGameLogic.Pathfinding.NavMesh mesh = MapManager.GetNavMesh(simulation.MapIndex);
        if (mesh?.Triangles == null || mesh.Triangles.Length == 0)
        {
            return position;
        }

        return new NavMeshPathfinder(mesh).SnapToWalkable(position);
    }

    private static Type ResolveType(string kind)
    {
        // Looter 不在 ZombieFactory.Kinds 里 (那张表只有七种僵尸)
        if (kind == "Looter")
        {
            return typeof(_0023Mk._0023nPb);
        }

        string obfuscated = ZombieFactory.ObfuscatedNameFor(kind);
        if (obfuscated == null)
        {
            return null;
        }

        return ZombieFactory.ResolveKindTypes().TryGetValue(obfuscated, out Type type)
            ? type
            : null;
    }

    /// <summary>
    /// Ram 触发区域的外扩量。**这个数是我们定的**, 地图数据里没有。
    ///
    /// ## 为什么只给 Ram 加
    ///
    /// 玩家实测三轮都是 "ram 还是没有出现" —— 而同一套生成、脑子、区域判定
    /// 在 Boss 和语音上都跑通了。差别在区域本身: 把两个区域并排量出来
    /// (见 <c>Dump_RamTriggerAreaGeometry</c>) 就看得很清楚:
    ///
    ///   General_Transmission06  area=10041   66 × 120   在门**西侧**(门后)
    ///   SpecialRamScoutTrigger  area=10088   **24 × 73**  紧贴门的东侧
    ///
    /// 24 单位宽的一条窄带, 而且它西边界 (-378) 就压在门上 (门在 x=-376.19)。
    /// 玩家从东边过来会被门的碰撞体挡住, 能站的地方只剩窄带东侧那十几个单位;
    /// 贴着门斜着走过去、中心始终没落进这条带子, 是很现实的。
    ///
    /// 15 大约是玩家半径的五倍。挑那个量级是为了补上"被门挡住站不进去"的余量。
    ///
    /// **但 15 太多了。** 玩家实测: "触发这段动画的区域太大, 我现在在门室外的
    /// 那一侧也能触发这段语音播报和动画。" 那条带子本身只有 24 宽, 外扩 15
    /// 等于把它撑到两倍多, 玩家还没走到门口就触发了。
    ///
    /// 收到 4 —— 略小于玩家半径, 只补"站在边界上因为浮点/碰撞差一点点"那种,
    /// 不再往外借地方。要是又变成触发不了, 往上加而不是一次给到 15。
    ///
    /// Boss 的区域没有这个问题 (玩家实测已经正常), 所以**不动它** ——
    /// 外扩是针对这一个窄区域的补偿, 不是普适规则。
    /// </summary>
    public float RamTriggerPadding { get; set; } = 4f;

    /// <param name="padding">
    /// 额外的外扩量, 加在玩家半径上。默认 0 = 严格按区域判。
    /// </param>
    private static bool AnyPlayerInside(GameManager simulation, Event_Trigger area,
                                        float padding = 0f)
    {
        if (area?.Polygon == null)
        {
            return false;
        }

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            if (_00236._0023huc._00237tc(area, player.Position, player.Radius + padding))
            {
                return true;
            }
        }

        return false;
    }

    public void Reset()
    {
        _scanned = false;
        _ramArea = null;
        _bossArea = null;
        _doorPositions.Clear();
        RamSpawned = false;
        DoorSmashed = false;
        Charged = false;
        _chargeIssued = false;
        _doorUnlocked = false;
        _sinceSmash = 0f;
        _sinceReport = 0f;
        BossSpawned = false;
        BossLootDropped = false;
        Ram = null;
        Boss = null;
        _sinceRam = 0f;
        SpawnedThisTickList.Clear();
        Events.Clear();
    }
}
