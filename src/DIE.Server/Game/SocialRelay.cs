using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using StunGameNetwork;

namespace DIE.Server.Game;

/// <summary>
/// 局内的三条"人对人"消息 —— 打字聊天、表情、小地图标记。
///
/// ## 都是纯转发, 但服务端一条都没接过 (2026-09-18 审校发现)
///
/// 这三条的形状一样: 客户端发一条上来, 服务端加上"是谁发的"再发下去。原厂服务端那半丢了,
/// 而战斗服的消息分发是一串 <c>if</c>、**没有兜底告警**, 所以它们一直被静默丢掉,
/// 日志里连一行都没有。
///
/// 表现: 打字聊天完全没用 (自己都看不到); 表情轮盘按了没反应; 小地图标记只有自己看得到
/// —— 标记那条客户端发完会本地回显一次 (GameManagerClient.cs:1263), 所以最容易被误认为"好的"。
///
/// ## 报文格式 (逐字对着客户端的读法写)
///
/// <code>
/// Chat                 收: string text, bool isTeam            (GameManagerClient.SendChat:1474)
///                      发: bool hasClient=true, int clientIndex, ChatType(3 位),
///                          bool isRawText=true, string text     (GameManagerClient.cs:449)
///
/// Emote                收: byte emoteID                        (SendEmoteEvent:1465)
///                      发: ushort playerIndex, byte emoteID    (GameManagerClient.cs:668)
///
/// MiniMapPlingData     收: byte type, float x, float y         (SendMiniMapPling:1258)
///                      发: byte type, float x, float y, ushort playerIndex  (GameManagerClient.cs:543)
/// </code>
///
/// 注意读写方法的对应 (StunGameNetwork/INetBuffer.cs): <c>_0023Fae</c>↔bool、<c>_0023jae</c>↔byte、
/// <c>_0023uKe</c>↔float、<c>_0023Hae</c>↔int、<c>_0023Qae</c>↔string、<c>_0023nae</c>↔ushort。
/// 聊天里的 clientIndex 是 <c>int</c> 不是 varint, ChatType 是 3 位。
///
/// ## 本队还是全体
///
/// 客户端自己带 <c>isTeam</c>。<see cref="ChatType.Team"/> 只发给同队 (拾荒是 4v4v4, 这条真的分队),
/// <see cref="ChatType.Global"/> 发给所有人。表情和标记原厂没给分队开关, 一律广播。
///
/// ## 防刷屏是我们加的
///
/// 原厂的限流规则随服务端丢了 (客户端只有小地图标记那条自己带了
/// <c>MiniMapPlingProtection()</c>)。这里按"每人每 <see cref="ChatCooldownSeconds"/> 秒一条 +
/// 文本截到 <see cref="MaxChatLength"/> 字"挡一下 —— 私服是熟人局, 不需要更严。
/// </summary>
public static class SocialRelay
{
    /// <summary>一条聊天最多几个字, 超出截断。**我们定的。**</summary>
    public const int MaxChatLength = 200;

    /// <summary>同一个人两条聊天之间至少隔几秒。**我们定的。**</summary>
    public const float ChatCooldownSeconds = 1f;

    /// <summary>
    /// 玩家打字聊天 -&gt; 转发。
    /// </summary>
    /// <param name="lastChat">
    /// 每个 client 槽位上一条聊天的时刻, 用来限流。**由宿主持有**而不是放静态字段 ——
    /// 一个进程里跑着多个战斗服, 静态表会让两局的槽位号撞在一起互相限流。
    /// </param>
    /// <returns>发给了几个人; 被限流或内容为空返回 0。</returns>
    public static int RelayChat(GameManager simulation, GameServer network, IClient from, INetBuffer buffer,
                                Dictionary<int, float> lastChat)
    {
        if (simulation == null || network == null || buffer == null || from is not Client sender)
        {
            return 0;
        }

        string text = buffer._0023Qae();
        bool isTeam = buffer._0023Fae();

        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (text.Length > MaxChatLength)
        {
            text = text.Substring(0, MaxChatLength);
        }

        float now = simulation.Time;
        if (lastChat != null)
        {
            if (lastChat.TryGetValue(sender.Index, out float last) && now - last < ChatCooldownSeconds)
            {
                return 0;
            }

            lastChat[sender.Index] = now;
        }

        TeamId team = sender.LocalPlayer?.TeamId ?? TeamId.Neutral;
        ChatType chatType = isTeam ? ChatType.Team : ChatType.Global;

        int sent = 0;
        foreach (Client recipient in network.ConnectedClientsList.OfType<Client>())
        {
            // 本队频道只给同队的。还没分队 (Neutral) 的人算一队 —— 单人局和观战都落在这一档,
            // 与其一条都发不出去, 不如让他们彼此看得见。
            if (isTeam && (recipient.LocalPlayer?.TeamId ?? TeamId.Neutral) != team)
            {
                continue;
            }

            _0023rye._00237ze outgoing = network.CreateGameMessage(32 + text.Length * 2);
            if (outgoing == null)
            {
                continue;
            }

            WriteChat(outgoing, sender.Index, chatType, text);
            network.SendGameMessage(outgoing, recipient);
            sent++;
        }

        return sent;
    }

    /// <summary>玩家放了个表情 -&gt; 广播。</summary>
    public static int RelayEmote(GameServer network, IClient from, INetBuffer buffer)
    {
        if (network == null || buffer == null || from is not Client sender)
        {
            return 0;
        }

        byte emote = buffer._0023jae();

        Player player = sender.LocalPlayer;
        if (player == null || !player.IsActive)
        {
            return 0;
        }

        int sent = 0;
        foreach (Client recipient in network.ConnectedClientsList.OfType<Client>())
        {
            _0023rye._00237ze outgoing = network.CreateGameMessage(8);
            if (outgoing == null)
            {
                continue;
            }

            WriteEmote(outgoing, player.IndexPlayer, emote);
            network.SendGameMessage(outgoing, recipient);
            sent++;
        }

        return sent;
    }

    /// <summary>
    /// 玩家在小地图上标了一下 -&gt; 广播。
    ///
    /// 发送者自己那一份客户端已经本地回显过了 (GameManagerClient.cs:1263), 但原厂的读法里
    /// 没有"跳过发送者"的余地 (它按 playerIndex 取人再画), 所以照样发给他 —— 至多是同一个
    /// 标记画两次, 而漏发会让人以为标记没生效。
    /// </summary>
    public static int RelayPing(GameServer network, IClient from, INetBuffer buffer)
    {
        if (network == null || buffer == null || from is not Client sender)
        {
            return 0;
        }

        byte pingType = buffer._0023jae();
        float x = buffer._0023uKe();
        float y = buffer._0023uKe();

        Player player = sender.LocalPlayer;
        if (player == null || !player.IsActive)
        {
            return 0;
        }

        int sent = 0;
        foreach (Client recipient in network.ConnectedClientsList.OfType<Client>())
        {
            _0023rye._00237ze outgoing = network.CreateGameMessage(16);
            if (outgoing == null)
            {
                continue;
            }

            WritePing(outgoing, pingType, x, y, player.IndexPlayer);
            network.SendGameMessage(outgoing, recipient);
            sent++;
        }

        return sent;
    }

    // ---- 写报文 ----
    //
    // 抽成单独的方法只为一件事: **能被测试直接调**。字节顺序错一个字段, 后面全部错位,
    // 而那种错在真机上表现成"客户端随机解析异常", 极难反推。见 SocialRelayTests。

    /// <summary>
    /// 聊天。对着客户端读侧 (GameManagerClient.cs:449):
    /// bool hasClient -&gt; [int clientIndex] -&gt; ChatType(3 位) -&gt; bool isRawText -&gt; string。
    /// </summary>
    public static void WriteChat(INetBuffer buffer, int clientIndex, ChatType chatType, string text)
    {
        buffer.Write((byte)ConductorGameMessageType.GameMessage);
        buffer.Write((byte)GameMessageType.Chat);
        buffer.Write(true);                      // 是某个玩家说的
        buffer.Write(clientIndex);               // int, 不是 varint
        buffer.Write((uint)chatType, 3);
        buffer.Write(true);                      // 自由文本
        buffer.Write(text);
    }

    /// <summary>表情。客户端读 ushort playerIndex + byte emoteID (GameManagerClient.cs:668)。</summary>
    public static void WriteEmote(INetBuffer buffer, int playerIndex, byte emote)
    {
        buffer.Write((byte)ConductorGameMessageType.GameMessage);
        buffer.Write((byte)GameMessageType.Emote);
        buffer.Write((ushort)playerIndex);
        buffer.Write(emote);
    }

    /// <summary>
    /// 小地图标记。客户端读 byte type + float x + float y + ushort playerIndex
    /// (GameManagerClient.cs:543) —— 注意 playerIndex 在**最后**, 和表情那条相反。
    /// </summary>
    public static void WritePing(INetBuffer buffer, byte pingType, float x, float y, int playerIndex)
    {
        buffer.Write((byte)ConductorGameMessageType.GameMessage);
        buffer.Write((byte)GameMessageType.MiniMapPlingData);
        buffer.Write(pingType);
        buffer.Write(x);
        buffer.Write(y);
        buffer.Write((ushort)playerIndex);
    }
}
