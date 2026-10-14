using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities.Characters;
using ConductorGameLogic.Gameplay;
using ConductorGameLogic.MapObjects;
using ConductorGameLogic.View;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 十字路口的**选题本**: 这张图有哪些可以做的目标, 每个目标该刷什么怪。
///
/// ## 为什么需要它
///
/// 十字路口不像 Scout —— Scout 一张图一个固定目标 (地图作者写死在对话点里),
/// 十字路口是**每局现摇**: 地图上摆着一批"事件位" (<see cref="Event_RaidEvent"/>),
/// 每个位子标着自己**能承办哪类事件**, 具体承办哪一类由服务端按难度档现挑。
///
/// 那套判据全在两份原厂数据里, 我们只是把它读出来:
///
/// <code>
/// Maps/raideventsettings.data   每种 RaidEventType 有几个变体 (VisualID),
///                              每个变体的 CompatibleAreaTags + 交互体类型
///        │ 标签求交
///        ▼
/// 地图上的 Event_RaidEvent     .Tags = 这个位子接受哪些标签
///        │
///        ▼
/// Maps/enemyeventsettings.data  同一种事件按难度分的 EnemyVariation:
///                              DifficultyMask / 刷怪组 / 时限 / 奖牌线
/// </code>
///
/// 实测 (Crossroad_map26_small, 9 个事件位): 每个位子都带
/// <c>504191385</c> 这个标签, 而 <c>KillEnemy</c> 的两个变体正好要它 ——
/// 所以"清场"是任何位子都办得起的事, 拿它当兜底。
///
/// ## 难度档就是 DifficultyMask 的位
///
/// <c>EnemyVariation.DifficultyMask</c> 实测取值 0x1 / 0x2 / 0x4 / 0x18,
/// 名字分别叫 Easy / Medium / Hard / Extreme —— 正好是
/// <c>LobbyDifficulty</c> 五档的位掩码 (0x18 = 第 4、5 档共用一套)。
/// 于是"第 N 档能用哪些变体"就是 <c>(mask &amp; (1 &lt;&lt; N)) != 0</c>,
/// 一个数都不用我们拍。
///
/// ## 我们定的只有三样
///
///   1. **哪些事件类型算实现了** (<see cref="Playable"/>) —— 护送平民 /
///      解救平民要平民实体, 而十字路口的对象池里 Trucker 是 0, 平民更没有,
///      硬摆只会摆出"目标指着空地"。宁可不选。
///   2. **摆位点归谁** (<see cref="OwnerOf"/>) —— 见那边的长注释, 地图里没有
///      这条关联, 是从 ID 编号规律推出来的。
///   3. **一波刷多少只** —— 原厂那套 (刷怪组的 cost + 名叫 "WaveHeavy" 的强度
///      曲线) 里, 曲线本体在 Unity 资源里, 反编译树拿不到。cost 我们照用
///      (走 <see cref="Mission.Draw"/> 的预算制), 预算是我们定的。
/// </summary>
public sealed class CrossroadEventCatalog
{
    /// <summary>
    /// 一个事件位。<see cref="Spawns"/> 是**归它的**僵尸摆位点, 见 <see cref="OwnerOf"/>。
    /// </summary>
    public sealed class Area
    {
        public int Id;
        public Vector2 Position;
        public Vector2 Direction;
        public MapObjectReference MainArea;

        /// <summary><see cref="MainArea"/> 指向的区域多边形 (没找到为 null)。</summary>
        public Event_Trigger Zone;
        public int[] Tags = Array.Empty<int>();
        public readonly List<Spawn_EventEntities> Spawns = new List<Spawn_EventEntities>();

        /// <summary>标签对得上、但位子太挤摆不下的补给设施变体 (<see cref="CrossroadStash.IsCramped"/>)。</summary>
        public readonly HashSet<int> CrampedFacilities = new HashSet<int>();
    }

    /// <summary>
    /// 一种"事件类型 + 第几个变体"。<see cref="Interaction"/> 有值 = 这个目标要玩家
    /// 按交互键读条 (偷物资 / 摇旗); 没值 = 打完了就算完 (清场)。
    /// </summary>
    public sealed class Choice
    {
        public RaidEventType Type;
        public int VisualId;
        public InteractionPointType? Interaction;
        public int[] Tags = Array.Empty<int>();
        public string Name = string.Empty;
    }

    /// <summary>
    /// 一只具名目标: 什么怪、带哪些词条、什么时候出来。全是原厂 <c>enemyeventsettings.data</c> 里的。
    /// </summary>
    /// <param name="Zombie">运行时类型。</param>
    /// <param name="Attributes">词条 (Defense / Relentless / Vitality ...), 各对应一个原厂 buff。</param>
    /// <param name="Condition">Start = 开场就在; Delay = 事件开始后 <paramref name="Time"/> 秒;
    ///   OnlyForced = 护送途中被"逼出来"的, 同样按时间出。</param>
    /// <param name="NameKey">名字的本地化键 (<c>PCStrings.GUI.EnemyNames.Minion</c> = 杂兵)。</param>
    public sealed record Target(Type Zombie, AttributeType[] Attributes,
                                SpawnCondition Condition, float Time, string NameKey)
    {
        /// <summary>有名有姓的 (不是 Minion) —— Boss 那类。</summary>
        public bool Named => !string.IsNullOrEmpty(NameKey) && !NameKey.EndsWith(".Minion");

        /// <summary>出场时刻 (秒, 从事件开始算)。Start 一律 0 —— 除了原厂写了 Start@30 的那几条。</summary>
        public float At => Condition == SpawnCondition.Start ? Math.Max(0f, Time) : Time;
    }

    /// <summary>一个排好的目标: 在哪个位子、办哪一类、刷什么怪、多久、奖牌线。</summary>
    public sealed class Mission
    {
        public Area Area;
        public Choice Choice;

        /// <summary>变体名, 只用来打日志 (比如 "Butcher Ambush Easy")。</summary>
        public string Variation = string.Empty;

        /// <summary>
        /// 原厂的强度曲线名 (<c>EnemyVariation.IntensityCurve</c>, 比如 "WaveHeavy")。
        /// 曲线本体丢了, 只按名字分档决定刷多少杂兵 —— 见 <see cref="SpawnIntensity"/>。
        /// </summary>
        public string Curve = string.Empty;

        /// <summary>
        /// 具名目标里有没有 BOSS (囤积者 / 巨型)。有 = 物资靠 BOSS 自己抛的那 150;
        /// 没有 = 伏击 / Looter 那 21 个变体, 见 <see cref="CrossroadMissionDirector.SupplyDropFor"/>。
        /// </summary>
        public bool HasBoss => Targets.Any(target => IsBoss(target.Zombie));

        /// <summary>时限 (秒)。原厂 <c>EventMedalTimeData.Start</c>, 实测 180~200。</summary>
        public float Seconds;

        /// <summary>奖牌线。原厂 <c>EventMedalTimeData</c> 的铜/银/金 (实测 0/50/100)。</summary>
        public ushort Bronze;
        public ushort Silver;
        public ushort Gold;

        /// <summary>
        /// 平民任务要救/送几个。只对 FreeCivilians / EscortCivilian 有意义, 见
        /// <see cref="CivilianSeconds"/> 那段为什么它来自 <c>MedalData.Start</c>。
        /// </summary>
        public int CivilianCount;

        /// <summary>这是平民任务 (奖牌线按人数, 不按时间)。</summary>
        public bool IsCivilian => Choice.Type == RaidEventType.FreeCivilians
                                  || Choice.Type == RaidEventType.EscortCivilian;

        /// <summary>
        /// 这个变体的**具名目标** —— 原厂 <c>EnemyVariation.EventTargets</c>, 按时间表出场。
        ///
        /// KillEnemy 的目标就是它们 (客户端文案: "Kill the named zombies in the area" /
        /// "Kill the Looter" / "Kill the Hoarder" / "Kill the Massive zombie")。别的事件
        /// 也有 (偷物资时的 Ram、占领时的 Puller), 那些是"警报来的硬茬"。
        ///
        /// 玩家实测 (2026-09-13): "走到任务点附近什么都没有, 这可能是刷 boss 的地方" ——
        /// 上一版只按刷怪组摇普通怪, 这张表一只都没刷。
        /// </summary>
        public readonly List<Target> Targets = new List<Target>();

        /// <summary>刷怪组: (僵尸类, 单只成本)。成本是原厂数, 见 <see cref="Draw"/>。</summary>
        public readonly List<(Type Zombie, float Cost, float Chance)> Mix =
            new List<(Type, float, float)>();

        /// <summary>
        /// 按预算摇一批怪出来。
        ///
        /// ## 预算制是原厂的形状, 预算数是我们的
        ///
        /// 刷怪组里每种僵尸带一条 <c>SpawnValue</c> 曲线, 实测取值:
        /// Walker 0.01 / Spitter 0.05 / 特殊 0.20 / 精英 0.50。这不是"数量"也不是
        /// "概率" —— 它是**成本**: 1.0 的预算买得起 100 只 Walker 或者 2 只精英。
        /// (同一份数据里还有一条 <c>SpawnChance</c>, 那才是概率, 见 Mix 里的第三项。)
        ///
        /// 原厂再拿一条按事件类型指定的强度曲线 (名字叫 "WaveHeavy" /
        /// "ConstantSpawn_Medium" 那些) 决定每一刻的预算。**那些曲线在 Unity 资源里,
        /// 反编译树里只剩名字**, 所以预算由我们给 (<paramref name="budget"/>),
        /// 等实测手感再调。
        /// </summary>
        public List<Type> Draw(GameManager simulation, float budget, int cap)
        {
            List<Type> drawn = new List<Type>();
            if (Mix.Count == 0 || simulation == null)
            {
                return drawn;
            }

            float totalChance = Mix.Sum(m => m.Chance);

            while (drawn.Count < cap && budget > 0f)
            {
                (Type zombie, float cost, float _) = Pick(simulation, totalChance);
                if (cost > budget && drawn.Count > 0)
                {
                    break;      // 买不起了 —— 但至少保证摇出一只, 免得空波
                }

                budget -= Math.Max(cost, 0.001f);
                drawn.Add(zombie);
            }

            return drawn;
        }

        /// <summary>按 <c>SpawnChance</c> 加权摇一种。权重全为 0 时退回等概率。</summary>
        private (Type, float, float) Pick(GameManager simulation, float totalChance)
        {
            if (totalChance <= 0f)
            {
                return Mix[simulation.Random._0023Otc(0, Mix.Count)];
            }

            // 走模拟层自己的 RNG —— 服务端所有随机都走同一个源 (确定性重放)。
            float roll = simulation.Random._0023Otc(0, 100000) / 100000f * totalChance;

            foreach ((Type zombie, float cost, float chance) in Mix)
            {
                roll -= chance;
                if (roll <= 0f)
                {
                    return (zombie, cost, chance);
                }
            }

            return Mix[Mix.Count - 1];
        }
    }

    /// <summary>
    /// 已经实现了玩法的事件类型。**其余一律不选**。
    ///
    /// 排除的和理由:
    ///
    ///   ProtectCivilians / DestroyObjects   <c>raideventsettings.data</c> 里
    ///                                    一个变体都没有 (实测 0 个), 原版自己也不用。
    ///   Scout*                           那是 Scout 任务的, 归 ScoutObjectiveDirector。
    ///   FetchNewQuest / ReturnToQuestGiver / EndEvent
    ///                                    不是"任务"而是任务之间的过场, 由
    ///                                    <see cref="CrossroadMissionDirector"/> 直接摆。
    /// </summary>
    public static readonly RaidEventType[] Playable =
    {
        RaidEventType.KillEnemy,
        RaidEventType.CaptureFlag,
        RaidEventType.StealSupplies,
        RaidEventType.TimedSupplyGrab,
        RaidEventType.SupplyFacility,

        // 平民两类 (2026-09-13 起): 十字路口的客户端对象池里平民 _00237Y 有 16 个名额,
        // Goo 茧 _0023EK._0023iL 也在 —— 上一版以为没有, 是只看了 Trucker 那一栏。
        RaidEventType.FreeCivilians,
        RaidEventType.EscortCivilian,
    };

    private readonly List<Area> _areas = new List<Area>();
    private readonly List<Choice> _choices = new List<Choice>();
    private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
    private bool _scanned;

    /// <summary>这张图有几个事件位。</summary>
    public int Areas => _areas.Count;

    /// <summary>标签能对上的 (类型, 变体) 有几种。</summary>
    public int Choices => _choices.Count;

    /// <summary>
    /// 所有刷怪位 (夜袭那批不在里面) —— 归了事件的 + 离事件太远没归的。沿途刷怪从这里挑。
    /// </summary>
    public IEnumerable<Spawn_EventEntities> AllSpawns =>
        _areas.SelectMany(a => a.Spawns).Concat(_orphans);

    /// <summary>
    /// 刷怪位离事件位最远多远才归它。
    ///
    /// ## 为什么要这个上限
    ///
    /// "同一块拼图" 这条判据在块很大、事件位偏在一角时会收进很远的点。实测 map26
    /// 区域 70058 @ (470,1): 它那一块往基地方向伸, 块里有刷怪位在 (237,23) ——
    /// **离事件 234, 离基地卡车只有 148**。事件波次抽到它, 怪就又刷到基地门口了
    /// (玩家报的那个病, 测试反复撞到)。
    ///
    /// 200 = 事件区域多边形的尺度 (实测 125~310 见方, 中心到边一两百)。超出的点
    /// 不归事件, 但还留给沿途刷怪 (那边自己会避开基地)。**我们定的。**
    /// </summary>
    public float MaxOwnerDistance { get; set; } = 200f;

    private readonly List<Spawn_EventEntities> _orphans = new List<Spawn_EventEntities>();

    /// <summary>行尸的运行时类型。沿途刷怪用 (那些怪不属于任何任务的刷怪组)。</summary>
    public Type WalkerType
    {
        get
        {
            string obfuscated = ZombieFactory.ObfuscatedNameFor("Walker");
            return obfuscated != null && _types.TryGetValue(obfuscated, out Type type) ? type : null;
        }
    }

    /// <summary>没能归到任何事件位的摆位点数 (基地那批 + 没有事件位的块)。</summary>
    public int OrphanSpawns { get; private set; }

    /// <summary>诊断用。宿主读完打日志。</summary>
    public readonly List<string> Notes = new List<string>();

    /// <summary>
    /// 扫一遍地图和两份设置。幂等。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        _scanned = true;

        foreach (MapObject mapObject in simulation.MapObjects)
        {
            if (mapObject is Event_RaidEvent raid)
            {
                _areas.Add(new Area
                {
                    Id = raid.ID,
                    Position = raid.Position,
                    Direction = raid.Direction,
                    MainArea = raid.MainArea,
                    Tags = raid.Tags == null
                        ? Array.Empty<int>()
                        : raid.Tags.Select(t => t.NewTag).ToArray(),
                });
            }
        }

        Dictionary<int, Event_Trigger> triggers = simulation.MapObjects
            .OfType<Event_Trigger>()
            .GroupBy(t => t.ID)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (Area area in _areas)
        {
            if (triggers.TryGetValue(area.MainArea.ObjectID, out Event_Trigger zone) && zone.Polygon != null)
            {
                zone.Polygon.CalculateVerticesWorldSpace();
                area.Zone = zone;
            }
        }

        CollectChoices();
        AssignSpawnPoints(simulation);
        MarkCrampedFacilities(simulation);

        foreach (KeyValuePair<string, Type> entry in ZombieFactory.ResolveKindTypes())
        {
            _types[entry.Key] = entry.Value;
        }

        Notes.Add($"十字路口选题: 事件位 {_areas.Count} 个, 可办事件 {_choices.Count} 种, "
                  + $"刷怪位 {_areas.Sum(a => a.Spawns.Count)} 个 (无主 {OrphanSpawns} 个)");
    }

    /// <summary>太挤的位子不办补给设施 (别的任务照办)。判据见 <see cref="CrossroadStash.IsCramped"/>。</summary>
    private void MarkCrampedFacilities(GameManager simulation)
    {
        foreach (Area area in _areas)
        {
            foreach (Choice choice in _choices.Where(c => c.Type == RaidEventType.SupplyFacility && Hosts(area, c)))
            {
                if (CrossroadStash.IsCramped(simulation, area.Position, area.Direction, choice.VisualId, out string detail))
                {
                    area.CrampedFacilities.Add(choice.VisualId);
                    Notes.Add($"十字路口选题: 区域 {area.Id} 太挤, 不办补给设施 {choice.Name} ({detail})");
                }
            }
        }
    }

    /// <summary>这个位子办得了这个变体: 标签全对得上, 补给设施还得不挤。</summary>
    private static bool Hosts(Area area, Choice choice) =>
        choice.Tags.All(tag => area.Tags.Contains(tag))
        && !(choice.Type == RaidEventType.SupplyFacility && area.CrampedFacilities.Contains(choice.VisualId));

    /// <summary>
    /// 从 <c>raideventsettings.data</c> 抄出每个 (类型, 变体) 的标签和交互体类型。
    /// </summary>
    private void CollectChoices()
    {
        RaidEventSettings settings = GameManager.RaidEventSettings;
        if (settings == null)
        {
            return;
        }

        foreach (RaidEventType type in Playable)
        {
            EventViewData[] options = settings._0023nS(type);
            if (options == null)
            {
                continue;
            }

            for (int i = 0; i < options.Length; i++)
            {
                EventViewData option = options[i];
                if (option.CompatibleAreaTags == null || option.CompatibleAreaTags.Length == 0)
                {
                    continue;   // 没标签 = 不由地图位子承办
                }

                _choices.Add(new Choice
                {
                    Type = type,
                    VisualId = i,
                    Interaction = InteractionOf(option),
                    Tags = option.CompatibleAreaTags.Select(t => t.NewTag).ToArray(),
                    Name = option.Name ?? string.Empty,
                });
            }
        }
    }

    /// <summary>
    /// 这个变体要不要玩家读条。
    ///
    /// 判据是原厂的交互体类型: <c>GrapSupplies</c> (偷物资) 和 <c>Flag</c> (摇旗)
    /// 是**真的能按**的 —— 客户端 <c>UnityRaidEventView</c> 给它们画出交互体、
    /// 走近弹按键提示。<c>ObjectPosition</c> 只是"这里有个东西"的摆件
    /// (KillEnemy 的三个变体特效还是 Null), 按不了, 所以那类目标靠打完收工。
    /// </summary>
    private static InteractionPointType? InteractionOf(EventViewData option)
    {
        if (option.Interactions == null)
        {
            return null;
        }

        foreach (InteractionViewData interaction in option.Interactions)
        {
            if (interaction.Type == InteractionPointType.GrapSupplies
                || interaction.Type == InteractionPointType.Flag)
            {
                return interaction.Type;
            }
        }

        return null;
    }

    /// <summary>
    /// 摆位点归谁。
    ///
    /// ## 地图里**没有**这条关联, 是从编号推的
    ///
    /// 教程图上 <c>Spawn_EventEntities</c> 是按 <c>ID</c> 分组的 (一个事件 ID
    /// 挂一串点)。十字路口不是 —— 实测 map26 共 390 个点, **ID 两两不同**
    /// (10000, 10001, ... 10086), 按 ID 一个都配不上, 于是全落进
    /// <see cref="EventSpawnDirector"/> 的"未知事件ID"日志里 (玩家看到的
    /// "怪都刷在基地附近", 就是只有基地那一批配上了)。
    ///
    /// 但编号本身有规律 —— 十字路口的图是**分块拼**的, 每块占一个 20000 宽的
    /// 号段 (第 n 块 = n×20000+10000)。实测 map26:
    ///
    ///     10000..10086 + 事件位 10087/10089 + 区 10088/10090   ← 同一块
    ///     30000..30024 + 事件位 30025                          ← 下一块
    ///     90000..90019 + 基地 (Event_Assault 90019、卡车、炮塔) ← 基地那块
    ///
    /// 所以判据是"**同一块里, 离哪个事件位近就归谁**"。两个例外:
    ///
    ///   - ID 和某个 <see cref="Event_Assault"/> 相同的点**不归这里** ——
    ///     那是基地夜袭自己的 18 个点, 归 <see cref="EventSpawnDirector"/>
    ///     (它按 ID 分组那条路在这些点上是对的)。
    ///   - 同一块里没有事件位的 (基地那块) 一律**不认领**。硬塞给最近的事件位
    ///     等于把怪刷进基地 —— 那正是要修的病。
    /// </summary>
    private void AssignSpawnPoints(GameManager simulation)
    {
        HashSet<int> assaultIds = new HashSet<int>(
            simulation.MapObjects.OfType<Event_Assault>().Select(a => a.ID));

        foreach (Spawn_EventEntities spawn in simulation.MapObjects.OfType<Spawn_EventEntities>())
        {
            if (assaultIds.Contains(spawn.ID))
            {
                continue;       // 基地夜袭的, 归 EventSpawnDirector
            }

            Area owner = OwnerOf(spawn);
            if (owner == null
                || Vector2.Distance(owner.Position, spawn.Position) > MaxOwnerDistance)
            {
                OrphanSpawns++;
                _orphans.Add(spawn);
                continue;
            }

            owner.Spawns.Add(spawn);
        }

        foreach (Area area in _areas)
        {
            area.Spawns.Sort((a, b) =>
                Vector2.DistanceSquared(a.Position, area.Position)
                    .CompareTo(Vector2.DistanceSquared(b.Position, area.Position)));
        }
    }

    /// <summary>同一块里最近的那个事件位。见 <see cref="AssignSpawnPoints"/>。</summary>
    private Area OwnerOf(Spawn_EventEntities spawn)
    {
        Area best = null;
        float bestDistance = float.MaxValue;

        foreach (Area area in _areas)
        {
            if (Block(area.Id) != Block(spawn.ID))
            {
                continue;
            }

            float distance = Vector2.DistanceSquared(area.Position, spawn.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = area;
            }
        }

        return best;
    }

    /// <summary>
    /// 这个位置在不在这个事件区域 (<c>Event_RaidEvent.MainArea</c> 多边形) 里, 外扩 <paramref name="padding"/>。
    /// 地图没配多边形的退回"离事件位 <see cref="MaxOwnerDistance"/> 以内"。
    /// </summary>
    public bool InsideArea(Area area, Vector2 position, float padding)
    {
        if (area == null)
        {
            return false;
        }

        return area.Zone != null
            ? _00236._0023huc._00237tc(area.Zone, position, padding)
            : Vector2.Distance(area.Position, position) <= MaxOwnerDistance;
    }

    /// <summary>ID 的号段 = 第几块拼图。见 <see cref="AssignSpawnPoints"/>。</summary>
    private static int Block(int id) => id / 20000;

    /// <summary>
    /// 排这一局的目标: 每个目标一个不同的事件位, 按难度档挑变体。
    ///
    /// ## 类型池
    ///
    /// 玩家实测 (2026-09-13): "剩下的任务还有拯救幸存者, 消灭病毒孢子等, 任务类型随机,
    /// 应该有个任务类型池, 随机抽取。"
    ///
    /// 上一版是"在所有 (类型, 变体) 组合里均匀抽" —— 变体多的类型 (限时抢物资有 5 个)
    /// 被抽中的概率是只有 1 个变体的类型 (偷物资) 的五倍。现在分两步:
    ///
    ///   1. 在**这个事件位办得起**的类型里**等概率**抽一类, 本局已经出过的类型先让开
    ///      (都出过了才允许重复) —— 三轮尽量三种不同的
    ///   2. 再在这一类里抽变体 (视觉) 和难度变体 (刷怪/时限)
    /// </summary>
    /// <param name="only">只从这几类里抽 (测试 / 实测某一类任务时用)。null = 全池。</param>
    public List<Mission> Plan(GameManager simulation, int difficulty, int count,
                              IReadOnlyCollection<RaidEventType> only = null)
    {
        Scan(simulation);

        List<Mission> missions = new List<Mission>();
        HashSet<RaidEventType> used = new HashSet<RaidEventType>();
        List<Area> pool = _areas.Where(a => a.Spawns.Count > 0).ToList();

        while (missions.Count < count && pool.Count > 0)
        {
            // 先在"还办得起没出过的类型"的位子里抽 —— 标签按"全部满足"匹配之后, 有的位子只办得起
            // 清场, 随便抽到它就只能重复; 都办不起新类型了才随便抽。
            List<Area> fresh = pool.Where(a => OffersNewType(a, used, only, difficulty)).ToList();
            List<Area> candidates = fresh.Count > 0 ? fresh : pool;

            Area area = candidates[simulation.Random._0023Otc(0, candidates.Count)];
            pool.Remove(area);

            Mission mission = Compose(simulation, area, difficulty, used, only);
            if (mission != null)
            {
                missions.Add(mission);
                used.Add(mission.Choice.Type);
            }
        }

        return missions;
    }

    /// <summary>给一个事件位配一类事件 + 一个难度变体。配不上返回 null。</summary>
    private bool OffersNewType(Area area, ICollection<RaidEventType> used,
                               IReadOnlyCollection<RaidEventType> only, int difficulty) =>
        _choices.Any(c => Hosts(area, c)
                          && (only == null || only.Contains(c.Type))
                          && AllowedAt(c.Type, difficulty)
                          && !used.Contains(c.Type));

    /// <summary>
    /// 这一类任务这一档出不出。
    ///
    /// 目前只有一条有原厂依据: **护送任务从 Infectious 档才开始有**
    /// (<c>StaticCrossroadsGUIData.InfectiousGate.Occurences</c> 里列着
    /// <c>EscortMissions</c>, 而 Normal 那一栏是空的)。Normal 是给 1~3 阶新人
    /// 的档, 护送要边打边保平民, 对那一档确实重。
    ///
    /// 别的类型原厂没说哪档不能出, 就都放行 —— 编一套"我们觉得的"分档
    /// 比不分更糟。
    /// </summary>
    private static bool AllowedAt(RaidEventType type, int difficulty) =>
        type != RaidEventType.EscortCivilian
        || CrossroadDifficulty.AllowsEscortMissions(difficulty);

    private Mission Compose(GameManager simulation, Area area, int difficulty,
                            ICollection<RaidEventType> used = null,
                            IReadOnlyCollection<RaidEventType> only = null)
    {
        // ## 变体要的标签必须**全部**在这个位子上
        //
        // 玩家实测 (2026-09-13 双人): "任务点刷出来的卡车直接把路堵死了"。上一版是"有一个对得上就行"。
        // 探针 (map26): 偷卡车 StealTruck 要 {1926081210, Jungle} 两个, 带武装 SUV / 坠毁直升机的抢物资也要
        // 这两个; 丛林小路位只有 Jungle, 只有 70058 那个开阔位有 1926081210 (但它不是 Jungle)。
        // 那个 1926081210 显然就是"这里摆得下大家伙" —— 按"任意一个"匹配, 卡车被塞进了每一条丛林小路。
        // 按"全部"匹配之后 map26 这张小图上没有能摆卡车的位子, 大图上才有。
        List<Choice> fits = _choices
            .Where(c => Hosts(area, c))
            .Where(c => only == null || only.Contains(c.Type))
            .Where(c => AllowedAt(c.Type, difficulty))
            .ToList();

        if (fits.Count == 0)
        {
            return null;
        }

        // 第一步: 类型等概率, 出过的先让开
        List<RaidEventType> types = fits.Select(c => c.Type).Distinct().ToList();
        List<RaidEventType> fresh = used == null
            ? types
            : types.Where(type => !used.Contains(type)).ToList();

        List<RaidEventType> typePool = fresh.Count > 0 ? fresh : types;
        RaidEventType picked = typePool[simulation.Random._0023Otc(0, typePool.Count)];

        // 第二步: 这一类里的视觉变体
        List<Choice> ofType = fits.Where(c => c.Type == picked).ToList();
        Choice choice = ofType[simulation.Random._0023Otc(0, ofType.Count)];

        Mission mission = new Mission
        {
            Area = area,
            Choice = choice,

            // 兜底时限: 原厂每个变体都给 180~200, 摸不到变体时用 180。
            Seconds = 180f,
            Bronze = 0,
            Silver = 50,
            Gold = 100,
            CivilianCount = 3,
        };

        FillFromVariation(simulation, mission, choice, difficulty);
        return mission;
    }

    /// <summary>
    /// 从 <c>enemyeventsettings.data</c> 取这一档的变体, 把时限/奖牌线/刷怪组填进去。
    /// </summary>
    private void FillFromVariation(GameManager simulation, Mission mission, Choice choice, int difficulty)
    {
        EnemyEventSettingsData data = GameManager.EnemyEventSettings;
        if (data?.Variations == null)
        {
            return;
        }

        EnemyVariation[] all = data.Variations
            .Where(v => v.EventType == choice.Type)
            .SelectMany(v => v.Variations ?? Array.Empty<EnemyVariation>())
            .ToArray();

        if (all.Length == 0)
        {
            return;
        }

        int bit = 1 << Math.Max(0, Math.Min(difficulty, 4));
        EnemyVariation[] fitting = all.Where(v => (v.DifficultyMask & bit) != 0).ToArray();

        // 这一档没有专属变体就用全表 —— 宁可难度不准, 也别让这个位子空着。
        EnemyVariation[] pool = fitting.Length > 0 ? fitting : all;
        // 同一档里的难度变体也随机 (比如 KillEnemy 简单档就有 Double Looter /
        // Butcher Ambush / Floater Ambush / 三种 Hoarder ... 十来个)。上一版恒取第一个。
        EnemyVariation variation = pool[simulation.Random._0023Otc(0, pool.Length)];

        mission.Variation = variation.Name ?? string.Empty;
        mission.Curve = variation.IntensityCurve ?? string.Empty;

        mission.Bronze = (ushort)Math.Max(0f, variation.MedalData.Bronze);
        mission.Silver = (ushort)Math.Max(0f, variation.MedalData.Silver);
        mission.Gold = (ushort)Math.Max(0f, variation.MedalData.Gold);

        if (mission.IsCivilian)
        {
            // 平民任务的 MedalData 是**人数**, 不是秒数 —— 见 CivilianSeconds。
            mission.CivilianCount = Math.Max(1, (int)variation.MedalData.Start);
            mission.Seconds = CivilianSeconds;
        }
        else if (variation.MedalData.Start > 0f)
        {
            mission.Seconds = variation.MedalData.Start;
        }

        FillMix(mission, data, variation);
        FillTargets(mission, data, variation);
    }

    /// <summary>刷怪组 -> (我们的僵尸类, 成本, 概率)。</summary>
    private void FillMix(Mission mission, EnemyEventSettingsData data, EnemyVariation variation)
    {
        if (variation.SpawningSet == null)
        {
            return;
        }

        foreach (EventSpawningSetID setId in variation.SpawningSet)
        {
            foreach (SpawningSet set in data.SpawnSets ?? Array.Empty<SpawningSet>())
            {
                if (!SameGuid(set.ID.GUID, setId.GUID))
                {
                    continue;
                }

                foreach (ZombieGroup group in data.ZombieGroups ?? Array.Empty<ZombieGroup>())
                {
                    if (!SameGuid(group.ID.GUID, set.ZombieGroup.GUID) || group.Zombies == null)
                    {
                        continue;
                    }

                    foreach (ZombieGroupValues entry in group.Zombies)
                    {
                        Type type = TypeFor(entry.Zombie);
                        if (type == null)
                        {
                            continue;
                        }

                        mission.Mix.Add((type, First(entry.SpawnValue, 0.05f),
                            First(entry.SpawnChance, 1f)));
                    }
                }
            }
        }
    }

    /// <summary>曲线的第一个关键帧值 (t=0)。见 <see cref="Mission.Draw"/> 里为什么只取头一个。</summary>
    private static float First(KeyframeData[] curve, float fallback) =>
        curve == null || curve.Length == 0 ? fallback : curve[0].Value;

    /// <summary>
    /// 平民任务的时限。**我们定的** —— 原厂数据里没有。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "接了一个从茧里解救幸存者的任务, 茧自己爆炸了, 然后任务自动失败。"
    ///
    /// 日志: <c>FreeCivilians/0 ... 时限 4 秒</c> -&gt; 到达 4 秒后 "超时失败" -&gt; 收尾把茧
    /// 收走 (茧碎的特效 = 玩家看到的"自己爆炸")。
    ///
    /// 病根: 平民两类的 <c>EventMedalTimeData</c> 不是时间。实测全部难度档:
    ///
    ///     FreeCivilians   Start=4  金/银/铜=4/2/1
    ///     EscortCivilian  Start=3  金/银/铜=3/2/1
    ///     其余事件        Start=180~200  金/银/铜=100/50/0
    ///
    /// 平民那两行是**人数**: 一共 4 个 (3 个) 平民, 救出 4/2/1 个分别拿金/银/铜。
    /// 我把 Start 当秒数用了。现在 Start 当人数, 奖牌按救出人数评, 时限另给 180 秒
    /// (和其他事件的原厂时限同一档)。
    /// </summary>
    public static float CivilianSeconds { get; set; } = 180f;

    /// <summary>
    /// 收尾那一幕 (EndEvent) 的具名目标 —— 原厂 <c>End Event Looters</c>: 5 只 Looter,
    /// 事件开始后 25/50/75/100/125 秒各一只 (所有难度档共用, mask 0x1F)。
    /// </summary>
    public Mission FinaleMission(GameManager simulation, int difficulty, Vector2 center,
                                 IEnumerable<Spawn_EventEntities> spawns)
    {
        Scan(simulation);

        Mission mission = new Mission
        {
            Area = new Area { Id = 0, Position = center },
            Choice = new Choice { Type = RaidEventType.EndEvent, Name = "End Event" },
        };

        mission.Area.Spawns.AddRange(spawns);

        EnemyEventSettingsData data = GameManager.EnemyEventSettings;
        EnemyVariation[] all = data?.Variations == null
            ? Array.Empty<EnemyVariation>()
            : data.Variations.Where(v => v.EventType == RaidEventType.EndEvent)
                .SelectMany(v => v.Variations ?? Array.Empty<EnemyVariation>()).ToArray();

        if (all.Length > 0)
        {
            int bit = 1 << Math.Max(0, Math.Min(difficulty, 4));
            EnemyVariation variation = all.FirstOrDefault(v => (v.DifficultyMask & bit) != 0);
            if (variation.Name == null)
            {
                variation = all[0];
            }

            mission.Variation = variation.Name ?? string.Empty;
            mission.Curve = variation.IntensityCurve ?? string.Empty;
            FillMix(mission, data, variation);
            FillTargets(mission, data, variation);
        }

        return mission;
    }

    /// <summary>变体的 EventTargets -> 具名目标表 (类型解析不出来的跳过)。</summary>
    private void FillTargets(Mission mission, EnemyEventSettingsData data, EnemyVariation variation)
    {
        if (variation.EventTargets == null || data.EnemyTargets == null)
        {
            return;
        }

        foreach (EnemyEventTarget entry in variation.EventTargets)
        {
            foreach (EnemyTarget target in data.EnemyTargets)
            {
                if (!SameGuid(target.ID.GUID, entry.EnemyTarget.GUID))
                {
                    continue;
                }

                Type type = TypeFor(target.EnemyType);
                if (type != null)
                {
                    mission.Targets.Add(new Target(type,
                        target.Attributes ?? Array.Empty<AttributeType>(),
                        entry.SpawnCondition.Condition, entry.SpawnCondition.Time,
                        target.Name ?? string.Empty));
                }

                break;
            }
        }
    }

    /// <summary>
    /// 原厂的 <see cref="ZombieType"/> -> 它**自己的**运行时类。
    ///
    /// ## 上一版同族归一, 这一版一一对应
    ///
    /// 上一版只认得 <see cref="ZombieFactory.Kinds"/> 那七个类, 所以 Runner / Veteran /
    /// Spitter 当行尸刷, 囤积者 / 巨型 / 精英当普通版刷。现在按类名一一对上 —— 判据是
    /// 每个类自己在 <c>ResetNPC</c> 里写的 <c>base.ZombieType = ...</c>, 以及
    /// <c>GameObjectBase</c> 上那一排 <c>IsButcherHoarder =&gt; this is _0023sPb</c>
    /// (GameObjectBase.cs:800-940)。
    ///
    /// 实测十字路口的客户端模型缓存: Looter 10、三种囤积者各 3、三种巨型各 1、
    /// 精英各 10、Runner/Veteran/Spitter 各 30 —— 全都刷得出来。
    ///
    /// 解析不到 (换版本类名变了) 才退回同族的普通版, 免得整只 Boss 消失。
    /// </summary>
    private static readonly Dictionary<ZombieType, (string Own, string Family)> ClassNames =
        new Dictionary<ZombieType, (string, string)>
        {
            [ZombieType.WALKER] = ("_0023rPb", "Walker"),
            [ZombieType.RUNNER] = ("_0023M3", "Walker"),
            [ZombieType.VETERAN] = ("_0023gPb", "Walker"),
            [ZombieType.SPITTER] = ("_0023W3", "Walker"),
            [ZombieType.CORRUPTEDWALKER] = ("_0023v3", "Walker"),
            [ZombieType.SPLASHER] = ("_0023xO", "Walker"),
            [ZombieType.WALKINGBOMB] = ("_0023xPb", "Suicider"),
            [ZombieType.SUICIDER] = ("_0023wPb", "Suicider"),
            [ZombieType.LOOTER] = ("_0023nPb", "Walker"),
            [ZombieType.BUTCHER] = ("_0023fPb", "Butcher"),
            [ZombieType.BUTCHERELITE] = ("_0023hPb", "Butcher"),
            [ZombieType.BUTCHERHOARDER] = ("_0023sPb", "Butcher"),
            [ZombieType.FLOATER] = ("_0023iPb", "Floater"),
            [ZombieType.FLOATERELITE] = ("_0023oPb", "Floater"),
            [ZombieType.FLOATERHOARDER] = ("_0023qPb", "Floater"),
            [ZombieType.PULLER] = ("_0023mPb", "Puller"),
            [ZombieType.PULLERELITE] = ("_0023kPb", "Puller"),
            [ZombieType.PULLERHOARDER] = ("_0023dPb", "Puller"),
            [ZombieType.PULLERMASSIVE] = ("_002350", "Puller"),
            [ZombieType.RAM] = ("_0023zPb", "Ram"),
            [ZombieType.RAMELITE] = ("_0023yPb", "Ram"),
            [ZombieType.RAMMASSIVE] = ("_00239Z", "Ram"),
            [ZombieType.SCREAMER] = ("_0023lPb", "Screamer"),
            [ZombieType.SCREAMERELITE] = ("_0023jPb", "Screamer"),
            [ZombieType.SCREAMERMASSIVE] = ("_002302", "Screamer"),
        };

    /// <summary>
    /// 死了自己抛一大把补给的那几种 —— 三种囤积者和三种巨型
    /// (<see cref="GameManagerServer"/> 的 <c>IsBossLike</c> 在**类型**上的对应)。
    /// </summary>
    private static readonly ZombieType[] BossTypes =
    {
        ZombieType.BUTCHERHOARDER, ZombieType.FLOATERHOARDER, ZombieType.PULLERHOARDER,
        ZombieType.PULLERMASSIVE, ZombieType.RAMMASSIVE, ZombieType.SCREAMERMASSIVE,
    };

    private static HashSet<Type> _bossClasses;

    /// <summary>这个类是不是囤积者 (不含 Looter) / 巨型。</summary>
    public static bool IsBoss(Type zombie)
    {
        _bossClasses ??= new HashSet<Type>(BossTypes
            .Select(type => ResolveClass(ClassNames[type].Own))
            .Where(found => found != null));

        return zombie != null && _bossClasses.Contains(zombie);
    }

    private static Dictionary<string, Type> _byName;

    /// <summary>按类名从原厂注册表里找运行时类型 (缓存一次)。</summary>
    public static Type ResolveClass(string name)
    {
        if (_byName == null)
        {
            Dictionary<string, Type> map = new Dictionary<string, Type>();
            foreach (KeyValuePair<int, Type> entry in GameManager.GameObjectTypeIDToType)
            {
                if (entry.Value != null)
                {
                    map[entry.Value.Name] = entry.Value;
                }
            }

            _byName = map;
        }

        return name != null && _byName.TryGetValue(name, out Type type) ? type : null;
    }

    private Type TypeFor(ZombieType zombie)
    {
        if (!ClassNames.TryGetValue(zombie, out (string Own, string Family) names))
        {
            return null;
        }

        Type own = ResolveClass(names.Own);
        if (own != null)
        {
            return own;
        }

        string obfuscated = ZombieFactory.ObfuscatedNameFor(names.Family);
        return obfuscated != null && _types.TryGetValue(obfuscated, out Type type) ? type : null;
    }

    private static bool SameGuid(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
