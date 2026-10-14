using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using DIE.Server.Data;
using Serilog;

namespace DIE.Server.Match;

/// <summary>起好的一局。</summary>
public sealed record LaunchedMatch(LidgrenHost Host, byte[] MatchGuid, ushort Port, int MapIndex);

/// <summary>
/// 开一局: 占端口 -&gt; 登记对局 -&gt; 查每个人的账号等级 -&gt; 起战斗服。
///
/// Solo 开局 (<c>SoloServerCreateRequest</c>)、组队排队 (<c>MatchmakingService</c>)、
/// 自定义房间都走这一个入口 —— 以前这串逻辑写在 <c>MatchHandlers.OnSoloServerCreate</c> 里,
/// 排队也要用, 于是抽出来。
///
/// **不管地址**: 发给客户端的战斗服地址取决于**客户端**从哪来 (<c>MatchHostAddress</c>),
/// 由各自的调用方按那条连接算。这里只把"对局记录里写哪个地址"交给调用方给。
/// </summary>
public sealed class MatchLauncher
{
    private readonly AccountStore _accounts;
    private readonly MatchStore _matches;
    private readonly MatchHostRegistry _hosts;
    private readonly Func<ulong, int> _refreshAccount;
    private readonly RewardLedger _ledger;
    private readonly AccountLevelRewards _levelRewards;
    private readonly StarterQuestProgress _starterQuests;
    private readonly DropTable _dropTable;

    /// <param name="refreshAccount">局末通知大厅刷新账号数据, 见 <c>RequestServer.RefreshAccount</c>。</param>
    public MatchLauncher(AccountStore accounts, MatchStore matches, MatchHostRegistry hosts,
                         Func<ulong, int> refreshAccount = null)
    {
        _accounts = accounts;
        _matches = matches;
        _hosts = hosts;
        _refreshAccount = refreshAccount;
        _ledger = new RewardLedger(accounts);
        _levelRewards = new AccountLevelRewards(accounts);
        _starterQuests = new StarterQuestProgress(accounts);

        // 掉落表拼一次就够 —— 拼它要遍历整份图鉴 (322 把武器的配方),
        // 每开一局重拼纯属浪费。表本身是只读的, 多局共用没有问题。
        _dropTable = new MatchDropTable().Build(new DropTableBuilder().Build());

        // 战斗服关掉时把对局记录标成"已结束" —— 状态列以前从来没人推进过 (T18)
        hosts.MatchClosed = matches.MarkEnded;
    }

    public MatchHostRegistry Hosts => _hosts;

    /// <summary>
    /// 开一局。失败返回 null 并给出原因 —— 调用方据此回失败, 而不是把一个连不上的地址
    /// 发给客户端 (那样客户端会卡在加载界面直到超时)。
    /// </summary>
    /// <param name="seats">名单, 第一位是队长 / 开局者。</param>
    /// <param name="recordedHostIp">写进对局记录的战斗服地址 (只是记录, 不影响谁连哪)。</param>
    /// <param name="infectionLevel">感染等级; 0 = 按全员装的武器算。</param>
    /// <param name="crossroadDifficulty">
    /// 十字路口的难度档 (0 简单 … 4 极难), 从票据里解出来; 别的模式恒 0。
    /// </param>
    public LaunchedMatch Launch(IReadOnlyList<MatchSeat> seats, QueueType queueType, int mapIndex,
                                string recordedHostIp, int infectionLevel, out string failure,
                                int crossroadDifficulty = 0)
    {
        if (seats == null || seats.Count == 0)
        {
            failure = "名单是空的";
            return null;
        }

        int? reserved = _hosts.ReservePort();
        if (reserved == null)
        {
            failure = $"战斗服端口段 {_hosts.FirstPort}-{_hosts.LastPort} 全部占满 (同时开着 {_hosts.Count} 局)";
            return null;
        }

        ushort port = (ushort)reserved.Value;
        MatchAllocation allocation;
        try
        {
            allocation = _matches.CreateMatch(seats.Select(s => s.UserId), recordedHostIp, port,
                (int)queueType, mapIndex);
        }
        catch (Exception)
        {
            // 占住的端口还回去, 不然每失败一次池子就少一个
            _hosts.ReleasePort(port);
            throw;
        }

        List<MatchMember> members = seats
            .Select(s => new MatchMember(s.UserId, s.Choice, AccountLevelOf(s.UserId), s.Team))
            .ToList();

        // 开局前还可能补人 (TryJoin), 结算时要按人查武器 —— 名单用一份能追加的
        List<MatchSeat> allSeats = seats.ToList();

        // 感染等级 = 这一局的难度档, 按全员装的武器品阶自动定。原版是玩家自己在服务器列表里
        // 挑档, 私服没有那张列表, 所以按装备走 —— 效果一样, 只是不用手选。详见 InfectionLevel。
        int infection = infectionLevel > 0
            ? DIE.Server.Game.InfectionLevel.Clamp(infectionLevel)
            : DIE.Server.Game.InfectionLevel.FromWeapons(
                seats.SelectMany(s => s.Choice?.WeaponSchematicIds ?? Array.Empty<ushort>()).ToList());

        MatchHostCallbacks callbacks = new MatchHostCallbacks
        {
            ResolveUserByTicket = _accounts.ResolveByAuthTicket,

            // 经验入账走 AccountLevelRewards 而不是 AccountStore —— 它加完经验顺便把这一局
            // 跨过的等级奖励发掉 (17/35 级的暗码、24/38 级的 Focus 装置蓝图那些)。
            AwardAccountXp = _levelRewards.AddExperience,
            AccountSnapshot = user => _ledger.SnapshotFor(user, WeaponsOf(allSeats, user)),
            GrantRewards = _ledger.Grant,
            MarkScavengerFirstWin = _ledger.MarkScavengerFirstWin,
            SpendConsumables = SpendConsumables,
            DropTable = _dropTable,
            AdvanceStarterQuests = (user, mode) => _starterQuests.OnMatchCompleted(user, mode),

            // 十字路口难度解锁: 够条件才推一步。判据和数都是原厂的,
            // 见 CrossroadUnlock —— 这一列以前只有人读没有人写。
            AdvanceCrossroadProgress = (user, difficulty, supplies) =>
            {
                byte before = _accounts.CrossroadProgressOf(user);

                if (!DIE.Server.Game.CrossroadUnlock.Qualifies(before, difficulty, supplies))
                {
                    // 不够格也要把**当前**进度报回去 —— 客户端在奖励界面里是
                    // 无条件 SetCrossroadProgress(报文里那个数) 的, 返回 0
                    // 会把玩家已有的进度抹掉。
                    Log.Information(
                        "账号 {UserId} 这局不算解锁进度 (第 {Gate} 档, 补给 {Supplies}/{Required}), "
                        + "进度保持 {Progress}",
                        user, difficulty, supplies,
                        DIE.Server.Game.CrossroadUnlock.RequiredSupplies(difficulty), before);

                    return before;
                }

                byte after = _accounts.AdvanceCrossroadProgress(user);

                Log.Information(
                    "账号 {UserId} 十字路口解锁推进 {Before} -> {After} "
                    + "(第 {Level} 档 {Step}/{Steps}, 这局补给 {Supplies} >= {Required})",
                    user, before, after,
                    DIE.Server.Game.CrossroadUnlock.LevelOf(after),
                    DIE.Server.Game.CrossroadUnlock.StepOf(after),
                    DIE.Server.Game.CrossroadUnlock.StepsFor(
                        DIE.Server.Game.CrossroadUnlock.LevelOf(after)),
                    supplies,
                    DIE.Server.Game.CrossroadUnlock.RequiredSupplies(difficulty));

                return after;
            },
            RefreshAccount = _refreshAccount,
        };

        LidgrenHost host = _hosts.TryCreate(allocation.MatchGuid, port, mapIndex,
            new MatchRoster(members), infection, callbacks, queueType, crossroadDifficulty);
        if (host == null)
        {
            failure = "战斗服端点启动失败";
            return null;
        }

        _matches.MarkRunning(allocation.MatchGuid);
        _seatsByHost.AddOrUpdate(host, allSeats);

        Log.Information("已开局 UDP {Port}: 队列={Queue} 地图={Map} 感染等级={Infection} 名单 {Roster}",
            port, queueType, mapIndex, infection,
            string.Join(", ", members.Select(m => $"{m.UserId}:{m.Choice?.Character}(Lv{m.AccountLevel})")));

        failure = null;
        return new LaunchedMatch(host, allocation.MatchGuid, port, mapIndex);
    }

    /// <summary>每个战斗服的完整名单 (含开局前补进来的), 结算查武器用。战斗服关了留着也无妨 (弱引用表)。</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LidgrenHost, List<MatchSeat>> _seatsByHost = new();

    /// <summary>
    /// 有没有一局还在等人、塞得下这么多人的 (匹配服据此决定"不用等凑人窗口, 直接去补")。
    /// 只是预判 —— 真正补的时候 (<see cref="TryJoin"/>) 还会再判一次。
    /// </summary>
    public bool HasOpenMatch(QueueType queueType, int crossroadDifficulty, int seats) =>
        OpenMatches(queueType, crossroadDifficulty)
            .Any(h => h.Roster.Count + seats <= GameModeAvailability.QueueCapacity(queueType));

    /// <summary>
    /// 把一批队伍补进一局**还在出生区等人**的战斗服 (拾荒 / 十字路口, 见
    /// <see cref="LidgrenHost.TryAddLateJoiners"/>)。优先补人最多的那一局 —— 先把一局凑满。
    /// 没有合适的返回 null, 调用方照常开新的一局。
    /// </summary>
    public LaunchedMatch TryJoin(IReadOnlyList<IReadOnlyList<MatchSeat>> parties, QueueType queueType,
                                 int crossroadDifficulty)
    {
        List<IReadOnlyList<MatchMember>> members = parties
            .Select(p => (IReadOnlyList<MatchMember>)p
                .Select(s => new MatchMember(s.UserId, s.Choice, AccountLevelOf(s.UserId)))
                .ToList())
            .ToList();

        foreach (LidgrenHost host in OpenMatches(queueType, crossroadDifficulty)
                     .OrderByDescending(h => h.Roster.Count))
        {
            if (!host.TryAddLateJoiners(members, out string why))
            {
                Log.Debug("战斗服 UDP {Port} 不能补人: {Why}", host.Port, why);
                continue;
            }

            List<MatchSeat> joined = parties.SelectMany(p => p).ToList();
            _matches.AddPlayers(host.MatchGuid, joined.Select(s => s.UserId));
            if (_seatsByHost.TryGetValue(host, out List<MatchSeat> all))
            {
                lock (all)
                {
                    all.AddRange(joined);
                }
            }

            return new LaunchedMatch(host, host.MatchGuid, (ushort)host.Port, host.MapIndex);
        }

        return null;
    }

    private IEnumerable<LidgrenHost> OpenMatches(QueueType queueType, int crossroadDifficulty) =>
        _hosts.Snapshot().Where(h => h.QueueType == queueType
                                     && h.CrossroadDifficulty == crossroadDifficulty
                                     && h.WaitsForPlayers
                                     && !h.MatchBegun
                                     && !h.MatchOver
                                     && h.SimulationReady
                                     && h.SecondsUntilForceStart >= LidgrenHost.LateJoinMinSeconds);

    /// <summary>
    /// 账号等级决定技能栏里有几个技能, 必须在开局前查好带给战斗服 —— 战斗服自己不连账号库。
    ///
    /// 换算用原厂的 <c>ExperienceToLevel</c> (AccountLevelHelpers.cs:137), 它从 1 起算,
    /// 经验为 0 也返回 1。读不到账号 (含 id 未知的队员位) 时退回 1 而不是 0 —— 0 级在原厂
    /// 根本不存在, 而它的表现是技能栏彻底为空且零报错。
    /// </summary>
    private void SpendConsumables(ulong userId, IReadOnlyList<(ushort Id, int Used)> used)
    {
        foreach ((ushort id, int count) in used)
        {
            _accounts.AddStackable(userId, id, StackableType.Consumable, -count);
        }
    }

    private int AccountLevelOf(ulong userId)
    {
        if (userId == 0)
        {
            return 1;
        }

        Account account = _accounts.Load(userId);
        if (account == null)
        {
            Log.Warning("开局时读不到账号 {UserId}, 账号等级退回 1 级", userId);
            return 1;
        }

        return AccountLevelHelpers.ExperienceToLevel(account.StoryMapXp);
    }

    private static IReadOnlyList<ushort> WeaponsOf(List<MatchSeat> seats, ulong userId)
    {
        lock (seats)
        {
            foreach (MatchSeat seat in seats)
            {
                if (seat.UserId == userId)
                {
                    return seat.Choice?.WeaponSchematicIds;
                }
            }
        }

        return null;
    }
}
