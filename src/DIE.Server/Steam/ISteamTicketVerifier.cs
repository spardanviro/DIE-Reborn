using System;
using System.Threading.Tasks;

namespace DIE.Server.Steam;

/// <summary>Steam 核验一张会话票据的结果。</summary>
public enum SteamAuthOutcome
{
    /// <summary>Steam 确认: 票据有效, 就是这个 SteamID, 而且他拥有这个游戏。</summary>
    Verified,

    /// <summary>Steam 明确说不行: 伪造 / 冒充 / 没有游戏 / 被封。</summary>
    Rejected,

    /// <summary>这次没核验成 (Steam 连不上、超时、票据还没登记好…), 客户端过会儿重试就行。</summary>
    Unavailable,
}

/// <param name="SteamId">核验通过时 = 票据的主人。</param>
/// <param name="OwnerSteamId">游戏的实际拥有者 —— Steam 家庭共享时和 SteamId 不同。</param>
/// <param name="Detail">Steam 给的原始结果, 写日志用。</param>
public sealed record SteamVerdict(SteamAuthOutcome Outcome, ulong SteamId, ulong OwnerSteamId, string Detail)
{
    public static SteamVerdict Verified(ulong steamId, ulong owner, string detail = "OK") =>
        new(SteamAuthOutcome.Verified, steamId, owner, detail);

    public static SteamVerdict Rejected(ulong steamId, string detail) =>
        new(SteamAuthOutcome.Rejected, steamId, 0, detail);

    public static SteamVerdict Unavailable(ulong steamId, string detail) =>
        new(SteamAuthOutcome.Unavailable, steamId, 0, detail);
}

/// <summary>
/// 把玩家的 Steam 会话票据交给 Steam 核验。
///
/// ## 为什么是这个, 不是原版那个
///
/// 原版客户端交的是**加密应用票据** (RequestEncryptedAppTicket), 服务端用 Stunlock 的
/// 应用密钥解开 —— 那把钥匙我们没有。会话票据 (GetAuthSessionTicket) 是 Steam 给
/// 玩家自建专用服务器准备的另一套官方身份验证, 服务端只要以匿名游戏服务器登录 Steam
/// 就能验, 不需要任何发行方密钥。效果和原版一样: Steam 替服务器担保"他就是他"。
///
/// 2026-09-11 实测 (tools\SteamAuthProbe): Steam 仍然替已下架的 222900 核验,
/// 真票据 OK, 篡改的和冒充别人 SteamID 的都当场被拒。
/// </summary>
public interface ISteamTicketVerifier
{
    /// <summary>
    /// 核验。会阻塞到 Steam 回话或超时 (一般几百毫秒)。
    /// </summary>
    /// <param name="claimedSteamId">客户端自称的 SteamID —— Steam 会核对票据是不是这个人的。</param>
    SteamVerdict Verify(byte[] ticket, ulong claimedSteamId, TimeSpan timeout);

    /// <summary>
    /// 异步核验, 等待期间不占线程。默认实现直接包同步的 <see cref="Verify"/> (假核验器、信任模式都是立刻出结果);
    /// 真正要等 Steam 的 <c>SteamTicketVerifier</c> 自己实现。
    /// </summary>
    Task<SteamVerdict> VerifyAsync(byte[] ticket, ulong claimedSteamId, TimeSpan timeout) =>
        Task.FromResult(Verify(ticket, claimedSteamId, timeout));
}

/// <summary>谁都不认 —— 没接 Steam 时的默认 (单测里构造 LoginHandlers 不传核验器就是它)。</summary>
public sealed class RejectAllVerifier : ISteamTicketVerifier
{
    public SteamVerdict Verify(byte[] ticket, ulong claimedSteamId, TimeSpan timeout) =>
        SteamVerdict.Rejected(claimedSteamId, "没有接 Steam 核验");
}

/// <summary>
/// 自称是谁就是谁, 不找 Steam 核验 —— 只给断网的本机开发用 (DIE_STEAM_AUTH=trust)。
/// 谁都能冒充任何人, 绝不能在公网上开。
/// </summary>
public sealed class TrustClaimVerifier : ISteamTicketVerifier
{
    public SteamVerdict Verify(byte[] ticket, ulong claimedSteamId, TimeSpan timeout) =>
        SteamVerdict.Verified(claimedSteamId, claimedSteamId, "未核验 (DIE_STEAM_AUTH=trust)");
}
