using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;

namespace DIE.Server.Game;

/// <summary>
/// 复活之后把装备重新下发一次 —— 修"复活后手上武器模型消失"。
///
/// ## 症状
///
/// 玩家实测: "复活后手上的武器模型消失了, 切换发现两把武器都不显示,
/// 但是是握在手上的, 可以正常使用。"
///
/// ## 客户端为什么不显示
///
/// <c>PlayerModel.Update</c> (:124) 完全按 <c>EquippedWeaponSchematicId</c> 选模型,
/// 而那个属性 (Character.cs:997) 是:
///
///     _EquippedWeapon != null ? _EquippedWeapon.Schematic.Id : -1
///
/// 拿到 -1 就走 :141 那一支 —— <c>SetActiveWeapon(null, noWeapon: true)</c>,
/// 手上什么都不挂。所以客户端的 <c>_EquippedWeapon</c> 确实是 null。
///
/// ## 是复活流程自己把客户端那份清掉的
///
/// <c>Player.RespawnCharacter()</c> (Player.cs:1839) 的前几句:
///
///     _EquippedWeapon.Remove(); _EquippedWeapon = null;
///     foreach (Client c in NetworkManager.ActiveClientsList)
///         _SyncHelper._00235Ce(c.Index, false);      // 对所有人置为不可见
///     ...
///     Spawn();                                       // 再可见
///
/// 中间那一步把玩家实体对每个客户端**先隐藏再显示**。客户端那边一次
/// "不可见"就是一次销毁, 而 <c>Character.DestroyEntity</c> (:2762) 里:
///
///     _EquippedWeapon.Remove(); _EquippedWeapon = null;
///     ClearWeapons();
///
/// 于是客户端的 <c>_Weapons[]</c> 和 <c>_EquippedWeapon</c> 全空了。重新
/// 可见时走的是 <c>SpawnEntity</c>, 那里**不发武器** —— 服务端侧靠
/// <c>RespawnCharacter</c> 后半段的 <c>EquipHaxWeaponNew</c> 补回来,
/// 客户端侧没有对应的东西。
///
/// ## 原厂靠脏标记, 我们从来没读过它
///
/// <c>EquipHaxWeaponNew</c> (Player.cs:2630) 最后写的是:
///
///     Client.ClientInfoData._0023sYb = true;        // 脏
///     Client.ClientInfoData._0023lYb = unique;      // 新的武器1
///     Client.ClientInfoData._0023mYb = unique;      // 新的武器2
///
/// 注意这三句每次复活都会重新跑 —— 生成的是**全新的 Item**(新 Guid),
/// 所以就算客户端那份没被清掉也已经对不上了。原厂服务端显然是看着
/// <c>_0023sYb</c> 把 ClientInfo 再发一遍, 那段随服务端丢失。
///
/// 补回来的就是这一发: <see cref="ClientInfoSender.BroadcastEquipUpdate"/>,
/// 它的接收方 (GameManagerClient.cs:569-587) 会重新构造武器物品并
/// <c>EquipWeaponsAndAbilities(forceSetSlot: true)</c> —— 和教程发武器
/// 走的是同一条路, 那条已经真机验证过。
///
/// ## 为什么是"边沿检测"而不是在复活处理器里直接发
///
/// 复活有两个入口:
///
///   1. 玩家按左键 -> <see cref="RespawnPolicy.TryRespawn"/>, 我们自己的代码
///   2. Amber 引导完成 -> <c>_0023EF._0023Gpc._0023blb</c> 里的
///      <c>Mb.AsPlayer.Respawn()</c> —— **原厂代码内部**, 我们插不进去
///
/// 第二条路没有钩子, 所以只能在外面看结果: 盯住 <c>IsDead</c> 由 true 变
/// false 的那一帧。两条路一起覆盖, 以后再多出第三条也不用改这里。
///
/// ## 顺带补上控制权重绑
///
/// <c>RespawnCharacter</c> 只重置了 <c>LookAtCharacter</c> (Player.cs:1876),
/// **没碰 <c>ControlledCharacter</c>**。玩家自己按左键那条路上我们在
/// <see cref="RespawnPolicy"/> 里手动补了一句 <c>SetControlledCharacter</c>;
/// Amber 那条路在原厂 GameObject 内部, 没人补。
///
/// 两条路的差别就落在这里, 所以挪到边沿上统一做一次, 两边行为一致。
/// </summary>
public sealed class RespawnFinisher
{
    /// <summary>上一帧还死着的玩家。键带池复用次数, 免得实体回池后串号。</summary>
    private readonly HashSet<(int Index, int Uses)> _wasDead =
        new HashSet<(int, int)>();

    /// <summary>诊断: 一共补发过几次。</summary>
    public int RefreshCount { get; private set; }

    /// <summary>
    /// 这一帧刚复活的玩家。给调用方打诊断用 —— 玩家报的"血量 0/怒气 0/打不掉血"
    /// 全是从 HUD 上读的, 而 HUD 读的是客户端那份。要判断故障在哪一侧,
    /// 得把服务端这一侧的同一组数字打出来对照。
    ///
    /// <c>RespawnServerStateTests</c> 已经钉住"服务端侧是健康的", 所以真机上
    /// 这行如果也是健康的, 问题就整个在下发那一侧。
    /// </summary>
    public readonly List<Player> RespawnedThisTick = new List<Player>();

    /// <summary>
    /// 每帧一次。检测到有人从死变活就重绑控制权 + 把全场装备重发一遍。
    /// </summary>
    /// <returns>这一帧补发了几条 ClientInfo (0 表示没人复活)。</returns>
    public int Update(GameManager simulation, GameServer network)
    {
        RespawnedThisTick.Clear();

        if (simulation?.ActivePlayerList == null)
        {
            return 0;
        }

        List<Player> players = simulation.ActivePlayerList;
        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null)
            {
                continue;
            }

            var key = (player.IndexGlobal, player.NumberOfGlobalCacheUses);

            if (player.IsDead)
            {
                _wasDead.Add(key);
            }
            else if (_wasDead.Remove(key))
            {
                RespawnedThisTick.Add(player);
            }
        }

        if (RespawnedThisTick.Count == 0)
        {
            return 0;
        }

        // 控制权重绑。幂等 —— 两者本来就指向同一个实体, 但 Spawn() 走的是
        // "先从活动列表摘除再重新激活"的路子, 显式重设一次才能保证控制权
        // 不随实体身份的变化而丢掉。故障表现是"人复活了但按键全无响应"。
        for (int i = 0; i < RespawnedThisTick.Count; i++)
        {
            Player player = RespawnedThisTick[i];
            if (player.Client is Client client)
            {
                client.SetControlledCharacter(player);
                client.SetLookAtCharacter(player);
            }
        }

        if (network == null)
        {
            return 0;
        }

        RefreshCount++;
        return ClientInfoSender.BroadcastEquipUpdate(network);
    }
}
