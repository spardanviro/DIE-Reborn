using System;
using System.Collections.Generic;
using System.Reflection;
using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 诊断: 玩家的技能读条没读完就断了 -&gt; 把当时可能的原因全记下来。
///
/// ## 为什么要它
///
/// 玩家实测 (2026-09-14): "夜袭的时候我发现 armored isys 的 Q 技能放不了, 刚开始引导就被打断了。"
/// Q 是 SentryGun (<c>-Mk/-osb.cs</c> Ability1)。它的原厂数据带三种会打断读条的条件
/// (<c>-hcb/-0Nb.cs</c>): <c>InterruptOnMovement</c> (移动输入变了)、<c>InterruptOnKnockback</c>
/// (身上挂着击退 buff)、<c>HasPlacementCollision</c> (放置点被固定物/路障占着, 读条期间每帧查,
/// Ability.cs:1909)。另外眩晕/锁操作也会打断 (AbilityBar.cs:413)。
///
/// 那一局的服务端日志里只有"引导中 true/false", 分不出是哪一条, 离线也复现不了
/// (放置判定查的动态四叉树, 单元测试夹具里是空的)。所以先把现场记下来, 下一局看日志定位。
///
/// ## 怎么判"没读完"
///
/// 每帧看每个玩家正在读条的技能 (非武器技能)。上一帧还在读、这一帧不读了, 而上一帧的读条进度
/// 不到 <c>CastTime</c> -&gt; 算被打断。现场取的是**断掉后那一帧**, 条件大多还在 (人还站在同一个位置)。
/// </summary>
public sealed class CastInterruptProbe
{
    private sealed class Watch
    {
        public Ability Ability;
        public float Progress;
    }

    private static readonly MethodInfo PlacementClear =
        typeof(Ability).GetMethod("_0023Tgc", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly Dictionary<Player, Watch> _watching = new Dictionary<Player, Watch>();

    /// <summary>这一帧记下的描述, 宿主打日志。</summary>
    public readonly List<string> Reports = new List<string>();

    public void Update(GameManager simulation)
    {
        Reports.Clear();

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive)
            {
                continue;
            }

            Ability casting = player.AbilityBar?.CastingAbility;
            if (casting != null && (!casting.IsCasting || casting.IsWeaponAbility))
            {
                casting = null;
            }

            _watching.TryGetValue(player, out Watch watch);

            if (watch != null && !ReferenceEquals(watch.Ability, casting))
            {
                float needed = watch.Ability.CastTime;
                if (needed > 0f && watch.Progress < needed - 0.05f)
                {
                    Reports.Add(Describe(simulation, player, watch, needed));
                }

                _watching.Remove(player);
                watch = null;
            }

            if (casting == null)
            {
                continue;
            }

            if (watch == null)
            {
                watch = new Watch { Ability = casting };
                _watching[player] = watch;
                Reports.Add($"玩家 #{player.IndexPlayer} 开始读条 {casting.AbilityIdentifier} (需 {casting.CastTime:F2} 秒)");
            }

            watch.Progress = casting.CurrentCastTime;
        }
    }

    private static string Describe(GameManager simulation, Player player, Watch watch, float needed)
    {
        Ability ability = watch.Ability;
        string placement = "不查";
        if (ability.AbilityData.HasPlacementCollision && PlacementClear != null)
        {
            try
            {
                placement = (bool)PlacementClear.Invoke(ability, null) ? "空着" : "被占";
            }
            catch (Exception ex)
            {
                placement = "查失败 " + ex.GetBaseException().GetType().Name;
            }
        }

        Vector2 aimAt = player.Position + player.AimDirection * player.AimLength;

        return $"玩家 #{player.IndexPlayer} 读条被打断: {ability.AbilityIdentifier} {watch.Progress:F2}/{needed:F2} 秒 | "
               + $"走打断接口={player.AbilityBar.CastWasInterrupted} 眩晕={player.IsStunned} 锁操作={player.IsGamePlayLocked} "
               + $"击退僵直={player.IsKnockbackStunned} 弱击退={player.IsWeakKnockbackStunned} "
               + $"挂着击退buff={player.HasBuff(_0023Oq._0023aD._0023nE)} 引导中={player.IsChanneling} | "
               + $"放置点 ({aimAt.X:F0},{aimAt.Y:F0}) 离人 {player.AimLength:F0} {placement} 附近固定物: {Nearby(simulation, aimAt)}";
    }

    private static string Nearby(GameManager simulation, Vector2 point)
    {
        List<string> found = new List<string>();

        foreach (Entity entity in simulation.ActiveEntityList)
        {
            if (entity == null || !entity.IsActive || entity.IsDead)
            {
                continue;
            }

            bool relevant = entity.IsBarricade || simulation.TypeInCategories(entity, ObjCat.Stationary);
            if (!relevant)
            {
                continue;
            }

            float distance = Vector2.Distance(entity.Position, point);
            if (distance <= 40f)
            {
                found.Add($"{entity.GetType().Name}@{distance:F0}");
            }
        }

        return found.Count == 0 ? "无" : string.Join(" ", found);
    }
}
