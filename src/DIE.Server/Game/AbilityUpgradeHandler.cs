using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using StunGameNetwork;
using _0023rye;

namespace DIE.Server.Game;

/// <summary>
/// 技能加点 —— 处理 <c>GameMessageType.UpgradeAbility</c>, 回
/// <c>UpgradeAbilityDone</c>。
///
/// ## 教程里是**自动**加点的
///
/// 这一点很容易误判成"玩家该手动点但点不动"。客户端每帧跑
/// (GameManagerClient.cs:1611):
///
///     if (AutoLevelUp || GameMode.IsTutorialGameMode)
///         _0023mQl();     // 自动挑一个技能升级
///
/// <c>_0023mQl</c> (:1618) 的逻辑:
///
///     if (LocalPlayer.GetUnspentAbilityUpgrades() &gt; 0 &amp;&amp; CalculateNextAutoAbility(out btn))
///     {
///         int slot = _0023oQl(btn);
///         Ability a = LocalPlayer.AbilityBar.GetAbility(slot);
///         UpgradeAbility(slot, a.IsEnabled ? a.Level + 1 : 1);
///         _0023kQl = Time + 1f;                      // 1 秒节流
///     }
///
/// 所以教程里玩家什么都不用按 —— 但**服务端不回应的话它就永远卡在那里**,
/// 每秒重发一次同样的请求。
///
/// 节流那个时间戳由 <c>_0023nQl()</c> 清零 (GameManagerClient.cs:1634),
/// 而它正是收到 <c>UpgradeAbilityDone</c> 时调的 (:902)。也就是说
/// **回包不是可选的**: 不回, 客户端就得干等满 1 秒才敢发下一个点;
/// 回了, 它可以连着把攒下的点一次性花完。
///
/// ## 规则本身是完整的
///
/// <c>Character.TryUpgradeAbility</c> (Character.cs:3795) 全都在:
///
///     if (ability == null || ability.Level &gt;= 4) return;      // 4 级封顶
///     if (_0023Rz() &gt;= GetLevel()) return;                    // 点数不够
///     if (ability.IsEnabled) { 升级 } else { 解锁 }
///
/// 其中 <c>_0023Rz()</c> 是"已花点数" = 所有已启用技能的等级之和,
/// 可用点数 = <c>GetLevel() - _0023Rz()</c> (即 <c>GetUnspentAbilityUpgrades</c>)。
///
/// **这意味着 0 级 = 一个点都加不了** —— <c>0 &gt;= 0</c> 直接 return。
/// 教程模式下 <c>Entity.GetLevel</c> 返回 <c>i</c> 而不是 <c>i + 1</c>
/// (Entity.cs:1161), 所以教程里必须先打怪攒到 80 经验才有第一个点。
///
/// 我们一条规则都不重写, 只负责收消息、转调、回包。
/// </summary>
public static class AbilityUpgradeHandler
{
    public enum Result
    {
        /// <summary>加点成功 (解锁或升级)。</summary>
        Upgraded,

        /// <summary>这个客户端没有玩家实体。</summary>
        NoPlayer,

        /// <summary>
        /// 请求被原厂规则拒了 —— 点数不够、技能已满级、或者槽位是空的。
        ///
        /// 这**不是错误**: 教程的自动加点每秒重试一次, 点数不够时会一直撞在这里。
        /// </summary>
        Rejected,
    }

    /// <summary>
    /// 处理一条加点请求。
    ///
    /// 载荷格式照抄发送方 <c>GameManagerClient.UpgradeAbility</c> (:1416):
    /// 两个 <c>int</c>, 先 slotId 再 expectedNewLevel。
    /// <c>expectedNewLevel</c> 为 -1 表示"不校验目标等级"。
    /// </summary>
    public static Result Handle(GameServer network, Client client, INetBuffer netBuffer,
                                out int slotId, out int level)
    {
        // 先读干净缓冲, 再判有没有玩家 —— 否则这条消息的两个 int 会留在流里,
        // 让后面的消息全部解析错位。位流没有分隔符, 少读一个字节就全乱了。
        //
        // _0023Hae() 是 32 位读 int, 和发送方的 Write(int) 配对
        // (LidgrenNetBuffer.cs:203 那侧带 NextType.Int 校验; -iae/-7d.cs:179 是
        // 实际的 32 位读)。不要用带位数的重载 _0023Hae(int) —— 那是压缩整数,
        // 位宽对不上就会静默读歪。
        slotId = netBuffer._0023Hae();
        int expectedNewLevel = netBuffer._0023Hae();
        level = -1;

        if (client?.LocalPlayer is not Player player)
        {
            return Result.NoPlayer;
        }

        int before = SpentPoints(player);

        player.TryUpgradeAbility(slotId, expectedNewLevel);

        int after = SpentPoints(player);
        if (after == before)
        {
            return Result.Rejected;
        }

        Ability ability = player.AbilityBar.GetAbility(slotId);
        level = ability?.Level ?? -1;

        SendDone(network, client);
        return Result.Upgraded;
    }

    /// <summary>
    /// 已花掉的点数 = 所有已启用技能的等级之和。
    ///
    /// 和 <c>Character._0023Rz()</c> (Character.cs:3823) 同一个算法 —— 那个是
    /// private, 这里重算一遍是为了判断"这次请求到底生效了没有"。
    /// <c>TryUpgradeAbility</c> 是 void, 不告诉调用方成功与否。
    /// </summary>
    private static int SpentPoints(Player player)
    {
        int spent = 0;

        foreach (Ability ability in player.AbilityBar.AbilitySlots)
        {
            if (ability != null && ability.IsAbilityBarAbility && ability.IsEnabled)
            {
                spent += ability.Level;
            }
        }

        return spent;
    }

    /// <summary>
    /// 回 <c>UpgradeAbilityDone</c> —— 空消息, 只有两字节的头。
    ///
    /// 客户端收到后调 <c>_0023nQl()</c> 把自动加点的节流时间戳清零
    /// (GameManagerClient.cs:902 -&gt; :1634), 于是能立刻花下一个点。
    ///
    /// 通道层的类型字节由 <see cref="GameServer.CreateGameMessage"/> 写,
    /// 这里不要重复写 —— 那正是死亡动画卡了两轮的原因。
    /// </summary>
    private static void SendDone(GameServer network, Client client)
    {
        _00237ze buffer = network?.CreateGameMessage(8);
        if (buffer == null)
        {
            return;
        }

        buffer.Write((byte)ConductorGameMessageType.GameMessage);
        buffer.Write((byte)GameMessageType.UpgradeAbilityDone);
        network.SendGameMessage(buffer, client);
    }
}
