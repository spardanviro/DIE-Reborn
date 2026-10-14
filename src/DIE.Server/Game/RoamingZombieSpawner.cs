using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 按地图上的 <see cref="Spawn_RoamingZombies"/> 生成漫游僵尸。
///
/// ## 为什么要自己写
///
/// <c>Spawn_RoamingZombies</c> 在整个客户端代码树里**只有定义, 没有任何消费者** ——
/// 读它并生成僵尸的那段是服务端代码, 随服务端丢失。地图数据本身是齐的:
/// 每个刷怪点都写明了各类僵尸的 min/max、精英开关、特殊僵尸概率。
///
/// 所以这不是"设计一套刷怪系统", 而是**照着地图数据把它还原出来** ——
/// 数量、种类、位置全部来自地图, 我们不发明任何内容。
///
/// ## 各地图的刷怪点数量 (实测)
///
///   Scavenger 1/2/3/8/14   24 / 35 / 24 / 24 / 16
///   Tutorial  4            9
///   Horde     5/6/7        8 / 10 / 10
///   Scout     15/16/17     **0**   —— 这三张图纯事件驱动, 本类对它们无效
///   Raid      12/13/18-20  **0**   —— 同上
///
/// ## 僵尸类型的判据
///
/// 类名全被混淆了, 但每个类注册的技能没有 —— 用技能名认人, 不靠猜:
///
///   _0023rPb  Walker    WalkerRush / WalkerStrikeFast|Normal|Slow
///   _0023fPb  Butcher   ButcherCharge / ButcherRavage / ButcherStrike
///   _0023iPb  Floater   FloaterSpit / FloaterRush / FloaterStrike
///   _0023zPb  Ram       RamCharge / RamStomp / RamStrike
///   _0023lPb  Screamer  ScreamerFear / ScreamerSummon / ScreamerStrike
///   _0023wPb  Suicider  SuiciderAttack / SuiciderStrike
///   _0023mPb  Puller    PullerHook / PullerCrawl / PullerStrike
///
/// 交叉验证: 这七个类在 <c>BelongsToCategoriesPerTypeID</c> 里的分类也对得上 ——
/// Walker 带 <c>ObjCat.Walker</c>, Puller 带 <c>ObjCat.Puller</c>, 其余带
/// <c>ObjCat.Specials</c>。
///
/// ## 还没做的
///
/// - (2026-09-17 已做) <c>Elites</c>: 勾上时特殊僵尸换精英版 (<see cref="ZombieFactory.EliteKinds"/>)。
///   <c>SpecialZombieType</c> / <c>SpecialZombieChance</c>: 每次刷这个点, 按几率**额外**刷一只该类特殊僵尸
///   (RANDOM = 六种特殊里随机一种, NONE = 不刷)。拾荒图上几率多是 0.15 随机, 另有一批点是 1 + 指定种类
///   (而那些点的特殊僵尸 min/max 全是 0) —— 读成"额外一只"才说得通。"额外"是我们的读法。
/// - (2026-09-18 已核) <c>Respawnable</c>: 重刷早就做了 —— 判定在
///   <see cref="RoamingZombieDirector.Update"/> (清完了 + 玩家走出
///   <see cref="RoamingZombieDirector.ReleaseRadius"/>)。"清完了"那一条同日修掉了池化误判,
///   见 <c>RoamingZombieDirector.Gone</c>。
/// - <c>IgnoreSpawnRestriction</c> / <c>BarricadeSpawn</c> / <c>HeroicOnly</c>:
///   语义未确认, 暂时忽略。
/// </summary>
public static class RoamingZombieSpawner
{
    /// <summary>
    /// 混淆类名 -> 人类可读的名字。判据 (每个类注册的技能名) 见
    /// <see cref="ZombieFactory.Kinds"/> —— 三个刷怪源共用同一张表。
    /// </summary>
    private static readonly (string Obfuscated, string Readable)[] Kinds = ZombieFactory.Kinds;

    /// <summary>
    /// 一个刷怪点周围的散布半径。地图数据没给这个值。
    ///
    /// 第一版 6 —— 一堆僵尸从同一个坑里爬出来。玩家实测 (2026-09-17): "有那种一堆僵尸挤着一个点刷新,
    /// 原版游戏没有这种刷新"。放大到 40, 落点避开静态碰撞 (墙、石头), 挑不到空地才退回原点。**我们定的。**
    /// </summary>
    public const float ScatterRadius = 40f;

    public sealed record Result(int Spawned, int SpawnPoints, IReadOnlyDictionary<string, int> ByKind);

    /// <summary>
    /// 遍历地图上所有 <see cref="Spawn_RoamingZombies"/>, 按各自的 min/max 生成僵尸。
    ///
    /// 一次把全图铺满 —— 这**不是**运行时用的路径 (那条走
    /// <see cref="RoamingZombieDirector"/> 的就近触发), 留着是给测试和
    /// "我就想看看这张图一共该有多少怪"用的。
    ///
    /// 幂等性由调用方保证 —— 本方法每调一次就生成一批。
    /// </summary>
    public static Result SpawnAll(GameManager manager)
    {
        Dictionary<string, int> byKind = new Dictionary<string, int>();
        int total = 0;
        int points = 0;

        if (manager?.MapObjects == null)
        {
            return new Result(0, 0, byKind);
        }

        foreach (MapObject mapObject in manager.MapObjects)
        {
            if (mapObject is not Spawn_RoamingZombies point)
            {
                continue;
            }

            points++;
            total += SpawnPoint(manager, point, byKind).Count;
        }

        return new Result(total, points, byKind);
    }

    /// <summary>
    /// 只生成**一个**刷怪点的僵尸, 按该点自己的 min/max。
    ///
    /// <paramref name="byKind"/> 传 null 就不统计。
    /// </summary>
    /// <returns>
    /// 实际生成出来的僵尸。<see cref="RoamingZombieDirector"/> 要拿它判断
    /// "这个点的怪是不是清完了"。
    /// </returns>
    /// <param name="countScale">数量倍率 (拾荒入夜 = 地图的 <c>EndGameSpawnRateIncrease</c>, 见 <see cref="RoamingZombieDirector.CountScale"/>)。</param>
    /// <param name="emerge">
    /// 从地里刨出来 (true) 还是直接站在那儿 (false)。野外普通点在玩家视野外就刷好, 不播出场动画;
    /// 路障旁的点 (<c>BarricadeSpawn</c>) 是打路障时冒出来的, 要出场动画。见 <see cref="RoamingZombieDirector"/>。
    /// </param>
    public static List<Character> SpawnPoint(GameManager manager, Spawn_RoamingZombies point,
                                             Dictionary<string, int> byKind = null, float countScale = 1f,
                                             bool emerge = true)
    {
        List<Character> spawned = new List<Character>();
        if (manager == null || point == null)
        {
            return spawned;
        }

        Dictionary<string, Type> typesByName = ZombieFactory.ResolveKindTypes();

        foreach ((string obfuscated, string readable) in Kinds)
        {
            (int min, int max) = CountsFor(point, readable);
            if (max <= 0)
            {
                continue;
            }

            if (!typesByName.TryGetValue(obfuscated, out Type type))
            {
                continue;
            }

            // 用模拟层自己的 RNG 而不是 System.Random —— 它参与回放的确定性重放,
            // 而且服务端所有随机都该走同一个源。
            int count = manager.Random._0023Otc(min, max + 1);
            if (countScale != 1f)
            {
                count = (int)Math.Round(count * countScale);
            }

            for (int i = 0; i < count; i++)
            {
                AddOne(manager, point, readable, type, spawned, byKind, emerge);
            }
        }

        SpawnSpecial(manager, point, typesByName, spawned, byKind, emerge);
        return spawned;
    }

    /// <summary>特殊僵尸的六种 (<c>BossType.RANDOM</c> 从这里挑)。</summary>
    private static readonly string[] SpecialKinds = { "Butcher", "Floater", "Puller", "Ram", "Screamer", "Suicider" };

    /// <summary>按 <c>SpecialZombieChance</c> 额外刷一只 <c>SpecialZombieType</c> —— 见类说明。</summary>
    private static void SpawnSpecial(GameManager manager, Spawn_RoamingZombies point, Dictionary<string, Type> typesByName,
                                     List<Character> spawned, Dictionary<string, int> byKind, bool emerge = true)
    {
        if (point.SpecialZombieType == BossType.NONE || point.SpecialZombieChance <= 0f
            || manager.Random._0023Otc(0, 1000) >= point.SpecialZombieChance * 1000f)
        {
            return;
        }

        string readable = point.SpecialZombieType switch
        {
            BossType.RANDOM => SpecialKinds[manager.Random._0023Otc(0, SpecialKinds.Length)],
            BossType.WALKER => "Walker",
            BossType.BUTCHER => "Butcher",
            BossType.FLOATER => "Floater",
            BossType.PULLER => "Puller",
            BossType.RAM => "Ram",
            BossType.SCREAMER => "Screamer",
            BossType.SUICIDER => "Suicider",
            _ => null,
        };

        string obfuscated = ZombieFactory.ObfuscatedNameFor(readable);
        if (obfuscated != null && typesByName.TryGetValue(obfuscated, out Type type))
        {
            AddOne(manager, point, readable, type, spawned, byKind, emerge);
        }
    }

    /// <summary>刷一只; 刷怪点勾了 <c>Elites</c> 且这种有精英版就刷精英版。</summary>
    private static void AddOne(GameManager manager, Spawn_RoamingZombies point, string readable, Type type,
                               List<Character> spawned, Dictionary<string, int> byKind, bool emerge = true)
    {
        Type elite = point.Elites ? ZombieFactory.EliteTypeFor(readable) : null;
        Character zombie = SpawnOne(manager, elite ?? type, point.Position,
            elite != null ? (Action<Character>)ZombieFactory.ApplyEliteLevel : null, emerge);
        if (zombie == null)
        {
            return;
        }

        spawned.Add(zombie);
        if (byKind != null)
        {
            string key = elite != null ? readable + "Elite" : readable;
            byKind.TryGetValue(key, out int seen);
            byKind[key] = seen + 1;
        }
    }

    /// <summary>
    /// 生成一只漫游僵尸。位置在刷怪点周围散布, 出场动画固定 Digging。
    ///
    /// ## 为什么漫游僵尸的出场动画是我们定的
    ///
    /// <see cref="Spawn_EventEntities"/> 和 <see cref="Spawn_Static"/> 都自带
    /// <c>SpawnAnimation</c> 字段, 事件刷怪和场景摆位按地图数据走 (见
    /// <see cref="EventSpawnDirector"/> / <see cref="StaticZombiePlacer"/>);
    /// 但 <see cref="Spawn_RoamingZombies"/> **没有这个字段** —— 漫游僵尸的出场
    /// 方式地图没规定。
    ///
    /// 取 Digging (从地里刨出来): 它是唯一不依赖场景几何的选项。ClimbWall/
    /// ClimbEdge 需要旁边真有墙, Water 需要水面, 在任意刷怪点上都用会穿帮。
    ///
    /// 这是**我们的选择**, 不是还原 —— 地图数据里没有可依据的值。
    /// </summary>
    private static Character SpawnOne(GameManager manager, Type type, Vector2 origin, Action<Character> beforeSpawn = null,
                                      bool emerge = true)
    {
        return ZombieFactory.Spawn(
            manager, type, Scatter(manager, origin),
            emerge ? _0023Oq._0023WM._0023eCc : _0023Oq._0023WM._0023Yc,   // Digging / 直接站着
            Vector2.Zero,               // 漫游僵尸的朝向没有依据, 交给 AI
            beforeSpawn);
    }

    /// <summary>
    /// 在刷怪点周围散开, 免得同一批僵尸叠在一个坐标上。
    ///
    /// 地图数据里没有散布半径这个字段, 所以这是我们加的 —— 取值只影响观感,
    /// 不影响任何判定。
    /// </summary>
    public static Vector2 Scatter(GameManager manager, Vector2 origin)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            int dx = manager.Random._0023Otc(-1000, 1001);
            int dy = manager.Random._0023Otc(-1000, 1001);
            Vector2 candidate = new Vector2(
                origin.X + dx / 1000f * ScatterRadius,
                origin.Y + dy / 1000f * ScatterRadius);

            // 原厂抛补给落点用的同一个判定 (Supplies.cs:470): 静态碰撞里有没有东西
            if (manager.CollisionManager?._0023c8 == null || !manager.CollisionManager._0023c8._0023Ub(candidate, 3f))
            {
                return candidate;
            }
        }

        return origin;
    }

    /// <summary>
    /// 刷怪点上某一类僵尸的 min/max。
    ///
    /// 字段名是明文的 (Spawn_RoamingZombies 没被混淆), 所以这里直接按名字取,
    /// 上游改字段名会编译失败而不是静默取到 0。
    /// </summary>
    private static (int Min, int Max) CountsFor(Spawn_RoamingZombies point, string kind) => kind switch
    {
        "Walker" => (point.Walkers_Min, point.Walkers_Max),
        "Butcher" => (point.Butchers_Min, point.Butchers_Max),
        "Floater" => (point.Floaters_Min, point.Floaters_Max),
        "Puller" => (point.Pullers_Min, point.Pullers_Max),
        "Ram" => (point.Rams_Min, point.Rams_Max),
        "Screamer" => (point.Screamers_Min, point.Screamers_Max),
        "Suicider" => (point.Suiciders_Min, point.Suiciders_Max),
        _ => (0, 0),
    };

}
