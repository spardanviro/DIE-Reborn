using System;
using System.Collections.Generic;
using System.Linq;
using ConductorCrafting;

namespace DIE.Server.Data;

/// <summary>
/// 商店货架。
///
/// 玩家定的方向 (第二轮): "商店改为不能直接购买武器, 只能购买 lockbox、
/// consumables、character, 然后玩家自行在 workbench 里合成武器和 gadgets。"
///
/// 他原本选的是"蓝图直售、零件开箱"。**后来对调了** —— 客户端的宝箱只能装
/// 蓝图和改装图, 零件那一档它当场报错并把箱子判成"没东西可开" (玩家实测的
/// "lockbox 全部显示 no items available")。详见 <see cref="DropTableBuilder"/>。
///
/// 现在是: 蓝图两条路都有 (商店定向买 / 开箱碰运气), 零件商店直售。
///
/// 所以货架四档:
///
///   武器蓝图 Blueprint       图鉴里每一把武器一档, 银币标价 (按品阶)
///   装置蓝图 GadgetBlueprint  图鉴里每一个装置一档, 同上
///   零件 Part         合成的另一半材料, 银币按品阶 (单价远低于蓝图)
///   宝箱 Lockbox      开出蓝图和改装图 —— 碰运气那条路
///   消耗品 Consumable  局内用的那些
///   角色 Character     用**角色点**买, 不是银币
///
/// **没有 Weapon 档** —— 武器只能在 workbench 造出来。
///
/// ## 合成链
///
/// 蓝图 (Stackable/Schematic) + 零件 (Stackable/Part) × N
///   -&gt; workbench (<c>CraftItemRequest</c>) -&gt; 武器 (Unique)
///
/// 装置**也吃零件** (图鉴实测 16 个装置全都有 <c>StructuredGadget.Parts</c>)。
/// <c>CraftGadgetRequest</c> 的报文里不带零件账, 那是服务端自己查的 ——
/// 见 <c>MenuHandlers.OnCraftGadget</c>。
///
/// ## 为什么价格是我们编的
///
/// 原厂的商品和定价来自运营下发的数据, 反编译树里只有读它的代码。所以货架
/// 是按图鉴**生成**的, 价格也是我们定的 —— 生成而不是手写一张表, 图鉴里有
/// 什么货架上就有什么, 不会漏。
///
/// 金币 (Gold) 是原厂的充值货币, 私服没有充值这一环, 一律不用。
/// </summary>
public sealed class ShopCatalog
{
    /// <summary>
    /// 一阶**蓝图**多少银币。**我们定的。**
    ///
    /// 参照系: 一局 Starter Mission 大约给 700 银币 (品阶 1, 十分钟一局 ——
    /// 见 <see cref="MatchDropTable"/> 的 GP 曲线)。蓝图只是合成的一半材料 (另一半
    /// 零件另买), 所以标得比"直接买武器"那一版便宜。
    ///
    /// 开箱也能出蓝图, 单价比这里便宜 —— 那是碰运气那条路的报酬。
    /// </summary>
    public int SilverPerBlueprintTier { get; set; } = 300;

    /// <summary>
    /// 价格随品阶增长的指数。**我们定的。**
    ///
    /// 早先按等比 (每阶 ×1.8) 算过, 图鉴里最高 16 阶, 顶阶要一百万银币 ——
    /// 一局 500, 等于两千局。幂曲线 <c>tier^1.5</c> 顶阶约七千, 仍然是凸的
    /// (高阶明显更贵) 但不会失控。
    /// </summary>
    public float TierExponent { get; set; } = 1.5f;

    /// <summary>
    /// 各种宝箱的银币价。**我们定的。**
    ///
    /// 金箱比银箱贵一档; 武器箱 (出武器蓝图) 和改装箱 (出 design) 同价 ——
    /// 两者对玩家的价值不好排序, 与其猜不如先并列, 等实测再分。
    /// </summary>
    public IReadOnlyDictionary<LockboxTypes, int> LockboxPrices { get; set; } =
        new Dictionary<LockboxTypes, int>
        {
            [LockboxTypes.SilverWeaponBox] = 400,
            [LockboxTypes.SilverModificationBox] = 400,
            [LockboxTypes.GoldWeaponBox] = 1200,
            [LockboxTypes.GoldModificationBox] = 1200,
        };

    /// <summary>
    /// 一阶零件多少银币。**我们定的。**
    ///
    /// 一把武器要好几种零件、每种好几个, 所以单价必须远低于蓝图 ——
    /// 否则凑齐一把的成本会盖过蓝图本身。
    /// </summary>
    public int SilverPerPartTier { get; set; } = 40;

    /// <summary>一个消耗品多少银币。**我们定的。**</summary>
    public int SilverPerConsumable { get; set; } = 150;

    /// <summary>解锁一个角色多少角色点。**我们定的。**</summary>
    public int CharacterPointsPerCharacter { get; set; } = 500;

    /// <summary>货架。<see cref="Build"/> 之前是空的。</summary>
    public IReadOnlyList<ShopItemData> Items => _items;

    /// <summary>货架上那一档卖的是什么 —— 购买时按它决定发什么货。</summary>
    public bool TryGetEntry(int shopItemId, out ShopEntry entry) =>
        _byShopItem.TryGetValue(shopItemId, out entry);

    /// <summary>
    /// 一个零件的标价 —— 合成缺料时按它补买。
    ///
    /// 客户端算总价时查的是同一档 (<c>ShopHelper.GetShopItem(id, ShopItemType.Part)</c>
    /// 按 <c>Items[j].ItemID == id &amp;&amp; Items[j].Type == Part</c> 找),
    /// 所以这里必须和货架上那份**同价**, 否则弹窗显示的数和实际扣的对不上。
    ///
    /// 查不到 = 这个零件没铺货 (图鉴上没 Shop 标签)。客户端那边表现为价格显示
    /// "?" 且合成按钮禁用, 服务端对应报 <c>NotEnoughParts</c>。
    /// </summary>
    public bool TryGetPartPrice(ushort partId, out int silver, out int gold)
    {
        if (_byShopItem.TryGetValue(PartIdBase + partId, out ShopEntry entry)
            && entry.Type == ShopItemType.Part)
        {
            silver = entry.SilverPrice;
            gold = entry.GoldPrice;
            return true;
        }

        silver = 0;
        gold = 0;
        return false;
    }

    /// <summary>货架上的一档。</summary>
    /// <param name="GoldPrice">
    /// 只有零件铺了金价, 而且**只给合成弹窗的现金按钮用** —— 商店那条路
    /// (<c>OnBuyItem</c>) 一律拒收金币。见 <see cref="GoldFromSilver"/>。
    /// </param>
    public readonly record struct ShopEntry(
        ShopItemType Type,
        ushort ItemId,
        int SilverPrice,
        int CharacterPointPrice,
        int GoldPrice = 0);

    private readonly List<ShopItemData> _items = new List<ShopItemData>();
    private readonly Dictionary<int, ShopEntry> _byShopItem = new Dictionary<int, ShopEntry>();

    /// <summary>
    /// 从图鉴生成货架。幂等 —— 重复调会重建。
    ///
    /// 前提是 <see cref="CraftingCatalog.Load"/> 跑过, 否则
    /// <c>CraftingManager</c> 里几张表都是空的, 货架也就只剩宝箱和角色。
    /// </summary>
    public void Build()
    {
        _items.Clear();
        _byShopItem.Clear();

        StockBlueprints();
        StockGadgetBlueprints();
        StockParts();
        StockLockboxes();
        StockKeycodes();
        StockConsumables();
        StockCharacters();
    }

    /// <summary>
    /// 武器蓝图 —— **只上带 <c>Shop</c> 标签的那 27 把**。
    ///
    /// 买到的是 <c>Stackable(id, StackableType.Schematic, ...)</c>,
    /// 客户端那边 <c>ShopItemInfo.cs:357</c> 就是这么解的。
    ///
    /// ## 为什么不是全部 322 把
    ///
    /// 图鉴里每把武器都有标签, 原厂就是按标签分销路的:
    ///
    ///     Drop        206 把   只能打出来 / 开箱出
    ///     Drop+Shop    18 把   两条路都有
    ///     InGameShop   38 把   局内商店 (Scavenger 打到一半买的那种)
    ///     Reward       16 把   账号升级奖励
    ///     Premium       9+3 把 金币档
    ///     Debug        32 把   不该出现在任何地方
    ///
    /// 我们原来把 322 把全铺上了货架 —— 那等于把"打" 这件事的意义抽掉:
    /// 攒够钱就能定向买到任意一把, 开箱和掉落都成了摆设。
    ///
    /// 收到 27 把之后, 品阶分布是
    /// <c>1:1 2:2 3:4 4:2 5:2 6:3 7:3 8:1 11:3 13:6</c> ——
    /// **9/10/12/14/15/16 阶一把都没有**。那不是漏了, 是原版的意图:
    /// 高阶蓝图只能靠开箱和掉落, 钱买不到。
    /// </summary>
    private void StockBlueprints()
    {
        foreach (ItemSchematic schematic in (CraftingManager.WeaponSchematics
                                             ?? Array.Empty<ItemSchematic>())
                 .Where(s => s != null && HasShopTag(s.Tags)))
        {
            int silver = BlueprintPrice(schematic.Tier, schematic.Tags);

            Add(new ShopItemData
            {
                // ShopItemID 直接用 schematic 的 ID —— 一一对应, 另编一套编号
                // 只会多一层要维护的映射。客户端只把它当不透明标识回传。
                ShopItemID = schematic.Id,
                LocalizedName = schematic.Name ?? string.Empty,
                WorkingName = schematic.Name ?? string.Empty,
                IsActive = true,
                SilverPrice = silver,
                Items = new[]
                {
                    new ItemData
                    {
                        Type = ShopItemType.Blueprint,
                        ItemID = schematic.Id,
                        Amount = 1,
                    },
                },
            },
            new ShopEntry(ShopItemType.Blueprint, (ushort)schematic.Id, silver, 0));
        }
    }

    /// <summary>
    /// 装置蓝图 —— 图鉴里每一个装置一档。
    ///
    /// 和武器蓝图同一个套路, 只是买到的可堆叠物类型是
    /// <c>StackableType.GadgetBlueprint</c> 而不是 <c>Schematic</c> ——
    /// 那是原厂分开的两档 (<c>ShopItemType.GadgetBlueprint</c> 也是单独一项)。
    ///
    /// 装置也吃零件 (<c>StructuredGadget.Parts</c>, 图鉴实测 16 个全都有),
    /// 所以合成链和武器完全一样。
    /// </summary>
    private void StockGadgetBlueprints()
    {
        foreach (StructuredGadget gadget in (CraftingManager.Trinkets
                                             ?? Array.Empty<StructuredGadget>())
                 .Where(g => g != null && HasShopTag(g.Tags)))
        {
            int silver = BlueprintPrice(gadget.Tier, gadget.Tags);

            Add(new ShopItemData
            {
                ShopItemID = GadgetBlueprintIdBase + gadget.ID,
                LocalizedName = gadget.LocalizedName ?? gadget.WorkingName ?? string.Empty,
                WorkingName = gadget.WorkingName ?? string.Empty,
                IsActive = true,
                SilverPrice = silver,
                Items = new[]
                {
                    new ItemData
                    {
                        Type = ShopItemType.GadgetBlueprint,
                        ItemID = gadget.ID,
                        Amount = 1,
                    },
                },
            },
            new ShopEntry(ShopItemType.GadgetBlueprint, (ushort)gadget.ID, silver, 0));
        }
    }

    /// <summary>
    /// 零件 —— 合成的另一半材料。
    ///
    /// ## 为什么改成直售
    ///
    /// 上一版是"零件靠开箱"。查下来客户端表达不了 —— 宝箱只能装蓝图和
    /// 改装图, 零件那一档它当场报错并把箱子判成"没东西可开"
    /// (详见 <see cref="DropTableBuilder"/>)。
    ///
    /// 所以两者对调: 蓝图和改装图靠开箱 (碰运气, 单价便宜), 零件直售
    /// (要多少买多少)。合成链的两半都还在, 只是来源换了个个儿。
    /// </summary>
    private void StockParts()
    {
        foreach (StructuredPart part in (CraftingManager.Parts
                                         ?? Array.Empty<StructuredPart>())
                 .Where(p => p != null && HasShopTag(p.Tags)))
        {
            int silver = PartPrice(part.Tier);
            int gold = GoldFromSilver(silver);

            Add(new ShopItemData
            {
                ShopItemID = PartIdBase + part.ID,
                LocalizedName = part.LocalizedName ?? part.WorkingName ?? string.Empty,
                WorkingName = part.WorkingName ?? string.Empty,
                IsActive = true,
                SilverPrice = silver,

                // 合成弹窗的现金按钮读的就是这个字段
                // (GUI_CraftCostTableEntry.SetData -> shopItem.GoldPrice)。
                // 留 0 的话客户端会以为缺料用现金买是**免费**的。
                GoldPrice = gold,
                Items = new[]
                {
                    new ItemData
                    {
                        Type = ShopItemType.Part,
                        ItemID = part.ID,
                        Amount = 1,
                    },
                },
            },
            new ShopEntry(ShopItemType.Part, (ushort)part.ID, silver, 0, gold));
        }
    }

    /// <summary>
    /// 宝箱 —— 蓝图和改装图从这里出。
    ///
    /// ShopItemID 用 <see cref="LockboxIdBase"/> 加类型枚举值错开, 免得和
    /// 蓝图那批 schematic ID 撞。
    /// </summary>
    private void StockLockboxes()
    {
        foreach (LockboxTypes type in Enum.GetValues<LockboxTypes>())
        {
            if (type == LockboxTypes.None
                || !LockboxPrices.TryGetValue(type, out int silver))
            {
                continue;
            }

            Add(new ShopItemData
            {
                ShopItemID = LockboxIdBase + (int)type,
                LocalizedName = type.ToString(),
                WorkingName = type.ToString(),
                IsActive = true,
                SilverPrice = silver,
                Items = new[]
                {
                    new ItemData
                    {
                        Type = ShopItemType.Lockbox,
                        ItemID = (int)type,
                        Amount = 1,
                    },
                },
            },
            new ShopEntry(ShopItemType.Lockbox, (ushort)type, silver, 0));
        }
    }

    /// <summary>
    /// 暗码 (Combination) —— 开宝箱要的那个东西。
    ///
    /// ## 这一档**不能缺**
    ///
    /// 不是"少了个商品"那么轻。<c>GUI_OpenLockBoxPopup.SetData</c> 一进来就是:
    ///
    ///     InventoryItemInfo keycodeItem =
    ///         ShopHelper.GetShopItemInfoOfType(CribItemType.KeyCode).First();
    ///
    /// <c>.First()</c> —— 货架上没有暗码这一档时它直接抛, 表现是"点开箱毫无反应"。
    /// (而且异常会被原厂 StunTCP 那层吞掉, 参见 <c>RequestServer.OnClientError</c>。)
    ///
    /// ## 单价怎么定
    ///
    /// 开一个银武器箱要 10 个 (<c>MonetizationStatics
    /// .CodesNeededToUnlock_WeaponCache_Silver</c>), 银改装箱 7 个。
    /// 单价 120 银币 = 一箱 1200, 和金武器箱的售价同量级 —— 也就是"不想攒暗码
    /// 就多花点钱直接买"。
    ///
    /// 开箱界面会自动按这个单价把**缺的那几个**补买掉
    /// (<c>(count - owned) * _KeycodeCost.Value</c>), 玩家不必先手动凑齐。
    /// </summary>
    private void StockKeycodes()
    {
        Add(new ShopItemData
        {
            ShopItemID = KeycodeShopItemId,
            LocalizedName = "Combination",
            WorkingName = "Keycode",
            IsActive = true,
            SilverPrice = KeycodePrice,
            Items = new[]
            {
                new ItemData
                {
                    Type = ShopItemType.Keycode,

                    // 暗码没有品类之分, 原厂一律 0
                    // (AccountLevelHelpers 的升级奖励里也是 0)。
                    ItemID = 0,
                    Amount = 1,
                },
            },
        },
        new ShopEntry(ShopItemType.Keycode, 0, KeycodePrice, 0));
    }

    /// <summary>
    /// 消耗品 —— 局内用的那些。
    ///
    /// 47 个里只上带 <c>Shop</c> 标签的 33 个; 另外 14 个标的是
    /// <c>InGameShop</c> —— 那是**局内商店**的货 (Scavenger 打到一半在补给点买的),
    /// 不该出现在大厅货架上。
    /// </summary>
    private void StockConsumables()
    {
        foreach (StructuredConsumable consumable in (CraftingManager.Consumables
                                                     ?? Array.Empty<StructuredConsumable>())
                 .Where(c => c != null && HasShopTag(c.Tags)))
        {
            Add(new ShopItemData
            {
                ShopItemID = ConsumableIdBase + consumable.ID,
                LocalizedName = consumable.LocalizedName ?? consumable.WorkingName ?? string.Empty,
                WorkingName = consumable.WorkingName ?? string.Empty,
                IsActive = true,
                SilverPrice = SilverPerConsumable,
                Items = new[]
                {
                    new ItemData
                    {
                        Type = ShopItemType.Consumable,
                        ItemID = consumable.ID,
                        Amount = 1,
                    },
                },
            },
            new ShopEntry(ShopItemType.Consumable, (ushort)consumable.ID,
                          SilverPerConsumable, 0));
        }
    }

    /// <summary>
    /// 角色 —— 用**角色点**买, 银币价留 0。
    ///
    /// 那正是原厂的分工: <c>ShopItemData.CharacterPointPrice</c> 和
    /// <c>ShopItemType.Character</c> 成对存在, 大厅的角色解锁弹窗读的就是
    /// <c>RewardType.CharacterPoints</c>。
    /// </summary>
    private void StockCharacters()
    {
        foreach (CharacterEnum character in CharacterCatalog.Grantable)
        {
            Add(new ShopItemData
            {
                ShopItemID = CharacterIdBase + (int)character,
                LocalizedName = character.ToString(),
                WorkingName = character.ToString(),
                IsActive = true,
                SilverPrice = 0,
                CharacterPointPrice = CharacterPointsPerCharacter,
                Items = new[]
                {
                    new ItemData
                    {
                        Type = ShopItemType.Character,
                        ItemID = (int)character,
                        Amount = 1,
                    },
                },
            },
            new ShopEntry(ShopItemType.Character, (ushort)character,
                          0, CharacterPointsPerCharacter));
        }
    }

    /// <summary>
    /// 三档非蓝图商品的 ShopItemID 起点。
    ///
    /// 蓝图那批直接用 schematic ID (实测最大四位数), 所以后面几档各让开一个
    /// 数量级 —— 撞号的表现是"买 A 发 B", 很难查, 不如一开始就分开。
    /// </summary>
    /// <summary>
    /// 这件东西该不该上大厅货架。
    ///
    /// 原厂靠图鉴里的标签分销路 —— <c>Shop</c> 是"大厅商店有卖",
    /// <c>Drop</c> 是"打得出来", <c>InGameShop</c> 是局内补给点,
    /// <c>Reward</c> 是账号升级送, <c>Premium</c> 是金币档, <c>Debug</c> 是内部测试用。
    ///
    /// 没有标签的一律不上 —— 图鉴里所有正经条目都至少有一个标签,
    /// 空标签只可能是解析出了问题, 那种时候宁可少卖也别乱卖。
    /// </summary>
    private static bool HasShopTag(TagEnum[] tags) =>
        tags != null && tags.Contains(TagEnum.Shop);

    /// <summary>暗码只有一档, 不需要按 ID 铺开, 给个固定号。</summary>
    private const int KeycodeShopItemId = 950_000;

    /// <summary>一个暗码多少银币。见 <see cref="StockKeycodes"/> 的定价说明。</summary>
    private const int KeycodePrice = 120;

    private const int LockboxIdBase = 900_000;

    private const int GadgetBlueprintIdBase = 600_000;

    private const int PartIdBase = 500_000;

    private const int ConsumableIdBase = 800_000;

    private const int CharacterIdBase = 700_000;

    private void Add(ShopItemData item, ShopEntry entry)
    {
        _items.Add(item);
        _byShopItem[item.ShopItemID] = entry;
    }

    /// <summary>
    /// 某个品阶的蓝图标价。
    ///
    /// 代入 <c>tier + 1</c> 而不是 <c>tier</c>, 是因为图鉴里**有 0 阶**
    /// (实测 0~16)。直接用 tier 的话 0 阶算出 0 元, 而 <c>max(tier, 1)</c>
    /// 又会让 0 阶和 1 阶同价 —— 两种都不对。除以 <c>2^指数</c> 是把曲线
    /// 归一到"一阶正好 <see cref="SilverPerBlueprintTier"/>"。
    /// </summary>
    /// <summary>
    /// 银价折成金价。
    ///
    /// ## 11:1 这个比价是从原厂数据反推的, 不是拍的
    ///
    /// 原厂每一档收费都有银/金两份, 两份之比稳定在 11 附近:
    ///
    ///   武器合成费  T1  50:5 = 10     T5  500:45 ≈ 11.1
    ///               T11 11000:1000 = 11
    ///               T16 45000:4090 ≈ 11.0
    ///   改装图绑定  普通 300:30 = 10  稀有 3000:275 ≈ 10.9
    ///               传奇 16000:1450 ≈ 11.0
    ///
    /// 低档取 10、其余取 11。统一按 11 折, 向上取整并保底 1 —— 宁可让金币
    /// 略贵一点, 也不要出现"0 金"这种白送档。
    /// </summary>
    private static int GoldFromSilver(int silver) =>
        silver <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(silver / (double)SilverPerGold));

    /// <summary>见 <see cref="GoldFromSilver"/> 的推导。</summary>
    private const int SilverPerGold = 11;

    /// <summary>某个品阶的零件标价 —— 和蓝图同一条曲线, 只是基数低得多。</summary>
    private int PartPrice(int tier)
    {
        double normalized = Math.Pow(Math.Max(0, tier) + 1, TierExponent)
                            / Math.Pow(2.0, TierExponent);

        return (int)(Math.Round(SilverPerPartTier * normalized / 10.0) * 10);
    }

    private int BlueprintPrice(int tier, TagEnum[] tags = null)
    {
        double normalized = Math.Pow(Math.Max(0, tier) + 1, TierExponent)
                            / Math.Pow(2.0, TierExponent);

        double silver = SilverPerBlueprintTier * normalized;

        if (tags != null && tags.Contains(TagEnum.Premium))
        {
            silver *= PremiumMarkup;
        }

        // 取整到十位 —— 商店里出现 1638 这种数字不好看
        return (int)(Math.Round(silver / 10.0) * 10);
    }

    /// <summary>
    /// <c>Premium</c> 档的加价倍数。
    ///
    /// ## 这是和原版的一处有意分歧
    ///
    /// 原版这一档标的是**金币价** —— 金币是充值货币, 靠
    /// <c>GUI_BuyCurrencyPopup</c> 走 Steam 微交易买。私服没有那条路, 金币也
    /// 没有任何产出 (掉落表不配, <c>RewardLedger</c> 连掉出来的金币都直接忽略),
    /// 所以照搬金币价等于把这几件东西永久锁死。
    ///
    /// 图鉴里 12 件 Premium 武器有 9 件**同时带 <c>Shop</c> 标签**, 也就是说
    /// 它们本来就该在大厅货架上 —— 上一版按普通蓝图定价把它们混了进去,
    /// 一把 T13 的 Premium 和一把普通 T13 一个价。
    ///
    /// 改成收银币但加价: 路留着, 溢价的语义也留着。
    ///
    /// 倍数是我们定的。参照系: 原厂造一把 T13 武器要 1820 金 / 20000 银,
    /// 金币的"含金量"大约是银币的 11 倍 —— 但那 11 倍是**充值**换来的,
    /// 私服里只能靠打。所以取一个"明显更贵但攒得到"的倍数, 而不是照抄 11。
    /// 按玩家实测调。
    /// </summary>
    private const double PremiumMarkup = 3.0;
}
