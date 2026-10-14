using System;
using System.Collections.Generic;

namespace DIE.Server.Game;

/// <summary>
/// 一个事件**刷多少杂兵** —— 按原厂那条强度曲线的**名字**分档。
///
/// ## 为什么只能按名字
///
/// 每个 <c>EnemyVariation</c> 都挂一条 <c>IntensityCurve</c>, 原厂拿它决定每一刻的
/// 刷怪预算。曲线本体在 Unity 资源里, <b>反编译树只剩这个字符串</b>。实测全部
/// 16 个名字 (<c>enemyeventsettings.data</c>):
///
///     ConstantSpawn_Medium  15 个      WaveHeavy             14 个
///     SteadySpawn Low       13 个      Mission_Steal_Truck   11 个
///     Mission_Grab / Mission_FreeCivilians / Mission_Flag /
///     Mission_Facility / Mission_Escort  各 4 个
///     WaveMedium             2 个      SteadySpawn            1 个
///     Scout_* / Base_Crossroads_End_Event 各 1 个 (不走这里)
///
/// 名字本身带着强弱和节奏: Wave* 是"成波"(间隔长、单波大), Constant/Steady 是
/// "持续"(间隔短、单波小), 后缀 Heavy / Medium / Low 是量。**这张表的数是我们定的**,
/// 分档的依据是原厂的名字。
///
/// ## 为什么这件事要紧 (玩家 2026-09-22)
///
/// "crossroad 有些任务不会奖励物资, 或者奖励物资太少, 比如单纯击杀精英怪和 looter 的任务。"
///
/// 查下来: 原厂 45 个 KillEnemy 变体里, 24 个的目标是囤积者/巨型 (死了抛 30 包 × 5 = 150 补给),
/// 另外 21 个的目标只是 Looter / 特殊怪 (每只 3)。后者的物资大头本来在**伴随刷出来的杂兵** ——
/// 它们挂的正是 <c>WaveHeavy</c> / <c>ConstantSpawn_Medium</c>, 而囤积者那批挂的是
/// <c>SteadySpawn Low</c>。
///
/// 上一版所有事件共用一个预算 (0.35 / 20 秒 / 一波 8 只 / 场上 16 只), 于是"重波"的
/// Looter 任务和"低量稳刷"的巨型任务刷一样多的怪 —— 前者被砍掉了物资大头。
///
/// ## 四个数各管什么
///
/// 真正卡住出怪量的常常不是预算而是两个上限: 预算 0.35 按原厂单只成本 (行尸 0.01)
/// 买得起 35 只, 一波 8 只的帽子先生效。所以分档要四个数一起动。
/// </summary>
public static class SpawnIntensity
{
    /// <param name="Seconds">隔多久补一波。</param>
    /// <param name="Budget">一波的预算 (配原厂单只成本: 行尸 0.01 / Spitter 0.05 / 特殊 0.20 / 精英 0.50)。</param>
    /// <param name="MaxPerWave">一波最多几只 —— 预算再宽也不一口气放这么多以上。</param>
    /// <param name="LiveCap">这个事件最多同时留几只活的。</param>
    public readonly record struct Profile(float Seconds, float Budget, int MaxPerWave, int LiveCap);

    /// <summary>
    /// 曲线名认不出来时用这一档 —— 就是上一版所有事件共用的那组数。
    /// </summary>
    public static readonly Profile Default = new Profile(20f, 0.35f, 8, 16);

    /// <summary>
    /// 曲线名 -> 档位。**数是我们定的**, 名字和分组是原厂的。
    ///
    /// 手感过头就整体缩 <see cref="Scale"/>, 别把这张表磨平 —— 那样"原厂的分档"
    /// 和"我们的调整"就分不清了。
    /// </summary>
    private static readonly Dictionary<string, Profile> Profiles =
        new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase)
        {
            // 成波型: 间隔长, 一波压过来
            ["WaveHeavy"] = new Profile(15f, 1.2f, 14, 28),
            ["WaveMedium"] = new Profile(18f, 0.8f, 10, 22),

            // 持续型: 间隔短, 每次少量, 不给喘息
            ["ConstantSpawn_Medium"] = new Profile(10f, 0.5f, 8, 24),
            ["SteadySpawn"] = new Profile(14f, 0.4f, 7, 18),

            // 低量: 囤积者 / 巨型那批用的 —— 物资靠 BOSS 给, 杂兵只是背景
            ["SteadySpawn Low"] = new Profile(22f, 0.3f, 5, 12),

            // 读条型任务: 人得站在点上不动, 维持原来那组数
            ["Mission_Steal_Truck"] = Default,
            ["Mission_Grab"] = Default,
            ["Mission_Flag"] = Default,
            ["Mission_Facility"] = Default,

            // 平民任务: 平民会被咬死, 怪多了没法玩
            ["Mission_FreeCivilians"] = new Profile(20f, 0.3f, 6, 12),
            ["Mission_Escort"] = new Profile(20f, 0.3f, 6, 12),
        };

    /// <summary>
    /// 整张表的总倍率 —— 一个旋钮调全局松紧, 不动分档之间的比例。
    /// </summary>
    public static float Scale { get; set; } = 1f;

    /// <summary>
    /// 这条曲线名在表里吗。测试用 —— 认不出来只会悄悄落回 <see cref="Default"/>,
    /// 没人会发现 (十字路口的变体全都该在表里, 见 <c>EveryVariation_CarriesItsIntensityCurve</c>)。
    /// </summary>
    public static bool Known(string curve) =>
        !string.IsNullOrEmpty(curve) && Profiles.ContainsKey(curve);

    /// <summary>这条曲线名对应的档位。认不出来 (换版本 / Scout 那几条) 就是 <see cref="Default"/>。</summary>
    public static Profile For(string curve)
    {
        Profile profile = !string.IsNullOrEmpty(curve) && Profiles.TryGetValue(curve, out Profile found)
            ? found
            : Default;

        if (Scale == 1f)
        {
            return profile;
        }

        float scale = Math.Max(0.1f, Scale);

        return new Profile(
            Math.Max(1f, profile.Seconds / scale),
            profile.Budget * scale,
            Math.Max(1, (int)Math.Round(profile.MaxPerWave * scale)),
            Math.Max(1, (int)Math.Round(profile.LiveCap * scale)));
    }
}
