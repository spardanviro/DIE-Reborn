using System.Collections.Generic;
using StunGameNetwork;

namespace DIE.Server.Game;

/// <summary>
/// 把世界状态写进出站帧 —— 服务端那半边唯一真正**丢失**的序列化代码。
///
/// ## 丢的是哪一层
///
/// 每个同步对象自己的序列化器都还在 (<c>GameObjectBase.Serialize(INetBuffer, IClient)</c>
/// 及其 80 个 override、<c>GameMode.Serialize</c> 等)。
/// <c>SynchronizableData.Deserialize</c> 也薄得只是委托:
///
///     public void Deserialize(INetBuffer buf) { _time = ...; _obj.Deserialize(buf); }
///
/// 丢的只有**帧级别的那个循环** —— 挑哪些对象、按什么顺序写。那部分只存在于
/// 从未发布的服务端里。
///
/// ## 格式判据: 客户端的读法
///
/// <c>GameClient._0023SCe</c> (:771), 逐行对应:
///
///     int count = ReadRanged(0, NumOfSynchronizables);
///     if (ReadBool()) {
///         int serverCount = ReadUInt16();
///         if (serverCount != _SynchronizableList.Count)
///             Error("Client-Server synchronizable mismatch! Server: X, Client: Y");
///     }
///     for (i &lt; count) {
///         int index = ReadRanged(0, _SynchronizableList.Count);
///         _SynchronizableList[index].Deserialize(buf);
///     }
///
/// **那条 mismatch 日志值得专门利用**: 我们总是写上表长, 于是两边一旦错开,
/// 客户端会立刻打出明确的一行, 而不是默默地把数据写到错误的对象上。
/// 这给"服务端同步表必须等于客户端"这个不变量提供了运行期安全网。
///
/// ## 发哪些: 原厂的优先级公式已经丢了
///
/// <c>SynchronizableData</c> 里的 <c>_0023eDe</c> (Prio) / <c>_0023fDe</c> (Panic)
/// 在整个客户端程序集里**只在 Reset 里被置 0**, 从无其它写入点 —— 计算它们的代码
/// 在服务端那半边, 已经随之消失。<c>CompareTo</c> 还在 (按 Prio 降序), 说明原厂是
/// 按优先级排序后取前 N 条塞满一个包。
///
/// 我们没法复原那个公式, 所以采用一个**简单且明确**的策略: 发送该客户端所有
/// 处于激活状态的同步对象。这不是原厂行为, 是我们的选择, 理由:
///
///   - 正确性优先: 全发一定不会漏, 而漏发的表现是"对象卡住不动", 极难排查
///   - 量级可控: 16664 是**对象池容量**, 不是活跃数; 绝大多数从未激活
///     (AddSynchronizable 时 isActiveFromStart: false)
///   - 私服并发低: 原厂的优先级机制是为了在带宽受限时保证重要对象优先, 而我们
///     一局就几个人
///
/// 等真的遇到带宽问题, 再按 CompareTo 暴露的语义重建一个优先级策略。
/// </summary>
public static class WorldStateWriter
{
    /// <summary>
    /// 写一帧的同步数据。返回实际写出的条目数。
    /// </summary>
    /// <param name="buffer">出站缓冲。</param>
    /// <param name="viewer">收这一帧的客户端 —— 每个对象的 Serialize 都按观察者裁剪内容。</param>
    /// <param name="all">整张同步表, 下标即线上的 index。</param>
    /// <summary>
    /// 上一帧写出去的条目数和其中是否含 GameMode。
    ///
    /// 客户端侧的插桩连续四轮没能落地 (Doorstop 不注入子进程), 所以改从服务端
    /// 取证: 如果这里确实每帧都在写 GameMode, 而客户端又没有任何反序列化错误,
    /// 那问题就只可能在客户端的判定逻辑上, 与传输无关。
    /// </summary>
    public static int LastWritten { get; private set; }
    public static bool LastIncludedGameMode { get; private set; }

    /// <summary>
    /// 挑出本帧该发给这个观察者的条目下标。
    ///
    /// 和写入分开是因为**判据在这一侧**: 一个实体发不出去, 几乎总是因为它的
    /// 每客户端同步位没被置上 (<c>_0023pDe</c>), 而不是因为字节写错了。
    /// 分开之后这一步不需要缓冲区就能验证 —— 见 ClientVisibilityTests。
    /// </summary>
    public static List<int> SelectActive(IClient viewer, IReadOnlyList<SynchronizableData> all)
    {
        List<int> active = new List<int>();
        for (int i = 0; i < all.Count; i++)
        {
            SynchronizableData data = all[i];
            if (data != null && data._0023pDe(viewer.Index))
            {
                active.Add(i);
            }
        }

        return active;
    }

    public static int Write(INetBuffer buffer, IClient viewer, IReadOnlyList<SynchronizableData> all)
    {
        List<int> active = SelectActive(viewer, all);

        LastIncludedGameMode = false;
        foreach (int index in active)
        {
            if (all[index]._0023Zye is ConductorGameLogic.GameModes.GameMode)
            {
                LastIncludedGameMode = true;
                break;
            }
        }

        // 顺序必须是: 条数 -> (可选)表长 -> 各条。客户端就是这么读的。
        WriteRanged(buffer, active.Count, 0, all.Count);

        // 总是写表长。客户端拿它和自己的表比, 不一致会打明确的 mismatch 日志 ——
        // 这是我们主动换来的诊断能力, 代价只有 17 bit。
        buffer.Write(true);
        buffer.Write((ushort)all.Count);

        foreach (int index in active)
        {
            WriteRanged(buffer, index, 0, all.Count);
            all[index]._0023Zye.Serialize(buffer, viewer);
        }

        LastWritten = active.Count;
        return active.Count;
    }

    /// <summary>
    /// 写一个限定范围的整数。
    ///
    /// <c>INetBuffer</c> 上**没有**对应的写方法 —— 只有读侧的 <c>_0023Pae(min, max)</c>,
    /// 因为客户端只读不写。所以这里按它的实现反推 (NetBuffer.cs:297):
    ///
    ///     uint range = (uint)(max - min);
    ///     int bits = BitsNeeded(range);
    ///     uint raw = ReadUInt(bits);
    ///     return (int)(min + raw);
    ///
    /// 对称的写法就是: 用同样的位数写 (value - min)。位数必须用**同一个**函数算,
    /// 自己实现一个 log2 是自找麻烦 —— 边界情况 (range 恰为 2 的幂) 差一位,
    /// 整个位流就错开了, 而且不会报错。
    /// </summary>
    public static void WriteRanged(INetBuffer buffer, int value, int min, int max)
    {
        int bits = _0023iae._0023Lge._0023Gge((uint)(max - min));
        buffer.Write((uint)(value - min), bits);
    }
}
