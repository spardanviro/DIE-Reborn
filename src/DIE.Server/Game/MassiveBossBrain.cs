using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 三只**巨型 BOSS** (Puller / Ram / Screamer Massive) 的出招。
///
/// ## 玩家实测 (2026-09-14)
///
/// "把巨型 boss 技能完全补全。"
///
/// ## 原厂剩下什么
///
/// 三个类 (<c>_002350</c> / <c>_00239Z</c> / <c>_002302</c>) 各装 7~10 招, 每一招的**判定体都完好**
/// (冲锋自己撞墙自晕、掀屋顶自己砸落石、钩子自己飞 ...)。丢的是"什么时候放哪一招"那一层 ——
/// 类里的每帧钩子 <c>_00239N</c> 只剩"朝向目标" (Screamer) 或干脆是空的 (Ram)。原厂那几招冷却
/// 只有 0.1~1 秒 (Burst 0.1、RockThrow 0.4、Hook 1), 说明节奏本来由 BOSS 脚本控, 不靠冷却。
///
/// 所以这个文件是**我们写的决策层** (节奏、距离、什么情况放什么全是我们定的), 用的全是原厂的招。
///
/// ## 招要读 BOSS 身上的几个字段, 得先喂好
///
///   Screamer  <c>_00235Z</c> = 目标 IndexGlobal (瘟疫弹 -EF/-a3.cs:73 按它追; 每帧钩子也按它转身,
///             默认 0 = 朝着 0 号对象转)、<c>TargetPosition</c> (活体炸弹落点 -SL/-Q2.cs:97)
///   Ram       <c>_00235Z</c> / <c>TargetPosition</c> (掀屋顶的指示圈 -Sq/-a2.cs:121)
///   Puller    <c>_002340</c> = 目标实体 (轰炸 -EF/-qL.cs:50 按它跟)
///   全部      <c>AimDirection</c> / <c>AimLength</c> (手雷落点 -SL/-J2.cs:95、钩子/吐酸方向) —— 走
///             <c>NPC.Attack(方向, 技能, 距离)</c>
///
/// ## 召唤类只有特效
///
/// <c>MassivePullerBossSpawnMinions</c> (-EF/-60.cs)、<c>MassivePullerBossCryForHelp</c> (-EF/-v0.cs)、
/// <c>MassiveScreamerSpawnScream</c> (-Sq/-j3.cs) 的判定体只播一个特效 1 秒就销毁 —— 刷小怪那一段在
/// 服务端, 丢了。这里补: 召唤 = 身边刷几只行尸冲着目标去, 呼救 = 叫附近的僵尸都来。**刷什么、刷几只是我们定的。**
/// (活体炸弹 <c>MassiveScreamerLivingBomb</c> 自己会刷腐化行尸, -EF/-P2.cs:108, 不用补。)
/// </summary>
public sealed class MassiveBossBrain
{
    /// <summary>一招的出招条件。距离按圆心到圆心。</summary>
    private sealed record Move(
        AbilityIdentifier Ability,
        float MinRange,
        float MaxRange,
        float Interval,
        bool Melee = false,
        Func<NPC, Entity, bool> Gate = null,
        Action<MassiveBossBrain, GameManager, NPC, Entity> After = null);

    private sealed class State
    {
        public readonly Dictionary<AbilityIdentifier, float> LastUse = new Dictionary<AbilityIdentifier, float>();
        public float LastBigMove = float.MinValue;
        public bool Roared;
        public Entity LastTarget;
    }

    /// <summary>两招大招 (非近战) 之间至少隔多久 —— 不然一个技能接一个技能没有喘息。**我们定的。**</summary>
    public float BigMoveGap { get; set; } = 2.5f;

    /// <summary>召唤一次刷几只行尸。**我们定的。**</summary>
    public int MinionsPerSummon { get; set; } = 4;

    /// <summary>呼救叫多远的僵尸。**我们定的。**</summary>
    public float CryForHelpRadius { get; set; } = 400f;

    /// <summary>诊断: 放了几招 (按技能)。</summary>
    public readonly Dictionary<AbilityIdentifier, int> Casts = new Dictionary<AbilityIdentifier, int>();

    /// <summary>诊断: 召唤刷出来几只。</summary>
    public int MinionsSummoned { get; private set; }

    /// <summary>这一帧刷出来 / 叫来的僵尸 —— 宿主 (ZombieBrain) 负责激怒它们冲着目标去。</summary>
    public readonly List<(Character Zombie, Entity Target)> Rallied = new List<(Character, Entity)>();

    private readonly Dictionary<(int Index, int Uses), State> _states = new Dictionary<(int, int), State>();

    private static readonly Dictionary<Type, Move[]> Moves = BuildMoves();

    /// <summary>这是不是巨型 BOSS (归这里管)。</summary>
    public static bool Handles(NPC npc) =>
        npc is _0023Mk._002350 or _0023Mk._00239Z or _0023Mk._002302;

    /// <summary>
    /// 这一帧的出招。
    /// </summary>
    /// <returns>true = 出手了 (或者正在施法, 这一帧别再干别的)。</returns>
    public bool Act(GameManager simulation, NPC boss, Entity target, float totalTime)
    {
        if (!Moves.TryGetValue(boss.GetType(), out Move[] moves))
        {
            return false;
        }

        State state = StateOf(boss);
        FeedTarget(boss, target);

        if (boss.IsPerformingAnAttack || boss.IsChanneling || boss.IsCasting)
        {
            return true;
        }

        // 目标刚被打死: Ram 放胜利吼 (原厂那招就是原地站着吼一会儿, -Sq/-n2.cs)
        if (state.LastTarget != null && !ReferenceEquals(state.LastTarget, target)
            && state.LastTarget.IsDead && boss is _0023Mk._00239Z
            && TryCast(simulation, boss, target, AbilityIdentifier.MassiveRamVictory, state, totalTime, 0f))
        {
            state.LastTarget = target;
            return true;
        }

        state.LastTarget = target;

        // 第一次盯上人: 先吼一嗓子 (Ram 的吼 / Puller 的无特效吼 / Screamer 的开场尖叫)
        if (!state.Roared)
        {
            state.Roared = true;
            AbilityIdentifier opener = boss switch
            {
                _0023Mk._00239Z => AbilityIdentifier.MassiveRamRoar,
                _0023Mk._002350 => AbilityIdentifier.MassivePullerNoEffectRoar,
                _ => AbilityIdentifier.MassiveScreamerSpawnScream,
            };

            if (TryCast(simulation, boss, target, opener, state, totalTime, 0f))
            {
                state.LastBigMove = totalTime;
                AfterOpener(simulation, boss, target, opener);
                return true;
            }
        }

        float distance = Vector2.Distance(boss.Position, target.Position);
        List<Move> ready = new List<Move>();

        foreach (Move move in moves)
        {
            if (distance < move.MinRange || distance > move.MaxRange + target.Radius)
            {
                continue;
            }

            if (!move.Melee && totalTime - state.LastBigMove < BigMoveGap)
            {
                continue;
            }

            if (state.LastUse.TryGetValue(move.Ability, out float last) && totalTime - last < move.Interval)
            {
                continue;
            }

            if (move.Gate != null && !move.Gate(boss, target))
            {
                continue;
            }

            Ability ability = boss.AbilityBar.GetAbility(move.Ability);
            if (ability == null || !ability.CanUse())
            {
                continue;
            }

            if (!move.Melee && !ZombieBrain.CanSee(boss, target))
            {
                continue;
            }

            ready.Add(move);
        }

        if (ready.Count == 0)
        {
            return false;
        }

        // 大招优先 (有大招就不平 A); 大招里挑**最久没放过**的 (没放过的最先), 一样久的随机 ——
        // 纯随机的话距离窗口窄的招 (轰炸 60~150) 老是被同距离的别的招抢掉, 一整场都轮不到
        List<Move> big = ready.FindAll(m => !m.Melee);
        List<Move> pool = big.Count > 0 ? big : ready;
        float oldest = pool.Min(m => LastUseOf(state, m.Ability));
        List<Move> stalest = pool.FindAll(m => LastUseOf(state, m.Ability) <= oldest);
        Move chosen = stalest[simulation.Random._0023Otc(0, stalest.Count)];

        if (!TryCast(simulation, boss, target, chosen.Ability, state, totalTime, distance))
        {
            return false;
        }

        if (!chosen.Melee)
        {
            state.LastBigMove = totalTime;
        }

        chosen.After?.Invoke(this, simulation, boss, target);
        return true;
    }

    private static float LastUseOf(State state, AbilityIdentifier ability) =>
        state.LastUse.TryGetValue(ability, out float last) ? last : float.MinValue;

    /// <summary>这一帧没出现在活着的名单里的, 全部忘掉 (见 ZombieBrain.ForgetMissing)。</summary>
    public void ForgetMissing(ISet<(int Index, int Uses)> alive)
    {
        foreach ((int, int) key in _states.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _states.Remove(key);
        }
    }

    /// <summary>BOSS 死了 / 回池 —— 忘掉它的节奏。</summary>
    public void Forget(NPC boss)
    {
        if (boss != null)
        {
            _states.Remove((boss.IndexGlobal, boss.NumberOfGlobalCacheUses));
        }
    }

    // ------------------------------------------------------------------ 出招表

    private static Dictionary<Type, Move[]> BuildMoves()
    {
        bool Hurt(NPC boss, float fraction) => boss.MaxHealth > 0f && boss.Health / boss.MaxHealth <= fraction;

        return new Dictionary<Type, Move[]>
        {
            // ---------------------------------------------------------------- Puller 巨型 (-Mk/-50.cs)
            [typeof(_0023Mk._002350)] = new[]
            {
                new Move(AbilityIdentifier.MassivePullerBossStrike, 0f, 28f, 0f, Melee: true),     // 锥形 28 (-xL/-e1.cs)
                new Move(AbilityIdentifier.MassivePullerBossHook, 50f, 2.3f * 150f, 9f),           // 钩子 2.3 秒 × 150 (-xL/-V0.cs)
                new Move(AbilityIdentifier.MassivePullerBossSpitChannel, 30f, 1f * 120f, 7f),      // 吐酸 1 × 120 (-xL/-a1.cs)
                new Move(AbilityIdentifier.MassivePullerBossCrawl, 60f, 160f, 10f),                // 爬到瞄准点 (位移 = AimLength, -xL/-u0.cs)
                new Move(AbilityIdentifier.MassivePullerBossCocoon, 40f, 0.9f * 190f, 16f),        // 结茧弹 0.9 × 190 (-xL/-l0.cs)
                new Move(AbilityIdentifier.MassivePullerBossAcidSlime, 0f, 120f, 14f),             // 身边 300 内的玩家挂酸 (-EF/-f0.cs)
                new Move(AbilityIdentifier.MassivePullerBossBombardment, 60f, 150f, 18f),          // 轰炸, 射程 _0023nH=150 (-xL/-wL.cs)
                new Move(AbilityIdentifier.MassivePullerBossSpawnMinions, 0f, 400f, 28f,
                    After: (brain, sim, boss, target) => brain.Summon(sim, boss, target)),
                new Move(AbilityIdentifier.MassivePullerBossCryForHelp, 0f, 400f, 40f,
                    Gate: (boss, _) => Hurt(boss, 0.5f),
                    After: (brain, sim, boss, target) => brain.CryForHelp(sim, boss, target)),
            },

            // ---------------------------------------------------------------- Ram 巨型 (-Mk/-9Z.cs)
            [typeof(_0023Mk._00239Z)] = new[]
            {
                new Move(AbilityIdentifier.MassiveRamBitchSlap, 0f, 30f, 0f, Melee: true),         // 30 / 120° (-EL/-vZ.cs 类 _0023vZ)
                new Move(AbilityIdentifier.MassiveRamBurst, 0f, 55f, 6f),                          // 短冲 55 (-EL/-y1.cs)
                new Move(AbilityIdentifier.MassiveRamGroundStomp, 0f, 40f, 14f),                   // 震地 40 (-EL/-vZ.cs 类 _0023VZ)
                new Move(AbilityIdentifier.MassiveRamRush, 70f, 150f, 8f),                         // 冲撞 150 (-EL/-i2.cs)
                new Move(AbilityIdentifier.MassiveRamPickupBounce, 50f, 180f, 14f),                // 抓人冲撞 180 (-EL/-i1.cs)
                new Move(AbilityIdentifier.MassiveRamRockThrow, 70f, 260f, 7f),                    // 扔石头 (-EL/-G1.cs)
                new Move(AbilityIdentifier.MassiveRamRoofDown, 0f, 250f, 35f,                      // 掀屋顶砸落石 (-Sq/-a2.cs, 范围 _0023Vb=250)
                    Gate: (boss, _) => Hurt(boss, 0.6f)),
                new Move(AbilityIdentifier.MassiveRamNoEffectRoar, 0f, 250f, 30f),
            },

            // ---------------------------------------------------------------- Screamer 巨型 (-Mk/-02.cs)
            [typeof(_0023Mk._002302)] = new[]
            {
                new Move(AbilityIdentifier.MassiveScreamerSlap, 0f, 24f, 0f, Melee: true),         // 24 / 120° (-SL/-i3.cs)
                new Move(AbilityIdentifier.MassiveScreamerGrenade, 30f, 110f, 5f),                 // 手雷落在瞄准点 (-SL/-J2.cs)
                new Move(AbilityIdentifier.MassiveScreamerPlagueProjectile, 50f, 250f, 7f),        // 追踪瘟疫弹 (-EF/-a3.cs)
                new Move(AbilityIdentifier.MassiveScreamerLivingBomb, 50f, 250f, 12f),             // 活体炸弹, 落点 TargetPosition (-SL/-Q2.cs)
                new Move(AbilityIdentifier.MassiveScreamerFearAura, 0f, 120f, 22f),                // 恐惧光环 200 (-SL/-y2.cs)
                new Move(AbilityIdentifier.MassiveScreamerMassiveFear, 0f, 250f, 22f,              // 大恐惧 250 (-SL/-RL.cs)
                    Gate: (boss, _) => Hurt(boss, 0.7f)),
                new Move(AbilityIdentifier.MassiveScreamerSpawnScream, 0f, 400f, 28f,
                    After: (brain, sim, boss, target) => brain.Summon(sim, boss, target)),
            },
        };
    }

    // ------------------------------------------------------------------ 干活

    private State StateOf(NPC boss)
    {
        (int, int) key = (boss.IndexGlobal, boss.NumberOfGlobalCacheUses);
        if (!_states.TryGetValue(key, out State state))
        {
            state = new State();
            _states[key] = state;
        }

        return state;
    }

    private static void FeedTarget(NPC boss, Entity target)
    {
        boss.TargetPosition = target.Position;

        switch (boss)
        {
            case _0023Mk._002302 screamer:
                screamer._00235Z = target.IndexGlobal;
                break;
            case _0023Mk._00239Z ram:
                ram._00235Z = target.IndexGlobal;
                break;
            case _0023Mk._002350 puller:
                puller._002340 = target;
                break;
        }
    }

    private bool TryCast(GameManager simulation, NPC boss, Entity target, AbilityIdentifier id, State state,
                         float totalTime, float distance)
    {
        Ability ability = boss.AbilityBar.GetAbility(id);
        if (ability == null || !ability.CanUse() || ZombieBrain.IsDirectionLocked(boss))
        {
            return false;
        }

        Vector2 aim = target.Position - boss.Position;
        if (distance <= 0f)
        {
            distance = aim.Length();
        }

        if (aim != Vector2.Zero)
        {
            aim.Normalize();
        }

        ZombieBrain.AimNow(boss, aim);      // 出手方向读当前朝向, 见 ZombieBrain.AimNow
        if (!boss.Attack(aim, id, distance))
        {
            return false;
        }

        state.LastUse[id] = totalTime;
        Casts[id] = Casts.TryGetValue(id, out int n) ? n + 1 : 1;
        return true;
    }

    private void AfterOpener(GameManager simulation, NPC boss, Entity target, AbilityIdentifier opener)
    {
        if (opener == AbilityIdentifier.MassiveScreamerSpawnScream)
        {
            Summon(simulation, boss, target);
        }
    }

    /// <summary>召唤: 身边一圈刷几只行尸, 冲着目标去。</summary>
    private void Summon(GameManager simulation, NPC boss, Entity target)
    {
        Type walker = CrossroadEventCatalog.ResolveClass("_0023rPb");

        for (int i = 0; i < MinionsPerSummon; i++)
        {
            double angle = Math.PI * 2.0 * i / MinionsPerSummon;
            Vector2 at = boss.Position + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * (boss.Radius + 20f);
            Vector2 facing = target.Position - at;
            if (facing != Vector2.Zero)
            {
                facing.Normalize();
            }

            Character minion = ZombieFactory.Spawn(simulation, walker, at, _0023Oq._0023WM._0023Llc, facing);
            if (minion == null)
            {
                continue;
            }

            MinionsSummoned++;
            Rallied.Add((minion, target));
        }
    }

    /// <summary>呼救: 附近活着的僵尸都冲着目标来。</summary>
    private void CryForHelp(GameManager simulation, NPC boss, Entity target)
    {
        float radiusSquared = CryForHelpRadius * CryForHelpRadius;
        List<GameObjectBase> objects = simulation.ActiveGameObjectList;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is Character zombie && zombie.IsZombie && zombie.IsActive && !zombie.IsDead
                && !ReferenceEquals(zombie, boss)
                && Vector2.DistanceSquared(zombie.Position, boss.Position) <= radiusSquared)
            {
                Rallied.Add((zombie, target));
            }
        }
    }
}
