using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using FarseerPhysics.Common;
using _0023Mk;

namespace DIE.Server.Game;

/// <summary>
/// 角色召唤出来的 NPC (除了 Amber 的 InfestedMinion, 它归 <see cref="MinionBrain"/>) 的行为。
///
/// ## 缺的是什么
///
/// 玩家实测 (2026-09-15) 要求把召唤类技能审一遍。全角色探针: 下面四种 NPC 召唤物生成得出来, 但**一动不动、一招不出**
/// —— 和 InfestedMinion 当初一样, 原厂的 AI 在服务端那半 (<c>GameManager.ConnectToAIUnit</c> 是空的)。
/// 召唤物自己的技能、伤害、寿命、特效全都在, 缺的只是"去哪、打谁、什么时候放招":
///
///   XianMei 分身 (<c>_0023E3</c>, Replica / 武器命中分裂)   拿主人的武器, 活几秒 (-EF/-RX.cs:240)
///   Allie 幽灵 (<c>_0023wSe</c>, Stealth 升级)              同上 (-Sq/-CG.cs:150)
///   Rico 的 Marco (<c>_0023grh</c>)                         三个技能: 援护 / 冲刺攻击 / 区域击杀
///   Bryce (变异) 大招的巨型孢子 (<c>_0023dWb</c>)           原地不动, 远程尖刺 + 升级后近身横扫
///
/// ## 哪些是原厂给的, 哪些是我们定的
///
/// 原厂给的 (照读, 不写死): 技能本身、Marco 的指令字段 (Rico 两个技能只负责写字段:
/// 援护目标 <c>_0023crh</c> -EF/-Uqh.cs:40, 击杀区域 <c>_0023erh/_0023drh</c> -EF/-Rqh.cs:57,
/// 领地被动挑的攻击目标 <c>_0023Uvb</c> -Sq/-3Se.cs:67)、Marco 冲刺距离 (<c>_0023d8i._0023RE</c> = 50)、
/// 援护范围 (<c>_0023b8i._00235j</c>)、孢子尖刺射程 (存活 × 弹速)、横扫半径 (<c>_0023u3f._0023FH</c>)、
/// 孢子的横扫要升级 (<c>_00234Vb</c>, -EF/-mzb.cs:216)、Bryce 右键指定的孢子目标 (<c>_00231Y</c>, -Mk/-B3b.cs:96)。
///
/// **我们定的** (原厂 AI 丢了, 无从考证): 各种"多远开始找敌人" (<see cref="HuntRadius"/> 等)、
/// Marco 没有领地被动时的默认目标 (Rico 在战斗中时打他身边最近的敌人)、走到多近再放援护/击杀。
/// </summary>
public sealed class CompanionBrain
{
    /// <summary>分身/幽灵主动找敌人的半径。我们定的, 比僵尸的仇恨半径小一点。</summary>
    public float HuntRadius { get; set; } = 60f;

    /// <summary>跟随时离主人多近就停。我们定的, 同 <see cref="MinionBrain.FollowDistance"/>。</summary>
    public float FollowDistance { get; set; } = 12f;

    /// <summary>Marco 没被指定目标时, 在 Rico 身边多远内找敌人。我们定的。</summary>
    public float MarcoGuardRadius { get; set; } = 70f;

    /// <summary>Marco 走到援护目标/击杀区域多近再放招。我们定的。</summary>
    public float MarcoCommandReach { get; set; } = 20f;

    /// <summary>击杀区域摆下之后, Marco 最多赶路几秒就直接放 (伤害以区域为中心, 不以 Marco 为中心)。我们定的。</summary>
    public float MarcoKillPatience { get; set; } = 1.5f;

    /// <summary>诊断: 上一帧各类召唤物在场几只。</summary>
    public int LastActive { get; private set; }

    /// <summary>诊断: 召唤物放出的技能数 (宿主定期打日志后清零)。</summary>
    public int CastsIssued;

    public void Update(GameManager simulation)
    {
        if (simulation?.ActiveGameObjectList == null)
        {
            return;
        }

        List<GameObjectBase> objects = simulation.ActiveGameObjectList;
        int active = 0;

        // 下标遍历: 放招会往列表里塞新对象
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not NPC npc || !npc.IsActive || npc.IsDead)
            {
                continue;
            }

            switch (npc)
            {
                case _0023E3 replica:
                    active++;
                    SteerWeaponClone(simulation, replica, replica.PetOwner);
                    break;
                case _0023wSe ghost:
                    active++;
                    SteerWeaponClone(simulation, ghost, ghost.PetOwner);
                    break;
                case _0023grh marco:
                    active++;
                    SteerMarco(simulation, marco);
                    break;
                case _0023dWb sprout:
                    active++;
                    SteerSprout(simulation, sprout);
                    break;
            }
        }

        LastActive = active;
    }

    // ---- 分身 / 幽灵: 跟着主人, 打附近的敌人 (用手上武器的主攻击, 槽 0) ----

    private void SteerWeaponClone(GameManager simulation, NPC clone, Player owner)
    {
        Entity enemy = NearestEnemy(simulation, clone, clone.Position, HuntRadius);
        if (enemy == null)
        {
            if (owner != null && owner.IsActive && Vector2.Distance(clone.Position, owner.Position) > FollowDistance)
            {
                Chase(clone, owner);
            }
            else
            {
                Idle(clone);
            }

            return;
        }

        Chase(clone, enemy);
        Ability weapon = clone.AbilityBar.GetAbility(0);
        if (weapon == null)
        {
            return;
        }

        float reach = System.Math.Max(weapon.AbilityData?.StopDistance ?? 0f, 5f) + clone.Radius + enemy.Radius;
        if (!clone.IsPerformingAnAttack)
        {
            TryCast(clone, enemy.Position, weapon.AbilityIdentifier, reach);
        }
    }

    // ---- Marco ----

    private void SteerMarco(GameManager simulation, _0023grh marco)
    {
        Player rico = marco.PetOwner;
        if (rico == null || !rico.IsActive || rico.IsDead)
        {
            Idle(marco);
            return;
        }

        // 正在区域击杀 (模型藏起来、速度 0, -EF/-jrh.cs:85) —— 别动它
        if (marco._0023frh)
        {
            Idle(marco);
            return;
        }

        if (marco._0023erh != Vector2.Zero)
        {
            if (marco._0023drh == null)
            {
                // 区域标记已经过期了还没打出去, 作废 (原厂只在击杀结束时清, -EF/-jrh.cs:170)
                marco._0023erh = Vector2.Zero;
            }
            else
            {
                MoveTo(marco, marco._0023erh);
                bool arrived = Vector2.Distance(marco.Position, marco._0023erh) <= MarcoCommandReach;
                if (arrived || marco._0023drh.Age >= MarcoKillPatience)
                {
                    TryCast(marco, marco._0023erh, AbilityIdentifier.MarcoKill, float.MaxValue);
                }

                return;
            }
        }

        if (marco._0023crh != null)
        {
            if (!marco._0023crh.IsActive || marco._0023crh.IsDead)
            {
                marco._0023crh = null;
            }
            else
            {
                Chase(marco, marco._0023crh);
                TryCast(marco, marco._0023crh.Position, AbilityIdentifier.MarcoHelp, MarcoCommandReach);
                return;
            }
        }

        Entity target = marco._0023Uvb;
        if (target == null || !target.IsActive || target.IsDead || !marco.IsEnemy(target))
        {
            target = rico.InCombat ? NearestEnemy(simulation, marco, rico.Position, MarcoGuardRadius) : null;
            marco._0023Uvb = target;
        }

        if (target == null)
        {
            if (Vector2.Distance(marco.Position, rico.Position) > FollowDistance)
            {
                Chase(marco, rico);
            }
            else
            {
                Idle(marco);
            }

            return;
        }

        Chase(marco, target);
        float dash = ConductorGameLogic.GameManager.BalanceData?._0023d8i?._0023RE ?? 50f;
        TryCast(marco, target.Position, AbilityIdentifier.MarcoAttack, dash);
    }

    // ---- 巨型孢子: 原地不动, 远的扎尖刺, 近的横扫 (要升级) ----

    private void SteerSprout(GameManager simulation, _0023dWb sprout)
    {
        // 破土动画放完之前不出手 (服务端同一个判据, -Mk/-dWb.cs:208)
        if (sprout.Age <= sprout._00237Vb || sprout.IsPerformingAnAttack)
        {
            return;
        }

        var balance = ConductorGameLogic.GameManager.BalanceData;
        float spikeRange = balance != null ? balance._0023P3f._0023jE * balance._0023P3f._0023kE : 87.5f;
        float bashRange = balance?._0023u3f._0023FH ?? 26f;

        Entity target = null;
        if (sprout._00231Y != -1
            && simulation.GetGameObject(sprout._00231Y) is Entity commanded
            && commanded.IsActive && !commanded.IsDead && sprout.IsEnemy(commanded))
        {
            target = commanded;
        }

        target ??= NearestEnemy(simulation, sprout, sprout.Position, spikeRange);
        if (target == null)
        {
            return;
        }

        float distance = Vector2.Distance(sprout.Position, target.Position);
        if (sprout._00234Vb && distance <= bashRange + target.Radius
            && TryCast(sprout, target.Position, AbilityIdentifier.SproutUltimateBash, float.MaxValue))
        {
            return;
        }

        TryCast(sprout, target.Position, AbilityIdentifier.SproutUltimateSpike, spikeRange + target.Radius);
    }

    // ---- 公用 ----

    /// <summary>够近就朝目标放招。出手方向读 AimDirection, 所以连它一起写 (同 <see cref="ZombieBrain.AimNow"/>)。</summary>
    private bool TryCast(NPC npc, Vector2 at, AbilityIdentifier ability, float reach)
    {
        Vector2 toTarget = at - npc.Position;
        float distance = toTarget.Length();
        // 不能用 AbilityBar.HasAbility(id): 它的循环 `num > 0` 跳过了 0 号槽 (AbilityBar.cs:521), 而武器主攻击就在 0 号槽
        if (distance > reach || npc.IsPerformingAnAttack || npc.AbilityBar.GetAbility(ability) == null)
        {
            return false;
        }

        Vector2 direction = distance > 0.001f ? toTarget / distance : npc.AimDirection;
        ZombieBrain.AimNow(npc, direction);
        if (!npc.Attack(direction, ability, distance))
        {
            return false;
        }

        CastsIssued++;
        return true;
    }

    private static Entity NearestEnemy(GameManager simulation, NPC self, Vector2 around, float radius)
    {
        Entity best = null;
        float bestDistanceSquared = radius * radius;
        List<GameObjectBase> objects = simulation.ActiveGameObjectList;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not Character candidate || candidate == self || !candidate.IsActive || candidate.IsDead)
            {
                continue;
            }

            if (!self.IsEnemy(candidate) || candidate.IsNonTargetable())
            {
                continue;
            }

            float distanceSquared = Vector2.DistanceSquared(around, candidate.Position);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = candidate;
            }
        }

        return best;
    }

    private static void Chase(NPC npc, Entity target)
    {
        npc.GoalTarget = target;
        npc.UseGoalTarget = true;
    }

    private static void MoveTo(NPC npc, Vector2 position)
    {
        npc.UseGoalTarget = false;
        npc.GoalTarget = null;
        npc.SetGoalPosition(position);
    }

    /// <summary>停下。两个字段一起清, 只清一个下一次序列化会 NRE (见 <see cref="MinionBrain"/>)。</summary>
    private static void Idle(NPC npc)
    {
        npc.UseGoalTarget = false;
        npc.GoalTarget = null;
    }
}
