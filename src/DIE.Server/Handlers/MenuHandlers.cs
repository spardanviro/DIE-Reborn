using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;
using ConductorShop;
using DIE.Server.Data;
using DIE.Server.Net;
using GameProtocol;
using Serilog;
using StunTCP;

namespace DIE.Server.Handlers;

/// <summary>
/// 主菜单加载后客户端陆续发出的查询请求。
///
/// 这些请求在 Phase 1 之前都落到兜底 (回 UnrecognizedError), 客户端能存活但
/// 对应 UI 面板会报错。补齐它们是 Phase 1 的收尾。
///
/// 三个带 ClientRevision 的请求 (Shop / DropTable / RewardSetup) 必须回
/// "你已过期" + 一份**合法的空数据**, 而不是"你没过期"。
///
/// 这一点最初判断错了, 是实机跑出来的:
/// ShopHelper.ShopRequest 只在 response.ClientShopOutdated == true 时才调用
/// ShopDocument.LoadShopDocument(...) 给 CurrentShopDocument 赋值。回 false 的话
/// 客户端"保留自己的缓存" —— 但这个游戏停服后本地**根本没有缓存**, 商店文档
/// 从来只由服务端下发。于是 CurrentShopDocument 一直是 null,
/// 紧接着 LoadShopItems 里的 CurrentShopDocument.GetAllShopItemIDsImpl() 就 NRE。
///
/// 回 true + 空数组是安全的: LoadShopDocument 会解引用每个数组 (banners.OrderByDescending /
/// shopItems.ToDictionary / events.Length), 所以它们必须非 null —— 而反序列化把
/// varint 0 还原成空数组而非 null, 正好满足; 后续遍历长度为 0, 什么也不做。
/// </summary>
public sealed class MenuHandlers
{
    /// <summary>
    /// 我们这份 (空) 内容的版本号。非零且稳定。
    ///
    /// 客户端每 60 秒会重发一次 GetShopRequest (ShopHelper.Update)。用
    /// "客户端版本 != 我们的版本" 来判定是否过期, 这样只有第一次会真正加载,
    /// 之后都回"已是最新", 不会每分钟重建一遍商店文档。
    /// </summary>
    private const int ContentRevision = 1;

    /// <summary>
    /// 商店/掉落表的版本号 —— **跟着货架内容走**, 不是常量。
    ///
    /// ## 写死会怎样
    ///
    /// 客户端只在 <c>ClientShopOutdated</c> 为真时才重新加载商店文档
    /// (<c>ShopHelper</c> 每 60 秒拉一次, 版本没变就什么也不做)。而我们这几轮
    /// 一直在改货架 (加装置蓝图、加零件、去掉直售武器), 版本号却一直是 1 ——
    /// 客户端手里于是是一份**上个版本的商店文档**。
    ///
    /// 后果不止是"看到旧货": <c>GUI_NewShopBuyPopup</c> 下单时要读
    /// <c>ShopDocument.GetActiveDocument().Revision</c>, 文档和实际货架对不上
    /// 时这一步会出问题, 请求根本发不出来 —— 表现就是玩家报的"点购买一直转圈",
    /// 而服务端日志里连一条记录都没有。
    ///
    /// 取货架的**内容指纹**: 条目数 + 每档的 (ID, 两种价格) 累加。改了任何
    /// 一档价格或增删商品, 版本号就变, 客户端下一次轮询自然会重拉。
    ///
    /// 用 <c>Math.Abs</c> 并避开 0: 0 是客户端"我还没有任何版本"的初值,
    /// 撞上会让它以为自己已经是最新。
    /// </summary>
    private int ShopRevision
    {
        get
        {
            if (_shopRevision != 0)
            {
                return _shopRevision;
            }

            int hash = 17;

            foreach (ShopItemData item in _shop.Items)
            {
                hash = hash * 31 + item.ShopItemID;
                hash = hash * 31 + item.SilverPrice;
                hash = hash * 31 + item.CharacterPointPrice;
            }

            hash = hash * 31 + _shop.Items.Count;

            _shopRevision = Math.Abs(hash);
            if (_shopRevision == 0)
            {
                _shopRevision = 1;
            }

            return _shopRevision;
        }
    }

    private int _shopRevision;

    private readonly AccountStore _accounts;
    private readonly MatchStore _matches;
    private readonly ShopCatalog _shop;
    private readonly CraftingCatalog _catalog;

    public MenuHandlers(AccountStore accounts, MatchStore matches,
                        ShopCatalog shop = null, CraftingCatalog catalog = null)
    {
        _accounts = accounts;
        _matches = matches;
        _shop = shop ?? new ShopCatalog();
        _catalog = catalog;
    }

    /// <summary>
    /// 62 — 奖励/故事地图配置。
    /// 客户端 (CribStart.cs:714) 只在 ClientShopOutdated 为 true 时才调用
    /// StoryMapData.LoadData(revision, setup), 所以必须回 true 才能让
    /// StoryMapData._Data 建起来。空数组安全: LoadData 里是 setup.Length 循环。
    /// </summary>
    public void OnGetRewardSetup(GetRewardSetupRequest request, RequestLink link, Request<GetRewardSetupResponse> respond)
    {
        GetRewardSetupResponse response = default;
        response.ClientShopOutdated = request.ClientRevision != ContentRevision;
        response.Revision = ContentRevision;
        response.RewardData = StoryMapOverrides;
        respond.Respond(response);
    }

    /// <summary>
    /// 要盖掉客户端硬编码故事地图的那几个节点。
    ///
    /// ## 这条路是通的
    ///
    /// 客户端收到响应后 <c>if (ClientShopOutdated) StoryMapData.LoadData(...)</c>
    /// (CribStart.cs:736 / CribMain.cs:489)。而它发的请求**永远是**
    /// <c>new GetRewardSetupRequest(-1)</c> —— 写死的 -1, 所以 ClientShopOutdated
    /// 恒为真, LoadData 每次登录都跑。
    ///
    /// <c>LoadData</c> 只覆盖数组里出现的节点, 而且只改
    /// Rewards / RewardChoices / NumRewardChoicesAllowed / Unlocks 四项 ——
    /// <c>Requirements</c> (解锁前置) 它碰都不碰。所以只列节点 12 是安全的:
    /// 它硬编码那份本来就只有 Rewards, Unlocks 是 0。
    ///
    /// ## 目前只有一条
    ///
    /// 节点 12 的完成奖励。客户端写死 <c>Cash ×1,000,000</c>, 我们实发
    /// <see cref="StarterQuestGoldReward"/> —— 不盖的话面板会一直标着一百万。
    /// </summary>
    /// <remarks>
    /// 是属性而不是 <c>static readonly</c> 字段: 它读
    /// <see cref="StarterQuestGoldReward"/>, 而那个字段声明在本文件**后面** ——
    /// 静态字段按文本顺序初始化, 做成字段的话这里会读到还没初始化的 0,
    /// 面板上就显示 "Cash +0"。每次登录构一次, 代价可以忽略。
    /// </remarks>
    private static RewardSetupCollection[] StoryMapOverrides => new[]
    {
        new RewardSetupCollection
        {
            NodeType = StoryMapData.Node.StarterQuestAll,
            Rewards = new[]
            {
                new RewardSetupData
                {
                    Type = RewardType.Cash,
                    Amount = StarterQuestGoldReward,
                },
            },
            RewardChoices = Array.Empty<RewardSetupData>(),
            NumRewardChoicesAllowed = 0,

            // 客户端硬编码那份节点 12 没有 Unlocks, 照抄 —— 填错会让地图上
            // 后面几个节点跟着变。
            Unlocks = 0,
        },
    };

    /// <summary>
    /// 56 — 虚拟形象资源的下载地址。
    ///
    /// 返回空串。**注意: 空串并不等于"不下载"** —— 客户端照样会发起一次请求然后失败,
    /// 菜单进程日志里的 `Error downloading definition from url : &lt;url&gt; malformed`
    /// 就是这条。这是可以接受的, 因为失败路径是优雅的:
    /// <c>VanityItemLoader.DownloadDefinition</c> 的错误分支会 <c>TrySetResult</c>
    /// 一个空的 VanityItemDefinitions 并调 <c>onDone(..., false)</c>, 不会挂住调用方。
    ///
    /// (早先这里的注释写的是"填不通的地址会让请求一直挂着, 所以空串才是安全值"。
    ///  那是错的: 两种取值都是优雅失败, 上面的日志和代码路径都能证。)
    ///
    /// ## 已知副作用: 战斗进程的命令行参数错位
    ///
    /// 菜单进程在拉起战斗进程时**无条件**拼接 (<c>GameStartScript.cs:275</c>):
    ///
    ///   text += " +vanityurl " + CribMain.VanityURL;
    ///   if (CribMain.VanityHash != null) text += " +vanityhash " + hex;
    ///
    /// 我们两个值都是空的, 于是命令行成了 `+vanityurl  +vanityhash `,
    /// <c>+vanityurl</c> 的处理器把 <c>+vanityhash</c> 当成自己的值拿去下载 ——
    /// 战斗进程日志里的 `Could not resolve host: +vanityhash` 就是这么来的,
    /// 同时 <c>+vanityhash</c> 这个参数本身被吃掉了。
    ///
    /// **不修**: 那个 hash 只用于校验虚拟形象定义文件, 而我们本来就不提供该文件。
    /// 要真正消掉这条错误得自己起个 HTTP 端点供一份空定义, 为纯外观功能不值当。
    /// </summary>
    public void OnGetVanityUrl(GetVanityURLRequest request, RequestLink link, Request<GetVanityURLResponse> respond)
    {
        GetVanityURLResponse response = default;
        response.URL = string.Empty;
        response.HashedData = Array.Empty<byte>();
        respond.Respond(response);
    }

    /// <summary>30 — 掉落表。空掉落表, 但要让客户端把对象建起来。</summary>
    public void OnGetDropTable(GetDropTableRequest request, RequestLink link, Request<GetDropTableResponse> respond)
    {
        GetDropTableResponse response = default;
        response.ClientDropTableOutdated = request.ClientRevision != ShopRevision;
        response.Revision = ShopRevision;

        // **不能留空。** 空表会让 DropTableManager.AvailableItems 恒返回 0,
        // 而 InventoryManager.HasItem 判宝箱"已拥有"的条件是
        // AvailableItems <= 手上箱子数 —— 于是每种箱子都被判成"没东西可开",
        // 商店里全是 "no items available"。玩家实测报的就是这个。
        response.Data = new DropTableData { CacheBoxes = _dropTable };

        respond.Respond(response);
    }

    /// <summary>
    /// 24 — 商店。空商店, 但必须是**建起来的**空商店 (见类注释)。
    /// LoadShopDocument 会解引用每一个数组, 所以一律给 Array.Empty 而非 null。
    /// </summary>
    public void OnGetShop(GetShopRequest request, RequestLink link, Request<GetShopResponse> respond)
    {
        ShopSettings settings = default;
        settings.ShopURL = string.Empty;
        settings.Boosts = Array.Empty<ShopBoostSettings>();

        GetShopResponse response = default;
        response.ClientShopOutdated = request.ClientRevision != ShopRevision;
        response.Revision = ShopRevision;
        response.Events = Array.Empty<ShopEvent>();
        response.ShopItems = _shop.Items.ToArray();
        response.UserGroups = Array.Empty<UserGroup>();
        response.Banners = Array.Empty<Banner>();
        response.GoldPrices = Array.Empty<GoldPriceEntry>();
        response.GlobalPriceChanges = Array.Empty<GlobalPriceChange>();
        response.CurrencyEvents = Array.Empty<CurrencyEvent>();
        response.Settings = settings;
        // 客户端拿它算商店活动的倒计时, 必须是合法的 .NET tick 数
        response.ServerTime = DateTime.UtcNow.Ticks;
        respond.Respond(response);
    }

    /// <summary>
    /// 买东西。
    ///
    /// 货架四档 (见 <see cref="ShopCatalog"/>): 蓝图 / 宝箱 / 消耗品 / 角色。
    /// **没有直售武器** —— 武器只能在 workbench 用蓝图加零件造。
    ///
    /// ## 价格一律以服务端为准
    ///
    /// <see cref="BuyShopItem"/> 里客户端会把它以为的价格一起发上来。
    /// **不能信** —— 那是客户端可以随便改的数。这里只用 <c>ShopItemID</c>
    /// 去货架查真价。
    ///
    /// 对不上时回 <see cref="BuyItemResult.InvalidPrice"/> 并置
    /// <c>ShopOutdated</c> —— 既可能是有人改价, 也可能是它的商店缓存过期了,
    /// 让它重新拉一份对两种情况都合适。
    ///
    /// ## 先扣钱还是先给货
    ///
    /// 先算总价、查余额, **够了才动账**。余额不够整单拒绝, 不做部分成交 ——
    /// 客户端那边也是按整单处理的。
    ///
    /// 银币和角色点分开算: 角色那一档标的是角色点, 别的标银币, 两笔账各查各的。
    /// </summary>
    /// <summary>一档商品一次最多买几个。客户端界面上最多一次买一组零件 / 消耗品, 远用不到; 挡的是伪造的超大数量。</summary>
    public const int MaxAmountPerLine = 9999;

    public void OnBuyItem(BuyItemRequest request, RequestLink link, Request<BuyItemResponse> respond)
    {
        BuyItemResponse response = default;
        response.UniquesPurchased = Array.Empty<Unique>();
        response.StackablesPurchased = Array.Empty<Stackable>();
        response.GadgetsPurchased = Array.Empty<Gadget>();

        List<BuyShopItem> wanted = request.ShopItems ?? new List<BuyShopItem>();

        // 入口日志 —— 玩家报过"点购买一直转圈而服务端毫无记录", 有这一条才能
        // 分清"请求没到"和"到了但处理出错"。
        Log.Information("{Link} 购买请求: {Count} 档 (客户端商店版本 {Theirs}, 我们 {Ours})",
            link, wanted.Count, request.ShopRevision, ShopRevision);

        if (wanted.Count == 0)
        {
            response.Result = BuyItemResult.InvalidItem;
            respond.Respond(response);
            return;
        }

        // 1) 核对每一件, 同时按**服务端**的价格分别累计两种货币
        long silverTotal = 0;
        long pointsTotal = 0;

        List<(ShopCatalog.ShopEntry Entry, int Amount)> order =
            new List<(ShopCatalog.ShopEntry, int)>();

        foreach (BuyShopItem item in wanted)
        {
            if (!_shop.TryGetEntry(item.ShopItemID, out ShopCatalog.ShopEntry entry))
            {
                Log.Warning("{Link} 想买货架上没有的 {ShopItemId}", link, item.ShopItemID);
                response.Result = BuyItemResult.InvalidItem;
                response.ShopOutdated = true;
                respond.Respond(response);
                return;
            }

            // GoldPrice 一律要求 0: 商店这条路只收银币和角色点。
            //
            // 零件那几档货架上**是有金价的** (ShopCatalog.StockParts), 但那份价
            // 只给合成弹窗的现金按钮读 —— 零件在商店里根本没有分类, 也没有任何
            // 客户端代码从这里买它 (InventoryManager.Request_BuyParts 零调用者)。
            // 真有人拿金价走到这里, 说明是改过的包, 拒掉。
            if (item.SilverPrice != entry.SilverPrice
                || item.CharacterPointPrice != entry.CharacterPointPrice
                || item.GoldPrice != 0)
            {
                Log.Warning("{Link} 报的价 (银 {S}/点 {P}/金 {G}) 和货架 "
                            + "(银 {OS}/点 {OP}) 对不上, 商品 {ShopItemId}",
                    link, item.SilverPrice, item.CharacterPointPrice, item.GoldPrice,
                    entry.SilverPrice, entry.CharacterPointPrice, item.ShopItemID);

                response.Result = BuyItemResult.InvalidPrice;
                response.ShopOutdated = true;
                respond.Respond(response);
                return;
            }

            // 角色是一次性解锁, 买多份没有意义 —— 数量一律按 1 算。
            // 数量是客户端给的, 必须夹住: 以前不设上限, 单价 x 数量超过 int 之后下面 (int) 转成负数,
            // TrySpend 对 <= 0 的金额直接跳过 —— 等于白拿几百万件 (2026-09-17 审校发现)。
            if (item.Amount > MaxAmountPerLine)
            {
                Log.Warning("{Link} 一次想买 {Amount} 个 {ShopItemId}, 超过上限 {Max}, 拒绝",
                    link, item.Amount, item.ShopItemID, MaxAmountPerLine);
                response.Result = BuyItemResult.InvalidItem;
                respond.Respond(response);
                return;
            }

            int amount = entry.Type == ShopItemType.Character
                ? 1
                : Math.Max(1, item.Amount);

            silverTotal += (long)entry.SilverPrice * amount;
            pointsTotal += (long)entry.CharacterPointPrice * amount;

            order.Add((entry, amount));
        }

        // 2) 两种货币**一起**扣, 包在一个事务里 —— 银币扣成了但角色点不够的话
        //    整笔回滚 (见 AccountStore.TrySpend)。
        //
        //    上一版是先各查各的余额再分两次 AddCurrency。那有个真实的漏洞:
        //    AddCurrency 扣超了会停在 0 而不是失败, 于是余额在查和扣之间掉下去
        //    的话玩家照样拿到货, 只付了手上剩的那点。
        if (silverTotal > int.MaxValue || pointsTotal > int.MaxValue)
        {
            Log.Warning("{Link} 购买总价溢出 ({Silver} 银 / {Points} 点), 拒绝", link, silverTotal, pointsTotal);
            response.Result = BuyItemResult.InsufficientFunds;
            respond.Respond(response);
            return;
        }

        if (!_accounts.TrySpend(link.UserId, new[]
            {
                (AccountStore.CurrencyKind.Silver, (int)silverTotal),
                (AccountStore.CurrencyKind.CharacterPoints, (int)pointsTotal),
            }))
        {
            Account balance = null;
            try
            {
                balance = _accounts.Load(link.UserId);
            }
            catch (Exception)
            {
                // 只是为了日志好看, 读不到就算了
            }

            Log.Warning("{Link} 余额不够: 要 {Silver} 银 / {Points} 点, "
                        + "有 {HaveSilver} 银 / {HavePoints} 点",
                link, silverTotal, pointsTotal,
                balance?.Silver ?? 0, balance?.CharacterPoints ?? 0);

            response.Result = BuyItemResult.InsufficientFunds;
            respond.Respond(response);
            return;
        }

        // 3) 发货
        List<Stackable> stackables = new List<Stackable>();

        foreach ((ShopCatalog.ShopEntry entry, int amount) in order)
        {
            switch (entry.Type)
            {
                case ShopItemType.Blueprint:
                    stackables.Add(GrantStackable(
                        link.UserId, entry.ItemId, StackableType.Schematic, amount));
                    break;

                case ShopItemType.GadgetBlueprint:
                    stackables.Add(GrantStackable(
                        link.UserId, entry.ItemId, StackableType.GadgetBlueprint, amount));
                    break;

                case ShopItemType.Lockbox:
                    stackables.Add(GrantStackable(
                        link.UserId, entry.ItemId, StackableType.Lockbox, amount));
                    break;

                case ShopItemType.Keycode:
                    // 暗码 —— 开箱界面会自动按缺口下单, 所以这一档的 amount
                    // 常常大于 1。
                    stackables.Add(GrantStackable(
                        link.UserId, entry.ItemId, StackableType.Keycode, amount));
                    break;

                case ShopItemType.Part:
                    stackables.Add(GrantStackable(
                        link.UserId, entry.ItemId, StackableType.Part, amount));
                    break;

                case ShopItemType.Consumable:
                    stackables.Add(GrantStackable(
                        link.UserId, entry.ItemId, StackableType.Consumable, amount));
                    break;

                case ShopItemType.Character:
                    _accounts.UnlockCharacter(link.UserId, (CharacterEnum)entry.ItemId);
                    break;
            }
        }

        Log.Information("{Link} 购买 {Count} 档, 花费 {Silver} 银 / {Points} 点",
            link, order.Count, silverTotal, pointsTotal);

        response.Result = BuyItemResult.Success;
        response.StackablesPurchased = stackables;
        respond.Respond(response);
    }

    /// <summary>
    /// 开宝箱。
    ///
    /// 扣掉一个箱子, 按 <see cref="LockboxTable"/> 掉零件或改装图。
    ///
    /// ## 钥匙那一套不做
    ///
    /// <c>UnlockLockboxRequest.Keys</c> 和 <c>CostExpected</c> 是原厂的两种
    /// 开箱途径 (用钥匙 / 花钱)。私服的箱子本身就是银币买的, 再收一道等于
    /// 收两次钱 —— 所以一律当"箱子即钥匙", 请求里那两个字段忽略。
    ///
    /// ## 掉落也要发给客户端
    ///
    /// 响应里的 <c>Rewards</c> 是客户端弹开箱动画用的。库要写、这里也要填,
    /// 少一边就是"背包里有东西但没看见开出来"。
    ///
    /// ## 先尽量别开到已经有的
    ///
    /// 原厂开箱那条路给掉落表传的是 <c>duplicatesPossible: false</c> 加上
    /// "背包里已有的"清单 (RewardManager.cs:350), 落到 <c>GiveAutoGroup</c>
    /// 里就是一句"把已有的从候选池里滤掉再抽" (RewardManager.cs:793)。
    /// 下面的折算是**兜底**, 不是主路。
    ///
    /// ## 全收齐了就不让开
    ///
    /// 原厂给这种情况留了专门的结果码: <c>AlreadyOwnsAllBlueprints</c> /
    /// <c>AlreadyOwnsAllDesigns</c>, 客户端翻成 "All Blueprints already owned" /
    /// "All modifications already owned" (GUI_OpenLockBoxPopup.cs:260)。
    /// 所以收齐之后是**拒绝开箱**, 而不是开出一个空箱子 —— 箱子当然也不扣。
    ///
    /// 两条合起来, 开箱这条路上**不会掉出重复的东西**: 没收齐就只掉没有的,
    /// 收齐了压根不让开。所以原厂那个"重复折成货币"
    /// (<see cref="DuplicateCollectibles"/>) 在这里用不上 —— 它管的是局末奖励
    /// 和等级奖励那两条 (局末给掉落表传的是 <c>duplicatesPossible: true</c>,
    /// RewardManager.cs:110)。
    /// </summary>
    public void OnUnlockLockbox(UnlockLockboxRequest request, RequestLink link,
                                Request<UnlockLockboxResult> respond)
    {
        UnlockLockboxResult response = default;
        response.Rewards = new RewardCollectionData
        {
            Items = ItemCollection.NewItemCollection(),
        };

        if (request.Type == LockboxTypes.None)
        {
            response.Result = UnlockLockboxResultType.UnknownError;
            respond.Respond(response);
            return;
        }

        // 这一箱掉哪一类, 就把那一类已经有的喂给掉落表, 让它优先挑没有的。
        bool designBox = LockboxTable.IsDesignBox(request.Type);

        ushort[] owned = _accounts.LoadStackables(link.UserId)
            .Where(x => x.Type == (designBox ? StackableType.Design : StackableType.Schematic))
            .Select(x => x.ID)
            .ToArray();

        // 全收齐了就不让开 —— **必须排在扣箱子之前**, 不然玩家白丢一个箱子。
        if (!_lockboxes.HasAnythingNewFor(request.Type, owned))
        {
            Log.Information("{Link} 这个箱子 {Type} 能开的已经全有了 ({Owned} 张), 不扣箱子",
                link, request.Type, owned.Length);

            response.Result = designBox
                ? UnlockLockboxResultType.AlreadyOwnsAllDesigns
                : UnlockLockboxResultType.AlreadyOwnsAllBlueprints;
            respond.Respond(response);
            return;
        }

        // 有没有这个箱子 —— 扣不掉就是没有
        if (!_accounts.TryConsumeStackables(link.UserId,
                new[] { ((ushort)request.Type, StackableType.Lockbox, 1) }))
        {
            Log.Warning("{Link} 想开没有的箱子 {Type}", link, request.Type);
            response.Result = UnlockLockboxResultType.CacheBoxNotOwned;
            respond.Respond(response);
            return;
        }

        IReadOnlyList<LockboxTable.Drop> drops = _lockboxes.Open(request.Type, _random, owned);

        foreach (LockboxTable.Drop drop in drops)
        {
            int total = _accounts.AddStackable(
                link.UserId, drop.ItemId, drop.Type, drop.Amount);

            response.Rewards.Items.Stackables.Add(
                new Stackable(drop.ItemId, drop.Type, link.UserId, (ushort)total));
        }

        Log.Information("{Link} 开箱 {Type} -> {Count} 样 (开之前手上已有 {Owned} 张)",
            link, request.Type, drops.Count, owned.Length);

        response.Result = UnlockLockboxResultType.Succeeded;
        respond.Respond(response);
    }

    /// <summary>
    /// workbench 合成武器。
    ///
    /// 玩家定的流程: 蓝图在商店买, 零件靠开箱, 到这里合成。
    ///
    /// ## 判据全在服务端
    ///
    /// 客户端把它以为的成本 (<c>CostExpected</c>) 和零件账 (<c>PartData</c>)
    /// 一起发上来, 那些都不能信。这里只认三样:
    ///
    ///   图鉴里有没有这个 schematic          -&gt; InvalidSchematic
    ///   背包里有没有那张蓝图                -&gt; SchematicNotOwned
    ///   缺的零件铺不铺得出货架              -&gt; NotEnoughParts
    ///   手续费 + 缺料补买的钱够不够         -&gt; NotEnoughCurrencies
    ///
    /// 零件需求读的是 <c>ItemSchematic.ItemParts</c> —— 原厂数据, 不是我们编的。
    ///
    /// ## 缺零件不是失败, 是加钱
    ///
    /// 原厂的合成弹窗里, 缺的零件按商店价折进总价一起收 —— 详见
    /// <see cref="TryBillParts"/>。所以这里不再有"零件不够"这条失败路径, 只有
    /// "钱不够"。
    ///
    /// ## 扣料要么全扣要么不扣
    ///
    /// 蓝图和零件一起进 <c>TryConsumeStackables</c>, 中间任何一样不够都不动 ——
    /// 否则玩家的材料会少掉一半而武器没造出来。
    ///
    /// ## 合成时顺带装改装图 (已做)
    ///
    /// 客户端的合成弹窗允许在造之前就往槽里放改装图, 那部分绑定费也算进它显示的总价里
    /// (<c>GUI_CraftCostTable</c> 里 <c>GetDesignBindGPCost</c> 那一段)。我们两边都跟上了:
    /// <see cref="TryBillDesigns"/> 按同一套算法收绑定费并把图算进要扣的料,
    /// <see cref="WithDesigns"/> 把它们写进新武器的 Slots blob (客户端自己不会补装,
    /// <c>DoCraftSuccess</c> 只做特效和扣钱)。
    /// </summary>
    public void OnCraftItem(CraftItemRequest request, RequestLink link,
                            Request<CraftingResult> respond)
    {
        CraftingResult response = default;

        ItemSchematic schematic = CraftingManager.GetWeaponSchematic(request.SchematicID);
        if (schematic == null)
        {
            response.Result = CraftingResultType.InvalidSchematic;
            respond.Respond(response);
            return;
        }

        // 看蓝图有没有 —— 好把"没蓝图"和"零件不够"分开报, 两种情况玩家该做的事
        // 完全不同 (去商店 vs 去开箱)。**只查不扣**: 图纸和改装图一样是收藏品,
        // 造一把不会少一张, 判据见 OnAddDesignsToWeapon 那一大段。
        if (_accounts.CountStackable(link.UserId, request.SchematicID,
                                     StackableType.Schematic) < 1)
        {
            response.Result = CraftingResultType.SchematicNotOwned;
            respond.Respond(response);
            return;
        }

        // 玩家点的是齿轮还是现金按钮 —— 三笔钱都按那一档算。
        CraftCurrency currency = CurrencyOf(request.CostExpected);

        // 手续费。原厂按品阶收 (银 T1 50 -> T16 45000; 金 T1 5 -> T16 4090),
        // 数在 CraftingManager 里 —— 见 CraftingFee 的说明。
        int fee = CraftingFee(
            currency == CraftCurrency.Gold
                ? CraftingManager.GetWeaponCraftingCashFee(request.SchematicID)
                : CraftingManager.GetWeaponCraftingFee(request.SchematicID),
            link, schematic.Name);

        if (!TryBillParts(link, schematic.ItemParts, out PartBill bill))
        {
            response.Result = CraftingResultType.NotEnoughParts;
            respond.Respond(response);
            return;
        }

        SlotData[] designs = request.DesignSlots ?? Array.Empty<SlotData>();

        // CraftingResult 没有 SlotIndex 字段 (那是改装那条响应上的), 出错的槽号
        // 只能进日志 —— TryBillDesigns 里已经打了。
        if (!TryBillDesigns(link, schematic, designs, currency, out long designPrice,
                out response.Result, out _))
        {
            respond.Respond(response);
            return;
        }

        // 要扣的只有零件 —— 图纸和改装图都是收藏品, 不消耗。
        int partKinds = bill.Consume.Count;

        List<(ushort, StackableType, int)> cost = bill.Consume;

        // **先扣钱**, 而且是原子的 (见 AccountStore.TrySpend)。
        // 扣不动就到此为止, 零件一个没动。
        int charged = fee + bill.PriceIn(currency) + (int)designPrice;

        if (!TrySpend(link, currency, charged, out response.Result))
        {
            respond.Respond(response);
            return;
        }

        if (!_accounts.TryConsumeStackables(link.UserId, cost))
        {
            // 零件在上面逐个查过了, 走到这里说明中间有人动过库存。
            _accounts.Refund(link.UserId, KindOf(currency), charged);

            response.Result = CraftingResultType.NotEnoughParts;
            respond.Respond(response);
            return;
        }

        Unique? created = _catalog?.CreateWeapon(request.SchematicID, link.UserId);
        if (created == null)
        {
            // 钱和料都扣了却造不出来 —— 两样都退回去, 不能让玩家白亏。
            _accounts.Refund(link.UserId, KindOf(currency), charged);

            foreach ((ushort id, StackableType type, int amount) in cost)
            {
                _accounts.AddStackable(link.UserId, id, type, amount);
            }

            response.Result = CraftingResultType.UnknownError;
            respond.Respond(response);
            return;
        }

        // 改装图直接写进新武器的 Slots blob —— 客户端读的就是这份编码,
        // 它自己不会补装 (DoCraftSuccess 只做特效和扣钱)。
        Unique weapon = WithDesigns(created.Value, designs);

        _accounts.AddUnique(link.UserId, weapon);

        MarkCraftingQuestDone(link, request.StarterQuest);

        // 占位符和参数一一对应 —— 原来 12 个坑喂 10 个值, 打出来是
        // "改装图  [T6 Alloy x125, ...]0{Bought}" 这种读不懂的东西。
        Log.Information("{Link} 合成武器 {Name}({Id}), 吃掉 {Parts} 种零件, 装 {Designs} 张改装图, "
                        + "手续费 {Fee}, 缺料补买 {Buy}, 绑定费 {Bind} ({Unit}){Bought}",
            link, schematic.Name, request.SchematicID,
            partKinds, designs.Length,
            fee, bill.PriceIn(currency), designPrice, NameOf(currency), bill.Describe());

        response.Result = CraftingResultType.Succeeded;
        response.CraftedItem = weapon;
        respond.Respond(response);
    }

    /// <summary>
    /// 把改装图 (Design) 装到一件武器上。
    ///
    /// ## 客户端怎么发
    ///
    /// <c>GUI_WeaponCraftPopup.ModifyButton_OnButtonClick</c> -&gt;
    /// <c>InventoryManager.Request_AddDesignsToWeapon(guid, slotData, cost, ...)</c>。
    /// 一次可以装**多个槽**: <c>SlotData[] { (SlotIndex, PerkID) }</c>。
    ///
    /// 成功之后客户端只做表现 (<c>WeaponItemInfo.UpdateSlots</c> 改本地字典),
    /// 也不动自己的改装图库存 —— **因为原厂根本就不扣**, 见下。
    ///
    /// ## 改装图是收藏品, 装上去不消耗 (2026-09-22 修)
    ///
    /// 玩家报"镶嵌后重新进入游戏会发现镶嵌的 modification 消失了", 顺带还有
    /// "模块图标不显示 / 显示得过大"。三个症状同一个根。
    ///
    /// 客户端画已装的图走这一段 (<c>GUI_WeaponDesignSlotsCollection.SetData</c>):
    ///
    /// <code>
    /// DesignItemInfo design = InventoryManager.GetDesign(slots[n]);   // 在**背包**里找
    /// if (design != null) SetAppliedDesign(...);                      // 画出来
    /// else                Clear(n, ...);                              // 画成空槽
    /// </code>
    ///
    /// 而 <c>InventoryManager.GetDesign</c> 翻的是 <c>_InventoryItemList</c> ——
    /// 登录时服务端下发的那份库存。我们把图扣掉了, 库存里就没有了, 于是每次
    /// 重新登录所有已装的槽统统走 <c>Clear</c> 那一支 = 空槽。
    ///
    /// ## 判据: 改装图压根没有"数量"这个概念
    ///
    ///   1. <c>DesignItemInfo</c> 只存一个 <c>ItemSchematicUpgrade</c>, **没有数量字段**;
    ///      <c>CreateInventoryItemInfo</c> 建它的时候把 <c>Stackable.Amount</c> 整个扔掉
    ///      (零件和消耗品则是 <c>new PartItemInfo(part, stackable.Amount)</c>)。
    ///   2. 原厂结算引擎 <c>RewardManager.AddDuplicatedCurrencies</c>: 开出重复的
    ///      改装图 / 图纸 / 装置蓝图一律折成货币 (<c>GetCurrencyFromDuplicate</c>) ——
    ///      也就是说一个账号**永远不可能有两张同样的图**, 那"扣一张"就无从谈起。
    ///   3. 花的钱叫 <c>GetDesignBind*Cost</c> —— **绑定**费。图是收藏, 每次绑进
    ///      一个槽收一次钱, 钱才是消耗品。
    ///
    /// 所以这里只查"有没有", 一张不扣。同一张图也就能同时装进好几个槽。
    ///
    /// 2026-09-22 之前装上去的图已经被扣掉了, 库里对不上 —— 开服时由
    /// <c>AccountStore.RestoreBoundCollectibles</c> 一次性补回来。
    ///
    /// ## 槽位存在武器的 Slots blob 里
    ///
    /// <c>Unique.Slots</c> 是一段字节, 编解码用原厂的
    /// <c>Unique.Serialize/Deserialize(ref Message, ref List&lt;UniqueSlot&gt;)</c>。
    /// 一个字节都不手写 —— 客户端读的就是这份编码
    /// (<c>WeaponItemInfo.cs:205</c> 那段)。
    ///
    /// ## 校验顺序
    ///
    /// 一条一条查完再动账, 中途任何一条不过就整单退回。原厂的
    /// <see cref="CraftingResultType"/> 把失败原因分得很细 (槽不存在 / 图不存在 /
    /// 品类不匹配 / 没这张图 / 钱不够), 客户端会把枚举名直接显示出来 ——
    /// 所以要报准, 不能一律 UnknownError。
    ///
    /// ## 品类规则
    ///
    /// <c>CraftingManager.IsAllowedDesignCategory(slotCategory, designCategory)</c>:
    /// 低品类的图能装进高品类的槽, 反过来不行。这条是原厂的, 直接调。
    /// </summary>
    public void OnAddDesignsToWeapon(AddDesignsToWeaponRequest request, RequestLink link,
                                     Request<AddDesignToWeaponResponse> respond)
    {
        AddDesignToWeaponResponse response = default;
        response.SlotIndex = -1;

        SlotData[] wanted = request.DesignSlots ?? Array.Empty<SlotData>();

        Log.Information("{Link} 装改装图: {Count} 个槽", link, wanted.Length);

        if (wanted.Length == 0)
        {
            response.Result = CraftingResultType.UnknownError;
            respond.Respond(response);
            return;
        }

        var found = _accounts.FindUnique(link.UserId, request.WeaponGUID);
        if (found == null)
        {
            Log.Warning("{Link} 想改一件不属于它的武器", link);
            response.Result = CraftingResultType.WeaponNotOwned;
            respond.Respond(response);
            return;
        }

        (long instanceId, Unique weapon) = found.Value;

        ItemSchematic schematic = CraftingManager.GetWeaponSchematic(weapon.SchematicID);
        if (schematic == null)
        {
            response.Result = CraftingResultType.InvalidSchematic;
            respond.Respond(response);
            return;
        }

        CraftCurrency currency = CurrencyOf(request.CostExpected);

        // 1) 一条一条查, 顺便把绑定费加起来 —— 和合成那条同一套 (见 TryBillDesigns)。
        //    图**不扣**, 所以这里没有"要消耗什么"这一项。
        if (!TryBillDesigns(link, schematic, wanted, currency, out long price,
                out response.Result, out response.SlotIndex))
        {
            respond.Respond(response);
            return;
        }

        // 2) 收绑定费, **原子的** (见 AccountStore.TrySpend)。
        //    价格一律以服务端为准 —— 请求里的 CostExpected 只用来看玩家点了
        //    哪个按钮, 里面的数是客户端可以随便改的。
        if (!TrySpend(link, currency, (int)price, out response.Result))
        {
            respond.Respond(response);
            return;
        }

        // 3) 改槽位并写回
        weapon = WithDesigns(weapon, wanted);
        _accounts.UpdateUnique(instanceId, weapon);

        Log.Information("{Link} 武器 {Name} 装上 {Count} 张改装图, 绑定费 {Cost}{Unit} (图不消耗)",
            link, schematic.Name, wanted.Length, price, NameOf(currency));

        response.Result = CraftingResultType.Succeeded;
        respond.Respond(response);
    }

    /// <summary>
    /// 槽位表的编解码挪到了 <see cref="CraftingCatalog"/> —— 开服时的补发
    /// (<c>AccountStore.RestoreBoundCollectibles</c>) 也要读它。
    /// </summary>
    private static List<UniqueSlot> DecodeSlots(byte[] blob) =>
        CraftingCatalog.DecodeSlots(blob);

    /// <inheritdoc cref="CraftingCatalog.EncodeSlots"/>
    private static byte[] EncodeSlots(List<UniqueSlot> slots) =>
        CraftingCatalog.EncodeSlots(slots);

    /// <summary>
    /// workbench 合成装置。
    ///
    /// 和武器合成 (<see cref="OnCraftItem"/>) 同一条链, 三处不同:
    ///
    ///   蓝图类型  <c>StackableType.GadgetBlueprint</c>, 不是 <c>Schematic</c>
    ///   零件需求  读 <c>StructuredGadget.Parts</c> —— **请求里不带**,
    ///             <c>CraftGadgetRequest</c> 只有 SchematicID + CostExpected
    ///   产出      属性是**随机生成**的 (<c>Gadget.GenerateTrinket</c>),
    ///             武器不是
    ///
    /// 最后一条是关键: 装置的词条槽由图鉴上的 Stats/Perks 数量决定, 值现掷。
    /// 所以不能自己拼字节 —— 拼出来的客户端解不了。
    /// </summary>
    public void OnCraftGadget(CraftGadgetRequest request, RequestLink link,
                              Request<CraftingResult> respond)
    {
        CraftingResult response = default;

        StructuredGadget schematic = CraftingManager.GetTrinket(request.SchematicID);
        if (schematic == null)
        {
            response.Result = CraftingResultType.InvalidSchematic;
            respond.Respond(response);
            return;
        }

        // 同武器那条: **只查不扣**, 装置蓝图也是收藏品。
        if (_accounts.CountStackable(link.UserId, request.SchematicID,
                                     StackableType.GadgetBlueprint) < 1)
        {
            response.Result = CraftingResultType.SchematicNotOwned;
            respond.Respond(response);
            return;
        }

        if (!TryBillParts(link, schematic.Parts, out PartBill bill))
        {
            response.Result = CraftingResultType.NotEnoughParts;
            respond.Respond(response);
            return;
        }

        int partKinds = bill.Consume.Count;

        List<(ushort, StackableType, int)> cost = bill.Consume;

        // 玩家点的是齿轮还是现金按钮。
        CraftCurrency currency = CurrencyOf(request.CostExpected);

        // 手续费。装置按稀有度收 (银: 稀有 4000 / 史诗 8000 / 传奇 16000;
        // 金: 365 / 725 / 1455)。装置没有改装图槽, 所以只有两笔钱。
        int fee = CraftingFee(
            currency == CraftCurrency.Gold
                ? CraftingManager.GetGadgetCraftingFeeCash(request.SchematicID)
                : CraftingManager.GetGadgetCraftingFee(request.SchematicID),
            link, schematic.WorkingName);

        // 和武器那条一样: 先原子扣钱, 之后每一步失败都退。
        int charged = fee + bill.PriceIn(currency);

        if (!TrySpend(link, currency, charged, out response.Result))
        {
            respond.Respond(response);
            return;
        }

        if (!_accounts.TryConsumeStackables(link.UserId, cost))
        {
            _accounts.Refund(link.UserId, KindOf(currency), charged);

            response.Result = CraftingResultType.NotEnoughParts;
            respond.Respond(response);
            return;
        }

        Gadget? gadget = _catalog?.CreateGadget(request.SchematicID, link.UserId);
        if (gadget == null)
        {
            // 钱和料都扣了却造不出来 —— 两样都退回去。
            _accounts.Refund(link.UserId, KindOf(currency), charged);

            foreach ((ushort id, StackableType type, int amount) in cost)
            {
                _accounts.AddStackable(link.UserId, id, type, amount);
            }

            response.Result = CraftingResultType.UnknownError;
            respond.Respond(response);
            return;
        }

        _accounts.AddGadget(link.UserId, gadget.Value);

        Log.Information("{Link} 合成装置 {Name}({Id}), 吃掉 {Parts} 种零件, "
                        + "手续费 {Fee}{Unit}, 缺料补买 {Buy}{Unit}{Bought}",
            link, schematic.WorkingName, request.SchematicID, partKinds,
            fee, NameOf(currency), bill.PriceIn(currency), bill.Describe());

        response.Result = CraftingResultType.Succeeded;
        response.CraftedGadget = gadget.Value;
        respond.Respond(response);
    }

    private readonly LockboxTable _lockboxes = new LockboxTable();

    /// <summary>
    /// 下发给客户端的宝箱掉落表。
    ///
    /// 建一次存着 —— 客户端每次进商店都会拉, 而内容只取决于图鉴 (不变)。
    /// </summary>
    private readonly IReadOnlyList<ConductorCrafting.CacheBox> _dropTable =
        new DropTableBuilder().Build();

    /// <summary>
    /// 开箱用的随机源。
    ///
    /// 整个进程一个实例 —— 每次开箱 <c>new Random()</c> 的话, 连着开几个箱子
    /// 会因为种子取自同一毫秒而掉出一模一样的东西。
    ///
    /// 用 <c>Random.Shared</c> 而不是自己 new 一个: 请求在 TCP 线程池上并发处理, 普通 <c>Random</c>
    /// 不是线程安全的, 并发调用会把内部状态搞坏 (之后恒返回 0, 开箱永远开出同一样)。
    /// </summary>
    private static Random _random => Random.Shared;

    /// <summary>
    /// 往背包里加一种可堆叠物, 返回加完之后那一堆 —— 客户端要拿它更新 UI。
    /// </summary>
    /// <summary>
    /// 把原厂的手续费换算成能收的数。
    ///
    /// ## 为什么要过一道
    ///
    /// <c>CraftingManager.GetWeaponCraftingFee</c> / <c>GetGadgetCraftingFee</c>
    /// 查不到东西时返回的是 <c>999999u</c> 这个哨兵值, 不是 0。照收的话玩家会
    /// 被莫名其妙扣掉一百万银币, 而且报的是"钱不够"这种毫无线索的错。
    ///
    /// 所以哨兵值一律当 0 处理: 图鉴里查不到的东西根本不该走到这一步
    /// (调用方在前面已经校验过 schematic 存在), 真走到了也宁可少收不乱收。
    /// </summary>
    /// <summary>
    /// 把原厂的手续费换算成能收的数。
    ///
    /// ## 哨兵值
    ///
    /// <c>CraftingManager.GetWeaponCraftingFee</c> / <c>GetGadgetCraftingFee</c>
    /// 查不到东西时返回的是 <see cref="UnknownCraftingFee"/> 这个哨兵值, 不是 0。
    /// 照收的话玩家会被扣掉一百万银币, 而且报的是"钱不够"这种毫无线索的错。
    ///
    /// 所以当 0 处理 —— 宁可少收不乱收。但要**吼一声**: 调用方在前面已经校验过
    /// schematic 存在, 走到这里说明原厂那两个函数和图鉴对不上了, 那是数据问题,
    /// 静默放行等于让玩家白造。这一整轮反复踩的就是"零报错"那一类坑。
    ///
    /// 合法费率最高是 T16 的 45000, 离哨兵值很远, 不会误伤。
    /// </summary>
    private static int CraftingFee(uint stockFee, RequestLink link, string what)
    {
        if (stockFee < UnknownCraftingFee)
        {
            return (int)stockFee;
        }

        Log.Warning("{Link} 查不到 {What} 的合成手续费 (原厂返回哨兵值 {Sentinel}), "
                    + "这一次按免费处理 —— 图鉴和 CraftingManager 对不上了",
            link, what, stockFee);

        return 0;
    }

    /// <summary>原厂那两个查询函数查不到时返回的哨兵值。</summary>
    private const uint UnknownCraftingFee = 999999u;

    /// <summary>
    /// 扣手续费。
    ///
    /// ## 为什么是"扣"而不是"先查再扣"
    ///
    /// 上一版是 <c>CanAfford</c> 查一次余额, 中间隔着扣零件和造武器, 最后才
    /// <c>AddCurrency(-fee)</c>。那有个真实的漏洞: <c>AddCurrency</c> 是
    /// <c>MAX(amount + delta, 0)</c>, **扣超了自动停在 0 而不是失败**。余额在
    /// 这中间掉下去的话 (局末结算在战斗服线程上写货币, 大厅这边同时能买东西),
    /// 玩家照样拿到武器, 只付了手上剩的那点 —— 而且零报错。
    ///
    /// 改成 <see cref="AccountStore.TrySpend"/>: 查和扣是同一条 SQL 的
    /// <c>WHERE amount &gt;= $n</c>, 扣不动就一分不动。
    ///
    /// 代价是钱得在动零件**之前**扣, 于是后面每一步失败都要退钱。那几条退路
    /// 是显式的, 比"悄悄少收"好查得多。
    /// </summary>
    private bool TrySpend(RequestLink link, CraftCurrency currency, int amount,
                          out CraftingResultType failure)
    {
        failure = CraftingResultType.Succeeded;

        if (amount <= 0)
        {
            return true;
        }

        if (_accounts.TrySpend(link.UserId, KindOf(currency), amount))
        {
            return true;
        }

        // 余额只在失败时读一次 —— 纯粹为了日志好看, 不参与判断。
        // "要" 这个数是手续费 + 缺料补买 + 改装图绑定费的和, 不只是手续费。
        Log.Warning("{Link} 合成的钱不够: 要 {Need}{Unit}, 有 {Have}",
            link, amount, NameOf(currency), BalanceIn(link.UserId, currency));

        failure = CraftingResultType.NotEnoughCurrencies;
        return false;
    }

    /// <summary>
    /// 这一单用什么结账。
    ///
    /// 合成弹窗有两个按钮 —— 齿轮 (银币) 和现金 (金币), 点哪个就把哪一档的
    /// 总价填进 <c>CostExpected</c>, 另一档留 0。原厂每一项收费都备了两份价:
    /// <c>GetWeaponCraftingFee</c> / <c>GetWeaponCraftingCashFee</c>、
    /// <c>GetDesignBindGPCost</c> / <c>GetDesignBindCashCost</c>, 零件则是货架上
    /// 的 SilverPrice / GoldPrice。
    ///
    /// 我们只信"玩家点了哪个按钮"这一个位, **价钱一律服务端自己算** ——
    /// CostExpected 里的数是客户端可以随便改的。
    /// </summary>
    private enum CraftCurrency
    {
        Silver,
        Gold,
    }

    /// <summary>玩家点的是哪个按钮。</summary>
    private static CraftCurrency CurrencyOf(CurrencyCollection expected) =>
        expected.Gold > 0 ? CraftCurrency.Gold : CraftCurrency.Silver;

    private static AccountStore.CurrencyKind KindOf(CraftCurrency currency) =>
        currency == CraftCurrency.Gold
            ? AccountStore.CurrencyKind.Gold
            : AccountStore.CurrencyKind.Silver;

    private static string NameOf(CraftCurrency currency) =>
        currency == CraftCurrency.Gold ? "金" : "银";

    /// <summary>
    /// 校验一组改装图能不能装到这件武器上, 顺便攒出要扣的图和要收的钱。
    ///
    /// ## 合成的时候就能装
    ///
    /// <c>GUI_WeaponCraftPopup.SetCraftStep1Stuff</c> 里
    /// <c>DesignSlotCollection.SetData(schematic, interactable: true)</c> ——
    /// 造之前那几个槽就是可点的。选完图之后 <c>GUI_CraftCostTable</c> 把每张图的
    /// 绑定费加进合成按钮上显示的总价, 点下去时 <c>DesignSlots</c> 跟着
    /// <c>CraftItemRequest</c> 一起发上来。
    ///
    /// 我们原来在 <c>OnCraftItem</c> 里**整个忽略**这个字段, 后果是三件事叠一起:
    /// 少收那笔绑定费、武器回来是光板的、而客户端已经按含绑定费的数扣了本地
    /// 余额 (<c>DoCraftSuccess</c> 里 <c>AddSilverChange(-currencyCollection.Silver)</c>),
    /// 于是屏幕上的钱比服务端实际扣的多掉一截, 直到下次刷新账号才跳回来。
    ///
    /// ## 为什么在这里就查库存
    ///
    /// 原来是靠 <c>TryConsumeStackables</c> 扣不动来判"没这张图"。合成那条路上
    /// 零件和改装图会并进同一次扣料, 失败了就分不清是哪一样 —— 所以改成先查,
    /// 报得准。
    ///
    /// ## 改装图**不消耗** (2026-09-22 改回原厂)
    ///
    /// 详见 <see cref="OnAddDesignsToWeapon"/> 里那一大段。这里只查"有没有",
    /// 查到了就照收绑定费, 一张不扣。所以同一张图也能同时装进好几个槽。
    /// </summary>
    /// <returns>全部能装且都有货就 true; 否则 false 并填好 <paramref name="failure"/>。</returns>
    private bool TryBillDesigns(RequestLink link, ItemSchematic schematic, SlotData[] wanted,
                                CraftCurrency currency,
                                out long price,
                                out CraftingResultType failure,
                                out int failedSlot)
    {
        price = 0;
        failure = CraftingResultType.Succeeded;
        failedSlot = 0;

        if (wanted == null || wanted.Length == 0)
        {
            return true;
        }

        DesignSlot[] slots = schematic.Slots ?? Array.Empty<DesignSlot>();

        foreach (SlotData slot in wanted)
        {
            failedSlot = slot.SlotIndex;

            if (slot.SlotIndex >= slots.Length)
            {
                Log.Warning("{Link} 武器 {Schematic} 只有 {Have} 个槽, 却要装到第 {Want} 个",
                    link, schematic.Name, slots.Length, slot.SlotIndex);

                failure = CraftingResultType.WeaponSlotDontExist;
                return false;
            }

            ItemSchematicUpgrade design = CraftingManager.GetPerk(slot.PerkID);
            if (design == null)
            {
                failure = CraftingResultType.DesignPerkDontExist;
                return false;
            }

            if (!CraftingManager.IsAllowedDesignCategory(
                    slots[slot.SlotIndex].Category, design.Category))
            {
                Log.Warning("{Link} 品类不匹配: 第 {Slot} 槽是 {SlotCategory}, 图是 {DesignCategory}",
                    link, slot.SlotIndex, slots[slot.SlotIndex].Category, design.Category);

                failure = CraftingResultType.NotAllowedDesignCategory;
                return false;
            }

            // 有没有这张图 —— 收藏品意义上的"有", 不看数量 (装了也不会少)
            if (_accounts.CountStackable(link.UserId, slot.PerkID, StackableType.Design) < 1)
            {
                Log.Warning("{Link} 手上没有改装图 {Perk}", link, slot.PerkID);

                failure = CraftingResultType.DesignNotOwned;
                return false;
            }

            price += currency == CraftCurrency.Gold
                ? CraftingManager.GetDesignBindCashCost(design.Category)
                : CraftingManager.GetDesignBindGPCost(design.Category);
        }

        failedSlot = 0;
        return true;
    }

    /// <summary>
    /// 造完武器就把新手任务第二项 (节点 6) 记上。
    ///
    /// ## 这一项没有独立的解锁请求
    ///
    /// 别的几项都有: 节点 3 和 12 是 <c>GUI_StarterQuestPopup</c> 直接发
    /// <c>UnlockStoryMapNodeRequest</c>, 节点 10 是 <c>GUI_NewShopBuyPopup</c> 发
    /// <c>AccountDataManager.UnlockRewardRequest</c>。
    ///
    /// 武器合成这条 (<c>GUI_WeaponCraftPopup.OnCraftSuccess</c>) 只调
    /// <c>AccountDataManager.UnlockNode(6)</c> —— 那个方法**纯本地**, 只改
    /// <c>_StoryMap.UnlockReward(p)</c> 这份内存状态, 一个字节都不往服务端发。
    /// 它不需要发, 因为 <c>CraftItemRequest</c> 自带了 <c>StarterQuest</c> 这个位:
    /// 合成请求本身就是通知。
    ///
    /// 我们把那个位整个忽略了, 于是玩家看到的是: 造完武器面板跳到下一项,
    /// 重进游戏又退回"制造武器" —— 因为服务端那份 story_map_data 里从来没有
    /// 节点 6, 而登录时下发的就是服务端那份。
    ///
    /// (装置那条不一样: <c>CraftGadgetRequest</c> 没有这个字段, 所以
    ///  <c>GUI_WorkBenchCraftingConfirmationPopup</c> 自己发 UnlockRewardRequest,
    ///  走的是 <c>OnUnlockStoryMapNode</c>。)
    ///
    /// ## 为什么不只在 flag 为真时记
    ///
    /// 那个位是客户端给的, 而任务本身就是"造一把武器" —— 造成了就是达成了,
    /// 没有别的判据可查。<c>AdvanceStoryMapNode</c> 到顶就不再加, 重复调无害。
    /// flag 只进日志, 用来对照客户端认为自己在不在教程流程里。
    /// </summary>
    private void MarkCraftingQuestDone(RequestLink link, bool clientSaysStarterQuest)
    {
        byte times = _accounts.AdvanceStoryMapNode(
            link.UserId, StarterQuests.Crafting,
            StarterQuests.TimesRequired(StarterQuests.Crafting));

        Log.Information("{Link} 新手任务 制造武器 -> {Times} 次 (客户端标记 StarterQuest={Flag})",
            link, times, clientSaysStarterQuest);
    }

    /// <summary>
    /// 把一组改装图写进武器的 <c>Slots</c> blob。
    ///
    /// 覆盖同一个槽时原来那张图**不退**, 和原版一致 —— 客户端的
    /// <c>UpdateSlots</c> 也是直接改字典, 没有"取回旧图"这回事。
    /// </summary>
    private Unique WithDesigns(Unique weapon, SlotData[] wanted)
    {
        List<UniqueSlot> current = DecodeSlots(weapon.Slots);

        foreach (SlotData slot in wanted ?? Array.Empty<SlotData>())
        {
            UniqueSlot replacement = new UniqueSlot
            {
                ID = slot.SlotIndex,
                PerkID = slot.PerkID,
            };

            int at = current.FindIndex(u => u.ID == slot.SlotIndex);

            if (at >= 0)
            {
                current[at] = replacement;
            }
            else
            {
                current.Add(replacement);
            }
        }

        weapon.Slots = EncodeSlots(current);
        return weapon;
    }

    /// <summary>
    /// 合成时那笔零件账 —— 手上的直接吃, 缺的按商店价补买。
    /// </summary>
    /// <param name="Consume">要从背包里扣掉的那部分 (只含**真有**的量)。</param>
    /// <param name="PurchaseSilver">缺料补买的银价, 加进手续费一起扣。</param>
    /// <param name="PurchaseGold">同上, 现金那一档的价。</param>
    /// <param name="Bought">补买了哪几样, 只给日志用。</param>
    private readonly record struct PartBill(
        List<(ushort Id, StackableType Type, int Amount)> Consume,
        int PurchaseSilver,
        int PurchaseGold,
        List<string> Bought)
    {
        /// <summary>按结账币种取那一档的价。</summary>
        public int PriceIn(CraftCurrency currency) =>
            currency == CraftCurrency.Gold ? PurchaseGold : PurchaseSilver;

        /// <summary>日志尾巴: 没补买就是空串, 补了就列出来。</summary>
        public string Describe() =>
            Bought == null || Bought.Count == 0
                ? string.Empty
                : " [" + string.Join(", ", Bought) + "]";
    }

    /// <summary>
    /// 算合成要吃掉哪些零件、还要替玩家补买多少。
    ///
    /// ## 原厂就是"缺料自动买"
    ///
    /// 合成弹窗的总价不是光收手续费 (<c>GUI_CraftCostTable.Set</c> /
    /// <c>GUI_WorkBenchCraftingConfirmationPopup.GetMissingPartsTotalCost</c>):
    ///
    ///     总价 = 合成手续费
    ///          + Σ (缺量 × 该零件的商店单价)
    ///          + Σ 选中改装图的绑定费          &lt;- 只有武器那条有
    ///
    /// 缺量 = <c>max(0, 需求 - 手上有的)</c>。玩家点下去之后客户端只把
    /// **手上那部分**从本地库存里减掉 (<c>HandleCraftingResult_*</c> 里
    /// <c>RemoveParts(usedParts)</c>), 缺的那部分它当作"这一单顺便买了"。
    ///
    /// 也就是说零件根本没有单独的购买入口 —— <c>InventoryManager.Request_BuyParts</c>
    /// 在整个大厅里**零调用者**。补买只发生在合成这一刻。
    ///
    /// 我们原来只扣手续费, 然后拿**完整需求量**去 TryConsumeStackables, 差一个
    /// 就整单失败。玩家看到的是一个标题"An unrecognized error has occured."、
    /// 正文写着 <c>NotEnoughParts</c> 的弹窗 (<c>GUI_WeaponCraftPopup.OnCraftError</c>
    /// 把枚举名直接当正文显示) —— 明明弹窗上标的价里已经含了这些零件的钱。
    ///
    /// ## 为什么不信请求里带的那份
    ///
    /// <c>CraftItemRequest.PartData</c> 确实带了客户端算的缺量, 但那是客户端说的。
    /// 服务端自己按库存重算一遍, 客户端那份只当参考 —— 否则改个包就能白拿零件。
    /// 装置那条 (<c>CraftGadgetRequest</c>) 更是**连字段都没有**, 只能重算。
    ///
    /// ## 买不到的零件
    ///
    /// 图鉴上没 <c>Shop</c> 标签的零件铺不出货架, 客户端那边价格显示 "?" 且
    /// 合成按钮禁用。服务端对应返回 false -&gt; <c>NotEnoughParts</c>。
    /// (当前 10 个零件全都带 Shop 标签, 这条是给以后改图鉴留的兜底。)
    /// </summary>
    /// <returns>缺的零件都买得到就 true; 有一样铺不出货架就 false。</returns>
    private bool TryBillParts(RequestLink link, List<ItemSchematicPart> required,
                              out PartBill bill)
    {
        List<(ushort, StackableType, int)> consume =
            new List<(ushort, StackableType, int)>();

        List<string> bought = new List<string>();
        int silverTotal = 0;
        int goldTotal = 0;

        foreach (ItemSchematicPart part in required ?? new List<ItemSchematicPart>())
        {
            if (part.Quantity <= 0)
            {
                continue;
            }

            ushort id = (ushort)part.ID;
            int owned = _accounts.CountStackable(link.UserId, id, StackableType.Part);
            int used = Math.Min(owned, part.Quantity);
            int missing = part.Quantity - used;

            if (used > 0)
            {
                consume.Add((id, StackableType.Part, used));
            }

            if (missing <= 0)
            {
                continue;
            }

            if (!_shop.TryGetPartPrice(id, out int unitSilver, out int unitGold))
            {
                Log.Warning("{Link} 零件 {Part} 缺 {Missing} 个, 但它没铺货架 —— "
                            + "补买不了, 这一单只能失败", link, id, missing);

                bill = default;
                return false;
            }

            silverTotal += unitSilver * missing;
            goldTotal += unitGold * missing;
            bought.Add($"{PartName(id)} x{missing}");
        }

        bill = new PartBill(consume, silverTotal, goldTotal, bought);
        return true;
    }

    /// <summary>零件的人话名字, 只给日志用; 查不到就退回编号。</summary>
    private static string PartName(ushort id) =>
        (CraftingManager.Parts ?? Array.Empty<StructuredPart>())
            .FirstOrDefault(p => p != null && p.ID == id)?.WorkingName
        ?? $"Part#{id}";

    /// <summary>读银币余额, 读不到就当 0。诊断用, 不参与判断。</summary>
    private int BalanceOrZero(ulong userId) => BalanceIn(userId, CraftCurrency.Silver);

    /// <summary>读某一种货币的余额。诊断用, 不参与判断。</summary>
    private int BalanceIn(ulong userId, CraftCurrency currency)
    {
        try
        {
            Account account = _accounts.Load(userId);

            if (account == null)
            {
                return 0;
            }

            return currency == CraftCurrency.Gold ? account.Gold : account.Silver;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private Stackable GrantStackable(ulong userId, ushort itemId,
                                     StackableType type, int amount)
    {
        int total = _accounts.AddStackable(userId, itemId, type, amount);
        return new Stackable(itemId, type, userId, (ushort)total);
    }

    /// <summary>
    /// 46 — 平衡数值热更。空变更 = "按客户端自带的数值走"。
    ///
    /// ## 为什么必须应答
    ///
    /// 大厅在**结算画面**里发这条 (CribMain.cs:2029, SwitchToPostMatchRewardWindow),
    /// 我们原来没注册, 兜底回了 UnrecognizedError —— 玩家日志里那句
    /// "未实现的请求 BalanceChangeRequest(46)" 就是它。
    ///
    /// 回错误的后果不止是一条报错: 客户端的回调是
    ///
    ///     if (RequestManager.HandleRequest(result)) {
    ///         CribGameManager.Instance.LoadChanges(response.Changes);
    ///         CharacterData.Initialize(response.Revision);
    ///     }
    ///
    /// 失败就整段跳过, <c>CharacterData</c> 也跟着不初始化。
    ///
    /// ## 空数组是安全的
    ///
    /// <c>LoadChanges</c> 建一个新的 <c>AbilityManager</c> 再
    /// <c>Deserialize(变更数组)</c> (CribGameManager.cs:28)。零个变更就是
    /// "不改任何数值", 客户端用它自带的那份平衡数据 —— 私服本来也没有热更需求。
    /// </summary>
    public void OnBalanceChange(BalanceChangeRequest request, RequestLink link,
                                Request<BalanceChangeResponse> respond)
    {
        BalanceChangeResponse response = default;
        response.Changes = Array.Empty<GameplayChange>();
        response.Revision = ContentRevision;
        respond.Respond(response);
    }

    /// <summary>3 — 货币余额。这条是真数据, 从库里读。</summary>
    public void OnGetCurrency(GetCurrencyRequest request, RequestLink link, Request<GetCurrencyResult> respond)
    {
        Account account = _accounts.Load(link.UserId);

        GetCurrencyResult response = default;
        response.Succeeded = true;
        response.Currency = AccountMapper.ToCurrency(account);
        response.GoldSpent = 0;
        response.SilverSpent = 0;
        response.CPSpent = 0;
        response.RPSpent = 0;
        response.UpsellTime = 0;
        response.UpsellStep = 0;
        respond.Respond(response);
    }

    /// <summary>27 — 账号数据。真数据。</summary>
    public void OnGetAccountData(GetAccountDataRequest request, RequestLink link, Request<GetAccountDataResponse> respond)
    {
        Account account = _accounts.Load(link.UserId);

        GetAccountDataResponse response = default;
        response.Data = AccountMapper.ToAccountsData(account);
        response.FirstLogin = account.FirstLogin;
        response.UniquesReceived = Array.Empty<Unique>();
        respond.Respond(response);
    }

    /// <summary>
    /// 2 — 背包。武器、装置、可堆叠物 (蓝图/零件/消耗品/宝箱) 全是真数据。
    /// </summary>
    public void OnGetInventory(GetInventoryRequest request, RequestLink link, Request<GetInventoryResponse> respond)
    {
        GetInventoryResponse response = default;

        // 可堆叠物 = 蓝图 / 零件 / 消耗品 / 宝箱 / 改装图。
        // 不返回的话玩家在商店买了蓝图, 打开背包和 workbench 都看不见。
        response.Stackables = _accounts.LoadStackables(link.UserId);
        response.Uniques = _accounts.LoadUniques(link.UserId);
        response.Trinkets = _accounts.LoadGadgets(link.UserId);
        respond.Respond(response);
    }

    /// <summary>
    /// 41 — 被禁用的功能。
    /// Revision 必须非零且**稳定**: 给 0 或每次递增会让客户端认为自己过期,
    /// 进而反复重新拉取, 在匹配流程里会变成无限重试。
    /// </summary>
    public void OnGetDisabledFeatures(GetDisabledFeaturesRequest request, RequestLink link, Request<GetDisabledFeaturesResponse> respond)
    {
        GetDisabledFeaturesResponse response = default;
        response.Data = DIE.Server.Match.GameModeAvailability.DisabledFeatures();
        respond.Respond(response);
    }

    /// <summary>33 — 账号设置里的邮箱/订阅信息。没有就回一份空的。</summary>
    public void OnGetMailData(GetMailDataRequest request, RequestLink link, Request<GetMailDataResponse> respond)
    {
        GetMailDataResponse response = default;
        response.Data = default;
        response.Success = true;
        respond.Respond(response);
    }

    /// <summary>34 — 保存邮箱设置。Phase 1 只确认收到, 不持久化。</summary>
    public void OnSetMailData(SetMailDataRequest request, RequestLink link, Request<SetMailDataResponse> respond)
    {
        Log.Debug("{Link} SetMailData (Phase 1 暂不持久化)", link);
        SetMailDataResponse response = default;
        respond.Respond(response);
    }

    /// <summary>
    /// 76 — 在线时长上报, 客户端待在主菜单期间会周期性发送。
    /// StatsReportAffirmativeMessage 是**空结构体**, 所以整条响应就是
    /// 长度(4) + packetType(1) + guid(16) + varint result(1) = 22 字节, 无载荷。
    /// </summary>
    public void OnAddActiveCribTime(AddActiveCribTimeMessage request, RequestLink link, Request<StatsReportAffirmativeMessage> respond)
    {
        Log.Debug("{Link} 在线时长上报 {Minutes} 分钟 (Phase 1 不入库)", link, request.MinutesActive);
        StatsReportAffirmativeMessage response = default;
        respond.Respond(response);
    }

    /// <summary>
    /// 20 — 解锁故事地图节点。
    ///
    /// 我们下发的是空的奖励配置 (ContentRevision 那份), 所以客户端手里没有任何
    /// 可解锁的节点数据, 这条请求实际上不该成功。回 CannotUnlock 是诚实且温和的:
    /// 客户端显示一句提示, 不会重试、不会崩。真正的故事地图进度属于 Phase 4。
    /// </summary>
    public void OnUnlockStoryMapNode(UnlockStoryMapNodeRequest request, RequestLink link,
                                     Request<UnlockStoryMapNodeResponse> respond)
    {
        UnlockStoryMapNodeResponse response = default;
        response.EventualUniqueRewards = Array.Empty<Unique>();
        response.Revision = ContentRevision;

        Log.Information("{Link} 请求解锁剧情节点 {NodeID} (客户端版本 {Theirs}, 我们 {Ours})",
            link, request.NodeID, request.StoryMapRevision, ContentRevision);

        // ## 版本对不上要回 RevisionOutdated 而不是别的错
        //
        // 客户端收到这一档会去重拉 GetRewardSetup 再**自动重试**
        // (AccountDataManager.UnlockRewardRequest 里那段)。回成别的错它就放弃了。
        if (request.StoryMapRevision != ContentRevision)
        {
            response.Result = StoryMapUnlockResult.RevisionOutdated;
            respond.Respond(response);
            return;
        }

        // ## 白名单
        //
        // 剧情地图上还有 DLC 礼包、周年庆那些节点 (50~56)。客户端只会为新手任务
        // 的几个发请求, 但"客户端只会"不是安全边界 —— 列表之外一律拒绝。
        if (!StarterQuests.ClientUnlockable.Contains(request.NodeID))
        {
            Log.Warning("{Link} 请求解锁的节点 {NodeID} 不在白名单里", link, request.NodeID);

            response.Result = StoryMapUnlockResult.CannotUnlock;
            respond.Respond(response);
            return;
        }

        // ## "全部完成"要真的全部完成
        //
        // 节点 12 带着 Cash ×1,000,000 的奖励, 而客户端那个 CLAIM 按钮是它自己
        // 按本地进度判着显示的。服务端这边照样得核一遍。
        if (request.NodeID == StarterQuests.All && !AllStarterQuestsDone(link.UserId))
        {
            Log.Warning("{Link} 五项没做完就想领总奖励", link);

            response.Result = StoryMapUnlockResult.CannotUnlock;
            respond.Respond(response);
            return;
        }

        byte required = StarterQuests.TimesRequired(request.NodeID);

        if (StarterQuests.IsComplete(_accounts.LoadUnlockedNodes(link.UserId), request.NodeID))
        {
            // 已经解锁过 —— 客户端把这一档和 Success 一样处理 (只是不再发奖),
            // 所以不能回错误, 否则重进大厅时那一格会卡住。
            response.Result = StoryMapUnlockResult.AlreadyUnlocked;
            respond.Respond(response);
            return;
        }

        byte times = _accounts.AdvanceStoryMapNode(link.UserId, request.NodeID, required);

        if (times >= required)
        {
            GrantNodeRewards(link, request.NodeID);
        }

        response.Result = StoryMapUnlockResult.Success;
        respond.Respond(response);
    }

    /// <summary>面板上那五项是不是都完成了。</summary>
    private bool AllStarterQuestsDone(ulong userId)
    {
        IReadOnlyList<StoryMapUnlockData> nodes = _accounts.LoadUnlockedNodes(userId);

        return StarterQuests.Panel
            .Where(id => id != StarterQuests.All)
            .All(id => StarterQuests.IsComplete(nodes, id));
    }

    /// <summary>
    /// 发这个节点的奖励。
    ///
    /// 奖励表在**客户端**硬编码 (<c>StoryMapData.CreateData</c>), 服务端这侧
    /// 没有那张表的实例 —— 所以这里只处理我们确实知道的那一项: 节点 12 的金币。
    ///
    /// Cash 就是金币 (原厂 <c>ProcessRewards</c> 里 <c>Cash -&gt; Currencies.Gold</c>)。
    /// 面板上显示多少由 <see cref="OnGetRewardSetup"/> 下发, 和这里发的是同一个
    /// 常量 —— 两处对不上就是另一种"看得见拿不到"。
    /// </summary>
    private void GrantNodeRewards(RequestLink link, int nodeId)
    {
        if (nodeId != StarterQuests.All)
        {
            return;
        }

        int balance = _accounts.AddCurrency(
            link.UserId, AccountStore.CurrencyKind.Gold, StarterQuestGoldReward);

        Log.Information("{Link} 新手任务全部完成, 发放 {Gold} 金币 (余额 {Balance})",
            link, StarterQuestGoldReward, balance);
    }

    /// <summary>
    /// 新手任务全部完成的奖励, 单位是金币。
    ///
    /// ## 为什么不是原厂那个 1,000,000
    ///
    /// 客户端硬编码的 <c>StoryMapData.StarterQuestRewards</c> 写的是
    /// <c>Cash ×1,000,000</c>。原厂那是**充值货币**, 有整套商城消耗它; 我们这边
    /// 金币只有这一个产出口, 而合成弹窗的现金按钮按原厂比价大约 11 银兑 1 金
    /// (见 <c>ShopCatalog.GoldFromSilver</c>) —— 一百万金就是一千一百万银的材料,
    /// 等于新手任务一做完经济系统就作废了。
    ///
    /// 一千金 ≈ 一万一千银的材料, 够开个头, 不至于把后面的路铺平。
    ///
    /// 改了数就必须连**面板上显示的数**一起改, 否则玩家看到 1,000,000 却只到账
    /// 1,000。<see cref="OnGetRewardSetup"/> 下发的就是这个常量 —— 面板和实发
    /// 读的是同一个字段, 所以倍率也只乘一次。
    ///
    /// ## 客户端管这个叫 Cash
    ///
    /// 玩家看到的 "Cash" 就是这里的金币 (<c>CurrencyItemInfo.Description</c> 把
    /// <c>CribCurrencyType.Gold</c> 显示成 Cash)。所以它归
    /// <see cref="DIE.Server.Data.RewardBoost"/> 管 —— 一千 -> 一万。
    ///
    /// 注意这是**全服唯一的金币产出口**: 局末掉落表里没配金币, 而
    /// <c>RewardLedger.Grant</c> 见到金币会直接忽略并告警。所以"Cash 十倍"
    /// 实际影响的就是新手任务这一次性的一笔。
    /// </summary>
    private static int StarterQuestGoldReward =>
        DIE.Server.Data.RewardBoost.Scale(1_000);

    /// <summary>
    /// 47 — 查询玩家所在地区的货币 ISO 代码 (用于真钱购买金币的定价显示)。
    ///
    /// 调用方是 GUI_BuyCurrencyPopup: Success=true 会让它进入 Step1, 也就是
    /// **Steam 微交易流程**; Success=false 则显示 Step3 的"不可用"提示。
    ///
    /// 这里故意回 false —— 私服既无法也不应该处理真实付款。让它干净地失败,
    /// 好过把玩家引进一个必然出错(或更糟, 真的扣款)的购买流程。
    /// </summary>
    public void OnGetCurrencyIsoCode(GetCurrencyISOCodeRequest request, RequestLink link, Request<GetCurrencyISOCodeResponse> respond)
    {
        Log.Information("{Link} 请求货币 ISO 代码 (真钱商店) —— 私服不支持内购, 回 Success=false", link);

        GetCurrencyISOCodeResponse response = default;
        response.Success = false;
        response.Currency = string.Empty;
        respond.Respond(response);
    }

    /// <summary>
    /// 42 — 举报玩家。局末结算画面发的 (<c>GUI_RewardWindow.SendMatchRewardEvent</c>),
    /// 而且只在玩家局内真举报过 (客户端留了 reports 文件) 时才发。
    ///
    /// 私服没有客服, 举报**只记日志**, 不落库、不封人 —— 但必须应答: 不答就落到兜底回
    /// UnrecognizedError, 客户端会在结算画面打一条 LogError。
    /// </summary>
    public void OnReportPlayers(ReportPlayersRequest request, RequestLink link, Request<ReportPlayersResponse> respond)
    {
        string reports = request.Reports == null
            ? "(空)"
            : string.Join(", ", request.Reports.Select(r => $"{r.ReporteeID}:{r.Type}"));

        Log.Information("{Link} (user={UserId}) 举报了对局 {Match} 里的玩家: {Reports} —— 私服只记日志",
            link, link.UserId,
            request.MatchID is { Length: 16 } guid ? new Guid(guid).ToString() : "(未知)", reports);

        respond.Respond(new ReportPlayersResponse { Success = true });
    }

    /// <summary>
    /// 58 — 签发匹配票据 —— 进入对局的第一步 (Solo 和组队排队都先要这张票)。
    ///
    /// 1555 和 2555 是**两条互不相干的 TCP 会话**, 客户端不会在它们之间传递身份。
    /// 票据是唯一的关联凭据: 这里记下 user_id, 客户端把票据原样带到 2555
    /// (SoloServerCreateRequest), 那边再取回来。
    ///
    /// 票据内容对客户端完全不透明, 所以是随机字节。做公开服时换成 HMAC 防伪造。
    ///
    /// 未登录的连接直接回 Success=false —— 客户端会弹提示并留在主菜单,
    /// 不会继续往 2555 发请求。
    /// </summary>
    public void OnGetMatchmakingTicket(GetMatchmakingTicketRequest request, RequestLink link, Request<GetMatchmakingTicketResponse> respond)
    {
        respond.Respond(IssueTicket(link, request.PlayerChoices, "匹配票据"));
    }

    /// <summary>
    /// 59 — 自定义房间用的票据。内部调试开局窗口 (<c>DebugPlayWindow</c>) 建房/进房前发它,
    /// 形状和 58 完全一样, 响应也是同一个类型。
    /// </summary>
    public void OnGetDebugMatchmakingTicket(GetDebugMatchmakingTicketRequest request, RequestLink link, Request<GetMatchmakingTicketResponse> respond)
    {
        respond.Respond(IssueTicket(link, request.PlayerChoices, "自定义房间票据"));
    }

    /// <summary>
    /// 签票。PlayerChoices 里是**全队每个人**的角色、装备 (组队时队长一次带上整队,
    /// <c>SteamLobbyData.cs:423</c>), 编成名单存进票据的 queue_data —— 菜单链路 (1555)
    /// 和战斗服 (UDP) 之间只有这一条纽带。格式见 <see cref="QueueRosterCodec"/>。
    ///
    /// 开票人排第一 (名单的 Owner, 结算的兜底)。队员的 UserID 是队长的客户端经 Steam 大厅
    /// 听来的, 我们不认它当身份 —— 战斗服进场时凭各自的会话票对号入座 (<c>MatchRoster</c>)。
    /// 这里只用它去各自的背包里把武器 GUID 换成图纸号; 查不到就不带武器。
    /// </summary>
    private GetMatchmakingTicketResponse IssueTicket(RequestLink link, IEnumerable<PlayerQueueData> choices, string what)
    {
        GetMatchmakingTicketResponse response = default;

        if (!link.HasLoggedIn)
        {
            Log.Warning("{Link} 未登录却请求{What}, 拒绝", link, what);
            response.Success = false;
            response.MatchmakingTicket = Array.Empty<byte>();
            return response;
        }

        List<PlayerQueueData> all = (choices ?? Array.Empty<PlayerQueueData>()).ToList();
        if (all.Count > QueueRosterCodec.MaxSeats)
        {
            Log.Warning("{Link} {What}带了 {Count} 份玩家选择, 只取前 {Max} 份", link, what, all.Count, QueueRosterCodec.MaxSeats);
            all = all.Take(QueueRosterCodec.MaxSeats).ToList();
        }

        // 开票人排第一。客户端总会带上自己那份 (UserID = 我们登录时发给它的 id);
        // 万一没带, 补一个没有选择的位子 —— 战斗服照样认得他, 只是走默认角色。
        List<(ulong UserId, byte[] ChoiceBlob)> seats = new();
        int self = all.FindIndex(c => c.UserID == link.UserId);
        if (self < 0)
        {
            Log.Warning("{Link} {What}里没有开票人自己 (user={UserId}) 的那份选择", link, what, link.UserId);
            seats.Add((link.UserId, null));
        }
        else
        {
            PlayerQueueData mine = all[self];
            all.RemoveAt(self);
            seats.Add((link.UserId, EncodeWithLoadout(link.UserId, mine)));
        }

        foreach (PlayerQueueData member in all)
        {
            seats.Add((member.UserID, member.UserID != 0
                ? EncodeWithLoadout(member.UserID, member)
                : PlayerChoiceCodec.Encode(member)));
        }

        // 十字路口的难度档只在这一趟看得见 (排队请求里没有这个字段) —— 存进票据,
        // 匹配服排队时读出来挑图 (小/中/大三批图按难度分)。
        int difficulty = DIE.Server.Match.GameModeAvailability.ClampDifficulty(
            CrossroadDifficultyOf(choices, link.UserId));

        byte[] queueData = QueueRosterCodec.Encode(seats, difficulty);
        byte[] ticket = _matches.IssueTicket(link.UserId, queueData);

        IReadOnlyList<MatchSeat> decoded = QueueRosterCodec.Decode(queueData, link.UserId);
        Log.Information("{Link} 已签发{What} (开票人 user={UserId}, 十字路口难度档 {Difficulty}, 名单 {Roster})",
            link, what, link.UserId, difficulty,
            string.Join(", ", decoded.Select(s => $"{s.UserId}:{s.Choice?.Character}")));

        response.Success = true;
        response.MatchmakingTicket = ticket;
        return response;
    }

    /// <summary>
    /// 开票人那份 <c>PlayerQueueData.QueuedCrossroadDifficulty</c> (十字路口的难度档)。
    ///
    /// 客户端给的是 <c>ushort</c>, 而且**每个人一份** —— 组队时全队同一个值
    /// (大厅统一写, <c>SteamLobbyData.cs:441</c>: <c>(ushort)Difficulty</c>),
    /// 所以取开票人的就够; 找不到他就退回第一份, 一份都没有就 0 (简单档)。
    ///
    /// 非十字路口的路径这个字段恒为 0 (Solo / 调试窗口 / 展会窗口都写死 0),
    /// 所以不用先判队列类型。
    /// </summary>
    private static int CrossroadDifficultyOf(IEnumerable<PlayerQueueData> choices, ulong owner)
    {
        List<PlayerQueueData> all = (choices ?? Array.Empty<PlayerQueueData>()).ToList();
        if (all.Count == 0)
        {
            return 0;
        }

        int self = all.FindIndex(c => c.UserID == owner);
        return self >= 0 ? all[self].QueuedCrossroadDifficulty : all[0].QueuedCrossroadDifficulty;
    }

    /// <summary>
    /// 把客户端选的武器 **GUID** 换成 **SchematicID**。
    ///
    /// ## 为什么在这一侧换
    ///
    /// 客户端发的 <c>EquippedWeaponGUIDs</c> 是物品实例的标识, 而战斗服装备
    /// 武器只要图纸号 (<c>Player.EquipHaxWeaponNew(slot, schematicId, equip)</c>)。
    /// 在菜单链路换好再编进票据, 战斗服就不用碰账号库 ——
    /// 这是 <c>MatchHostRegistry.TryCreate</c> 那条既定原则, accountLevel 也一样。
    ///
    /// ## 玩家实测: "场外装备好的武器进游戏没有装备, 用的是空手攻击"
    ///
    /// 根因就在这一步缺失: <c>PlayerChoiceCodec</c> 原本只编码角色和外观,
    /// 武器在签发票据时被丢掉, 战斗服无从得知玩家装了什么。
    /// 那个类的注释里当时就写着"还没带的: 武器/饰品的 GUID"。
    ///
    /// ## 匹配不上就跳过, 不猜
    ///
    /// GUID 在库里查不到 (数据不同步、物品已卖掉) 时**不回退到随便一把** ——
    /// 装错武器比空手更难查。返回空列表, 战斗服那边照旧走默认。
    /// </summary>
    private byte[] EncodeWithLoadout(ulong userId, PlayerQueueData choice) =>
        PlayerChoiceCodec.Encode(choice, ResolveWeapons(userId, choice), ResolveGadgets(userId, choice),
            ResolveConsumables(userId, choice));

    /// <summary>
    /// 场外装的消耗品按账号等级和背包核一遍, 定下每格带几个 (规则见 <see cref="ConsumableLoadout"/>)。
    /// 这里不扣背包 —— 局末按实际用掉的扣 (<c>LidgrenHost</c> 的消耗品账本)。
    /// </summary>
    private IReadOnlyList<ConsumableSlot> ResolveConsumables(ulong userId, PlayerQueueData choice)
    {
        int[] wanted = choice.EquippedConsumableIDs?.ToArray() ?? Array.Empty<int>();
        if (!wanted.Any(id => id > 0))
        {
            return Array.Empty<ConsumableSlot>();
        }

        int level;
        try
        {
            level = AccountLevelHelpers.ExperienceToLevel(_accounts.Load(userId)?.StoryMapXp ?? 0);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "签票时读不到账号 {UserId}, 消耗品不带", userId);
            return Array.Empty<ConsumableSlot>();
        }

        IReadOnlyList<ConsumableSlot> carried = ConsumableLoadout.Resolve(wanted, level,
            id => _accounts.CountStackable(userId, id, StackableType.Consumable));
        if (carried.Count != wanted.Count(id => id > 0))
        {
            Log.Information("user={UserId} (账号 {Level} 级) 装了消耗品 [{Wanted}], 带进去 [{Carried}] —— " +
                            "没解锁的格子、背包里没有的、局内商店的货不带",
                userId, level, string.Join(",", wanted), string.Join(",", carried.Select(c => $"{c.Slot}:{c.Id}x{c.Amount}")));
        }

        return carried;
    }

    private IReadOnlyList<ConductorCrafting.Unique> ResolveWeapons(ulong userId, PlayerQueueData choice)
    {
        List<byte[]> wanted = (choice.EquippedWeaponGUIDs ?? Array.Empty<byte[]>())
            .Where(g => g != null && g.Length > 0)
            .Take(PlayerChoiceCodec.MaxWeapons)
            .ToList();
        if (wanted.Count == 0)
        {
            return Array.Empty<ConductorCrafting.Unique>();
        }

        IReadOnlyList<ConductorCrafting.Unique> owned = _accounts.LoadUniques(userId);
        List<ConductorCrafting.Unique> resolved = new List<ConductorCrafting.Unique>();
        foreach (byte[] guid in wanted)
        {
            foreach (ConductorCrafting.Unique item in owned)
            {
                if (item.Guid != null && item.Guid.AsSpan().SequenceEqual(guid))
                {
                    resolved.Add(item);
                    break;
                }
            }
        }

        if (resolved.Count != wanted.Count)
        {
            Log.Warning("user={UserId} 选了 {Wanted} 把武器, 库里只对上 {Resolved} 把 —— " +
                        "对不上的那些不会带进战斗服 (宁可空手也不装错)",
                userId, wanted.Count, resolved.Count);
        }

        return resolved;
    }

    /// <summary>
    /// 装置 (Gadget) 同理: 客户端给 GUID, 这里换成库里那件 (带词条掷点 <c>Stats</c>, 属性由它算)。
    /// 2026-09-15 之前装置根本没进票据, 装了等于没装。
    /// </summary>
    private IReadOnlyList<ConductorCrafting.Gadget> ResolveGadgets(ulong userId, PlayerQueueData choice)
    {
        List<byte[]> wanted = (choice.EquippedTrinketGUIDs ?? Array.Empty<byte[]>())
            .Where(g => g != null && g.Length > 0)
            .Take(PlayerChoiceCodec.MaxGadgets)
            .ToList();
        if (wanted.Count == 0)
        {
            return Array.Empty<ConductorCrafting.Gadget>();
        }

        IReadOnlyList<ConductorCrafting.Gadget> owned = _accounts.LoadGadgets(userId);
        List<ConductorCrafting.Gadget> resolved = owned
            .Where(g => g.GUID != null && wanted.Any(w => g.GUID.AsSpan().SequenceEqual(w)))
            .Take(PlayerChoiceCodec.MaxGadgets)
            .ToList();

        if (resolved.Count != wanted.Count)
        {
            Log.Warning("user={UserId} 装了 {Wanted} 个装置, 库里只对上 {Resolved} 个", userId, wanted.Count, resolved.Count);
        }

        return resolved;
    }
}
