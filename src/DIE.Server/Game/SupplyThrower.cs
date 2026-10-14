using ConductorGameLogic;
using ConductorGameLogic.Entities;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 把补给**喷出去** —— 从一个中心点抛撒补给包, 散落在周围。
///
/// ## 用原厂的抛撒器, 不自己摆
///
/// 第一版 (占领补给点那边) 是自己算角度均匀摆一圈箱子 —— 箱子凭空出现在地上,
/// 玩家反馈"资源箱直接出现, 没有播放动画", 他要的是"从中心喷出来, 然后散开"。
///
/// 原厂有现成的: <c>_0023EF._0023cyc</c> (日志里叫 BossSupplyAreaEffect)。
/// 它每 <c>_0023ET</c>(0.2 秒) 扔一个包, 播 <c>Vfx_Boss_Supply_Spawn_Throw_01</c>
/// 抛出特效, 生成飞行物 <c>_0023ci</c> 按随机方向飞 <c>_0023KP</c> 距离,
/// **落点还会做碰撞检测**避免卡进墙里 (-EF/-cyc.cs:78-130)。
///
/// 名字里的 "Boss" 不是巧合 —— 这个抛撒器本来就是给 Boss 掉落用的。
///
/// ## 字段语义是交叉验证的, 不是猜的
///
///   Character.cs:2917  囤积者僵尸死亡  _00237q=30 次, _0023Pec=5  -> 共 150
///                      (150 正是 BalanceData._0023j7f._0023Qtb)
///   Supplies.cs:411    通用抛撒       _00237q=总量/每包, _0023Pec=每包
///
/// 也就是 <c>_00237q</c> = **包数**, <c>_0023Pec</c> = **每包补给量**。
/// <c>UpdateGameObject</c> 里 <c>_00237q--</c> 每扔一个减一, 对得上。
///
/// ## 为什么抽出来
///
/// 占领补给点和 Boss 掉落要的是**同一件事**。玩家实测:
/// "BOSS LOOTER 死亡掉落的物资箱只有一个, 应该掉落和占领补给点后同样数量的物资,
/// 且要播放物资箱掉落动画。" —— 与其在两处各写一份迟早会漂移的代码,
/// 不如让两边共用这一个入口。
/// </summary>
public static class SupplyThrower
{
    /// <summary>
    /// 掉几包。
    ///
    /// **我们定的**。玩家实测: "占领成功掉落大量物资箱" —— 第一版只掉一个,
    /// 观感差太远。
    /// </summary>
    public const int DefaultCrateCount = 10;

    /// <summary>
    /// 每包多少补给。
    ///
    /// **我们定的**, 但上限是硬的: 玩家身上一次最多背 100
    /// (<c>BalanceData._0023j7f._0023Etb</c>)。第一版给 100, 结果玩家**捡一个
    /// 箱子就满了**, 剩下九个全白掉。
    ///
    /// 改成 10: 十个箱子加起来 100, 正好背满一趟, 玩家得走一圈捡。
    /// </summary>
    public const int DefaultSuppliesPerCrate = 10;

    /// <summary>
    /// 补给包最远飞多远 (<c>_0023cyc._0023KP</c>)。原厂默认 50 (-EF/-cyc.cs:56)。
    ///
    /// 占领补给点那边用 30 —— 玩家要的是"散在**补给点里**", 而占领圈是 100,
    /// 50 会把包扔到圈边上。抛撒器自己会做落点碰撞检测, 不用担心卡进墙里。
    /// </summary>
    public const float DefaultScatter = 30f;

    /// <summary>
    /// 从 <paramref name="center"/> 抛撒补给。
    /// </summary>
    /// <param name="owner">
    /// 抛撒者。Boss 掉落传那只 Boss —— 原厂囤积者死亡那段
    /// (Character.cs:2917) 传的就是 <c>Owner</c> 和自己。传 null 也能跑。
    /// </param>
    /// <returns>抛撒器成功挂上场返回 true。</returns>
    public static bool Throw(GameManager simulation, Vector2 center,
                             int crateCount = DefaultCrateCount,
                             int suppliesPerCrate = DefaultSuppliesPerCrate,
                             float scatter = DefaultScatter,
                             Entity owner = null)
    {
        if (simulation == null || crateCount <= 0)
        {
            return false;
        }

        _0023EF._0023cyc thrower = simulation.GetGameObjectFromPool<_0023EF._0023cyc>();
        if (thrower == null || thrower.IsDummyObject)
        {
            return false;
        }

        thrower._0023Fc = center;              // 喷射中心
        thrower._00237q = crateCount;          // 扔几包
        thrower._0023Pec = suppliesPerCrate;   // 每包多少
        thrower._0023KP = scatter;             // 最远飞多远

        simulation.SpawnGameObject(thrower, owner, owner);

        return thrower.IsActive;
    }
}
