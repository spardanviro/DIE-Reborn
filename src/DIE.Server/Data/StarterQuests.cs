using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Data;

/// <summary>
/// 新手任务 —— 大厅里那个 STARTER QUESTS 面板。
///
/// ## 它其实是"剧情地图"的一小段
///
/// 进度不是单独存的, 而是 <c>StoryMapData.Node</c> 里的几个节点有没有解锁。
/// 节点表本身**客户端硬编码** (<c>StoryMapData.CreateData</c> 那 22 项),
/// 服务端只负责记住"这个账号解锁了哪几个" —— 存在 <c>story_map_data</c> blob
/// 里, 和角色所有权是同一段字节。
///
/// 客户端判解锁的条件是
///
///     UnlockedTimes &gt;= StoryMapData.Data.GetNode(id).Unlocks
///
/// <c>Unlocks</c> 默认 1, 但节点 11 是 **3** —— 那一条是"打满三局"。
///
/// ## 谁来推
///
/// 分两种, 这一点决定了为什么之前一直不动:
///
///   客户端发请求   合成 (6) 和商店 (10) —— GUI_WorkBenchCraftingConfirmationPopup
///                  和 GUI_NewShopBuyPopup 会发 UnlockStoryMapNodeRequest。
///                  我们那个处理器原来是个桩, 一律回 CannotUnlock。
///
///   服务端自己判   Scout (5)、Horde (9)、打三局 (11) —— 大厅那边**没有任何
///                  代码**会为它们发请求, 只能局末由服务端推。我们从来没推过。
///
/// 两条都断着, 所以玩家打完 lab raid 面板还是 0%。
/// </summary>
public static class StarterQuests
{
    /// <summary>
    /// 打完 Scout 任务 (lab raid)。
    /// 客户端叫它 <c>StarterQuestSoloScout2</c>。
    /// </summary>
    public const int SoloScout = (int)StoryMapData.Node.StarterQuestSoloScout2;

    /// <summary>合成第一把武器。客户端在 workbench 里自己发请求。</summary>
    public const int Crafting = (int)StoryMapData.Node.StarterQuestCraftingTutorial;

    /// <summary>打完一局 Horde。</summary>
    public const int Horde = (int)StoryMapData.Node.StarterQuestHoard;

    /// <summary>在商店买过东西。客户端自己发请求。</summary>
    public const int Shop = (int)StoryMapData.Node.StarterQuestShopTutorial;

    /// <summary>打满三局 —— 这一个要推 <see cref="MatchesRequired"/> 次。</summary>
    public const int PlayMatches = (int)StoryMapData.Node.StarterQuestPlay3Matches;

    /// <summary>五项全完成, 领总奖励 (金币, 数额见 <c>MenuHandlers.StarterQuestGoldReward</c>)。客户端点 CLAIM 时发请求。</summary>
    public const int All = (int)StoryMapData.Node.StarterQuestAll;

    /// <summary>
    /// **做过教程了** —— 这一项不在面板五项里, 但它是整个新手流程的闸门。
    ///
    /// ## 不解锁它, 玩家会被永远锁在教程里
    ///
    /// 大厅每次选完角色都查一次 (<c>CribMain.PreCheckPickCharacters</c>):
    ///
    ///     bool startTutorialWhenDone = !StoryMap.IsUnlocked(Node.StarterQuestTutorial);
    ///     if (startTutorialWhenDone &amp;&amp; popupsShown)
    ///         StartTutorial(null, cancelable: <b>false</b>);
    ///
    /// 没解锁就强制开教程, 而且**不可取消**。玩家 2026-09-21 报的
    /// "卡在完成 tutorial 后 starter quest 没有继续推进" 就是这个:
    /// 打完教程 -&gt; 节点没解锁 -&gt; 回大厅又被塞回教程, 面板那五项一步都走不到。
    ///
    /// ## 两条路都解锁它
    ///
    ///   打完教程   服务端在 <c>OnFinaleOver</c> 推 (见 <see cref="StarterQuestProgress"/>)
    ///   点 POSTPONE 客户端发 <c>UnlockStoryMapNodeRequest(3)</c>
    ///              (<c>GUI_StarterQuestPopup.PostponeButton_OnButtonClick</c>)
    ///
    /// 所以它既在 <see cref="ClientUnlockable"/> 白名单里, 也由服务端主动推。
    /// </summary>
    public const int Tutorial = (int)StoryMapData.Node.StarterQuestTutorial;

    /// <summary>
    /// 节点 11 要打几局。
    ///
    /// 值来自客户端硬编码的 <c>Unlocks = 3</c> —— 名字叫 Play3Matches, 数也是 3。
    /// 这里写常量而不是查 <c>StoryMapData</c>: 那张表是客户端的静态数据,
    /// 服务端这侧没有实例。有测试钉住两者一致。
    /// </summary>
    public const byte MatchesRequired = 3;

    /// <summary>
    /// 这个节点要推几次才算解锁。
    ///
    /// 除了"打三局"都是一次。
    /// </summary>
    public static byte TimesRequired(int nodeId) =>
        nodeId == PlayMatches ? MatchesRequired : (byte)1;

    /// <summary>
    /// 客户端允许自己请求解锁的节点。
    ///
    /// **白名单而不是黑名单**: 剧情地图上还有 DLC 礼包、周年庆之类的节点
    /// (50~56), 那些不该让客户端一句请求就领走。列表之外一律拒绝。
    /// </summary>
    public static readonly IReadOnlyList<int> ClientUnlockable = new[]
    {
        Tutorial,   // 3  —— 点 POSTPONE 跳过教程 (打完教程那条走服务端)
        Crafting,   // 6  —— 合成完第一把武器
        Shop,       // 10 —— 商店买完东西
        All,        // 12 —— 五项做完点 CLAIM
    };

    /// <summary>面板上按顺序排的那五项 + 总完成。</summary>
    public static readonly IReadOnlyList<int> Panel = new[]
    {
        SoloScout, Crafting, Horde, Shop, PlayMatches, All,
    };

    /// <summary>这个节点在已解锁列表里算不算完成。</summary>
    public static bool IsComplete(IEnumerable<StoryMapUnlockData> nodes, int nodeId)
    {
        StoryMapUnlockData? found = nodes?.Cast<StoryMapUnlockData?>()
            .FirstOrDefault(n => n.Value.NodeID == nodeId);

        return found.HasValue && found.Value.UnlockedTimes >= TimesRequired(nodeId);
    }
}
