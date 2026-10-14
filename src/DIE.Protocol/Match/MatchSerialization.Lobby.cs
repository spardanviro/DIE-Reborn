using ConductorCrafting;
using ConductorShop;
using StunMessage;

namespace MatchmakingProtocol;

// 自定义房间 (T20)、观战列表、离开对局、十字路口加难度的序列化器。
// 逐句对应 CRIB 树 -8je\-97d.cs, 行号标在每个方法上。

public static partial class MatchSerialization
{
    // ------------------------------------------------------------------
    // CreateCustomGameRequest (:871)
    //
    // 定长和变长混着来, 是最容易抄错的一条: Map/TeamSize/TeamCount 是裸 byte,
    // IsDebugServer 裸 bool, HordeType varint, 两个 modifier 是定长 float,
    // ServerLevel/NumberOfBots varint, Team 裸 byte, AfkEnabled 裸 bool。
    // ------------------------------------------------------------------

    public static void Serialize(ref CreateCustomGameRequest P_0, ref Message P_1)
    {
        Serialize(ref P_0.AuthData, ref P_1);
        P_1.Write(P_0.Map);
        P_1.Write(P_0.TeamSize);
        P_1.Write(P_0.TeamCount);
        P_1.Write(P_0.IsDebugServer);
        P_1.WriteVarInt((int)P_0.HordeType);
        P_1.Write(P_0.ServerName);
        P_1.Write(P_0.BarricadeHitpointsModifier);
        P_1.Write(P_0.SuppliesModifier);
        P_1.WriteVarInt(P_0.ServerLevel);
        P_1.WriteVarInt(P_0.NumberOfBots);
        WriteBytes(P_0.MatchmakingTicket, ref P_1);
        P_1.Write(P_0.Team);
        P_1.Write(P_0.AfkEnabled);
    }

    public static bool Deserialize(ref CreateCustomGameRequest P_0, ref Message P_1)
    {
        if (!Deserialize(ref P_0.AuthData, ref P_1)) return false;
        if (!P_1.Read(ref P_0.Map)) return false;
        if (!P_1.Read(ref P_0.TeamSize)) return false;
        if (!P_1.Read(ref P_0.TeamCount)) return false;
        if (!P_1.Read(ref P_0.IsDebugServer)) return false;
        int horde = 0;
        if (!P_1.ReadVarInt(ref horde)) return false;
        P_0.HordeType = (HordeModeType)horde;
        if (!P_1.Read(ref P_0.ServerName)) return false;
        if (!P_1.Read(ref P_0.BarricadeHitpointsModifier)) return false;
        if (!P_1.Read(ref P_0.SuppliesModifier)) return false;
        if (!P_1.ReadVarInt(ref P_0.ServerLevel)) return false;
        if (!P_1.ReadVarInt(ref P_0.NumberOfBots)) return false;
        if (!ReadBytes(ref P_0.MatchmakingTicket, ref P_1)) return false;
        if (!P_1.Read(ref P_0.Team)) return false;
        return P_1.Read(ref P_0.AfkEnabled);
    }

    // ------------------------------------------------------------------
    // JoinCustomGameRequest (:975) —— CustomGameID 是 varint long
    // ------------------------------------------------------------------

    public static void Serialize(ref JoinCustomGameRequest P_0, ref Message P_1)
    {
        Serialize(ref P_0.AuthData, ref P_1);
        P_1.WriteVarInt(P_0.CustomGameID);
        WriteBytes(P_0.MatchmakingTicket, ref P_1);
        P_1.Write(P_0.Team);
    }

    public static bool Deserialize(ref JoinCustomGameRequest P_0, ref Message P_1)
    {
        if (!Deserialize(ref P_0.AuthData, ref P_1)) return false;
        if (!P_1.ReadVarInt(ref P_0.CustomGameID)) return false;
        if (!ReadBytes(ref P_0.MatchmakingTicket, ref P_1)) return false;
        return P_1.Read(ref P_0.Team);
    }

    // ------------------------------------------------------------------
    // GetCustomGamesListRequest / Response (:1027 / :1038), CustomGameListData (:1080)
    // ------------------------------------------------------------------

    public static void Serialize(ref GetCustomGamesListRequest P_0, ref Message P_1)
    {
        Serialize(ref P_0.AuthData, ref P_1);
    }

    public static bool Deserialize(ref GetCustomGamesListRequest P_0, ref Message P_1)
    {
        return Deserialize(ref P_0.AuthData, ref P_1);
    }

    public static void Serialize(ref GetCustomGamesListResponse P_0, ref Message P_1)
    {
        P_1.WriteVarInt((int)P_0.AuthResult);
        if (P_0.CustomGames == null)
        {
            P_1.WriteVarInt(0);
            return;
        }

        P_1.WriteVarInt((uint)P_0.CustomGames.Length);
        for (int i = 0; i < P_0.CustomGames.Length; i++)
        {
            Serialize(ref P_0.CustomGames[i], ref P_1);
        }
    }

    public static bool Deserialize(ref GetCustomGamesListResponse P_0, ref Message P_1)
    {
        int auth = 0;
        if (!P_1.ReadVarInt(ref auth)) return false;
        P_0.AuthResult = (AuthResult)auth;

        uint count = 0u;
        if (!P_1.ReadVarInt(ref count)) return false;
        CustomGameListData[] games = new CustomGameListData[count];
        for (uint i = 0u; i < count; i++)
        {
            if (!Deserialize(ref games[i], ref P_1)) return false;
        }

        P_0.CustomGames = games;
        return true;
    }

    /// <summary>顺序: Id(varint long), Name, **Max**, **Count**, Map —— 最大人数在前。</summary>
    public static void Serialize(ref CustomGameListData P_0, ref Message P_1)
    {
        P_1.WriteVarInt(P_0.CustomGameId);
        P_1.Write(P_0.Name);
        P_1.Write(P_0.MaxPlayers);
        P_1.Write(P_0.PlayerCount);
        P_1.Write(P_0.Map);
    }

    public static bool Deserialize(ref CustomGameListData P_0, ref Message P_1)
    {
        if (!P_1.ReadVarInt(ref P_0.CustomGameId)) return false;
        if (!P_1.Read(ref P_0.Name)) return false;
        if (!P_1.Read(ref P_0.MaxPlayers)) return false;
        if (!P_1.Read(ref P_0.PlayerCount)) return false;
        return P_1.Read(ref P_0.Map);
    }

    // ------------------------------------------------------------------
    // CustomGameInfoMessage (:1703) —— 顺序: Id, **Count**, **Max** (和列表那条反过来)
    // ------------------------------------------------------------------

    public static void Serialize(ref CustomGameInfoMessage P_0, ref Message P_1)
    {
        P_1.WriteVarInt(P_0.CustomGameId);
        P_1.Write(P_0.PlayerCount);
        P_1.Write(P_0.MaxPlayers);
    }

    public static bool Deserialize(ref CustomGameInfoMessage P_0, ref Message P_1)
    {
        if (!P_1.ReadVarInt(ref P_0.CustomGameId)) return false;
        if (!P_1.Read(ref P_0.PlayerCount)) return false;
        return P_1.Read(ref P_0.MaxPlayers);
    }

    // ------------------------------------------------------------------
    // GetObservableMatchesRequest / Response (:1245 / :1256)
    // ------------------------------------------------------------------

    public static void Serialize(ref GetObservableMatchesRequest P_0, ref Message P_1)
    {
        Serialize(ref P_0.AuthData, ref P_1);
    }

    public static bool Deserialize(ref GetObservableMatchesRequest P_0, ref Message P_1)
    {
        return Deserialize(ref P_0.AuthData, ref P_1);
    }

    public static void Serialize(ref GetObservableMatchesResponse P_0, ref Message P_1)
    {
        P_1.WriteVarInt((int)P_0.AuthResult);
        if (P_0.Matches == null)
        {
            P_1.WriteVarInt(0);
            return;
        }

        P_1.WriteVarInt((uint)P_0.Matches.Length);
        for (int i = 0; i < P_0.Matches.Length; i++)
        {
            Serialize(ref P_0.Matches[i], ref P_1);
        }
    }

    public static bool Deserialize(ref GetObservableMatchesResponse P_0, ref Message P_1)
    {
        int auth = 0;
        if (!P_1.ReadVarInt(ref auth)) return false;
        P_0.AuthResult = (AuthResult)auth;

        uint count = 0u;
        if (!P_1.ReadVarInt(ref count)) return false;
        ObservableMatchData[] matches = new ObservableMatchData[count];
        for (uint i = 0u; i < count; i++)
        {
            if (!Deserialize(ref matches[i], ref P_1)) return false;
        }

        P_0.Matches = matches;
        return true;
    }

    /// <summary>
    /// ObservableMatchData (:1298)。末尾那个 Teams 我们**恒写 0 队** —— 客户端的观战列表
    /// 不读它 (见结构体注释)。顺序: guid, ip, 定长 Port, 队列, 名字, 队伍数。
    /// </summary>
    public static void Serialize(ref ObservableMatchData P_0, ref Message P_1)
    {
        WriteBytes(P_0.MatchGUID, ref P_1);
        WriteBytes(P_0.IPAddress, ref P_1);
        P_1.Write(P_0.Port);
        P_1.WriteVarInt((int)P_0.Type);
        P_1.Write(P_0.Name);
        P_1.WriteVarInt(0);
    }

    public static bool Deserialize(ref ObservableMatchData P_0, ref Message P_1)
    {
        if (!ReadBytes(ref P_0.MatchGUID, ref P_1)) return false;
        if (!ReadBytes(ref P_0.IPAddress, ref P_1)) return false;
        if (!P_1.Read(ref P_0.Port)) return false;
        int type = 0;
        if (!P_1.ReadVarInt(ref type)) return false;
        P_0.Type = (QueueType)type;
        if (!P_1.Read(ref P_0.Name)) return false;

        uint teams = 0u;
        if (!P_1.ReadVarInt(ref teams)) return false;
        if (teams != 0)
        {
            // 我们自己从不写非空队伍; 真收到了说明对面不是我们 —— 别假装读懂了
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // LeaveMatchRequest / Response (:1430 / :1471), StackableUpdate (:1520)
    // ------------------------------------------------------------------

    public static void Serialize(ref LeaveMatchRequest P_0, ref Message P_1)
    {
        Serialize(ref P_0.AuthData, ref P_1);
        WriteBytes(P_0.SessionGUID, ref P_1);
    }

    public static bool Deserialize(ref LeaveMatchRequest P_0, ref Message P_1)
    {
        if (!Deserialize(ref P_0.AuthData, ref P_1)) return false;
        return ReadBytes(ref P_0.SessionGUID, ref P_1);
    }

    public static void Serialize(ref LeaveMatchResponse P_0, ref Message P_1)
    {
        P_1.WriteVarInt((int)P_0.AuthResult);
        P_1.WriteVarInt((int)P_0.Result);
        if (P_0.ConsumablesLost == null)
        {
            P_1.WriteVarInt(0);
            return;
        }

        P_1.WriteVarInt((uint)P_0.ConsumablesLost.Length);
        for (int i = 0; i < P_0.ConsumablesLost.Length; i++)
        {
            Serialize(ref P_0.ConsumablesLost[i], ref P_1);
        }
    }

    /// <summary>
    /// 服务端的 GameProtocol 只有这个类型的读方法 (客户端从不发它),
    /// 写方法照 CRIB 誊: UserID varint, ID 定长 ushort, Type 裸 byte, Amount varint,
    /// Max = bool + varint。
    /// </summary>
    public static void Serialize(ref StackableUpdate P_0, ref Message P_1)
    {
        P_1.WriteVarInt(P_0.UserID);
        P_1.Write(P_0.ID);
        P_1.Write((byte)P_0.Type);
        P_1.WriteVarInt(P_0.Amount);
        P_1.Write(P_0.Max.HasValue);
        P_1.WriteVarInt(P_0.Max.Value);
    }

    // ------------------------------------------------------------------
    // AddRaidDifficultyRequest / Response (:1781 / :1797)
    // ------------------------------------------------------------------

    public static void Serialize(ref AddRaidDifficultyRequest P_0, ref Message P_1)
    {
        P_1.WriteVarInt(P_0.NewDifficulty);
    }

    public static bool Deserialize(ref AddRaidDifficultyRequest P_0, ref Message P_1)
    {
        return P_1.ReadVarInt(ref P_0.NewDifficulty);
    }

    public static void Serialize(ref AddRaidDifficultyResponse P_0, ref Message P_1)
    {
        P_1.WriteVarInt((int)P_0.Result);
    }
}
