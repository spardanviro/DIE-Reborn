using System.Collections.Concurrent;

namespace DIE.Server.Net;

/// <summary>
/// 哪些账号的数据变了、但还没告诉大厅。
///
/// ## 为什么需要它: 大厅不是长连接
///
/// 玩家实测"账号经验还是不实时更新"。上一版的修法是局末往大厅连接上推一份
/// 新的 <c>LoginDataMessage</c>, 日志里却是:
///
///     04:42:49  局末结算已下发 1 份 (账号经验 593, ...)
///     04:42:49  已通知 **0** 条大厅连接刷新账号数据
///
/// 因为那一刻**一条连接都没有**。翻连接日志才看清大厅的行为:
///
///     04:41:53 conn#29 已连接 (在线 1)
///     04:42:14 conn#29 已断开 (在线 0)     &lt;- 只活了 21 秒
///     04:42:49 局末                         &lt;- 这时没人连着
///     04:43:03 conn#30 已连接 (在线 1)
///     04:43:17 conn#30 已断开 (在线 0)     &lt;- 又只活 14 秒
///
/// 大厅是**轮询**的: 连上来、拉一次、断开, 隔几十秒再来一轮。所以"推"这个
/// 动作本身就没有落点 —— 推的时候对面不在。
///
/// ## 改成"记账 + 下次补推"
///
/// 局末发现没人连着就把这个账号记下来。大厅下一轮连上来 (走
/// <c>AuthRequest</c> 重连) 时把那份新数据补推过去, 然后销账。
///
/// 只推**真的变过**的账号, 而不是每次重连都推一遍 —— 后者等于让大厅每隔
/// 几十秒重跑一次登录流程 (那个处理器还会连带重发 GetRewardSetup /
/// GetVanityURL 两个请求)。
/// </summary>
public sealed class AccountRefreshQueue
{
    private readonly ConcurrentDictionary<ulong, byte> _pending = new();

    /// <summary>记一笔: 这个账号的数据变了。</summary>
    public void Mark(ulong userId)
    {
        if (userId != 0)
        {
            _pending[userId] = 0;
        }
    }

    /// <summary>
    /// 这个账号欠不欠一次刷新 —— 欠的话顺手销账。
    ///
    /// 取走即销账 (而不是"查了再单独删") 是为了避免两条连接同时进来时
    /// 各推一次。
    /// </summary>
    public bool TakeIfPending(ulong userId) => _pending.TryRemove(userId, out _);

    /// <summary>还欠着几笔。诊断用。</summary>
    public int PendingCount => _pending.Count;
}
