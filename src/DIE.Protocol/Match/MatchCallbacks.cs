using StunMessage;
using StunTCP;

namespace MatchmakingProtocol;

/// <summary>
/// 匹配协议的处理器插槽, 与原厂 CRIB 树 <c>MatchmakingProtocol\MessageCallbacks.cs</c> 同构。
///
/// 客户端会发的全部 9 个请求和 2 个单向消息都在这里。没注册处理器的会落到
/// <see cref="UnhandledRequest"/> / <see cref="UnhandledMessage"/>, 客户端拿到明确的失败码,
/// 而不是无响应。
///
/// 服务端推给客户端的那几条 (MatchupFound / PlayersReadyCount / ...) 不在这里 ——
/// 它们只有写方向, 由 <c>MatchLink.SendOneWay</c> / <c>SendRequest</c> 发出。
/// </summary>
public class MessageCallbacks<T>
{
    public delegate void RequestCallback<TRequest, TResponse, B>(TRequest request, B link, Request<TResponse> respond)
        where TResponse : struct;

    public delegate void MessageCallback<TMessage, B>(TMessage message, B link);

    public delegate void UnhandledRequestCallback<B>(MatchmakingMessageType messageType, B link, byte[] guid);

    public delegate void UnhandledMessageCallback<B>(MatchmakingMessageType messageType, B link);

    // ---- 请求 (packetType 2), 必须恰好回一个响应 ----

    public RequestCallback<JoinQueueRequest, JoinQueueResponse, T> JoinQueueRequest;

    public RequestCallback<GetActiveMatchRequest, GetActiveMatchResponse, T> GetActiveMatchRequest;

    public RequestCallback<LeaveMatchRequest, LeaveMatchResponse, T> LeaveMatchRequest;

    public RequestCallback<AddRaidDifficultyRequest, AddRaidDifficultyResponse, T> AddRaidDifficultyRequest;

    public RequestCallback<CreateCustomGameRequest, JoinQueueResponse, T> CreateCustomGameRequest;

    public RequestCallback<JoinCustomGameRequest, JoinQueueResponse, T> JoinCustomGameRequest;

    public RequestCallback<GetCustomGamesListRequest, GetCustomGamesListResponse, T> GetCustomGamesListRequest;

    public RequestCallback<SoloServerCreateRequest, SoloServerCreated, T> SoloServerCreateRequest;

    public RequestCallback<GetObservableMatchesRequest, GetObservableMatchesResponse, T> GetObservableMatchesRequest;

    // ---- 单向消息 (packetType 1) ----

    public MessageCallback<ReadyForMatchMessage, T> ReadyForMatchMessage;

    public MessageCallback<LeaveQueueMessage, T> LeaveQueueMessage;

    public UnhandledRequestCallback<T> UnhandledRequest;

    public UnhandledMessageCallback<T> UnhandledMessage;
}

/// <summary>
/// 按操作码分派到 <see cref="MessageCallbacks{T}"/>。
///
/// 形状照搬原厂 <c>_0023Qke.HandleRequest</c> (CRIB 树 -8je\-qke.cs): 先反序列化,
/// 成功才构造 <see cref="Request{T}"/> 并调用处理器, 失败返回 false (分帧层记为 DeserializeFail)。
///
/// 与原厂的一处差异: 原厂没有注册处理器时 <c>break</c> 掉、最终返回 false,
/// 表现为"静默不响应"。对服务端来说那是致命的 —— 客户端的 SingleThreadTCPClient
/// 会等满 20 秒后强制断开、退避重连、**重放该请求**, 无限循环且无 UI 报错。
/// 所以这里改成落到 UnhandledRequest, 由它回一个明确的失败响应。
/// </summary>
public static class MatchMessageTypeExtensions
{
    private delegate bool Reader<TValue>(ref TValue value, ref Message buffer);

    public static bool HandleRequest<T>(this MatchmakingMessageType messageType, ref MessageCallbacks<T> callbacks,
        ref Message buffer, ref T link, byte[] guid, IResponseHandler responder)
    {
        long startTick = RequestTime.ElapsedTicks;
        MessageCallbacks<T> c = callbacks;

        bool? handled = messageType switch
        {
            MatchmakingMessageType.JoinQueueRequest =>
                Dispatch(c.JoinQueueRequest, MatchSerialization.Deserialize, MatchSerialization.Serialize,
                    startTick, messageType, ref buffer, link, guid, responder),
            MatchmakingMessageType.GetActiveMatchRequest =>
                Dispatch(c.GetActiveMatchRequest, MatchSerialization.Deserialize, MatchSerialization.Serialize,
                    startTick, messageType, ref buffer, link, guid, responder),
            MatchmakingMessageType.LeaveMatchRequest =>
                Dispatch(c.LeaveMatchRequest, MatchSerialization.Deserialize, MatchSerialization.Serialize,
                    startTick, messageType, ref buffer, link, guid, responder),
            MatchmakingMessageType.AddRaidDifficultyRequest =>
                Dispatch(c.AddRaidDifficultyRequest, MatchSerialization.Deserialize, MatchSerialization.Serialize,
                    startTick, messageType, ref buffer, link, guid, responder),
            MatchmakingMessageType.CreateCustomGameRequest =>
                Dispatch(c.CreateCustomGameRequest, MatchSerialization.Deserialize, MatchSerialization.Serialize,
                    startTick, messageType, ref buffer, link, guid, responder),
            MatchmakingMessageType.JoinCustomGameRequest =>
                Dispatch(c.JoinCustomGameRequest, MatchSerialization.Deserialize, MatchSerialization.Serialize,
                    startTick, messageType, ref buffer, link, guid, responder),
            MatchmakingMessageType.GetCustomGamesListRequest =>
                Dispatch(c.GetCustomGamesListRequest, MatchSerialization.Deserialize, MatchSerialization.Serialize,
                    startTick, messageType, ref buffer, link, guid, responder),
            MatchmakingMessageType.SoloServerCreateRequest =>
                Dispatch(c.SoloServerCreateRequest, MatchSerialization.Deserialize, MatchSerialization.Serialize,
                    startTick, messageType, ref buffer, link, guid, responder),
            MatchmakingMessageType.GetObservableMatchesRequest =>
                Dispatch(c.GetObservableMatchesRequest, MatchSerialization.Deserialize, MatchSerialization.Serialize,
                    startTick, messageType, ref buffer, link, guid, responder),
            _ => null,
        };

        if (handled.HasValue)
        {
            return handled.Value;
        }

        callbacks.UnhandledRequest?.Invoke(messageType, link, guid);

        // 返回 true: 我们**已经**处理了它 (由兜底回了失败响应)。
        // 返回 false 会让分帧层再当成 DeserializeFail 处理一次, 变成一个请求两个响应。
        return true;
    }

    public static bool HandleMessage<T>(this MatchmakingMessageType messageType, ref MessageCallbacks<T> callbacks,
        ref Message buffer, ref T link)
    {
        MessageCallbacks<T> c = callbacks;

        bool? handled = messageType switch
        {
            MatchmakingMessageType.ReadyForMatchMessage =>
                Dispatch(c.ReadyForMatchMessage, MatchSerialization.Deserialize, ref buffer, link),
            MatchmakingMessageType.LeaveQueueMessage =>
                Dispatch(c.LeaveQueueMessage, MatchSerialization.Deserialize, ref buffer, link),
            _ => null,
        };

        if (handled.HasValue)
        {
            return handled.Value;
        }

        callbacks.UnhandledMessage?.Invoke(messageType, link);
        return true;
    }

    /// <summary>没注册处理器返回 null (交给兜底), 否则返回反序列化是否成功。</summary>
    private static bool? Dispatch<T, TRequest, TResponse>(
        MessageCallbacks<T>.RequestCallback<TRequest, TResponse, T> callback,
        Reader<TRequest> read,
        Request<TResponse>.StructSerializeDelegate write,
        long startTick, MatchmakingMessageType messageType, ref Message buffer, T link,
        byte[] guid, IResponseHandler responder)
        where TRequest : struct
        where TResponse : struct
    {
        if (callback == null)
        {
            return null;
        }

        TRequest request = default;
        if (!read(ref request, ref buffer))
        {
            return false;
        }

        callback(request, link, new Request<TResponse>
        {
            StartTick = startTick,
            RequestType = (ushort)messageType,
            Guid = guid,
            ResponseHandler = responder,
            SerializeDelegate = write,
        });
        return true;
    }

    private static bool? Dispatch<T, TMessage>(MessageCallbacks<T>.MessageCallback<TMessage, T> callback,
        Reader<TMessage> read, ref Message buffer, T link)
        where TMessage : struct
    {
        if (callback == null)
        {
            return null;
        }

        TMessage message = default;
        if (!read(ref message, ref buffer))
        {
            return false;
        }

        callback(message, link);
        return true;
    }
}
