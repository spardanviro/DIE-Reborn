using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Entities.Characters;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 召唤物 (<see cref="InfestedMinion"/>) 的行为 —— 跟随主人、护主、执行指令。
///
/// ## 技能本身不需要服务端实现, AI 才需要
///
/// 这一点值得说清楚, 因为它决定了后面还有多少活:
///
/// 召唤这个动作是**完整**的。<c>Command</c> 技能的实现 (-EF/-Hyc.cs:145-172)
/// 从池里取 InfestedMinion、设位置、<c>SpawnGameObject</c>、设 <c>PetOwner</c>、
/// 灌 MaxHealth/Health/Power、挂天赋标志、发特效事件、给主人加一个 buff ——
/// 一行都不缺, 而且**已经在服务端跑了** (玩家能召唤出来就是证据)。
///
/// 技能系统是双端共享的 (<c>ConductorGameLogic.Abilities</c>), 服务端只是
/// 把 <c>IsServer</c> 拨到 true 之后跑同一套代码。所以**不需要给每个技能
/// 写服务端实现**。
///
/// 缺的是同一个老缺口: <c>GameManager.ConnectToAIUnit</c> (:2063) 是空的,
/// 没人给 NPC 下达目标。<see cref="ZombieBrain"/> 只管敌对僵尸,
/// 而 <c>InfestedMinion</c> 直接继承 <c>NPC</c> 而不是僵尸基类 <c>_0023iO</c>,
/// 所以 <c>IsZombie</c> 对它是 false —— 那个脑子完全跳过了它。
///
/// ## 五个状态
///
/// <c>InfestedMinion._0023cZ</c> (:18), 名字由客户端的特效分支反推 (:194-219):
///
///   _00238Y  Hunt          默认。跟着主人, 顺手打附近的敌人
///   _00239Y  KillTarget    去打 TargetIndex 指定的目标
///   _0023MY  FollowOwner   只跟随, 不主动出击
///   _0023aZ  MoveTo        走到指定位置
///   _0023bZ  MoveAndHold   走到指定位置并驻守
///
/// 状态由 <c>Command</c> / <c>CommandTarget</c> 技能设置, 那些是双端共享的,
/// 我们不碰。**每个状态下具体怎么走、打谁**才是丢掉的部分。
///
/// <c>InfestedMinion.UpdateNPC</c> (:188) 那句
/// <c>if (... || base.GameManager.IsServer || ...) return;</c> 之后全是客户端
/// 特效 —— 服务端在那个方法里什么都不做, 印证了行为层确实在别处 (在服务端)。
/// </summary>
public sealed class MinionBrain
{
    /// <summary>
    /// 跟随时和主人保持的距离。近于此就停下, 免得贴着玩家推挤。
    ///
    /// 地图数据和代码里都没有这个值 —— 是我们定的。取 12 大约是三四个身位。
    /// </summary>
    public float FollowDistance { get; set; } = 12f;

    /// <summary>
    /// 掉队超过这个距离就不再管敌人, 先归队。
    ///
    /// 没有它的话 Hunt 状态的小兵会被一路上的敌人牵着走, 越离越远。
    /// </summary>
    public float LeashDistance { get; set; } = 70f;

    /// <summary>Hunt 状态下主动找敌人的半径。比僵尸的仇恨半径小 —— 它是护卫不是猎犬。</summary>
    public float HuntRadius { get; set; } = 35f;

    /// <summary>
    /// 攻击距离基准, 和 <see cref="ZombieBrain"/> 同源:
    /// 技能数据里的 <c>StopDistance</c>。
    /// </summary>
    public float AttackStopDistance { get; set; } = 5f;

    /// <summary>诊断: 当前有几个召唤物在场。</summary>
    public int LastActive { get; private set; }

    public void Update(GameManager simulation)
    {
        if (simulation?.ActiveGameObjectList == null)
        {
            return;
        }

        List<GameObjectBase> objects = simulation.ActiveGameObjectList;
        int active = 0;

        // 下标遍历: 攻击会往列表里塞新的判定对象, foreach 会抛。
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not InfestedMinion minion)
            {
                continue;
            }

            if (!minion.IsActive || minion.IsDead)
            {
                continue;
            }

            active++;
            Steer(simulation, minion);
        }

        LastActive = active;
    }

    private void Steer(GameManager simulation, InfestedMinion minion)
    {
        Player owner = minion.PetOwner;

        // 主人没了 (断线/换人) 就原地待着, 别去追一个空引用。
        // UseGoalTarget = true 且 GoalTarget == null 会让 NPC.SerializeCharacter
        // (:818) 直接 NRE —— 那行没有空判。
        if (owner == null || !owner.IsActive)
        {
            Idle(minion);
            return;
        }

        switch (minion.CurrentState)
        {
            case InfestedMinion._0023cZ._00239Y:      // KillTarget
                if (TryChaseCommandedTarget(simulation, minion))
                {
                    return;
                }
                // 目标死了或没了 -> 退回跟随, 而不是杵在原地
                Follow(minion, owner);
                return;

            case InfestedMinion._0023cZ._0023aZ:      // MoveTo
            case InfestedMinion._0023cZ._0023bZ:      // MoveAndHold
                // 这两个走 GoalPosition, 由指令技能设好。我们只要确保不去追目标。
                minion.UseGoalTarget = false;
                minion.GoalTarget = null;
                return;

            case InfestedMinion._0023cZ._0023MY:      // FollowOwner —— 只跟随, 不出击
                Follow(minion, owner);
                return;

            default:                                   // _00238Y Hunt
                Hunt(simulation, minion, owner);
                return;
        }
    }

    /// <summary>
    /// Hunt: 跟着主人, 顺手打附近的敌人。
    ///
    /// 掉队太远时**先归队** —— 否则小兵会被沿途的敌人一个接一个牵走。
    /// </summary>
    private void Hunt(GameManager simulation, InfestedMinion minion, Player owner)
    {
        float toOwner = Vector2.Distance(minion.Position, owner.Position);
        if (toOwner > LeashDistance)
        {
            Follow(minion, owner);
            return;
        }

        Entity enemy = NearestEnemy(simulation, minion, HuntRadius);
        if (enemy == null)
        {
            Follow(minion, owner);
            return;
        }

        minion.GoalTarget = enemy;
        minion.UseGoalTarget = true;
        TryAttack(minion, enemy);
    }

    private bool TryChaseCommandedTarget(GameManager simulation, InfestedMinion minion)
    {
        if (minion.TargetIndex == -1)
        {
            return false;
        }

        GameObjectBase target = simulation.GetGameObject(minion.TargetIndex);
        if (target is not { IsActive: true, IsEntity: true })
        {
            return false;
        }

        Entity entity = target.AsEntity;
        if (entity.IsDead)
        {
            return false;
        }

        minion.GoalTarget = entity;
        minion.UseGoalTarget = true;
        TryAttack(minion, entity);
        return true;
    }

    /// <summary>
    /// 跟随主人。
    ///
    /// 近到 <see cref="FollowDistance"/> 之内就松手 —— 一直把主人当 GoalTarget
    /// 的话小兵会顶着玩家走, 观感很差而且会推挤碰撞体。
    /// </summary>
    private void Follow(InfestedMinion minion, Player owner)
    {
        if (Vector2.Distance(minion.Position, owner.Position) <= FollowDistance)
        {
            Idle(minion);
            return;
        }

        minion.GoalTarget = owner;
        minion.UseGoalTarget = true;
    }

    /// <summary>
    /// 停下。两个字段必须一起清 —— 只清一个就是下一次序列化 NRE。
    /// </summary>
    private static void Idle(InfestedMinion minion)
    {
        minion.UseGoalTarget = false;
        minion.GoalTarget = null;
    }

    /// <summary>
    /// 最近的敌对目标。
    ///
    /// 判据用游戏自己的 <c>Entity.IsEnemy</c> —— 不要自己比 TeamId, 那会漏掉
    /// 中立、魅惑等情况。<c>IsNonTargetable</c> 覆盖隐身/无敌类状态。
    /// </summary>
    private static Entity NearestEnemy(GameManager simulation, InfestedMinion minion, float radius)
    {
        Entity best = null;
        float bestDistanceSquared = radius * radius;

        List<GameObjectBase> objects = simulation.ActiveGameObjectList;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not Character candidate || candidate == minion)
            {
                continue;
            }

            if (!candidate.IsActive || candidate.IsDead)
            {
                continue;
            }

            if (!minion.IsEnemy(candidate) || candidate.IsNonTargetable())
            {
                continue;
            }

            float distanceSquared = Vector2.DistanceSquared(minion.Position, candidate.Position);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// 够近就放基础攻击。和 <see cref="ZombieBrain"/> 同一套:
    /// 用 <c>TauntAbilityIdentifier</c> (InfestedMinion 设的是
    /// <c>InfestedMinionStrike</c>, InfestedMinion.cs:143), 冷却和消耗
    /// 交给 <c>AbilityBar.TryUseAbility</c> 自己判。
    /// </summary>
    private void TryAttack(InfestedMinion minion, Entity target)
    {
        if (minion.IsPerformingAnAttack)
        {
            return;
        }

        Vector2 toTarget = target.Position - minion.Position;
        float reach = AttackStopDistance + minion.Radius + target.Radius;
        if (toTarget.LengthSquared() > reach * reach)
        {
            return;
        }

        if (toTarget != Vector2.Zero)
        {
            toTarget.Normalize();
        }

        minion.ChangeAimDirection(toTarget);
        minion.Attack(toTarget, minion.TauntAbilityIdentifier);
    }
}
