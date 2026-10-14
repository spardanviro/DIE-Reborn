using System;
using System.Collections.Generic;
using System.Linq;
using _0023z;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 拾荒的囤积者 (Hoarder) 事件: 定时在地图的动态事件点放一只囤积者 Boss, 打死喷一大把补给。
///
/// ## 原厂的部分
///
///   - 事件点: <c>Event_DynamicEvent</c> (AllowHoardeSpawn), 五张图 1~3 个, 名字就是 "Hoarderspawn.WaterPark" 这种
///   - 状态: 模式数据 <c>_0023ftb</c> 列表 (id / 类型 / 位置) —— 类型 1 = 囤积者, 0 = 其它动态事件 (小地图图标不同);
///     列表里类型 1 的个数变多, 客户端加目标 "KillHoarder" 并播 Sinamoi "HoarderArrive" (ScavengerHunt._0023avb);
///     变少则完成目标, 按 <c>_0023btb</c> (打死它的队) 是不是自己队播 "HoarderDownTeam" / "HoarderDownEnemy"
///   - 掉落: 囤积者 (非 Looter) 死亡时共享代码自己抛 <c>BalanceData._0023j7f._0023Qtb</c> = 150 补给 (Character.cs:2911)
///   - 三种囤积者: Puller (_0023dPb) / Butcher (_0023sPb) / Floater (_0023qPb), 拾荒对象池各 3 个
///
/// ## 我们定的
///
///   第一个在第一支队冲出出生区 <see cref="FirstDelaySeconds"/> 秒后出现; 死了隔 <see cref="NextDelaySeconds"/> 秒出下一个;
///   同时最多一个; 点位和种类随机 (不连着用同一个点)。
/// </summary>
public sealed class ScavengerHoarders
{
    public float FirstDelaySeconds { get; set; } = 180f;

    public float NextDelaySeconds { get; set; } = 240f;

    /// <summary>三种囤积者 Boss 的类名 (GameObjectBase.IsPullerHoarder / IsButcherHoarder / IsFloaterHoarder)。</summary>
    public static readonly string[] HoarderClasses = { "_0023dPb", "_0023sPb", "_0023qPb" };

    private readonly List<(int Id, Vector2 Position)> _points = new List<(int, Vector2)>();
    private readonly Random _random;
    private float _timer = -1f;
    private int _lastPoint = -1;
    private Character _boss;
    private int _bossUses;
    private int _bossId;

    public ScavengerHoarders(Random random = null) => _random = random ?? new Random();

    public int ArmedPoints => _points.Count;

    public Character Boss => _boss;

    /// <summary>这个点被别的事件占着 (宿主接 Looter 事件的占用)。</summary>
    public Func<int, bool> IsPointBusy { get; set; }

    /// <summary>囤积者现在占着的点 (<c>Event_DynamicEvent.ID</c>); 没有返回 -1。Looter 事件避开它。</summary>
    public int OccupiedPointId => _boss != null ? _bossId : -1;

    /// <summary>地图上允许刷囤积者的动态事件点 (ID, 位置)。</summary>
    public IReadOnlyList<(int Id, Vector2 Position)> Points => _points;

    public int Killed { get; private set; }

    public readonly List<string> Events = new List<string>();

    /// <summary>刚刷出来的 (宿主不激怒它 —— 囤积者守着点, 等人来)。</summary>
    public void Scan(GameManager simulation)
    {
        _points.Clear();
        foreach (Event_DynamicEvent point in simulation?.MapObjects?.OfType<Event_DynamicEvent>() ?? Enumerable.Empty<Event_DynamicEvent>())
        {
            if (point.AllowHoardeSpawn)
            {
                _points.Add((point.ID, point.Position));
            }
        }
    }

    /// <param name="anyTeamOut">有队已经冲出出生区 (计时从这一刻开始)。</param>
    public void Update(GameManagerServer simulation, ScavengerHunt hunt, bool anyTeamOut, float deltaTime)
    {
        Events.Clear();
        if (_points.Count == 0 || hunt.IsCompleted)
        {
            return;
        }

        if (_boss != null)
        {
            WatchBoss(simulation, hunt);
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
            SpawnBoss(simulation, hunt);
        }
    }

    /// <summary>立刻刷一只 (测试 / 调试)。</summary>
    public bool SpawnNow(GameManagerServer simulation, ScavengerHunt hunt) => _boss == null && SpawnBoss(simulation, hunt);

    private bool SpawnBoss(GameManagerServer simulation, ScavengerHunt hunt)
    {
        // 不连着用同一个点; 别的事件 (Looter) 占着的点也先不用 —— 实在没得挑再说
        List<int> free = Enumerable.Range(0, _points.Count)
            .Where(i => IsPointBusy == null || !IsPointBusy(_points[i].Id))
            .ToList();
        if (free.Count == 0)
        {
            _timer = 10f;
            return false;
        }

        List<int> fresh = free.Where(i => i != _lastPoint).ToList();
        int index = fresh.Count > 0 ? fresh[_random.Next(fresh.Count)] : free[_random.Next(free.Count)];

        (int id, Vector2 position) = _points[index];
        Type type = CrossroadEventCatalog.ResolveClass(HoarderClasses[_random.Next(HoarderClasses.Length)]);
        Character boss = ZombieFactory.Spawn(simulation, type, position, _0023Oq._0023WM._0023Yc, Vector2.UnitX);
        if (boss == null || !boss.IsActive)
        {
            _timer = 10f;   // 池子空 / 点被占, 过一会儿再试
            Events.Add($"拾荒囤积者没刷出来 ({type?.Name} @ 点 {id}), 10 秒后再试");
            return false;
        }

        _boss = boss;
        _bossUses = boss.NumberOfGlobalCacheUses;
        _bossId = id;
        _lastPoint = index;
        hunt._0023l9._0023ftb.Add(new _0023Zsb { _0023kg = id, _0023Eg = 1, _0023B = position });
        Events.Add($"拾荒囤积者出现: {boss.GetType().Name} @ 点 {id} ({position.X:F0},{position.Y:F0}) 血 {boss.MaxHealth:F0}");
        return true;
    }

    private void WatchBoss(GameManagerServer simulation, ScavengerHunt hunt)
    {
        bool gone = !_boss.IsActive || _boss.IsDead || _boss.NumberOfGlobalCacheUses != _bossUses;
        if (!gone)
        {
            return;
        }

        Player killer = simulation.KillerOf(_boss, _bossUses) ?? simulation.LastPlayerToHit(_boss, _bossUses);
        hunt._0023l9._0023btb = killer?.TeamId ?? TeamId.Neutral;
        hunt._0023l9._0023ftb.RemoveAll(e => e._0023kg == _bossId && e._0023Eg == 1);
        Killed++;
        Events.Add($"拾荒囤积者倒下 (点 {_bossId}, 最后一击 {hunt._0023l9._0023btb}), {NextDelaySeconds:F0} 秒后出下一只");

        _boss = null;
        _timer = NextDelaySeconds;
    }
}
