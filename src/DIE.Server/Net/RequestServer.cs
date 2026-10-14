using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using DIE.Server.Data;
using DIE.Server.Handlers;
using DIE.Server.Steam;
using GameProtocol;
using Serilog;
using StunTCP;

namespace DIE.Server.Net;

/// <summary>
/// Request/Account 服务器 (TCP 1555)。
///
/// 分帧、请求/响应关联、长度回填全部由原厂 StunTCP 完成 —— 与客户端用的是同一份代码,
/// 所以线格式天然一致。这里只负责: 起监听、把收到的字节喂进分帧器、挂处理器委托。
/// </summary>
public sealed class RequestServer : IDisposable
{
    private readonly AccountStore _accounts;
    private readonly MatchStore _matches;
    private readonly CraftingCatalog _catalog;
    private readonly ShopCatalog _shop = new ShopCatalog();
    private readonly TCPServer<RequestLink> _listener;
    private readonly ConcurrentDictionary<RequestLink, byte> _connections = new();
    private readonly AccountRefreshQueue _refreshQueue = new AccountRefreshQueue();

    private readonly ISteamTicketVerifier _steamAuth;
    private MessageCallbacks<RequestLink> _callbacks;
    private LoginHandlers _login;
    private bool _disposed;

    /// <param name="steamAuth">玩家身份的 Steam 核验, 见 <see cref="LoginHandlers.ResolveIdentity"/>。</param>
    public RequestServer(AccountStore accounts, MatchStore matches,
                         CraftingCatalog catalog = null, ISteamTicketVerifier steamAuth = null)
    {
        _accounts = accounts;
        _matches = matches;
        _catalog = catalog;
        _steamAuth = steamAuth;

        // 货架从物品图鉴生成 —— 图鉴没加载的话是空货架, 不报错。
        _shop.Build();
        Log.Information("商店货架已上货 {Count} 件", _shop.Items.Count);
        _listener = new TCPServer<RequestLink>(concurrentAccepts: 4);
        _listener.SetCreationFunc(socket => new RequestLink(socket));
        _callbacks = BuildCallbacks();
    }

    /// <summary>登录着的账号数 (同一个账号开两条连接只算一个)。状态接口用。</summary>
    public int OnlineCount
    {
        get
        {
            HashSet<ulong> users = new HashSet<ulong>();

            foreach (RequestLink link in _connections.Keys)
            {
                if (link.UserId != 0)
                {
                    users.Add(link.UserId);
                }
            }

            return users.Count;
        }
    }

    /// <summary>大厅里的一位玩家 (控制台的玩家列表用)。</summary>
    public sealed record LobbyPlayer(ulong UserId, string Name, string Address, DateTime ConnectedAtUtc);

    /// <summary>登录着的玩家, 每个账号一项 (同一个账号开两条连接取最早那条)。</summary>
    public IReadOnlyList<LobbyPlayer> Players()
    {
        Dictionary<ulong, LobbyPlayer> players = new Dictionary<ulong, LobbyPlayer>();

        foreach (RequestLink link in _connections.Keys)
        {
            if (link.UserId == 0)
            {
                continue;
            }

            if (players.TryGetValue(link.UserId, out LobbyPlayer known) && known.ConnectedAtUtc <= link.ConnectedAtUtc)
            {
                continue;
            }

            players[link.UserId] = new LobbyPlayer(link.UserId, link.ProfileName ?? string.Empty,
                link.RemoteEndPoint?.Address?.ToString() ?? string.Empty, link.ConnectedAtUtc);
        }

        return players.Values.ToList();
    }

    public bool Start(IPAddress address, int port)
    {
        TCPClient.OnDataReceived += OnDataReceived;
        TCPClient.OnClientDisconnected += OnClientDisconnected;
        TCPClient.OnClientError += OnClientError;
        TCPServer<RequestLink>.ClientConnected += OnClientConnected;
        TCPServer<RequestLink>.ServerError += OnServerError;

        if (!_listener.Start(address, port, backlog: 64, out Exception error))
        {
            Log.Fatal(error, "无法在 {Address}:{Port} 上监听", address, port);
            return false;
        }

        Log.Information("Request 服务器已监听 {Address}:{Port}", address, port);
        return true;
    }

    private void OnClientConnected(TCPServer<RequestLink> server, RequestLink link)
    {
        // TCPServer.AcceptCompleted 是先 BeginReceiving 再触发 ClientConnected,
        // 所以数据可能比这个事件先到 —— OnDataReceived 里也会 TryAdd, 两边都要幂等。
        if (_connections.TryAdd(link, 0))
        {
            Log.Information("{Link} 已连接 (在线 {Count})", link, _connections.Count);
        }
    }

    private void OnClientDisconnected(TCPClient client)
    {
        if (client is RequestLink link && _connections.TryRemove(link, out _))
        {
            Log.Information("{Link} 已断开 (在线 {Count})", link, _connections.Count);
        }
    }

    private void OnServerError(TCPServer<RequestLink> server, Exception error)
    {
        Log.Error(error, "监听器错误");
    }

    /// <summary>
    /// **处理器抛的异常唯一的出口。**
    ///
    /// 原厂 <c>MessageHandler.HandlePacket</c> 把每条消息的分发包在 try/catch 里,
    /// 异常不往上抛, 而是转给 <c>TCPClient.OnClientError</c> 这个**静态**事件。
    /// 没人订阅的话 <c>CallErrorHandler</c> 里的 <c>?.Invoke</c> 直接什么也不做 ——
    /// 异常就此消失, 请求也永远等不到响应。
    ///
    /// 症状是"客户端一直转圈, 服务端日志毫无异常", 排查成本极高: 玩家实测的
    /// "lockbox 点购买一直转圈"就是这样, 真正的原因是旧库上
    /// <c>AddStackable</c> 的 ON CONFLICT 子句对不上主键 (见
    /// <c>AccountStore.FixStackablePrimaryKey</c>), 而日志里一个字都没有。
    ///
    /// 只记录不关连接: 一条请求处理失败不代表这条 TCP 流坏了 (那种情况在
    /// <c>OnDataReceived</c> 的 catch 里另有处理)。客户端那一发请求会自己超时。
    /// </summary>
    private void OnClientError(TCPClient client, Exception error, CloseReason reason)
    {
        // 和 OnDataReceived 一样是**静态**事件, 进程内所有 TCPClient 共用 ——
        // 匹配服务器的连接由 MatchServer 自己记, 这里先过滤。
        if (client is not RequestLink link)
        {
            return;
        }

        Log.Error(error, "{Link} 处理请求时异常 (reason={Reason})", link, reason);
    }

    /// <summary>
    /// OnDataReceived 是 TCPClient 的**静态**事件, 进程内所有 TCPClient 都会触发,
    /// 所以必须先按类型过滤。
    /// </summary>
    private void OnDataReceived(TCPClient client, byte[] buffer, int offset, int byteCount)
    {
        if (client is not RequestLink link)
        {
            return;
        }

        // 见 OnClientConnected 的注释: 数据可能先于 ClientConnected 事件到达。
        // 不在这里补登记的话, 这条连接断开时 TryRemove 会失败, 在线数就会永久偏高。
        if (_connections.TryAdd(link, 0))
        {
            Log.Information("{Link} 已连接 (在线 {Count})", link, _connections.Count);
        }

        try
        {
            link.HandlePacket(ref _callbacks, buffer, offset, byteCount);
        }
        catch (Exception ex)
        {
            // 分帧层自己抛异常说明流已经不可信了, 关掉让客户端重连比继续解析安全
            Log.Error(ex, "{Link} 处理入站数据时异常, 关闭连接", link);
            link.ForceClose();
        }
    }

    /// <summary>
    /// 通知某个账号的大厅"数据变了, 重新拉一份"。
    ///
    /// 局末用 —— 战斗服把经验和货币落库之后调这里, 大厅才会刷新顶栏。
    /// 详见 <see cref="LoginHandlers.PushAccountRefresh"/>。
    ///
    /// 同一个账号理论上只有一条大厅连接, 但按 userId 遍历而不是记映射 ——
    /// 连接数是个位数, 而维护一张映射表要处理断线清理, 不划算。
    /// </summary>
    /// <returns>推给了几条连接。</returns>
    public int RefreshAccount(ulong userId)
    {
        if (_login == null || userId == 0)
        {
            return 0;
        }

        int pushed = 0;

        foreach (RequestLink link in _connections.Keys)
        {
            if (link.UserId == userId && _login.PushAccountRefresh(link))
            {
                pushed++;
            }
        }

        // ## 没人连着是**常态**, 不是异常
        //
        // 大厅是轮询式短连接 (连上来拉一次就断, 实测每条只活十几秒), 局末
        // 那一刻往往一条都没有 —— 玩家实测的日志里就是
        // "已通知 0 条大厅连接刷新账号数据"。
        //
        // 所以记一笔, 等它下一轮重连时补推 (LoginHandlers.PushRefreshIfPending)。
        if (pushed == 0)
        {
            _refreshQueue.Mark(userId);
        }

        return pushed;
    }

    private MessageCallbacks<RequestLink> BuildCallbacks()
    {
        LoginHandlers login = new LoginHandlers(_accounts, _refreshQueue, _steamAuth);
        _login = login;
        MenuHandlers menu = new MenuHandlers(_accounts, _matches, _shop, _catalog);

        MessageCallbacks<RequestLink> callbacks = new MessageCallbacks<RequestLink>
        {
            // ---- 登录 / 重连 ----
            LoginRequest = login.OnLoginRequest,
            AuthRequest = login.OnAuthRequest,
            SteamAuthRequest = login.OnSteamAuthRequest,

            // ---- 主菜单加载后的查询 ----
            GetRewardSetupRequest = menu.OnGetRewardSetup,
            GetVanityURLRequest = menu.OnGetVanityUrl,
            GetDropTableRequest = menu.OnGetDropTable,
            GetShopRequest = menu.OnGetShop,
            BuyItemRequest = menu.OnBuyItem,
            UnlockLockboxRequest = menu.OnUnlockLockbox,
            CraftItemRequest = menu.OnCraftItem,
            AddDesignsToWeaponRequest = menu.OnAddDesignsToWeapon,
            CraftGadgetRequest = menu.OnCraftGadget,
            GetCurrencyRequest = menu.OnGetCurrency,
            BalanceChangeRequest = menu.OnBalanceChange,
            GetAccountDataRequest = menu.OnGetAccountData,
            GetInventoryRequest = menu.OnGetInventory,
            GetDisabledFeaturesRequest = menu.OnGetDisabledFeatures,
            GetMailDataRequest = menu.OnGetMailData,
            SetMailDataRequest = menu.OnSetMailData,
            AddActiveCribTimeMessage = menu.OnAddActiveCribTime,
            UnlockStoryMapNodeRequest = menu.OnUnlockStoryMapNode,
            GetCurrencyISOCodeRequest = menu.OnGetCurrencyIsoCode,

            // Phase 2 的入口, 但明确应答比落到兜底更好 (理由见处理器注释)
            ReportPlayersRequest = menu.OnReportPlayers,
            GetMatchmakingTicketRequest = menu.OnGetMatchmakingTicket,
            GetDebugMatchmakingTicketRequest = menu.OnGetDebugMatchmakingTicket,

            // ---- 兜底: 不是可选项 ----
            //
            // 客户端的 SingleThreadTCPClient 以 connectionTimeout:20s / failOnIOFailure:false
            // 构造。一个请求没有响应, 它会等满 20 秒 -> 强制断开 -> 按 {4,4,4,8,16,32} 秒退避
            // 重连 -> **重放该请求**。全过程无 UI 报错、客户端日志也不指名是哪条消息,
            // 表现就像网络故障, 极易浪费几小时。
            //
            // 所以: 每个 packetType-2 帧必须恰好产生一个 packetType-3 帧, 哪怕内容是错误码。
            UnhandledRequest = OnUnhandledRequest,
            UnhandledMessage = OnUnhandledMessage,
        };

        return callbacks;
    }

    private void OnUnhandledRequest(GameMessageType messageType, RequestLink link, byte[] guid)
    {
        Log.Warning("{Link} 未实现的请求 {MessageType}({Id}) —— 回 UnrecognizedError 以免客户端重连循环",
            link, messageType, (ushort)messageType);

        // 这里只能回一个"无载荷"的失败响应: 我们并不知道该请求对应的响应类型,
        // 而 RequestResult != OK 时原厂 Respond 本来就不写载荷, 所以类型无关紧要。
        // 泛型参数用 DebugResponse 纯粹是借它当占位 —— 它的值从不被序列化,
        // 所以不需要真的造一个实例。
        Request<DebugResponse> request = new Request<DebugResponse>
        {
            RequestType = (ushort)messageType,
            Guid = guid,
            ResponseHandler = link,
            SerializeDelegate = MessageSerialization.Serialize,
        };
        request.Fail(RequestResult.UnrecognizedError);
    }

    private void OnUnhandledMessage(GameMessageType messageType, RequestLink link)
    {
        // 单向消息没有响应契约, 记录即可
        Log.Warning("{Link} 未实现的单向消息 {MessageType}({Id})", link, messageType, (ushort)messageType);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TCPClient.OnDataReceived -= OnDataReceived;
        TCPClient.OnClientDisconnected -= OnClientDisconnected;
        TCPClient.OnClientError -= OnClientError;
        TCPServer<RequestLink>.ClientConnected -= OnClientConnected;
        TCPServer<RequestLink>.ServerError -= OnServerError;
        _listener.Stop(reusable: false);
    }
}
