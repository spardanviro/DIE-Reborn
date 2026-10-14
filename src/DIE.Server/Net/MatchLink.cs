using System;
using System.Net.Sockets;
using MatchmakingProtocol;
using StunMessage;
using StunTCP;

namespace DIE.Server.Net;

/// <summary>
/// 匹配服务器 (TCP 2555) 上的一条客户端连接。
///
/// 结构与 <see cref="RequestLink"/> 完全一致 —— 两条链路用的是同一套 StunTCP 分帧,
/// 只是消息枚举和回调表不同。会话状态直接挂在实例上, 不需要额外的 session 字典。
///
/// 注意: 这条链路上的连接与 1555 上的连接是**两个独立的 TCP 会话**, 客户端不会
/// 在它们之间传递身份。要把两边关联起来只能靠 <c>AuthData</c> 或匹配票据 ——
/// Phase 2 的做法是靠票据 (T16 签发时记下 user_id, T17 校验时取回)。
/// </summary>
public sealed class MatchLink : TCPMessageClient
{
    private static readonly MessageHandler.HandleMessageDelegate<MessageCallbacks<MatchLink>, MatchLink>
        HandleMessageMethod = (ref Message buffer, ushort msgType, ref MessageCallbacks<MatchLink> callbacks, ref MatchLink link) =>
            ((MatchmakingMessageType)msgType).HandleMessage(ref callbacks, ref buffer, ref link);

    private static readonly MessageHandler.HandleRequestDelegate<MessageCallbacks<MatchLink>, MatchLink>
        HandleRequestMethod = (ref Message buffer, ushort msgType, ref MessageCallbacks<MatchLink> callbacks, ref MatchLink link, byte[] guid, IResponseHandler responder) =>
            ((MatchmakingMessageType)msgType).HandleRequest(ref callbacks, ref buffer, ref link, guid, responder);

    private static int _nextConnectionId;

    public MatchLink(Socket socket)
        : base(socket)
    {
        ConnectionId = System.Threading.Interlocked.Increment(ref _nextConnectionId);
        ConnectedAtUtc = DateTime.UtcNow;
    }

    public int ConnectionId { get; }

    public DateTime ConnectedAtUtc { get; }

    /// <summary>
    /// 这条连接是谁。由票据校验 (开局/排队/建房) 或 AuthData 里的会话票填入;
    /// 0 表示还没认出来。
    /// </summary>
    public ulong UserId { get; set; }

    public int HandlePacket(ref MessageCallbacks<MatchLink> callbacks, byte[] buffer, int offset, int byteCount)
    {
        MatchLink self = this;
        return HandlePacket(HandleMessageMethod, HandleRequestMethod, buffer, offset, byteCount, ref callbacks, ref self, this);
    }

    /// <summary>
    /// 发送一条单向消息 (packetType 1)。排队时序里的推送 (MatchupFound / PlayersReadyCount /
    /// FindingServer / ReadyTimeout / CustomGameInfo) 全走这里。
    /// </summary>
    public void SendOneWay<T>(MatchmakingMessageType messageType, ref T payload, SerializeDelegate<T> serialize) where T : struct
    {
        Message buffer = MessageHandler.GetTempMessageBuffer();
        buffer.Write((ushort)messageType);
        serialize(ref payload, ref buffer);
        SendMessage(ref buffer);
    }

    /// <summary>
    /// 服务端发起的请求 (packetType 2) —— 匹配链路上只有 ServerFoundMessage 这一条,
    /// 客户端回 ServerFoundAffirmativeMessage (<c>Matchmaker.cs:396</c>)。
    ///
    /// 帧头由 StunTCP 自己的 <c>GetTempRequestBuffer</c> 写 (类型 2 + 16 字节 guid),
    /// 回应按 guid 配对后调 <paramref name="onResponse"/>; 超时或断线时也会调, 结果非 OK。
    /// </summary>
    public void SendServerRequest<T>(MatchmakingMessageType messageType, ref T payload, SerializeDelegate<T> serialize,
                                     Action<RequestResult> onResponse, int timeoutMs = 30000) where T : struct
    {
        byte[] guid = null;
        Message buffer = MessageHandler.GetTempRequestBuffer(ref guid);
        buffer.Write((ushort)messageType);
        serialize(ref payload, ref buffer);
        SendRequest((RequestResult result, ref Message _) => onResponse?.Invoke(result), ref buffer, guid, timeoutMs);
    }

    public delegate void SerializeDelegate<T>(ref T payload, ref Message buffer);

    public override string ToString() => $"match#{ConnectionId}({RemoteEndPoint})";
}
