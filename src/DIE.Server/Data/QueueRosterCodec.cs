using System;
using System.Collections.Generic;

namespace DIE.Server.Data;

/// <summary>名单上的一个位子: 谁 (可能是 0 = 队长没拿到他的 id) + 他排队时的选择 + 拾荒里分到的队 (0 = 没分)。</summary>
public readonly record struct MatchSeat(ulong UserId, PlayerChoice? Choice, byte Team = 0);

/// <summary>
/// 匹配票据 <c>queue_data</c> 列里的**全队名单**。
///
/// ## 为什么要扩
///
/// 以前一张票只记开票人自己的选择 (<see cref="PlayerChoiceCodec"/>) —— Solo 够用。
/// 组队排队时, 队长一次 <c>GetMatchmakingTicketRequest</c> 就把全队每个人的
/// <c>PlayerQueueData</c> 都带上了 (<c>SteamLobbyData.cs:423</c>), UserID 各是各的。
/// 战斗服要按人对号入座、按人发武器, 所以票里得存整队。
///
/// ## 格式 (我们自己的, 客户端看不到)
///
///   [0]      0x81 或 0x82 —— 和 PlayerChoiceCodec 的版本号 (1, 2) 错开, 一眼能分出新旧
///   [1]      人数 N
///   [2]      **仅 0x82**: 十字路口难度档 (0..4), 见 <see cref="Encode(IReadOnlyList{ValueTuple{ulong, byte[]}}, int)"/>
///   N × {    8 字节 UserId (LE)
///            2 字节 选择块长度 L (LE)
///            L 字节 PlayerChoiceCodec 编出来的块 (L = 0 表示没有) }
///
/// ## 三种格式都认
///
/// 在途票据可能是更老的格式 (单人的 PlayerChoiceCodec 块, 没有名单), 或者 0x81
/// (有名单没难度)。<see cref="Decode"/> 三种都认: 认不出名单就解成"开票人一个人",
/// 没有难度字节就当 0 (简单档)。
/// </summary>
public static class QueueRosterCodec
{
    /// <summary>只有名单。</summary>
    private const byte Marker = 0x81;

    /// <summary>名单 + 一个字节的十字路口难度档。</summary>
    private const byte MarkerWithDifficulty = 0x82;

    /// <summary>一局最多几个人 —— 大厅一队 4 人, 自定义房间最多 16 (战斗服的客户端表)。</summary>
    public const int MaxSeats = 16;

    public static byte[] Encode(IReadOnlyList<(ulong UserId, byte[] ChoiceBlob)> seats) =>
        Encode(seats, crossroadDifficulty: -1);

    /// <summary>
    /// 带上十字路口的难度档 (<c>LobbyDifficulty</c>: 0 简单 … 4 极难)。
    ///
    /// 为什么难度要走票据: 排队请求 (<c>JoinQueueRequest</c>) 里**没有**难度字段,
    /// 客户端把它放在每个人的 <c>PlayerQueueData.QueuedCrossroadDifficulty</c> 里,
    /// 也就是签票那一趟 (1555) 才看得到。而挑图要用它 (小/中/大三批图按难度分),
    /// 于是签票时存进票据, 排队时再读出来。
    ///
    /// 传负数 = 不带 (编成 0x81 老格式), 给 Solo 和测试用。
    /// </summary>
    public static byte[] Encode(IReadOnlyList<(ulong UserId, byte[] ChoiceBlob)> seats,
                                int crossroadDifficulty)
    {
        if (seats == null || seats.Count == 0 || seats.Count > MaxSeats)
        {
            throw new ArgumentException($"名单人数必须在 1..{MaxSeats}", nameof(seats));
        }

        bool withDifficulty = crossroadDifficulty >= 0;
        int header = withDifficulty ? 3 : 2;

        int size = header;
        foreach ((ulong _, byte[] blob) in seats)
        {
            size += 8 + 2 + (blob?.Length ?? 0);
        }

        byte[] buffer = new byte[size];
        buffer[0] = withDifficulty ? MarkerWithDifficulty : Marker;
        buffer[1] = (byte)seats.Count;
        if (withDifficulty)
        {
            buffer[2] = (byte)(crossroadDifficulty > byte.MaxValue ? byte.MaxValue : crossroadDifficulty);
        }

        int offset = header;
        foreach ((ulong userId, byte[] blob) in seats)
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(offset), userId);
            offset += 8;

            ushort length = (ushort)(blob?.Length ?? 0);
            BitConverter.TryWriteBytes(buffer.AsSpan(offset), length);
            offset += 2;

            blob?.CopyTo(buffer, offset);
            offset += length;
        }

        return buffer;
    }

    /// <summary>
    /// 解出名单。认不出来的数据不抛, 退回"开票人一个人、没有选择" ——
    /// 战斗服照旧能开, 只是走默认角色, 比拒绝开局好查。
    /// </summary>
    /// <param name="ticketOwner">开票的那个账号 (老格式没有名单时就是他)。</param>
    public static IReadOnlyList<MatchSeat> Decode(byte[] blob, ulong ticketOwner) =>
        Decode(blob, ticketOwner, out _);

    /// <summary>
    /// 解出名单, 顺带把十字路口的难度档带出来 (0x81 和老格式都给 0 = 简单档)。
    /// </summary>
    public static IReadOnlyList<MatchSeat> Decode(byte[] blob, ulong ticketOwner,
                                                  out int crossroadDifficulty)
    {
        crossroadDifficulty = 0;

        if (blob == null || blob.Length == 0)
        {
            return new[] { new MatchSeat(ticketOwner, null) };
        }

        bool withDifficulty = blob[0] == MarkerWithDifficulty;
        if (blob[0] != Marker && !withDifficulty)
        {
            return new[] { new MatchSeat(ticketOwner, PlayerChoiceCodec.Decode(blob)) };
        }

        List<MatchSeat> seats = new List<MatchSeat>();
        int count = blob.Length > 1 ? blob[1] : 0;
        int offset = 2;

        if (withDifficulty)
        {
            crossroadDifficulty = blob.Length > 2 ? blob[2] : 0;
            offset = 3;
        }

        for (int i = 0; i < count; i++)
        {
            if (offset + 10 > blob.Length)
            {
                break;
            }

            ulong userId = BitConverter.ToUInt64(blob, offset);
            int length = BitConverter.ToUInt16(blob, offset + 8);
            offset += 10;

            if (offset + length > blob.Length)
            {
                break;
            }

            byte[] choice = blob.AsSpan(offset, length).ToArray();
            offset += length;

            seats.Add(new MatchSeat(userId, length == 0 ? null : PlayerChoiceCodec.Decode(choice)));
        }

        return seats.Count > 0 ? seats : new[] { new MatchSeat(ticketOwner, null) };
    }
}
