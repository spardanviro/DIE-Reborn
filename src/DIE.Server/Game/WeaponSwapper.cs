using ConductorGameLogic;
using ConductorGameLogic.Entities;

namespace DIE.Server.Game;

/// <summary>
/// 服务端主动帮玩家切换武器 —— 生成原厂的 <c>SwapWeapon</c> 实体。
///
/// ## 为什么需要它
///
/// 切武器平时是**客户端权威**的:
///
///   1. 客户端按键 -> <c>Player.SwapWeapon()</c> (:674) 置标志 `_0023cC`
///   2. <c>Player._0023oD()</c> (:1452) 每帧消费, 里面有教程守卫
///      (`if (IsTutorialGameMode &amp;&amp; GetWeapon(num) == null) return;`)
///   3. 客户端每帧在控制器数据里上报 `_00230z`(= `weapon1Equipped`),
///      服务端 <c>Character.UpdateSwapWeapon</c> (:3944) 据此跟随
///
/// 真机上玩家按键切不过去, 而服务端唯一能看到的是诊断行的 `武器槽=` 一直是 0 ——
/// 也就是客户端从没上报过切换。原因只能在客户端观测, 而那条路本项目是死路。
///
/// ## `SwapWeapon` 实体是另一条路, 而且是原厂的
///
/// <c>_0023EF._0023VC</c>, 明文名 **SwapWeapon** (查表得来, GUID 5b46a628-…, 池 12)。
/// 它是个 <c>GameObjectBase</c>, 生成时:
///
///     protected override void SpawnGameObject()
///     {
///         Item weapon = Owner.AsPlayer.GetWeapon(_0023zy);
///         if (!IsTutorialGameMode || weapon != null)
///         {
///             Owner.AsCharacter.EquipWeapon(_0023zy);
///             ... 播 SwapEffect 特效 ...
///         }
///     }
///
/// 目标槽 <c>_0023zy</c> 走 <c>SerializeGameObjectBase</c> 传给客户端 (:67),
/// 所以**两端各跑一次 `EquipWeapon`** —— 这就是原厂让客户端切武器的机制,
/// 而生成实体是服务端的权力。
///
/// 实体 1 秒后自毁 (<c>UpdateGameObject</c>: `if (Age > 1f) Destroy();`),
/// 不用我们管生命周期。
///
/// ## 它同时是一个观测点
///
/// 生成之后看服务端的 `武器槽`:
///
///   - **保持在目标槽** -> 客户端接受了, 说明它的 `_Weapons[槽]` 非 null
///   - **被拉回原槽** -> 客户端那边 `GetWeapon(槽)` 是 null, 守卫拦住了,
///     于是它继续上报旧值, 服务端的 `UpdateSwapWeapon` 把自己切回去
///
/// 这是**服务端可见**地判断"客户端到底有没有拿到那把武器"的办法 ——
/// 比在客户端装观测点靠谱。
/// </summary>
public static class WeaponSwapper
{
    /// <summary>
    /// 让 <paramref name="player"/> 切到 <paramref name="slot"/> 槽。
    /// </summary>
    /// <returns>成功生成 SwapWeapon 实体返回 true。</returns>
    public static bool SwapTo(GameManager manager, Player player, int slot)
    {
        if (manager == null || player == null || !player.IsActive)
        {
            return false;
        }

        if (player.EquippedWeaponSlot == slot)
        {
            return true;   // 已经在那个槽了, 幂等
        }

        // 服务端侧先确认那把武器真的在 —— _0023VC 的守卫读的是 Owner 的
        // GetWeapon(slot), 服务端为空的话生成了也什么都不做, 白占一个池位。
        if (player.GetWeapon(slot) == null)
        {
            return false;
        }

        _0023EF._0023VC swap = manager.GetGameObjectFromPool<_0023EF._0023VC>();
        if (swap == null || swap.IsDummyObject)
        {
            return false;
        }

        // 目标槽要在 Spawn **之前**设 —— SpawnGameObject 里立刻就读它
        // (`GetWeapon(_0023zy)`), 之后设等于白设。
        swap._0023zy = slot;

        // owner 必须是玩家 —— 实体全靠 `Owner.AsPlayer` 找目标, 传 null 就是 NRE。
        manager.SpawnGameObject(swap, player, null);

        return swap.IsActive;
    }
}
