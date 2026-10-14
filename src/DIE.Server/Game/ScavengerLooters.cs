using System;
using System.Collections.Generic;
using System.Linq;
using _0023z;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 拾荒的 Looter (小型囤积者) 事件: 一小群 Looter 占住一个动态事件点, 打光了给补给和计分。
///
/// ## 原厂留下的
///
///   - Looter 类 <c>_0023nPb</c> (模型名 "MiniHoarder", 分类 Hoarder | Looter), 拾荒对象池 10 个, 客户端模型缓存 3 个;
///     死了共享规则掉 <c>_0023Ntb</c> = 3 补给 (Supplies._0023Xj), 不走囤积者那 150 的一大把 (Character.cs:2911 排除了 Looter)
///   - 计分板有"击杀 Looter"一栏 (<see cref="ScavengerScores.LooterKill"/>)
///   - 模式数据动态事件列表 <c>_0023ftb</c> 里**类型 0** 的条目: 变多时客户端播 Sinamoi "EliteSpawn",
///     变少时按 <c>_0023btb</c> (打掉它的队) 播语音 (ScavengerHunt._0023avb); 列表最多 2 条 (序列化上界)
///
/// 类型 0 = Looter 事件是**推断** (同样是 Hoarder 分类、同样占事件点、同样有"谁打掉的"语音), 服务端的刷新规则随服务端丢了。
///
/// ## 我们定的 (节奏)
///
///   第一队冲出出生区 <see cref="FirstDelaySeconds"/> 秒后第一波 —— 比囤积者 (180 秒) 早, 给刚冲出去的队一个近一点的中期目标;
///   一波 <see cref="PackSize"/> 只 (第一版 3 只一群, 玩家实测 2026-09-17 要一次一只); 打光了 <see cref="NextDelaySeconds"/> 秒后下一波;
///   同时最多一波; 不和囤积者抢同一个点; 刷出来守着点, 不主动冲人 (同囤积者)。
/// </summary>
public sealed class ScavengerLooters
{
    public float FirstDelaySeconds { get; set; } = 90f;

    public float NextDelaySeconds { get; set; } = 150f;

    public int PackSize { get; set; } = 1;

    /// <summary>
    /// 每只 Looter 被玩家打死一共掉多少补给。**玩家定的** (2026-09-23): "击败 looter 只掉 3 点物资",
    /// 定为 50 = 补给点奖励补给的上限 (<see cref="ScavengerStorageRooms"/> 的 MaxBonus)。
    ///
    /// 原厂那 3 个 (<c>BalanceData._0023j7f._0023Ntb</c>, Supplies._0023Xj) 照常由共享代码掉;
    /// 这个数和精英怪共用, 改它会连精英怪一起改, 所以不动它, 差额由这里补 (<see cref="ThrowExtra"/>)。
    /// </summary>
    public int SuppliesPerLooter { get; set; } = 50;

    /// <summary>Looter 的类名 (GameObjectBase.IsLooter)。</summary>
    public const string LooterClass = "_0023nPb";

    private readonly Random _random;
    private readonly List<(Character Zombie, int Uses)> _pack = new List<(Character, int)>();
    private float _timer = -1f;
    private int _pointId = -1;
    private int _lastPointId = -1;

    public ScavengerLooters(Random random = null) => _random = random ?? new Random();

    public readonly List<string> Events = new List<string>();

    /// <summary>打光了几波。</summary>
    public int Cleared { get; private set; }

    /// <summary>现在这一波还活着的。</summary>
    public IReadOnlyList<Character> Alive => _pack.Where(p => !Gone(p)).Select(p => p.Zombie).ToList();

    /// <summary>这一波占着的点; 没有返回 -1。</summary>
    public int OccupiedPointId => _pack.Count > 0 ? _pointId : -1;

    /// <param name="points">动态事件点 (和囤积者同一批)。</param>
    /// <param name="busyPoint">囤积者占着的点, 没有 -1。</param>
    /// <param name="anyTeamOut">有队已经冲出出生区 (计时从这一刻开始)。</param>
    public void Update(GameManagerServer simulation, ScavengerHunt hunt, IReadOnlyList<(int Id, Vector2 Position)> points,
                       int busyPoint, bool anyTeamOut, float deltaTime)
    {
        Events.Clear();
        if (points == null || points.Count == 0 || hunt.IsCompleted)
        {
            return;
        }

        if (_pack.Count > 0)
        {
            WatchPack(simulation, hunt);
            return;
        }

        if (_timer < 0f)
        {
            if (!anyTeamOut)
            {
                return;
            }

            _timer = FirstDelaySeconds;
        }

        _timer -= deltaTime;
        if (_timer <= 0f)
        {
            SpawnPack(simulation, hunt, points, busyPoint);
        }
    }

    /// <summary>立刻刷一波 (测试 / 调试)。</summary>
    public bool SpawnNow(GameManagerServer simulation, ScavengerHunt hunt, IReadOnlyList<(int Id, Vector2 Position)> points, int busyPoint = -1) =>
        _pack.Count == 0 && SpawnPack(simulation, hunt, points, busyPoint);

    private bool SpawnPack(GameManagerServer simulation, ScavengerHunt hunt, IReadOnlyList<(int Id, Vector2 Position)> points, int busyPoint)
    {
        List<(int Id, Vector2 Position)> free = points.Where(p => p.Id != busyPoint).ToList();
        List<(int Id, Vector2 Position)> fresh = free.Where(p => p.Id != _lastPointId).ToList();
        if (free.Count == 0 || hunt._0023l9._0023ftb.Count >= 2)
        {
            _timer = 10f;   // 唯一的点被囤积者占着 / 事件列表满了, 过一会儿再试
            return false;
        }

        (int id, Vector2 position) = fresh.Count > 0 ? fresh[_random.Next(fresh.Count)] : free[_random.Next(free.Count)];
        Type type = CrossroadEventCatalog.ResolveClass(LooterClass);
        for (int i = 0; i < PackSize; i++)
        {
            Character looter = ZombieFactory.Spawn(simulation, type, RoamingZombieSpawner.Scatter(simulation, position),
                _0023Oq._0023WM._0023eCc, Vector2.Zero);   // 从地里刨出来 (Looter 会的出场动画之一)
            if (looter != null && looter.IsActive)
            {
                _pack.Add((looter, looter.NumberOfGlobalCacheUses));
            }
        }

        if (_pack.Count == 0)
        {
            _timer = 10f;
            Events.Add($"拾荒 Looter 没刷出来 (点 {id}), 10 秒后再试");
            return false;
        }

        _pointId = id;
        _lastPointId = id;
        hunt._0023l9._0023ftb.Add(new _0023Zsb { _0023kg = id, _0023Eg = 0, _0023B = position });
        Events.Add($"拾荒 Looter 出现: {_pack.Count} 只 @ 点 {id} ({position.X:F0},{position.Y:F0})");
        return true;
    }

    /// <summary>
    /// 补上 <see cref="SuppliesPerLooter"/> 和原厂那 3 个的差额。按 10 个一包 (同补给点奖励),
    /// 零头单独一包, 凑出来正好是差额。
    /// </summary>
    private void ThrowExtra(GameManager simulation, Vector2 at)
    {
        int original = GameManager.BalanceData?._0023j7f?._0023Ntb ?? 3;
        int extra = SuppliesPerLooter - original;
        if (extra <= 0)
        {
            return;
        }

        const int perCrate = SupplyThrower.DefaultSuppliesPerCrate;
        if (extra / perCrate > 0)
        {
            SupplyThrower.Throw(simulation, at, extra / perCrate, perCrate, SupplyThrower.DefaultScatter);
        }

        if (extra % perCrate > 0)
        {
            SupplyThrower.Throw(simulation, at, 1, extra % perCrate, SupplyThrower.DefaultScatter);
        }

        Events.Add($"拾荒 Looter 掉落: 原厂 {original} + 补 {extra} = {SuppliesPerLooter}");
    }

    private static bool Gone((Character Zombie, int Uses) entry) =>
        !entry.Zombie.IsActive || entry.Zombie.IsDead || entry.Zombie.NumberOfGlobalCacheUses != entry.Uses;

    private void WatchPack(GameManagerServer simulation, ScavengerHunt hunt)
    {
        TeamId lastKiller = TeamId.Neutral;
        for (int i = _pack.Count - 1; i >= 0; i--)
        {
            if (!Gone(_pack[i]))
            {
                continue;
            }

            Player killer = simulation.KillerOf(_pack[i].Zombie, _pack[i].Uses);
            if (killer != null)
            {
                lastKiller = killer.TeamId;

                // 原厂只在"不是炮塔打死的"时掉 (Supplies._0023Xj 判 Owner.IsSpawnTurret);
                // KillerOf 返回的是玩家, 条件一致
                ThrowExtra(simulation, _pack[i].Zombie.Position);
            }

            _pack.RemoveAt(i);
            if (_pack.Count == 0)
            {
                break;
            }
        }

        if (_pack.Count > 0)
        {
            return;
        }

        hunt._0023l9._0023btb = lastKiller;
        hunt._0023l9._0023ftb.RemoveAll(e => e._0023kg == _pointId && e._0023Eg == 0);
        Cleared++;
        Events.Add($"拾荒 Looter 清光 (点 {_pointId}, 最后一击 {lastKiller}), {NextDelaySeconds:F0} 秒后下一波");
        _pointId = -1;
        _timer = NextDelaySeconds;
    }
}
