using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;

namespace DIE.Server.Game;

/// <summary>
/// 下发 <c>GameMessageType.PlayerData</c> —— 计分板上那一整块数字。
///
/// 玩家实测: "计分板上的数据没有被驱动, 全部显示的 0。"
///
/// ## 和 PlayerScoreData 不是一回事
///
/// 我们一直在发的是 <c>PlayerScoreData</c> (GameManagerServer.cs:1364),
/// 那是**增量事件** ("这次击杀 +N 分"), 用来弹飘字和累计。
/// 计分板读的是另一条: <c>PlayerData</c>, 一整块 <see cref="PlayerScoreStats"/>。
/// 我们从来没发过, 所以那张表一直是初始值 —— 全 0。
///
/// ## 客户端会主动来要
///
/// <c>GameManagerClient.SendRequestPlayerData()</c> (:1424) 发一条**空的**
/// <c>PlayerData</c> 上来 (只有类别 + 类型两个字节, 没有载荷) ——
/// 那就是"把大家的数据给我"。玩家每次打开计分板都会发一次
/// (UnityClient.cs:1450 那个按键分支)。
///
/// 除此之外局末也得主动推一次: 那时候客户端是被
/// <see cref="ScoutFinaleDirector"/> 的 TeamFinished 逼着弹计分板的,
/// 不走按键那条路, 不会自己来要。
///
/// ## 报文由原厂的 Serialize 写
///
/// 按客户端读侧 (GameManagerClient.cs:588) 原样对齐:
///
///     while (读 bool)                  // true = 还有一个玩家
///         写 ushort  IndexPlayer      (客户端 _0023nae() 读的就是 ushort)
///         写 PlayerScoreStats.Serialize
///     写 false                          // 结束
///
/// <c>PlayerScoreStats.Serialize</c> 是原厂的 (PlayerScoreStats.cs:121),
/// 我们一个字节都不手写。
///
/// 注意客户端读完自己会补两个字段 (<c>_00230t</c> 等级、<c>_00238g</c> 名字),
/// 那两个不在报文里 —— 它从 Player 实体上取。所以那两列显示对不对,
/// 取决于实体同步, 不取决于这条消息。
/// </summary>
public static class PlayerDataSender
{
    /// <summary>
    /// 把所有活跃玩家的统计发给 <paramref name="recipient"/>。
    /// 传 null 就是广播给所有人。
    /// </summary>
    /// <returns>发出去几份。</returns>
    public static int Send(GameManager simulation, GameServer network,
                           Client recipient = null)
    {
        if (simulation?.ActivePlayerList == null || network == null)
        {
            return 0;
        }

        List<Player> players = simulation.ActivePlayerList
            .Where(p => p != null && p.IsActive)
            .ToList();

        if (players.Count == 0)
        {
            return 0;
        }

        IEnumerable<Client> targets = recipient != null
            ? new[] { recipient }
            : network.ConnectedClientsList.OfType<Client>();

        int sent = 0;

        foreach (Client target in targets)
        {
            // 每人一段 (bool + varint + stats), 128 字节/人是宽松的上界。
            _0023rye._00237ze buffer = network.CreateGameMessage(64 + players.Count * 128);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.PlayerData);

            for (int i = 0; i < players.Count; i++)
            {
                buffer.Write(true);                                 // 还有一个
                buffer.Write((ushort)players[i].IndexPlayer);       // 客户端读的是 ushort
                players[i].PlayerScoreStats.Serialize(buffer);
            }

            buffer.Write(false);                             // 结束

            network.SendGameMessage(buffer, target);
            sent++;
        }

        return sent;
    }
}
