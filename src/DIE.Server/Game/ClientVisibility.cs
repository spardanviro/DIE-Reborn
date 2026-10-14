using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using _0023rye;
using StunGameNetwork;

namespace DIE.Server.Game;

/// <summary>
/// 维护 <c>NetworkBase.ActiveClientsList</c>, 以及客户端加入/离开时的对象可见性。
///
/// ## 为什么这段必须我们自己写
///
/// 整个 <c>NetworkBase</c> 里, <c>ActiveClientsList</c> 只在构造时被 new 出来
/// (:461) 和被遍历 (:750) —— **没有任何一处往里加东西**。维护它是服务端的活,
/// 属于随服务端一起丢失的那部分代码。
///
/// 不维护的后果是完全静默的。<c>GameObjectBase.Spawn()</c> 用
///
///     foreach (Client c in NetworkManager.get_ActiveClientsList())
///         _SyncHelper._00235Ce(c.Index, ShouldBeSynchronized(c));   // :1163
///
/// 来置每客户端同步位。列表是空的, 循环体一次都不执行, 位永远是 0,
/// <c>WorldStateWriter</c> 里的 <c>_0023pDe(viewer.Index)</c> 就永远为假 ——
/// 实体明明活着、在服务端正常模拟, 却一个字节都不下发。没有任何错误,
/// 因为位流本身是自洽的。
///
/// ## 为什么单独成类
///
/// 这段逻辑的正确性只能靠"实体到底有没有被下发"来验证, 而那需要把整个模拟层
/// 搭起来。放在 <c>LidgrenHost</c> 里就得连带把 UDP 监听也起起来才能测。
/// </summary>
public static class ClientVisibility
{
    /// <summary>
    /// 把客户端登记为活动客户端, 并补算它对**现有**对象的可见性。
    ///
    /// 必须在给该客户端 <c>Spawn()</c> 任何实体**之前**调用 —— Spawn 自己那个
    /// 循环靠这个列表工作。
    ///
    /// 补算是必要的: <c>GameObjectBase</c> 只在 Spawn (:1163) 和 Destroy (:1222)
    /// 时算可见性, 没有逐帧重算。所以在这个客户端连上**之前**就已经 Spawn 的东西
    /// (地图上的实体、先进来的其他玩家) 不会自己变可见。
    ///
    /// <c>AbilityBar</c> 不用管, 它在 <c>Update</c> 里逐帧重算 (AbilityBar.cs:351),
    /// 列表一有内容就会自愈。
    /// </summary>
    /// <returns>补算了多少个已有对象。首个客户端进场时通常是 0。</returns>
    public static int Activate(GameServer network, GameManager simulation, Client client)
    {
        List<IClient> active = network.ActiveClientsList;
        if (active.Contains(client))
        {
            return 0;
        }

        active.Add(client);

        // ConnectedClientsList 是**另一个**同样没人维护的列表 (NetworkBase.cs:462
        // 只 new 了它)。GameManager 销毁对象时靠它给客户端发 GameObjectDestroyed
        // (:1625 的循环) —— 列表为空, 循环就一次都不执行, 客户端永远不知道有东西
        // 死了, 表现是僵尸打完不播死亡动画、过一会儿模型凭空消失。
        //
        // 语义上 connected (有连接) 和 active (已进场) 可能不同, 但在我们目前的
        // 流程里两者同时发生 —— 都在审批通过时。等做到观战/重连再区分。
        List<IClient> connected = network.ConnectedClientsList;
        if (!connected.Contains(client))
        {
            connected.Add(client);
        }

        int refreshed = 0;
        foreach (GameObjectBase obj in simulation.ActiveGameObjectList)
        {
            _00236Ce helper = network.GetSyncHelper(obj.SyncHelperIndex);
            if (helper == null)
            {
                continue;
            }

            helper._00235Ce(client.Index, obj.ShouldBeSynchronized(client));
            refreshed++;
        }

        return refreshed;
    }

    /// <summary>
    /// 把玩家实体登记进 <c>ConnectedPlayers</c>。
    ///
    /// **必须在实体生成之后调**, 不能并进 <see cref="Activate"/> —— 那个在
    /// 入场审批时就跑了, 那时 <c>LocalPlayer</c> 还是 null。
    ///
    /// ## 这是第三个没人维护的列表
    ///
    /// <c>GameManager.cs:1057</c> 只 new 了它。客户端那侧由
    /// <c>Client.OnGameManagerCreated</c> (:567) 填, 但那条路服务端不走 ——
    /// 我们是直接给 <c>LocalPlayer</c> 赋值的。
    ///
    /// 它决定**谁能拿到击杀经验**: 经验分发 (-bd/-Zt.cs:111) 遍历的就是它。
    /// 列表为空 -> 循环一次都不执行 -> 打死怪一点经验都不给, 且零报错。
    ///
    /// 和前两个列表 (<c>ActiveClientsList</c> / <c>ConnectedClientsList</c>)
    /// 是同一个形状: 一个只被 new 过的容器, 空着的时候整条链路安静地失效。
    /// </summary>
    /// <returns>true = 这次真的加进去了 (false 表示已经在里面)。</returns>
    public static bool RegisterPlayer(GameManager simulation, Client client)
    {
        if (client?.LocalPlayer is not Player player)
        {
            return false;
        }

        if (simulation.ConnectedPlayers.Contains(player))
        {
            return false;
        }

        simulation.ConnectedPlayers.Add(player);
        return true;
    }

    /// <summary>
    /// 客户端离开时从活动列表摘除。
    ///
    /// 不摘的话 <c>Spawn()</c> 会继续给一个已经断开的槽位算可见性, 而该槽位
    /// 之后被新玩家复用时会带着上一局的位状态。
    /// </summary>
    public static void Deactivate(GameServer network, int clientIndex)
    {
        RemoveByIndex(network.ActiveClientsList, clientIndex);
        RemoveByIndex(network.ConnectedClientsList, clientIndex);
    }

    /// <summary>
    /// 客户端离开时把它的玩家实体从 <c>ConnectedPlayers</c> 摘掉。
    ///
    /// 不摘的话经验会继续分给一个已经断线的玩家, 而且因为分发是**均摊**的
    /// (-bd/-Zt.cs:130 `num2 /= num`), 在场玩家实际拿到的经验会凭空变少。
    /// </summary>
    public static void UnregisterPlayer(GameManager simulation, Client client)
    {
        if (client?.LocalPlayer is Player player)
        {
            simulation.ConnectedPlayers.Remove(player);
        }
    }

    private static void RemoveByIndex(List<IClient> list, int clientIndex)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] != null && list[i].Index == clientIndex)
            {
                list.RemoveAt(i);
            }
        }
    }
}
