using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Entities.GameModeObjects;
using ConductorGameLogic.MapObjects;

namespace DIE.Server.Game;

/// <summary>
/// Horde 图上的占领点 (<c>HordeSpawner</c>) 和它锁着的路障。
///
/// ## 原厂在这条链上留下了什么
///
/// 和补给点占领 (<see cref="StorageRoomCaptureDirector"/>) 高度同构 —— 按 X
/// 到"读条结束"为止**全是原厂的**:
///
///   1. <c>HordeMode._00236fc</c> 给每个 <c>Event_Horde</c> 建一个
///      <c>_00237P._0023Sob</c> 静态地图对象 (Lab Raid 上 2 个)
///   2. 走近 -&gt; <c>_0023Sob._00234P</c> 推交互提示
///   3. 按 X -&gt; 技能槽 **26** (<c>Player.ResetGeneralAbilites</c> 装的是
///      <c>AbilityIdentifier.CaptureHorde</c>) -&gt; <c>_0023Sq._0023PZb</c> buff
///      引导 -&gt; <c>GameMode.HordeSpawnerCaptured</c>
///   4. 那里找最近的 <c>_0023Sob</c>, 调 <c>._0023Oob.StartCapture()</c>
///
/// ## 断在哪两处
///
/// **一、<c>HordeSpawner</c> 实体从来没人创建。**
/// 全代码树零个 <c>GetGameObjectFromPool&lt;HordeSpawner&gt;</c>。
/// <c>_0023Sob._00234P</c> 每帧按 ID 去 <c>ObjCat.HordeSpawner</c> 里找它
/// (`-7P/-Sob.cs:63-72`), 找不到就一直是 null。
///
/// 后果不是"没反应"而是**崩**: <c>HordeMode.cs:205</c> 写的是
/// <c>_0023Sob2?._0023Oob.StartCapture()</c> —— <c>?.</c> 只保护了外层的
/// <c>_0023Sob2</c>, <c>_0023Oob</c> 为 null 直接 NRE。玩家一按占领就炸。
///
/// **二、<c>CaptureDone()</c> 和 <c>SpawnBlockage()</c> 都是零调用方。**
/// <c>StartCapture()</c> 把 <c>CaptureState</c> 设成 1 (正在占领) 之后
/// **没人推进到 2**, <c>HordeSpawner.UpdateGameObject</c> 里只有同步簿记
/// (HordeSpawner.cs:124)。路障也从来没生成过。
///
/// ## 路障的参数是原厂数据, 不是我们编的
///
/// <c>Spawn_HordeBlockage</c> 地图对象 (Lab Raid 上 18 个) 的字段和
/// <c>HordeSpawner.SpawnBlockage(position, direction, part, type)</c> 的形参
/// **逐个对应**, 还多一个 <c>ActivatingHordeSpawner</c> 说明它归哪个占领点管:
///
///   Position                 -&gt; position
///   Direction                -&gt; direction
///   BarricadePart            -&gt; barricadePart
///   BarricadeType            -&gt; barricadeType
///   ActivatingHordeSpawner   -&gt; 归属的 HordeSpawner.ID
///
/// <c>CaptureDone()</c> 自己会把该 spawner 名下的路障全 <c>Destroy()</c>
/// (HordeSpawner.cs:162-169) —— 所以"占领完解锁路障"这件事我们不用管,
/// 只要在对的时机调它。
///
/// ## 哪些数是我们定的
///
/// **只有 <see cref="CaptureSeconds"/>。** <c>Event_Horde</c> 上有
/// CaptureRange / RestrictingFog / RestrictingBlockages / RestrictingRadius,
/// **没有时长字段** —— 引导那一段的时长在 buff 的 <c>_0023Gab</c> 里 (原厂),
/// 但"读条读完之后到 CaptureDone 之间"要不要再等一段, 无从考证。
/// 现在取 0 (读条完就算占下), 等玩家实测再校准。
/// </summary>
public sealed class HordeSpawnerDirector
{
    /// <summary>
    /// 从 <c>StartCapture()</c> 到 <c>CaptureDone()</c> 之间还要多久。
    ///
    /// **这是我们定的。** 0 = 引导读条读完就立刻算占领成功 —— 原厂的读条时长
    /// 已经由 <c>_0023Sq._0023PZb</c> 的 <c>_0023Gab</c> 管了, 这里再加一段
    /// 是没有依据的。留成可调是为了实测时好试。
    /// </summary>
    public float CaptureSeconds { get; set; }

    /// <summary>生成了几个 <c>HordeSpawner</c>。诊断用。</summary>
    public int SpawnersCreated { get; private set; }

    /// <summary>生成了几段路障。诊断用。</summary>
    public int BlockagesCreated { get; private set; }

    /// <summary>占领完成了几次。诊断用。</summary>
    public int CapturesCompleted { get; private set; }

    private bool _scanned;

    /// <summary>正在占领中的点 -&gt; 已经读了多久。</summary>
    private readonly Dictionary<HordeSpawner, float> _capturing = new();

    /// <summary>
    /// 建占领点实体, 并把路障挂上去。幂等。
    ///
    /// 顺序要紧: <c>SpawnBlockage</c> 内部要用 <c>GameManager</c> 取对象池
    /// (HordeSpawner.cs:171), 那是 <c>SpawnGameObject</c> 之后才有的 ——
    /// 所以先 Spawn 占领点, 再给它加路障。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.GameMode == null)
        {
            return;
        }

        _scanned = true;

        // _0023Sob 是原厂 HordeMode._00236fc 建的, 每个 Event_Horde 一个。
        // 我们只补它缺的那个实体。
        List<_00237P._0023Sob> points = simulation.GameMode.StaticMapObjects
            .OfType<_00237P._0023Sob>()
            .ToList();

        if (points.Count == 0)
        {
            return;
        }

        List<Spawn_HordeBlockage> blockages = simulation.MapObjects?
            .OfType<Spawn_HordeBlockage>()
            .ToList() ?? new List<Spawn_HordeBlockage>();

        foreach (_00237P._0023Sob point in points)
        {
            HordeSpawner spawner = simulation.GetGameObjectFromPool<HordeSpawner>();
            if (spawner == null)
            {
                // 对象池上限是 [_0023had(2, 2, -1, 0, 0, 0)] 的第一个参数 = 2。
                // Lab Raid 正好 2 个点; 别的图要是更多, 这里会静默少建 ——
                // 所以记个数, 让宿主能把它和地图上的点数对上。
                continue;
            }

            // HordeSpawner 是 GameObjectBase 不是 Entity —— **它没有位置**。
            // 位置只存在于 _0023Sob._0023B 上, 两者靠 ID 关联
            // (`-7P/-Sob.cs:68` 按 ID 找, HordeMode.cs:207 按 _0023B 算距离)。
            spawner.ID = point._0023kg;
            simulation.SpawnGameObject(spawner, null, null);
            SpawnersCreated++;

            // **当场挂上, 不等 _00234P 那一帧。**
            //
            // `-7P/-Sob.cs:63-72` 每帧在 _0023Oob 为 null 时按 ID 去找, 所以
            // 迟早会挂上 —— 但"迟早"意味着 Scan 之后的**第一帧内**占领会 NRE
            // (HordeMode.cs:205 的 ?. 只保护外层)。真机上玩家不可能那么快按到,
            // 但把不变量做成"建好即可用"比依赖时序可靠。
            //
            // 和 ActivateStatListener 那个坑同类: 那次是 fixture 少跑一帧,
            // 整条伤害钩子从没被测到 (踩坑 28)。
            point._0023Oob = spawner;

            // 路障按 ActivatingHordeSpawner 归属。用 spawner.ID 匹配 ——
            // 那个 ID 来自 Event_Horde.ID, 和地图数据是同一个编号体系。
            foreach (Spawn_HordeBlockage blockage in blockages
                         .Where(b => b.ActivatingHordeSpawner == spawner.ID))
            {
                spawner.SpawnBlockage(blockage.Position, blockage.Direction,
                                      blockage.BarricadePart, blockage.BarricadeType);
                BlockagesCreated++;
            }
        }
    }

    /// <summary>
    /// 推进正在占领中的点。
    ///
    /// <c>StartCapture()</c> 只把状态设成 1 就不管了 (HordeSpawner.cs:153),
    /// 推进到 2 的那一步随服务端丢失。<c>CaptureDone()</c> 自己会销毁该点
    /// 名下的路障。
    /// </summary>
    public void Update(GameManager simulation, float deltaTime)
    {
        if (simulation == null)
        {
            return;
        }

        foreach (GameObjectBase obj in simulation.GetActiveObjectsFromCategory(ObjCat.HordeSpawner))
        {
            if (obj is not HordeSpawner spawner)
            {
                continue;
            }

            if (!spawner.IsBeeingCaptured)
            {
                // 占完了或者被重置了, 别把读条留在表里
                _capturing.Remove(spawner);
                continue;
            }

            float elapsed = _capturing.TryGetValue(spawner, out float value) ? value : 0f;
            elapsed += deltaTime;

            if (elapsed >= CaptureSeconds)
            {
                _capturing.Remove(spawner);
                spawner.CaptureDone();
                CapturesCompleted++;
                continue;
            }

            _capturing[spawner] = elapsed;
        }
    }

    /// <summary>换局时清干净。</summary>
    public void Reset()
    {
        _scanned = false;
        _capturing.Clear();
        SpawnersCreated = 0;
        BlockagesCreated = 0;
        CapturesCompleted = 0;
    }
}
