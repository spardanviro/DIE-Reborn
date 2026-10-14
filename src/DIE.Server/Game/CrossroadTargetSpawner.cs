using System;
using System.Collections.Generic;
using System.Linq;
using _0023cL;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Gameplay;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 十字路口一个事件的**具名目标**按原厂时间表出场 —— Looter、囤积者、巨型僵尸、带词条的硬茬。
///
/// ## 玩家实测 (2026-09-13)
///
/// "我碰见了一个问题, 走到任务点附近什么都没有, 这可能是刷 boss 的地方。"
///
/// 就是。KillEnemy 的客户端文案是 "Kill the named zombies in the area" / "Kill the Looter" /
/// "Kill the Hoarder" / "Kill the Massive zombie", 要打的东西列在原厂
/// <c>EnemyVariation.EventTargets</c> 里 —— 上一版一只都没刷, 只按刷怪组摇了普通怪,
/// 于是"Boss 点"空着。
///
/// ## 时间表是原厂的
///
/// 每条目标带 <c>SpawnCondition</c> + 秒数, 实测三种:
///
///   Start@0      开场就在 (Hoarder Easy 那种一只 Boss 的)
///   Delay@t      事件开始后 t 秒 (Butcher Ambush: 0/6/14/20/28 秒各一只)
///   OnlyForced@t 护送途中逼出来的 —— 我们同样按时间出 (没有"逼"的判据可抄)
///
/// ## 词条是原厂的
///
/// 每条目标带 <c>AttributeType[]</c> (Defense / Relentless / Vitality / Slaughterous ...)。
/// 原厂留了一张完整的 词条 -&gt; buff 类型 表 (<c>_0023cL._0023bL._0023aL</c>, -cL/-bL.cs),
/// 却零调用者 —— 又是"身子齐、调用点随服务端丢了"。这里照那张表把 buff 挂到怪身上,
/// 客户端的词条图标和数值效果都是 buff 自己带的。池子里拿不到 (某个词条在这个模式没缓存)
/// 就跳过并计数, 不影响出怪。
///
/// ## 名字没做
///
/// <c>EnemyTarget.Name</c> (比如 <c>PCStrings.GUI.EnemyNames.KarlBulk</c>) 客户端的悬停名
/// 读的是类上写死的 <c>HoverEntityName</c> 属性, 服务端没有可设的字段 —— 这个名字在原厂
/// 是怎么上屏的查不到, 先不编。
/// </summary>
public sealed class CrossroadTargetSpawner
{
    private readonly List<CrossroadEventCatalog.Target> _pending = new List<CrossroadEventCatalog.Target>();
    private readonly List<Character> _spawned = new List<Character>();
    private IReadOnlyList<Spawn_EventEntities> _spawns = Array.Empty<Spawn_EventEntities>();
    private readonly HashSet<int> _paid = new HashSet<int>();
    private Vector2 _center;
    private float _elapsed;

    /// <summary>这一局一共要出几只。</summary>
    public int Total { get; private set; }

    /// <summary>出过场的具名目标 (按出场顺序, 含已经死了的)。</summary>
    public IReadOnlyList<Character> Spawned => _spawned;

    /// <summary>已经出场的。</summary>
    public int Released => Total - _pending.Count;

    /// <summary>出场之后死掉的 (含对象池回收)。</summary>
    public int Killed => _spawned.Count(z => z == null || !z.IsActive || z.IsDead);

    /// <summary>全部出过场、而且全死了 —— KillEnemy 的完成判据。没有目标时恒为 false。</summary>
    public bool AllDown => Total > 0 && _pending.Count == 0 && Killed >= _spawned.Count
                           && _spawned.Count > 0;

    /// <summary>出场被挡住的 (模型缓存满 / 池空), 挡住的会在下一帧重试。诊断用。</summary>
    public int Blocked { get; private set; }

    /// <summary>挂不上的词条数 (池里没有那种 buff)。诊断用。</summary>
    public int AttributesMissing { get; private set; }

    /// <summary>本帧出场的 —— 交给宿主激怒 (它们是冲着玩家来的)。</summary>
    public readonly List<Character> SpawnedThisTick = new List<Character>();

    /// <summary>宿主读完打日志再清。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>
    /// 开一个新事件。<paramref name="spawns"/> 是这个区域自己的刷怪位 (离中心近的在前)。
    /// </summary>
    public void Arm(CrossroadEventCatalog.Mission mission, IReadOnlyList<Spawn_EventEntities> spawns)
    {
        _pending.Clear();
        _spawned.Clear();
        _paid.Clear();
        _elapsed = 0f;
        Blocked = 0;

        _pending.AddRange(mission.Targets.OrderBy(t => t.At));
        _spawns = spawns ?? Array.Empty<Spawn_EventEntities>();
        _center = mission.Area.Position;
        Total = _pending.Count;
    }

    /// <summary>
    /// 每帧一次: 时间到了的出场。
    /// </summary>
    /// <param name="anchor">
    /// 在哪附近出 —— 一般是事件中心; 护送时是平民现在的位置 (逼出来的就该冲着平民来)。
    /// </param>
    public void Update(GameManager simulation, float deltaTime, Vector2? anchor = null)
    {
        SpawnedThisTick.Clear();

        if (simulation == null || _pending.Count == 0)
        {
            return;
        }

        _elapsed += deltaTime;
        Vector2 around = anchor ?? _center;

        // 一帧最多放两只 —— 同 EventSpawnDirector 的削峰理由 (客户端一口气建模型会卡)
        for (int placed = 0; placed < 2 && _pending.Count > 0 && _pending[0].At <= _elapsed; placed++)
        {
            CrossroadEventCatalog.Target target = _pending[0];

            Character zombie = Place(simulation, target, around);
            if (zombie == null)
            {
                Blocked++;
                break;      // 留在队头, 下一帧再试
            }

            _pending.RemoveAt(0);
            _spawned.Add(zombie);
            SpawnedThisTick.Add(zombie);

            int attributes = ApplyAttributes(simulation, zombie, target.Attributes);

            // 词条越多描边越亮 (暗金 -> 金橙 -> 红), 见 ZombieFactory.MarkElite
            ZombieFactory.MarkElite(zombie, attributes);

            Events.Add($"十字路口具名目标出场: {target.Zombie.Name}"
                       + (target.Named ? $" \"{target.NameKey.Split('.').Last()}\"" : "")
                       + $" @{target.Condition}{target.Time:F0}s 词条 {attributes}/{target.Attributes.Length} "
                       + $"({Released}/{Total})");
        }
    }

    /// <summary>
    /// 把 <paramref name="pool"/> 按目标数平摊, <paramref name="dead"/> 该拿多少。
    /// 不是这个事件的具名目标 (伴随的杂兵) 返回 null。除不尽的零头给先出场的几只,
    /// 所以全杀完正好是 <paramref name="pool"/>。
    ///
    /// 每个位子只付一次: 对象池会把死掉的目标回收再发出来 (可能又当成杂兵),
    /// 不记账的话同一个对象能领两回。
    /// </summary>
    public int? ShareOf(Character dead, int pool)
    {
        if (dead == null || Total <= 0)
        {
            return null;
        }

        for (int index = 0; index < _spawned.Count; index++)
        {
            if (ReferenceEquals(_spawned[index], dead) && _paid.Add(index))
            {
                return pool / Total + (index < pool % Total ? 1 : 0);
            }
        }

        return null;
    }

    /// <summary>挑一个离 <paramref name="around"/> 最近的刷怪位出场 (带原厂出场动画)。</summary>
    private Character Place(GameManager simulation, CrossroadEventCatalog.Target target, Vector2 around)
    {
        if (_spawns.Count == 0)
        {
            return ZombieFactory.Spawn(simulation, target.Zombie, around, _0023Oq._0023WM._0023eCc,
                Vector2.UnitX);
        }

        // 最近的一撮里挑这种怪会那个出场动画的 (公羊只会跃出, Puller 只会爬 ...),
        // 再在前几个里轮着用, 免得一串 Boss 从同一个洞里爬出来
        List<Spawn_EventEntities> nearest = ZombieFactory.PreferAnimated(target.Zombie, _spawns
                .OrderBy(s => Vector2.DistanceSquared(s.Position, around))
                .Take(16)
                .ToList())
            .Take(6)
            .ToList();

        Spawn_EventEntities at = nearest[_spawned.Count % nearest.Count];
        return ZombieFactory.Spawn(simulation, target.Zombie, at.Position, at.SpawnState, at.Direction);
    }

    /// <summary>
    /// 词条 -&gt; 原厂 buff (<c>AttributeType._0023aL()</c>), 挂到怪身上。返回挂上几个。
    /// </summary>
    private int ApplyAttributes(GameManager simulation, Character zombie, AttributeType[] attributes)
    {
        int applied = 0;

        foreach (AttributeType attribute in attributes)
        {
            Type type = attribute._0023aL();
            if (type == null)
            {
                continue;
            }

            if (simulation.GetGameObjectFromPool(type) is not Buff buff || buff.IsDummyObject)
            {
                AttributesMissing++;
                continue;
            }

            simulation.ApplyBuff(buff, zombie, zombie, zombie);
            applied++;
        }

        return applied;
    }
}
