using System;
using System.Collections.Generic;
using ConductorGameLogic.Pathfinding;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 在原厂导航网格上做 A* 寻路。
///
/// ## 数据一直都在, 我先前判断错了
///
/// 我看到 <c>ChunkData : MonoBehaviour</c> 把 NavMesh 存成 base64 字符串,
/// 就下结论"导航数据在 Unity 场景资源里, 服务端不跑 Unity 拿不到",
/// 于是只做了局部避障。**那是错的** —— ChunkData 只是 Unity 编辑器侧的
/// 存储壳 (烘焙时往里塞), 运行时的数据在磁盘上:
///
///     Maps/Tutorial_01_Prologue.snm     899 个三角形, 1764 条边
///
/// <c>MapManager</c> 加载它, <c>MapManager.GetNavMesh(mapIndex)</c> 是 public。
/// 教训: "某个类把数据存成了 X" 不等于 "数据只能从 X 拿"。
///
/// ## 网格的结构 (实测确认)
///
///   NavMeshTriangle 的三个 int = **三条边的索引**
///       前三个三角形是 0,1,2 | 3,4,5 | 6,7,8 —— 每个三角形三条边, 顺序排列
///
///   NavMeshEdge._0023Sdc / _0023Tdc = **相邻的两个三角形索引**
///       -1 表示这条边是地图边界 (教程图有 831 条)
///
///   NavMeshEdge._0023Qdc / _0023Rdc = 两个顶点, _0023Bd = 中点
///
/// 也就是一张现成的三角形邻接图, A* 直接能跑。
///
/// ## 只做到"路径点序列", 不做漏斗平滑
///
/// 路径点取**共享边的中点**。标准做法是再跑一遍漏斗算法 (funnel) 把折线
/// 拉直, 但那对"跟着玩家走"这个用途收益不大 —— NPC 本来就在动, 路径每隔
/// 一会儿就重算。先要能绕过障碍, 平不平滑是后话。
/// </summary>
public sealed class NavMeshPathfinder
{
    private readonly NavMesh _mesh;

    /// <summary>每个三角形的三个顶点。三条边的顶点去重得来, 预计算一次。</summary>
    private readonly Vector2[][] _corners;

    /// <summary>每个三角形的重心, A* 的代价和启发式都用它。</summary>
    private readonly Vector2[] _centers;

    public NavMeshPathfinder(NavMesh mesh)
    {
        _mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));

        int count = mesh.Triangles.Length;
        _corners = new Vector2[count][];
        _centers = new Vector2[count];

        for (int i = 0; i < count; i++)
        {
            _corners[i] = CornersOf(mesh, i);
            _centers[i] = (_corners[i][0] + _corners[i][1] + _corners[i][2]) / 3f;
        }
    }

    /// <summary>三角形数, 诊断用。</summary>
    public int TriangleCount => _mesh.Triangles.Length;

    /// <summary>
    /// 三角形的三个顶点。
    ///
    /// 三条边一共给出 6 个顶点, 每个角被两条边共用 —— 去重就剩 3 个。
    /// 用坐标相等去重: 这些顶点是**同一份数据复制出来的**, 没有经过运算,
    /// 浮点相等在这里是安全的 (和 <c>AmbushDirector</c> 按位置认路障同理)。
    /// </summary>
    private static Vector2[] CornersOf(NavMesh mesh, int triangle)
    {
        NavMeshTriangle t = mesh.Triangles[triangle];
        Vector2[] found = new Vector2[3];
        int n = 0;

        foreach (int edgeIndex in new[] { t._0023s5b, t._0023t5b, t._0023Ndc })
        {
            NavMeshEdge edge = mesh.Edges[edgeIndex];

            foreach (Vector2 v in new[] { edge._0023Qdc, edge._0023Rdc })
            {
                bool seen = false;
                for (int i = 0; i < n; i++)
                {
                    if (found[i] == v)
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen && n < 3)
                {
                    found[n++] = v;
                }
            }
        }

        // 退化三角形 (数据里有重合顶点) —— 补齐避免越界, 它自然会被 A* 绕开
        while (n < 3)
        {
            found[n] = found[n == 0 ? 0 : n - 1];
            n++;
        }

        return found;
    }

    /// <summary>点落在哪个三角形里。不在网格上返回 -1。</summary>
    public int TriangleAt(Vector2 point)
    {
        for (int i = 0; i < _corners.Length; i++)
        {
            if (Contains(_corners[i], point))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 离这个点最近的三角形。
    ///
    /// 站在网格外时 (贴着墙、站在栈道上) <see cref="TriangleAt"/> 会返回 -1,
    /// 那时用最近的三角形兜底, 否则一旦踏出网格就再也算不出路径。
    /// </summary>
    public int NearestTriangle(Vector2 point)
    {
        int inside = TriangleAt(point);
        if (inside >= 0)
        {
            return inside;
        }

        int best = -1;
        float bestDistanceSquared = float.MaxValue;

        for (int i = 0; i < _centers.Length; i++)
        {
            float d = Vector2.DistanceSquared(_centers[i], point);
            if (d < bestDistanceSquared)
            {
                bestDistanceSquared = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// 把一个点吸附到可行走面上。
    ///
    /// ## 为什么需要它
    ///
    /// 剧情 NPC 的出生点是**从地图标记算出来的**, 不保证落在导航网格上 ——
    /// Boss 取的是 <c>Event_RaidEvent.Position</c>, Ram 取的是"门中心往外推
    /// 一段"。玩家实测 "Boss Looter 无法移动, 虽然显示在走动, 但是位置
    /// 停在原地" 就是这么来的: 动画在播 (客户端只看状态), 而寻路那边
    /// 起点就不在网格上, 一步都迈不出去。
    ///
    /// 点已经在网格上就原样返回; 不在就退到最近三角形的重心 ——
    /// 重心必然在网格内, 不像顶点可能压在边界上。
    /// </summary>
    public Vector2 SnapToWalkable(Vector2 point)
    {
        if (TriangleAt(point) >= 0)
        {
            return point;
        }

        int nearest = NearestTriangle(point);
        return nearest >= 0 ? _centers[nearest] : point;
    }

    /// <summary>这个点在可行走面上吗。诊断用。</summary>
    public bool IsWalkable(Vector2 point) => TriangleAt(point) >= 0;

    private static bool Contains(Vector2[] corners, Vector2 p)
    {
        // 重心坐标的符号判定。三个叉积同号 = 在里面。
        float d1 = Cross(p, corners[0], corners[1]);
        float d2 = Cross(p, corners[1], corners[2]);
        float d3 = Cross(p, corners[2], corners[0]);

        bool negative = d1 < 0f || d2 < 0f || d3 < 0f;
        bool positive = d1 > 0f || d2 > 0f || d3 > 0f;

        return !(negative && positive);
    }

    private static float Cross(Vector2 p, Vector2 a, Vector2 b) =>
        (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);

    /// <summary>
    /// 从 <paramref name="from"/> 走到 <paramref name="to"/> 的路径点。
    ///
    /// 返回的是**中途拐点**, 不含起点; 终点用 <paramref name="to"/> 本身
    /// (而不是终点三角形的中心), 免得最后一段拐去中心再折回来。
    ///
    /// 两点在同一个三角形里时返回只含终点的一段 —— 直着走就行。
    /// 找不到路 (网格不连通) 返回 null, 调用方自己决定退回什么行为。
    /// </summary>
    public List<Vector2> FindPath(Vector2 from, Vector2 to, int maxExpansions = 4000)
    {
        int start = NearestTriangle(from);
        int goal = NearestTriangle(to);

        if (start < 0 || goal < 0)
        {
            return null;
        }

        if (start == goal)
        {
            return new List<Vector2> { to };
        }

        int count = _mesh.Triangles.Length;
        float[] gScore = new float[count];
        int[] cameFrom = new int[count];
        int[] viaEdge = new int[count];
        bool[] closed = new bool[count];

        for (int i = 0; i < count; i++)
        {
            gScore[i] = float.MaxValue;
            cameFrom[i] = -1;
            viaEdge[i] = -1;
        }

        gScore[start] = 0f;

        // 小规模图 (899 个三角形), 用简单的优先队列就够
        PriorityQueue<int, float> open = new PriorityQueue<int, float>();
        open.Enqueue(start, Vector2.Distance(_centers[start], to));

        int expansions = 0;

        while (open.Count > 0)
        {
            int current = open.Dequeue();

            if (current == goal)
            {
                return Reconstruct(cameFrom, viaEdge, start, goal, to);
            }

            if (closed[current])
            {
                continue;
            }

            closed[current] = true;

            // 上限是防呆 —— 网格坏掉或者目标不可达时别把一帧卡死
            if (++expansions > maxExpansions)
            {
                return null;
            }

            NavMeshTriangle t = _mesh.Triangles[current];
            foreach (int edgeIndex in new[] { t._0023s5b, t._0023t5b, t._0023Ndc })
            {
                NavMeshEdge edge = _mesh.Edges[edgeIndex];

                // 边的另一侧。-1 是地图边界, 走不过去。
                int neighbour = edge._0023Sdc == current ? edge._0023Tdc : edge._0023Sdc;
                if (neighbour < 0 || neighbour >= count || closed[neighbour])
                {
                    continue;
                }

                float tentative = gScore[current]
                                  + Vector2.Distance(_centers[current], _centers[neighbour]);

                if (tentative >= gScore[neighbour])
                {
                    continue;
                }

                gScore[neighbour] = tentative;
                cameFrom[neighbour] = current;
                viaEdge[neighbour] = edgeIndex;

                open.Enqueue(neighbour, tentative + Vector2.Distance(_centers[neighbour], to));
            }
        }

        return null;
    }

    private List<Vector2> Reconstruct(int[] cameFrom, int[] viaEdge, int start, int goal, Vector2 to)
    {
        List<Vector2> reversed = new List<Vector2> { to };

        int current = goal;
        while (current != start && cameFrom[current] >= 0)
        {
            int edgeIndex = viaEdge[current];
            if (edgeIndex >= 0)
            {
                // 拐点取共享边的中点 —— 那是从一个三角形进入下一个的必经之处
                reversed.Add(_mesh.Edges[edgeIndex]._0023Bd);
            }

            current = cameFrom[current];
        }

        reversed.Reverse();
        return reversed;
    }
}
