using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using ConductorGameLogic.Stats;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 打破路障给经验。
///
/// ## 为什么要自己写: 原厂那条路对非 Character 不通
///
/// 经验只有一个发放函数 <c>_0023bd._0023Zt</c>, 两个入口:
///
///   `_00232R(Character 死者, GameObjectBase 凶手, GameManager, float 系数)`
///   `_0023mar(Player 领取者, Character 死者, GameManager, float 系数)`  ← 固定 60, **零调用者**
///
/// 两个都要 <c>Character</c>。而路障是 <c>_0023Yh : Destructible : Entity</c> ——
/// **不是 Character**。所以:
///
///   - <c>GameManager.CharacterDied</c> 对路障根本不触发
///   - <c>_00234R</c> 的 <c>ObjCat</c> 分派表里也没有 <c>ObjCat.Barricade</c> 分支
///     (Player / Hoarder / Specials / Elites / Infected / Creep / Bosses / Walker / Pet)
///
/// 全树搜 <c>StatType.Experience</c> 的 <c>ChangeStats</c> 只有四处, 没有一处和
/// 路障相关。**结论: 原厂的路障经验是服务端逻辑, 随服务端丢失。**
///
/// ## 数值是我们定的
///
/// 玩家说原版打破栅栏会给经验, 但给多少没有任何来源。参照系:
///
///   Walker 基础经验 10  (`_0023Aub._0023Utb = 10f`, 实测到手 14 = 10 × 1.4 等级差修正)
///   Infected        15
///   Specials        45
///   Elites         125
///   Hoarder        200
///
/// 取 <see cref="ExperiencePerBarricade"/> = 15 —— 和 Infected 同档:
/// 一段 200 血的栅栏挨十几下才破, 工作量比一只 Walker 大一点。
///
/// **这是估的**, 不是还原。玩家觉得不对就调。
///
/// ## 实现方式: 自己盯着, 不用原厂监听
///
/// <c>Destructible.OnStatValueChangedDestructible</c> (:177) 是 blood 0 时
/// <c>Destroy()</c> 的地方, 但它 <c>protected virtual</c> 而 <c>_0023Yh</c> 是
/// <c>sealed</c> —— 不改反编译树就没法重写。
///
/// <c>GameManager.ActivateDestroyListener</c> (:3069) 是原厂机制, 但它按对象注册,
/// 而路障是池化复用的 —— 注册的监听在对象被回收再利用后会打到错的东西上。
///
/// 所以这里自己记一份名单, 每帧比对"上一帧活着、这一帧死了"。开销是路障数 ×
/// 一次布尔读, 教程 11 段, 忽略不计。
/// </summary>
public sealed class DestructibleRewards
{
    /// <summary>
    /// 打破一段路障给多少经验。见类说明 —— **这是估的值**。
    /// </summary>
    public static float ExperiencePerBarricade { get; set; } = 15f;

    /// <summary>
    /// 领取经验的距离上限。和击杀经验用同一个数 —— <c>_0023Zt._00238R</c> 里
    /// 分配击杀经验时的距离判据是 150, 路障没理由不同。
    /// </summary>
    public static float RewardRadius { get; set; } = 150f;

    private sealed class Tracked
    {
        public _0023EK._0023Yh Barricade;
        public bool WasAlive;

        /// <summary>
        /// 池化复用的防串号。对象被回收再拿出来当别的路障时这个数会变,
        /// 那时旧记录作废 —— 不然会把"复用"误判成"死了"。
        /// </summary>
        public int CacheUses;
    }

    private readonly List<Tracked> _tracked = new List<Tracked>();

    /// <summary>本次 <see cref="Update"/> 打破的段数, 供宿主打日志。</summary>
    public int DestroyedThisTick { get; private set; }

    /// <summary>累计发出去的经验, 诊断用。</summary>
    public float TotalExperienceGranted { get; private set; }

    /// <summary>登记在册的路障数。</summary>
    public int TrackedCount => _tracked.Count;

    /// <summary>
    /// 登记一段路障。<see cref="DestructibleSpawner"/> 生成后调。
    /// </summary>
    public void Track(_0023EK._0023Yh barricade)
    {
        if (barricade == null || barricade.IsDummyObject)
        {
            return;
        }

        _tracked.Add(new Tracked
        {
            Barricade = barricade,
            WasAlive = IsAlive(barricade),
            CacheUses = barricade.NumberOfGlobalCacheUses,
        });
    }

    /// <summary>
    /// 每帧一次。发现"上一帧活着、这一帧死了"的路障就给附近玩家发经验。
    /// </summary>
    public void Update(GameManager simulation)
    {
        DestroyedThisTick = 0;

        if (simulation == null)
        {
            return;
        }

        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            Tracked entry = _tracked[i];
            _0023EK._0023Yh barricade = entry.Barricade;

            if (barricade == null)
            {
                _tracked.RemoveAt(i);
                continue;
            }

            // 被池回收再利用了 —— 这条记录说的已经不是同一段路障, 作废。
            if (barricade.NumberOfGlobalCacheUses != entry.CacheUses)
            {
                _tracked.RemoveAt(i);
                continue;
            }

            bool alive = IsAlive(barricade);
            if (entry.WasAlive && !alive)
            {
                DestroyedThisTick++;
                GrantNearby(simulation, barricade.Position);
                _tracked.RemoveAt(i);
                continue;
            }

            entry.WasAlive = alive;
        }
    }

    /// <summary>
    /// 这段路障还活着吗。
    ///
    /// ## ⚠️ 判据是血量, **不能用 <c>IsDead</c>**
    ///
    /// <c>_0023Yh</c> 重写了 <c>IsDead</c> (-EK/-Yh.cs:105):
    ///
    ///     public override bool IsDead =&gt; BarricadeStatus == _0023Yxc._0023Xxc;
    ///
    /// 它看的是**状态枚举**而不是血量, 而那个枚举在
    /// <c>UpdateDestructible</c> 里按 `Health / MaxHealth` 的比例每帧刷
    /// (-EK/-Yh.cs:498-511)。
    ///
    /// 后果: 血刚打到 0 的那一帧 <c>IsDead</c> 还是 false。真机上下一帧就对了,
    /// 所以影响不大 —— 但在单元测试里 (不跑 <c>manager.Update</c>) 它永远是 false,
    /// 而那正是我第一版测试红掉的原因。观测点打出来的是
    /// `HP=-100/200 IsActive=True IsDead=False`, 一眼就看出问题在哪。
    ///
    /// 用 <c>Health &lt;= 0</c> 是即时的, 两边都对。
    /// </summary>
    private static bool IsAlive(_0023EK._0023Yh barricade) =>
        barricade.IsActive && barricade.Health > 0f;

    /// <summary>
    /// 给附近活着的玩家发经验。
    ///
    /// 走 <c>ChangeStats</c> 而不是自己加 —— 属性系统会触发升级、通知客户端、
    /// 走我们已经接回来的 <c>OnExperienceGranted</c> 回调。直接改字段那些全没有。
    /// </summary>
    private void GrantNearby(GameManager simulation, Vector2 position)
    {
        List<Player> players = simulation.ActivePlayerList;
        if (players == null)
        {
            return;
        }

        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            if (Vector2.Distance(player.Position, position) > RewardRadius)
            {
                continue;
            }

            simulation.ChangeStats(
                player, StatType.Experience, _00230k._0023UIc._0023RIc,
                ExperiencePerBarricade, StatChangeReason._0023Zt, player);

            TotalExperienceGranted += ExperiencePerBarricade;
        }
    }
}
