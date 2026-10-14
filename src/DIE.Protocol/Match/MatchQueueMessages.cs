using ConductorCrafting;
using StunCore;

namespace MatchmakingProtocol;

// 排队 (T19) 这一路的消息。
//
// 结构体逐字取自 CRIB 树 MatchmakingProtocol\*.cs 和 -8je\*.cs。有一半在原厂就是混淆名
// (_0023kke 这类), 这里的名字取自**客户端怎么用它** —— 每个类型的注释都写了出处,
// 不是按字段类型猜的。字段顺序 = 线上顺序, 不要调整。
//
// 整条时序 (服务端只跟**队长**的匹配连接打交道, 队员之间走 Steam 大厅转发):
//
//   队长 -> JoinQueueRequest              队员的选择都在票据里 (GetMatchmakingTicketRequest)
//   <- JoinQueueResponse(Success)         客户端显示"匹配中..."
//   <- MatchupFoundMessage                客户端弹"找到对局"+ 15 秒准备倒计时, 转发给队员
//   -> ReadyForMatchMessage (可多次)      队长报"我这队已准备 N 人", 累计值
//   <- PlayersReadyCount                  整局已准备人数, 客户端刷新弹窗上的格子
//   <- FindingServerMessage               全员准备好了, 客户端显示"等待服务器"
//   <- ServerFoundMessage (请求)          战斗服地址; 队长回 ServerFoundAffirmativeMessage
//                                         并经 Steam 大厅发给队员, 各自拉起 Game.exe
//   <- ReadyTimeoutMessage                有人没点准备: 点了的回队列, 没点的踢出匹配

/// <summary>
/// 排队请求。只有队长发, 一队一条。
///
/// 取自 <c>MatchmakingProtocol\JoinQueueRequest.cs</c>, 构造点 <c>Matchmaker.cs:709</c>。
/// </summary>
public struct JoinQueueRequest
{
    public AuthData AuthData;

    /// <summary>1555 上刚签发的票据 —— 全队每个人的角色/装备都编在里面。</summary>
    public byte[] MatchmakingTicket;

    /// <summary>每个队员一份, 顺序同大厅成员列表。私服不按段位匹配, 只记录。</summary>
    public SkillRatingData[] SkillDatas;

    public QueueType Type;
}

/// <summary>
/// 段位数据。字段在原厂是混淆的 (<c>_00239je / _0023ake / _0023bke / _0023cke</c>),
/// 语义取自构造点 <c>SteamLobbyData.cs:455</c>:
/// <c>new SkillRatingData(金+银+铜, 金, 银, 铜)</c> —— 数的是拾荒模式拿过的奖牌。
/// </summary>
public struct SkillRatingData
{
    public int TotalMedals;
    public int GoldMedals;
    public int SilverMedals;
    public int BronzeMedals;
}

/// <summary>
/// 排队 / 建房 / 进房的共用响应 (三个请求都回它)。
///
/// 取自 <c>MatchmakingProtocol\JoinQueueResponse.cs</c>。
/// </summary>
public struct JoinQueueResponse
{
    public AuthResult AuthResult;
    public JoinQueueResult JoinQueueResult;

    /// <summary>只在 <see cref="JoinQueueResult.PartyMemberIsTimedOut"/> 时有内容。私服不罚离局, 恒空。</summary>
    public MatchmakingTimeOutData[] TimeoutData;
}

/// <summary>
/// 离局惩罚数据。原厂字段 <c>_0023a8d</c> (ulong) / <c>_0023dke</c> (long),
/// 语义取自 <c>SteamLobbyData.cs:853</c>: 构造 <c>LobbyMatchmakingTimedOutData(x._0023a8d, x._0023dke)</c>,
/// 另一端 <c>GUI_PlayPartyWindow.cs:620</c> 用的是 <c>(GameLinkClient.UserID, dateAvailable.Ticks)</c>。
/// </summary>
public struct MatchmakingTimeOutData
{
    public ulong UserId;

    /// <summary>什么时候之后可以再排 (UTC Ticks)。</summary>
    public long AvailableAtTicks;
}

/// <summary>
/// 找到对局 —— 客户端弹"找到对局"+ 准备按钮。
///
/// 原厂 <c>_0023kke(byte[] P_0, QueueType P_1, int P_2)</c>, 用法 <c>Matchmaker.cs:377</c>:
/// <c>CurrentLobby.MatchFound(msg._0023pje, msg._0023jke)</c> -&gt; <c>MatchFound(matchGuid, userCount)</c>。
/// <see cref="QueueType"/> 为 <c>Debug</c> (自定义房间) 时客户端自动准备, 不弹窗。
/// </summary>
public struct MatchupFoundMessage
{
    public byte[] MatchGuid;
    public QueueType QueueType;

    /// <summary>整局人数 —— 弹窗上画几个准备格子。</summary>
    public int MatchSize;
}

/// <summary>
/// 队长报告"我这队已准备 N 人"。**累计值**, 每多一个人点准备就再发一次。
///
/// 原厂 <c>_0023fke(int P_0, byte[] P_1)</c>, 发送点 <c>MatchmakingLinkClient.cs:165</c>
/// <c>ReadyForMatchMessage(int playersReadyInParty, byte[] matchguid)</c> —— 参数名没混淆。
/// </summary>
public struct ReadyForMatchMessage
{
    public int PlayersReadyInParty;
    public byte[] MatchGuid;
}

/// <summary>
/// 整局已准备人数 —— 客户端刷新弹窗上的格子并转发给队员。
///
/// 原厂 <c>_0023hke(int P_0, byte[] P_1)</c>, 用法 <c>Matchmaker.cs:454</c>
/// <c>GUI_GameFoundPopup.Instance.SetReadyAmount(msg._0023gke)</c>。
/// </summary>
public struct PlayersReadyCountMessage
{
    public int PlayersReady;
    public byte[] MatchGuid;
}

/// <summary>
/// 准备阶段没凑齐。
///
/// 原厂 <c>_0023pke(bool P_0, _0023oke P_1, byte[] P_2)</c>, 用法 <c>Matchmaker.cs:411</c>:
/// 第一个字段 true 时客户端打一行 "Rejoining Queue" 就返回 (留在匹配中),
/// false 时停止匹配。
/// </summary>
public struct ReadyTimeoutMessage
{
    /// <summary>true = 这一队点了准备, 服务端已把它放回队列。</summary>
    public bool RejoiningQueue;

    public ReadyTimeoutReason Reason;
    public byte[] MatchGuid;
}

/// <summary>
/// 原厂 <c>_0023oke</c> 的三个值, 名字取自 <c>Matchmaker.cs:413-432</c> 的映射:
/// <c>_0023lke</c> -&gt; RequestServerFailed, <c>_0023mke</c> -&gt; StateError,
/// <c>_0023nke</c> -&gt; ReadyTimeout。
/// </summary>
public enum ReadyTimeoutReason
{
    /// <summary>战斗服起不来。客户端停止匹配并弹"请求游戏服务器失败"。</summary>
    RequestServerFailed,
    StateError,

    /// <summary>有人没在时限内点准备。</summary>
    ReadyTimeout,
}

/// <summary>
/// 全员准备好了, 正在开战斗服。客户端显示"等待服务器"。
///
/// 原厂 <c>_0023qke(byte[] P_0)</c>, 用法 <c>Matchmaker.cs:601</c>。
/// </summary>
public struct FindingServerMessage
{
    public byte[] MatchGuid;
}

/// <summary>
/// 战斗服地址。这是匹配链路上**唯一一个服务端发出的请求** —— 客户端要回
/// <see cref="ServerFoundAffirmativeMessage"/>。
///
/// 取自 <c>MatchmakingProtocol\ServerFoundMessage.cs</c>, 处理点 <c>Matchmaker.cs:394</c>:
/// 队长回执, 然后 <c>CurrentLobby.FoundServer(ip, port, region)</c> 经 Steam 大厅发给队员;
/// <c>QueueType.Debug</c> (自定义房间) 时直接自己拉起游戏。
/// </summary>
public struct ServerFoundMessage
{
    public byte[] MatchGuid;

    /// <summary>4 字节 IPv4。队员拿到的也是这个地址 (队长转发), 见 MatchHostAddress。</summary>
    public byte[] IPAddress;

    public ushort Port;
    public QueueType QueueType;
    public MatchmakingRegion MatchmakingRegion;
}

/// <summary><see cref="ServerFoundMessage"/> 的回执。空结构体, 线上零字节。</summary>
public struct ServerFoundAffirmativeMessage
{
}

/// <summary>客户端取消排队。空结构体。原厂 <c>_0023rke</c>。</summary>
/// <remarks>
/// 客户端是**先 ForceClose 再发**它 (<c>Matchmaker.cs:1066</c> + <c>MatchmakingLinkClient.cs:159</c>),
/// 所以多半到不了 —— 服务端要把"队长的匹配连接断了"当作离队处理。
/// </remarks>
public struct LeaveQueueMessage
{
}

/// <summary>服务器维护中。空结构体。原厂 <c>_0023Y5d</c>。私服不发。</summary>
public struct MatchMaintenanceModeMessage
{
}

/// <summary>
/// 禁用功能表更新推送。原厂 <c>_0023O5d(DisabledFeaturesData P_0)</c>。
/// 私服的表是固定的, 登录时已下发, 不发这条。
/// </summary>
public struct DisabledFeaturesUpdatedMessage
{
    public DisabledFeaturesData Data;
}
