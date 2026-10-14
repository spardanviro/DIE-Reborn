using ConductorCrafting;
using StunMessage;

namespace MatchmakingProtocol;

/// <summary>
/// 匹配协议的序列化器。
///
/// ## 这些代码是誊抄, 不是推导
///
/// 每个方法都逐行对应 CRIB 树 <c>-8je\-97d.cs</c> 里的原厂实现。混淆符号的映射:
///
///   <c>_0023Zzc</c>  -> <see cref="Message"/>
///   <c>_0023Cse</c>  -> <see cref="Message.WriteVarInt"/>   (uint / int 两个重载)
///   <c>_0023Ese</c>  -> <see cref="Message.ReadVarInt"/>
///   <c>Write/Read</c> -> 定长原语, 与原厂同名
///
/// 这一点很重要: Phase 1 的教训是"照着规格手写解码器"会出错 (曾把定长 ushort 的
/// SchematicID 当成 varint)。这里不存在那个风险 —— 原件就在眼前, 逐句对照即可。
///
/// ## 为什么不直接编译原厂那份
///
/// 试过, 走不通。CRIB 树是 ILMerge + 混淆过的 Assembly-CSharp: 公开类型名保留、
/// 内部类型改名, 于是它自带一个**与干净树全限定名相同、底层 Message 类型却不同**的
/// StunTCP.TCPMessageClient, 两棵树无法编进同一程序集。做成独立程序集又会把
/// ConductorCrafting/ConductorShop 乃至 CharacterEnum、StoryMapData 这些根命名空间的
/// Unity 游戏逻辑整片拖进来。为省下这 100 行誊抄付出那个代价不划算。
///
/// ## 校验手段
///
/// 请求方向 (客户端 -> 我们): 用 tools\capture_listener.py 抓真实字节, 断言解析后
/// **零剩余字节** —— 这是 Phase 1 用过、行之有效的判据。
/// 响应方向 (我们 -> 客户端): 客户端解析失败会回 <c>RequestResult.DeserializeFail</c>
/// 并弹框, 所以是能观测的。
///
/// ## 分文件
///
/// 本文件: 认证块、Solo 开局、进行中对局查询, 以及公用原语。
/// <c>MatchSerialization.Queue.cs</c>: 排队时序 (T19)。
/// <c>MatchSerialization.Lobby.cs</c>: 自定义房间 (T20)、观战列表、离开对局、加难度。
/// </summary>
public static partial class MatchSerialization
{
    // ------------------------------------------------------------------
    // AuthData
    // ------------------------------------------------------------------

    public static void Serialize(ref AuthData P_0, ref Message P_1)
    {
        WriteBytes(P_0.AuthTicket, ref P_1);
        P_1.WriteVarInt(P_0.VersionMajor);
        P_1.WriteVarInt(P_0.VersionMinor);
        P_1.WriteVarInt(P_0.VersionBuild);
        P_1.WriteVarInt(P_0.VersionRevision);
        P_1.Write(P_0.Branch);
        P_1.WriteVarInt(P_0.DisabledFeaturesRevision);
    }

    public static bool Deserialize(ref AuthData P_0, ref Message P_1)
    {
        if (!ReadBytes(ref P_0.AuthTicket, ref P_1)) return false;
        if (!P_1.ReadVarInt(ref P_0.VersionMajor)) return false;
        if (!P_1.ReadVarInt(ref P_0.VersionMinor)) return false;
        if (!P_1.ReadVarInt(ref P_0.VersionBuild)) return false;
        if (!P_1.ReadVarInt(ref P_0.VersionRevision)) return false;
        if (!P_1.Read(ref P_0.Branch)) return false;
        return P_1.ReadVarInt(ref P_0.DisabledFeaturesRevision);
    }

    // ------------------------------------------------------------------
    // GetActiveMatchRequest / Response
    // ------------------------------------------------------------------

    public static bool Deserialize(ref GetActiveMatchRequest P_0, ref Message P_1)
    {
        return Deserialize(ref P_0.AuthData, ref P_1);
    }

    /// <summary>客户端方向 (-97d.cs:431)。服务端不发它, 留着给测试造字节。</summary>
    public static void Serialize(ref GetActiveMatchRequest P_0, ref Message P_1)
    {
        Serialize(ref P_0.AuthData, ref P_1);
    }

    public static void Serialize(ref GetActiveMatchResponse P_0, ref Message P_1)
    {
        P_1.WriteVarInt((int)P_0.AuthResult);
        P_1.Write(P_0.HasMatch);
        P_1.Write(P_0.IsAFKKicked);
        WriteBytes(P_0.IPAddress, ref P_1);
        P_1.Write(P_0.Port);          // 裸 ushort, 定长 2 字节小端 —— 不是 varint
        WriteBytes(P_0.MatchGUID, ref P_1);
    }

    /// <summary>客户端方向 (-97d.cs:474)。同上, 给测试用。</summary>
    public static bool Deserialize(ref GetActiveMatchResponse P_0, ref Message P_1)
    {
        int auth = 0;
        if (!P_1.ReadVarInt(ref auth)) return false;
        P_0.AuthResult = (AuthResult)auth;
        if (!P_1.Read(ref P_0.HasMatch)) return false;
        if (!P_1.Read(ref P_0.IsAFKKicked)) return false;
        if (!ReadBytes(ref P_0.IPAddress, ref P_1)) return false;
        if (!P_1.Read(ref P_0.Port)) return false;
        return ReadBytes(ref P_0.MatchGUID, ref P_1);
    }

    // ------------------------------------------------------------------
    // SoloServerCreateRequest / SoloServerCreated
    // ------------------------------------------------------------------

    public static bool Deserialize(ref SoloServerCreateRequest P_0, ref Message P_1)
    {
        if (!Deserialize(ref P_0.AuthData, ref P_1)) return false;
        if (!ReadBytes(ref P_0.MatchmakingTicket, ref P_1)) return false;

        int type = 0;
        if (!P_1.ReadVarInt(ref type)) return false;
        P_0.Type = (QueueType)type;

        return P_1.ReadVarInt(ref P_0.MapIndex);
    }

    /// <summary>客户端方向 (-97d.cs:532)。同上, 给测试用。</summary>
    public static void Serialize(ref SoloServerCreateRequest P_0, ref Message P_1)
    {
        Serialize(ref P_0.AuthData, ref P_1);
        WriteBytes(P_0.MatchmakingTicket, ref P_1);
        P_1.WriteVarInt((int)P_0.Type);
        P_1.WriteVarInt(P_0.MapIndex);
    }

    /// <summary>客户端方向 (-97d.cs:1206)。同上, 给测试用。</summary>
    public static bool Deserialize(ref SoloServerCreated P_0, ref Message P_1)
    {
        int auth = 0;
        if (!P_1.ReadVarInt(ref auth)) return false;
        P_0.AuthResult = (AuthResult)auth;

        int result = 0;
        if (!P_1.ReadVarInt(ref result)) return false;
        P_0.JoinQueueResult = (JoinQueueResult)result;

        if (!ReadBytes(ref P_0.IPAddress, ref P_1)) return false;
        return P_1.Read(ref P_0.Port);
    }

    public static void Serialize(ref SoloServerCreated P_0, ref Message P_1)
    {
        P_1.WriteVarInt((int)P_0.AuthResult);
        P_1.WriteVarInt((int)P_0.JoinQueueResult);
        WriteBytes(P_0.IPAddress, ref P_1);
        P_1.Write(P_0.Port);          // 同上, 定长
    }

    // ------------------------------------------------------------------
    // 原语
    // ------------------------------------------------------------------

    /// <summary>
    /// 变长字节数组: varint(长度) + 原始字节。
    /// null 与空数组在线上不可区分 —— 原厂就是这么写的, 两者都写成长度 0。
    /// </summary>
    private static void WriteBytes(byte[] data, ref Message P_1)
    {
        if (data == null)
        {
            P_1.WriteVarInt(0);
            return;
        }

        P_1.WriteVarInt((uint)data.Length);
        foreach (byte b in data)
        {
            P_1.Write(b);
        }
    }

    private static bool ReadBytes(ref byte[] data, ref Message P_1)
    {
        uint length = 0u;
        if (!P_1.ReadVarInt(ref length))
        {
            return false;
        }

        byte[] buffer = new byte[length];
        for (uint i = 0u; i < length; i++)
        {
            byte b = 0;
            if (!P_1.Read(ref b))
            {
                return false;
            }

            buffer[i] = b;
        }

        data = buffer;
        return true;
    }
}
