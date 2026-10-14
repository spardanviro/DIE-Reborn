using System.Collections.Generic;
using ConductorCrafting;
using ConductorShop;

namespace MatchmakingProtocol;

// 自定义房间 (T20)、观战列表、离开对局、十字路口加难度。
//
// 结构体逐字取自 CRIB 树 MatchmakingProtocol\*.cs 和 -8je\-yke.cs。
// 混淆字段的名字取自客户端的用法, 每处都注了出处。

/// <summary>
/// 建自定义房间。取自 <c>MatchmakingProtocol\CreateCustomGameRequest.cs</c>。
///
/// 正式版客户端里只有两处会发它: 内部调试开局窗口 (<c>DebugPlayWindow.cs:985</c>,
/// 由 <c>Features.CribDebug</c> 控制显示) 和展会模式窗口 (<c>GUI_PlayExpoInfoWindow</c>)。
/// 票据走的是 1555 上的 <c>GetDebugMatchmakingTicketRequest</c> (59)。
/// </summary>
public struct CreateCustomGameRequest
{
    public AuthData AuthData;
    public byte Map;
    public byte TeamSize;
    public byte TeamCount;
    public bool IsDebugServer;
    public HordeModeType HordeType;
    public string ServerName;
    public float BarricadeHitpointsModifier;
    public float SuppliesModifier;

    /// <summary>感染等级。0 = 让服务端按装备算。</summary>
    public int ServerLevel;

    public int NumberOfBots;
    public byte[] MatchmakingTicket;

    /// <summary>0 = 自动分队, 1..3 = Team1..Team3 (<c>DebugPlayWindow.cs:281</c> 的帮助文本)。</summary>
    public byte Team;

    public bool AfkEnabled;
}

/// <summary>进自定义房间。取自 <c>MatchmakingProtocol\JoinCustomGameRequest.cs</c>。</summary>
public struct JoinCustomGameRequest
{
    public AuthData AuthData;
    public long CustomGameID;
    public byte[] MatchmakingTicket;
    public byte Team;
}

public struct GetCustomGamesListRequest
{
    public AuthData AuthData;
}

public struct GetCustomGamesListResponse
{
    public AuthResult AuthResult;
    public CustomGameListData[] CustomGames;
}

/// <summary>
/// 房间列表里的一行。原厂字段 <c>_0023wke / _0023TYb / _0023VYb / _0023UYb / _0023k</c>,
/// 语义取自 <c>DebugPlayWindow.cs:276</c>:
/// <c>wke + ": " + TYb + " - " + UYb + "/" + VYb + " Map: " + GetMapName(k)</c>,
/// 以及 <c>ServerList.AddServer(TYb, UYb, VYb, k, ..., wke)</c>。
///
/// **线上顺序是 Id, Name, MaxPlayers, PlayerCount, Map** (最大人数在当前人数前面)。
/// </summary>
public struct CustomGameListData
{
    public long CustomGameId;
    public string Name;
    public byte MaxPlayers;
    public byte PlayerCount;
    public byte Map;
}

/// <summary>
/// 房间人数变了。原厂 <c>_0023yke(long, byte, byte)</c>, 用法 <c>DebugPlayWindow.cs:527</c>:
/// <c>SetMaxAmount(msg._0023VYb); SetAmount(msg._0023xke);</c> —— 等待弹窗上的 "N / M"。
/// </summary>
public struct CustomGameInfoMessage
{
    public long CustomGameId;
    public byte PlayerCount;
    public byte MaxPlayers;
}

public struct GetObservableMatchesRequest
{
    public AuthData AuthData;
}

/// <summary>可观战的对局列表。</summary>
public struct GetObservableMatchesResponse
{
    public AuthResult AuthResult;
    public ObservableMatchData[] Matches;
}

/// <summary>
/// 一局可观战的对局。取自 <c>MatchmakingProtocol\ObservableMatchData.cs</c>。
///
/// ## Teams 我们恒发空
///
/// 原厂这里带的是"每队都有谁" (<c>IEnumerable&lt;IEnumerable&lt;PlayerQueueData&gt;&gt;</c>)。
/// 客户端的观战列表**根本不读它** —— <c>GUI_ServerListEntry.SetData(ObservableMatchData)</c>
/// 只用 <see cref="Name"/> / <see cref="IPAddress"/> / <see cref="Port"/> (人数那一栏写死 "Full")。
/// 要填它就得把整份 <c>PlayerQueueData</c> 的写方向也誊一遍 (90 行), 收益是零, 所以不填。
/// </summary>
public struct ObservableMatchData
{
    public byte[] MatchGUID;
    public byte[] IPAddress;
    public ushort Port;
    public QueueType Type;

    /// <summary>列表里显示的名字 —— 观战列表唯一真正读的字段。</summary>
    public string Name;
}

/// <summary>
/// 放弃一局进行中的对局 (重连弹窗上点"离开")。
/// 取自 <c>MatchmakingProtocol\LeaveMatchRequest.cs</c>, 发送点 <c>GUI_RejoinGamePopup.cs:366</c>。
/// </summary>
public struct LeaveMatchRequest
{
    public AuthData AuthData;
    public byte[] SessionGUID;
}

public struct LeaveMatchResponse
{
    public AuthResult AuthResult;
    public LeaveMatchResult Result;

    /// <summary>中途离开被扣掉的消耗品。私服不扣, 恒空。</summary>
    public StackableUpdate[] ConsumablesLost;
}

/// <summary>
/// 十字路口排久了, 队长同意"加一档难度扩大匹配范围"。
/// 发送点 <c>GUI_PlayPartyWindow.cs:107</c>, 回调是空的 (<c>delegate { }</c>) —— 客户端不看结果。
/// </summary>
public struct AddRaidDifficultyRequest
{
    public int NewDifficulty;
}

public struct AddRaidDifficultyResponse
{
    public AddRaidDifficultyResult Result;
}

/// <summary>
/// 原厂的六个值全是混淆名 (<c>_0023Yc, _0023Wc, _0023ske ..</c>), 客户端也从不读它,
/// 语义无从定案。按序号命名, 不猜。
/// </summary>
public enum AddRaidDifficultyResult
{
    Value0,
    Value1,
    Value2,
    Value3,
    Value4,
    Value5,
}
