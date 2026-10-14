using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;

namespace DIE.Server.Game;

/// <summary>
/// 观战 —— 谁能看谁。
///
/// ## 服务端在这件事上到底管什么
///
/// 可见性的中心是 <c>Client.LookAtCharacter?.Position ?? Client.ObserverPosition</c>
/// (扩展方法 <c>_0023hYb._0023fYb</c>, -z/-hYb.cs:43)。原厂所有带距离判据的
/// <c>ShouldBeSynchronized</c> 覆盖读的都是它 —— 也就是说**服务端把这个点设在哪, 客户端就只能看见哪**。
///
/// 所以观战不是"客户端自己把镜头挪过去"就完事: 镜头挪了而服务端没跟着挪, 那边是一片空地
/// (实体根本没下发)。服务端要做的就是把 <c>LookAtCharacter</c> 跟着玩家的观战选择走。
///
/// ## 入场那一半早就做好了
///
/// 握手包里带 <c>isObserver</c> (<c>LidgrenHost</c> 里那段), 观众不占名单、不生成角色、
/// <c>client.IsObserver</c> 当场置对 —— 而 <c>IsObserver</c> 决定控制器数据的**线格式**
/// (观察者上报镜头坐标, 玩家上报输入, Client.cs:334/348), 所以这一位必须双端一致, 不能乱改。
/// 观察者的 <c>ObserverPosition</c> 由原厂共享代码每帧从控制器数据里读出来, 自由镜头本来就能飞。
///
/// 缺的是两条**局内**消息 (2026-09-19 补):
///
///   DeadSpectator   死了之后切换观战目标 —— 每个玩家都会用到 (UnityClient.cs:1521/2536,
///                   客户端在 teamMembers 里轮换找下一个活人)
///   SetObserveMode  观察者在"跟随角色"和"自由镜头"之间切 (只有命令行 +observer
///                   启动的客户端会发, UnityClient.cs:1785/2088/2973)
///
/// ## 两条安全线
///
///   1. 普通玩家只能观战**同队的活人**, 而且**自己得先死**。
///      拾荒是 4v4v4 —— 能观战敌方等于知道敌人在哪, 那是透视。客户端自己的轮换逻辑
///      也只在 <c>teamMembers</c> 里挑, 所以"同队"就是原厂语义。
///   2. <c>SetObserveMode</c> 只对**握手时就是观众**的连接生效。
///      否则任何玩家发一条就能变成自由镜头飞遍全图 —— 同样是透视, 而且更彻底。
///      入场身份由宿主记着 (<c>LidgrenHost._observerSlots</c>), 不看会被改写的 <c>IsObserver</c>。
/// </summary>
public static class SpectatorPolicy
{
    /// <summary>一次观战请求的结论。</summary>
    public enum Result
    {
        /// <summary>看过去了。</summary>
        Focused,

        /// <summary>没这个玩家 (下标越界 / 不在场上)。</summary>
        NoSuchPlayer,

        /// <summary>请求者还活着, 不给观战 —— 活人只能看自己。</summary>
        StillAlive,

        /// <summary>不是同队的。</summary>
        NotOnYourTeam,

        /// <summary>目标也死了。</summary>
        TargetIsDead,
    }

    /// <summary>
    /// 把 <paramref name="client"/> 的视野中心挪到 <paramref name="playerIndex"/> 这个玩家身上。
    /// </summary>
    /// <param name="isObserver">入场时就是观众吗 —— 观众不受"自己得先死"和"同队"两条限制。</param>
    public static Result TryFocus(GameManager simulation, Client client, int playerIndex, bool isObserver,
                                  out Player target)
    {
        target = null;
        if (simulation == null || client == null)
        {
            return Result.NoSuchPlayer;
        }

        target = FindActivePlayer(simulation, playerIndex);
        if (target == null)
        {
            return Result.NoSuchPlayer;
        }

        if (!isObserver)
        {
            Player self = client.LocalPlayer;
            if (self == null || !self.IsDead)
            {
                return Result.StillAlive;
            }

            if (target.TeamId != self.TeamId)
            {
                return Result.NotOnYourTeam;
            }

            if (target.IsDead)
            {
                return Result.TargetIsDead;
            }
        }

        client.SetLookAtCharacter(target);
        return Result.Focused;
    }

    /// <summary>
    /// 观察者切"自由镜头 / 跟随角色"。
    ///
    /// 原厂枚举 <c>_0023u6b</c> 只有两个值 (-z/-u6b.cs), 按客户端三处调用点反推:
    /// <c>_0023t6b</c>(1) = 开观察 (命令行 +observer 连上时发, 观察者点左键也发),
    /// <c>_0023s6b</c>(0) = 关观察 (观察者按 RightShift, 发之前还把 ObserverPosition
    /// 设成当前看的角色位置, 好让镜头平滑接上)。
    /// </summary>
    /// <returns>true = 这次请求被接受。</returns>
    public static bool TrySetObserveMode(Client client, byte mode, bool isObserver)
    {
        // 入场不是观众的一律不给 —— 见类说明的第 2 条安全线
        if (client == null || !isObserver)
        {
            return false;
        }

        bool observing = mode != 0;

        if (observing)
        {
            // 自由镜头: 松开对角色的跟随, 视野中心退回 ObserverPosition
            // (那个值由观察者自己每帧上报, Client.cs:352)。
            client.SetLookAtCharacter(null);
        }

        client.IsObserver = observing;
        return true;
    }

    /// <summary>按 <c>IndexPlayer</c> 找一个在场的玩家。</summary>
    private static Player FindActivePlayer(GameManager simulation, int playerIndex)
    {
        IReadOnlyList<Player> players = simulation.ActivePlayerList;
        if (players == null)
        {
            return null;
        }

        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player != null && player.IsActive && player.IndexPlayer == playerIndex)
            {
                return player;
            }
        }

        return null;
    }
}
