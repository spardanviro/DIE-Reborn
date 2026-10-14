using System;
using System.Collections.Generic;
using System.Linq;
using DIE.Server.Data;

namespace DIE.Server.Match;

/// <summary>一局里的一名玩家: 账号、排队时的选择、账号等级 (决定技能栏)。</summary>
/// <param name="UserId">
/// 账号 id。**可能是 0** —— 组队时队长的客户端靠 Steam 大厅里的
/// <c>LobbyIDAnnounceMessage</c> 才知道队员的 UserID, 没收到就填 0
/// (<c>SteamLobbyData.cs:448</c> 只打一行错误日志)。这种位子等进场时凭会话票认人。
/// </param>
/// <param name="Team">
/// 拾荒 (4v4v4) 里分到第几队 (1..3, 即 <c>TeamId</c> 的值); 0 = 没分 (别的模式都是 0, 进场按模式定队)。
/// 同一小队的人一定同一队, 见 <see cref="ScavengerTeams"/>。
/// </param>
public sealed record MatchMember(ulong UserId, PlayerChoice? Choice, int AccountLevel, byte Team = 0);

/// <summary>名单绑定的结果。<see cref="Member"/> 为 null 表示拒绝入场。</summary>
public readonly record struct RosterBinding(MatchMember Member, string Rule);

/// <summary>
/// 一局的名单, 以及"谁坐进了哪个客户端槽位"。
///
/// ## 为什么要有这一层
///
/// 以前战斗服是"按局绑定"的: 端点是匹配服为某个账号单独起的, 连进来的就是那个人。
/// 一局一人时成立, 组队排队之后就不成立了 —— 同一个端点上会进来好几个人。
///
/// ## 凭什么认人
///
/// 战斗客户端的握手里带着 <c>+authticket</c> 传进来的那张票 (<c>AuthenticationData</c>),
/// 它就是**我们登录时签发的会话票** (<c>GameStartScript.cs:299</c> 取的是
/// <c>GameLinkClient.AuthTicket</c>, 来自 LoginDataMessage)。查 <c>sessions</c> 表就知道是谁 ——
/// 这是服务端自己签的票, 不需要 Steam 的密钥。
///
/// ## 绑定规则 (按顺序)
///
/// 1. 票查得出账号, 且名单上有他 -&gt; 就是他。已经坐在别的槽位上也照样给
///    (崩溃重进时旧连接还没超时; 旧槽位断开时 <see cref="Release"/> 只放自己那一格)。
/// 2. 名单上有 UserId = 0 的空位 (队长没拿到那个队员的 id) -&gt; 给第一个。
/// 3. 票查不出账号, 而这是一人局且那一位还空着 -&gt; 就是他。
///    保留旧的"按局绑定": 单测里的假票、以及票过期后的边缘情况都走这条。
/// 4. 其余拒绝 —— 票认得出来却不在名单上, 那是别人的局。
///
/// **<see cref="RequireTicket"/> 打开时 (正式服都打开) 没票一律拒绝**: 第 2 条要票认得出账号才给, 第 3 条不走。
/// 2026-09-17 审校: 战斗服端口段可以扫, 以前一人局主人掉线后, 任何不带票的连接都能顶进他的位子控制他的角色。
///
/// 只在战斗服的泵线程上用, 但加了锁 —— 匹配服那边以后要读名单 (重连查询)。
/// </summary>
public sealed class MatchRoster
{
    private readonly object _lock = new();

    /// <summary>
    /// 槽位 -&gt; 名单下标。**按下标记账, 不按 MatchMember 比较** —— record 是值相等,
    /// 两个 id 未知、选择相同的队员会被当成同一个人。
    /// </summary>
    private readonly Dictionary<int, int> _bySlot = new();

    /// <summary>匿名位 (UserId = 0) 进场时凭票认出来的真实账号, 按名单下标存。</summary>
    private readonly Dictionary<int, ulong> _claimedIds = new();

    public MatchRoster(IReadOnlyList<MatchMember> members)
    {
        if (members == null || members.Count == 0)
        {
            throw new ArgumentException("名单不能为空", nameof(members));
        }

        _members = new List<MatchMember>(members);
    }

    /// <summary>一人局的名单 —— Solo 开局、以及旧的构造方式。</summary>
    public static MatchRoster Solo(ulong userId, PlayerChoice? choice, int accountLevel) =>
        new MatchRoster(new[] { new MatchMember(userId, choice, accountLevel) });

    private readonly List<MatchMember> _members;

    /// <summary>
    /// 名单快照。拾荒 / 十字路口开局前还能补人 (<see cref="Add"/>), 所以给的是拷贝 ——
    /// 调用方遍历时名单被另一个线程加长也不会炸。
    /// </summary>
    public IReadOnlyList<MatchMember> Members
    {
        get
        {
            lock (_lock)
            {
                return _members.ToArray();
            }
        }
    }

    /// <summary>名单上几个人 (不拷贝)。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _members.Count;
            }
        }
    }

    /// <summary>
    /// 开局前补进来的人 (匹配服把后来排队的队伍补进一局还在等人的战斗服,
    /// 见 <c>LidgrenHost.TryAddLateJoiners</c>)。只追加, 已有的下标不变 ——
    /// 槽位是按下标记账的。
    /// </summary>
    public void Add(IEnumerable<MatchMember> members)
    {
        lock (_lock)
        {
            _members.AddRange(members);
        }
    }

    /// <summary>
    /// 进场必须带查得出账号的会话票。宿主接了账号库 (<c>LidgrenHost.ResolveUserByTicket</c>) 时打开;
    /// 单测和假客户端没有账号库, 保持关着走旧规则。
    /// </summary>
    public bool RequireTicket { get; set; }

    /// <summary>名单上第一位 —— 排队时的队长 / Solo 的本人。日志和兜底用。</summary>
    public MatchMember Owner
    {
        get
        {
            lock (_lock)
            {
                return _members[0];
            }
        }
    }

    /// <summary>已经坐进槽位的人数 (同一人占两格按两格算)。</summary>
    public int BoundSlots
    {
        get
        {
            lock (_lock)
            {
                return _bySlot.Count;
            }
        }
    }

    /// <summary>这个槽位上是谁。没绑过返回 null。</summary>
    public MatchMember MemberAt(int slot)
    {
        lock (_lock)
        {
            return _bySlot.TryGetValue(slot, out int index) ? Resolved(index) : null;
        }
    }

    /// <summary>
    /// 给刚进场的连接找名单上的位子, 见类说明的四条规则。
    /// </summary>
    /// <param name="slot">这个连接分到的客户端槽位。</param>
    /// <param name="ticketUserId">会话票查出来的账号; 查不出为 null。</param>
    public RosterBinding Bind(int slot, ulong? ticketUserId)
    {
        lock (_lock)
        {
            ulong known = ticketUserId ?? 0;

            if (RequireTicket && known == 0)
            {
                return new RosterBinding(null, "没有查得出账号的会话票");
            }

            if (known != 0)
            {
                int listed = IndexOf(i => Resolved(i).UserId == known);
                if (listed >= 0)
                {
                    bool already = _bySlot.ContainsValue(listed);
                    _bySlot[slot] = listed;
                    return new RosterBinding(Resolved(listed), already ? "会话票 (顶替自己的旧连接)" : "会话票");
                }
            }

            int anonymous = IndexOf(i => Resolved(i).UserId == 0 && !_bySlot.ContainsValue(i));
            if (anonymous >= 0)
            {
                if (known != 0)
                {
                    _claimedIds[anonymous] = known;
                }

                _bySlot[slot] = anonymous;
                return new RosterBinding(Resolved(anonymous), "名单上 id 未知的队员位");
            }

            if (ticketUserId == null && _members.Count == 1 && !_bySlot.ContainsValue(0))
            {
                _bySlot[slot] = 0;
                return new RosterBinding(Resolved(0), "一人局按局绑定");
            }

            return new RosterBinding(null, ticketUserId == null
                ? "会话票查不出账号, 且名单已满"
                : $"账号 {ticketUserId} 不在这一局的名单上");
        }
    }

    private MatchMember Resolved(int index)
    {
        MatchMember member = _members[index];
        return _claimedIds.TryGetValue(index, out ulong id) ? member with { UserId = id } : member;
    }

    private int IndexOf(Func<int, bool> match)
    {
        for (int i = 0; i < _members.Count; i++)
        {
            if (match(i))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>这个槽位的连接断了, 位子空出来 (同一人可以再凭票坐回来)。</summary>
    public void Release(int slot)
    {
        lock (_lock)
        {
            _bySlot.Remove(slot);
        }
    }
}
