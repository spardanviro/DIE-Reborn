using System;
using System.Collections.Generic;
using ConductorCrafting;
using ConductorGameLogic.GameModes;
using DIE.Server.Data;
using DIE.Server.Game;

namespace DIE.Server.Match;

/// <summary>
/// 战斗服和账号库之间的几条回调。
///
/// 原则是**战斗服自己不碰账号库** —— 进场前要的 (账号等级、装备) 由建局的人查好放进名单,
/// 局中/局末要的 (认人、经验、奖励、新手任务、通知大厅刷新) 都走这里, 由建局的人接上。
/// 哪条没接就是不做, 不报错 (单测和 FakeClient 就是全都不接)。
/// </summary>
public sealed class MatchHostCallbacks
{
    /// <summary>凭战斗客户端握手里的会话票查账号 (<c>AccountStore.ResolveByAuthTicket</c>)。</summary>
    public Func<byte[], ulong?> ResolveUserByTicket { get; init; }

    /// <summary>任务完成时把某人本局的经验落库, 返回累计总量。</summary>
    public Func<ulong, uint, uint> AwardAccountXp { get; init; }

    /// <summary>局末算奖励前取某人账号侧的输入 (经验、背包里已有的蓝图等)。</summary>
    public Func<ulong, RewardAccountSnapshot> AccountSnapshot { get; init; }

    /// <summary>把原厂算出来的一份奖励落库, 返回给日志看的摘要。</summary>
    public Func<ulong, RewardCollectionData, IReadOnlyList<byte>, RewardLedger.GrantSummary> GrantRewards { get; init; }

    /// <summary>记下某人今天的拾荒每日首胜已经拿了。</summary>
    public Action<ulong> MarkScavengerFirstWin { get; init; }

    /// <summary>从某人背包里扣掉这一局用掉的消耗品 (局末, 或中途放弃时)。</summary>
    public Action<ulong, IReadOnlyList<(ushort Id, int Used)>> SpendConsumables { get; init; }

    /// <summary>这一局用的掉落表 (拼一张要读整份图鉴, 所以由外面拼好传进来)。</summary>
    public DropTable DropTable { get; init; }

    /// <summary>局末推进某人的新手任务。</summary>
    public Action<ulong, GameModeType> AdvanceStarterQuests { get; init; }

    /// <summary>
    /// 局末推进十字路口难度解锁 (账号, 难度档, 这个人收集的补给), 返回这局之后的进度。
    /// 不够格就返回当前值 —— 结算报文里那一格不能是 0, 客户端会照单全收。
    /// </summary>
    public Func<ulong, int, int, byte> AdvanceCrossroadProgress { get; init; }

    /// <summary>通知某人的大厅重新拉账号数据, 返回推给了几条连接。</summary>
    public Func<ulong, int> RefreshAccount { get; init; }
}
