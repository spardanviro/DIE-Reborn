using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using DIE.Server.Game;
using Serilog;

namespace DIE.Server.Data;

/// <summary>
/// 账号库和局末奖励之间的两座桥。
///
/// 往哪边走各一座:
///
///   <see cref="SnapshotFor"/>  账号 -&gt; 算奖励要用的输入 (经验、背包里已有的东西)
///   <see cref="Grant"/>        原厂算出来的奖励 -&gt; 落库
///
/// 放在 <c>AccountStore</c> 外面是因为它认识 <c>RewardCollectionData</c> ——
/// 那是奖励系统的类型, 不该渗进"存取账号"这一层。
/// </summary>
public sealed class RewardLedger
{
    private readonly AccountStore _accounts;

    public RewardLedger(AccountStore accounts) =>
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

    /// <summary>
    /// 算奖励要用的账号侧输入。
    ///
    /// 背包里已有的蓝图/改装图/装置蓝图是给原厂**配平**用的:
    /// <c>GiveAutoGroup</c> 会看你远程和近战蓝图的比例, 缺哪类给哪类。
    /// 传空不会报错, 只是掉落变成纯随机。
    /// </summary>
    public RewardAccountSnapshot SnapshotFor(ulong userId, IReadOnlyList<ushort> equippedWeapons)
    {
        Account account;
        IReadOnlyList<Stackable> stackables;

        try
        {
            // Load 对不存在的账号是**抛**不是返回 null (AccountStore.cs)。
            // 这里不能让它冒出去: 上面那层 (SendMatchRewards) 的 catch 会把
            // 整局的奖励一起丢掉 —— 一个人读不到不该连累同队的其他人。
            account = _accounts.Load(userId);
            stackables = _accounts.LoadStackables(userId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "算局末奖励时读不到账号 {UserId}, 按空账号处理", userId);
            return RewardAccountSnapshot.Empty;
        }

        if (account == null)
        {
            Log.Warning("算局末奖励时读不到账号 {UserId}, 按空账号处理", userId);
            return RewardAccountSnapshot.Empty;
        }

        return new RewardAccountSnapshot(
            UserId: userId,
            AccountExperience: account.StoryMapXp,
            WeaponSchematicIds: equippedWeapons ?? Array.Empty<ushort>(),
            BlueprintIds: IdsOf(stackables, StackableType.Schematic),
            DesignIds: IdsOf(stackables, StackableType.Design),
            GadgetBlueprintIds: IdsOf(stackables, StackableType.GadgetBlueprint),

            Counters: PadCounters(account.CounterData),
            ScavengerFirstWinAvailable: IsNewDay(_accounts.LastScavengerFirstWinUtc(userId), DateTime.UtcNow));
    }

    /// <summary>"一天"的分界: 北京时间 (UTC+8) 零点。玩家都在国内, 按服务器所在时区算会随部署地点漂。**我们定的。**</summary>
    public static readonly TimeSpan DayOffset = TimeSpan.FromHours(8);

    /// <summary>上一次首胜之后是不是已经跨过一天 (从没拿过也算)。</summary>
    public static bool IsNewDay(DateTime? lastUtc, DateTime nowUtc) =>
        lastUtc == null || (lastUtc.Value + DayOffset).Date < (nowUtc + DayOffset).Date;

    /// <summary>记下这个人今天的拾荒首胜已经拿了。</summary>
    public void MarkScavengerFirstWin(ulong userId)
    {
        if (userId != 0)
        {
            _accounts.MarkScavengerFirstWin(userId, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// 把原厂算出来的一份奖励落库。
    ///
    /// **顺序上必须先落库再下发**: 结算画面上写着"+N", 那些数得已经进账,
    /// 否则玩家一退到大厅就对不上。
    /// </summary>
    /// <param name="counters">
    /// 这一局之后的保底计数器 (原厂 <c>CalculateCounters</c> 算的)。
    /// 传 null 表示这一局没算 —— 保持库里原样, 别把它抹成空。
    /// </param>
    /// <returns>落了什么, 给日志用。</returns>
    public GrantSummary Grant(ulong userId, RewardCollectionData rewards,
                              IReadOnlyList<byte> counters = null)
    {
        if (userId == 0)
        {
            return default;
        }

        if (counters != null && counters.Count > 0)
        {
            _accounts.SaveCounters(userId, counters.ToArray());
        }

        GrantSummary summary = new GrantSummary
        {
            Detail = new List<string>(),
            Silver = rewards.Currencies.Silver,
            CharacterPoints = rewards.Currencies.CharacterPoints,
            ResearchPoints = rewards.Currencies.ResearchPoints,
        };

        if (summary.Silver > 0)
        {
            summary.SilverBalance = _accounts.AddCurrency(
                userId, AccountStore.CurrencyKind.Silver, summary.Silver);
        }

        if (summary.CharacterPoints > 0)
        {
            _accounts.AddCurrency(
                userId, AccountStore.CurrencyKind.CharacterPoints, summary.CharacterPoints);
        }

        if (summary.ResearchPoints > 0)
        {
            _accounts.AddCurrency(
                userId, AccountStore.CurrencyKind.ResearchPoints, summary.ResearchPoints);
        }

        // 金币不落库 —— 那是充值货币, 掉落表里也没配。真配了也当没看见,
        // 免得哪天手滑就成了免费氪金。
        if (rewards.Currencies.Gold > 0)
        {
            Log.Warning("局末奖励里出现了 {Gold} 金币, 已忽略 —— 金币是充值货币",
                rewards.Currencies.Gold);
        }

        if (rewards.Items == default(ItemCollection))
        {
            return summary;
        }

        // 收藏品 (图纸 / 装置蓝图 / 改装图) 重复了就不进库, 折成账号经验 ——
        // 见 DuplicateCollectibles 和下面 DuplicateExperience 的说明。
        int duplicateXp = 0;

        foreach (Stackable stackable in rewards.Items.Stackables ?? new List<Stackable>())
        {
            if (DuplicateCollectibles.IsCollection(stackable.Type)
                && _accounts.CountStackable(userId, stackable.ID, stackable.Type) > 0)
            {
                int xp = DuplicateCollectibles.For(stackable.Type, stackable.ID).AccountXP;
                duplicateXp += xp;

                summary.Detail.Add($"{Describe(stackable)} (已有, 折 {xp} 经验)");
                continue;
            }

            // Stackable.Amount 是原厂在这份奖励里填的**本次数量**, 不是余额 ——
            // GiveStackable 建的时候传的就是 dropReward.Amount。
            _accounts.AddStackable(userId, stackable.ID, stackable.Type, stackable.Amount);

            summary.Stackables += stackable.Amount;
            summary.Detail.Add(Describe(stackable));
        }

        if (duplicateXp > 0)
        {
            summary.DuplicateExperience = duplicateXp;
            _accounts.AddStoryMapXp(userId, (uint)duplicateXp);
        }

        foreach (Gadget gadget in rewards.Items.Gadgets ?? new List<Gadget>())
        {
            _accounts.AddGadget(userId, gadget);
            summary.Gadgets++;
        }

        foreach (Unique weapon in rewards.Items.Uniques ?? new List<Unique>())
        {
            _accounts.AddUnique(userId, weapon);
            summary.Uniques++;
        }

        return summary;
    }

    /// <summary>
    /// 一件堆叠物的人话描述, 给日志用。
    ///
    /// ## 为什么值得多查这一次图鉴
    ///
    /// 原来那条日志只说"堆叠物 N 件"。玩家报"掉落显示有零件但仓库看不到"时,
    /// 光凭那句话分不清是**没掉**、**没入库**、还是**入了但没显示** ——
    /// 得去翻库、对 StackableType 的枚举值才知道那 N 件是什么。
    ///
    /// 一局就这么几件, 多查几次图鉴换来的是下次一眼能看出问题在哪。
    /// 查不到名字也不影响落库, 退回 "类型#编号"。
    /// </summary>
    private static string Describe(Stackable stackable)
    {
        string name = stackable.Type switch
        {
            StackableType.Part => CraftingManager.Parts?
                .FirstOrDefault(p => p != null && p.ID == stackable.ID)?.WorkingName,
            StackableType.Schematic => CraftingManager.GetWeaponSchematic(stackable.ID)?.Name,
            StackableType.GadgetBlueprint => CraftingManager.GetTrinket(stackable.ID)?.WorkingName,
            StackableType.Consumable => CraftingManager.Consumables?
                .FirstOrDefault(c => c != null && c.ID == stackable.ID)?.WorkingName,
            StackableType.Lockbox => ((LockboxTypes)stackable.ID).ToString(),

            // 暗码没有品类 (原厂一律 ID 0), 改装图只有编号没有名字
            StackableType.Keycode => "Keycode",
            _ => null,
        };

        return $"{name ?? $"{stackable.Type}#{stackable.ID}"} x{stackable.Amount}";
    }

    private static IReadOnlyList<ushort> IdsOf(IReadOnlyList<Stackable> stackables,
                                               StackableType type) =>
        stackables.Where(s => s.Type == type).Select(s => s.ID).ToArray();

    /// <summary>
    /// 把库里存的计数器补齐到枚举的长度。
    ///
    /// 原厂 <c>ResetCounter</c>/<c>IncreaseCounter</c> 会按需 Resize, 所以短了
    /// 不至于出错; 但曲线上的 <c>BasedOnCounter</c> 判的是
    /// <c>index &lt; counters.Length</c> —— 数组短的话那一档保底**静默失效**。
    /// 与其依赖原厂补, 不如进来就补齐。
    /// </summary>
    private static byte[] PadCounters(byte[] stored)
    {
        byte[] counters = new byte[Enum.GetValues<AccountCounter>().Length];

        if (stored != null)
        {
            Array.Copy(stored, counters, Math.Min(stored.Length, counters.Length));
        }

        return counters;
    }

    /// <summary>落了什么。纯诊断用 —— 玩家报"这局什么都没拿到"时对得上账。</summary>
    public struct GrantSummary
    {
        public int Silver;
        public int SilverBalance;
        public int CharacterPoints;
        public int ResearchPoints;
        public int Stackables;
        public int Gadgets;
        public int Uniques;

        /// <summary>
        /// 重复的收藏品折成的账号经验。
        ///
        /// 只有这一项是 <c>Grant</c> **自己**入账的 —— 别的货币原厂已经加进报文
        /// (<c>RewardManager.AddDuplicatedCurrencies</c>, RewardManager.cs:116),
        /// 而报文里的货币上面几行已经落过库了; 账号经验则是另一条路进账的
        /// (<c>AwardAccountXp</c> 只落"任务目标挣的"那份), 报文里多出来的这一截
        /// 没人管, 所以在这里补。
        ///
        /// 两段加起来正好等于结算画面上显示的 <c>AccountExperience</c>。
        /// </summary>
        public int DuplicateExperience;

        /// <summary>
        /// 具体落了哪几样堆叠物。<c>Grant</c> 会往里加, 所以**取的时候可能是
        /// null** (default(GrantSummary) 那几条早退路径)。
        /// </summary>
        public List<string> Detail;

        public override string ToString()
        {
            string items = Detail != null && Detail.Count > 0
                ? " [" + string.Join(", ", Detail) + "]"
                : string.Empty;

            string duplicates = DuplicateExperience > 0
                ? $", 重复折算 +{DuplicateExperience} 经验"
                : string.Empty;

            return $"银 +{Silver} (余 {SilverBalance}), 点 +{CharacterPoints}, "
                   + $"研究点 +{ResearchPoints}, 堆叠物 {Stackables} 件{items}, "
                   + $"装置 {Gadgets} 个, 武器 {Uniques} 把{duplicates}";
        }
    }
}
