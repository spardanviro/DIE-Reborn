using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace DIE.Server.Data;

/// <summary>已签发票据的持有者与其排队数据。</summary>
public sealed record TicketOwner(ulong UserId, byte[] QueueData);

/// <summary>一局已分配的对局。</summary>
public sealed record MatchAllocation(byte[] MatchGuid, string HostIp, ushort Port);

/// <summary>
/// 匹配票据与对局的存储。
///
/// 单独成类而不是塞进 <see cref="AccountStore"/>: 这些表属于匹配链路 (2555),
/// 生命周期是"一局游戏", 与账号数据的持久性不同。共用同一个 SQLite 文件即可。
/// </summary>
public sealed class MatchStore
{
    /// <summary>
    /// 票据有效期。
    ///
    /// 客户端拿到票据后立刻就会用 (<c>Matchmaker.cs:168</c> 是回调里直接发下一个请求),
    /// 所以只需要覆盖一次网络往返。给 5 分钟是为了容忍玩家在确认弹窗上停留,
    /// 同时不至于让过期票据长期堆积。
    /// </summary>
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);

    private const int TicketBytes = 16;

    private readonly string _connectionString;

    public MatchStore(string databasePath)
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

    /// <summary>
    /// 签发一张票据。
    ///
    /// 内容对客户端不透明 —— 它只是原样转发到 2555。所以随机字节足够,
    /// 不需要签名。做公开服时再换成 HMAC 防伪造。
    /// </summary>
    public byte[] IssueTicket(ulong userId, byte[] queueData)
    {
        byte[] ticket = RandomNumberGenerator.GetBytes(TicketBytes);
        long now = DateTime.UtcNow.Ticks;

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO matchmaking_tickets (ticket, user_id, issued_utc, expires_utc, consumed, queue_data)
            VALUES ($ticket, $userId, $issued, $expires, 0, $queueData)
            """;
        cmd.Parameters.AddWithValue("$ticket", ticket);
        cmd.Parameters.AddWithValue("$userId", (long)userId);
        cmd.Parameters.AddWithValue("$issued", now);
        cmd.Parameters.AddWithValue("$expires", now + TicketLifetime.Ticks);
        cmd.Parameters.AddWithValue("$queueData", (object)queueData ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        return ticket;
    }

    /// <summary>
    /// 校验并消费一张票据。返回 null 表示票据无效/过期/已用过。
    ///
    /// 一次性使用: 用 UPDATE...WHERE consumed = 0 的受影响行数来判定, 这样
    /// 即使同一张票据被并发提交两次, 也只有一次能成功。
    /// </summary>
    /// <summary>
    /// 删掉过期超过 <paramref name="keepExpiredFor"/> 的匹配票据 (用没用过都删), 返回删了几条。
    /// 每次排队 / 开局签一张, 以前从不清理 (2026-09-17 审校)。
    /// </summary>
    public int PurgeExpiredTickets(DateTime nowUtc, TimeSpan keepExpiredFor)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM matchmaking_tickets WHERE expires_utc < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", (nowUtc - keepExpiredFor).Ticks);
        return cmd.ExecuteNonQuery();
    }

    public TicketOwner ConsumeTicket(byte[] ticket)
    {
        if (ticket == null || ticket.Length == 0)
        {
            return null;
        }

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE matchmaking_tickets
               SET consumed = 1
             WHERE ticket = $ticket
               AND consumed = 0
               AND expires_utc > $now
            RETURNING user_id, queue_data
            """;
        cmd.Parameters.AddWithValue("$ticket", ticket);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        ulong userId = (ulong)reader.GetInt64(0);
        byte[] queueData = reader.IsDBNull(1) ? Array.Empty<byte>() : (byte[])reader[1];
        return new TicketOwner(userId, queueData);
    }

    /// <summary>登记一局对局并把玩家挂上去。</summary>
    public MatchAllocation CreateMatch(ulong userId, string hostIp, ushort port, int queueType, int mapIndex) =>
        CreateMatch(new[] { userId }, hostIp, port, queueType, mapIndex);

    /// <summary>
    /// 登记一局对局并把名单上的人都挂上去。id 为 0 的位子 (队长没拿到那个队员的 id)
    /// 不记 —— 那个人是谁要等他进战斗服时凭会话票才知道。
    /// </summary>
    public MatchAllocation CreateMatch(System.Collections.Generic.IEnumerable<ulong> userIds, string hostIp,
                                       ushort port, int queueType, int mapIndex)
    {
        byte[] matchGuid = Guid.NewGuid().ToByteArray();
        long now = DateTime.UtcNow.Ticks;

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO matches (match_guid, host_ip, port, queue_type, map_index, state, created_utc)
                VALUES ($guid, $ip, $port, $queueType, $mapIndex, 0, $now)
                """;
            cmd.Parameters.AddWithValue("$guid", matchGuid);
            cmd.Parameters.AddWithValue("$ip", hostIp);
            cmd.Parameters.AddWithValue("$port", port);
            cmd.Parameters.AddWithValue("$queueType", queueType);
            cmd.Parameters.AddWithValue("$mapIndex", mapIndex);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        foreach (ulong userId in System.Linq.Enumerable.Distinct(userIds))
        {
            if (userId == 0)
            {
                continue;
            }

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO match_players (match_guid, user_id, joined_utc)
                VALUES ($guid, $userId, $now)
                """;
            cmd.Parameters.AddWithValue("$guid", matchGuid);
            cmd.Parameters.AddWithValue("$userId", (long)userId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return new MatchAllocation(matchGuid, hostIp, port);
    }

    /// <summary>开局前补进一局的人也记进对局名单 (已在名单上的跳过)。</summary>
    public void AddPlayers(byte[] matchGuid, System.Collections.Generic.IEnumerable<ulong> userIds)
    {
        long now = DateTime.UtcNow.Ticks;

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();

        foreach (ulong userId in System.Linq.Enumerable.Distinct(userIds))
        {
            if (userId == 0)
            {
                continue;
            }

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO match_players (match_guid, user_id, joined_utc)
                VALUES ($guid, $userId, $now)
                """;
            cmd.Parameters.AddWithValue("$guid", matchGuid);
            cmd.Parameters.AddWithValue("$userId", (long)userId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// 这一局的战斗服起来了 (state: 已分配 -&gt; 运行中)。
    ///
    /// 状态列以前建好就没人动过, 永远停在"已分配" —— 于是 <see cref="FindActiveMatch"/>
    /// 查什么都查不到。重连现在走的是内存里那张活着的战斗服表 (<c>MatchHostRegistry</c>),
    /// 这两个方法是把库里的记录补齐, 便于事后翻账。
    /// </summary>
    public void MarkRunning(byte[] matchGuid) => SetState(matchGuid, 1, endedUtc: null);

    /// <summary>这一局结束了 (state -&gt; 已结束, 记下结束时间)。</summary>
    public void MarkEnded(byte[] matchGuid) => SetState(matchGuid, 2, endedUtc: DateTime.UtcNow.Ticks);

    private void SetState(byte[] matchGuid, int state, long? endedUtc)
    {
        if (matchGuid == null || matchGuid.Length == 0)
        {
            return;
        }

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = endedUtc == null
            ? "UPDATE matches SET state = $state WHERE match_guid = $guid"
            : "UPDATE matches SET state = $state, ended_utc = $ended WHERE match_guid = $guid";
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$guid", matchGuid);
        if (endedUtc != null)
        {
            cmd.Parameters.AddWithValue("$ended", endedUtc.Value);
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 查一名玩家是否有进行中的对局, 供 GetActiveMatchRequest 使用。
    ///
    /// 只查"运行中"的 (state = 1)。
    ///
    /// **重连不走这条路** —— 那边问的是内存里活着的战斗服 (<c>MatchHostRegistry.FindRejoinable</c>),
    /// 因为"人还在不在局里"、"角色还在不在场上"这些只有战斗服知道, 库里看不出来。
    /// 这个查询留给事后翻账和以后可能的跨进程重启恢复。
    /// </summary>
    public MatchAllocation FindActiveMatch(ulong userId)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.match_guid, m.host_ip, m.port
              FROM matches m
              JOIN match_players p ON p.match_guid = m.match_guid
             WHERE p.user_id = $userId
               AND m.state = 1
             ORDER BY m.created_utc DESC
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$userId", (long)userId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MatchAllocation((byte[])reader[0], reader.GetString(1), (ushort)reader.GetInt32(2));
    }
}
