namespace DIE.Server.Data;

/// <summary>
/// 玩家**挣来**的东西统一放大多少倍 —— 这是私服的自定倍率, 不是原厂设定。
///
/// ## 为什么是一个倍率而不是把各处数字改大
///
/// 掉落表里那些数每一个都有来处: GP 曲线是原厂
/// <c>FormulaType.SilverCoins</c> 每小时值 ÷ 6, CP 是每小时 450 摊平。
/// 把 700 直接写成 7000, 旁边那句"品阶 1~6 是原数"当场就成了谎话, 下次审校
/// 还会照着改错的数往回推。
///
/// 所以原值原样留着, 倍率单独摆在这里: 读表的人看见的是原厂锚点, 想调倍数
/// 或者想撤回原速只改 <see cref="Multiplier"/> 一处。
///
/// ## 管到哪四样
///
/// 就是结算画面上那四行 (<c>RewardCollection.Serialize</c> 写的四个字段):
///
/// | 玩家看到的 | 代码里的字段 | 产出口 |
/// |---|---|---|
/// | Gear Points | <c>Currencies.Silver</c> | <see cref="MatchDropTable"/> 的 GP 曲线 |
/// | Character Points | <c>Currencies.CharacterPoints</c> | 同上, CP 曲线 |
/// | Cash | <c>Currencies.Gold</c> | 新手任务全完成那一次性奖励 |
/// | 账号经验 | <c>AccountExperience</c> | <c>LidgrenHost.PlayerExperience</c> |
///
/// 名字对不上是原厂自己的历史包袱, 别改: 客户端
/// <c>CurrencyItemInfo.Description</c> 把 <c>CribCurrencyType.Gold</c> 显示成
/// **Cash**、<c>Silver</c> 显示成 **GearPoints**, 而
/// <c>RewardManager.cs:535</c> 那个 switch 把 <c>RewardType.GearPoints</c> 加到
/// <c>Currencies.Silver</c> 上。双端都按这套对应走, 改一边就错位。
///
/// ## 不管什么
///
/// **花掉的不跟着涨** —— 商店价格、打造手续费、绑定费一律原样
/// (<c>ShopCatalog</c> / <c>MonetizationStatics</c>)。倍率只加在进账那一侧,
/// 所以效果就是"东西便宜了十倍", 这正是想要的。
///
/// 研究点 (ResearchPoints) 不在里面 —— 玩家点的是结算那四行, 而结算画面
/// 压根不显示 RP (序列化里那四个字段之后是三个写死的 0)。
/// </summary>
public static class RewardBoost
{
    /// <summary>
    /// 倍率。1 = 原速。
    ///
    /// 设成可写是为了测试能钉住"×1 时和原值一致"这条 —— 跑服时没人改它。
    /// </summary>
    public static int Multiplier { get; set; } = 10;

    /// <summary>放大一个整数额度, 溢出时停在 <c>int.MaxValue</c>。</summary>
    public static int Scale(int amount)
    {
        long scaled = (long)amount * Multiplier;
        return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
    }

    /// <summary>放大一个无符号额度 (经验), 溢出时停在 <c>uint.MaxValue</c>。</summary>
    public static uint Scale(uint amount)
    {
        ulong scaled = (ulong)amount * (ulong)Multiplier;
        return scaled > uint.MaxValue ? uint.MaxValue : (uint)scaled;
    }

    /// <summary>放大一个曲线值。</summary>
    public static float Scale(float amount) => amount * Multiplier;

    /// <summary>
    /// 放大一整条曲线, 返回新数组 —— 原数组是 <c>static readonly</c> 的共享表,
    /// 就地改会在第二次 <c>Build()</c> 时又乘一遍。
    /// </summary>
    public static float[] Scale(float[] points)
    {
        float[] scaled = new float[points.Length];

        for (int i = 0; i < points.Length; i++)
        {
            scaled[i] = points[i] * Multiplier;
        }

        return scaled;
    }
}
