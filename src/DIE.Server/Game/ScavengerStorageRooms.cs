using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Entities.GameModeObjects;
using ConductorGameLogic.GameModes;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 拾荒的补给点 (StorageRoom): 读条占领、持有收入、积攒的奖励补给。
///
/// ## 原厂的部分
///
///   - 按 X 读条 -&gt; <c>GameMode.TriggerCapturePerformed</c> -&gt; <c>StorageRoom.PerformedTriggerCapture = 队</c>
///     (谁能读条: 不是持有者、也不是正在接管的队 —— GameMode.cs:960)
///   - HUD 读模式状态里的五个数组 (<c>ScavengerHunt._0023tvb</c> -&gt; <c>StorageRoomCaptureGUIScript.SetGUI</c>):
///       _0023jtb 持有队 (名字颜色、小地图图标颜色、己方视野 175)
///       _0023ktb 正在占领的队 (Neutral = 进度条整个隐藏; 非 Neutral = 小地图上闪)
///       _0023gtb 进度 0..1
///       _0023htb 速度 (100 = x1; &lt;= 0 显示红色 "Contested")
///       _0023itb 奖励补给 (0..150, 显示 "N Bonus Supplies")
///   - 计分板的"补给点数" = 持有且没被别人占领中的点 (<c>_0023uvb</c>)
///   - 任务目标 "CaptureIncome" (冲出出生区后出现)
///
/// ## 我们定的 (原厂的数值和收入规则随服务端丢失)
///
///   占领: 独占 <see cref="SecondsToCapture"/> 秒, 每多一个队友 +50% (同教程, 玩家对原版的实测规则);
///         时长原厂没有数据 (客户端只显示百分比和倍率)。第一版 20 秒, 玩家实测 (2026-09-17) "占领速度太快", 选单人 60 秒。
///         圈里有别队的活玩家 = 争夺, 进度不动; 占领方全离开圈 = 进度按原速倒退, 退到 0 取消。
///   收入: 持有的点每 <see cref="IncomeSeconds"/> 秒直接给持有队的卡车记 <see cref="IncomePerTick"/> (计分板和
///         目标栏上"CaptureIncome"说的就是这个)。
///   奖励: 没人持有的点每 <see cref="BonusSeconds"/> 秒攒 1 (上限 <see cref="MaxBonus"/>), 被占下时喷出来给占领方捡。
///         原厂字段 <c>_0023itb</c> 的序列化上界是 150, 但积攒速度和上限都没有数据。第一版 2 秒 1 / 上限 150,
///         玩家实测 (2026-09-17) "补给点掉的物资太多了" —— 四个点一圈 600, 一个人一局就快交满。改成 4 秒 1 / 上限 50。
///   刷怪: 读条后按进度 0% / 33% / 66% 刷三波 (同教程补给点, 玩家定的节奏), 摆位点是地图上 ID 等于这个补给点的
///         <c>Spawn_EventEntities</c> (五张图每个点 15~20 个, 以前一个没用), 每波用掉三分之一; 刷出来就追占领的人。
/// </summary>
public sealed class ScavengerStorageRooms
{
    public float SecondsToCapture { get; set; } = 60f;

    /// <summary>站多远算在场。原厂 StorageRoom.cs:121 判"谁听得到占领音效"的距离。</summary>
    public float HoldRadius { get; set; } = 100f;

    /// <summary>
    /// 圈里人数对应的速度倍率 —— 走 <see cref="CaptureSpeed"/> 那张**原版表**
    /// (1 / 1.5 / 1.8 / 2.0), 和教程补给点、十字路口任务点同一份。
    /// </summary>
    public static float[] SpeedByHolders => CaptureSpeed.ByHolders;

    public float IncomeSeconds { get; set; } = 10f;

    public int IncomePerTick { get; set; } = 3;

    public float BonusSeconds { get; set; } = 4f;

    /// <summary>奖励补给上限。**我们定的** (<c>_0023itb</c> 序列化上界是 150, -z/-Ctb.cs:228)。</summary>
    public int MaxBonus { get; set; } = 50;

    /// <summary>
    /// 占领时刷怪的节奏 —— 全部取自原厂 <c>Event_SupplySpawn</c> 的字段默认值
    /// (ConductorGameLogic.MapObjects/Event_SupplySpawn.cs)。那个地图对象客户端只拿来画
    /// 小地图图标 (GameManagerClient.cs:2264), 刷怪那半是服务端的, 随服务端丢了 ——
    /// 字段本身还在, 数也就还在。
    ///
    /// 第一版是我们自己定的"进度 0 / 33 / 66% 各刷一波, 每波用掉三分之一的摆位点";
    /// 09-18 审校挖出 <c>Event_SupplySpawn</c> 后改成原厂节奏 (玩家定)。
    ///
    /// 差别: 原厂是**按时间**持续刷 + 场上封顶, 不是按进度刷完三波就停 ——
    /// 占得越久压力越持续, 而不是前半段打完后半段白站。
    /// </summary>
    public float WaveSeconds { get; set; } = 30f;

    /// <summary>一波几只行尸 (<c>AmountWalkerPerWave</c>)。</summary>
    public int WalkersPerWave { get; set; } = 12;

    /// <summary>一波几只特殊 (<c>AmountSpecialPerWave</c>)。</summary>
    public int SpecialsPerWave { get; set; } = 1;

    /// <summary>场上最多几只活行尸 (<c>MaxActiveWalkers</c>)。</summary>
    public int MaxActiveWalkers { get; set; } = 16;

    /// <summary>场上最多几只活特殊 (<c>MaxActiveSpecials</c>)。</summary>
    public int MaxActiveSpecials { get; set; } = 2;

    /// <summary>奖励补给每包多少 (喷出来的包数 = 奖励 / 这个数)。</summary>
    public int SuppliesPerCrate { get; set; } = 10;

    public float CrateScatter { get; set; } = 30f;

    private sealed class Room
    {
        public StorageRoom Entity;
        public TeamId Capturer = TeamId.Neutral;
        public float Percent;
        public int Speed;
        public float Bonus;
        public float IncomeTimer;
        public int WavesFired;
        public float WaveTimer;
        public int NextSpawn;

        /// <summary>这个点刷出来的, 用来数场上还活着几只 (原厂的 MaxActive*)。</summary>
        public readonly List<(Character Zombie, bool Special)> Live = new List<(Character, bool)>();
        public readonly List<ConductorGameLogic.MapObjects.Spawn_EventEntities> Spawns =
            new List<ConductorGameLogic.MapObjects.Spawn_EventEntities>();
    }

    private readonly Dictionary<int, List<ConductorGameLogic.MapObjects.Spawn_EventEntities>> _spawnsById =
        new Dictionary<int, List<ConductorGameLogic.MapObjects.Spawn_EventEntities>>();

    /// <summary>本 tick 刷出来的 (宿主拿去激怒, 追正在占领的人)。</summary>
    public readonly List<Character> SpawnedThisTickList = new List<Character>();

    private readonly Dictionary<int, Room> _rooms = new Dictionary<int, Room>();
    private int _expected;

    public int ArmedRooms => _expected;

    public readonly List<string> Events = new List<string>();

    /// <summary>某队持有几个点 (测试 / 诊断)。</summary>
    public int OwnedBy(TeamId team) => _rooms.Values.Count(r => r.Entity != null && r.Entity.DenyRoomHolder == team);

    public void Scan(GameManager simulation)
    {
        _expected = simulation?.MapObjects?.OfType<ConductorGameLogic.MapObjects.Event_StorageRoom>().Count() ?? 0;

        HashSet<int> ids = simulation?.MapObjects?.OfType<ConductorGameLogic.MapObjects.Event_StorageRoom>()
            .Select(r => r.ID).ToHashSet() ?? new HashSet<int>();
        foreach (ConductorGameLogic.MapObjects.Spawn_EventEntities spawn in
                 simulation?.MapObjects?.OfType<ConductorGameLogic.MapObjects.Spawn_EventEntities>()
                 ?? Enumerable.Empty<ConductorGameLogic.MapObjects.Spawn_EventEntities>())
        {
            if (!ids.Contains(spawn.ID))
            {
                continue;
            }

            if (!_spawnsById.TryGetValue(spawn.ID, out var list))
            {
                list = new List<ConductorGameLogic.MapObjects.Spawn_EventEntities>();
                _spawnsById[spawn.ID] = list;
            }

            list.Add(spawn);
        }
    }

    /// <summary>某个补给点 (按 IndexStorageRoom) 的刷怪位数量。测试 / 诊断用。</summary>
    public int SpawnPointsOf(int index) => _rooms.TryGetValue(index, out Room room) ? room.Spawns.Count : 0;

    public void Update(GameManager simulation, ScavengerHunt hunt, float deltaTime)
    {
        Events.Clear();
        SpawnedThisTickList.Clear();

        Bind(simulation);
        if (_rooms.Count == 0)
        {
            return;
        }

        foreach (Room room in _rooms.Values)
        {
            Capture(simulation, hunt, room, deltaTime);
            Income(simulation, hunt, room, deltaTime);
        }

        Publish(hunt);
    }

    private void Bind(GameManager simulation)
    {
        foreach (StorageRoom entity in simulation.GetActiveObjectsFromCategory(ObjCat.StorageRoom))
        {
            if (entity == null || !entity.IsActive)
            {
                continue;
            }

            if (!_rooms.TryGetValue(entity.IndexStorageRoom, out Room room))
            {
                room = new Room();
                if (_spawnsById.TryGetValue(entity.ID, out var spawns))
                {
                    room.Spawns.AddRange(spawns);
                }

                _rooms[entity.IndexStorageRoom] = room;
            }

            room.Entity = entity;
        }
    }

    private void Capture(GameManager simulation, ScavengerHunt hunt, Room room, float deltaTime)
    {
        StorageRoom entity = room.Entity;

        // 边沿: 原厂 TriggerCapturePerformed 在 GameMode 里改这个字段, 插不进钩子, 只能盯着它
        if (entity.PerformedTriggerCapture != TeamId.Neutral && entity.PerformedTriggerCapture != room.Capturer
            && entity.PerformedTriggerCapture != entity.DenyRoomHolder)
        {
            room.Capturer = entity.PerformedTriggerCapture;
            room.Percent = 0f;
            room.WavesFired = 0;
            room.WaveTimer = 0f;   // 0 = 这一帧就刷第一波
            entity.DenyTakeOverTeam = room.Capturer;
            MarkDirty(simulation, entity);
            Events.Add($"拾荒补给点 {entity.IndexStorageRoom} 开始被 {room.Capturer} 占领 (持有 {entity.DenyRoomHolder})");
        }

        if (room.Capturer == TeamId.Neutral)
        {
            room.Speed = 0;
            return;
        }

        (int holders, int enemies) = CountAround(simulation, entity.Position, room.Capturer);
        int speed = CaptureSpeed.Percent(holders);

        if (holders > 0 && enemies > 0)
        {
            room.Speed = -speed;   // 客户端: <= 0 显示红色 "x{-speed/100} (Contested)"
            return;
        }

        if (holders == 0)
        {
            room.Speed = 0;
            room.Percent -= deltaTime / SecondsToCapture;
            if (room.Percent <= 0f)
            {
                Events.Add($"拾荒补给点 {entity.IndexStorageRoom}: {room.Capturer} 离开, 占领取消");
                AwardAround(simulation, entity, entity.DenyRoomHolder,
                    _00230k._0023Kcj._0023Rrh, ScavengerScores.DefendSupplyPoint);
                Cancel(simulation, room);
            }

            return;
        }

        room.Speed = speed;
        room.Percent = Math.Min(1f, room.Percent + deltaTime / SecondsToCapture * (speed / 100f));
        FireWaves(simulation, room, deltaTime);
        if (room.Percent >= 1f)
        {
            Complete(simulation, room);
        }
    }

    /// <summary>
    /// 占领期间按原厂 <c>Event_SupplySpawn</c> 的节奏刷怪: 每 <see cref="WaveSeconds"/> 秒一波,
    /// 一波 <see cref="WalkersPerWave"/> 行尸 + <see cref="SpecialsPerWave"/> 特殊,
    /// 场上分别封顶 <see cref="MaxActiveWalkers"/> / <see cref="MaxActiveSpecials"/>。
    ///
    /// 封顶算的是**这个点自己刷出来的**那批, 不算路过的漫游僵尸。
    /// 满了就少刷几只 (而不是整波跳过) —— 字面意思是"场上最多这么多"。
    /// </summary>
    private void FireWaves(GameManager simulation, Room room, float deltaTime)
    {
        if (room.Spawns.Count == 0)
        {
            return;
        }

        room.WaveTimer -= deltaTime;
        if (room.WaveTimer > 0f)
        {
            return;
        }

        room.WaveTimer = WaveSeconds;
        room.Live.RemoveAll(e => e.Zombie == null || !e.Zombie.IsActive || e.Zombie.IsDead);

        int walkers = Math.Min(WalkersPerWave, MaxActiveWalkers - room.Live.Count(e => !e.Special));
        int specials = Math.Min(SpecialsPerWave, MaxActiveSpecials - room.Live.Count(e => e.Special));
        int spawned = SpawnBatch(simulation, room, "_0023rPb", Math.Max(0, walkers), special: false);

        for (int i = 0; i < Math.Max(0, specials); i++)
        {
            // 原厂没说哪一种, 按六种特殊随机 —— 和漫游点的 BossType.RANDOM 同一个读法
            (string obfuscated, _) = ZombieFactory.Kinds[1 + simulation.Random._0023Otc(0, ZombieFactory.Kinds.Length - 1)];
            spawned += SpawnBatch(simulation, room, obfuscated, 1, special: true);
        }

        if (spawned == 0)
        {
            return;
        }

        room.WavesFired++;
        Events.Add($"拾荒补给点 {room.Entity.IndexStorageRoom} 第 {room.WavesFired} 波: {spawned} 只 "
                   + $"(进度 {room.Percent * 100f:F0}%, 场上 {room.Live.Count})");
    }

    private int SpawnBatch(GameManager simulation, Room room, string obfuscated, int count, bool special)
    {
        if (count <= 0)
        {
            return 0;
        }

        Type type = CrossroadEventCatalog.ResolveClass(obfuscated);
        int spawned = 0;
        for (int i = 0; i < count; i++)
        {
            var at = room.Spawns[room.NextSpawn++ % room.Spawns.Count];
            Character zombie = ZombieFactory.Spawn(simulation, type, at.Position, at.SpawnState, at.Direction);
            if (zombie != null)
            {
                SpawnedThisTickList.Add(zombie);
                room.Live.Add((zombie, special));
                spawned++;
            }
        }

        return spawned;
    }

    private void Complete(GameManager simulation, Room room)
    {
        StorageRoom entity = room.Entity;
        TeamId previous = entity.DenyRoomHolder;
        entity.DenyRoomHolder = room.Capturer;
        AwardAround(simulation, entity, room.Capturer, _00230k._0023Kcj._0023qkf, ScavengerScores.CaptureSupplyPoint);

        int bonus = (int)room.Bonus;
        bool thrown = bonus > 0 && SupplyThrower.Throw(simulation, entity.Position,
            Math.Max(1, bonus / SuppliesPerCrate), Math.Max(1, Math.Min(bonus, SuppliesPerCrate)), CrateScatter);

        Events.Add($"拾荒补给点 {entity.IndexStorageRoom} 被 {room.Capturer} 占下 (原持有 {previous})"
                   + (thrown ? $", 喷出奖励补给 {bonus}" : string.Empty));

        room.Bonus = 0f;
        room.IncomeTimer = 0f;
        Cancel(simulation, room);
    }

    /// <summary>圈里某队的活人各记一笔计分板得分 (见 <see cref="ScavengerScores"/>)。没有持有方 (中立) 不记。</summary>
    private void AwardAround(GameManager simulation, StorageRoom entity, TeamId team, _00230k._0023Kcj source, int value)
    {
        if (team == TeamId.Neutral)
        {
            return;
        }

        float radiusSquared = HoldRadius * HoldRadius;
        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player != null && player.IsActive && !player.IsDead && player.TeamId == team
                && Vector2.DistanceSquared(player.Position, entity.Position) <= radiusSquared)
            {
                ScavengerScores.Award(simulation, player, source, value, player.Position);
            }
        }
    }

    private static void Cancel(GameManager simulation, Room room)
    {
        room.Capturer = TeamId.Neutral;
        room.Percent = 0f;
        room.Speed = 0;
        room.Entity.DenyTakeOverTeam = TeamId.Neutral;
        room.Entity.PerformedTriggerCapture = TeamId.Neutral;
        MarkDirty(simulation, room.Entity);
    }

    private void Income(GameManager simulation, ScavengerHunt hunt, Room room, float deltaTime)
    {
        TeamId owner = room.Entity.DenyRoomHolder;
        if (owner == TeamId.Neutral)
        {
            room.Bonus = Math.Min(MaxBonus, room.Bonus + deltaTime / BonusSeconds);
            return;
        }

        // 被别人占领中就断收入 (和计分板 _0023uvb 的口径一致)
        if (room.Capturer != TeamId.Neutral || hunt.IsCompleted)
        {
            return;
        }

        room.IncomeTimer += deltaTime;
        if (room.IncomeTimer < IncomeSeconds)
        {
            return;
        }

        room.IncomeTimer -= IncomeSeconds;

        int leftToWin = hunt._0023jj - hunt.SupplyData._0023Rj(owner);
        int amount = Math.Min(IncomePerTick, Math.Max(0, leftToWin));
        if (amount > 0)
        {
            hunt.SupplyData._0023Lj(owner, amount, _0023bd._0023tKs._0023byc);
        }
    }

    private void Publish(ScavengerHunt hunt)
    {
        int slots = _rooms.Keys.Max() + 1;
        ref _0023z._0023Ctb data = ref hunt._0023l9;

        Grow(data._0023gtb, slots, 0f);
        Grow(data._0023htb, slots, 0);
        Grow(data._0023itb, slots, 0);
        Grow(data._0023jtb, slots, TeamId.Neutral);
        Grow(data._0023ktb, slots, TeamId.Neutral);
        data._0023atb = slots;

        foreach ((int index, Room room) in _rooms)
        {
            data._0023gtb[index] = room.Percent;
            data._0023htb[index] = Math.Clamp(room.Speed, -200, 200);
            data._0023itb[index] = Math.Clamp((int)room.Bonus, 0, MaxBonus);
            data._0023jtb[index] = room.Entity.DenyRoomHolder;
            data._0023ktb[index] = room.Capturer;
        }
    }

    private (int Holders, int Enemies) CountAround(GameManager simulation, Vector2 position, TeamId team)
    {
        int holders = 0;
        int enemies = 0;
        float radiusSquared = HoldRadius * HoldRadius;

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead
                || Vector2.DistanceSquared(player.Position, position) > radiusSquared)
            {
                continue;
            }

            if (player.TeamId == team)
            {
                holders++;
            }
            else if (player.TeamId != TeamId.Neutral)
            {
                enemies++;
            }
        }

        return (holders, enemies);
    }

    private static void MarkDirty(GameManager simulation, StorageRoom room) =>
        simulation?.NetworkManager?.GetSyncHelper(room.SyncHelperIndex)?._00234Ce();

    private static void Grow<T>(List<T> list, int count, T fill)
    {
        while (list.Count < count)
        {
            list.Add(fill);
        }
    }
}
