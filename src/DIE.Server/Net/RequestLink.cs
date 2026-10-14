using System;
using System.Net.Sockets;
using GameProtocol;
using StunMessage;
using StunTCP;

namespace DIE.Server.Net;

/// <summary>
/// 一条已接受的客户端连接 —— 既是传输端点, 也是会话状态载体。
///
/// 这是原厂 <c>GameProtocol.GameLink</c> 的服务端镜像。GameLink 继承 TCPMessageClient
/// 并把**自己**作为 MessageCallbacks&lt;T&gt; 的 userData T, 我们照搬这个设计:
/// 每个连接一个 RequestLink 实例, 会话字段直接挂在上面, 处理器拿到的第二个参数
/// 就是它, 不需要额外的 session 字典。
///
/// 分帧、请求/响应关联、长度回填全部由继承来的 MessageHandler 完成 ——
/// 那是原厂代码, 与客户端用的是同一份, 所以线格式天然一致。
/// </summary>
public sealed class RequestLink : TCPMessageClient
{
    private static readonly MessageHandler.HandleMessageDelegate<MessageCallbacks<RequestLink>, RequestLink>
        HandleMessageMethod = (ref Message buffer, ushort msgType, ref MessageCallbacks<RequestLink> callbacks, ref RequestLink link) =>
            ((GameMessageType)msgType).HandleMessage(ref callbacks, ref buffer, ref link);

    private static readonly MessageHandler.HandleRequestDelegate<MessageCallbacks<RequestLink>, RequestLink>
        HandleRequestMethod = (ref Message buffer, ushort msgType, ref MessageCallbacks<RequestLink> callbacks, ref RequestLink link, byte[] guid, IResponseHandler responder) =>
            ((GameMessageType)msgType).HandleRequest(ref callbacks, ref buffer, ref link, guid, responder);

    private static int _nextConnectionId;

    public RequestLink(Socket socket)
        : base(socket)
    {
        ConnectionId = System.Threading.Interlocked.Increment(ref _nextConnectionId);
        ConnectedAtUtc = DateTime.UtcNow;
    }

    /// <summary>日志里用来串联同一条连接的短标识。</summary>
    public int ConnectionId { get; }

    public DateTime ConnectedAtUtc { get; }

    // ---- 会话状态 ----
    // 登录成功后由 LoginHandlers 填写。AuthTicket / SessionID 对客户端都是不透明字节,
    // 它只是原样缓存并在重连时回传, 从不检查内容。

    public ulong UserId { get; set; }

    public byte[] AuthTicket { get; set; }

    public byte[] SessionId { get; set; }

    public bool HasLoggedIn { get; set; }

    /// <summary>客户端在 LoginRequest 里自报的 Steam 昵称, 仅用于日志。</summary>
    public string ProfileName { get; set; }

    /// <summary>
    /// 把收到的原始字节喂进原厂分帧器。返回已处理的字节数, 未成帧的部分由
    /// MessageHandler 自己缓存到下次调用。
    /// </summary>
    public int HandlePacket(ref MessageCallbacks<RequestLink> callbacks, byte[] buffer, int offset, int byteCount)
    {
        RequestLink self = this;
        return HandlePacket(HandleMessageMethod, HandleRequestMethod, buffer, offset, byteCount, ref callbacks, ref self, this);
    }

    /// <summary>
    /// 发送一条单向消息 (packetType 1)。
    ///
    /// 登录流程的关键: 客户端收到 LoginResponse{Success} 之后**不会**继续,
    /// 必须再收到 LoginDataMessage 这条推送才会进主菜单。顺序必须是先响应后推送 ——
    /// 响应的回调里才会装好接收推送所需的状态。
    /// </summary>
    public void SendOneWay<T>(GameMessageType messageType, ref T payload, SerializeDelegate<T> serialize) where T : struct
    {
        Message buffer = MessageHandler.GetTempMessageBuffer();
        buffer.Write((ushort)messageType);
        serialize(ref payload, ref buffer);
        SendMessage(ref buffer);
    }

    public delegate void SerializeDelegate<T>(ref T payload, ref Message buffer);

    public override string ToString() => $"conn#{ConnectionId}({RemoteEndPoint})";
}
