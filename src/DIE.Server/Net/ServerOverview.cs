using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using DIE.Server.Match;

namespace DIE.Server.Net;

/// <summary>
/// 服务器控制台看到的全貌: 三层各有多少人、每一局的情况、每个玩家在哪。
///
/// ## 三层
///
///   - 大厅 (Request, TCP 1555): 登录着的账号。局里的人也连着大厅, 所以它是"在线总数"。
///   - 匹配 (Match, TCP 2555): 排队等凑人的、凑齐了正在点准备的、自定义房间里的。
///   - 战斗服 (UDP 50000 起, 每局一个): 正在打的局, 和连在局里的人。
///
/// 输入全是现成的快照 (<see cref="RequestServer.Players"/>、<see cref="MatchmakingService.TakeSnapshot"/>、
/// <see cref="MatchView.Of"/>), 这里只负责拼, 方便单测。
/// </summary>
public sealed class ServerOverview
{
    private static readonly TimeSpan NameCacheFor = TimeSpan.FromMinutes(10);

    private readonly Func<ulong, (string Name, long? SteamId)?> _identity;
    private readonly ConcurrentDictionary<ulong, (DateTime At, string Name, long? SteamId)> _names = new();

    /// <param name="identity">按账号查名字和 Steam ID (接 <c>AccountStore.Identity</c>), 结果缓存 10 分钟。</param>
    public ServerOverview(Func<ulong, (string Name, long? SteamId)?> identity)
    {
        _identity = identity;
    }

    /// <summary>一局战斗服此刻的样子。</summary>
    /// <param name="State">loading 正在建 / waiting 等人进 / running 在打 / over 打完了 / error 建失败。</param>
    /// <param name="BusyPercent">上一个汇总窗口 (30 秒) 里泵线程忙的比例 —— 这一局的负载。</param>
    public sealed record MatchView(int Port, string Queue, string Map, int Difficulty, int Infection,
                                   int Roster, IReadOnlyCollection<ulong> Connected, string State,
                                   DateTime CreatedAtUtc, double BusyPercent, double AverageFrameMs,
                                   double WorstFrameMs)
    {
        public static MatchView Of(LidgrenHost host)
        {
            string state = host.MatchOver ? "over"
                : host.SimulationError != null ? "error"
                : !host.SimulationReady ? "loading"
                : !host.MatchBegun ? "waiting"
                : "running";

            string map = host.MapIndex >= 0 && MapManagerData.MapsInEnum != null
                         && host.MapIndex < MapManagerData.MapsInEnum.Length
                ? MapManagerData.MapsInEnum[host.MapIndex].ToString()
                : host.MapIndex.ToString();

            return new MatchView(host.Port, host.QueueType.ToString(), map, host.CrossroadDifficulty,
                host.InfectionLevel, host.Roster.Count, host.ConnectedUserIds, state, host.CreatedAtUtc,
                host.Profiler.LastBusyPercent, host.Profiler.LastAverageMs, host.Profiler.LastWorstMs);
        }
    }

    /// <summary>不随时间变的那几样: 版本、端口、启动时间。</summary>
    public sealed record Fixed(int ProcessId, string Version, DateTime StartedAtUtc, string PublicHost,
                               int RequestPort, int MatchPort, int StatusPort, int UdpFirst, int UdpLast,
                               int MaxMatches);

    public Overview Build(Fixed basics, ServerInfo info, IReadOnlyList<RequestServer.LobbyPlayer> lobby,
                          int matchLinks, MatchmakingService.Snapshot queue, IReadOnlyList<MatchView> matches,
                          DateTime nowUtc)
    {
        List<MatchView> live = matches.Where(m => m.State != "over").ToList();

        Dictionary<ulong, int> inMatch = new Dictionary<ulong, int>();
        foreach (MatchView match in live)
        {
            foreach (ulong user in match.Connected)
            {
                inMatch[user] = match.Port;
            }
        }

        HashSet<ulong> queued = new HashSet<ulong>(queue.QueuedUsers);

        List<PlayerRow> players = new List<PlayerRow>();
        HashSet<ulong> listed = new HashSet<ulong>();

        foreach (RequestServer.LobbyPlayer player in lobby.OrderBy(p => p.ConnectedAtUtc))
        {
            listed.Add(player.UserId);
            players.Add(Row(player.UserId, player.Name, player.Address, player.ConnectedAtUtc, inMatch, queued,
                nowUtc));
        }

        // 大厅连接断了、人还在局里的 (局里掉线重连期间常见) 也列出来
        foreach (KeyValuePair<ulong, int> user in inMatch.Where(u => !listed.Contains(u.Key)))
        {
            players.Add(Row(user.Key, null, string.Empty, null, inMatch, queued, nowUtc));
        }

        int waiting = queue.Queues.Sum(q => q.Waiting);
        int readying = queue.Queues.Sum(q => q.Readying);

        return new Overview
        {
            Pid = basics.ProcessId,
            Version = basics.Version ?? string.Empty,
            StartedUtc = basics.StartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            UptimeSeconds = (long)Math.Max(0, (nowUtc - basics.StartedAtUtc).TotalSeconds),
            Name = info?.Name ?? string.Empty,
            Location = info?.Location ?? string.Empty,
            Listed = info?.Listed ?? true,
            PublicHost = basics.PublicHost ?? string.Empty,
            Ports = new PortsBlock
            {
                Request = basics.RequestPort,
                Match = basics.MatchPort,
                Status = basics.StatusPort,
                UdpFirst = basics.UdpFirst,
                UdpLast = basics.UdpLast,
            },
            Lobby = new LobbyBlock
            {
                Online = lobby.Count,
                Idle = players.Count(p => p.Where == "lobby"),
            },
            Matchmaking = new MatchmakingBlock
            {
                Links = matchLinks,
                Waiting = waiting,
                Readying = readying,
                Rooms = queue.Rooms,
                RoomPlayers = queue.RoomPlayers,
                Queues = queue.Queues.Select(q => new QueueRow
                {
                    Queue = q.Queue.ToString(),
                    Waiting = q.Waiting,
                    Readying = q.Readying,
                }).ToList(),
            },
            Battle = new BattleBlock
            {
                Matches = live.Count,
                MaxMatches = basics.MaxMatches,
                Players = live.Sum(m => m.Connected.Count),
            },
            Matches = matches.OrderBy(m => m.CreatedAtUtc).Select(m => new MatchRow
            {
                Port = m.Port,
                Queue = m.Queue,
                Map = m.Map,
                Difficulty = m.Difficulty,
                Infection = m.Infection,
                Players = m.Connected.Count,
                Roster = m.Roster,
                State = m.State,
                AgeSeconds = (long)Math.Max(0, (nowUtc - m.CreatedAtUtc).TotalSeconds),
                BusyPercent = Math.Round(m.BusyPercent, 1),
                AverageFrameMs = Math.Round(m.AverageFrameMs, 2),
                WorstFrameMs = Math.Round(m.WorstFrameMs, 1),
            }).ToList(),
            Players = players,
            Time = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
    }

    private PlayerRow Row(ulong userId, string name, string address, DateTime? since,
                          Dictionary<ulong, int> inMatch, HashSet<ulong> queued, DateTime nowUtc)
    {
        (string known, long? steamId) = Lookup(userId, nowUtc);

        string where = inMatch.ContainsKey(userId) ? "match" : queued.Contains(userId) ? "queue" : "lobby";

        return new PlayerRow
        {
            UserId = userId.ToString(),
            Name = string.IsNullOrEmpty(name) ? known ?? string.Empty : name,
            SteamId = steamId?.ToString() ?? string.Empty,
            Address = address ?? string.Empty,
            Where = where,
            Port = inMatch.TryGetValue(userId, out int port) ? port : 0,
            OnlineSeconds = since.HasValue ? (long)Math.Max(0, (nowUtc - since.Value).TotalSeconds) : 0,
        };
    }

    private (string Name, long? SteamId) Lookup(ulong userId, DateTime nowUtc)
    {
        if (_names.TryGetValue(userId, out var cached) && nowUtc - cached.At < NameCacheFor)
        {
            return (cached.Name, cached.SteamId);
        }

        (string Name, long? SteamId)? found = null;
        try
        {
            found = _identity?.Invoke(userId);
        }
        catch (Exception)
        {
            // 库忙 / 账号刚被删 —— 名字空着就是, 不影响别的
        }

        string name = found?.Name;
        long? steamId = found?.SteamId;
        _names[userId] = (nowUtc, name, steamId);
        return (name, steamId);
    }

    // ---- 发给控制台的 JSON (camelCase) ----

    public sealed class Overview
    {
        public int V { get; init; } = 1;
        public int Pid { get; init; }
        public string Version { get; init; }
        public string StartedUtc { get; init; }
        public long UptimeSeconds { get; init; }
        public string Name { get; init; }
        public string Location { get; init; }
        public bool Listed { get; init; }
        public string PublicHost { get; init; }
        public PortsBlock Ports { get; init; }
        public LobbyBlock Lobby { get; init; }
        public MatchmakingBlock Matchmaking { get; init; }
        public BattleBlock Battle { get; init; }
        public List<MatchRow> Matches { get; init; }
        public List<PlayerRow> Players { get; init; }
        public string Time { get; init; }
    }

    public sealed class PortsBlock
    {
        public int Request { get; init; }
        public int Match { get; init; }
        public int Status { get; init; }
        public int UdpFirst { get; init; }
        public int UdpLast { get; init; }
    }

    public sealed class LobbyBlock
    {
        /// <summary>登录着的账号 (局里的也算)。</summary>
        public int Online { get; init; }

        /// <summary>只在大厅、没排队也没进局的。</summary>
        public int Idle { get; init; }
    }

    public sealed class MatchmakingBlock
    {
        public int Links { get; init; }
        public int Waiting { get; init; }
        public int Readying { get; init; }
        public int Rooms { get; init; }
        public int RoomPlayers { get; init; }
        public List<QueueRow> Queues { get; init; }
    }

    public sealed class QueueRow
    {
        public string Queue { get; init; }
        public int Waiting { get; init; }
        public int Readying { get; init; }
    }

    public sealed class BattleBlock
    {
        public int Matches { get; init; }
        public int MaxMatches { get; init; }
        public int Players { get; init; }
    }

    public sealed class MatchRow
    {
        public int Port { get; init; }
        public string Queue { get; init; }
        public string Map { get; init; }
        public int Difficulty { get; init; }
        public int Infection { get; init; }
        public int Players { get; init; }
        public int Roster { get; init; }
        public string State { get; init; }
        public long AgeSeconds { get; init; }
        public double BusyPercent { get; init; }
        public double AverageFrameMs { get; init; }
        public double WorstFrameMs { get; init; }
    }

    public sealed class PlayerRow
    {
        public string UserId { get; init; }
        public string Name { get; init; }
        public string SteamId { get; init; }
        public string Address { get; init; }

        /// <summary>lobby 只在大厅 / queue 在排队或房间里 / match 在局里 (<see cref="Port"/> 是哪一局)。</summary>
        public string Where { get; init; }

        public int Port { get; init; }
        public long OnlineSeconds { get; init; }
    }
}
