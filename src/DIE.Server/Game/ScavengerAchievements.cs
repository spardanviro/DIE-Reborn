using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;

namespace DIE.Server.Game;

/// <summary>
/// 拾荒的 Steam 成就。
///
/// ## 原厂留下的
///
/// 服务端发 <c>GameMessageType.Achievement</c> + 编号, 客户端调 Steam 解锁 (GameManagerClient.cs:486)。
/// 十个编号 (<c>GameManager._0023shd</c>) 里拾荒的七个只有名字, 判定条件随服务端丢了:
///
///   _0023ihd CapPoints5   _0023jhd QuickScavenger   _0023khd ImmortalScavenger   _0023nhd Advantage200
///   _0023ohd BarricadeRacer   _0023phd ControlledPoints4   _0023qhd Deliver150
///
/// ## 条件: 按名字定的, 都是一局之内
///
///   BarricadeRacer    路障竞速第一 (冲出去那一刻, 全队)
///   ControlledPoints4 本队同时握着 <see cref="ControlledPoints"/> 个补给点 (那一刻在场的全队)
///   CapPoints5        一局亲手参与占下 <see cref="CapturedPoints"/> 个补给点 (计分板"占领补给点"的次数)
///   Deliver150        一局交了 <see cref="DeliveredSupplies"/> 补给 (计分板"交补给"分 / 每点分值)
///   QuickScavenger    <see cref="QuickWinSeconds"/> 秒内赢下一局
///   ImmortalScavenger 赢下一局且自己一次没死
///   Advantage200      赢的时候已交补给比第二名多 <see cref="AdvantageSupplies"/>
///
/// 后四个 (以及 CapPoints5) 在分出胜负时判; 每个人每局每个成就只发一次。Steam 那边重复解锁是无害的。
/// </summary>
public sealed class ScavengerAchievements
{
    public const int ControlledPoints = 4;
    public const int CapturedPoints = 5;
    public const int DeliveredSupplies = 150;
    public const float QuickWinSeconds = 20f * 60f;
    public const int AdvantageSupplies = 200;

    private readonly HashSet<(Player Player, GameManager._0023shd Id)> _sent = new HashSet<(Player, GameManager._0023shd)>();

    public readonly List<string> Events = new List<string>();

    /// <summary>发过的 (玩家, 成就)。测试 / 诊断用。</summary>
    public IReadOnlyCollection<(Player Player, GameManager._0023shd Id)> Sent => _sent;

    public void OnBarricadeRaceWon(GameManager simulation, TeamId team)
    {
        foreach (Player player in TeamPlayers(simulation, team))
        {
            Unlock(simulation, player, GameManager._0023shd._0023ohd);
        }
    }

    /// <summary>每帧: 哪队同时握着足够多的补给点。</summary>
    public void WatchControlledPoints(GameManager simulation, ScavengerStorageRooms rooms, IEnumerable<TeamId> teams)
    {
        foreach (TeamId team in teams)
        {
            if (rooms.OwnedBy(team) < ControlledPoints)
            {
                continue;
            }

            foreach (Player player in TeamPlayers(simulation, team))
            {
                Unlock(simulation, player, GameManager._0023shd._0023phd);
            }
        }
    }

    /// <summary>分出胜负那一刻 (认输的局不算)。</summary>
    public void OnMatchWon(GameManager simulation, ScavengerHunt hunt, TeamId winner, IEnumerable<TeamId> teams)
    {
        if (simulation == null || hunt == null || hunt.WasConceded)
        {
            return;
        }

        int perSupply = Math.Max(1, GameManager.BalanceData._0023j7f._0023zrh);
        int perCapture = Math.Max(1, ScavengerScores.CaptureSupplyPoint);
        foreach (Player player in TeamPlayers(simulation, null))
        {
            if (player.PlayerScoreStats._0023Ucj[(int)_00230k._0023Kcj._0023qkf] / perCapture >= CapturedPoints)
            {
                Unlock(simulation, player, GameManager._0023shd._0023ihd);
            }

            if (player.PlayerScoreStats._0023Ucj[(int)_00230k._0023Kcj._0023ajc] / perSupply >= DeliveredSupplies)
            {
                Unlock(simulation, player, GameManager._0023shd._0023qhd);
            }
        }

        if (winner == TeamId.Neutral)
        {
            return;
        }

        int winnerDelivered = hunt.SupplyData._0023Rj(winner);
        int runnerUp = teams.Where(t => t != winner).Select(t => hunt.SupplyData._0023Rj(t)).DefaultIfEmpty(0).Max();
        foreach (Player player in TeamPlayers(simulation, winner))
        {
            if (hunt.MatchLength <= QuickWinSeconds)
            {
                Unlock(simulation, player, GameManager._0023shd._0023jhd);
            }

            if (player.PlayerScoreStats._0023m9b == 0)
            {
                Unlock(simulation, player, GameManager._0023shd._0023khd);
            }

            if (winnerDelivered - runnerUp >= AdvantageSupplies)
            {
                Unlock(simulation, player, GameManager._0023shd._0023nhd);
            }
        }
    }

    private void Unlock(GameManager simulation, Player player, GameManager._0023shd id)
    {
        if (!_sent.Add((player, id)))
        {
            return;
        }

        bool sent = simulation is GameManagerServer server && server.SendAchievement(player, id);
        Events.Add($"拾荒成就 {NameOf(id)} -> {player.TeamId} 玩家 {player.IndexPlayer}" + (sent ? string.Empty : " (没有客户端, 未发)"));
    }

    private static IEnumerable<Player> TeamPlayers(GameManager simulation, TeamId? team) =>
        simulation.ActivePlayerList.Where(p => p != null && p.IsActive && (team == null || p.TeamId == team)).ToList();

    public static string NameOf(GameManager._0023shd id) => id switch
    {
        GameManager._0023shd._0023ihd => "CapPoints5",
        GameManager._0023shd._0023jhd => "QuickScavenger",
        GameManager._0023shd._0023khd => "ImmortalScavenger",
        GameManager._0023shd._0023nhd => "Advantage200",
        GameManager._0023shd._0023ohd => "BarricadeRacer",
        GameManager._0023shd._0023phd => "ControlledPoints4",
        GameManager._0023shd._0023qhd => "Deliver150",
        _ => id.ToString(),
    };
}
