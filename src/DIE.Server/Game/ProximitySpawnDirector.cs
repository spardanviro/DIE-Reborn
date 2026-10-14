using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 玩家走近才出场的僵尸 —— Scout 图上那些**没有事件认领**的
/// <see cref="Spawn_EventEntities"/>。
///
/// ## 为什么不是开局全摆
///
/// 上一版把它们全部开局摆出来 (Scout_02_Lab 上 73 只 + 38 只静态 = 111 只),
/// 玩家实测: **"walker 刷得太多了, 原游戏没那么多。"**
///
/// 而**出场姿势本身就是证据**:
///
///   Spawn_Static         Sitting / Eating / Laying     待机 -> 一直在场
///   Spawn_EventEntities  Crawling / Digging / Jumping / Leaping / Stumbling
///                                                      **出场动画** -> 被触发才现身
///
/// 从地里钻出来、跳出来这些是"登场"动作, 不是"待着"的姿势。所以这些点
/// 本来就该等玩家靠近才出场 —— 位置是原厂的, 时机由距离决定。
///
/// 玩家说的"没有刷怪, 僵尸都是提前摆好的"和这条不矛盾: 位置固定、每个点
/// 只出一次, 不是凭空循环刷新。
///
/// ## 只管没人认领的
///
/// 教程图和 Horde 图上这些点归 <see cref="EventSpawnDirector"/>
/// (Event_TriggerPosition -&gt; Event_Assault -&gt; Spawn_EventEntities 三层)。
/// 判据放在图级别: 全图有 <c>Event_Assault</c> 就整个跳过, 一个都不碰。
///
/// ## 哪些数是我们定的
///
/// <see cref="TriggerRadius"/> **全是我们定的** —— <c>Spawn_EventEntities</c>
/// 只有 <c>SpawnAnimation</c>, 没有半径也没有归属字段。等玩家实测校准。
/// </summary>
public sealed class ProximitySpawnDirector
{
    /// <summary>
    /// 玩家离多近就让这个点出场。
    ///
    /// **这是我们定的。** 取 120 —— 比教程里事件触发的半径
    /// (<see cref="EventSpawnDirector.MaxTriggerRadius"/> = 160) 小一点,
    /// 免得玩家还没看清就一片怪冒出来; 又比僵尸的仇恨半径大, 让它们有时间
    /// 播完出场动画再扑上来。手感不对调这里。
    /// </summary>
    public float TriggerRadius { get; set; } = 120f;

    /// <summary>
    /// 两个出场点之间至少隔多远 —— 比这近的只留一个。
    ///
    /// **这是我们定的。** 玩家实测"第一个 barricade 前原版十几只, 现在大几十只",
    /// 取 60 大约能把密集段稀释到三分之一。数字不对就调这里, 调大更稀。
    ///
    /// 稀释是**确定性**的 (按地图顺序贪心), 不用随机数 —— 同一张图每局一样,
    /// 玩家报"这里怪太多"时才对得上号。
    /// </summary>
    public float MinimumSpacing { get; set; } = 60f;

    /// <summary>被稀释掉的点数。诊断用。</summary>
    public int ThinnedOut { get; private set; }

    /// <summary>登记了几个待出场的点。诊断用。</summary>
    public int ArmedPoints => _pending.Count;

    /// <summary>已经出场了几只。诊断用。</summary>
    public int Spawned { get; private set; }

    /// <summary>这一帧新出场的 —— 宿主拿它去激怒 (让它们立刻扑向玩家)。</summary>
    public readonly List<Character> SpawnedThisTickList = new();

    private sealed class Point
    {
        public Vector2 Position;
        public Vector2 Direction;
        public _0023Oq._0023WM State;
    }

    private readonly List<Point> _pending = new();
    private Type _walker;
    private bool _scanned;

    /// <summary>
    /// 登记待出场的点。幂等。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        _scanned = true;

        // 有事件刷怪的图 (教程/Horde) 一个都不碰 —— 抢着摆会让它们开局全涌出来
        if (simulation.MapObjects.OfType<Event_Assault>().Any())
        {
            return;
        }

        string obfuscated = ZombieFactory.ObfuscatedNameFor(ZombieFactory.DefaultKind);
        Dictionary<string, Type> types = ZombieFactory.ResolveKindTypes();

        if (obfuscated == null || !types.TryGetValue(obfuscated, out _walker))
        {
            return;
        }

        foreach (Spawn_EventEntities marker in simulation.MapObjects.OfType<Spawn_EventEntities>())
        {
            // 离已选中的点太近就丢掉 —— 地图给的是候选池, 见类说明
            bool tooClose = false;
            foreach (Point chosen in _pending)
            {
                if (Vector2.Distance(chosen.Position, marker.Position) < MinimumSpacing)
                {
                    tooClose = true;
                    break;
                }
            }

            if (tooClose)
            {
                ThinnedOut++;
                continue;
            }

            _pending.Add(new Point
            {
                Position = marker.Position,
                Direction = marker.Direction,
                State = marker.SpawnState,
            });
        }
    }

    /// <summary>
    /// 玩家走近的点让它出场。每个点只出一次。
    /// </summary>
    public void Update(GameManager simulation)
    {
        SpawnedThisTickList.Clear();

        if (simulation == null || _pending.Count == 0 || _walker == null)
        {
            return;
        }

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            Point point = _pending[i];

            if (!AnyPlayerNear(simulation, point.Position))
            {
                continue;
            }

            _pending.RemoveAt(i);

            Character zombie = ZombieFactory.Spawn(
                simulation, _walker, point.Position, point.State, point.Direction);

            if (zombie == null)
            {
                // 模型缓存拦下了 —— 这个点就此作废, 不排队重试
                // (重试会在玩家站着不动时反复撞同一道闸门)
                continue;
            }

            Spawned++;
            SpawnedThisTickList.Add(zombie);
        }
    }

    /// <summary>换局时清干净。</summary>
    public void Reset()
    {
        _scanned = false;
        _pending.Clear();
        SpawnedThisTickList.Clear();
        Spawned = 0;
        ThinnedOut = 0;
        _walker = null;
    }

    private bool AnyPlayerNear(GameManager simulation, Vector2 position)
    {
        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            if (Vector2.Distance(player.Position, position) <= TriggerRadius)
            {
                return true;
            }
        }

        return false;
    }
}
