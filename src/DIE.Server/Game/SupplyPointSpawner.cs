using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;

namespace DIE.Server.Game;

/// <summary>
/// 按地图上的 <see cref="SupplySpawnPoint"/> 铺补给箱。
///
/// ## 为什么需要它
///
/// 教程的胜利条件是收集补给 (<c>TutorialGameModeSettings.SuppliesToWin = 1000</c>),
/// 而补给箱只有两个来源:
///
///   1. 死亡掉落 —— <c>Supplies._0023Xj</c>, 但**只有 Looter / 特殊僵尸 / 精英**
///      才掉, 普通 Walker 不掉 (Supplies.cs:315-332)
///   2. 地图上预先铺好的 <see cref="SupplySpawnPoint"/>
///
/// 教程图的漫游刷怪点只生成 Walker, 所以第 1 条在教程里几乎不产出 ——
/// **第 2 条才是教程补给的主来源**。
///
/// <c>SupplySpawnPoint</c> 在整个代码树里**只有定义、零消费者** —— 读它并
/// 铺箱子的那段是服务端代码, 随服务端丢失。和 <see cref="RoamingZombieSpawner"/>
/// 是同一个形状。
///
/// ## 地图数据给了什么
///
///   _MinSupplies / _MaxSupplies   这个点放多少补给  (Ranged 0-250)
///   _SpawnRatio                   生成概率          (Ranged 0-1)
///
/// 三个字段齐全, 我们不发明任何数值。
///
/// ## 一个箱子装多少
///
/// <c>_0023yGc._0023sj</c> 是 **byte** —— 单箱上限 255。地图允许单点 250,
/// 所以一个点一个箱子放得下, 不用拆分。
///
/// 拾取上限是另一回事: <c>BalanceData._0023j7f._0023Etb</c> (=100) 是玩家
/// **身上**能带的补给上限, 超了就得先去补给点交付
/// (<c>_0023yGc._0023xGc</c> 里那段就是在处理"背不下了"的情况)。
/// </summary>
public static class SupplyPointSpawner
{
    public sealed record Result(int Spawned, int Points, int TotalSupplies);

    /// <summary>
    /// 铺一遍全图的补给点。开局调一次。
    ///
    /// 幂等性由调用方保证 —— 每调一次就铺一批。
    /// </summary>
    public static Result SpawnAll(GameManager manager)
    {
        int spawned = 0;
        int points = 0;
        int totalSupplies = 0;

        if (manager?.MapObjects == null)
        {
            return new Result(0, 0, 0);
        }

        foreach (MapObject mapObject in manager.MapObjects)
        {
            if (mapObject is not SupplySpawnPoint point)
            {
                continue;
            }

            points++;

            // 概率判定用模拟层自己的 RNG —— 服务端所有随机都该走同一个源,
            // 它参与回放的确定性重放。
            if (point._SpawnRatio < 1f && manager.Random.NextDouble() > point._SpawnRatio)
            {
                continue;
            }

            int amount = AmountFor(manager, point);
            if (amount <= 0)
            {
                continue;
            }

            if (Spawn(manager, point, amount))
            {
                spawned++;
                totalSupplies += amount;
            }
        }

        return new Result(spawned, points, totalSupplies);
    }

    /// <summary>
    /// 这个点放多少补给。
    ///
    /// 夹到 byte 上限 —— <c>_0023yGc._0023sj</c> 是 byte, 溢出会静默截断成
    /// 一个小得多的数 (250 不会溢, 但地图数据以后改了呢)。
    /// </summary>
    private static int AmountFor(GameManager manager, SupplySpawnPoint point)
    {
        int min = point._MinSupplies;
        int max = point._MaxSupplies;

        if (max < min)
        {
            (min, max) = (max, min);
        }

        int amount = max > min ? manager.Random._0023Otc(min, max + 1) : min;
        return amount > byte.MaxValue ? byte.MaxValue : amount;
    }

    /// <summary>
    /// 生成一个补给箱。
    ///
    /// 字段名照抄原厂自己生成补给箱的那两处 (Supplies.cs:334 的 _0023Yj
    /// 和 :374): <c>_0023B</c> 是位置 (注意**不是** Position —— 拾取物基类
    /// <c>_00235W</c> 用自己的字段), <c>_0023sj</c> 是补给数量。
    ///
    /// owner/spawner 都传 null, 和原厂一致 —— 补给箱不属于任何人。
    /// </summary>
    private static bool Spawn(GameManager manager, SupplySpawnPoint point, int amount)
    {
        _0023aX._0023yGc crate = manager.GetGameObjectFromPool<_0023aX._0023yGc>();
        if (crate == null || crate.IsDummyObject)
        {
            return false;
        }

        crate._0023B = point.Position;
        crate._0023sj = (byte)amount;
        manager.SpawnGameObject(crate, null, null);

        return crate.IsActive;
    }

    /// <summary>
    /// 场上还剩多少个补给箱, 诊断用。
    /// </summary>
    public static int CountActive(GameManager manager)
    {
        if (manager?.ActiveGameObjectList == null)
        {
            return 0;
        }

        int count = 0;
        List<GameObjectBase> objects = manager.ActiveGameObjectList;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is _0023aX._0023yGc { IsActive: true })
            {
                count++;
            }
        }

        return count;
    }
}
