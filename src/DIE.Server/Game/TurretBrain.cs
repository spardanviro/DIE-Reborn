using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using FarseerPhysics.Common;
using _00236;
using ConductorGameLogic.Collision;
using _0023EK;
using _0023Oq;

namespace DIE.Server.Game;

/// <summary>
/// 炮塔的"眼睛" —— 给基地机枪 (<c>_0023Zuc</c>) 挑目标。
///
/// ## 真机症状
///
/// 玩家实测 (十字路口, 2026-09-12): "基地的机枪不会主动攻击僵尸。"
///
/// ## 炮塔的身子是齐的, 丢的只有"看见谁"
///
/// 和 <see cref="ZombieBrain"/> 一模一样的形状。原厂 <c>_0023Zuc</c> 里现成的有:
///
///   - 摆出来: <c>GameMode.Load</c> -&gt; <c>_0023yg()</c> (GameMode.cs:1186) 按地图上的
///     <c>Spawn_TruckTurret</c> 逐个生成, 伤害 45 / 0.25 秒一发 / 一梭 16 发 / 装弹 1 秒 /
///     弹速 220 / 索敌半径 120 / 血量 2000 —— **全是原厂数字**, 我们一个都没定
///   - 转向: <c>UpdateDestructible</c> 每帧朝目标转 (270°/秒)
///   - 开火: 对准了 (点积 &gt; 0.96) 且视线无遮挡就吐子弹, 打空一梭自己装弹
///   - 目标失效 / 被墙挡住时自己把目标清掉
///
/// 唯独**没有任何一处去找目标**: <c>SetTarget(Entity)</c> 是 public 却零调用者,
/// 而那个"这东西能不能打"的判据 <c>_0023CK</c> (:236) 被缓存进
/// <c>_0023BK</c> (:139) 之后就再没被读过 —— 典型的"服务端权威逻辑被裁":
/// 拿着判据去问碰撞管理器的那一句在服务端, 随服务端丢了。
///
/// 所以这里补的就是那一句。
///
/// ## 判据逐句抄原厂的
///
/// <see cref="CanShoot"/> 是 <c>_0023Zuc._0023CK</c> 的翻写 (那个方法是 private,
/// 缓存它的字段也是 private, 拿不到)。四个条件一个不少, 见那边的注释。
///
/// 队伍关系用原厂的 <c>IsEnemy</c> (阵营不同即敌对, GameObjectBase.cs:995) ——
/// 地图上的炮塔是 <c>Team1</c>, 僵尸是 <c>Neutral</c>, 于是僵尸是它的敌人。
/// 玩家在十字路口也是 Team1, 所以不会被自家炮塔打。
/// </summary>
public sealed class TurretBrain
{
    /// <summary>
    /// 目标离得多远就不管了。**原厂的数**: <c>GameMode.cs:1211</c> 给每座卡车炮塔设
    /// <c>_0023Hnb = 120f</c> (那个字段名在 <c>Trigger_Sentry</c> 那条路上叫
    /// <c>EnemyTargetArea</c>, GameMode.cs:1178)。
    ///
    /// 读炮塔自己身上的值, 不写死 —— 哨兵炮台 (玩家放的那种) 用的是地图/技能给的别的数。
    /// 这里只在读不到时兜底。
    /// </summary>
    public float FallbackRange { get; set; } = 120f;

    /// <summary>诊断: 上一帧有几座炮塔正咬着目标。宿主每秒打一次日志。</summary>
    public int LastEngaged { get; private set; }

    /// <summary>诊断: 本秒换过多少次目标。宿主读完自行清零。</summary>
    public int TargetsAcquired;

    /// <summary>
    /// 每帧一次, 跑在 <c>GameManager.Update</c> **之前** —— 理由同 <see cref="ZombieBrain"/>:
    /// 炮塔的转向和开火当帧就消费目标, 跑在后面每个决策都晚一帧。
    /// </summary>
    public void Update(GameManager simulation)
    {
        List<GameObjectBase> objects = simulation?.ActiveGameObjectList;
        if (objects == null)
        {
            return;
        }

        int engaged = 0;

        // 下标遍历: 开火会往表里塞子弹对象, foreach 会抛
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not _0023Zuc turret || !turret.IsActive || turret.IsDead)
            {
                continue;
            }

            // 停火的不用挑目标 —— 十字路口夜袭会把基地炮塔断电
            // (CrossroadMissionDirector.CutPowerToBaseTurrets), 开火那一句判的
            // 就是这个字段。挑了也打不出去, 还会让它白转。
            if (turret._0023qab)
            {
                continue;
            }

            Entity target = Retarget(simulation, turret);
            if (target != null)
            {
                engaged++;
            }
        }

        LastEngaged = engaged;
    }

    /// <summary>
    /// 一座炮塔的目标: 现在这个还打得着就留着, 否则换最近的一个。
    ///
    /// ## 为什么"还打得着就留着"
    ///
    /// <c>SetTarget</c> 会强制同步一次 (<c>_SyncHelper._00234Ce()</c>, -EK/-Zuc.cs:145)。
    /// 每帧重新指派同一个目标等于每帧多一条同步, 8 座炮塔就是每秒 240 条 —— 而且
    /// 炮塔转向是渐进的 (270°/秒), 目标抖一下就永远对不准、打不出子弹。
    /// </summary>
    /// <returns>这座炮塔现在的目标 (可能是 null)。</returns>
    private Entity Retarget(GameManager simulation, _0023Zuc turret)
    {
        float range = turret._0023Hnb > 0f ? turret._0023Hnb : FallbackRange;

        if (IsStillGood(simulation, turret, turret._0023nk, range))
        {
            return turret._0023nk;
        }

        Entity found = Nearest(simulation, turret, range);

        if (!ReferenceEquals(found, turret._0023nk))
        {
            turret.SetTarget(found);
            if (found != null)
            {
                TargetsAcquired++;
            }
        }

        return found;
    }

    /// <summary>
    /// 射程内最近的一个能打的 —— **遍历场上对象表**, 不查碰撞四叉树。
    ///
    /// ## 为什么不用原厂那句四叉树查询
    ///
    /// 原厂这里几乎肯定是一句 <c>CollisionManager._0023o8(new _0023Gjc(位置, 半径), ...)</c>
    /// (炮塔自己缓存的那个判据就是为它准备的)。但那棵**动态四叉树在我们的单元测试夹具里
    /// 是空的** —— 实测: 在 Scout 图和十字路口图上, 半径 5000 不带过滤的查询都返回 0 个,
    /// 而场上明明有 9 个对象。它要靠真正的帧循环去填, 夹具只跑 <c>LoadMap</c>。
    ///
    /// 于是这里改成遍历 <c>ActiveGameObjectList</c>:
    ///
    ///   - 测得动 (这条病的回归测试跑得起来, 见 <c>TurretBrainTests</c>)
    ///   - 和 <see cref="ZombieBrain"/> 同一个做法 (它也是遍历 <c>ActivePlayerList</c> 挑目标)
    ///   - 开销可接受: 一局场上几十个实体 × 8 座炮塔, 比僵尸脑子那层还小
    ///
    /// 视线遮挡仍然用原厂那句静态几何查询 —— 那一句在夹具里**是好的**
    /// (它查的是地图几何, 不是动态树)。
    /// </summary>
    private Entity Nearest(GameManager simulation, _0023Zuc turret, float range)
    {
        List<GameObjectBase> objects = simulation.ActiveGameObjectList;
        Entity best = null;
        float bestDistance = range * range;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not Entity candidate || !candidate.IsActive || candidate.IsDead)
            {
                continue;
            }

            float distance = Vector2.DistanceSquared(turret.Position, candidate.Position);
            if (distance > bestDistance || !CanShoot(candidate, turret))
            {
                continue;
            }

            // 视线判定放最后 —— 它比前面几条贵。开火那边也会查同一句
            // (-EK/-Zuc.cs:310), 但它查完是把目标**清掉**; 我们这边不查的话
            // 下一帧又指回去, 就成了死循环 (每帧一条同步, 一发子弹都不出)。
            if (IsBlocked(simulation, turret, candidate))
            {
                continue;
            }

            best = candidate;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>现在这个目标还值得打吗 (活着、在场、在射程内、没被墙挡住)。</summary>
    private bool IsStillGood(GameManager simulation, _0023Zuc turret, Entity target, float range)
    {
        if (target == null || !target.IsActive || target.IsDead || !CanShoot(target, turret))
        {
            return false;
        }

        if (Vector2.DistanceSquared(turret.Position, target.Position) > range * range)
        {
            return false;
        }

        return !IsBlocked(simulation, turret, target);
    }

    /// <summary>
    /// 这东西能不能打 —— <c>_0023Zuc._0023CK</c> (-EK/-Zuc.cs:236) 的翻写, 四个条件:
    ///
    ///   1. <c>_0023jtc._0023btc</c>: 有法术碰撞体积 且 是敌人 (阵营不同)
    ///   2. 不是路障 (<c>_0023Yh</c>) —— 炮塔不拆自家的栅栏
    ///   3. 不是"不可选中"状态 (隐身、幽灵、进场动画里的那些)
    ///   4. 身上没有"失去行动能力"的 buff (<c>_0023zBc</c>) —— 已经倒地的不补枪
    /// </summary>
    private static bool CanShoot(Entity candidate, GameObjectBase turret)
    {
        if (candidate == null || !_0023jtc._0023btc(candidate, turret))
        {
            return false;
        }

        if (candidate is _0023Yh || candidate.IsNonTargetable())
        {
            return false;
        }

        return !candidate.HasBuff(_0023fGc._0023zBc);
    }

    /// <summary>
    /// 炮塔到目标之间有没有挡着的地形 —— 和开火那边用的是**同一句**
    /// (<c>CollisionManager._0023c8._00233b</c>, -EK/-Zuc.cs:310), 两个几何标志也照抄,
    /// 否则会出现"我选了它、开火那边不认"的死循环。
    /// </summary>
    private static bool IsBlocked(GameManager simulation, _0023Zuc turret, Entity target)
    {
        _00236._0023Cc geometry = simulation?.CollisionManager?._0023c8;
        if (geometry == null)
        {
            return false;
        }

        return geometry._00233b(turret.Position, target.Position,
            GeometryCollisionFlags._00237jd | GeometryCollisionFlags._00239jd,
            _00237b: false, _00238b: false);
    }
}
