using System;
using ConductorCrafting;
using ConductorGameLogic.GameModes;
using Serilog;

namespace DIE.Server.Data;

/// <summary>
/// 局末推进新手任务 —— <see cref="StarterQuests"/> 里"服务端自己判"的那三项。
///
/// ## 为什么必须服务端推
///
/// 大厅那边为合成 (6) 和商店 (10) 会主动发 <c>UnlockStoryMapNodeRequest</c>,
/// 但 Scout (5) / Horde (9) / 打三局 (11) **没有任何客户端代码会发请求** ——
/// 那三项本来就是靠服务端在局末判的。我们一直没判, 所以玩家打完 lab raid
/// 面板还停在 0%, 箭头也还指着 lab raid。
///
/// ## 只算打完的局
///
/// 中途退出不算。判据用 <c>GameMode.IsCompleted</c> —— 和局末奖励那边
/// (<c>MatchRewards</c> 的 VictoryPlacement) 是同一个判据, 两边不该有分歧。
/// </summary>
public sealed class StarterQuestProgress
{
    private readonly AccountStore _accounts;

    public StarterQuestProgress(AccountStore accounts) =>
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

    /// <summary>
    /// 打完一局。按模式推对应的节点, 外加"打三局"那一项。
    /// </summary>
    /// <returns>推动了几个节点。</returns>
    public int OnMatchCompleted(ulong userId, GameModeType mode)
    {
        if (userId == 0)
        {
            return 0;
        }

        int advanced = 0;

        int? modeNode = NodeFor(mode);

        if (modeNode.HasValue)
        {
            _accounts.AdvanceStoryMapNode(userId, modeNode.Value,
                StarterQuests.TimesRequired(modeNode.Value));
            advanced++;
        }

        // "打三局"跟模式无关, 任何一局打完都算 —— 名字就是 Play3Matches。
        // 上限交给 AdvanceStoryMapNode: 到 3 就不再加, 免得 byte 绕回 0。
        //
        // **教程不算一局**: 面板那一项写的是 "Play 3 Matches", 而教程是
        // tutorial 不是 match。算进去的话玩家打完教程就白得一格。
        if (mode != GameModeType.Tutorial)
        {
            _accounts.AdvanceStoryMapNode(userId, StarterQuests.PlayMatches,
                StarterQuests.MatchesRequired);
            advanced++;
        }

        Log.Information("账号 {UserId} 打完一局 {Mode}, 推进 {Count} 个新手任务节点",
            userId, mode, advanced);

        return advanced;
    }

    /// <summary>
    /// 这个模式对应新手任务里的哪一项。
    ///
    /// <c>ScoutMission</c> 就是 lab raid (Scout_02_Lab) —— 客户端那一项叫
    /// <c>StarterQuestSoloScout2</c>。
    ///
    /// 第三项节点名叫 Hoard, 面板文字是 "Complete a cooperative mission"。开了十字路口 (Crossroads 功能开关)
    /// 的版本里, 点这一项客户端建的就是**十字路口** (<c>QueueType.RaidMission</c>) 的房间, 没开才是 Horde
    /// (GUI_StarterQuestPopup.cs:344)。我们开放的是十字路口, 所以两种模式都算。
    ///
    /// 以前只认 Horde —— 玩家打完十字路口第三项一直不亮 (2026-09-15 报)。
    /// </summary>
    private static int? NodeFor(GameModeType mode) => mode switch
    {
        GameModeType.ScoutMission => StarterQuests.SoloScout,
        GameModeType.RaidMission => StarterQuests.Horde,
        GameModeType.Horde => StarterQuests.Horde,

        // 打完教程要解锁"做过教程了"那个节点, 否则大厅每次进来都强制重播教程
        // (CribMain.PreCheckPickCharacters, cancelable: false) —— 玩家 09-21
        // 报的"卡在完成 tutorial 后 starter quest 没有继续推进"就是这个。
        // 它不在面板五项里, 但是整个新手流程的闸门, 见 StarterQuests.Tutorial。
        GameModeType.Tutorial => StarterQuests.Tutorial,

        _ => null,
    };
}
