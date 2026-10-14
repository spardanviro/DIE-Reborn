using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Collision;
using ConductorGameLogic.Entities;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 僵尸绕墙: 直线过不去就沿导航网格走。
///
/// ## 玩家实测 (2026-09-14)
///
/// "僵尸应该能自动寻路, 和 tutorial 里面的 amber 一样, 现在僵尸只会顶着地图往玩家的方向走, 不会自动寻路。"
///
/// ## 原厂留下的只有局部避障
///
/// <c>NPC.UpdateCharacter</c> (NPC.cs:440) 有目标就朝目标直线走, 路上撞到静态碰撞时
/// <c>_00236M</c> 挑一个侧向的"局部绕行点" —— 绕得过一根柱子, 绕不过一堵墙、一排车。
/// 真正的寻路在服务端, 丢了。
///
/// ## 做法 (和 Amber / 平民同一套网格)
///
///   - 离目标很近 (<see cref="DirectDistance"/>), 或者和目标之间**没有静态几何挡着** -&gt; 不插手,
///     照原厂直追 (<c>UseGoalTarget</c>), 原厂的局部避障照样生效
///   - 挡着 -&gt; <see cref="NavMeshPathfinder"/> 算一条路, 用 <c>SetGoalPosition</c> 走拐点
///     (关掉 <c>UseGoalTarget</c>, 开着的话 NPC 每帧朝目标重算方向, 直奔撞墙 —— Amber 那边踩过)
///   - 拐点用"拉线": 下一个拐点之后那个也看得见 (三条平行射线, 按体宽) 就跳过去, 不在边中点之间折来折去
///
/// "挡没挡着"用视线判定那组几何标志 (同 <see cref="ZombieBrain.CanSee"/>): 挡视线的墙、车、建筑就是要绕的东西。
///
/// ## 开销
///
/// 一局几十只僵尸同时追人, 每只每帧算 A* 撑不住。所以: 射线每只每 <see cref="RecheckSeconds"/> 秒测一次;
/// 目标挪开超过 <see cref="RecomputeDistance"/> 才重算; 一帧最多算 <see cref="MaxPathsPerFrame"/> 条,
/// 轮不到的这一帧先直追。**这几个数是我们定的。**
/// </summary>
public sealed class ZombiePathing
{
    private sealed class State
    {
        public List<Vector2> Path;
        public Vector2 PathTarget;
        public float RecheckIn;
        public bool Blocked;
    }

    private readonly Dictionary<(int Index, int Uses), State> _states = new Dictionary<(int, int), State>();
    private NavMeshPathfinder _pathfinder;
    private int _pathfinderMap = -1;
    private int _pathsThisFrame;

    /// <summary>离目标这么近就直追, 不寻路。</summary>
    public float DirectDistance { get; set; } = 20f;

    /// <summary>多久重新测一次"直线挡没挡着"。</summary>
    public float RecheckSeconds { get; set; } = 0.3f;

    /// <summary>目标挪开超过这么远才重算路径。</summary>
    public float RecomputeDistance { get; set; } = 40f;

    /// <summary>走到拐点这么近算过了。</summary>
    public float WaypointReachedDistance { get; set; } = 10f;

    /// <summary>一帧最多算几条路径。</summary>
    public int MaxPathsPerFrame { get; set; } = 6;

    /// <summary>诊断: 这一帧有几只在沿网格绕。</summary>
    public int Detouring { get; private set; }

    /// <summary>诊断: 一共算过几条路径。</summary>
    public int PathsComputed { get; private set; }

    /// <summary>每帧开头调一次。</summary>
    public void BeginFrame()
    {
        _pathsThisFrame = 0;
        Detouring = 0;
    }

    /// <summary>
    /// 往 <paramref name="goal"/> 走。
    /// </summary>
    /// <returns>
    /// true = 这一帧由寻路接管 (已经 <c>SetGoalPosition</c> 到拐点、关了 <c>UseGoalTarget</c>);
    /// false = 不用绕, 调用方照原厂直追。
    /// </returns>
    public bool Steer(GameManager simulation, NPC npc, Vector2 goal, float deltaTime)
    {
        (int, int) key = (npc.IndexGlobal, npc.NumberOfGlobalCacheUses);
        if (!_states.TryGetValue(key, out State state))
        {
            state = new State();
            _states[key] = state;
        }

        if (Vector2.DistanceSquared(npc.Position, goal) <= DirectDistance * DirectDistance)
        {
            state.Path = null;
            return false;
        }

        state.RecheckIn -= deltaTime;
        if (state.RecheckIn <= 0f)
        {
            state.RecheckIn = RecheckSeconds;
            state.Blocked = Blocked(simulation, npc.Position, goal, npc.Radius);
        }

        if (!state.Blocked)
        {
            state.Path = null;
            return false;
        }

        NavMeshPathfinder pathfinder = PathfinderFor(simulation);
        if (pathfinder == null)
        {
            return false;
        }

        bool stale = state.Path == null || state.Path.Count == 0
                     || Vector2.DistanceSquared(state.PathTarget, goal) > RecomputeDistance * RecomputeDistance;

        if (stale && _pathsThisFrame < MaxPathsPerFrame)
        {
            _pathsThisFrame++;
            PathsComputed++;
            state.Path = pathfinder.FindPath(npc.Position, goal);
            state.PathTarget = goal;
        }

        List<Vector2> path = state.Path;
        if (path == null || path.Count == 0)
        {
            return false;       // 不连通 / 这帧轮不到: 先直追
        }

        while (path.Count > 0 && Vector2.DistanceSquared(npc.Position, path[0])
               <= WaypointReachedDistance * WaypointReachedDistance)
        {
            path.RemoveAt(0);
        }

        // 拉线: 后面那个拐点也直线可达, 就跳过眼前这个 (每帧最多跳两个, 射线不白打)
        for (int skip = 0; skip < 2 && path.Count > 1
                           && !Blocked(simulation, npc.Position, path[1], npc.Radius); skip++)
        {
            path.RemoveAt(0);
        }

        if (path.Count == 0)
        {
            state.Path = null;
            return false;
        }

        npc.UseGoalTarget = false;
        npc.SetGoalPosition(path[0]);
        Detouring++;
        return true;
    }

    /// <summary>这一帧没出现在活着的名单里的, 全部忘掉 (见 ZombieBrain.ForgetMissing)。</summary>
    public void ForgetMissing(ISet<(int Index, int Uses)> alive)
    {
        foreach ((int, int) key in _states.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _states.Remove(key);
        }
    }

    /// <summary>这只僵尸没了 / 交出去了 —— 忘掉它的路径。</summary>
    public void Forget(NPC npc)
    {
        if (npc != null)
        {
            _states.Remove((npc.IndexGlobal, npc.NumberOfGlobalCacheUses));
        }
    }

    /// <summary>
    /// 从 <paramref name="from"/> 到 <paramref name="to"/> 有没有静态几何挡着: 中线加左右各偏一个体宽,
    /// 任何一条被挡就算挡 (只测中线的话, 拉线会把拐角切掉, 身子卡在墙角)。
    /// </summary>
    private static bool Blocked(GameManager simulation, Vector2 from, Vector2 to, float radius)
    {
        var geometry = simulation.CollisionManager?._0023c8;
        if (geometry == null)
        {
            return false;
        }

        if (Ray(geometry, from, to))
        {
            return true;
        }

        Vector2 along = to - from;
        if (along == Vector2.Zero || radius <= 0f)
        {
            return false;
        }

        along.Normalize();
        Vector2 side = new Vector2(-along.Y, along.X) * radius;
        return Ray(geometry, from + side, to + side) || Ray(geometry, from - side, to - side);
    }

    private static bool Ray(_00236._0023Cc geometry, Vector2 from, Vector2 to) =>
        geometry._00233b(from, to, MovementFlags, _00237b: false, _00238b: false);

    /// <summary>
    /// 挡**走路**的几何 = 原厂移动碰撞用的那组 (-6/-Oc.cs:68 等: 1 | 4 | 0x20)。
    ///
    /// 以前用的是视线那组 (1 | 4), 少了 0x20 —— 那类东西 (矮墙/围栏一类, 不挡视线) 射线看不见,
    /// 于是判成"直线没挡", 僵尸直追顶在上面不动。玩家实测 (2026-09-14): "夜袭时刷的远的怪物会站在原地不动"。
    /// </summary>
    private const GeometryCollisionFlags MovementFlags =
        GeometryCollisionFlags._00237jd | GeometryCollisionFlags._00239jd | GeometryCollisionFlags._0023ckd;

    private NavMeshPathfinder PathfinderFor(GameManager simulation)
    {
        int map = simulation.MapIndex;
        if (_pathfinderMap == map)
        {
            return _pathfinder;
        }

        ConductorGameLogic.Pathfinding.NavMesh mesh = MapManager.GetNavMesh(map);
        _pathfinder = mesh?.Triangles == null || mesh.Triangles.Length == 0 ? null : new NavMeshPathfinder(mesh);
        _pathfinderMap = map;
        return _pathfinder;
    }
}
