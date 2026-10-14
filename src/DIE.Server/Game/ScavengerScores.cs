using System;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 拾荒计分板上的得分 (<c>PlayerScoreStats._0023Xrh</c> + 头顶 "+N" 飘字)。
///
/// ## 原厂留下的
///
/// 拾荒计分板按来源列分 (UI_ScoreboardScavengerBinding.GetScoreString), 十个来源 <c>_0023Kcj</c>:
///
///   _0023Qrh 击杀玩家        _0023Req 击杀高等级玩家    _0023Seq 击杀低等级玩家
///   _0023Rrh 守补给点        _0023qkf 占领补给点
///   _0023ajc 交补给          _0023fdb 偷补给            _0023pAc 升级
///   _0023Srh 击杀 Looter     _0023Trh 击杀囤积者
///
/// 交补给 / 偷补给 (每点 <c>_0023zrh</c> = 2, -Sq/-ajc.cs:249、-Sq/-fdb.cs) 和升级 (<c>_0023Drh</c> = 50, Player.cs:2093)
/// 共享代码自己记。其余七个是服务端记的, 随服务端丢了。
///
/// ## 分值: 原厂的数, 对应关系是推断
///
/// 平衡数据 <c>_0023j7f</c> 开头正好十个整数, 客户端只读了其中三个 (zrh / Drh 和没人读的 Arh 同为 2):
///
///   xrh 100  yrh 100  zrh 2  Arh 2  Brh 100  Crh 300  Drh 50  Peq 25  Grh 75  Qeq 125
///
/// 十个数对十个来源。已确证: zrh = 交/偷补给每点, Drh = 升级。其余按大小和成对的命名推断 ——
/// **击杀玩家 100 / 守点 100 / 占点 100 / 囤积者 300 / Looter 75 / 高等级 125 / 低等级 25**。
///
/// ## 触发时机: 我们定的
///
///   击杀玩家   玩家打死别队玩家; 死者等级比杀手高 = 高等级, 低 = 低等级, 一样 = 普通
///   占领补给点 占下那一刻在圈里的占领方玩家
///   守补给点   别队占领被打断 (进度退回 0) 那一刻, 圈里的持有方玩家
///   囤积者 / Looter  最后一击的玩家
/// </summary>
public static class ScavengerScores
{
    public static int Takedown => GameManager.BalanceData._0023j7f._0023xrh;

    public static int DefendSupplyPoint => GameManager.BalanceData._0023j7f._0023yrh;

    public static int CaptureSupplyPoint => GameManager.BalanceData._0023j7f._0023Brh;

    public static int HoarderKill => GameManager.BalanceData._0023j7f._0023Crh;

    public static int LooterKill => GameManager.BalanceData._0023j7f._0023Grh;

    public static int TakedownHighLevel => GameManager.BalanceData._0023j7f._0023Qeq;

    public static int TakedownLowLevel => GameManager.BalanceData._0023j7f._0023Peq;

    /// <summary>记一笔: 计分板统计 + 只发给得分者的飘字。</summary>
    public static void Award(GameManager simulation, Player player, _00230k._0023Kcj source, int value, Vector2 at)
    {
        if (simulation == null || player == null || value <= 0)
        {
            return;
        }

        player.PlayerScoreStats._0023Xrh((short)Math.Min(value, short.MaxValue), source);
        simulation.SendPlayerScoreData(source, (ushort)Math.Min(value, ushort.MaxValue), at, player);
    }

    /// <summary>击杀玩家算哪一档, 以及多少分。</summary>
    public static (_00230k._0023Kcj Source, int Value) TakedownOf(Player killer, Player victim)
    {
        int killerLevel = killer.GetLevel();
        int victimLevel = victim.GetLevel();
        if (victimLevel > killerLevel)
        {
            return (_00230k._0023Kcj._0023Req, TakedownHighLevel);
        }

        if (victimLevel < killerLevel)
        {
            return (_00230k._0023Kcj._0023Seq, TakedownLowLevel);
        }

        return (_00230k._0023Kcj._0023Qrh, Takedown);
    }

    /// <summary>
    /// 一只角色死了, 拾荒里该给谁记分 (宿主在死亡汇合点调)。
    /// </summary>
    /// <param name="killerPlayer">最后一击归属的玩家 (顺着 Owner 链找到的); 没有传 null。</param>
    public static void OnCharacterDied(GameManager simulation, Character died, Player killerPlayer)
    {
        if (simulation?.GameMode is not { IsScavengerHunt: true } mode || mode.IsCompleted
            || died == null || killerPlayer == null)
        {
            return;
        }

        if (died is Player victim)
        {
            if (victim.TeamId == killerPlayer.TeamId || victim == killerPlayer)
            {
                return;
            }

            (_00230k._0023Kcj source, int value) = TakedownOf(killerPlayer, victim);
            Award(simulation, killerPlayer, source, value, victim.Position);
            return;
        }

        if (died.IsLooter)
        {
            Award(simulation, killerPlayer, _00230k._0023Kcj._0023Srh, LooterKill, died.Position);
        }
        else if (died.IsHoarderZombie)
        {
            Award(simulation, killerPlayer, _00230k._0023Kcj._0023Trh, HoarderKill, died.Position);
        }
    }
}
