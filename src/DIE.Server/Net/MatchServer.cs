using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using ConductorCrafting;
using DIE.Server.Data;
using DIE.Server.Handlers;
using DIE.Server.Match;
using MatchmakingProtocol;
using Serilog;
using StunTCP;

namespace DIE.Server.Net;

/// <summary>
/// 匹配服务器 (TCP 2555)。
///
/// 客户端不需要任何改动就能连上: ip.cfg 里 MatchmakingServerIPEU/NA 已指向本机,
/// 端口走 <c>StunCore.GetIP.GetMatchmakingServerPort()</c> 的默认值 2555
/// (public 分支没写 MatchmakingServerPort, GetPort 回落到默认)。
///
/// 结构与 <see cref="RequestServer"/> 相同, 两处坑也一模一样, 见下面的注释。
/// </summary>
public sealed class MatchServer : IDisposable
{
    private readonly AccountStore _accounts;
    private readonly MatchStore _matches;
    private readonly MatchHostRegistry _hosts;
    private readonly TCPServer<MatchLink> _listener;
    private readonly ConcurrentDictionary<MatchLink, byte> _connections = new();

    private readonly Func<ulong, int> _refreshAccount;
    private readonly string _publicHost;
    private readonly LeavePunishmentStore _punishments;
    private MessageCallbacks<MatchLink> _callbacks;
    private MatchHandlers _handlers;
    private MatchmakingService _queue;
    private System.Threading.Timer _queueTicker;
    private bool _disposed;

    /// <param name="refreshAccount">
    /// 局末通知大厅重新拉账号数据 —— 接的是
    /// <see cref="RequestServer.RefreshAccount"/>。匹配服和 Request 服是两个
    /// 独立监听器, 只能靠回调搭。传 null 就是不通知。
    /// </param>
    /// <param name="publicHost">服务器公网 IP 或域名, 见 <see cref="MatchHostAddress"/>。</param>
    /// <param name="punishments">离局惩罚的账本。传 null 就是不罚 (单测)。</param>
    public MatchServer(AccountStore accounts, MatchStore matches, MatchHostRegistry hosts,
                       Func<ulong, int> refreshAccount = null, string publicHost = null,
                       LeavePunishmentStore punishments = null)
    {
        _accounts = accounts;
        _matches = matches;
        _hosts = hosts;
        _refreshAccount = refreshAccount;
        _publicHost = publicHost;
        _punishments = punishments;
        _listener = new TCPServer<MatchLink>(concurrentAccepts: 4);
        _listener.SetCreationFunc(socket => new MatchLink(socket));
        _callbacks = BuildCallbacks();
    }

    /// <summary>连着匹配服 (2555) 的连接数。</summary>
    public int LinkCount => _connections.Count;

    /// <summary>排队 / 准备 / 自定义房间里的人, 见 <see cref="MatchmakingService.TakeSnapshot"/>。</summary>
    public MatchmakingService.Snapshot QueueSnapshot() => _queue.TakeSnapshot();

    public bool Start(IPAddress address, int port)
    {
        TCPClient.OnDataReceived += OnDataReceived;
        TCPClient.OnClientDisconnected += OnClientDisconnected;
        TCPClient.OnClientError += OnClientError;
        TCPServer<MatchLink>.ClientConnected += OnClientConnected;
        TCPServer<MatchLink>.ServerError += OnServerError;

        if (!_listener.Start(address, port, backlog: 64, out Exception error))
        {
            Log.Fatal(error, "匹配服务器无法在 {Address}:{Port} 上监听", address, port);
            return false;
        }

        Log.Information("匹配服务器已监听 {Address}:{Port}", address, port);

        // 凑人窗口 / 准备超时都靠它推 (MatchmakingService.Tick)。
        _queueTicker = new System.Threading.Timer(_ =>
        {
            try
            {
                _queue.Tick();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "排队定时处理出错");
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        return true;
    }

    private void OnClientConnected(TCPServer<MatchLink> server, MatchLink link)
    {
        // 与 RequestServer 同一个坑: TCPServer.AcceptCompleted 先 BeginReceiving 再触发
        // ClientConnected, 数据可能比事件先到, 所以两边都要幂等地登记。
        if (_connections.TryAdd(link, 0))
        {
            Log.Information("{Link} 已连接 (匹配链路在线 {Count})", link, _connections.Count);
        }
    }

    private void OnClientDisconnected(TCPClient client)
    {
        if (client is MatchLink link && _connections.TryRemove(link, out _))
        {
            Log.Information("{Link} 已断开 (匹配链路在线 {Count})", link, _connections.Count);
            _handlers.OnDisconnected(link);
        }
    }

    private void OnServerError(TCPServer<MatchLink> server, Exception error)
    {
        Log.Error(error, "匹配监听器错误");
    }

    /// <summary>
    /// 处理器抛的异常唯一的出口 —— 详见 <c>RequestServer.OnClientError</c>。
    ///
    /// 不挂它的话, 匹配请求处理中的任何异常都会被原厂 StunTCP 静静吞掉,
    /// 客户端只会看到"排队一直转", 日志里什么也没有。
    /// </summary>
    private void OnClientError(TCPClient client, Exception error, CloseReason reason)
    {
        if (client is not MatchLink link)
        {
            return;
        }

        Log.Error(error, "{Link} 处理匹配请求时异常 (reason={Reason})", link, reason);
    }

    /// <summary>
    /// OnDataReceived 是 TCPClient 的**静态**事件 —— 进程内所有 TCPClient 都会触发它,
    /// 包括 1555 上的 RequestLink。必须先按类型过滤, 否则两个服务器会互相吃对方的数据。
    /// </summary>
    private void OnDataReceived(TCPClient client, byte[] buffer, int offset, int byteCount)
    {
        if (client is not MatchLink link)
        {
            return;
        }

        if (_connections.TryAdd(link, 0))
        {
            Log.Information("{Link} 已连接 (匹配链路在线 {Count})", link, _connections.Count);
        }

        try
        {
            link.HandlePacket(ref _callbacks, buffer, offset, byteCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Link} 处理入站数据时异常, 关闭连接", link);
            link.ForceClose();
        }
    }

    private MessageCallbacks<MatchLink> BuildCallbacks()
    {
        MatchLauncher launcher = new MatchLauncher(_accounts, _matches, _hosts, _refreshAccount);

        // 排队出来的一局: 对局记录里的地址写"-" —— 每个队长拿到的地址按各自的连接算
        // (MatchLinkQueueClient), 一局里可能不止一个, 记录里写哪个都不对。
        Random maps = new Random();
        _queue = new MatchmakingService(
            (IReadOnlyList<MatchSeat> seats, QueueType queue, int map, int difficulty, int infection,
             out string failure) =>
                launcher.Launch(seats, queue, map, "-", infection, out failure, difficulty)?.Port,
            (queue, difficulty) => GameModeAvailability.PickMap(queue, maps, difficulty),
            GameModeAvailability.IsMapRunnable,
            punishedOf: seats => PunishedSeats(seats),
            join: (parties, queue, difficulty) => launcher.TryJoin(parties, queue, difficulty)?.Port,
            hasOpenMatch: launcher.HasOpenMatch);

        MatchHandlers match = new MatchHandlers(_matches, launcher, _publicHost, _queue,
            _accounts.ResolveByAuthTicket, _punishments);
        _handlers = match;

        return new MessageCallbacks<MatchLink>
        {
            GetActiveMatchRequest = match.OnGetActiveMatch,
            SoloServerCreateRequest = match.OnSoloServerCreate,
            JoinQueueRequest = match.OnJoinQueue,
            ReadyForMatchMessage = match.OnReadyForMatch,
            LeaveQueueMessage = match.OnLeaveQueue,
            CreateCustomGameRequest = match.OnCreateCustomGame,
            JoinCustomGameRequest = match.OnJoinCustomGame,
            GetCustomGamesListRequest = match.OnGetCustomGamesList,
            GetObservableMatchesRequest = match.OnGetObservableMatches,
            LeaveMatchRequest = match.OnLeaveMatch,
            AddRaidDifficultyRequest = match.OnAddRaidDifficulty,

            // 兜底不是可选项 —— 理由同 RequestServer: 未应答的请求会让客户端
            // 静默重连并重放, 无限循环且没有任何 UI 报错。
            UnhandledRequest = OnUnhandledRequest,
            UnhandledMessage = OnUnhandledMessage,
        };
    }

    /// <summary>
    /// 名单里谁还在离局罚期 (<c>JoinQueueResult.PartyMemberIsTimedOut</c>)。
    /// id 未知的队员位 (0) 查不了, 只能放过。
    ///
    /// 调用方 (<c>MatchmakingService.JoinQueue</c>) 传进来的只有**发起排队的人自己** ——
    /// 私服不做整队连坐, 理由见那边的注释。所以这里虽然写成收一份名单,
    /// 实际拿到的是一个人。
    /// </summary>
    private MatchmakingTimeOutData[] PunishedSeats(IReadOnlyList<MatchSeat> seats)
    {
        if (_punishments == null)
        {
            return Array.Empty<MatchmakingTimeOutData>();
        }

        DateTime now = DateTime.UtcNow;

        return _punishments.ActiveFor(seats.Select(s => s.UserId).Where(id => id != 0), now)
            .Select(p => new MatchmakingTimeOutData
            {
                UserId = p.UserId,
                AvailableAtTicks = p.UntilUtc.Ticks,
            })
            .ToArray();
    }

    private void OnUnhandledRequest(MatchmakingMessageType messageType, MatchLink link, byte[] guid)
    {
        Log.Warning("{Link} 未实现的匹配请求 {MessageType}({Id}) —— 回 UnrecognizedError 以免客户端重连循环",
            link, messageType, (ushort)messageType);

        // RequestResult != OK 时原厂 Respond 不写载荷, 所以这里的响应类型无关紧要,
        // 借 SoloServerCreated 当占位 struct 即可。
        Request<SoloServerCreated> request = new Request<SoloServerCreated>
        {
            RequestType = (ushort)messageType,
            Guid = guid,
            ResponseHandler = link,
            SerializeDelegate = MatchSerialization.Serialize,
        };
        request.Fail(RequestResult.UnrecognizedError);
    }

    private void OnUnhandledMessage(MatchmakingMessageType messageType, MatchLink link)
    {
        Log.Warning("{Link} 未实现的匹配单向消息 {MessageType}({Id})", link, messageType, (ushort)messageType);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queueTicker?.Dispose();
        TCPClient.OnDataReceived -= OnDataReceived;
        TCPClient.OnClientDisconnected -= OnClientDisconnected;
        TCPClient.OnClientError -= OnClientError;
        TCPServer<MatchLink>.ClientConnected -= OnClientConnected;
        TCPServer<MatchLink>.ServerError -= OnServerError;
        _listener.Stop(reusable: false);
    }
}
