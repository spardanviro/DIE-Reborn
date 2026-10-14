using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DIE.ServerConsole.Core
{
    /// <summary>
    /// 服务端管理接口 <c>/admin/overview</c> 的回答 (服务端那边是 <c>DIE.Server.Net.ServerOverview</c>)。
    ///
    /// 控制台是 .NET Framework 4.8 程序, 没有 System.Text.Json; 用框架自带的
    /// DataContractJsonSerializer 读, 缺的字段留默认值, 多的字段忽略 —— 新旧版本的服务端都能对上。
    /// </summary>
    [DataContract]
    public sealed class Overview
    {
        [DataMember(Name = "pid")] public int Pid { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "uptimeSeconds")] public long UptimeSeconds { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "location")] public string Location { get; set; }
        [DataMember(Name = "listed")] public bool Listed { get; set; }
        [DataMember(Name = "publicHost")] public string PublicHost { get; set; }
        [DataMember(Name = "ports")] public PortsBlock Ports { get; set; }
        [DataMember(Name = "lobby")] public LobbyBlock Lobby { get; set; }
        [DataMember(Name = "matchmaking")] public MatchmakingBlock Matchmaking { get; set; }
        [DataMember(Name = "battle")] public BattleBlock Battle { get; set; }
        [DataMember(Name = "matches")] public List<MatchRow> Matches { get; set; }
        [DataMember(Name = "players")] public List<PlayerRow> Players { get; set; }

        public static Overview Parse(string json)
        {
            Overview overview = Json.Read<Overview>(json) ?? new Overview();
            overview.Ports = overview.Ports ?? new PortsBlock();
            overview.Lobby = overview.Lobby ?? new LobbyBlock();
            overview.Matchmaking = overview.Matchmaking ?? new MatchmakingBlock();
            overview.Matchmaking.Queues = overview.Matchmaking.Queues ?? new List<QueueRow>();
            overview.Battle = overview.Battle ?? new BattleBlock();
            overview.Matches = overview.Matches ?? new List<MatchRow>();
            overview.Players = overview.Players ?? new List<PlayerRow>();
            return overview;
        }
    }

    [DataContract]
    public sealed class PortsBlock
    {
        [DataMember(Name = "request")] public int Request { get; set; } = 1555;
        [DataMember(Name = "match")] public int Match { get; set; } = 2555;
        [DataMember(Name = "status")] public int Status { get; set; } = 8555;
        [DataMember(Name = "udpFirst")] public int UdpFirst { get; set; } = 50000;
        [DataMember(Name = "udpLast")] public int UdpLast { get; set; } = 50099;
    }

    [DataContract]
    public sealed class LobbyBlock
    {
        [DataMember(Name = "online")] public int Online { get; set; }
        [DataMember(Name = "idle")] public int Idle { get; set; }
    }

    [DataContract]
    public sealed class MatchmakingBlock
    {
        [DataMember(Name = "links")] public int Links { get; set; }
        [DataMember(Name = "waiting")] public int Waiting { get; set; }
        [DataMember(Name = "readying")] public int Readying { get; set; }
        [DataMember(Name = "rooms")] public int Rooms { get; set; }
        [DataMember(Name = "roomPlayers")] public int RoomPlayers { get; set; }
        [DataMember(Name = "queues")] public List<QueueRow> Queues { get; set; }

        /// <summary>匹配这一层的总人数: 排队的 + 点准备的 + 自定义房间里的。</summary>
        public int Total => Waiting + Readying + RoomPlayers;
    }

    [DataContract]
    public sealed class QueueRow
    {
        [DataMember(Name = "queue")] public string Queue { get; set; }
        [DataMember(Name = "waiting")] public int Waiting { get; set; }
        [DataMember(Name = "readying")] public int Readying { get; set; }
    }

    [DataContract]
    public sealed class BattleBlock
    {
        [DataMember(Name = "matches")] public int Matches { get; set; }
        [DataMember(Name = "maxMatches")] public int MaxMatches { get; set; }
        [DataMember(Name = "players")] public int Players { get; set; }
    }

    [DataContract]
    public sealed class MatchRow
    {
        [DataMember(Name = "port")] public int Port { get; set; }
        [DataMember(Name = "queue")] public string Queue { get; set; }
        [DataMember(Name = "map")] public string Map { get; set; }
        [DataMember(Name = "difficulty")] public int Difficulty { get; set; }
        [DataMember(Name = "infection")] public int Infection { get; set; }
        [DataMember(Name = "players")] public int Players { get; set; }
        [DataMember(Name = "roster")] public int Roster { get; set; }
        [DataMember(Name = "state")] public string State { get; set; }
        [DataMember(Name = "ageSeconds")] public long AgeSeconds { get; set; }
        [DataMember(Name = "busyPercent")] public double BusyPercent { get; set; }
        [DataMember(Name = "averageFrameMs")] public double AverageFrameMs { get; set; }
        [DataMember(Name = "worstFrameMs")] public double WorstFrameMs { get; set; }
    }

    [DataContract]
    public sealed class PlayerRow
    {
        [DataMember(Name = "userId")] public string UserId { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "steamId")] public string SteamId { get; set; }
        [DataMember(Name = "address")] public string Address { get; set; }
        [DataMember(Name = "where")] public string Where { get; set; }
        [DataMember(Name = "port")] public int Port { get; set; }
        [DataMember(Name = "onlineSeconds")] public long OnlineSeconds { get; set; }
    }

    /// <summary>服务端旁边的 admin.json: 管理接口的端口、口令、进程号。</summary>
    [DataContract]
    public sealed class AdminEndpoint
    {
        [DataMember(Name = "port")] public int Port { get; set; }
        [DataMember(Name = "token")] public string Token { get; set; }
        [DataMember(Name = "pid")] public int Pid { get; set; }

        public bool IsValid => Port > 0 && Port <= 65535 && !string.IsNullOrEmpty(Token);
    }

    /// <summary>登记处 <c>/probe</c> 的回答: 外面看到的公网 IP, 和每个 TCP 端口连不连得进来。</summary>
    [DataContract]
    public sealed class ProbeResult
    {
        [DataMember(Name = "ip")] public string Ip { get; set; }
        [DataMember(Name = "ports")] public List<ProbePort> Ports { get; set; }
    }

    [DataContract]
    public sealed class ProbePort
    {
        [DataMember(Name = "port")] public int Port { get; set; }
        [DataMember(Name = "open")] public bool Open { get; set; }
    }

    /// <summary>DataContractJsonSerializer 的薄包装: 读坏了返回 null, 不抛。</summary>
    public static class Json
    {
        public static T Read<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
                }
            }
            catch (SerializationException)
            {
                return null;
            }
        }

        public static string Write<T>(T value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
