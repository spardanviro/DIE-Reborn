using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;

namespace DIE.Server.Game;

/// <summary>
/// 把玩家登记进补给表 —— 不登记的话他捡多少补给都是 0。
///
/// ## 症状和根因隔了三层
///
/// 玩家实测: 占领补给点掉了 10 个箱子, 走过去捡, **教程不往下走**。
/// 服务端日志显示携带补给量从开局到结束一直是 0。
///
/// 拾取那条链是完整的原厂代码, 服务端也在跑:
///
///   1. <c>_00236W._00235W.UpdateGameObject</c> 每帧做碰撞检测, 玩家进 4 单位
///      就调 <c>_0023XW</c>
///   2. <c>_0023aX._0023yGc._0023XW</c> -> 教程实现了 ISupplies, 走
///      <c>_0023xGc</c>
///   3. <c>_0023xGc</c> -> <c>Supplies._0023Ij(IndexPlayer, 数量)</c>
///
/// 断在第 3 步: <c>_0023Ij</c> 是**遍历 <c>_0023zj</c> 表找 IndexPlayer**
/// (Supplies.cs:175), 找不到就什么也不做 —— 没有报错, 没有日志, 箱子照样消失。
///
/// 而往那张表里塞人的方法 <c>Supplies._0023Dj(Player)</c> (:91) 在**整个客户端树
/// 里零调用者** —— 又是"调用点丢了但类本身完整可用"(踩坑第 6 条第五种)。
///
/// ## 顺带补上 LoadSettings
///
/// <c>_0023Dj</c> 第一件事就是检查设置有没有加载过, 没有就
/// <c>StunLog.Error("Supply settings not loaded!")</c>。原厂在
/// <c>LoadGameMode</c> 里调 (-bd/-5cc.cs:123), 参数照抄那三个字段;
/// 其余用 <c>_0023lj</c> 的默认值, 和原厂一致。
///
/// 重复调无害 —— <c>LoadSettings</c> 只是存下 settings 并置一个标志
/// (Supplies.cs:66-70)。
/// </summary>
public sealed class SupplyRegistrar
{
    /// <summary>
    /// 已经登记过的玩家。
    ///
    /// 键带 <c>NumberOfGlobalCacheUses</c> —— Player 是池化的, 只用下标会串号
    /// (踩坑第 1 条)。<c>_0023Ej</c> 本身也是幂等的 (找到就 return),
    /// 这张表只是免得每帧白跑一遍。
    /// </summary>
    private readonly HashSet<(int Index, int Uses)> _registered =
        new HashSet<(int, int)>();

    private bool _settingsLoaded;

    /// <summary>诊断: 本 tick 登记了几个人。宿主读完自行清零。</summary>
    public int RegisteredThisTick;

    /// <summary>累计登记过几个人。</summary>
    public int Registered => _registered.Count;

    public void Update(GameManager simulation)
    {
        // ## 认接口, 不认教程
        //
        // 第一版这里写死了 `is _0023bd._00235cc tutorial` —— 只有教程模式能登记。
        // 玩家实测 (Starter Mission): "looter 掉落的物资箱拾取后并没有真正到身上,
        // 但是拾取物资这个功能在 tutorial 里面已经实现了, 这里不知道为什么又没有
        // 生效, 物资数量的 UI 也没有显示。"
        //
        // 原因就是这个类型判断: ScoutMission 不是 _00235cc, 当场 return,
        // 补给表里一个人都没有, 于是 _0023Ij 遍历不到 IndexPlayer, 箱子照样消失
        // 但数量不入账 —— 和教程当初那个 bug 一模一样, 只是这次卡在更早一步。
        //
        // 正确的判据是接口 <c>_0023bd._0023ad</c>: 教程 (-bd/-5cc.cs:23)、
        // ScoutMission、RaidMission、ScavengerHunt、TeamvsTeam 全都实现了它,
        // <c>_00239c()</c> 返回各自的 Supplies。凡是有补给的模式都该登记。
        if (simulation?.GameMode is not _0023bd._0023ad withSupplies
            || simulation.ActivePlayerList == null)
        {
            return;
        }

        Supplies supplies = withSupplies._00239c();
        if (supplies == null)
        {
            return;
        }

        // ## LoadSettings 只补教程那一份
        //
        // ScoutMission 自己在 LoadGameMode 里就调了 LoadSettings 并
        // AddSynchronizable (ScoutMission.cs:118-125) —— 那份是原厂的, 参数也是
        // 按那个模式配的, 我们不该拿教程的参数去盖掉它。
        //
        // 教程 (-bd/-5cc.cs:123) 那份原厂也调了, 但我们的 HeadlessViewManager
        // 路径下走不到, 所以补一次。重复调无害 (只是存下 settings 并置标志),
        // 但拿错参数就有害了。
        if (!_settingsLoaded && simulation.GameMode is _0023bd._00235cc)
        {
            // 照抄原厂教程的 LoadGameMode (-bd/-5cc.cs:123-128)。
            supplies.LoadSettings(new _0023bd._0023lj
            {
                _0023ej = true,
                _0023gj = true,
                _0023hj = false,
            });

            _settingsLoaded = true;
        }

        List<Player> players = simulation.ActivePlayerList;
        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null || !player.IsActive)
            {
                continue;
            }

            if (!_registered.Add((player.IndexGlobal, player.NumberOfGlobalCacheUses)))
            {
                continue;
            }

            supplies._0023Dj(player);
            RegisteredThisTick++;
        }
    }
}
