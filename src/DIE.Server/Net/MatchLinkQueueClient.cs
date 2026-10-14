using System;
using System.Net;
using ConductorCrafting;
using DIE.Server.Match;
using MatchmakingProtocol;
using Serilog;
using StunCore;
using StunTCP;

namespace DIE.Server.Net;

/// <summary>
/// 把 <see cref="IQueueClient"/> 的几条推送落到一条匹配连接上。
///
/// 战斗服地址按**这条连接**的来源算 (<see cref="MatchHostAddress"/>): 本机/局域网进来的回它连进来
/// 用的地址, 公网进来的回公网地址。组队时队员拿到的是队长转发的这一个地址 ——
/// 云服务器上大家都从公网进来, 没有区别; 本机开发服上队长和队员同在本机, 也没有区别。
/// 只有"队长在服务器局域网里、队员在公网"这种混搭会发错, 私服用不上。
/// </summary>
public sealed class MatchLinkQueueClient : IQueueClient
{
    private readonly MatchLink _link;
    private readonly Func<MatchLink, IPAddress> _addressFor;

    public MatchLinkQueueClient(MatchLink link, Func<MatchLink, IPAddress> addressFor)
    {
        _link = link;
        _addressFor = addressFor;
    }

    public string Name => $"{_link} user={_link.UserId}";

    public void SendMatchupFound(byte[] matchGuid, QueueType queue, int matchSize)
    {
        MatchupFoundMessage message = new MatchupFoundMessage
        {
            MatchGuid = matchGuid,
            QueueType = queue,
            MatchSize = matchSize,
        };
        _link.SendOneWay(MatchmakingMessageType.MatchupFoundMessage, ref message, MatchSerialization.Serialize);
    }

    public void SendPlayersReady(int playersReady, byte[] matchGuid)
    {
        PlayersReadyCountMessage message = new PlayersReadyCountMessage
        {
            PlayersReady = playersReady,
            MatchGuid = matchGuid,
        };
        _link.SendOneWay(MatchmakingMessageType.PlayersReadyCount, ref message, MatchSerialization.Serialize);
    }

    public void SendFindingServer(byte[] matchGuid)
    {
        FindingServerMessage message = new FindingServerMessage { MatchGuid = matchGuid };
        _link.SendOneWay(MatchmakingMessageType.FindingServerMessage, ref message, MatchSerialization.Serialize);
    }

    public void SendServerFound(byte[] matchGuid, ushort port, QueueType queue)
    {
        IPAddress address = _addressFor(_link);
        ServerFoundMessage message = new ServerFoundMessage
        {
            MatchGuid = matchGuid,
            IPAddress = address.GetAddressBytes(),
            Port = port,
            QueueType = queue,

            // 客户端拿它拼 Game.exe 的 +region 参数。私服只有一台服务器, 分区没有意义;
            // 请求里也没带客户端自己的分区设置, 给个固定的合法值。
            MatchmakingRegion = MatchmakingRegion.Europe,
        };

        string name = Name;
        _link.SendServerRequest(MatchmakingMessageType.ServerFoundMessage, ref message, MatchSerialization.Serialize,
            result =>
            {
                if (result == RequestResult.OK)
                {
                    Log.Information("{Client} 已确认收到战斗服地址 {Ip}:{Port}", name, address, port);
                }
                else
                {
                    Log.Warning("{Client} 没确认战斗服地址 {Ip}:{Port} ({Result}) —— 它和队员可能进不了这一局",
                        name, address, port, result);
                }
            });
    }

    public void SendReadyTimeout(bool rejoiningQueue, ReadyTimeoutReason reason, byte[] matchGuid)
    {
        ReadyTimeoutMessage message = new ReadyTimeoutMessage
        {
            RejoiningQueue = rejoiningQueue,
            Reason = reason,
            MatchGuid = matchGuid,
        };
        _link.SendOneWay(MatchmakingMessageType.ReadyTimeoutMessage, ref message, MatchSerialization.Serialize);
    }

    public void SendCustomGameInfo(long customGameId, byte playerCount, byte maxPlayers)
    {
        CustomGameInfoMessage message = new CustomGameInfoMessage
        {
            CustomGameId = customGameId,
            PlayerCount = playerCount,
            MaxPlayers = maxPlayers,
        };
        _link.SendOneWay(MatchmakingMessageType.CustomGameInfoMessage, ref message, MatchSerialization.Serialize);
    }
}
