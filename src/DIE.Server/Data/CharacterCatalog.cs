using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ConductorCrafting;
using StunMessage;

namespace DIE.Server.Data;

/// <summary>
/// 账号拥有哪些角色 —— 编码在 <c>AccountsData.StoryMapData</c> 这个 blob 里。
///
/// ## 角色所有权不是一张表, 是一段序列化数据
///
/// 客户端从来不问"我有哪些角色"。它在 <c>AccountDataManager.SetAccountData</c>
/// (:366) 里解 <c>AccountData.StoryMapData</c>:
///
///     AccountsData.Deserialize(ref data, out unlockedNodes, out characters)
///     foreach (CharacterData c in characters)
///         if (c.Owned) _OwnedCharacters.Add(new CharacterItemInfo(c.Character));
///
/// blob 为空 -> Deserialize 直接返回 false -> <c>_OwnedCharacters</c> 是空表 ->
/// 大厅的 SELECT CHARACTER 四个格子全灰、PARTY STRENGTH 为 0、READY 点不动。
/// 表现就是"除了新手任务哪都进不去" —— 新手任务不要求拥有角色。
///
/// ## 为什么用原厂的序列化器
///
/// <c>AccountsData.Serialize</c> / <c>CharacterData.Serialize</c> 是双端共享的
/// 同一份代码 (ConductorCrafting)。用它写出来的字节, 客户端的 Deserialize 必然
/// 接受 —— 这比照着反序列化器手写一遍可靠得多, 也是这个项目一贯的做法。
///
/// 格式 (AccountsData.cs:91):
///     byte  version = 1
///     StoryMapUnlockData.Serialize(...)     // 剧情地图解锁节点
///     CharacterData.Serialize(...)          // 角色所有权
/// </summary>
public static class CharacterCatalog
{
    /// <summary>
    /// 可选角色 —— <c>CharacterEnum</c> 里没有标 <c>[Hide]</c> 的那些。
    ///
    /// <c>[Hide]</c> 标的是不该出现在选人界面上的条目: <c>None</c> 占位、
    /// <c>Orber</c>/<c>Goat</c> 这类内部实体。用特性过滤而不是手抄一份名单,
    /// 是为了上游枚举改动时不会悄悄不同步。
    /// </summary>
    public static IReadOnlyList<CharacterEnum> Playable { get; } = BuildPlayableList();

    /// <summary>
    /// 有玩法实现、但**客户端美术资源残缺**的角色, 不发放。
    ///
    /// 判据来自真机观察 + 服务端交叉验证:
    ///
    ///   Logan  选人界面顶着 Berg 的头像, 详情页用 Isys 的立绘, 主菜单里没有模型,
    ///          进对局后显示成 Berg。
    ///          而服务端这侧一切正常 —— 日志是 `角色=Logan`、
    ///          `生成玩家: Logan charIndex=465`, 且 26 个角色的 typeId 和实现类
    ///          互不相同 (Logan -> _0023n9q)。
    ///
    ///   也就是说玩法类齐全, 缺的是客户端的图标/立绘/模型映射 —— 这个版本
    ///   (v0.8.5, 官方停服前的最后一版) 里 Logan 显然还没做完。
    ///
    /// 服务端查不到"客户端有没有这个角色的美术资源", 所以只能按观察列举。
    /// 以后发现别的角色有同样症状, 加进这个数组即可。
    /// </summary>
    private static readonly CharacterEnum[] MissingClientAssets = { CharacterEnum.Logan };

    /// <summary>
    /// 商店里卖得的角色 —— 可选角色减去美术资源残缺的那些。
    ///
    /// 名字保留 "Grantable" 是因为它同时是**货架的来源**和"这个 ID 合不合法"
    /// 的判据 (见 <see cref="ShopCatalog"/> 和 <c>AccountStore.UnlockCharacter</c>)。
    /// </summary>
    public static IReadOnlyList<CharacterEnum> Grantable { get; } =
        Playable.Where(c => !MissingClientAssets.Contains(c)).ToList();

    /// <summary>
    /// 新账号开局送的角色。**这几个是我们定的。**
    ///
    /// ## 为什么不再全发
    ///
    /// 早先每个账号一进来就拿到全部 <see cref="Grantable"/>。玩家把商店改成
    /// "角色用角色点买"之后, 那个行为让商店那一档彻底没意义 —— 货架上摆的
    /// 全是你已经有的。
    ///
    /// 送三个: 够挑一个顺手的开局, 又留得下继续解锁的动力。
    ///
    /// ## 点名普通版 (Survivor), 不是"可选角色的前三个"
    ///
    /// 早先取的是 <see cref="Grantable"/> 的前三个, 想着不抄枚举名。可枚举的顺序是
    /// <c>BergMutated, IsysMutated, AmberMutated, BryceMutated, BergSurvivor ...</c> —— 前三个正好是**变异版**。
    /// 玩家实测 (2026-09-13): "初始的三个角色现在是 mutated 版本的 isys, amber, berg, 应该是普通版本的。"
    ///
    /// 老账号手上那三个变异版由 <c>AccountStore.SwapMutatedStarters</c> 换回来。
    /// </summary>
    public static IReadOnlyList<CharacterEnum> Starter { get; } = new[]
    {
        CharacterEnum.BergSurvivor,
        CharacterEnum.IsysSurvivor,
        CharacterEnum.AmberSurvivor,
    };

    /// <summary>
    /// 早先误发的初始角色 (变异版), 和 <see cref="Starter"/> 一一对应。只给老账号迁移用。
    /// </summary>
    public static IReadOnlyList<CharacterEnum> MistakenStarter { get; } = new[]
    {
        CharacterEnum.BergMutated,
        CharacterEnum.IsysMutated,
        CharacterEnum.AmberMutated,
    };

    private static IReadOnlyList<CharacterEnum> BuildPlayableList()
    {
        List<CharacterEnum> playable = new List<CharacterEnum>();

        foreach (CharacterEnum value in Enum.GetValues(typeof(CharacterEnum)).Cast<CharacterEnum>())
        {
            FieldInfo field = typeof(CharacterEnum).GetField(value.ToString());
            if (field == null || field.GetCustomAttribute<HideAttribute>() != null)
            {
                continue;
            }

            playable.Add(value);
        }

        return playable;
    }

    /// <summary>
    /// 造一份 <c>StoryMapData</c> blob, 把给定的角色标成已拥有。
    /// </summary>
    /// <param name="owned">要标成已拥有的角色。</param>
    /// <param name="unlockedNodes">
    /// 剧情地图的解锁节点 —— 新手任务的进度就存在这里。
    ///
    /// **必须原样带上**。这个 blob 里角色和节点是同一份数据的两半, 重写任何
    /// 一半都要把另一半读出来再写回去 —— 只传角色的话玩家的新手任务进度会被
    /// 悄悄清空 (客户端那边表现为进度条退回 0%)。
    /// </param>
    public static byte[] BuildStoryMapData(IEnumerable<CharacterEnum> owned,
                                           IEnumerable<StoryMapUnlockData> unlockedNodes = null)
    {
        CharacterData[] characters = owned
            .Select(c => new CharacterData { Character = c, Owned = true, XP = 0 })
            .ToArray();

        Message buffer = Message.Create(1024);
        AccountsData.Serialize(ref buffer,
                               unlockedNodes ?? Array.Empty<StoryMapUnlockData>(),
                               characters);
        buffer.Position = 0L;
        return buffer.ToBytes();
    }

    /// <summary>
    /// blob 里已拥有的角色。用**原厂的反序列化器**读 —— 也就是客户端会读到的东西。
    ///
    /// 读不动就返回空表 (而不是抛)。空 blob 是新账号的合法状态。
    /// </summary>
    public static IReadOnlyList<CharacterEnum> ReadOwned(byte[] storyMapData) =>
        Read(storyMapData).Owned;

    /// <summary>blob 里已解锁的剧情地图节点 (新手任务进度就是它)。</summary>
    public static IReadOnlyList<StoryMapUnlockData> ReadUnlockedNodes(byte[] storyMapData) =>
        Read(storyMapData).Nodes;

    /// <summary>
    /// 一次把 blob 的两半都读出来。
    ///
    /// 分开读会**解析两遍**, 更要紧的是容易写出"只读一半就重写"的代码 ——
    /// 那正是新手任务进度被角色解锁悄悄清掉的那个 bug。
    /// </summary>
    public static (IReadOnlyList<CharacterEnum> Owned,
                   IReadOnlyList<StoryMapUnlockData> Nodes) Read(byte[] storyMapData)
    {
        if (storyMapData == null || storyMapData.Length == 0)
        {
            return (Array.Empty<CharacterEnum>(), Array.Empty<StoryMapUnlockData>());
        }

        Message buffer = Message.Create(storyMapData.Length, storyMapData);

        if (!AccountsData.Deserialize(ref buffer,
                out IEnumerable<StoryMapUnlockData> nodes,
                out IEnumerable<CharacterData> characters))
        {
            return (Array.Empty<CharacterEnum>(), Array.Empty<StoryMapUnlockData>());
        }

        return (
            characters?.Where(c => c.Owned).Select(c => c.Character).ToList()
                ?? (IReadOnlyList<CharacterEnum>)Array.Empty<CharacterEnum>(),
            nodes?.ToList() ?? (IReadOnlyList<StoryMapUnlockData>)Array.Empty<StoryMapUnlockData>());
    }
}
