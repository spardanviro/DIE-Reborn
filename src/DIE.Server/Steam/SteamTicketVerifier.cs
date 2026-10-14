using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Steamworks;

namespace DIE.Server.Steam;

/// <summary>
/// 以"Steam 游戏服务器"身份匿名登录 Steam, 核验玩家的会话票据。见 <see cref="ISteamTicketVerifier"/>。
///
/// ## 线程
///
/// steam_api 的游戏服务器接口不是线程安全的, 回调也只在调 <c>GameServer.RunCallbacks</c>
/// 的线程上触发。所以所有 Steam 调用都在一条专用线程上: 登录处理器 (在网络 IO 线程上)
/// 把"核验这张票"丢进队列, 然后等结果。
///
/// ## 一次核验
///
/// <c>BeginAuthSession</c> 当场就能拒掉格式不对 / 不是这个人的票据; 过了这一关再等
/// <c>ValidateAuthTicketResponse</c> 回调, 那是 Steam 后台的最终结论 (在线、有游戏、
/// 票据没被用过)。拿到结论立刻 <c>EndAuthSession</c> —— 我们只要身份, 不需要一直挂着会话。
/// </summary>
public sealed class SteamTicketVerifier : ISteamTicketVerifier, IDisposable
{
    public const uint AppId = 222900;

    /// <summary>刚启动时 Steam 登录要几秒 (实测 5 秒), 这段时间来的核验先等它一下。</summary>
    private static readonly TimeSpan LogOnGrace = TimeSpan.FromSeconds(10);

    private readonly BlockingCollection<Action> _work = new();
    private readonly Dictionary<ulong, Pending> _pending = new();   // 只在 Steam 线程上碰
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);

    private volatile bool _loggedOn;
    private volatile string _initError;
    private volatile bool _stopping;

    // 回调对象必须一直有人引用着, 否则被 GC 回收后回调就不来了
    private Callback<SteamServersConnected_t> _onConnected;
    private Callback<SteamServerConnectFailure_t> _onConnectFailure;
    private Callback<SteamServersDisconnected_t> _onDisconnected;
    private Callback<ValidateAuthTicketResponse_t> _onValidate;

    private sealed record Pending(TaskCompletionSource<SteamVerdict> Result, DateTime DeadlineUtc);

    private SteamTicketVerifier()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Steam 身份核验" };
    }

    /// <summary>起专用线程、初始化 steam_api、匿名登录。初始化失败不抛, 之后的核验都回 Unavailable。</summary>
    public static SteamTicketVerifier Start()
    {
        SteamTicketVerifier verifier = new SteamTicketVerifier();
        verifier._thread.Start();
        verifier._started.Wait(TimeSpan.FromSeconds(10));
        return verifier;
    }

    /// <summary>初始化失败的原因; null = 初始化成功 (不代表已经登录上)。</summary>
    public string InitError => _initError;

    public bool IsLoggedOn => _loggedOn;

    /// <summary>
    /// 异步版: 等 Steam 登录宽限、等验票回调都不占线程 (登录请求走这个, 见 LoginHandlers.ResolveIdentityAsync)。
    /// </summary>
    public async Task<SteamVerdict> VerifyAsync(byte[] ticket, ulong claimedSteamId, TimeSpan timeout)
    {
        if (_initError != null)
        {
            return SteamVerdict.Unavailable(claimedSteamId, "Steam 初始化失败: " + _initError);
        }

        Stopwatch waited = Stopwatch.StartNew();
        while (!_loggedOn && waited.Elapsed < LogOnGrace && !_stopping)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (!_loggedOn)
        {
            return SteamVerdict.Unavailable(claimedSteamId, "服务器还没连上 Steam");
        }

        if (ticket == null || ticket.Length == 0)
        {
            return SteamVerdict.Rejected(claimedSteamId, "没有票据");
        }

        TaskCompletionSource<SteamVerdict> result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTime deadline = DateTime.UtcNow + timeout;
        _work.Add(() => Begin((byte[])ticket.Clone(), claimedSteamId, result, deadline));

        // Steam 线程自己会在截止时间把它判超时 (ExpireOverdue); 这里再兜一层, 以防 Steam 线程已经停了
        Task finished = await Task.WhenAny(result.Task, Task.Delay(timeout + TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        return finished == result.Task
            ? await result.Task.ConfigureAwait(false)
            : SteamVerdict.Unavailable(claimedSteamId, "等 Steam 超时");
    }

    public SteamVerdict Verify(byte[] ticket, ulong claimedSteamId, TimeSpan timeout)
    {
        if (_initError != null)
        {
            return SteamVerdict.Unavailable(claimedSteamId, "Steam 初始化失败: " + _initError);
        }

        Stopwatch waited = Stopwatch.StartNew();
        while (!_loggedOn && waited.Elapsed < LogOnGrace && !_stopping)
        {
            Thread.Sleep(50);
        }

        if (!_loggedOn)
        {
            return SteamVerdict.Unavailable(claimedSteamId, "服务器还没连上 Steam");
        }

        if (ticket == null || ticket.Length == 0)
        {
            return SteamVerdict.Rejected(claimedSteamId, "没有票据");
        }

        TaskCompletionSource<SteamVerdict> result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTime deadline = DateTime.UtcNow + timeout;
        _work.Add(() => Begin((byte[])ticket.Clone(), claimedSteamId, result, deadline));

        // 多给一秒: 真正的超时由 Steam 线程按 deadline 判, 这里只是兜底别永远卡住
        return result.Task.Wait(timeout + TimeSpan.FromSeconds(1))
            ? result.Task.Result
            : SteamVerdict.Unavailable(claimedSteamId, "等 Steam 超时");
    }

    // ---------------- 以下都在 Steam 线程上 ----------------

    private void Run()
    {
        try
        {
            Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString());
            Environment.SetEnvironmentVariable("SteamGameId", AppId.ToString());

            // 仅认证模式: 不跑 VAC, 不开心跳, 不会出现在 Steam 的服务器列表里。
            // 端口只是报给 Steam 的信息, 共享查询端口 = 不额外绑套接字。
            if (!GameServer.Init(0, 27115, Constants.STEAMGAMESERVER_QUERY_PORT_SHARED,
                                 EServerMode.eServerModeAuthentication, "0.8.5.38860"))
            {
                _initError = "GameServer.Init 返回 false (steam_api64.dll / steam_appid.txt 缺了?)";
                return;
            }

            _onConnected = Callback<SteamServersConnected_t>.CreateGameServer(_ =>
            {
                _loggedOn = true;
                Log.Information("已以匿名游戏服务器登录 Steam (SteamID {Id}), 玩家身份由 Steam 核验",
                    SteamGameServer.GetSteamID().m_SteamID);
            });
            _onConnectFailure = Callback<SteamServerConnectFailure_t>.CreateGameServer(f =>
                Log.Warning("连不上 Steam: {Result} (还会自动重试: {Retrying})", f.m_eResult, f.m_bStillRetrying));
            _onDisconnected = Callback<SteamServersDisconnected_t>.CreateGameServer(d =>
            {
                _loggedOn = false;
                Log.Warning("和 Steam 断开了: {Result} —— 期间登录的玩家会自动重连等待", d.m_eResult);
            });
            _onValidate = Callback<ValidateAuthTicketResponse_t>.CreateGameServer(OnValidate);

            SteamGameServer.SetProduct("DeadIslandEpidemic");
            SteamGameServer.SetModDir("DeadIslandEpidemic");
            SteamGameServer.SetGameDescription("DIE_Reborn");
            SteamGameServer.SetDedicatedServer(true);
            SteamGameServer.LogOnAnonymous();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _initError = ex.Message;
            return;
        }
        finally
        {
            _started.Set();
        }

        try
        {
            while (!_stopping)
            {
                GameServer.RunCallbacks();

                while (_work.TryTake(out Action job))
                {
                    job();
                }

                ExpireOverdue();
                Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Steam 核验线程出错, 之后的登录都会等待重试");
            _initError = ex.Message;
        }
        finally
        {
            foreach (Pending pending in _pending.Values)
            {
                pending.Result.TrySetResult(SteamVerdict.Unavailable(0, "服务器正在关闭"));
            }

            _pending.Clear();
            try { SteamGameServer.LogOff(); } catch { }
            try { GameServer.Shutdown(); } catch { }
        }
    }

    private void Begin(byte[] ticket, ulong claimed, TaskCompletionSource<SteamVerdict> result, DateTime deadline)
    {
        CSteamID who = new CSteamID(claimed);

        // 同一个人上一次还没出结果 (比如断线秒重连): 结束旧的, 让旧请求那边重试
        if (_pending.Remove(claimed, out Pending previous))
        {
            SteamGameServer.EndAuthSession(who);
            previous.Result.TrySetResult(SteamVerdict.Unavailable(claimed, "被同一玩家更新的登录顶掉"));
        }

        EBeginAuthSessionResult begin = SteamGameServer.BeginAuthSession(ticket, ticket.Length, who);
        switch (begin)
        {
            case EBeginAuthSessionResult.k_EBeginAuthSessionResultOK:
                _pending[claimed] = new Pending(result, deadline);
                break;

            // 票据过期 / 重复: 客户端重连会拿一张新票据, 按"稍后重试"处理
            case EBeginAuthSessionResult.k_EBeginAuthSessionResultExpiredTicket:
            case EBeginAuthSessionResult.k_EBeginAuthSessionResultDuplicateRequest:
                result.TrySetResult(SteamVerdict.Unavailable(claimed, begin.ToString()));
                break;

            // InvalidTicket (伪造 / 不是这个人的 / 没装插件交的是加密票据) / GameMismatch / InvalidVersion
            default:
                result.TrySetResult(SteamVerdict.Rejected(claimed, begin.ToString()));
                break;
        }
    }

    private void OnValidate(ValidateAuthTicketResponse_t response)
    {
        ulong steamId = response.m_SteamID.m_SteamID;
        if (!_pending.Remove(steamId, out Pending pending))
        {
            return;   // 已经超时放弃了的, 或者是结束会话后的迟到通知
        }

        SteamGameServer.EndAuthSession(response.m_SteamID);
        pending.Result.TrySetResult(Classify(steamId, response.m_OwnerSteamID.m_SteamID,
                                             response.m_eAuthSessionResponse));
    }

    /// <summary>Steam 的最终结论归成三类。见 <see cref="SteamAuthOutcome"/>。</summary>
    public static SteamVerdict Classify(ulong steamId, ulong owner, EAuthSessionResponse response)
    {
        string detail = response.ToString();
        return response switch
        {
            EAuthSessionResponse.k_EAuthSessionResponseOK => SteamVerdict.Verified(steamId, owner, detail),

            // "这次不行, 换张票 / 等一下就好": 玩家自己的 Steam 掉线、票据被取消或重用。
            //
            // AuthTicketInvalid 也算这一类: 伪造的 / 冒充别人的票据在 BeginAuthSession
            // 那一步就当场 InvalidTicket 了 (SteamAuthProbe 实测), 走到最终结论这一步才
            // 说无效, 多半是票据刚取、Steam 还没登记好。当成冒充塞进共享账号的话,
            // 玩家这一局的进度就记到共享账号上去了; 让他重连换张票才对。
            EAuthSessionResponse.k_EAuthSessionResponseUserNotConnectedToSteam or
            EAuthSessionResponse.k_EAuthSessionResponseAuthTicketCanceled or
            EAuthSessionResponse.k_EAuthSessionResponseAuthTicketInvalidAlreadyUsed or
            EAuthSessionResponse.k_EAuthSessionResponseAuthTicketInvalid or
            EAuthSessionResponse.k_EAuthSessionResponseLoggedInElseWhere
                => SteamVerdict.Unavailable(steamId, detail),

            // 没有游戏 / VAC 或发行方封禁
            _ => SteamVerdict.Rejected(steamId, detail),
        };
    }

    private void ExpireOverdue()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        List<ulong> overdue = null;
        foreach (KeyValuePair<ulong, Pending> entry in _pending)
        {
            if (entry.Value.DeadlineUtc <= now)
            {
                (overdue ??= new List<ulong>()).Add(entry.Key);
            }
        }

        if (overdue == null)
        {
            return;
        }

        foreach (ulong steamId in overdue)
        {
            Pending pending = _pending[steamId];
            _pending.Remove(steamId);
            SteamGameServer.EndAuthSession(new CSteamID(steamId));
            pending.Result.TrySetResult(SteamVerdict.Unavailable(steamId, "Steam 迟迟没给结论"));
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
