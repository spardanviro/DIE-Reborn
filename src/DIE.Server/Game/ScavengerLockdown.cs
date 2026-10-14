using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 拾荒的基地封锁 ("LockDown Gauntlet"): 入夜那一刻把各队基地入口封上, 顶住自家夜袭再开门。
///
/// ## 原厂留下的
///
///   - 地图上每队卡车旁 130~160 单位有一道 <c>LockDownGauntletBlockage</c> (Resort / Jungle / Expedition 各三道, 组 101..103,
///     标 <c>DynamiclySpawned</c> = 开局不在场); Town / Village 没有
///   - 这种路障**只挡玩家不挡僵尸** (Entity.IgnoreCollision, Entity.cs:358 / :1306), 有专门的淡入特效 Vfx_Blockage_Gauntlet_FadeIn_01
///   - 摆法照原厂 <c>HordeSpawner.SpawnBlockage</c>: 打不破 (<c>_0023Txc</c>) + 血量 float.MaxValue
///
/// 什么时候放、什么时候收, 服务端丢了。
///
/// ## 我们的设计
///
/// 入夜 = 卡车线以上的补给开始可以被偷, 同时各队基地挨夜袭。封锁在这一刻给每队一段"关门守家"的窗口:
/// 僵尸照样涌进来, 别队的人进不来偷, 自己人也出不去 —— 名字里的 Gauntlet 就是这个。
/// 这队的夜袭打光 (至少封 <see cref="MinSeconds"/> 秒, 等夜袭刷出来) 或者封满 <see cref="MaxSeconds"/> 秒就开门,
/// 不会把在外面的人 (比如扛着补给回家的) 挡太久。只封有人的队; 没有这道门的图不封。
/// </summary>
public sealed class ScavengerLockdown
{
    public float MinSeconds { get; set; } = 8f;

    public float MaxSeconds { get; set; } = 30f;

    /// <summary>
    /// 地图没有 <c>Trigger_Barricade</c> 时, 门离卡车多远以内算这队的 (五张图实测 130~160)。
    /// Resort / Jungle / Expedition 有 <c>Trigger_Barricade</c>, 走不到这一步。
    /// </summary>
    public const float GateRadius = 250f;

    private sealed class Gate
    {
        public TeamId Team;
        public int GroupId;
        public readonly List<Spawn_Destructible> Pieces = new List<Spawn_Destructible>();
        public readonly List<(_0023EK._0023Yh Piece, int Uses)> Spawned = new List<(_0023EK._0023Yh, int)>();
        public float Elapsed;
        public bool Engaged;
    }

    private readonly List<Gate> _gates = new List<Gate>();

    public readonly List<string> Events = new List<string>();

    /// <summary>地图上认出来的门 (队伍 -&gt; 组号)。</summary>
    public IReadOnlyDictionary<TeamId, int> Gates => _gates.ToDictionary(g => g.Team, g => g.GroupId);

    /// <summary>这队的门现在关着吗。</summary>
    public bool IsLocked(TeamId team) => _gates.Any(g => g.Team == team && g.Engaged);

    /// <summary>
    /// 认门。
    ///
    /// 哪些组是门: 优先认原厂的 <c>Trigger_Barricade.BarricadeID</c> —— 它就是为这批门准备的
    /// 区域标记, 客户端一处不读 (= 服务端用的), Resort / Jungle / Expedition 各 3 个,
    /// <c>BarricadeID</c> 正好是 LockDownGauntletBlockage 的组号 101/102/103。
    /// Town / Village 没摆这个标记, 退回按 <see cref="GateRadius"/> 认。
    ///
    /// 门归哪一队: 两条路都是"离哪辆卡车最近" —— <c>Trigger_Barricade</c> 本身不带队伍。
    /// </summary>
    public void Scan(GameManager simulation, IReadOnlyList<(TeamId Team, Vector2 Position)> trucks)
    {
        _gates.Clear();
        if (simulation?.MapObjects == null || trucks == null || trucks.Count == 0)
        {
            return;
        }

        HashSet<int> marked = simulation.MapObjects.OfType<Trigger_Barricade>()
            .Select(t => t.BarricadeID).ToHashSet();

        foreach (IGrouping<int, Spawn_Destructible> group in simulation.MapObjects.OfType<Spawn_Destructible>()
                     .Where(d => d.BarricadeType == BarricadeType.LockDownGauntletBlockage)
                     .GroupBy(d => d.GroupID))
        {
            Vector2 center = new Vector2(group.Average(d => d.Position.X), group.Average(d => d.Position.Y));
            (TeamId team, Vector2 truck) = trucks.OrderBy(t => Vector2.DistanceSquared(t.Position, center)).First();

            // 原厂标记过的组直接认; 没标记的图 (Town / Village) 才退回距离判定
            bool isGate = marked.Contains(group.Key) || Vector2.Distance(truck, center) <= GateRadius;
            if (!isGate || _gates.Any(g => g.Team == team))
            {
                continue;
            }

            Gate gate = new Gate { Team = team, GroupId = group.Key };
            gate.Pieces.AddRange(group);
            _gates.Add(gate);
        }
    }

    /// <summary>入夜: 把这些队的门关上。</summary>
    public void Engage(GameManager simulation, IEnumerable<TeamId> teams)
    {
        foreach (Gate gate in _gates.Where(g => !g.Engaged && teams.Contains(g.Team)))
        {
            foreach (Spawn_Destructible spawn in gate.Pieces)
            {
                _0023EK._0023Yh piece = simulation.GetGameObjectFromPool<_0023EK._0023Yh>();
                if (piece == null || piece.IsDummyObject)
                {
                    continue;
                }

                piece.Position = spawn.Position;
                piece._0023Gt = spawn.Direction;
                piece._00238Zb = spawn.Part;
                piece._00239Zb = BarricadeType.LockDownGauntletBlockage;
                simulation.SpawnGameObject(piece, null, null);
                piece._0023Txc();
                piece._0023DZb(float.MaxValue);
                if (piece.IsActive)
                {
                    gate.Spawned.Add((piece, piece.NumberOfGlobalCacheUses));
                }
            }

            gate.Engaged = true;
            gate.Elapsed = 0f;
            Events.Add($"拾荒 {gate.Team} 基地封锁 (组 {gate.GroupId}, {gate.Spawned.Count} 段)");
        }
    }

    /// <param name="assaultRemaining">这队基地夜袭还剩几只没打完。</param>
    public void Update(float deltaTime, Func<TeamId, int> assaultRemaining)
    {
        Events.Clear();
        foreach (Gate gate in _gates.Where(g => g.Engaged))
        {
            gate.Elapsed += deltaTime;
            bool cleared = gate.Elapsed >= MinSeconds && (assaultRemaining?.Invoke(gate.Team) ?? 0) == 0;
            if (cleared || gate.Elapsed >= MaxSeconds)
            {
                Release(gate, cleared ? "夜袭打光" : $"封满 {MaxSeconds:F0} 秒");
            }
        }
    }

    /// <summary>全部开门 (一局结束)。</summary>
    public void ReleaseAll()
    {
        foreach (Gate gate in _gates.Where(g => g.Engaged))
        {
            Release(gate, "一局结束");
        }
    }

    private void Release(Gate gate, string reason)
    {
        foreach ((_0023EK._0023Yh piece, int uses) in gate.Spawned)
        {
            if (piece.IsActive && piece.NumberOfGlobalCacheUses == uses)
            {
                piece.Destroy();
            }
        }

        gate.Spawned.Clear();
        gate.Engaged = false;
        Events.Add($"拾荒 {gate.Team} 基地开门 ({reason}, 封了 {gate.Elapsed:F0} 秒)");
    }
}
