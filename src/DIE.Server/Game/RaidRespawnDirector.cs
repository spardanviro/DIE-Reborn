using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// Starter Mission / Raid 图上的推进式复活点 (<c>Spawn_RaidRespawn</c>)。
///
/// ## 玩家问"地图里的复活点有没有实装" —— 之前没有
///
/// <c>Spawn_RaidRespawn</c> 这个类型**全代码树零消费者**, 原厂那半边随服务端
/// 丢失。表现是玩家死了走 <c>LidgrenHost.HandleRespawn -&gt; PickSpawnPoint()</c>,
/// 和开局出生点是同一个 —— **永远回图头**, 打到一半死了要重新跑一遍。
///
/// <c>ScoutMission.GetSpawnPosition</c> 也帮不上忙: 它写死了
/// <c>GetPlayerSpawnPoint(team, stillAtStart: **true**)</c>, 只认起点。
///
/// ## 数据长什么样 (探针实测, ScoutMissionProbeTests)
///
/// 每个复活点关联**一个** <c>Event_Trigger</c> 区域:
///
///   Scout_02_Lab  id=10002 pos=(-395.3, 237.6)  activeAreas=[10098]
///                 id=10031 pos=(-533.2,-133.6)  activeAreas=[10081]
///                 id=10123 pos=(-192.0, 330.8)  activeAreas=[10038]
///
/// 三张 Scout 图结构一致 (Outpost 3 个 / Lab 3 个 / Club 4 个)。
/// <c>Event_Trigger</c> 是 <c>PolygonMapObject</c> —— 多边形区域, 不是半径。
///
/// ## 判定要用原厂那个, 而且**不是** PolygonShape.TestPoint
///
/// 第一版用了 <c>PolygonShape.TestPoint</c>, 当场越界抛异常。原因是它
/// 拿 <c>Normals[i]</c> 和 <c>Vertices[i]</c> 配对遍历 (PolygonShape.cs:105-116)
/// —— 那是给**凸**多边形做物理碰撞的, 要求两者等长。而地图上的触发区域
/// 不是凸的 (探针实测 Scout_02_Lab):
///
///   trigger 10098  顶点 4  法线 4   <- 复活点 10002 用, 恰好没炸
///   trigger 10038  顶点 7  法线 4   <- 复活点 10123 用, **越界**
///   trigger 10081  顶点 7  法线 4   <- 复活点 10031 用, **越界**
///
/// 原厂判"角色在不在 Event_Trigger 里"用的是另一个函数
/// (<c>-EF/-K2b.cs:216</c>):
///
///     _0023huc._00237tc(Collider, entity.Position, entity.Radius)
///
/// 它走 <c>Polygon.VerticesWorldSpace</c> 的射线法 (`-6/-huc.cs:142`),
/// **不碰法线**, 所以凹多边形也对; 还多接一个半径参数, 比点判定更接近
/// 玩家真实的碰撞体。
///
/// 用之前要先 <c>CalculateVerticesWorldSpace()</c> —— 原厂在
/// <c>-EK/-O1b.cs:373</c> 建触发器时就是先调它。
///
/// ## 只记录, 不越权
///
/// 本类只回答"这个玩家现在的复活点在哪", 真正复活由
/// <c>RespawnPolicy.TryRespawn</c> 做 (它的位置参数本来就由调用方给)。
///
/// ## 顺序: 最后进入的区域说了算
///
/// <c>Spawn_RaidRespawn</c> 上**没有**顺序字段 (ID 是 10002/10031/10123 这种
/// 地图编号, 不是关卡进度)。所以不像 Horde 的 <c>Checkpoint</c> 那样能
/// "只往大了推" —— 这里按"最后踏入的区域"算。玩家往前走就是往前推,
/// 往回走会退回去。等实测看是不是原版手感。
///
/// ## 十字路口: 复活点没有区域, 走近就激活
///
/// 玩家实测 (2026-09-14): "野外的复活点复活功能没有实装, 应该是走近一个复活点就激活这个复活点, 然后死亡的话
/// 就可以在这个复活点复活。现在死亡后显示点击鼠标左键在复活点复活, 但是左键没有效果, 还要等倒计时结束复活到基地里。"
///
/// 探针 (Crossroad_map26_small): 8 个 <c>Spawn_RaidRespawn</c>, 每块拼图一个 (90003 在基地), **ActiveAreas 全空**
/// —— 上一版登记时把没区域的点丢了, 于是十字路口一个复活点都没有, 左键永远回基地。
/// 客户端那一侧是现成的: 倒计时走完 <c>GUI_DeathPanel.RespawnFinishEffect</c> 在 RaidMission / ScoutMission
/// 里写 "Left click to respawn at a Respawn Point!", 左键发 <c>GameMessageType.Respawn</c>。
///
/// 没区域的点按距离激活 (<see cref="ActivationRadius"/>, **我们定的**), 激活时放原厂检查点那个
/// <c>Vfx_CheckPoint_Reached_01</c> (每个点第一次被激活时放一次)。
/// </summary>
public sealed class RaidRespawnDirector
{
    /// <summary>一个复活点和它的激活区域。</summary>
    private sealed class Point
    {
        public int Id;
        public Vector2 Position;
        public List<Event_Trigger> Areas;
        public bool EverActivated;
    }

    /// <summary>没有激活区域的复活点 (十字路口), 离多近算走到了。**我们定的。**</summary>
    public float ActivationRadius { get; set; } = 50f;

    private readonly List<Point> _points = new();

    /// <summary>每个玩家当前的复活点。</summary>
    private readonly Dictionary<Player, Vector2> _current = new();

    private bool _scanned;

    /// <summary>登记了几个复活点。诊断用。</summary>
    public int ArmedPoints => _points.Count;

    /// <summary>这一帧有谁的复活点变了 —— 宿主拿它打日志。</summary>
    public readonly List<string> Events = new();

    /// <summary>
    /// 把复活点和它的区域配上对。幂等。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        _scanned = true;

        // Event_Trigger 按 ID 建索引 —— ActiveAreas 里存的是 ObjectID
        Dictionary<int, Event_Trigger> triggers = new();
        foreach (Event_Trigger trigger in simulation.MapObjects.OfType<Event_Trigger>())
        {
            triggers[trigger.ID] = trigger;
        }

        foreach (Spawn_RaidRespawn spawn in simulation.MapObjects.OfType<Spawn_RaidRespawn>())
        {
            List<Event_Trigger> areas = new();

            foreach (MapObjectReference reference in
                     spawn.ActiveAreas ?? System.Array.Empty<MapObjectReference>())
            {
                if (!triggers.TryGetValue(reference.ObjectID, out Event_Trigger area)
                    || area.Polygon == null)
                {
                    continue;
                }

                // 判定读的是 VerticesWorldSpace, 不先算它数组是空的 ——
                // 原厂建触发器时同样先调这一句 (`-EK/-O1b.cs:373`)。
                // 内部有 _IsDirty 守卫, 重复调用不重算。
                area.Polygon.CalculateVerticesWorldSpace();
                areas.Add(area);
            }

            // 区域为空的 (十字路口) 按距离激活, 见类说明
            _points.Add(new Point
            {
                Id = spawn.ID,
                Position = spawn.Position,
                Areas = areas,
            });
        }
    }

    /// <summary>
    /// 看每个活着的玩家踩在哪个区域里, 更新他的复活点。
    /// </summary>
    public void Update(GameManager simulation)
    {
        Events.Clear();

        if (simulation == null || _points.Count == 0)
        {
            return;
        }

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            foreach (Point point in _points)
            {
                if (!IsInside(point, player.Position, player.Radius))
                {
                    continue;
                }

                if (_current.TryGetValue(player, out Vector2 already) && already == point.Position)
                {
                    break;
                }

                _current[player] = point.Position;
                Events.Add($"复活点前移到 #{point.Id} ({point.Position.X:F0},{point.Position.Y:F0})");

                if (!point.EverActivated)
                {
                    point.EverActivated = true;
                    simulation.SendEffectEvent(ConductorGameLogic.View.EffectType.Vfx_CheckPoint_Reached_01, point.Position);
                }

                break;
            }
        }
    }

    /// <summary>
    /// 这个玩家现在该在哪复活。没踏过任何区域时返回 false ——
    /// 调用方退回开局出生点。
    /// </summary>
    public bool TryGetRespawnPoint(Player player, out Vector2 position)
    {
        if (player != null && _current.TryGetValue(player, out position))
        {
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>换局时清干净。</summary>
    public void Reset()
    {
        _scanned = false;
        _points.Clear();
        _current.Clear();
        Events.Clear();
    }

    /// <summary>
    /// 玩家在这个复活点的任一激活区域内吗 —— 走原厂那条判定, 理由见类说明。
    /// </summary>
    private bool IsInside(Point point, Vector2 worldPosition, float radius)
    {
        if (point.Areas.Count == 0)
        {
            return Vector2.DistanceSquared(point.Position, worldPosition)
                   <= (ActivationRadius + radius) * (ActivationRadius + radius);
        }

        foreach (Event_Trigger area in point.Areas)
        {
            if (area?.Polygon == null)
            {
                continue;
            }

            if (_00236._0023huc._00237tc(area, worldPosition, radius))
            {
                return true;
            }
        }

        return false;
    }
}
