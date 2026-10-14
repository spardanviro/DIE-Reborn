using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using Serilog;

namespace DIE.Server.Game;

/// <summary>
/// 看住"活着的普通玩家, 视野中心必须是他自己" —— 偏了就拨回来, 并且喊一声。
///
/// ## 为什么需要这道兜底
///
/// 玩家实测 (2026-09-20 四人联机): "刚进入游戏这个玩家就出现了相机问题,
/// 得退出重进才行。" 截图里 UI 全在, 场景却只剩几道光柱 —— 典型的
/// **相机没跟上角色**。
///
/// 服务端的 <c>Client.LookAtCharacter</c> **直接决定客户端相机跟谁**, 这不是
/// 可见性那一侧的事 (那是另一个后果)。链路在原厂
/// <c>Client.ControllerServerToClientSerialize</c> (Client.cs:387):
///
///     bool flag2 = LocalPlayer != LookAtCharacter;
///     netBuffer.Write(flag2);
///     if (flag2) { netBuffer.Write(LookAtCharacter != null); ... }
///
/// 客户端照单全收 (Client.cs:465 起):
///
///     flag2 为假                  -> SetLookAtCharacter(LocalPlayer)  正常
///     flag2 为真 + 有目标         -> 相机跟那个目标
///     flag2 为真 + null           -> SetLookAtCharacter(null)
///                                    -> 相机目标退回 _ObserverTransform (原点)
///                                    -> TopDownCamera 停在开场动画结束的位置 -> 黑屏
///
/// ## 根因还没抓到, 但这条不变式是确定的
///
/// 能改这个字段的只有五处: 原厂 <c>Player</c> 生成时、
/// <see cref="RespawnFinisher"/> 复活时、<c>LidgrenHost</c> 入场/重连时
/// (这四处都设成玩家自己), 以及 <see cref="SpectatorPolicy"/> 的死后观战。
///
/// 那一局的日志显示: 入场没有报错 (<c>TrySpawnPlayer</c> 的两处提前返回都会
/// 记日志), 观察模式切换全天只有 2 次且都是合法观众, 没有一条"入场时不是观众
/// —— 拒绝"的告警。所以最可能是**死后观战之后没拨回来**:
/// <c>RespawnFinisher</c> 认的是"上一帧死、这一帧活", 断线重连或者到局末都没
/// 复活, 这个判据就不触发。
///
/// 与其赌某一条路径, 不如守住这条不变式本身: **活着的、非观众的玩家,
/// 视野中心只能是他自己**, 任何偏离都是 bug。拨回来的代价是一次赋值,
/// 而代价的另一边是"这一局玩不了, 得退出重进"。
///
/// 每次纠正都记 Warning 并说明原来指向谁 —— 下次复现时日志里就能直接看到
/// 根因, 不用再猜。
/// </summary>
public sealed class CameraTargetGuard
{
    /// <summary>纠正过几次 —— 测试和诊断用。</summary>
    public int Corrections { get; private set; }

    /// <summary>最近一次纠正前, 视野中心指着谁 (null 表示指着空)。</summary>
    public string LastStrayTarget { get; private set; }

    /// <summary>
    /// 每帧太密, 而这只是兜底 —— 一秒一次足够 (黑屏是持续状态, 不是瞬时的)。
    /// </summary>
    public float CheckSeconds { get; set; } = 1f;

    private float _sinceCheck;

    /// <summary>
    /// 扫一遍在场玩家。
    /// </summary>
    /// <param name="observerSlots">
    /// 入场时就是观众的槽位 —— 他们的视野中心本来就该在别处, 跳过。
    /// </param>
    /// <returns>这一次拨回了几个。</returns>
    public int Update(GameManager simulation, float deltaTime, int port,
                      IReadOnlySet<int> observerSlots = null)
    {
        if (simulation?.ActivePlayerList == null)
        {
            return 0;
        }

        _sinceCheck += deltaTime;
        if (_sinceCheck < CheckSeconds)
        {
            return 0;
        }

        _sinceCheck = 0f;
        int fixedUp = 0;

        for (int i = 0; i < simulation.ActivePlayerList.Count; i++)
        {
            Player player = simulation.ActivePlayerList[i];

            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            if (player.Client is not Client client || client.LocalPlayer != player)
            {
                continue;
            }

            // 真观众不管 —— 他们没有自己的角色, 视野中心在哪是他们自己的事。
            if (client.IsObserver || observerSlots?.Contains(client.Index) == true)
            {
                continue;
            }

            if (ReferenceEquals(client.LookAtCharacter, player))
            {
                continue;
            }

            LastStrayTarget = client.LookAtCharacter?.Name ?? "(空)";

            Log.Warning("战斗服 UDP {Port} slot {Index} 活着却没看着自己 —— "
                        + "视野中心是 {Stray}, 已拨回 {Self}。"
                        + "客户端那边的表现就是相机不跟人 (黑屏)",
                port, client.Index, LastStrayTarget, player.Name);

            client.SetLookAtCharacter(player);
            Corrections++;
            fixedUp++;
        }

        return fixedUp;
    }
}
