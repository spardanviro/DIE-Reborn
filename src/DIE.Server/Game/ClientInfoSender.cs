using System.Collections.Generic;
using ConductorGameLogic;
using StunGameNetwork;
using _0023rye;

namespace DIE.Server.Game;

/// <summary>
/// 把 <see cref="Client.ClientInfoData"/> 下发给客户端。
///
/// ## 这条路一直是断的
///
/// 可靠通道有两个分支, 我们只用了 <c>_0023YCe</c>(游戏消息), 从没发过
/// <c>_0023XCe</c>(ClientInfo 批)。
///
/// <c>ClientInfoData</c> 装着**装备**: 武器1/武器2/饰品1/饰品2、相机朝向、
/// 队伍、显示名。服务端一直在改它 —— 入场时设相机朝向, 教程发武器时
/// <c>EquipHaxWeaponNew</c> 写 <c>_0023lYb</c> 和脏标记 <c>_0023sYb</c> ——
/// 但**一次都没发出去过**。
///
/// ## 后果不是"图标没更新"
///
/// 客户端按它以为的装备决定发哪个技能, 服务端按真实装备校验。两边的技能栏
/// 对不上时 <c>AbilityBar.TryUseAbility</c> 找不到那个技能, 直接返回 false ——
/// **攻击静默失败**。
///
/// 真机表现: 教程发了船桨之后, 玩家"攻击模式还是空手", 而且打僵尸、打栅栏
/// 全都不掉血。服务端零错误, 因为从服务端看什么都没发生 —— 客户端发来的
/// 技能请求被 CanUseWithResult 挡在门外, 那是一条正常的拒绝路径。
///
/// ## 什么时候发
///
/// 装备变了就发。目前两处: 玩家进场、教程发武器。
/// 不做每帧广播 —— ClientInfo 里的东西不常变, 而它带着完整的物品结构,
/// 逐帧发是纯浪费。
/// </summary>
public static class ClientInfoSender
{
    /// <summary>
    /// 把 <paramref name="subjects"/> 的 ClientInfo 发给 <paramref name="recipient"/>。
    ///
    /// 载荷格式照抄接收方 <c>GameClient._0023KCe</c> (:269):
    ///
    ///     byte 数量
    ///     N × (byte 槽位号 + ClientInfoServerToClientSerialize)
    ///
    /// 数量是 <c>byte</c> —— 一局最多 16 个客户端, 装得下。
    /// </summary>
    /// <returns>实际发出去的条数, 0 表示什么都没发。</returns>
    public static int Send(GameServer network, IReadOnlyList<Client> subjects, Client recipient)
    {
        if (network == null || recipient == null || subjects == null || subjects.Count == 0)
        {
            return 0;
        }

        _00237ze buffer = network.CreateClientInfoMessage(256);
        if (buffer == null)
        {
            return 0;
        }

        buffer.Write((byte)subjects.Count);

        for (int i = 0; i < subjects.Count; i++)
        {
            Client subject = subjects[i];
            buffer.Write((byte)subject.Index);

            // 第二个参数在原厂的实现里**根本没被用到** ——
            // ClientInfoData.Serialize(buffer, localPlayer) 的方法体里没有
            // 它的引用 (ClientInfoData.cs:34)。传"这条是不是收件人自己"是
            // 语义上正确的值, 万一以后原厂逻辑被补回来也不会错。
            subject.ClientInfoServerToClientSerialize(buffer, ReferenceEquals(subject, recipient));
        }

        network.SendGameMessage(buffer, recipient);
        return subjects.Count;
    }

    /// <summary>
    /// 发 <c>GameMessageType.ClientInfo</c> **游戏消息** —— 这才是让客户端
    /// 真正装备武器的那条路。
    ///
    /// ## 和 <see cref="Send"/> 的区别: 两条完全不同的通道
    ///
    /// 我先前只实现了 <see cref="Send"/>, 走 <c>_0023XCe</c>(ClientInfo 批) 通道,
    /// 接收方是 <c>GameClient._0023KCe</c> (:269)。那条**只更新
    /// <c>ClientInfoData</c> 字段**, 不碰武器和技能栏。
    ///
    /// 装备武器要走的是 <c>ConductorGameMessageType.GameMessage</c> 下的
    /// <c>GameMessageType.ClientInfo</c>, 接收方 <c>GameManagerClient.cs:569-587</c>:
    ///
    ///     case GameMessageType.ClientInfo:
    ///         client2.ClientInfoServerToClientDeserialize(buffer);
    ///         Player localPlayer = client2.GetLocalPlayer();
    ///         localPlayer.Weapon1 = GetWeapon(client2.ClientInfoData._0023lYb, localPlayer);
    ///         localPlayer.Weapon2 = GetWeapon(client2.ClientInfoData._0023mYb, localPlayer);
    ///         localPlayer.Gadget1 = GetTrinket(...);
    ///         localPlayer.Gadget2 = GetTrinket(...);
    ///         localPlayer.EquipWeaponsAndAbilities(forceSetSlot: true);
    ///
    /// 两个关键点:
    ///
    ///   1. **它构造武器物品** (`GetWeapon(Unique, player)`) —— 客户端的
    ///      `_Weapons[slot]` 只能从这里来 (唯一赋值点是
    ///      `Character.SetWeaponAndEquipGlobals`, :1623)。
    ///   2. **`forceSetSlot: true`** 绕过 `Player.cs:752` 那个教程判断
    ///      (`!forceSetSlot &amp;&amp; IsTutorialGameMode &amp;&amp; !ReceivedTutorialPaddle`),
    ///      于是 `_EquippedWeaponSlot = 0` 并 `EquipWeapon(0)` ->
    ///      `SetAbility(HeavyPrimary, 0)`。
    ///
    /// **这条路径没有教程地图排除。** 我先前引用的 `Client.cs:572`
    /// (`MapIndex != TutorialMapIndex`) 在 `Client.OnGameManagerCreated` 里,
    /// 那是**初始化**路径 —— 开局时玩家本来没武器, 跳过是对的。
    /// 运行时这条消息的处理器另算, 而且顺序刚好: 先 `OnGameManagerCreated`
    /// (教程跳过, 无害), 再 :581-585 重新设武器并强制装备。
    ///
    /// ## 载荷格式
    ///
    ///     byte ConductorGameMessageType.GameMessage
    ///     byte GameMessageType.ClientInfo
    ///     byte 客户端槽位号
    ///     ClientInfoServerToClientSerialize(buffer, isLocal)
    ///
    /// 一条消息只带**一个**客户端 —— 接收方读一个 byte 就直接反序列化,
    /// 没有数量前缀 (和 <see cref="Send"/> 的批量格式不同, 这点混了就解析错位)。
    /// </summary>
    /// <returns>发出去的消息条数。</returns>
    public static int SendEquipUpdate(GameServer network, Client subject, Client recipient)
    {
        if (network == null || subject == null || recipient == null)
        {
            return 0;
        }

        // 客户端处理器里 `client2.GetLocalPlayer()` 之后**直接解引用**
        // (GameManagerClient.cs:581 `localPlayer.Weapon1 = ...`) —— 玩家还没生成时
        // 那边会 NRE, 而客户端的异常我们看不见。宁可不发。
        //
        // 服务端侧的判据是 Client.LocalPlayer: ClientInfoServerToClientSerialize
        // (:504) 写的正是 `LocalPlayer != null`, 为 false 时不带 PlayerIndex,
        // 客户端那边 GetLocalPlayer() 就取不到东西。
        if (subject.LocalPlayer == null)
        {
            return 0;
        }

        _00237ze buffer = network.CreateGameMessage(256);
        if (buffer == null)
        {
            return 0;
        }

        buffer.Write((byte)ConductorGameMessageType.GameMessage);
        buffer.Write((byte)GameMessageType.ClientInfo);
        buffer.Write((byte)subject.Index);
        subject.ClientInfoServerToClientSerialize(buffer, ReferenceEquals(subject, recipient));

        network.SendGameMessage(buffer, recipient);
        return 1;
    }

    /// <summary>
    /// 把每个活动客户端的装备更新发给每一个活动客户端。
    ///
    /// 单人局就是自己发给自己 —— 而那恰恰是必须的: 本地玩家的武器物品只能
    /// 从这条消息构造出来。
    /// </summary>
    public static int BroadcastEquipUpdate(GameServer network)
    {
        if (network?.ActiveClientsList == null)
        {
            return 0;
        }

        List<Client> clients = new List<Client>();
        foreach (IClient client in network.ActiveClientsList)
        {
            if (client is Client concrete)
            {
                clients.Add(concrete);
            }
        }

        int sent = 0;
        for (int i = 0; i < clients.Count; i++)
        {
            for (int j = 0; j < clients.Count; j++)
            {
                sent += SendEquipUpdate(network, clients[i], clients[j]);
            }
        }

        return sent;
    }

    /// <summary>
    /// 把所有活动客户端的 ClientInfo 发给每一个活动客户端。
    ///
    /// 单人局就是自己发给自己 —— 听起来多余, 但客户端的装备状态确实只能
    /// 从这条消息来。
    /// </summary>
    /// <returns>发出去的消息条数 (每个收件人一条)。</returns>
    public static int Broadcast(GameServer network)
    {
        if (network?.ActiveClientsList == null)
        {
            return 0;
        }

        List<Client> clients = new List<Client>();
        foreach (IClient client in network.ActiveClientsList)
        {
            if (client is Client concrete)
            {
                clients.Add(concrete);
            }
        }

        if (clients.Count == 0)
        {
            return 0;
        }

        int sent = 0;
        for (int i = 0; i < clients.Count; i++)
        {
            if (Send(network, clients, clients[i]) > 0)
            {
                sent++;
            }
        }

        return sent;
    }
}
