using ConductorCrafting;

namespace MatchmakingProtocol;

/// <summary>
/// 匹配协议 (TCP 2555) 的操作码。
///
/// 逐字取自 CRIB 树 <c>MatchmakingProtocol\MatchmakingMessageType.cs</c>。
/// 编号是不连续的 (8 和 13 排在后面), 这是原厂枚举里的真实顺序, 不要"整理"它。
/// </summary>
public enum MatchmakingMessageType : ushort
{
    JoinQueueRequest = 1,
    ServerFoundMessage = 2,
    ReadyForMatchMessage = 3,
    MatchupFoundMessage = 4,
    ReadyTimeoutMessage = 5,
    FindingServerMessage = 6,
    LeaveQueueMessage = 7,
    GetObservableMatchesRequest = 8,
    GetActiveMatchRequest = 9,
    SoloServerCreateRequest = 10,
    MaintenanceModeMessage = 11,
    PlayersReadyCount = 12,
    LeaveMatchRequest = 13,
    CreateCustomGameRequest = 14,
    JoinCustomGameRequest = 15,
    GetCustomGamesListRequest = 16,
    DisabledFeaturesUpdatedMessage = 17,
    CustomGameInfoMessage = 18,
    AddRaidDifficultyRequest = 19,
}

/// <summary>
/// 认证结果。取自 CRIB 树 <c>MatchmakingProtocol\AuthResult.cs</c>。
///
/// 服务端只应返回 <see cref="Success"/>。其余值会让客户端弹本地化错误框
/// (走 <c>RequestManager.HandleJoinQueueResult</c>) —— 比 GameProtocol 那边的
/// <c>MarkContentCorrupt</c> 温和, 不会触发 Steam 重新校验, 但也没有理由返回。
/// </summary>
public enum AuthResult
{
    UnknownError,
    Success,
    InvalidTicket,
    InvalidVersion,
    WrongBranch,
    Maintenance,
    UserCapReached,
    DisabledFeaturesOutdated,
    OutdatedTicket,
    AccountFrozen,
}

/// <summary>取自 CRIB 树 <c>MatchmakingProtocol\JoinQueueResult.cs</c>。</summary>
public enum JoinQueueResult
{
    FailedToAuth,
    Success,
    UserCapReached,
    InvalidNumberOfMembers,
    SessionRequestFailed,
    InvalidMatchmakingTicket,
    PartyMemberIsTimedOut,
    WrongSoloData,
    InvalidDifficulty,
}

/// <summary>
/// 每个匹配请求都带的认证信息块。
///
/// **字段名在原厂是混淆的** (<c>_00231je</c> .. <c>_00237je</c>), 语义由唯一的构造点定案 (T14):
/// <code>
/// // MatchmakingLinkClient.cs:42
/// internal static AuthData GetAuthData() =&gt; new AuthData(
///     GameLinkClient.AuthTicket,
///     GameVersion.Major, GameVersion.Minor, GameVersion.Build, GameVersion.Revision,
///     SteamWrapperClient.Instance.GetCurrentBetaName(),
///     DisabledFeaturesVersion);
/// </code>
/// 和 1555 上的 <c>AuthRequest</c> 字段一一对应。
///
/// <see cref="AuthTicket"/> 就是**我们登录时签发的会话票** (<c>sessions.auth_ticket</c>,
/// 经 LoginDataMessage / SteamAuthResponse 下发)。所以匹配链路凭它就能认出是哪个账号 ——
/// 两条 TCP 会话之间不需要别的纽带。
/// </summary>
public struct AuthData
{
    /// <summary>登录会话票 (32 字节, 我们签发)。</summary>
    public byte[] AuthTicket;
    public int VersionMajor;
    public int VersionMinor;
    public int VersionBuild;
    public int VersionRevision;

    /// <summary>Steam 分支名 (正式版是 "public")。</summary>
    public string Branch;

    /// <summary>客户端手里那份禁用功能表的版本号。</summary>
    public int DisabledFeaturesRevision;
}

/// <summary>
/// 客户端轮询"我是否有一局正在进行"。
///
/// <c>CribMain.cs:761</c> 在主菜单初始化时就调 <c>Matchmaker.CheckIfMatchIsRunning()</c>,
/// 失败后每 2 秒重试 —— 所以匹配服务器一上线, 这个请求就会持续打进来。
/// </summary>
public struct GetActiveMatchRequest
{
    public AuthData AuthData;
}

public struct GetActiveMatchResponse
{
    public AuthResult AuthResult;
    public bool HasMatch;
    public bool IsAFKKicked;
    public byte[] IPAddress;
    public ushort Port;
    public byte[] MatchGUID;
}

/// <summary>
/// 单人/教程开局。这是 Phase 2 的主干路径 —— 一问一答, 不涉及大厅和准备阶段的推送时序。
///
/// 客户端流程 (<c>Matchmaker.cs:168</c>):
/// GetMatchmakingTicketRequest (1555) → 拿到票据 → 本请求 (2555) →
/// <see cref="SoloServerCreated"/> → <c>Matchmaker.StartGameInMatchmaking</c> →
/// <c>Process.Start("Dead Island Epidemic.exe", "+connect ip port ...")</c>
/// </summary>
public struct SoloServerCreateRequest
{
    public AuthData AuthData;
    public byte[] MatchmakingTicket;
    public QueueType Type;
    public int MapIndex;
}

public struct SoloServerCreated
{
    public AuthResult AuthResult;
    public JoinQueueResult JoinQueueResult;

    /// <summary>战斗服地址, 4 字节网络序 —— 客户端用 <c>new IPAddress(bytes)</c> 还原。</summary>
    public byte[] IPAddress;

    public ushort Port;
}
