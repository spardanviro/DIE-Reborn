using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Serilog;

namespace DIE.Server.Match;

/// <summary>
/// 管理进行中对局的 UDP 端点。
///
/// 原厂是由一个独立的 ServerPool 进程池拉起战斗服实例的 (ip.cfg 里还留着
/// ServerPoolTCPPort / ServerPoolExternalIP)。私服不需要那一层 —— 战斗服和
/// 本进程同生共死, 一局一个 <see cref="LidgrenHost"/>。
///
/// 时序上有个要求: 端点必须在**回复 SoloServerCreated 之前**就绪。
/// 客户端拿到地址后会立刻拉起 Game.exe 去连, 晚一步就是一次连接失败。
///
/// ## 端口池 + 回收 (公网开服才有的要求)
///
/// 以前端口从 50000 一路 +1 到 60000, 端点也从来不关。本机玩看不出问题; 放到公网上,
/// 路由器得映射一万个 UDP 端口, 而且每开一局就永久漏一个线程 + 一整份模拟。
///
/// 现在: 端口从一个固定小段里取 (默认 50000-50099, 路由器照这段映射), 总是取最小的
/// 空闲端口; 没人在线超过 <see cref="IdleTimeout"/> 的端点由 <see cref="ReapIdle"/>
/// 关掉, 端口还回池里。刚还回来的端口冷却 <see cref="PortCooldown"/> 再发 ——
/// Lidgren 的 Shutdown 是异步的, 立刻在同一端口上重新绑定可能失败。
/// </summary>
public sealed class MatchHostRegistry : IDisposable
{
    /// <summary>默认端口段起点。沿用原厂战斗服的惯用段。</summary>
    public const int DefaultFirstPort = 50000;

    /// <summary>默认端口段大小 = 最多同时开多少局。</summary>
    public const int DefaultPortCount = 100;

    /// <summary>
    /// 没人在线多久就关。刚起来还没人连也算 —— 客户端拿到地址才去拉起 Game.exe
    /// 加载地图, 慢机器要一两分钟; 掉线后原厂客户端也会弹"等待重连", 都得等得起。
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>刚还回来的端口多久之后才能再发出去。</summary>
    public static readonly TimeSpan PortCooldown = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, LidgrenHost> _hosts = new();
    private readonly object _portLock = new();
    private readonly HashSet<int> _portsInUse = new();
    private readonly Dictionary<int, DateTime> _portFreeAfterUtc = new();
    private Timer _reaper;
    private bool _disposed;

    /// <summary>
    /// 战斗服被关掉时叫一声 (参数是 matchGuid) —— <c>MatchLauncher</c> 接它去把对局记录
    /// 标成"已结束"。战斗服自己不碰账号库/对局库, 所以走回调。
    /// </summary>
    public Action<byte[]> MatchClosed { get; set; }

    public MatchHostRegistry(int firstPort = DefaultFirstPort, int portCount = DefaultPortCount)
    {
        if (firstPort < 1 || portCount < 1 || firstPort + portCount - 1 > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(portCount),
                $"战斗服端口段 {firstPort}+{portCount} 不合法");
        }

        FirstPort = firstPort;
        PortCount = portCount;
    }

    public int FirstPort { get; }

    public int PortCount { get; }

    public int LastPort => FirstPort + PortCount - 1;

    /// <summary>当前开着的战斗服数。</summary>
    public int Count => _hosts.Count;

    /// <summary>
    /// 取一个空闲端口并占住。端口段满了 (同时开的局数到顶) 返回 null。
    /// 占住之后必须接着 <see cref="TryCreate"/> —— 它失败时会自己把端口还回来。
    /// </summary>
    public int? ReservePort() => ReservePort(DateTime.UtcNow);

    /// <summary>同上, 时间由调用方给 (单测用)。</summary>
    public int? ReservePort(DateTime nowUtc)
    {
        lock (_portLock)
        {
            for (int port = FirstPort; port <= LastPort; port++)
            {
                if (_portsInUse.Contains(port))
                {
                    continue;
                }

                if (_portFreeAfterUtc.TryGetValue(port, out DateTime freeAfter) && freeAfter > nowUtc)
                {
                    continue;
                }

                _portFreeAfterUtc.Remove(port);
                _portsInUse.Add(port);
                return port;
            }
        }

        return null;
    }

    /// <summary>把 <see cref="ReservePort()"/> 占住的端口还回去 (进冷却)。重复还是无害的。</summary>
    public void ReleasePort(int port)
    {
        lock (_portLock)
        {
            if (_portsInUse.Remove(port))
            {
                _portFreeAfterUtc[port] = DateTime.UtcNow + PortCooldown;
            }
        }
    }

    /// <summary>开始定时回收空战斗服 (每 30 秒扫一次)。单测不开, 直接调 <see cref="ReapIdle"/>。</summary>
    public void StartReaper()
    {
        _reaper ??= new Timer(_ =>
        {
            try
            {
                ReapIdle(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "回收空战斗服时出错");
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>关掉没人在线超过 <see cref="IdleTimeout"/> 的战斗服, 返回关了几个。</summary>
    public int ReapIdle(DateTime nowUtc)
    {
        int closed = 0;
        foreach (KeyValuePair<string, LidgrenHost> entry in _hosts)
        {
            DateTime? emptySince = entry.Value.EmptySinceUtc;
            if (emptySince.HasValue && nowUtc - emptySince.Value >= IdleTimeout
                && _hosts.TryRemove(entry.Key, out LidgrenHost host))
            {
                Log.Information("战斗服 UDP {Port} 已空 {Minutes:F0} 分钟, 回收", host.Port,
                    (nowUtc - emptySince.Value).TotalMinutes);
                host.Dispose();
                ReleasePort(host.Port);
                MatchClosed?.Invoke(host.MatchGuid);
                closed++;
            }
        }

        if (closed > 0)
        {
            Log.Information("回收了 {Closed} 个空战斗服, 还开着 {Open} 个 (端口段 {First}-{Last})",
                closed, _hosts.Count, FirstPort, LastPort);
        }

        return closed;
    }

    /// <summary>
    /// 起一个一人局的战斗服端点。失败返回 null —— 调用方据此回失败响应, 而不是把一个
    /// 连不上的地址发给客户端 (那样客户端会卡在加载界面直到超时)。
    /// </summary>
    /// <param name="accountLevel">
    /// 玩家账号等级, 决定技能栏里有几个技能。由调用方从账号经验换算
    /// (<c>AccountLevelHelpers.ExperienceToLevel</c>) —— 战斗服自己不碰账号库。
    /// </param>
    /// <param name="callbacks">战斗服和账号库之间的几条回调, 见 <see cref="MatchHostCallbacks"/>。</param>
    public LidgrenHost TryCreate(byte[] matchGuid, int port, int mapIndex, ulong ownerUserId,
                                 DIE.Server.Data.PlayerChoice? choice = null,
                                 int accountLevel = 1,
                                 int infectionLevel = 0,
                                 MatchHostCallbacks callbacks = null) =>
        TryCreate(matchGuid, port, mapIndex, MatchRoster.Solo(ownerUserId, choice, accountLevel),
                  infectionLevel, callbacks);

    /// <summary>按名单起一个战斗服 (组队排队 / 自定义房间 / Solo 都走这里)。</summary>
    /// <param name="infectionLevel">感染等级; 0 = 按全员装的武器现算。</param>
    /// <param name="crossroadDifficulty">十字路口的难度档 (0 简单 … 4 极难); 别的模式给 0。</param>
    public LidgrenHost TryCreate(byte[] matchGuid, int port, int mapIndex, MatchRoster roster,
                                 int infectionLevel, MatchHostCallbacks callbacks,
                                 ConductorCrafting.QueueType queueType = ConductorCrafting.QueueType.None,
                                 int crossroadDifficulty = 0)
    {
        callbacks ??= new MatchHostCallbacks();
        LidgrenHost host = new LidgrenHost(matchGuid, port, mapIndex, roster,
                                           infectionLevel: infectionLevel, queueType: queueType,
                                           crossroadDifficulty: crossroadDifficulty)
        {
            AwardAccountXp = callbacks.AwardAccountXp,
            AccountSnapshot = callbacks.AccountSnapshot,
            GrantRewards = callbacks.GrantRewards,
            MarkScavengerFirstWin = callbacks.MarkScavengerFirstWin,
            SpendConsumables = callbacks.SpendConsumables,
            DropTable = callbacks.DropTable,
            AdvanceStarterQuests = callbacks.AdvanceStarterQuests,
            AdvanceCrossroadProgress = callbacks.AdvanceCrossroadProgress,
            RefreshAccount = callbacks.RefreshAccount,
            ResolveUserByTicket = callbacks.ResolveUserByTicket,
        };
        try
        {
            host.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法在 UDP {Port} 上启动战斗服", port);
            host.Dispose();
            ReleasePort(port);
            return null;
        }

        _hosts[Key(matchGuid)] = host;
        return host;
    }

    /// <summary>当前开着的战斗服快照 (观战列表、重连查询用)。</summary>
    public IReadOnlyList<LidgrenHost> Snapshot() => new List<LidgrenHost>(_hosts.Values);

    /// <summary>
    /// 这个账号有没有一局可以重连回去的 (<see cref="LidgrenHost.CanRejoin"/>)。没有就返回 null。
    /// </summary>
    /// <summary>同一个账号最多同时挂着几局 (没打完的)。**我们定的**: 正常玩家同一时间只在一局里, 留一格给"刚开了一局又开"。</summary>
    public const int MaxLiveMatchesPerUser = 2;

    /// <summary>名单上有这个人、而且空了这么久的局, 他再开新局时直接关掉 (刚开出来还没人连上的不算, 客户端要几秒才连进来)。</summary>
    public static readonly TimeSpan StaleEmptyMatch = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 某人开新局之前清理: 关掉名单上有他、已经空了 <see cref="StaleEmptyMatch"/> 的局, 返回还剩几局没打完。
    ///
    /// 2026-09-17 审校: 以前单人开局不设限, 一个登录用户反复开局, 每局一整份模拟层、空 5 分钟才回收,
    /// 100 个端口开满后别人都开不了, 内存也可能先撑爆。
    /// </summary>
    public int PruneAndCountLiveMatches(ulong userId, DateTime nowUtc)
    {
        if (userId == 0)
        {
            return 0;
        }

        int live = 0;
        foreach (KeyValuePair<string, LidgrenHost> entry in _hosts)
        {
            LidgrenHost host = entry.Value;
            if (!host.Roster.Members.Any(m => m.UserId == userId))
            {
                continue;
            }

            if (host.EmptySinceUtc is DateTime emptySince && nowUtc - emptySince >= StaleEmptyMatch
                && _hosts.TryRemove(entry.Key, out LidgrenHost stale))
            {
                Log.Information("账号 {UserId} 开新局, 关掉他已经空了 {Seconds:F0} 秒的旧局 UDP {Port}",
                    userId, (nowUtc - emptySince).TotalSeconds, stale.Port);
                stale.Dispose();
                ReleasePort(stale.Port);
                MatchClosed?.Invoke(stale.MatchGuid);
                continue;
            }

            if (!host.MatchOver)
            {
                live++;
            }
        }

        return live;
    }

    public LidgrenHost FindRejoinable(ulong userId)
    {
        foreach (LidgrenHost host in _hosts.Values)
        {
            if (host.CanRejoin(userId))
            {
                return host;
            }
        }

        return null;
    }

    public LidgrenHost Find(byte[] matchGuid)
    {
        return _hosts.TryGetValue(Key(matchGuid), out LidgrenHost host) ? host : null;
    }

    public void Close(byte[] matchGuid)
    {
        if (_hosts.TryRemove(Key(matchGuid), out LidgrenHost host))
        {
            host.Dispose();
            ReleasePort(host.Port);
            MatchClosed?.Invoke(host.MatchGuid);
        }
    }

    private static string Key(byte[] matchGuid) => Convert.ToHexString(matchGuid);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reaper?.Dispose();
        foreach (LidgrenHost host in _hosts.Values)
        {
            host.Dispose();
        }

        _hosts.Clear();
    }
}
