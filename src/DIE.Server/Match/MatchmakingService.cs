using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using DIE.Server.Data;
using MatchmakingProtocol;
using Serilog;

namespace DIE.Server.Match;

/// <summary>
/// 匹配服这一侧的一条客户端连接 —— 组队排队时是**队长**, 自定义房间时是每个房间成员。
/// 服务端要推的几条消息都在这里; 实现 (<c>MatchLinkQueueClient</c>) 负责按这条连接的来源
/// 算战斗服地址。抽成接口是为了排队时序能脱离网络单测。
/// </summary>
public interface IQueueClient
{
    /// <summary>日志里怎么称呼它。</summary>
    string Name { get; }

    void SendMatchupFound(byte[] matchGuid, QueueType queue, int matchSize);

    void SendPlayersReady(int playersReady, byte[] matchGuid);

    void SendFindingServer(byte[] matchGuid);

    void SendServerFound(byte[] matchGuid, ushort port, QueueType queue);

    void SendReadyTimeout(bool rejoiningQueue, ReadyTimeoutReason reason, byte[] matchGuid);

    void SendCustomGameInfo(long customGameId, byte playerCount, byte maxPlayers);
}

/// <summary>
/// 起一局战斗服, 返回端口; 起不来返回 null 并给出原因。生产上接 <see cref="MatchLauncher.Launch"/>。
/// </summary>
public delegate ushort? MatchStarter(IReadOnlyList<MatchSeat> seats, QueueType queue, int mapIndex,
                                    int crossroadDifficulty,
                                     int infectionLevel, out string failure);

/// <summary>
/// 把一批队伍补进一局**开局前还在等人**的战斗服 (拾荒 / 十字路口), 返回端口;
/// 没有能补的返回 null, 匹配服照常开新局。生产上接 <see cref="MatchLauncher.TryJoin"/>。
/// </summary>
public delegate ushort? MatchJoiner(IReadOnlyList<IReadOnlyList<MatchSeat>> parties, QueueType queue,
                                   int crossroadDifficulty);

/// <summary>
/// 排队的结果。带上罚期数据 —— 客户端拿它弹"还要等 X 分钟"
/// (<c>Matchmaker.DisplayUserTimedOutPopup</c>)。
/// </summary>
public sealed record JoinQueueOutcome(JoinQueueResult Result, MatchmakingTimeOutData[] TimeoutData)
{
    public static JoinQueueOutcome Of(JoinQueueResult result) =>
        new JoinQueueOutcome(result, Array.Empty<MatchmakingTimeOutData>());
}

/// <summary>建自定义房间时房主给的设置 (<c>CreateCustomGameRequest</c> 里我们用得上的那几项)。</summary>
public sealed record CustomRoomSettings(string Name, int MapIndex, int TeamSize, int TeamCount,
                                        int NumberOfBots, int ServerLevel);

/// <summary>
/// 排队 (T19) 和自定义房间 (T20) 的全部状态与时序。
///
/// ## 排队时序 (原厂客户端的期望, 见 MatchQueueMessages.cs 顶部)
///
///   JoinQueue -&gt; [凑人] -&gt; MatchupFound -&gt; 各队报准备人数 -&gt; PlayersReadyCount
///   -&gt; 全员准备 -&gt; FindingServer -&gt; 起战斗服 -&gt; ServerFound (每个队长一条)
///
/// 服务端只跟**队长**打交道: 队员之间的"找到对局 / 点准备 / 战斗服地址"全靠队长的客户端经
/// Steam 大厅转发 (<c>SteamLobbyData</c>)。所以一队就是一个 <see cref="IQueueClient"/>。
///
/// ## 凑人
///
/// 同一队列里的队伍按排队先后凑成一局, 人数不超过 <see cref="GameModeAvailability.QueueCapacity"/>,
/// 并且**段位要够近** (<see cref="SkillWindow"/>: 起步 ±5 块奖牌, 每秒放宽, 等够 30 秒就不限)。
/// 凑满立刻开; 凑不满时, 最早那队等够 <see cref="GatherWindow"/> 就带着现有的人开 ——
/// 私服人少, 等满 4 人不现实; 而分头排队的朋友在这几秒里能被凑进同一局。
///
/// ## 补进等人中的局 (拾荒 / 十字路口, 2026-09-23)
///
/// 这两个模式开局前要在出生区等人 (原厂 <c>GameMode.UpdateGameCountDown</c>, 最多到 ForceStartTime),
/// 等人期间后来排队的队伍**补进同一局** (<see cref="MatchJoiner"/>), 不另开 —— 5 秒凑人窗口
/// 挡不住"分头点匹配、差了十几秒"的朋友, 玩家实测过两个人各进了一局、都是橙队。
/// 有能补的局时不必等凑人窗口; 准备确认期间那一局开打了就照常开新局。
///
/// ## 准备
///
/// 客户端的"找到对局"弹窗倒计时 15 秒, 25 秒时自己关掉 (<c>GUI_GameFoundPopup</c>)。
/// 服务端在 <see cref="ReadyTimeout"/> 判超时: 点满了的队伍回队列 (客户端收到 rejoining=true
/// 只是关掉弹窗, 继续显示"匹配中"), 没点满的踢出匹配。
///
/// ## 自定义房间
///
/// 房主建房 (人数上限 = 每队人数 × 队伍数), 别人从列表里进; 人满就开。开局走的是同一套
/// 准备时序, 只是队列类型是 <c>Debug</c> —— 客户端对 Debug 自动准备、ServerFound 后直接开游戏
/// (<c>Matchmaker.cs:379 / :398</c>)。房间成员各有各的连接, 每人算一"队"。
///
/// ## 先回响应, 再凑局
///
/// <see cref="JoinQueue"/> / <see cref="CreateRoom"/> / <see cref="JoinRoom"/> 只登记, **不当场凑局** ——
/// 调用方先把这次请求的响应发出去, 再调 <see cref="Tick"/>。反过来的话 MatchupFound 会先于
/// JoinQueueResponse 到客户端, 而客户端收到响应时调的 <c>DisplayJoinedMatchmaking</c> 会把
/// <c>_GameFound</c> 清回 false (<c>Matchmaker.cs:898</c>), 刚弹出的"找到对局"就和状态对不上了。
/// 同一条连接上的发送是按顺序入队的, 所以"先响应后推送"在线上也是这个顺序。
///
/// ## 线程
///
/// 匹配连接的回调来自 IO 线程, 定时器又是另一个线程 —— 全部状态由一把锁保护。
/// 发消息在锁里做: StunTCP 的发送是异步入队的, 不会阻塞。
/// </summary>
public sealed class MatchmakingService
{
    /// <summary>
    /// 凑人窗口: 最早那队排了这么久还凑不满, 就带着现有的人开。
    ///
    /// **这是我们定的。** 原版人多, 等满为止; 私服人少, 等不满。短到单人排队几乎不用等,
    /// 长到分头点"准备"的朋友能被凑进同一局。
    /// </summary>
    public static readonly TimeSpan GatherWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 准备超时 —— **跟原版客户端的倒计时一致: 15 秒**。
    ///
    /// 这个数不是我们定的: 客户端弹窗里那个常量本身就叫 <c>_TimeoutTime = 15f</c>
    /// (<c>GUI_GameFoundPopup.cs:7</c>), 倒计时显示的也是 <c>15 - 已过秒数</c>。
    /// 所以服务端在同一秒判超时, 玩家看到的"剩 0 秒"和真实结论对得上。
    ///
    /// 弹窗另有一个 **25 秒**的兜底 (<c>Update</c> 里 <c>TotalSeconds >= 25</c> 时自己关掉并打
    /// "Kicked from matchmaking due to GUI timeout") —— 那是客户端等服务端的结论等不到时的自保,
    /// 不是准备时限。我们比它早 10 秒给出结论, 所以正常永远走不到。
    /// </summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>战斗服的客户端表只有 16 格 (<c>LidgrenHost.MaximumConnections</c>)。</summary>
    public const int MaxRoomSize = 16;

    /// <summary>
    /// 按段位匹配: 刚进队列时只和段位相差这么多的队伍凑一局。
    ///
    /// 段位 = 拾荒模式拿过的奖牌总数 (<see cref="SkillRatingData"/> 的第一个字段, 客户端
    /// 排队时报上来)。**这是我们定的** —— 原版的匹配范围算法没公开。
    /// </summary>
    public const int SkillWindowStart = 5;

    /// <summary>每等一秒, 段位范围放宽这么多 (我们定的)。</summary>
    public const int SkillWindowGrowthPerSecond = 2;

    /// <summary>
    /// 等够这么久之后段位不再限制 —— 私服人少, 宁可跨段位也别让人干等。
    /// 大厅那个"扩大匹配范围"的勾选框 (<c>SpanIncreaseEnabled</c>) 表达的是同一个意思。
    /// </summary>
    public static readonly TimeSpan SkillWindowOpensAt = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly MatchStarter _start;
    private readonly Func<QueueType, int, int> _pickMap;
    private readonly Func<int, bool> _isMapRunnable;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<IReadOnlyList<MatchSeat>, IReadOnlyList<MatchmakingTimeOutData>> _punishedOf;
    private readonly MatchJoiner _join;
    private readonly Func<QueueType, int, int, bool> _hasOpenMatch;

    /// <summary>每条连接最多一个 (一队 / 一个房间成员)。</summary>
    private readonly Dictionary<IQueueClient, Party> _parties = new();
    private readonly List<CustomRoom> _rooms = new();
    private long _nextRoomId = 1;

    /// <param name="start">起战斗服。</param>
    /// <param name="pickMap">给排队出来的一局挑图 (队列, 十字路口难度档) -&gt; 地图。</param>
    /// <param name="isMapRunnable">自定义房间选的图战斗服跑不跑得起来。</param>
    /// <param name="utcNow">时钟 (单测注入)。</param>
    /// <param name="punishedOf">
    /// 名单里谁还在离局罚期 (<c>LeavePunishmentStore</c>)。不接就是不罚。
    ///
    /// 注意传进去的名单只有**发起排队的人自己**, 不是全队 —— 私服不做整队连坐
    /// (玩家定的, 2026-09-20)。原版那个 <c>JoinQueueResult.PartyMemberIsTimedOut</c>
    /// 的名字虽然是"队员被罚", 但连坐会把整队卡住, 而且客户端在名单里找不到自己时
    /// 会崩。详见 <see cref="JoinQueue"/> 里的注释。
    /// </param>
    /// <param name="join">
    /// 把队伍补进一局还在等人的战斗服 (<see cref="MatchJoiner"/>)。不接就是永远开新局。
    /// </param>
    /// <param name="hasOpenMatch">
    /// (队列, 难度档, 人数) -&gt; 眼下有没有一局等人中、塞得下的。有的话排队的队伍不必等
    /// <see cref="GatherWindow"/> 凑人, 直接去补。
    /// </param>
    public MatchmakingService(MatchStarter start, Func<QueueType, int, int> pickMap,
                              Func<int, bool> isMapRunnable = null, Func<DateTime> utcNow = null,
                              Func<IReadOnlyList<MatchSeat>, IReadOnlyList<MatchmakingTimeOutData>> punishedOf = null,
                              MatchJoiner join = null, Func<QueueType, int, int, bool> hasOpenMatch = null)
    {
        _join = join;
        _hasOpenMatch = hasOpenMatch ?? ((_, _, _) => false);
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _pickMap = pickMap ?? throw new ArgumentNullException(nameof(pickMap));
        _isMapRunnable = isMapRunnable ?? (_ => true);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _punishedOf = punishedOf;
    }

    // ==================================================================
    // 排队
    // ==================================================================

    /// <summary>
    /// 一队进队列。这条连接之前若还挂着别的 (上一次排队 / 某个房间), 先撤掉。
    /// 只登记不凑局 —— 回完响应再调 <see cref="Tick"/> (类说明)。
    /// </summary>
    /// <param name="skill">
    /// 这一队的段位 (拾荒奖牌总数的平均值), 用于按段位凑局。练习/Horde 这些模式里大家都是 0,
    /// 于是谁都能凑一起 —— 等拾荒开放了这个字段才真正起作用。
    /// </param>
    /// <param name="crossroadDifficulty">
    /// 十字路口的难度档 (0 简单 … 4 极难), 从票据里解出来。只有同一档的队伍会被凑进一局 ——
    /// 玩家在大厅选的就是"打哪一档", 混着开等于给错了难度和错的那批图。别的队列恒 0。
    /// </param>
    /// <param name="requesterUserId">
    /// 发起排队的人 (队长)。离局罚期**只查他自己** —— 见方法体里那段注释。
    /// 传 0 表示认不出是谁, 那就不查罚期。
    /// </param>
    public JoinQueueOutcome JoinQueue(IQueueClient client, QueueType queue, IReadOnlyList<MatchSeat> seats,
                                      int skill = 0, int crossroadDifficulty = 0, ulong requesterUserId = 0)
    {
        int capacity = GameModeAvailability.QueueCapacity(queue);
        if (capacity <= 0)
        {
            Log.Warning("{Client} 排队 {Queue}: 这个模式私服还没开放 (客户端本该显示为锁定)", client.Name, queue);
            return JoinQueueOutcome.Of(JoinQueueResult.SessionRequestFailed);
        }

        if (seats == null || seats.Count == 0 || seats.Count > capacity)
        {
            Log.Warning("{Client} 排队 {Queue}: 一队 {Count} 人, 超出一局上限 {Capacity}",
                client.Name, queue, seats?.Count ?? 0, capacity);
            return JoinQueueOutcome.Of(JoinQueueResult.InvalidNumberOfMembers);
        }

        // ## 只罚自己, 不连坐 (玩家定的, 2026-09-20)
        //
        // ## Endemic 档要满队 (原厂 RequiresFullParty)
        //
        // 五档里只有最高那档要求, 客户端自己也会在界面上亮出
        // ExtraImpossibleWarningShorter 提示一次 —— 服务端这道是防改过的客户端,
        // 也防"客户端提示了但玩家硬点"。
        //
        // 满队 = 这个队列一局的人数上限 (十字路口 4 人)。数来自
        // StaticCrossroadsGUIData, 见 DIE.Server.Game.CrossroadDifficulty。
        if (queue == QueueType.RaidMission
            && DIE.Server.Game.CrossroadDifficulty.RequiresFullParty(crossroadDifficulty)
            && seats.Count < capacity)
        {
            Log.Information("{Client} 排队 {Queue} 被拒: {Gate} 档要满队 ({Have}/{Need} 人)",
                client.Name, queue,
                DIE.Server.Game.CrossroadDifficulty.Name(crossroadDifficulty),
                seats.Count, capacity);

            return JoinQueueOutcome.Of(JoinQueueResult.InvalidNumberOfMembers);
        }

        // 原版是整队连坐 —— 队里有一个在罚期, 全队都进不了队列。私服是朋友局,
        // 一个人退过局就把整队卡住太难受, 所以这里只查**发起排队的人**自己。
        //
        // 这同时绕开了原版客户端的一个必崩点: 它收到罚期名单后无条件
        // First(找自己) (SteamLobbyData.cs:270 / Matchmaker.cs:476), 名单里
        // 只有被罚的队友、没有自己时就抛 InvalidOperationException ——
        // 而那个处理器由 Steam 回调队列每帧驱动, 抛一次就再也停不下来, 界面全锁死。
        // (客户端侧我们也补了, 见 plugins/DIE.Diagnostics/LobbyTimedOutFix.cs,
        //  但没更新插件的朋友照样会崩, 所以服务端这一侧也得保证名单里一定有自己。)
        //
        // 认不出请求者时宁可不罚: 发一份不含自己的名单出去比放他进队列危险得多。
        IReadOnlyList<MatchSeat> requester = requesterUserId == 0
            ? Array.Empty<MatchSeat>()
            : seats.Where(s => s.UserId == requesterUserId).ToList();

        if (requester.Count == 0 && requesterUserId != 0)
        {
            Log.Warning("{Client} 排队 {Queue}: 发起人 {UserId} 不在自己的队伍名单里, 跳过罚期检查",
                client.Name, queue, requesterUserId);
        }

        IReadOnlyList<MatchmakingTimeOutData> punished =
            requester.Count > 0 ? _punishedOf?.Invoke(requester) : null;

        if (punished is { Count: > 0 })
        {
            Log.Information("{Client} 排队 {Queue} 被拦下: 本人 {User} 还在离局罚期 (到 {Until:u})",
                client.Name, queue, punished[0].UserId,
                new DateTime(punished[0].AvailableAtTicks, DateTimeKind.Utc));
            return new JoinQueueOutcome(JoinQueueResult.PartyMemberIsTimedOut, punished.ToArray());
        }

        lock (_lock)
        {
            RemoveLocked(client, "重新排队");

            _parties[client] = new Party(client, queue, seats, _utcNow())
            {
                Skill = skill,
                CrossroadDifficulty = crossroadDifficulty,
            };
            Log.Information("{Client} 进入队列 {Queue} ({Count} 人, 段位 {Skill}: {Roster}), 队列里现有 {Waiting} 队",
                client.Name, queue, seats.Count, skill, Describe(seats), _parties.Values.Count(p => p.Queue == queue));
        }

        return JoinQueueOutcome.Of(JoinQueueResult.Success);
    }

    /// <summary>
    /// 队长报"我这队已准备 N 人" (累计值)。凑齐了就开局。
    /// </summary>
    public void ReportReady(IQueueClient client, byte[] matchGuid, int playersReadyInParty)
    {
        lock (_lock)
        {
            if (!_parties.TryGetValue(client, out Party party) || party.Match == null)
            {
                Log.Debug("{Client} 报准备但它不在准备阶段 (可能已超时或已开局), 忽略", client.Name);
                return;
            }

            PendingMatch match = party.Match;
            if (matchGuid == null || !matchGuid.AsSpan().SequenceEqual(match.Guid))
            {
                Log.Warning("{Client} 报的准备对局不是当前这局, 忽略", client.Name);
                return;
            }

            party.Ready = Math.Max(party.Ready, Math.Clamp(playersReadyInParty, 0, party.Seats.Count));
            int ready = match.Parties.Sum(p => p.Ready);

            Log.Information("{Client} 已准备 {PartyReady}/{PartySize}, 整局 {Ready}/{Size}",
                client.Name, party.Ready, party.Seats.Count, ready, match.Size);

            foreach (Party each in match.Parties)
            {
                each.Client.SendPlayersReady(ready, match.Guid);
            }

            if (ready >= match.Size)
            {
                LaunchLocked(match);
            }
        }
    }

    /// <summary>
    /// 这条连接走了 —— 断线、或者客户端发了 LeaveQueueMessage (多半到不了, 见那个结构体的注释)。
    /// </summary>
    public void Leave(IQueueClient client, string why)
    {
        lock (_lock)
        {
            RemoveLocked(client, why);
        }
    }

    /// <summary>
    /// 凑局、准备超时收场、人满的房间开局。定时器每秒调一次; 排队/建房/进房的处理器
    /// 回完响应后也立刻调一次 (满员的队伍不必干等下一拍)。
    /// </summary>
    public void Tick()
    {
        lock (_lock)
        {
            ExpireReadyChecksLocked();
            FormMatchesLocked();
        }
    }

    /// <summary>队列里 (不含准备阶段) 有几队, 单测和日志用。</summary>
    public int WaitingParties(QueueType queue)
    {
        lock (_lock)
        {
            return _parties.Values.Count(p => p.Queue == queue && p.Match == null && p.Room == null);
        }
    }

    // ==================================================================
    // 自定义房间
    // ==================================================================

    public JoinQueueResult CreateRoom(IQueueClient client, CustomRoomSettings settings, MatchSeat seat)
    {
        if (!_isMapRunnable(settings.MapIndex))
        {
            Log.Warning("{Client} 建房选的地图 {Map} 战斗服还跑不起来, 拒绝", client.Name, settings.MapIndex);
            return JoinQueueResult.SessionRequestFailed;
        }

        int max = Math.Clamp(Math.Max(1, settings.TeamSize) * Math.Max(1, settings.TeamCount), 1, MaxRoomSize);

        lock (_lock)
        {
            RemoveLocked(client, "建新房间");

            // 机器人私服不支持; 但房主要了几个机器人, 就少等几个真人, 免得房间永远凑不满。
            CustomRoom room = new CustomRoom(_nextRoomId++, string.IsNullOrWhiteSpace(settings.Name) ? "Room" : settings.Name,
                settings.MapIndex, max, Math.Max(1, max - Math.Max(0, settings.NumberOfBots)), settings.ServerLevel);
            _rooms.Add(room);

            Log.Information("{Client} 建了自定义房间 #{Id} \"{Name}\" 地图 {Map}, {Required}/{Max} 人开局{Bots}",
                client.Name, room.Id, room.Name, room.MapIndex, room.Required, room.MaxPlayers,
                settings.NumberOfBots > 0 ? $" (要了 {settings.NumberOfBots} 个机器人, 私服不支持, 按少等这么多真人处理)" : "");

            JoinRoomLocked(client, room, seat);
        }

        return JoinQueueResult.Success;
    }

    public JoinQueueResult JoinRoom(IQueueClient client, long roomId, MatchSeat seat)
    {
        lock (_lock)
        {
            CustomRoom room = _rooms.FirstOrDefault(r => r.Id == roomId);
            if (room == null || room.Started || room.Members.Count >= room.MaxPlayers)
            {
                Log.Warning("{Client} 进房间 #{Id} 失败: {Why}", client.Name, roomId,
                    room == null ? "没有这个房间" : room.Started ? "已经开局" : "满了");
                return JoinQueueResult.SessionRequestFailed;
            }

            RemoveLocked(client, "进别的房间");
            JoinRoomLocked(client, room, seat);
        }

        return JoinQueueResult.Success;
    }

    public IReadOnlyList<CustomGameListData> ListRooms()
    {
        lock (_lock)
        {
            return _rooms
                .Where(r => !r.Started)
                .Select(r => new CustomGameListData
                {
                    CustomGameId = r.Id,
                    Name = r.Name,
                    MaxPlayers = (byte)r.MaxPlayers,
                    PlayerCount = (byte)r.Members.Count,
                    Map = (byte)r.MapIndex,
                })
                .ToList();
        }
    }

    private void JoinRoomLocked(IQueueClient client, CustomRoom room, MatchSeat seat)
    {
        Party member = new Party(client, QueueType.Debug, new[] { seat }, _utcNow()) { Room = room };
        _parties[client] = member;
        room.Members.Add(member);

        Log.Information("{Client} 进了房间 #{Id} ({Count}/{Max})", client.Name, room.Id, room.Members.Count, room.MaxPlayers);
        BroadcastRoomLocked(room);
    }

    private static void BroadcastRoomLocked(CustomRoom room)
    {
        foreach (Party member in room.Members)
        {
            member.Client.SendCustomGameInfo(room.Id, (byte)room.Members.Count, (byte)room.MaxPlayers);
        }
    }

    // ==================================================================
    // 内部: 凑人 / 准备 / 开局 / 收场
    // ==================================================================

    private void FormMatchesLocked()
    {
        DateTime now = _utcNow();

        foreach (CustomRoom room in _rooms.Where(r => !r.Started && r.Members.Count >= r.Required).ToList())
        {
            StartReadyCheckLocked(room.Members.ToList(), QueueType.Debug, room);
        }

        foreach (IGrouping<QueueType, Party> queue in _parties.Values
                     .Where(p => p.Match == null && p.Room == null)
                     .GroupBy(p => p.Queue)
                     .ToList())
        {
            int capacity = GameModeAvailability.QueueCapacity(queue.Key);
            List<Party> waiting = queue.OrderBy(p => p.QueuedAt).ToList();

            while (waiting.Count > 0)
            {
                // 按排队先后装箱: 段位够近、又装得下, 就装。
                // 段位范围以**等得最久的那一队**为基准 (它的范围最宽, 最该被照顾)。
                List<Party> group = new List<Party> { waiting[0] };
                int size = waiting[0].Seats.Count;
                int window = SkillWindow(waiting[0], now);

                foreach (Party party in waiting.Skip(1))
                {
                    // 十字路口的难度档必须一致 (别的队列恒 0, 这条自动成立)
                    if (size + party.Seats.Count <= capacity
                        && party.CrossroadDifficulty == waiting[0].CrossroadDifficulty
                        && Math.Abs(party.Skill - waiting[0].Skill) <= window)
                    {
                        group.Add(party);
                        size += party.Seats.Count;
                    }
                }

                bool full = size >= capacity;
                bool waitedLongEnough = now - group[0].QueuedAt >= GatherWindow;

                // 有一局还在出生区等人、塞得下这一批 -> 不用等凑人窗口, 直接去补
                // (拾荒 / 十字路口, 见 LidgrenHost.TryAddLateJoiners)
                bool canJoinWaiting = _hasOpenMatch(queue.Key, waiting[0].CrossroadDifficulty, size);
                if (!full && !waitedLongEnough && !canJoinWaiting)
                {
                    break;
                }

                StartReadyCheckLocked(group, queue.Key, room: null);
                waiting.RemoveAll(group.Contains);
            }
        }
    }

    /// <summary>
    /// 这一队现在能接受多大的段位差: 起步 <see cref="SkillWindowStart"/>, 每秒放宽一点,
    /// 等够 <see cref="SkillWindowOpensAt"/> 就不限。
    /// </summary>
    private static int SkillWindow(Party party, DateTime now)
    {
        TimeSpan waited = now - party.QueuedAt;
        if (waited >= SkillWindowOpensAt)
        {
            return int.MaxValue;
        }

        return SkillWindowStart + (int)waited.TotalSeconds * SkillWindowGrowthPerSecond;
    }

    private void StartReadyCheckLocked(List<Party> parties, QueueType queue, CustomRoom room)
    {
        PendingMatch match = new PendingMatch(Guid.NewGuid().ToByteArray(), queue, parties, room,
            _utcNow() + ReadyTimeout);

        if (room != null)
        {
            room.Started = true;
        }

        foreach (Party party in parties)
        {
            party.Match = match;
            party.Ready = 0;
        }

        Log.Information("凑成一局 {Queue} ({Size} 人, {Parties} 队{Room}), 等全员准备 (限时 {Timeout} 秒)",
            queue, match.Size, parties.Count, room != null ? $", 房间 #{room.Id}" : "", ReadyTimeout.TotalSeconds);

        foreach (Party party in parties)
        {
            party.Client.SendMatchupFound(match.Guid, queue, match.Size);
        }
    }

    private void LaunchLocked(PendingMatch match)
    {
        foreach (Party party in match.Parties)
        {
            party.Client.SendFindingServer(match.Guid);
        }

        // 难度档取第一队的 —— 凑局时已经保证一局里所有队伍同档 (FormMatchesLocked)。
        // 自定义房间没有这个概念, 恒 0。
        int difficulty = match.Parties.Count > 0 ? match.Parties[0].CrossroadDifficulty : 0;

        // 先看能不能补进一局还在等人的 (拾荒 / 十字路口)。补上了就不开新的。
        if (match.Room == null && _join != null && TryJoinWaitingLocked(match, difficulty))
        {
            return;
        }

        int map = match.Room?.MapIndex ?? _pickMap(match.Queue, difficulty);
        // 拾荒 (4v4v4) 要分队: 同一小队同队, 人数均摊 (见 ScavengerTeams)。别的模式都是一队, 名单直接摊平。
        List<MatchSeat> seats = GameModeAvailability.IsScavengerMap(map)
            ? ScavengerTeams.Assign(match.Parties.Select(p => p.Seats).ToList())
            : match.Parties.SelectMany(p => p.Seats).ToList();

        ushort? port;
        string failure;
        try
        {
            port = _start(seats, match.Queue, map, difficulty, match.Room?.ServerLevel ?? 0, out failure);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "起战斗服时出错");
            port = null;
            failure = ex.Message;
        }

        foreach (Party party in match.Parties)
        {
            _parties.Remove(party.Client);
        }

        if (match.Room != null)
        {
            _rooms.Remove(match.Room);
        }

        if (port == null)
        {
            Log.Error("一局 {Queue} 起不来战斗服: {Why} —— 通知 {Count} 队退出匹配", match.Queue, failure, match.Parties.Count);
            foreach (Party party in match.Parties)
            {
                party.Client.SendReadyTimeout(false, ReadyTimeoutReason.RequestServerFailed, match.Guid);
            }

            return;
        }

        Log.Information("一局 {Queue} 开在 UDP {Port} (地图 {Map}, 难度档 {Difficulty}), 通知 {Count} 队进场",
            match.Queue, port, map, difficulty, match.Parties.Count);
        foreach (Party party in match.Parties)
        {
            party.Client.SendServerFound(match.Guid, port.Value, match.Queue);
        }
    }

    /// <summary>补进一局等人中的战斗服。成功就通知各队进场并把队伍移出队列。</summary>
    private bool TryJoinWaitingLocked(PendingMatch match, int difficulty)
    {
        ushort? port;
        try
        {
            port = _join(match.Parties.Select(p => p.Seats).ToList(), match.Queue, difficulty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "补进等人中的战斗服时出错, 改开新局");
            return false;
        }

        if (port == null)
        {
            return false;
        }

        foreach (Party party in match.Parties)
        {
            _parties.Remove(party.Client);
        }

        Log.Information("一局 {Queue} ({Size} 人) 补进 UDP {Port} (那一局还在出生区等人), 通知 {Count} 队进场",
            match.Queue, match.Size, port, match.Parties.Count);
        foreach (Party party in match.Parties)
        {
            party.Client.SendServerFound(match.Guid, port.Value, match.Queue);
        }

        return true;
    }

    private void ExpireReadyChecksLocked()
    {
        DateTime now = _utcNow();

        foreach (PendingMatch match in _parties.Values
                     .Select(p => p.Match)
                     .Where(m => m != null && now >= m.Deadline)
                     .Distinct()
                     .ToList())
        {
            Log.Information("一局 {Queue} 准备超时 ({Ready}/{Size} 人准备)", match.Queue,
                match.Parties.Sum(p => p.Ready), match.Size);
            DissolveLocked(match, notReadyAreKicked: true);
        }
    }

    /// <summary>
    /// 这一局开不成了: 点满准备的队伍回队列 (房间成员回房间), 没点满的踢出匹配。
    /// </summary>
    private void DissolveLocked(PendingMatch match, bool notReadyAreKicked)
    {
        foreach (Party party in match.Parties)
        {
            party.Match = null;

            bool keep = !notReadyAreKicked || party.Ready >= party.Seats.Count;
            party.Ready = 0;

            if (!_parties.ContainsKey(party.Client))
            {
                continue;       // 就是它走了才散的局
            }

            if (keep)
            {
                party.Client.SendReadyTimeout(true, ReadyTimeoutReason.ReadyTimeout, match.Guid);
            }
            else
            {
                party.Client.SendReadyTimeout(false, ReadyTimeoutReason.ReadyTimeout, match.Guid);
                RemovePartyLocked(party);
            }
        }

        if (match.Room != null)
        {
            match.Room.Started = false;
            if (match.Room.Members.Count == 0)
            {
                _rooms.Remove(match.Room);
            }
            else
            {
                BroadcastRoomLocked(match.Room);
            }
        }
    }

    private void RemoveLocked(IQueueClient client, string why)
    {
        if (!_parties.TryGetValue(client, out Party party))
        {
            return;
        }

        Log.Information("{Client} 离开{Where} ({Why})", client.Name,
            party.Room != null ? $"房间 #{party.Room.Id}" : $"队列 {party.Queue}", why);

        PendingMatch match = party.Match;
        RemovePartyLocked(party);

        if (match != null)
        {
            // 准备阶段有人走了: 这局开不成, 其余的人一律回去继续等 (他们没做错什么)。
            DissolveLocked(match, notReadyAreKicked: false);
        }
        else if (party.Room != null)
        {
            if (party.Room.Members.Count == 0)
            {
                _rooms.Remove(party.Room);
                Log.Information("房间 #{Id} 空了, 撤掉", party.Room.Id);
            }
            else
            {
                BroadcastRoomLocked(party.Room);
            }
        }
    }

    private void RemovePartyLocked(Party party)
    {
        _parties.Remove(party.Client);
        party.Room?.Members.Remove(party);
    }

    private static string Describe(IReadOnlyList<MatchSeat> seats) =>
        string.Join(", ", seats.Select(s => $"{s.UserId}:{s.Choice?.Character}"));

    // ==================================================================

    private sealed class Party
    {
        public Party(IQueueClient client, QueueType queue, IReadOnlyList<MatchSeat> seats, DateTime queuedAt)
        {
            Client = client;
            Queue = queue;
            Seats = seats;
            QueuedAt = queuedAt;
        }

        public IQueueClient Client { get; }
        public QueueType Queue { get; }
        public IReadOnlyList<MatchSeat> Seats { get; }

        /// <summary>进队列的时间。准备超时回队列时保留 —— 回去之后还排在前面。</summary>
        public DateTime QueuedAt { get; }

        public PendingMatch Match { get; set; }
        public int Ready { get; set; }
        public CustomRoom Room { get; init; }

        /// <summary>段位 (拾荒奖牌数的平均值)。房间成员不按段位挑人, 恒 0。</summary>
        public int Skill { get; init; }

        /// <summary>十字路口的难度档 (0 简单 … 4 极难)。别的队列恒 0。</summary>
        public int CrossroadDifficulty { get; init; }
    }

    private sealed class PendingMatch
    {
        public PendingMatch(byte[] guid, QueueType queue, List<Party> parties, CustomRoom room, DateTime deadline)
        {
            Guid = guid;
            Queue = queue;
            Parties = parties;
            Room = room;
            Deadline = deadline;
            Size = parties.Sum(p => p.Seats.Count);
        }

        public byte[] Guid { get; }
        public QueueType Queue { get; }
        public List<Party> Parties { get; }
        public CustomRoom Room { get; }
        public DateTime Deadline { get; }
        public int Size { get; }
    }

    private sealed class CustomRoom
    {
        public CustomRoom(long id, string name, int mapIndex, int maxPlayers, int required, int serverLevel)
        {
            Id = id;
            Name = name;
            MapIndex = mapIndex;
            MaxPlayers = maxPlayers;
            Required = required;
            ServerLevel = serverLevel;
        }

        public long Id { get; }
        public string Name { get; }
        public int MapIndex { get; }
        public int MaxPlayers { get; }

        /// <summary>几个真人到齐就开 (房主要了机器人的话会比上限少)。</summary>
        public int Required { get; }

        public int ServerLevel { get; }
        public List<Party> Members { get; } = new();
        public bool Started { get; set; }
    }
}
