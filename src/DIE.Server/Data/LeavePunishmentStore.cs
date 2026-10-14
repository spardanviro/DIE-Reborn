using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace DIE.Server.Data;

/// <summary>一次离局惩罚: 到什么时候为止, 以及这是第几次。</summary>
public sealed record LeavePunishment(ulong UserId, DateTime UntilUtc, int Strikes);

/// <summary>
/// 离局惩罚 —— 中途放弃一局之后一段时间不能排队。
///
/// ## 客户端本来就有这套 UI
///
/// 服务端回 <c>JoinQueueResult.PartyMemberIsTimedOut</c> 并带上
/// <c>MatchmakingTimeOutData(账号, 到什么时候)</c>, 客户端就会弹"还要等 X 分钟"
/// (<c>Matchmaker.DisplayUserTimedOutPopup</c>, 时间由它自己按 Ticks 算成天/时/分)。
/// 所以这一侧只要记账 + 在排队时拦住。
///
/// ## 时长是我们定的
///
/// 原版的档位没公开。取"越跑越久, 但一天不跑就重新算":
/// 5 / 15 / 30 / 60 分钟, 封顶 60; <see cref="StrikeMemory"/> 之内不再犯就从第一档重新开始。
///
/// ## 什么时候记
///
/// 只记**明确放弃**: 玩家在重连弹窗上点了"离开" (<c>LeaveMatchRequest</c>)。
/// 单纯掉线不罚 —— 分不清是跑了还是网断了, 而且他还能重连回去 (那正是重连要解决的事)。
/// 教程也不罚 (客户端自己也把教程排除在惩罚之外)。
///
/// ## 免罚名单
///
/// 玩家 (2026-09-14): "把我这个账号的退出游戏的惩罚消掉, 现在在测试期间需要频繁退出游戏。"
///
/// 名单写 **Steam ID** (<see cref="ExemptFile"/>, 和 public-host.txt 放一起) —— 本机库和云上库的
/// user_id 不一定一样, Steam ID 两边都认。名单上的人不记账, 以前记下的罚期也不再拦。
/// </summary>
public sealed class LeavePunishmentStore
{
    /// <summary>时长档位 (分钟), 超出就用最后一档。**我们定的。**</summary>
    public static readonly IReadOnlyList<int> LadderMinutes = new[] { 5, 15, 30, 60 };

    /// <summary>多久不犯规就把次数归零。**我们定的。**</summary>
    public static readonly TimeSpan StrikeMemory = TimeSpan.FromHours(24);

    /// <summary>免罚名单文件名: 一行一个 Steam ID, # 开头是注释。</summary>
    public const string ExemptFile = "leave-punish-exempt.txt";

    private readonly string _connectionString;

    /// <summary>免罚的 Steam ID。见类说明。</summary>
    public HashSet<ulong> ExemptSteamIds { get; } = new HashSet<ulong>();

    public LeavePunishmentStore(string databasePath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    private SqliteConnection Open()
    {
        SqliteConnection conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>读 <paramref name="directory"/> 下的 <see cref="ExemptFile"/> 进名单。返回读到几个。</summary>
    public int LoadExemptions(string directory)
    {
        string path = Path.Combine(directory, ExemptFile);
        if (!File.Exists(path))
        {
            return 0;
        }

        int before = ExemptSteamIds.Count;
        foreach (string line in File.ReadAllLines(path).Select(l => l.Trim()))
        {
            if (line.Length > 0 && !line.StartsWith("#") && ulong.TryParse(line, out ulong steamId))
            {
                ExemptSteamIds.Add(steamId);
            }
        }

        return ExemptSteamIds.Count - before;
    }

    /// <summary>这个账号在免罚名单上吗 (按它绑的 Steam ID)。</summary>
    public bool IsExempt(ulong userId)
    {
        if (userId == 0 || ExemptSteamIds.Count == 0)
        {
            return false;
        }

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT steam_id FROM accounts WHERE user_id = $id";
        cmd.Parameters.AddWithValue("$id", (long)userId);

        return cmd.ExecuteScalar() is long steamId && ExemptSteamIds.Contains((ulong)steamId);
    }

    /// <summary>
    /// 记一次离局。返回这次罚到什么时候。调用方先看 <see cref="IsExempt"/>。
    /// </summary>
    public LeavePunishment Punish(ulong userId, DateTime nowUtc)
    {
        int strikes = 1;

        using SqliteConnection conn = Open();
        using (SqliteCommand read = conn.CreateCommand())
        {
            read.CommandText = "SELECT strikes, last_utc FROM leave_punishments WHERE user_id = $id";
            read.Parameters.AddWithValue("$id", (long)userId);

            using SqliteDataReader reader = read.ExecuteReader();
            if (reader.Read())
            {
                DateTime last = new DateTime(reader.GetInt64(1), DateTimeKind.Utc);

                // 隔得久了就当第一次 —— 不然偶尔掉一局的人会被上个月的记录连坐
                strikes = nowUtc - last <= StrikeMemory ? reader.GetInt32(0) + 1 : 1;
            }
        }

        int minutes = LadderMinutes[Math.Min(strikes, LadderMinutes.Count) - 1];
        DateTime until = nowUtc.AddMinutes(minutes);

        using (SqliteCommand write = conn.CreateCommand())
        {
            write.CommandText = """
                INSERT INTO leave_punishments (user_id, until_utc, strikes, last_utc)
                VALUES ($id, $until, $strikes, $now)
                ON CONFLICT(user_id) DO UPDATE SET
                    until_utc = excluded.until_utc,
                    strikes   = excluded.strikes,
                    last_utc  = excluded.last_utc
                """;
            write.Parameters.AddWithValue("$id", (long)userId);
            write.Parameters.AddWithValue("$until", until.Ticks);
            write.Parameters.AddWithValue("$strikes", strikes);
            write.Parameters.AddWithValue("$now", nowUtc.Ticks);
            write.ExecuteNonQuery();
        }

        return new LeavePunishment(userId, until, strikes);
    }

    /// <summary>这个账号现在还在罚期里吗? 不在就返回 null。</summary>
    public LeavePunishment ActiveFor(ulong userId, DateTime nowUtc)
    {
        if (userId == 0 || IsExempt(userId))
        {
            return null;
        }

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT until_utc, strikes FROM leave_punishments WHERE user_id = $id AND until_utc > $now";
        cmd.Parameters.AddWithValue("$id", (long)userId);
        cmd.Parameters.AddWithValue("$now", nowUtc.Ticks);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new LeavePunishment(userId, new DateTime(reader.GetInt64(0), DateTimeKind.Utc), reader.GetInt32(1));
    }

    /// <summary>名单里还在罚期的人 (排队时用: 一队里有一个就整队进不去)。</summary>
    public IReadOnlyList<LeavePunishment> ActiveFor(IEnumerable<ulong> userIds, DateTime nowUtc)
    {
        List<LeavePunishment> punished = new List<LeavePunishment>();
        HashSet<ulong> seen = new HashSet<ulong>();

        foreach (ulong userId in userIds)
        {
            if (!seen.Add(userId))
            {
                continue;
            }

            if (ActiveFor(userId, nowUtc) is LeavePunishment active)
            {
                punished.Add(active);
            }
        }

        return punished;
    }
}
