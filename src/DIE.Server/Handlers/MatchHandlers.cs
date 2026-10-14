using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using ConductorShop;
using DIE.Server.Data;
using DIE.Server.Match;
using DIE.Server.Net;
using MatchmakingProtocol;
using Serilog;
using StunTCP;

namespace DIE.Server.Handlers;

/// <summary>
/// 匹配服务器 (TCP 2555) 的请求处理器。
/// </summary>
public sealed class MatchHandlers
{
    private readonly MatchStore _matches;
    private readonly MatchLauncher _launcher;
    private readonly string _publicHost;
    private readonly MatchmakingService _queue;
    private readonly Func<byte[], ulong?> _resolveUser;
    private readonly LeavePunishmentStore _punishments;
    private readonly ConcurrentDictionary<MatchLink, MatchLinkQueueClient> _queueClients = new();

    /// <param name="launcher">开局入口 (占端口、登记、起战斗服), 和排队共用。</param>
    /// <param name="publicHost">
    /// 服务器的公网 IP 或域名, 发给从公网隔着路由器进来的客户端。见 <see cref="MatchHostAddress"/>。
    /// </param>
    /// <param name="queue">排队和自定义房间。传 null 就是只有 Solo (单测)。</param>
    /// <param name="resolveUser">凭 AuthData 里的会话票查账号 (<c>AccountStore.ResolveByAuthTicket</c>)。</param>
    /// <param name="punishments">离局惩罚的账本。传 null 就是不罚。</param>
    public MatchHandlers(MatchStore matches, MatchLauncher launcher, string publicHost = null,
                         MatchmakingService queue = null, Func<byte[], ulong?> resolveUser = null,
                         LeavePunishmentStore punishments = null)
    {
        _matches = matches;
        _launcher = launcher;
        _publicHost = publicHost;
        _queue = queue;
        _resolveUser = resolveUser;
        _punishments = punishments;
    }

    /// <summary>这条连接在排队服务里的化身 (一条连接一个, 断开时撤掉)。</summary>
    private MatchLinkQueueClient ClientOf(MatchLink link) =>
        _queueClients.GetOrAdd(link, l => new MatchLinkQueueClient(l, ClientFacingAddress));

    /// <summary>
    /// 连接断了: 它若在排队或在房间里, 撤出来。客户端取消排队时是**先断连接**再发
    /// LeaveQueueMessage 的, 所以这里就是"离队"的主路径。
    /// </summary>
    public void OnDisconnected(MatchLink link)
    {
        if (_queueClients.TryRemove(link, out MatchLinkQueueClient client))
        {
            _queue?.Leave(client, "匹配连接断开");
        }
    }

    /// <summary>
    /// 凭 AuthData 认出这条连接是谁 (T14: AuthData 的第一个字段就是我们签发的会话票)。
    /// 认不出不拦 —— 票据校验才是开局的门槛, 这里只为日志和"谁在排队"。
    /// </summary>
    private void Identify(MatchLink link, AuthData auth)
    {
        if (link.UserId == 0 && _resolveUser != null && auth.AuthTicket is { Length: > 0 } ticket)
        {
            link.UserId = _resolveUser(ticket) ?? 0;
        }
    }

    /// <summary>
    /// 9 — "我是否有一局正在进行?" 也就是**掉线重连**的入口。
    ///
    /// 这是匹配链路上最先到达的请求: <c>CribMain.cs:761</c> 在主菜单初始化时就调
    /// <c>Matchmaker.CheckIfMatchIsRunning()</c>, 拿不到结果每 2 秒重试 (最多 5 次);
    /// 另外每次游戏进程退出而**没有**留下 lastRewards.data 时也会问一次
    /// (<c>Matchmaker.OnMatchmakedGameEnded</c> -&gt; 重连弹窗) —— 那正是"崩了 / 中途退了"的情形。
    ///
    /// 回 true 客户端就弹重连框 (<c>GUI_RejoinGamePopup</c>), 玩家点"重新加入"就拿我们给的
    /// 地址重开游戏, 战斗服会把他接回原来的角色 (<c>LidgrenHost.CanRejoin</c> /
    /// <c>TryReattachPlayer</c>); 点"离开"则发 <see cref="OnLeaveMatch"/>。
    ///
    /// 打完的局不提示 (客户端那时有结算文件, 本来也不会问)。认不出身份就只能回"没有"。
    ///
    /// IPAddress / MatchGUID 给空数组而非 null —— 线上两者等价 (都写成长度 0),
    /// 但空数组能让客户端的 <c>new IPAddress(bytes)</c> 之类的调用不至于 NRE。
    /// </summary>
    public void OnGetActiveMatch(GetActiveMatchRequest request, MatchLink link, Request<GetActiveMatchResponse> respond)
    {
        Identify(link, request.AuthData);

        GetActiveMatchResponse response = default;
        response.AuthResult = AuthResult.Success;
        response.IsAFKKicked = false;
        response.IPAddress = System.Array.Empty<byte>();
        response.Port = 0;
        response.MatchGUID = System.Array.Empty<byte>();

        LidgrenHost host = link.UserId == 0 ? null : _launcher.Hosts.FindRejoinable(link.UserId);
        if (host == null)
        {
            // 这条请求会连着来好几次, 用 Debug 级别, 免得刷屏盖住真正的信息
            Log.Debug("{Link} (user={UserId}) 查询进行中的对局 —— 无", link, link.UserId);
            response.HasMatch = false;
            respond.Respond(response);
            return;
        }

        System.Net.IPAddress address = ClientFacingAddress(link);
        Log.Information("{Link} (user={UserId}) 有一局可以重连: UDP {Port} 地图 {Map} -> 告诉它 {Ip}:{Port}",
            link, link.UserId, host.Port, host.MapIndex, address, host.Port);

        response.HasMatch = true;
        response.IPAddress = address.GetAddressBytes();
        response.Port = (ushort)host.Port;
        response.MatchGUID = host.MatchGuid;
        respond.Respond(response);
    }

    /// <summary>
    /// 单人/教程开局 —— Phase 2 的主干。
    ///
    /// 客户端拿到成功响应后会走 <c>Matchmaker.StartGameInMatchmaking</c> →
    /// <c>GameStartScript.TryStartGame</c> → <c>Process.Start("Dead Island Epidemic.exe",
    /// "+connect &lt;ip&gt; &lt;port&gt; +authticket … +bindip … +region N")</c>。
    ///
    /// Phase 2 的验收点是"Game.exe 带着正确参数被拉起来", 已达成。
    /// 3B 起这里还会先把该端口的 Lidgren 端点起起来, 所以战斗客户端能连上;
    /// 但连上之后没有世界数据可收 —— 那是 3C。
    /// </summary>
    public void OnSoloServerCreate(SoloServerCreateRequest request, MatchLink link, Request<SoloServerCreated> respond)
    {
        SoloServerCreated response = default;
        response.AuthResult = AuthResult.Success;
        response.IPAddress = System.Array.Empty<byte>();
        response.Port = 0;

        TicketOwner owner = _matches.ConsumeTicket(request.MatchmakingTicket);
        if (owner == null)
        {
            // 票据无效/过期/已用过。回 InvalidMatchmakingTicket 而不是含糊的失败 ——
            // 客户端会弹一个明确的提示框并回到主菜单, 我们也能从日志里认出这条路径。
            Log.Warning("{Link} 票据无效或已过期 ({Length}B), 拒绝开局", link, request.MatchmakingTicket?.Length ?? 0);
            response.JoinQueueResult = JoinQueueResult.InvalidMatchmakingTicket;
            respond.Respond(response);
            return;
        }

        link.UserId = owner.UserId;

        // 2026-09-17 审校: 单人开局不走排队, 以前地图和数量都不查 ——
        // 伪造请求能开没做完的模式, 反复开局能把端口和内存耗光。
        if (!GameModeAvailability.IsMapRunnable(request.MapIndex))
        {
            Log.Warning("{Link} (user={UserId}) 想单人开没开放的地图 {MapIndex} ({QueueType}), 拒绝",
                link, owner.UserId, request.MapIndex, request.Type);
            response.JoinQueueResult = JoinQueueResult.SessionRequestFailed;
            respond.Respond(response);
            return;
        }

        int live = _launcher.Hosts.PruneAndCountLiveMatches(owner.UserId, DateTime.UtcNow);
        if (live >= MatchHostRegistry.MaxLiveMatchesPerUser)
        {
            Log.Warning("{Link} (user={UserId}) 已经挂着 {Live} 局没打完, 拒绝再开", link, owner.UserId, live);
            response.JoinQueueResult = JoinQueueResult.SessionRequestFailed;
            respond.Respond(response);
            return;
        }

        // 下发给客户端的战斗服地址 —— 必须是**客户端视角**的地址。见 MatchHostAddress。
        System.Net.IPAddress matchHost = ClientFacingAddress(link);

        // Solo 的票里只有开票人自己 (老格式也解成这样)。
        IReadOnlyList<MatchSeat> seats = QueueRosterCodec.Decode(owner.QueueData, owner.UserId);

        // 端点必须在**回复之前**就绪 —— 客户端拿到地址会立刻拉起 Game.exe 去连。
        LaunchedMatch launched = _launcher.Launch(seats, request.Type, request.MapIndex,
            matchHost.ToString(), infectionLevel: 0, out string failure);
        if (launched == null)
        {
            Log.Error("{Link} {Why}, 拒绝开局 (避免把连不上的地址发给客户端)", link, failure);
            response.JoinQueueResult = JoinQueueResult.SessionRequestFailed;
            respond.Respond(response);
            return;
        }

        Log.Information("{Link} 已分配对局 (user={UserId} 队列={QueueType} 地图={MapIndex}) -> {Ip}:{Port}",
            link, owner.UserId, request.Type, request.MapIndex, matchHost, launched.Port);

        response.JoinQueueResult = JoinQueueResult.Success;
        response.IPAddress = matchHost.GetAddressBytes();
        response.Port = launched.Port;
        respond.Respond(response);
    }

    /// <summary>
    /// 发给这条连接的战斗服地址 —— **客户端视角**的地址, 见 <see cref="MatchHostAddress"/>。
    /// 公网进来却没配公网地址时吼一声 (它的游戏会连不上战斗服)。
    /// </summary>
    public System.Net.IPAddress ClientFacingAddress(MatchLink link)
    {
        System.Net.IPEndPoint remote = link.RemoteEndPoint;
        System.Net.IPAddress address = MatchHostAddress.Resolve(link.LocalEndPoint, remote, _publicHost);

        if (MatchHostAddress.NeedsPublicHost(link.LocalEndPoint, remote)
            && MatchHostAddress.ParseOrLookUp(_publicHost ?? "") == null)
        {
            Log.Warning("{Link} 是从公网进来的, 但没配 (或解析不了) 服务器公网地址 —— 发给它的是内网地址 {Ip}, "
                        + "它的游戏会连不上战斗服。在 {File} 里填上公网 IP 或域名", link, address,
                MatchHostAddress.PublicHostFile);
        }

        return address;
    }

    // ==================================================================
    // 排队 (T19)
    // ==================================================================

    /// <summary>
    /// 1 — 组队排队。只有队长发; 全队的选择在票据里 (<see cref="QueueRosterCodec"/>)。
    ///
    /// 回完响应再凑局 —— 顺序反了客户端的"找到对局"状态会被清掉 (见 MatchmakingService 类说明)。
    /// </summary>
    public void OnJoinQueue(JoinQueueRequest request, MatchLink link, Request<JoinQueueResponse> respond)
    {
        Identify(link, request.AuthData);

        if (!TryConsumeTicket(request.MatchmakingTicket, link, "排队", out TicketOwner owner, respond))
        {
            return;
        }

        // 十字路口的难度档存在票据里 (排队请求没有这个字段), 见 QueueRosterCodec
        IReadOnlyList<MatchSeat> seats =
            QueueRosterCodec.Decode(owner.QueueData, owner.UserId, out int difficulty);

        // owner.UserId 是票据持有者 = 发起排队的人。离局罚期只查他自己, 不整队连坐
        // (见 MatchmakingService.JoinQueue)。
        JoinQueueOutcome outcome = _queue?.JoinQueue(ClientOf(link), request.Type, seats,
                                       SkillOf(request.SkillDatas), difficulty, owner.UserId)
                                   ?? JoinQueueOutcome.Of(JoinQueueResult.SessionRequestFailed);

        respond.Respond(new JoinQueueResponse
        {
            AuthResult = AuthResult.Success,
            JoinQueueResult = outcome.Result,
            TimeoutData = outcome.TimeoutData,
        });

        _queue?.Tick();
    }

    /// <summary>
    /// 这一队的段位 = 每个队员拾荒奖牌总数的平均值。
    ///
    /// 客户端每个队员报一份 <c>SkillRatingData</c> (总数, 金, 银, 铜), 构造点
    /// <c>SteamLobbyData.cs:455</c>。没报就算 0 —— 练习/Horde 里所有人本来都是 0。
    /// </summary>
    private static int SkillOf(SkillRatingData[] skills)
    {
        if (skills == null || skills.Length == 0)
        {
            return 0;
        }

        return (int)Math.Round(skills.Average(s => (double)s.TotalMedals));
    }

    /// <summary>3 — 队长报"我这队已准备 N 人" (累计值)。</summary>
    public void OnReadyForMatch(ReadyForMatchMessage message, MatchLink link)
    {
        _queue?.ReportReady(ClientOf(link), message.MatchGuid, message.PlayersReadyInParty);
    }

    /// <summary>7 — 取消排队。多半到不了 (客户端先断连接再发它), 断线那条路 (<see cref="OnDisconnected"/>) 才是主路径。</summary>
    public void OnLeaveQueue(LeaveQueueMessage message, MatchLink link)
    {
        if (_queueClients.TryGetValue(link, out MatchLinkQueueClient client))
        {
            _queue?.Leave(client, "客户端取消排队");
        }
    }

    // ==================================================================
    // 自定义房间 (T20)
    // ==================================================================

    /// <summary>14 — 建自定义房间。只有内部调试开局窗口会发 (由 Features.CribDebug 控制显示)。</summary>
    public void OnCreateCustomGame(CreateCustomGameRequest request, MatchLink link, Request<JoinQueueResponse> respond)
    {
        Identify(link, request.AuthData);

        if (!TryConsumeTicket(request.MatchmakingTicket, link, "建房", out TicketOwner owner, respond))
        {
            return;
        }

        MatchSeat seat = QueueRosterCodec.Decode(owner.QueueData, owner.UserId)[0];
        CustomRoomSettings settings = new CustomRoomSettings(request.ServerName, request.Map,
            request.TeamSize, request.TeamCount, request.NumberOfBots, request.ServerLevel);

        JoinQueueResult result = _queue?.CreateRoom(ClientOf(link), settings, seat)
                                 ?? JoinQueueResult.SessionRequestFailed;

        respond.Respond(new JoinQueueResponse
        {
            AuthResult = AuthResult.Success,
            JoinQueueResult = result,
            TimeoutData = Array.Empty<MatchmakingTimeOutData>(),
        });

        _queue?.Tick();
    }

    /// <summary>15 — 进别人的自定义房间。</summary>
    public void OnJoinCustomGame(JoinCustomGameRequest request, MatchLink link, Request<JoinQueueResponse> respond)
    {
        Identify(link, request.AuthData);

        if (!TryConsumeTicket(request.MatchmakingTicket, link, "进房", out TicketOwner owner, respond))
        {
            return;
        }

        MatchSeat seat = QueueRosterCodec.Decode(owner.QueueData, owner.UserId)[0];
        JoinQueueResult result = _queue?.JoinRoom(ClientOf(link), request.CustomGameID, seat)
                                 ?? JoinQueueResult.SessionRequestFailed;

        respond.Respond(new JoinQueueResponse
        {
            AuthResult = AuthResult.Success,
            JoinQueueResult = result,
            TimeoutData = Array.Empty<MatchmakingTimeOutData>(),
        });

        _queue?.Tick();
    }

    /// <summary>16 — 房间列表。</summary>
    public void OnGetCustomGamesList(GetCustomGamesListRequest request, MatchLink link, Request<GetCustomGamesListResponse> respond)
    {
        Identify(link, request.AuthData);

        IReadOnlyList<CustomGameListData> rooms = _queue?.ListRooms() ?? Array.Empty<CustomGameListData>();
        Log.Debug("{Link} 查房间列表: {Count} 个", link, rooms.Count);

        respond.Respond(new GetCustomGamesListResponse
        {
            AuthResult = AuthResult.Success,
            CustomGames = rooms.ToArray(),
        });
    }

    // ==================================================================
    // 其余三个
    // ==================================================================

    /// <summary>
    /// 8 — 可观战的对局 (调试开局窗口里的观战列表)。
    ///
    /// 列的是**还在打**的局: 模拟层已就绪、还没打完。客户端选一条之后自己用
    /// "+observer +connect ip port" 起游戏 (<c>DebugPlayWindow.JoinGame</c>), 不再经过我们 ——
    /// 所以地址必须是这条连接视角的地址。
    /// </summary>
    public void OnGetObservableMatches(GetObservableMatchesRequest request, MatchLink link, Request<GetObservableMatchesResponse> respond)
    {
        Identify(link, request.AuthData);

        System.Net.IPAddress address = ClientFacingAddress(link);

        ObservableMatchData[] matches = _launcher.Hosts.Snapshot()
            .Where(host => host.SimulationReady && !host.MatchOver)
            .Select(host => new ObservableMatchData
            {
                MatchGUID = host.MatchGuid,
                IPAddress = address.GetAddressBytes(),
                Port = (ushort)host.Port,
                Type = host.QueueType,
                Name = $"{MapManagerData.GetMapName(host.MapIndex)} ({host.ConnectedClients} 人在线)",
            })
            .ToArray();

        Log.Debug("{Link} 查可观战的对局: {Count} 局", link, matches.Length);

        respond.Respond(new GetObservableMatchesResponse
        {
            AuthResult = AuthResult.Success,
            Matches = matches,
        });
    }

    /// <summary>
    /// 13 — 放弃进行中的对局 (重连弹窗上点"离开")。
    ///
    /// 这算**中途跑了**, 要记一次离局惩罚 (<see cref="LeavePunishmentStore"/>): 一段时间内不能排队,
    /// 客户端会弹"还要等 X 分钟"。已经打完的那一局不算, 教程也不罚 (客户端自己也把教程排除在外)。
    ///
    /// 到这时为止用掉的消耗品当场扣掉, 放进响应的 ConsumablesLost 让客户端也从背包里减
    /// (2026-09-15 补; 以前消耗品根本没带进局, 这里一直回空)。
    /// </summary>
    public void OnLeaveMatch(LeaveMatchRequest request, MatchLink link, Request<LeaveMatchResponse> respond)
    {
        Identify(link, request.AuthData);

        LidgrenHost host = request.SessionGUID is { Length: > 0 } guid ? _launcher.Hosts.Find(guid) : null;
        LeaveMatchResult result = LeaveMatchResult.Success;
        IReadOnlyList<(ushort Id, int Used)> consumed = Array.Empty<(ushort, int)>();

        if (host == null)
        {
            result = LeaveMatchResult.NoActiveSession;
            Log.Information("{Link} (user={UserId}) 请求放弃对局, 但那一局已经不在了 —— 不罚", link, link.UserId);
        }
        else if (host.MatchOver)
        {
            // 已经打完的局: 客户端只是没收到结算, 不算跑
            Log.Information("{Link} (user={UserId}) 放弃的是已经打完的一局 (UDP {Port}) —— 不罚",
                link, link.UserId, host.Port);
        }
        else if (_punishments != null && _punishments.IsExempt(link.UserId))
        {
            consumed = host.MarkAbandoned(link.UserId);
            Log.Information("{Link} (user={UserId}) 中途放弃 UDP {Port} 的对局 —— 在免罚名单上, 不罚",
                link, link.UserId, host.Port);
        }
        else if (_punishments != null && link.UserId != 0 && host.QueueType != QueueType.Tutorial)
        {
            consumed = host.MarkAbandoned(link.UserId);
            LeavePunishment punishment = _punishments.Punish(link.UserId, DateTime.UtcNow);
            Log.Information("{Link} (user={UserId}) 中途放弃 UDP {Port} 的对局 —— 第 {Strikes} 次, "
                            + "罚到 {Until:HH:mm:ss} 之前不能排队",
                link, link.UserId, host.Port, punishment.Strikes, punishment.UntilUtc.ToLocalTime());
        }
        else
        {
            consumed = host.MarkAbandoned(link.UserId);
            Log.Information("{Link} (user={UserId}) 中途放弃 UDP {Port} 的对局 ({Queue}) —— 不罚",
                link, link.UserId, host.Port, host.QueueType);
        }

        respond.Respond(new LeaveMatchResponse
        {
            AuthResult = AuthResult.Success,
            Result = result,
            ConsumablesLost = consumed
                .Select(c => new StackableUpdate(link.UserId, c.Id, StackableType.Consumable, -c.Used, default))
                .ToArray(),
        });
    }

    /// <summary>
    /// 19 — 十字路口排久了加一档难度。十字路口没开放 (GameModeAvailability), 正常到不了;
    /// 客户端也不看回应 (<c>GUI_PlayPartyWindow.cs:107</c> 的回调是空的)。
    /// </summary>
    public void OnAddRaidDifficulty(AddRaidDifficultyRequest request, MatchLink link, Request<AddRaidDifficultyResponse> respond)
    {
        Log.Information("{Link} 请求把十字路口难度加到 {Difficulty} —— 十字路口未开放, 忽略", link, request.NewDifficulty);
        respond.Respond(new AddRaidDifficultyResponse { Result = AddRaidDifficultyResult.Value0 });
    }

    /// <summary>
    /// 校验并消费票据; 不行就直接回 InvalidMatchmakingTicket (客户端弹明确的提示框)。
    /// </summary>
    private bool TryConsumeTicket(byte[] ticket, MatchLink link, string what, out TicketOwner owner,
                                  Request<JoinQueueResponse> respond)
    {
        owner = _matches.ConsumeTicket(ticket);
        if (owner == null)
        {
            Log.Warning("{Link} {What}: 票据无效或已过期 ({Length}B)", link, what, ticket?.Length ?? 0);
            respond.Respond(new JoinQueueResponse
            {
                AuthResult = AuthResult.Success,
                JoinQueueResult = JoinQueueResult.InvalidMatchmakingTicket,
                TimeoutData = Array.Empty<MatchmakingTimeOutData>(),
            });
            return false;
        }

        link.UserId = owner.UserId;
        return true;
    }
}
