using ConductorCrafting;
using StunCore;
using StunMessage;

namespace MatchmakingProtocol;

// 排队 (T19) 这一路的序列化器。逐句对应 CRIB 树 -8je\-97d.cs, 行号标在每个方法上。
//
// 两个方向都写了: 服务端真正要的是"请求读、推送写", 反方向是给测试造字节用的 ——
// 和原厂那份一样两边齐全, 往返测试才有得比。

public static partial class MatchSerialization
{
    // ------------------------------------------------------------------
    // JoinQueueRequest / JoinQueueResponse   (-97d.cs:13 / :89)
    // ------------------------------------------------------------------

    public static void Serialize(ref JoinQueueRequest P_0, ref Message P_1)
    {
        Serialize(ref P_0.AuthData, ref P_1);
        WriteBytes(P_0.MatchmakingTicket, ref P_1);
        if (P_0.SkillDatas == null)
        {
            P_1.WriteVarInt(0);
        }
        else
        {
            P_1.WriteVarInt((uint)P_0.SkillDatas.Length);
            for (int i = 0; i < P_0.SkillDatas.Length; i++)
            {
                Serialize(ref P_0.SkillDatas[i], ref P_1);
            }
        }

        P_1.WriteVarInt((int)P_0.Type);
    }

    public static bool Deserialize(ref JoinQueueRequest P_0, ref Message P_1)
    {
        if (!Deserialize(ref P_0.AuthData, ref P_1)) return false;
        if (!ReadBytes(ref P_0.MatchmakingTicket, ref P_1)) return false;

        uint count = 0u;
        if (!P_1.ReadVarInt(ref count)) return false;
        SkillRatingData[] skills = new SkillRatingData[count];
        for (uint i = 0u; i < count; i++)
        {
            if (!Deserialize(ref skills[i], ref P_1)) return false;
        }

        P_0.SkillDatas = skills;

        int type = 0;
        if (!P_1.ReadVarInt(ref type)) return false;
        P_0.Type = (QueueType)type;
        return true;
    }

    public static void Serialize(ref JoinQueueResponse P_0, ref Message P_1)
    {
        P_1.WriteVarInt((int)P_0.AuthResult);
        P_1.WriteVarInt((int)P_0.JoinQueueResult);
        if (P_0.TimeoutData == null)
        {
            P_1.WriteVarInt(0);
            return;
        }

        P_1.WriteVarInt((uint)P_0.TimeoutData.Length);
        for (int i = 0; i < P_0.TimeoutData.Length; i++)
        {
            Serialize(ref P_0.TimeoutData[i], ref P_1);
        }
    }

    public static bool Deserialize(ref JoinQueueResponse P_0, ref Message P_1)
    {
        int auth = 0;
        if (!P_1.ReadVarInt(ref auth)) return false;
        P_0.AuthResult = (AuthResult)auth;

        int result = 0;
        if (!P_1.ReadVarInt(ref result)) return false;
        P_0.JoinQueueResult = (JoinQueueResult)result;

        uint count = 0u;
        if (!P_1.ReadVarInt(ref count)) return false;
        MatchmakingTimeOutData[] data = new MatchmakingTimeOutData[count];
        for (uint i = 0u; i < count; i++)
        {
            if (!Deserialize(ref data[i], ref P_1)) return false;
        }

        P_0.TimeoutData = data;
        return true;
    }

    // ------------------------------------------------------------------
    // SkillRatingData (:1729) / MatchmakingTimeOutData (:1760)
    // ------------------------------------------------------------------

    public static void Serialize(ref SkillRatingData P_0, ref Message P_1)
    {
        P_1.WriteVarInt(P_0.TotalMedals);
        P_1.WriteVarInt(P_0.GoldMedals);
        P_1.WriteVarInt(P_0.SilverMedals);
        P_1.WriteVarInt(P_0.BronzeMedals);
    }

    public static bool Deserialize(ref SkillRatingData P_0, ref Message P_1)
    {
        if (!P_1.ReadVarInt(ref P_0.TotalMedals)) return false;
        if (!P_1.ReadVarInt(ref P_0.GoldMedals)) return false;
        if (!P_1.ReadVarInt(ref P_0.SilverMedals)) return false;
        return P_1.ReadVarInt(ref P_0.BronzeMedals);
    }

    /// <summary>两个字段都是 varint —— ulong 和 long 各走各的重载 (原厂 <c>_0023Cse</c>)。</summary>
    public static void Serialize(ref MatchmakingTimeOutData P_0, ref Message P_1)
    {
        P_1.WriteVarInt(P_0.UserId);
        P_1.WriteVarInt(P_0.AvailableAtTicks);
    }

    public static bool Deserialize(ref MatchmakingTimeOutData P_0, ref Message P_1)
    {
        if (!P_1.ReadVarInt(ref P_0.UserId)) return false;
        return P_1.ReadVarInt(ref P_0.AvailableAtTicks);
    }

    // ------------------------------------------------------------------
    // MatchupFoundMessage (:183) —— 注意顺序: guid, queueType, matchSize
    // ------------------------------------------------------------------

    public static void Serialize(ref MatchupFoundMessage P_0, ref Message P_1)
    {
        WriteBytes(P_0.MatchGuid, ref P_1);
        P_1.WriteVarInt((int)P_0.QueueType);
        P_1.WriteVarInt(P_0.MatchSize);
    }

    public static bool Deserialize(ref MatchupFoundMessage P_0, ref Message P_1)
    {
        if (!ReadBytes(ref P_0.MatchGuid, ref P_1)) return false;
        int type = 0;
        if (!P_1.ReadVarInt(ref type)) return false;
        P_0.QueueType = (QueueType)type;
        return P_1.ReadVarInt(ref P_0.MatchSize);
    }

    // ------------------------------------------------------------------
    // ReadyForMatchMessage (:139) / PlayersReadyCount (:595) —— 同一形状: count, guid
    // ------------------------------------------------------------------

    public static void Serialize(ref ReadyForMatchMessage P_0, ref Message P_1)
    {
        P_1.WriteVarInt(P_0.PlayersReadyInParty);
        WriteBytes(P_0.MatchGuid, ref P_1);
    }

    public static bool Deserialize(ref ReadyForMatchMessage P_0, ref Message P_1)
    {
        if (!P_1.ReadVarInt(ref P_0.PlayersReadyInParty)) return false;
        return ReadBytes(ref P_0.MatchGuid, ref P_1);
    }

    public static void Serialize(ref PlayersReadyCountMessage P_0, ref Message P_1)
    {
        P_1.WriteVarInt(P_0.PlayersReady);
        WriteBytes(P_0.MatchGuid, ref P_1);
    }

    public static bool Deserialize(ref PlayersReadyCountMessage P_0, ref Message P_1)
    {
        if (!P_1.ReadVarInt(ref P_0.PlayersReady)) return false;
        return ReadBytes(ref P_0.MatchGuid, ref P_1);
    }

    // ------------------------------------------------------------------
    // ReadyTimeoutMessage (:332) —— bool 定长 1 字节在最前
    // ------------------------------------------------------------------

    public static void Serialize(ref ReadyTimeoutMessage P_0, ref Message P_1)
    {
        P_1.Write(P_0.RejoiningQueue);
        P_1.WriteVarInt((int)P_0.Reason);
        WriteBytes(P_0.MatchGuid, ref P_1);
    }

    public static bool Deserialize(ref ReadyTimeoutMessage P_0, ref Message P_1)
    {
        if (!P_1.Read(ref P_0.RejoiningQueue)) return false;
        int reason = 0;
        if (!P_1.ReadVarInt(ref reason)) return false;
        P_0.Reason = (ReadyTimeoutReason)reason;
        return ReadBytes(ref P_0.MatchGuid, ref P_1);
    }

    // ------------------------------------------------------------------
    // FindingServerMessage (:383)
    // ------------------------------------------------------------------

    public static void Serialize(ref FindingServerMessage P_0, ref Message P_1)
    {
        WriteBytes(P_0.MatchGuid, ref P_1);
    }

    public static bool Deserialize(ref FindingServerMessage P_0, ref Message P_1)
    {
        return ReadBytes(ref P_0.MatchGuid, ref P_1);
    }

    // ------------------------------------------------------------------
    // ServerFoundMessage (:235) —— Port 是定长 ushort, 同 SoloServerCreated
    // ------------------------------------------------------------------

    public static void Serialize(ref ServerFoundMessage P_0, ref Message P_1)
    {
        WriteBytes(P_0.MatchGuid, ref P_1);
        WriteBytes(P_0.IPAddress, ref P_1);
        P_1.Write(P_0.Port);
        P_1.WriteVarInt((int)P_0.QueueType);
        P_1.WriteVarInt((int)P_0.MatchmakingRegion);
    }

    public static bool Deserialize(ref ServerFoundMessage P_0, ref Message P_1)
    {
        if (!ReadBytes(ref P_0.MatchGuid, ref P_1)) return false;
        if (!ReadBytes(ref P_0.IPAddress, ref P_1)) return false;
        if (!P_1.Read(ref P_0.Port)) return false;
        int type = 0;
        if (!P_1.ReadVarInt(ref type)) return false;
        P_0.QueueType = (QueueType)type;
        int region = 0;
        if (!P_1.ReadVarInt(ref region)) return false;
        P_0.MatchmakingRegion = (MatchmakingRegion)region;
        return true;
    }

    // ------------------------------------------------------------------
    // 空结构体 (:323 / :422 / :586) —— 线上零字节
    // ------------------------------------------------------------------

    public static void Serialize(ref ServerFoundAffirmativeMessage P_0, ref Message P_1)
    {
    }

    public static bool Deserialize(ref ServerFoundAffirmativeMessage P_0, ref Message P_1) => true;

    public static void Serialize(ref LeaveQueueMessage P_0, ref Message P_1)
    {
    }

    public static bool Deserialize(ref LeaveQueueMessage P_0, ref Message P_1) => true;

    public static void Serialize(ref MatchMaintenanceModeMessage P_0, ref Message P_1)
    {
    }

    public static bool Deserialize(ref MatchMaintenanceModeMessage P_0, ref Message P_1) => true;

    // ------------------------------------------------------------------
    // DisabledFeaturesUpdatedMessage (:1553) —— 载荷就是 DisabledFeaturesData,
    // 直接用 GameProtocol 那份原厂写方法 (登录推送里也是它)。
    // ------------------------------------------------------------------

    public static void Serialize(ref DisabledFeaturesUpdatedMessage P_0, ref Message P_1)
    {
        GameProtocol.MessageSerialization.Serialize(ref P_0.Data, ref P_1);
    }

    public static bool Deserialize(ref DisabledFeaturesUpdatedMessage P_0, ref Message P_1)
    {
        return GameProtocol.MessageSerialization.Deserialize(ref P_0.Data, ref P_1);
    }
}
