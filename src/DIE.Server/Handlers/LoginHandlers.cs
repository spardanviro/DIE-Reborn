using System;
using System.Threading.Tasks;
using ConductorCrafting;
using ConductorShop;
using DIE.Server.Data;
using DIE.Server.Net;
using DIE.Server.Steam;
using GameProtocol;
using Serilog;
using StunTCP;

namespace DIE.Server.Handlers;

/// <summary>
/// 登录握手 —— Phase 1 的门槛交付物。
///
/// 客户端侧的契约 (GameLinkClient.cs:102 OnConnectAuth / :204 OnLoginDataMessageRecieved):
///   1. 客户端连上后立刻发 LoginRequest(43)
///   2. 服务端回 LoginResponse{Success}
///      —— 但客户端的响应回调**只在失败时**释放自己, 成功时什么都不做
///   3. 服务端必须**再单独推送** LoginDataMessage(44, packetType 1)
///      —— OnLoginDataMessageRecieved 才会设置 _HasLoggedIn / _AuthTicket / _UserID /
///         SessionID, 触发挂起的 onDone, 冲刷排队的请求, 并让
///         CribStart.cs:714 调用 StartLoadLevel() 进主菜单
///
/// 顺序不能反: 响应的回调里才装好接收推送所需的状态。
/// </summary>
public sealed class LoginHandlers
{
    /// <summary>客户端自己会在 3 小时后丢弃 AuthTicket 并重新走 SteamAuthRequest, 这里给足余量。</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    /// <summary>等 Steam 核验的上限。平时几百毫秒; 客户端自己的请求超时是 20 秒。</summary>
    private static readonly TimeSpan SteamVerifyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 重新认证请求 (SteamAuthRequest) 里没有昵称, 插件把自称的 SteamID 以
    /// "&lt;原 Branch&gt;#SID:&lt;steamid&gt;" 附在 Branch 后面。只是"自称" —— 真假由 Steam 核验。
    /// </summary>
    public const string SteamIdClaimMarker = "#SID:";

    private readonly AccountStore _accounts;
    private readonly AccountRefreshQueue _refreshQueue;
    private readonly ISteamTicketVerifier _steam;

    /// <param name="steam">Steam 身份核验。不传就谁都不认 (全进共享账号) —— 单测用。</param>
    public LoginHandlers(AccountStore accounts, AccountRefreshQueue refreshQueue = null,
                         ISteamTicketVerifier steam = null)
    {
        _accounts = accounts;
        _refreshQueue = refreshQueue;
        _steam = steam ?? new RejectAllVerifier();
    }

    /// <summary>身份判定: 进哪个账号, 或者这次先别进 (Steam 暂时核验不了, 让客户端重连再试)。</summary>
    public sealed record IdentityDecision(Account Account, bool Retry, SteamVerdict Verdict, string Why);

    /// <summary>
    /// 这个人能以自称的 SteamID 进来吗? 交给 Steam 核验。
    ///
    /// | 情况 | 结果 |
    /// |---|---|
    /// | 没自称 SteamID (没装插件) | 共享账号, 不找 Steam |
    /// | Steam 核验通过 | 这个 SteamID 的账号 (没有就建) |
    /// | Steam 拒绝 (伪造 / 冒充 / 没有游戏) | 共享账号 |
    /// | Steam 暂时核验不了 | Retry —— 断开连接, 客户端自己重连重试 |
    ///
    /// 被拒进共享账号而不是回失败码: 失败码里没有安全的 (0/2/4 触发 Steam 重新校验
    /// 整个安装, 5/6/9 弹只能退出的框, 8 在大厅里显示"未知认证错误")。
    /// </summary>
    /// <param name="keepExistingName">重新认证不带昵称, 找已有账号时别把昵称改掉。</param>
    public IdentityDecision ResolveIdentity(ulong? claimedSteamId, byte[] ticket, string profileName,
                                            int language, bool keepExistingName = false) =>
        ResolveIdentityAsync(claimedSteamId, ticket, profileName, language, keepExistingName).GetAwaiter().GetResult();

    /// <summary>
    /// <see cref="ResolveIdentity"/> 的异步版 —— 请求处理走这个。
    ///
    /// 2026-09-17 审校: 以前在 TCP 回调线程上同步等 Steam, 一次最多 ~20 秒 (连 Steam 宽限 + 验票)。
    /// Steam 抽风时一堆登录把线程池占满, 所有请求一起卡住。现在等待期间不占线程。
    /// </summary>
    public async Task<IdentityDecision> ResolveIdentityAsync(ulong? claimedSteamId, byte[] ticket, string profileName,
                                                             int language, bool keepExistingName = false)
    {
        if (!claimedSteamId.HasValue)
        {
            return new IdentityDecision(_accounts.SharedAccount(profileName, language), false, null,
                "没有上报 SteamID (没装插件)");
        }

        ulong steamId = claimedSteamId.Value;
        SteamVerdict verdict = await _steam.VerifyAsync(ticket, steamId, SteamVerifyTimeout).ConfigureAwait(false);

        switch (verdict.Outcome)
        {
            case SteamAuthOutcome.Verified:
                Account account = keepExistingName
                    ? _accounts.FindBySteamId(steamId) ?? _accounts.ResolveOrCreate(steamId, null, language)
                    : _accounts.ResolveOrCreate(steamId, profileName, language);
                return new IdentityDecision(account, false, verdict, "Steam 核验通过");

            case SteamAuthOutcome.Rejected:
                return new IdentityDecision(_accounts.SharedAccount(profileName, language), false, verdict,
                    "Steam 拒绝: " + verdict.Detail);

            default:
                return new IdentityDecision(null, true, verdict, "Steam 暂时核验不了: " + verdict.Detail);
        }
    }

    /// <summary>从 Branch 里取插件附的自称 SteamID。没有返回 null。</summary>
    public static ulong? ParseSteamIdClaim(string branch)
    {
        if (string.IsNullOrEmpty(branch))
        {
            return null;
        }

        int at = branch.IndexOf(SteamIdClaimMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        return ulong.TryParse(branch.AsSpan(at + SteamIdClaimMarker.Length), out ulong steamId) && steamId != 0UL
            ? steamId
            : null;
    }

    /// <summary>
    /// Steam 暂时核验不了: 不回应, 直接断开 —— 客户端显示"正在连接服务器", 按
    /// {4,4,4,8,16,32} 秒退避重连, 重连时插件会取一张新票据。Steam 恢复后自动进来,
    /// 不用玩家做任何事。
    /// </summary>
    private static void AskToRetry(RequestLink link, IdentityDecision decision, ulong? claimed)
    {
        Log.Warning("{Link} SteamID {SteamId} {Why} —— 断开连接, 让客户端稍后重连再试",
            link, claimed, decision.Why);
        link.ForceClose();
    }

    private static void LogIdentity(RequestLink link, IdentityDecision decision, ulong? claimed)
    {
        Account account = decision.Account;

        if (decision.Verdict?.Outcome == SteamAuthOutcome.Verified)
        {
            SteamVerdict v = decision.Verdict;
            Log.Information("{Link} Steam 核验通过 SteamID {SteamId}{Family} -> userId={UserId} name={Name}",
                link, v.SteamId,
                v.OwnerSteamId != 0 && v.OwnerSteamId != v.SteamId ? $" (家庭共享, 游戏属于 {v.OwnerSteamId})" : "",
                account.UserId, account.ProfileName);
        }
        else if (claimed.HasValue)
        {
            Log.Warning("{Link} SteamID {SteamId} 没通过 ({Why}) —— 放进共享账号 userId={UserId}",
                link, claimed, decision.Why, account.UserId);
        }
        else
        {
            Log.Information("{Link} {Why} —— 使用共享账号 userId={UserId}", link, decision.Why, account.UserId);
        }
    }

    public void OnLoginRequest(LoginRequest request, RequestLink link, Request<LoginResponse> respond) =>
        _ = Guarded(link, "LoginRequest", OnLoginRequestAsync(request, link, respond));

    /// <summary>
    /// 异步处理的兜底: 同步部分的异常原本会冒到 RequestServer 那层关连接, 现在进了 Task, 在这里接住、同样关连接。
    /// 假验票器 (单测) 同步返回时整个处理照旧当场做完。
    /// </summary>
    private static async Task Guarded(RequestLink link, string what, Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Link} 处理 {What} 时异常, 关闭连接", link, what);
            link.ForceClose();
        }
    }

    private async Task OnLoginRequestAsync(LoginRequest request, RequestLink link, Request<LoginResponse> respond)
    {
        Log.Information(
            "{Link} LoginRequest v{Major}.{Minor}.{Build}.{Revision} branch={Branch} lang={Language} name={Name} ticket={TicketLen}B",
            link, request.VersionMajor, request.VersionMinor, request.VersionBuild, request.VersionRevision,
            request.Branch, request.Language, request.Name, request.SteamAuthTicket?.Length ?? 0);

        // 版本不匹配只记录, 绝不拦截。
        //
        // 返回 AuthResponseEnum 的 UnknownError(0) / InvalidVersion(2) / LoginFail(4) 会让
        // 客户端调用 MarkContentCorrupt() —— 那会让 Steam 重新校验整个游戏安装, 然后退出。
        // WrongBranch(5) / Maintenance(6) / AccountFrozen(9) 则弹出只有"退出"按钮的框。
        // 一个填错的枚举值就能把开发循环变成"重下 3GB", 所以这里只放行。
        if (request.VersionRevision != GameVersionConstants.Revision)
        {
            Log.Warning(
                "{Link} 客户端版本 {Actual} 与预期 {Expected} 不符 —— 仍然放行 (返回 InvalidVersion 会触发 Steam 重新校验安装)",
                link, request.VersionRevision, GameVersionConstants.Revision);
        }

        // 身份交给 Steam 核验, 和原版一样由 Steam 担保"他就是他"。
        // 原版在 SteamAuthTicket 里交加密应用票据 (要 Stunlock 的密钥才解得开);
        // 插件把同一个字段换成了会话票据, Name 里附上自称的 SteamID —— 见 ResolveIdentity。
        (ulong? claimedSteamId, string profileName) = AccountStore.ParseTaggedName(request.Name);
        IdentityDecision decision = await ResolveIdentityAsync(
            claimedSteamId, request.SteamAuthTicket, profileName, (int)request.Language).ConfigureAwait(false);

        if (decision.Retry)
        {
            AskToRetry(link, decision, claimedSteamId);
            return;
        }

        Account account = decision.Account;
        LogIdentity(link, decision, claimedSteamId);

        (byte[] authTicket, byte[] sessionId) = _accounts.IssueSession(
            account.UserId,
            link.RemoteEndPoint?.Address?.ToString(),
            SessionLifetime);

        link.UserId = account.UserId;
        link.AuthTicket = authTicket;
        link.SessionId = sessionId;
        link.ProfileName = account.ProfileName;
        link.HasLoggedIn = true;

        // ---- 第 1 步: 响应 ----
        LoginResponse response = default;
        response.Result = AuthResponseEnum.Success;
        response.QueuePosition = 0;
        response.LoginsPerSecond = 0f;
        respond.Respond(response);

        // ---- 第 2 步: 推送 (真正解锁客户端的那一步) ----
        LoginDataMessage data = BuildLoginData(account, authTicket, sessionId, Inventory(account.UserId));
        link.SendOneWay(GameMessageType.LoginDataMessage, ref data, ServerSerialization.Serialize);

        _accounts.ClearFirstLogin(account.UserId);

        Log.Information("{Link} 登录完成 userId={UserId} name={Name} —— 已推送 LoginDataMessage",
            link, account.UserId, account.ProfileName);
    }

    /// <summary>
    /// 重连路径: 客户端缓存着我们上次签发的 AuthTicket。
    /// 与 LoginRequest 不同, 这条**直接释放客户端**, 不需要额外推送。
    /// 传输抖动后和 3 小时 ticket 过期前都会走到, 长时间游玩必然命中。
    /// </summary>
    public void OnAuthRequest(AuthRequest request, RequestLink link, Request<AuthResponse> respond)
    {
        ulong? userId = _accounts.ResolveByAuthTicket(request.AuthTicket);

        AuthResponse response = default;
        response.Data = DIE.Server.Match.GameModeAvailability.DisabledFeatures();
        response.BanReason = default;
        response.BannedDays = 0;

        if (userId.HasValue)
        {
            link.UserId = userId.Value;
            link.AuthTicket = request.AuthTicket;
            link.HasLoggedIn = true;
            response.Result = AuthResponseEnum.Success;
            Log.Information("{Link} AuthRequest 重连成功 userId={UserId}", link, userId.Value);

            // 先回响应再补推 —— 客户端要先拿到 AuthResponse 才算"连上",
            // 否则那份 LoginDataMessage 会落在它还没准备好的时候。
            respond.Respond(response);
            PushRefreshIfPending(link);
            return;
        }
        else
        {
            // TicketOutdated(8) 是唯一"温和"的失败: 客户端会重新向 Steam 索要 app ticket
            // 然后走 SteamAuthRequest 重试, 不弹框也不退出。
            response.Result = AuthResponseEnum.TicketOutdated;
            Log.Warning("{Link} AuthRequest ticket 无效或过期 —— 回 TicketOutdated 让客户端重新走 Steam 认证", link);
        }

        respond.Respond(response);
    }

    /// <summary>
    /// 重连路径之二: 客户端的 AuthTicket 已过期, 带着新的 Steam ticket 来重认证。
    /// 同样直接释放客户端, 且响应里要带上新签发的 AuthTicket 和 UserID。
    ///
    /// ## 以前这里会悄悄换号
    ///
    /// 以前用 <c>ResolveOrCreate(link.ProfileName)</c>。可这条请求来的时候是**新连接**
    /// (GameLinkClient.cs:132: 已登录过、票据没了, 重连时发它而不是 LoginRequest),
    /// link 上没有昵称; 就算有, 存的也是去掉 SteamID 的纯昵称 —— 两种情况都解析成
    /// 共享账号。表现是玩满 3 小时 (客户端丢票据) 的人被换进共享账号。
    ///
    /// 现在: 同一条连接上已经登录过就沿用; 否则插件在 SteamAuthTicket 里交会话票据、
    /// 在 Branch 里附自称的 SteamID, 交给 Steam 核验 —— 和登录同一套。
    /// </summary>
    public void OnSteamAuthRequest(SteamAuthRequest request, RequestLink link, Request<SteamAuthResponse> respond) =>
        _ = Guarded(link, "SteamAuthRequest", OnSteamAuthRequestAsync(request, link, respond));

    private async Task OnSteamAuthRequestAsync(SteamAuthRequest request, RequestLink link, Request<SteamAuthResponse> respond)
    {
        Account account;
        if (link.HasLoggedIn && link.UserId != 0 && _accounts.Load(link.UserId) is Account current)
        {
            // 同一条连接上已经核验过了
            account = current;
        }
        else
        {
            ulong? claimed = ParseSteamIdClaim(request.Branch);
            IdentityDecision decision = await ResolveIdentityAsync(
                claimed, request.SteamAuthTicket, link.ProfileName, 0, keepExistingName: true).ConfigureAwait(false);

            if (decision.Retry)
            {
                AskToRetry(link, decision, claimed);
                return;
            }

            account = decision.Account;
            LogIdentity(link, decision, claimed);
        }

        (byte[] authTicket, byte[] sessionId) = _accounts.IssueSession(
            account.UserId, link.RemoteEndPoint?.Address?.ToString(), SessionLifetime);

        link.UserId = account.UserId;
        link.AuthTicket = authTicket;
        link.SessionId = sessionId;
        link.HasLoggedIn = true;

        SteamAuthResponse response = default;
        response.Result = AuthResponseEnum.Success;
        response.AuthTicket = authTicket;
        response.UserID = account.UserId;
        response.Data = DIE.Server.Match.GameModeAvailability.DisabledFeatures();
        response.BanReason = default;
        response.BannedDays = 0;
        respond.Respond(response);

        // 票据过期走的是这条重连路径, 和 AuthRequest 一样要补推欠着的刷新。
        PushRefreshIfPending(link);

        Log.Information("{Link} SteamAuthRequest 重认证成功 userId={UserId}", link, account.UserId);
    }

    /// <summary>
    /// 给一个已登录的连接**重推**一份 LoginDataMessage —— 让大厅刷新账号数据。
    ///
    /// ## 为什么要这么做
    ///
    /// 玩家实测: "还是无法实时更新账号经验, 得退出游戏重进才能看见。"
    ///
    /// 查下来经验早就落库了, 结算画面也确实弹出来了 (客户端日志有
    /// "Got rewards! Writing to file: lastRewards.data Size: 124")。断的是最后
    /// 一环: 大厅顶栏显示的经验来自 <c>AccountDataManager.AccountData</c>,
    /// 那份缓存只在 <c>SetAccountData</c> 时更新, 而调它的只有两处 ——
    ///
    ///   RequestAccountData 的回调 (AccountDataManager.cs:433)
    ///   LoginDataMessage 的处理器 (CribMain.cs:531)
    ///
    /// 而 <c>RequestAccountData</c> 在大厅里只被调试台、workbench 合成确认框、
    /// 重连弹窗调用 —— **局末没有任何地方调它**。大厅进程整局都活着 (日志里
    /// 同一个 conn 从对局前一直连到对局后), 握的是登录那一刻的快照。
    ///
    /// 所以走另一条: <c>LoginDataMessage</c> 本来就是**服务端主动推**的
    /// (登录时我们就是 SendOneWay 推过去的), 大厅的处理器里第一件事就是
    /// <c>AccountDataManager.SetAccountData(msg.AccountData, msg.UnlockedDLCs)</c>。
    ///
    /// ## 重推安全吗
    ///
    /// 处理器里除了 SetAccountData 还会重发 GetVanityURL / GetRewardSetup 两个
    /// 请求, 都是幂等查询。<c>msg.Result</c> 仍是 Success, 不会走封号/失败分支。
    /// 票据和会话 ID 用**连接上原来那一份**, 不重新签发 —— 换票据会让客户端
    /// 后续请求带着旧票据被拒。
    /// </summary>
    /// <summary>
    /// 这条连接的账号欠一次刷新的话, 补推一份。
    ///
    /// 挂在重连成功之后 —— 大厅是轮询式的短连接, 局末推送那一刻往往没人在,
    /// 只能等它下次来 (详见 <see cref="AccountRefreshQueue"/>)。
    /// </summary>
    private void PushRefreshIfPending(RequestLink link)
    {
        if (_refreshQueue == null || !_refreshQueue.TakeIfPending(link.UserId))
        {
            return;
        }

        PushAccountRefresh(link);
    }

    /// <returns>推出去了返回 true; 连接没登录过就跳过。</returns>
    public bool PushAccountRefresh(RequestLink link)
    {
        if (link == null || !link.HasLoggedIn || link.UserId == 0)
        {
            return false;
        }

        Account account = _accounts.Load(link.UserId);
        if (account == null)
        {
            return false;
        }

        InventorySnapshot inventory = Inventory(account.UserId);

        LoginDataMessage data = BuildLoginData(
            account, link.AuthTicket, link.SessionId, inventory);

        link.SendOneWay(GameMessageType.LoginDataMessage, ref data, ServerSerialization.Serialize);

        Log.Information("{Link} 已重推账号数据 (经验 {Xp}, 银币 {Silver}, 角色点 {Cp}, "
                        + "武器 {Uniques} 把, 堆叠物 {Stackables} 条, 装置 {Gadgets} 个)",
            link, account.StoryMapXp, account.Silver, account.CharacterPoints,
            inventory.Uniques.Count, inventory.Stackables.Count, inventory.Gadgets.Count);

        return true;
    }

    /// <summary>
    /// 大厅的**整个背包**就是这一条消息带过去的那份。
    ///
    /// ## 为什么不能只发 Uniques
    ///
    /// <c>CribStart.cs:751</c> / <c>CribMain.cs:530</c> 收到 LoginDataMessage 的第一件事是
    /// <c>InventoryManager.LoadInventory(msg.Uniques, msg.Stackables, msg.Gadgets)</c> ——
    /// 它**无条件重建** <c>_InventoryItemList</c>。而之后 <c>GetInventoryList</c> 看到
    /// 这个列表非 null 就直接回缓存, **永远不会**再发 GetInventoryRequest:
    ///
    ///   if ((_InventoryItemList == null &amp;&amp; !_LoadingInventory) || forceReload)
    ///
    /// 所以 <c>MenuHandlers.OnGetInventory</c> 那份"真数据"客户端根本不会去要。
    /// 这里发空数组的后果是:
    ///
    ///   * 打局掉的零件落了库, 但仓库 Parts 页永远是空的
    ///   * 商店买的蓝图当场能看见 (买完是本地往列表里塞的), **重进游戏就没了**
    ///   * 局末我们主动重推一份刷新, 反而把当次会话里攒的东西**清回只剩武器**
    ///
    /// 三个症状同一个根: 背包的真相在这条推送里, 不在 GetInventoryRequest 里。
    /// </summary>
    public InventorySnapshot Inventory(ulong userId) => new InventorySnapshot(
        _accounts.LoadUniques(userId),
        _accounts.LoadStackables(userId),
        _accounts.LoadGadgets(userId));

    /// <summary>登录推送要带的那份背包 —— 三条列表一起取, 免得漏掉其中一条。</summary>
    public readonly record struct InventorySnapshot(
        System.Collections.Generic.IReadOnlyList<Unique> Uniques,
        System.Collections.Generic.IReadOnlyList<Stackable> Stackables,
        System.Collections.Generic.IReadOnlyList<Gadget> Gadgets);

    public static LoginDataMessage BuildLoginData(Account account, byte[] authTicket, byte[] sessionId, InventorySnapshot inventory)
    {
        LoginDataMessage msg = default;
        msg.Result = AuthResponseEnum.Success;
        msg.AuthTicket = authTicket;
        msg.UserID = account.UserId;
        msg.Currency = AccountMapper.ToCurrency(account);
        msg.GoldSpent = 0;
        msg.SilverSpent = 0;
        msg.CPSpent = 0;
        msg.UpsellStep = 0;
        msg.UpsellTime = 0;
        msg.AccountData = AccountMapper.ToAccountsData(account);
        msg.FirstLogin = account.FirstLogin;
        // 大厅的背包完全由这三条决定, 见 Inventory() 上面那段注释。
        msg.Stackables = inventory.Stackables;  // 蓝图 / 零件 / 改装图 / 暗码 / 宝箱
        msg.Uniques = inventory.Uniques;        // 初始装备 (Paddle + ScrapShotgun) 也走这里
        msg.Gadgets = inventory.Gadgets;
        msg.UnlockedDLCs = Array.Empty<DLCPackage>();
        msg.DisabledFeatures = DIE.Server.Match.GameModeAvailability.DisabledFeatures();
        msg.GameplayChanges = Array.Empty<GameplayChange>();
        msg.GameplayRevision = ProtocolDefaults.GameplayRevision;
        msg.LoginRewardData = Array.Empty<LoginRewardData>();
        msg.IsExpoUser = account.IsExpoUser;
        msg.SessionID = sessionId;
        msg.BanReason = (ReportType)account.BanReason;
        msg.BannedDays = account.BannedDays;
        // 空串最安全 —— 客户端会把它赋给 WelcomeDataManager.URL 并发起 HTTP 请求,
        // 填一个不通的地址会让请求一直挂着。
        msg.WelcomeDataURL = string.Empty;
        // 客户端做 new DateTime(msg.ServerTime), 必须是合法的 .NET tick 数,
        // 量级错了商店的倒计时会变成乱码。
        msg.ServerTime = DateTime.UtcNow.Ticks;

        return msg;
    }
}

/// <summary>抓包实测的客户端版本 (captures\043_LoginRequest.bin), 与 GameVersion.cs 一致。</summary>
public static class GameVersionConstants
{
    public const int Major = 0;
    public const int Minor = 8;
    public const int Build = 5;
    public const int Revision = 38860;
}
