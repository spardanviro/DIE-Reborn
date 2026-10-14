using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using ConductorShop;
using StunMessage;

namespace GameProtocol;

/// <summary>
/// 原厂 <see cref="MessageSerialization"/> 是**服务端那一半**: 每条消息只提供服务端需要的方向
/// (收请求给 Deserialize, 发响应给 Serialize)。但有少数消息是服务端**主动推送**的单向消息
/// (packetType 1), 客户端只需要 Deserialize, 所以原厂那份里没有对应的 Serialize。
///
/// 这个类补齐服务端要发的那些。字段顺序逐行镜像自原厂 Deserialize —— 那才是权威,
/// 因为它就是客户端实际运行的代码。
///
/// 验证方式见 ServerSerializationTests: 用原厂 Deserialize 做对手方做往返, 并断言
/// 读指针正好停在末尾。原厂反序列化器就是客户端本体, 它接受了即等于真实客户端会接受。
///
/// 为什么单独开一个类而不是给 MessageSerialization 加 partial:
/// MessageSerialization.cs 是 4310 行的生成文件, 从外部反编译树 glob 引入、永不手改。
/// 加 partial 就得整份复制到 _Fixups, 代价大且每次重新反编译都要重做。
/// 独立类零成本, 而且调用点能一眼看出这是我们补的实现。
/// </summary>
public static class ServerSerialization
{
    /// <summary>
    /// LoginDataMessage (GameMessageType = 44, packetType 1 单向推送)。
    ///
    /// 这是整个 Phase 1 最关键的一条消息: 客户端收到 LoginResponse{Success} 之后**不会**
    /// 继续, 必须再收到这条推送, GameLinkClient.OnLoginDataMessageRecieved 才会设置
    /// _HasLoggedIn / _AuthTicket / _UserID / SessionID 并触发 CribStart.StartLoadLevel()。
    ///
    /// 失败模式很阴险: 只要有一个字段写错, 客户端的 Deserialize 返回 false, 而
    /// HandleMessage 会**静默丢弃**该消息 —— 两侧都不打日志, 客户端永远停在加载画面。
    ///
    /// 镜像自 MessageSerialization.cs:1774 Deserialize(ref LoginDataMessage)。
    /// </summary>
    public static void Serialize(ref LoginDataMessage P_0, ref Message P_1)
    {
        P_1.WriteVarInt((int)P_0.Result);
        WriteBytes(P_0.AuthTicket, ref P_1);
        P_1.WriteVarInt(P_0.UserID);                                   // ulong
        MessageSerialization.Serialize(ref P_0.Currency, ref P_1);
        P_1.WriteVarInt(P_0.GoldSpent);
        P_1.WriteVarInt(P_0.SilverSpent);
        P_1.WriteVarInt(P_0.CPSpent);
        P_1.Write(P_0.UpsellStep);                                     // 裸 byte, 不是 varint
        P_1.WriteVarInt(P_0.UpsellTime);                               // long
        MessageSerialization.Serialize(ref P_0.AccountData, ref P_1);
        P_1.Write(P_0.FirstLogin);

        WriteCollection(P_0.Stackables, ref P_1, static (ref Stackable item, ref Message m) =>
            MessageSerialization.Serialize(ref item, ref m));

        WriteCollection(P_0.Uniques, ref P_1, static (ref Unique item, ref Message m) =>
            MessageSerialization.Serialize(ref item, ref m));

        WriteCollection(P_0.Gadgets, ref P_1, static (ref Gadget item, ref Message m) =>
            MessageSerialization.Serialize(ref item, ref m));

        // DLCPackage 是枚举, 原厂 Deserialize 按 varint 读, 所以这里按 varint 写
        WriteCollection(P_0.UnlockedDLCs, ref P_1, static (ref DLCPackage item, ref Message m) =>
            m.WriteVarInt((uint)item));

        MessageSerialization.Serialize(ref P_0.DisabledFeatures, ref P_1);

        WriteCollection(P_0.GameplayChanges, ref P_1, static (ref GameplayChange item, ref Message m) =>
            MessageSerialization.Serialize(ref item, ref m));

        P_1.WriteVarInt(P_0.GameplayRevision);

        WriteCollection(P_0.LoginRewardData, ref P_1, static (ref LoginRewardData item, ref Message m) =>
            Serialize(ref item, ref m));                               // 这个也是我们补的, 见下

        P_1.Write(P_0.IsExpoUser);
        WriteBytes(P_0.SessionID, ref P_1);
        P_1.WriteVarInt((int)P_0.BanReason);
        P_1.Write(P_0.BannedDays);                                     // ushort 定长 2 字节小端, 不是 varint
        P_1.Write(P_0.WelcomeDataURL);
        P_1.WriteVarInt(P_0.ServerTime);                               // long, DateTime.Ticks
    }

    /// <summary>
    /// 镜像自 MessageSerialization.cs:2729 Deserialize(ref LoginRewardData)。
    /// 只在 LoginDataMessage 内部作为元素出现, 所以原厂那份里也没有 Serialize。
    /// </summary>
    public static void Serialize(ref LoginRewardData P_0, ref Message P_1)
    {
        P_1.WriteVarInt((uint)P_0.DLCPackage);
        MessageSerialization.Serialize(ref P_0.CurrencyGained, ref P_1);
    }

    private delegate void ElementWriter<T>(ref T item, ref Message m);

    /// <summary>
    /// 集合的线格式: varint(元素个数) + 逐个元素。null 编码为 varint 0 —— 与原厂
    /// Serialize(ref GetInventoryResponse) 的惯例一致, 客户端读出来就是空集合。
    /// </summary>
    private static void WriteCollection<T>(IEnumerable<T> items, ref Message P_1, ElementWriter<T> writeElement)
    {
        if (items == null)
        {
            P_1.WriteVarInt(0);
            return;
        }

        // IEnumerable 可能是惰性的, 先物化, 保证 Count() 与后续遍历看到的是同一批元素
        T[] materialized = items as T[] ?? items.ToArray();
        P_1.WriteVarInt((uint)materialized.Length);
        for (int i = 0; i < materialized.Length; i++)
        {
            writeElement(ref materialized[i], ref P_1);
        }
    }

    /// <summary>byte[] 的线格式: varint(长度) + 原始字节。null 编码为 varint 0。</summary>
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
}
