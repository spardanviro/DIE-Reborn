using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 让一个 NPC 跟着一个角色走: 沿导航网格寻路、被挤住了横着绕开、到身边就停。
///
/// ## 玩家实测 (2026-09-13)
///
/// "打破茧营救到幸存者后, 幸存者应该跟着玩家移动, 类似于 tutorial 里面的 amber 的自动跟随和
/// 自动寻路。"
///
/// ## 和 Amber 那套是同一个做法
///
/// <see cref="TutorialNpcAI"/> 的归队 (<c>Steer</c> / <c>TryFollowPath</c> / <c>UpdateStuck</c>)
/// 每一条都是实测踩出来的:
///
///   - 走路径点用 <c>SetGoalPosition</c> 并关掉 <c>UseGoalTarget</c> —— 开着的话 NPC 每帧朝
///     GoalTarget 重算目标位置, 直奔、撞墙不拐
///   - 迟滞区: 离开超过 <see cref="StartDistance"/> 才起步, 走到 <see cref="StopDistance"/> 才停,
///     中间不来回抖
///   - 路径对但被僵尸/玩家顶住推不动: 清路径 + 侧向走一段, 换个位置再算
///
/// 那边的代码绑死在教程 NPC 的类 (<c>_0023Mk._0023YO</c>, 有冲刺档 <c>_0023eO</c>) 上, 平民
/// (<c>_0023Mk._00237Y</c>) 直接继承 <c>NPC</c>, 没有那几个方法。所以这里按 <c>NPC</c> 重写一份,
/// 跑/走档用平民自己声明的两档 (<c>IsSprinting</c> -&gt; 跑 50 / 走 30, -Mk/-7Y.cs:100-101)。
///
/// 迟滞距离、重算距离、卡住判据的数都照抄 Amber 那边 (实测校准过), 起步距离比 Amber 小 ——
/// Amber 在跟随距离内要打怪, 平民不打, 就该贴着走。**起步/停步距离是我们定的。**
/// </summary>
public sealed class NpcFollower
{
    private sealed class State
    {
        public List<Vector2> Path;
        public Vector2 PathTarget;
        public Vector2 LastPosition;
        public bool HasLastPosition;
        public float StuckFor;
        public float DetourFor;
        public float Side = 1f;
        public bool Moving;
    }

    private readonly Dictionary<NPC, State> _states = new Dictionary<NPC, State>();
    private NavMeshPathfinder _pathfinder;
    private int _pathfinderMap = -1;

    /// <summary>离跟随对象超过这么远才起步。</summary>
    public float StartDistance { get; set; } = 35f;

    /// <summary>走到离跟随对象这么近就停。</summary>
    public float StopDistance { get; set; } = 18f;

    /// <summary>离得比这远就跑, 近了走 —— 免得贴身时一顿一顿地冲。</summary>
    public float RunDistance { get; set; } = 45f;

    /// <summary>跟随对象挪开超过这么远就重算路径 (同 Amber)。</summary>
    public float PathRecomputeDistance { get; set; } = 60f;

    /// <summary>走到拐点这么近就算过了 (同 Amber)。</summary>
    public float WaypointReachedDistance { get; set; } = 18f;

    /// <summary>每秒挪不到这么远算没在动 (同 Amber)。</summary>
    public float StuckSpeedThreshold { get; set; } = 8f;

    /// <summary>卡住多久才开始绕 (同 Amber)。</summary>
    public float StuckGracePeriod { get; set; } = 0.6f;

    /// <summary>一次绕多久 (同 Amber)。</summary>
    public float DetourDuration { get; set; } = 1.2f;

    /// <summary>绕行时目标点放多远 (同 Amber)。</summary>
    public float DetourStepDistance { get; set; } = 80f;

    /// <summary>
    /// 往 <paramref name="goal"/> 走一步 (每帧调)。<paramref name="goal"/> 是跟随对象的位置,
    /// 或者一个固定的目的地 (比如基地)。
    /// </summary>
    /// <returns>true = 这一帧在走; false = 已经到了、停着。</returns>
    public bool Step(GameManager simulation, NPC npc, Vector2 goal, float deltaTime)
    {
        if (!_states.TryGetValue(npc, out State state))
        {
            state = new State();
            _states[npc] = state;
        }

        float distance = Vector2.Distance(npc.Position, goal);

        if (!state.Moving && distance > StartDistance)
        {
            state.Moving = true;
        }

        if (state.Moving && distance <= StopDistance)
        {
            state.Moving = false;
            state.Path = null;
            state.StuckFor = 0f;
            state.DetourFor = 0f;
        }

        if (!state.Moving)
        {
            Stop(npc);
            state.HasLastPosition = false;
            return false;
        }

        npc.IsSprinting = distance > RunDistance;

        if (UpdateStuck(state, npc.Position, deltaTime))
        {
            state.Path = null;
            state.DetourFor = DetourDuration;
            state.Side = -state.Side;
        }

        if (state.DetourFor > 0f)
        {
            state.DetourFor -= deltaTime;
            MoveTo(npc, npc.Position + Sideways(npc.Position, goal, state.Side) * DetourStepDistance);
            return true;
        }

        NavMeshPathfinder pathfinder = PathfinderFor(simulation);
        if (pathfinder != null)
        {
            if (state.Path == null || state.Path.Count == 0
                || Vector2.Distance(state.PathTarget, goal) > PathRecomputeDistance)
            {
                state.Path = pathfinder.FindPath(npc.Position, goal);
                state.PathTarget = goal;
            }

            while (state.Path != null && state.Path.Count > 0
                   && Vector2.Distance(npc.Position, state.Path[0]) <= WaypointReachedDistance)
            {
                state.Path.RemoveAt(0);
            }

            if (state.Path != null && state.Path.Count > 0)
            {
                MoveTo(npc, state.Path[0]);
                return true;
            }
        }

        // 没网格 / 不连通 / 最后一段: 直奔
        MoveTo(npc, goal);
        return true;
    }

    /// <summary>停下, 并忘掉这个 NPC 的寻路状态 (交出去之前调)。</summary>
    public void Forget(NPC npc)
    {
        if (npc != null)
        {
            _states.Remove(npc);
        }
    }

    /// <summary>全部忘掉。</summary>
    public void Clear() => _states.Clear();

    /// <summary>
    /// 站得下一个人吗: 在导航网格上, **而且**不压着静态碰撞。
    ///
    /// 只看网格不够 —— 实测 map26 有网格上的点压在车/杂物的碰撞体里, 平民生成在那儿速度是满的,
    /// 位置一帧都不变 (四面都是碰撞)。碰撞判定用抛补给那段的同一个调用 (-EF/-cyc.cs:124)。
    /// </summary>
    public bool IsFree(GameManager simulation, Vector2 point, float radius = SpotRadius) =>
        (PathfinderFor(simulation)?.IsWalkable(point) ?? true)
        && !simulation.CollisionManager._0023c8._0023Ub(point, radius, StaticGeometry);

    /// <summary>
    /// 只看地形/建筑 (视线判定用的同一组标志)。默认那组还带动态物体 —— 茧、玩家、僵尸站着的地方
    /// 都会判成"站不下", 茧刚碎还没回池的那一帧, 平民就一个出生点都找不到了。
    /// </summary>
    private const ConductorGameLogic.Collision.GeometryCollisionFlags StaticGeometry =
        ConductorGameLogic.Collision.GeometryCollisionFlags._00237jd
        | ConductorGameLogic.Collision.GeometryCollisionFlags._00239jd;

    /// <summary>
    /// 离 <paramref name="point"/> 最近的站得下的地方: 本身能站就原样; 不能就一圈圈往外找
    /// (每 8 单位一圈, 找到 80); 都不行原样返回。
    ///
    /// **不退到"最近三角形的重心"** —— 几个挨着的点会吸到同一个重心上, 人叠在一起互相顶死。
    /// </summary>
    public Vector2 FindFreeSpot(GameManager simulation, Vector2 point, float radius = SpotRadius)
    {
        if (IsFree(simulation, point, radius))
        {
            return point;
        }

        for (float ring = 8f; ring <= 80f; ring += 8f)
        {
            for (int i = 0; i < 12; i++)
            {
                double angle = System.Math.PI * 2.0 * i / 12;
                Vector2 candidate = point + new Vector2((float)System.Math.Cos(angle), (float)System.Math.Sin(angle)) * ring;
                if (IsFree(simulation, candidate, radius))
                {
                    return candidate;
                }
            }
        }

        return point;
    }

    /// <summary>判"站得下"时的人体半径 —— 平民 Radius 5, 留一点余量。</summary>
    public const float SpotRadius = 6f;

    /// <summary>停下。见 <see cref="TutorialNpcAI"/> 的 Idle —— 目标位置设成自己才会关掉"有目标要走"。</summary>
    public static void Stop(NPC npc)
    {
        npc.UseGoalTarget = false;
        npc.GoalTarget = null;
        npc.IsSprinting = false;
        npc.SetGoalPosition(npc.Position);
    }

    private static void MoveTo(NPC npc, Vector2 where)
    {
        npc.UseGoalTarget = false;
        npc.GoalTarget = null;
        npc.SetGoalPosition(where);
    }

    private bool UpdateStuck(State state, Vector2 position, float deltaTime)
    {
        bool justStuck = false;

        if (state.HasLastPosition && deltaTime > 0f && state.DetourFor <= 0f)
        {
            float moved = Vector2.Distance(position, state.LastPosition);
            state.StuckFor = moved / deltaTime < StuckSpeedThreshold ? state.StuckFor + deltaTime : 0f;

            if (state.StuckFor >= StuckGracePeriod)
            {
                state.StuckFor = 0f;
                justStuck = true;
            }
        }

        state.LastPosition = position;
        state.HasLastPosition = true;
        return justStuck;
    }

    private static Vector2 Sideways(Vector2 from, Vector2 to, float side)
    {
        Vector2 toward = to - from;
        if (toward == Vector2.Zero)
        {
            return new Vector2(side, 0f);
        }

        toward.Normalize();
        return new Vector2(-toward.Y * side, toward.X * side);
    }

    private NavMeshPathfinder PathfinderFor(GameManager simulation)
    {
        int map = simulation.MapIndex;
        if (_pathfinderMap == map)
        {
            return _pathfinder;
        }

        ConductorGameLogic.Pathfinding.NavMesh mesh = MapManager.GetNavMesh(map);
        if (mesh?.Triangles == null || mesh.Triangles.Length == 0)
        {
            _pathfinderMap = map;       // 没网格就别每帧再查
            return null;
        }

        _pathfinder = new NavMeshPathfinder(mesh);
        _pathfinderMap = map;
        return _pathfinder;
    }
}
