using System;
using System.Collections.Generic;
using System.Linq;
using DIE.Server.Data;

namespace DIE.Server.Match;

/// <summary>
/// 拾荒 (Scavenger, 4v4v4) 开局分队: 把凑进这一局的几支小队装进三个队伍。
///
/// ## 规则 (我们定的, 原厂的分队在匹配服里, 随服务端丢失)
///
///   1. 同一支小队的人**一定同队** —— 组队排队就是为了一起玩。
///   2. 大的小队先装; 每支装进"剩余空位最多且装得下"的那一队, 平手取编号小的。
///      这样人数尽量均摊, 一个人排进来的永远是 1 队。
///   3. 一局最多 3 队 × 4 人, 由 <see cref="GameModeAvailability.QueueCapacity"/> 保证总人数不超;
///      万一装不下 (不该发生), 塞进人最少的队, 不丢人。
///
/// 自定义房间里每人算一支小队 (房间成员各自连接), 所以会被均摊到三队。
/// </summary>
public static class ScavengerTeams
{
    /// <summary>拾荒的队伍数 (<c>GameModeSettings.NumOfTeams</c> 默认 3, 五张图的 .gms 实测也是 3)。</summary>
    public const int TeamCount = 3;

    /// <summary>每队人数 (<c>GameModeSettings.NumOfPlayersPerTeam</c> 默认 4)。</summary>
    public const int TeamSize = 4;

    /// <summary>
    /// 分队, 返回摊平的名单 (顺序照旧: 小队按传入顺序、队内按原顺序), 每个位子带上 <see cref="MatchSeat.Team"/> (1..3)。
    /// </summary>
    public static List<MatchSeat> Assign(IReadOnlyList<IReadOnlyList<MatchSeat>> parties,
                                         int teamCount = TeamCount, int teamSize = TeamSize)
    {
        if (parties == null)
        {
            return new List<MatchSeat>();
        }

        int[] filled = new int[teamCount];
        byte[] teamOf = new byte[parties.Count];

        IEnumerable<int> biggestFirst = Enumerable.Range(0, parties.Count)
            .OrderByDescending(i => parties[i]?.Count ?? 0)
            .ThenBy(i => i);

        foreach (int i in biggestFirst)
        {
            int size = parties[i]?.Count ?? 0;
            int team = PickTeam(filled, size, teamSize);
            filled[team] += size;
            teamOf[i] = (byte)(team + 1);
        }

        List<MatchSeat> seats = new List<MatchSeat>();
        for (int i = 0; i < parties.Count; i++)
        {
            if (parties[i] == null)
            {
                continue;
            }

            seats.AddRange(parties[i].Select(seat => seat with { Team = teamOf[i] }));
        }

        return seats;
    }

    private static int PickTeam(int[] filled, int size, int teamSize)
    {
        int best = -1;
        for (int team = 0; team < filled.Length; team++)
        {
            if (filled[team] + size > teamSize)
            {
                continue;
            }

            if (best < 0 || filled[team] < filled[best])
            {
                best = team;
            }
        }

        if (best >= 0)
        {
            return best;
        }

        // 装不下 (总人数超了, 按理匹配那层就拦住了) —— 塞进人最少的队
        int emptiest = 0;
        for (int team = 1; team < filled.Length; team++)
        {
            if (filled[team] < filled[emptiest])
            {
                emptiest = team;
            }
        }

        return emptiest;
    }
}
