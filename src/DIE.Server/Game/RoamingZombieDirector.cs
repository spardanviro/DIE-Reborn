using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 决定**什么时候**在哪个刷怪点刷怪 —— 玩家走近才刷, 不是开局一次铺满。
///
/// ## 为什么开局铺满是错的
///
/// 上一版在 <c>StartMatchIfReady</c> 里一次性把全图的点都刷了。后果:
/// 玩家走到刷怪点时僵尸早就站在那儿了, 从地里爬出来的出场动画在开局那一瞬
/// 就播完了 —— 而那时玩家还在几百单位外, 甚至还没同步到那些实体。
///
/// 真机现象正是这样: "我走过去时僵尸就已经站在那了"。
///
/// ## 地图数据给了什么, 没给什么
///
/// <see cref="Spawn_RoamingZombies"/> 只有**位置**和各类僵尸的 min/max,
/// 外加三个布尔: <c>Respawnable</c>、<c>IgnoreSpawnRestriction</c>、
/// <c>HeroicOnly</c>。**没有触发半径, 也没有"spawn restriction"到底是什么的定义**
/// —— 全代码树搜 SpawnRestriction 只搜得到字段声明本身, 消费它的那段是服务端的。
///
/// 所以触发策略是**我们定的**。采用的读法:
///
///   IgnoreSpawnRestriction = 无视"离玩家太近就别刷"这条限制
///
/// 依据是字面语义 + 通用做法 (免得僵尸在玩家脸上凭空冒出来)。这是推断,
/// 不是从代码里读出来的 —— 如果以后发现别的解释, 改这里。
///
/// ## 2026-09-23 改: 视野外刷好, 直接站着 (玩家定)
///
/// 玩家实测拾荒: "现在野外刷怪是大片的同时刷怪, 一大群僵尸从地里面钻出来, 我印象中野外刷怪不是这样的。"
/// 以前是走到 85 以内整点一起钻地 —— 正好在视野里, 看得一清二楚。改成:
///
///   普通点      玩家进 <see cref="TriggerRadius"/> (150) 就刷, 但有人在 <see cref="MinPlayerDistance"/>
///               (100, 约等于视野) 以内时先不刷 (免得在眼前凭空出现; IgnoreSpawnRestriction 不管这条)。
///               **不播出场动画**, 直接站在那儿 —— 玩家走到时僵尸已经在了。
///   路障旁的点  (<c>BarricadeSpawn</c>) 不当野外点刷: 只在附近那道路障开打时刷一次, 钻地出来、
///               直接激怒 (见 <see cref="AllowBarricadeSpawn"/> / ScavengerDirector.BarricadeFightStarted)。
///               这批是成群冒出来的主要来源 (一个点固定 5~12 只)。
///
/// 距离的锚: <c>Maps/camera.cfg</c> 的相机偏移 (67.175, 67.175), 模长约 95, 即玩家身周大致 60~90 单位可见。
/// TriggerRadius 必须小于 <see cref="ReleaseRadius"/>, 否则一离开就立刻重刷。
/// </summary>
public sealed class RoamingZombieDirector
{
    /// <summary>
    /// 关掉之后 <see cref="Update"/> 什么都不做。
    ///
    /// ## 教程要关
    ///
    /// 教程地图确实有 9 个 <see cref="Spawn_RoamingZombies"/> (每点 1~3 只 Walker,
    /// 全部 <c>HeroicOnly=false</c>、<c>Respawnable=true</c>), 但**原版教程里没有
    /// 这些怪** —— 玩家实测确认过, 场上只有那 95 只手摆的
    /// (<see cref="StaticZombiePlacer"/> 29 + <see cref="EventSpawnDirector"/> 66)。
    ///
    /// 合理的解释是: 消费 <c>Spawn_RoamingZombies</c> 的那段服务端代码是
    /// **按游戏模式**挂的, 而教程模式是全脚本的, 从不启用它。地图里留着那些点
    /// 大概是从别的图复制场景时带过来的。
    ///
    /// 我们没有代码证据能证明这一点 (消费方随服务端丢失), 所以这是**按实测行为
    /// 反推的开关**, 不是从数据里读出来的。真正的证据是玩家的观察:
    /// 那 18 只从地里刨出来的僵尸不该在场上, 而且它们的 Digging 出场动画本来
    /// 也是我们自己选的 (地图没规定漫游僵尸的出场方式)。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 每次刷的数量倍率。1 = 按地图 min/max; 拾荒入夜后宿主设成地图的 <c>EndGameSpawnRateIncrease</c> (1.5)。
    /// 漫游点没有计时器, "刷怪速率"只能落到数量上 —— 这是我们的读法。
    /// </summary>
    public float CountScale { get; set; } = 1f;

    /// <summary>玩家进这个距离就刷 (在视野外)。</summary>
    public float TriggerRadius { get; set; } = 150f;

    /// <summary>所有人都走出这个距离才允许重刷。</summary>
    public float ReleaseRadius { get; set; } = 220f;

    /// <summary>有人比这更近 (在视野里) 就先不刷, 除非 IgnoreSpawnRestriction。</summary>
    public float MinPlayerDistance { get; set; } = 100f;

    /// <summary>
    /// 勾了 <c>BarricadeSpawn</c> 的点现在该不该刷。<c>null</c> = 没有这层规则, 当普通点刷
    /// (拾荒以外的模式)。接上之后这批点**只刷一次**、钻地出来、刷出来就进 <see cref="ProvokeThisTick"/>。
    /// </summary>
    public Func<Spawn_RoamingZombies, bool> AllowBarricadeSpawn { get; set; }

    /// <summary>本次 <see cref="Update"/> 刷出来、宿主该激怒的 (路障旁的点)。</summary>
    public readonly List<Character> ProvokeThisTick = new List<Character>();

    /// <summary>
    /// 勾了 <c>HighLevelBarricadeSpawn</c> 的刷怪点现在准不准刷。<c>null</c> = 不设限。
    ///
    /// 原厂字段, 客户端一处不读 (= 服务端用的); 拾荒五张图上 14 个漫游点勾着它,
    /// 另有 9 个 <c>Event_Assault</c> 也勾着 (同一个钩子, 见
    /// <see cref="ScavengerDirector.AllowHighLevelBarricadeSpawn"/> 里的推断)。
    /// </summary>
    public Func<Spawn_RoamingZombies, bool> AllowHighLevelBarricade { get; set; }

    /// <summary>已经激活 (刷过怪) 的点数, 诊断用。</summary>
    public int ArmedPoints => _points.Count;

    public int ActivePoints { get; private set; }

    /// <summary>本次 <see cref="Update"/> 新刷出来的只数, 供宿主打日志。</summary>
    public int SpawnedThisTick { get; private set; }

    /// <summary>本次 <see cref="Update"/> 触发的点, 供宿主打日志。</summary>
    public readonly List<string> TriggeredThisTick = new List<string>();

    private sealed class PointState
    {
        public Spawn_RoamingZombies Point;

        /// <summary>
        /// 这一批怪, 连同生成那一刻的 <c>NumberOfGlobalCacheUses</c>。
        /// 记代数的理由见 <see cref="RoamingZombieDirector.Gone"/>;
        /// 和 <c>ScavengerLooters</c> / <c>ScavengerStorageRooms</c> 是同一个写法。
        /// </summary>
        public readonly List<(Character Zombie, int Uses)> Spawned = new List<(Character, int)>();

        public bool IsSpawned;
    }

    private readonly List<PointState> _points = new List<PointState>();
    private bool _scanned;

    /// <summary>
    /// 扫一遍地图上的刷怪点。幂等 —— 重复调用不会重复登记。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        foreach (MapObject mapObject in simulation.MapObjects)
        {
            if (mapObject is Spawn_RoamingZombies point)
            {
                _points.Add(new PointState { Point = point });
            }
        }

        _scanned = true;
    }

    /// <summary>
    /// 每帧一次。开销是"点数 × 玩家数"次距离平方, 教程是 9×1, 忽略不计。
    /// </summary>
    public void Update(GameManager simulation)
    {
        SpawnedThisTick = 0;
        TriggeredThisTick.Clear();
        ProvokeThisTick.Clear();

        if (simulation == null || !Enabled)
        {
            return;
        }

        Scan(simulation);

        List<Player> players = simulation.ActivePlayerList;
        int active = 0;

        foreach (PointState state in _points)
        {
            float nearest = NearestPlayerDistance(players, state.Point.Position);
            bool barricadePoint = state.Point.BarricadeSpawn && AllowBarricadeSpawn != null;

            if (state.IsSpawned)
            {
                active++;

                // 重刷的两个条件都要满足: 怪清完了, 而且玩家已经走远。
                // 只看"清完了"会导致玩家站在原地被无限刷。
                // 路障旁的点只刷一次 —— 路障破了之后"开打"条件一直成立, 允许重刷就会反复刷。
                if (!state.Point.Respawnable || barricadePoint)
                {
                    continue;
                }

                if (nearest < ReleaseRadius || state.Spawned.Exists(e => !Gone(e)))
                {
                    continue;
                }

                state.Spawned.Clear();
                state.IsSpawned = false;
                continue;
            }

            if (barricadePoint)
            {
                if (!AllowBarricadeSpawn(state.Point))
                {
                    continue;
                }
            }
            else
            {
                if (nearest > TriggerRadius)
                {
                    continue;
                }

                // 有人在视野里就先不刷 —— 等他走开或者走过来之前刷好, 而不是在他眼前冒出来。
                if (!state.Point.IgnoreSpawnRestriction && nearest < MinPlayerDistance)
                {
                    continue;
                }
            }

            // 原厂 HighLevelBarricadeSpawn (拾荒五张图上 14 个点勾着): 见 AllowHighLevelBarricade
            if (state.Point.HighLevelBarricadeSpawn && AllowHighLevelBarricade != null
                && !AllowHighLevelBarricade(state.Point))
            {
                continue;
            }

            List<Character> fresh = RoamingZombieSpawner.SpawnPoint(simulation, state.Point, null, CountScale,
                emerge: barricadePoint);
            if (barricadePoint)
            {
                ProvokeThisTick.AddRange(fresh);
            }

            state.Spawned.Clear();
            for (int i = 0; i < fresh.Count; i++)
            {
                state.Spawned.Add((fresh[i], fresh[i].NumberOfGlobalCacheUses));
            }

            state.IsSpawned = true;
            active++;

            SpawnedThisTick += state.Spawned.Count;
            TriggeredThisTick.Add(
                $"({state.Point.Position.X:F0},{state.Point.Position.Y:F0})×{state.Spawned.Count}");
        }

        ActivePoints = active;
    }

    /// <summary>
    /// 到最近玩家的距离。没有活着的玩家时返回 <see cref="float.MaxValue"/> ——
    /// 这样全员阵亡时不会继续刷怪。
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

        return best == float.MaxValue ? float.MaxValue : (float)System.Math.Sqrt(best);
    }

    /// <summary>
    /// 这一只是不是"没了" —— 死了、不在场上, 或者**它的池化实例已经被拿去当别的怪**。
    ///
    /// ## 为什么要比对代数 (2026-09-18 修的)
    ///
    /// 僵尸对象是池化复用的。这只的尸体被回收后, 同一个实例可能被拿去当另一只怪 ——
    /// 那时它 <c>IsActive</c> 又是 true、<c>IsDead</c> 又是 false, 只看这两样会把它
    /// 误判成"还活着", 于是这个刷怪点的"清完了"永远不成立, **再也不重刷**。
    ///
    /// 后果不是崩溃, 是"越打越稀": 一局打到中后期, 走过的地方渐渐不再出怪。
    /// 旧注释说"等做正经重刷时一并处理" —— 但重刷本身 (<see cref="Update"/> 里判
    /// <c>Respawnable</c> + <see cref="ReleaseRadius"/>) 早就做了, 只剩这一条没跟上。
    ///
    /// <c>NumberOfGlobalCacheUses</c> 是原厂对象池的取用计数, 每次从池里拿都会 +1。
    /// 生成时记下, 之后不一致就说明这个实例已经换了身份。
    /// </summary>
    private static bool Gone((Character Zombie, int Uses) entry) =>
        entry.Zombie == null || !entry.Zombie.IsActive || entry.Zombie.IsDead
        || entry.Zombie.NumberOfGlobalCacheUses != entry.Uses;
}
