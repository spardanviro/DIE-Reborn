using System;
using System.Collections.Generic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using _0023Oq;

namespace DIE.Server.Game;

/// <summary>
/// 僵尸的**特殊技能**表 —— 除基础攻击之外, 那些"放出来才像这只怪"的招式:
/// 屠夫的横扫和跳劈、公羊的冲撞和跺地、飘浮者吐酸、自爆者扑上来炸、
/// 尖叫者的恐惧、Puller 的钩舌。
///
/// ## 为什么需要这张表 (真机症状)
///
/// 玩家实测: "camp by night 里面刷出来的一个 butcher 不会放技能只会普攻。"
///
/// 病根是 <see cref="ZombieBrain"/> 只会放 <c>TauntAbilityIdentifier</c>
/// (= 基础攻击), 而每只特殊僵尸的看家本领都在**别的技能槽**上:
///
///   屠夫 (-Mk/-fPb.cs:96-100)   槽0 ButcherStrike(基础) 槽1 ButcherCharge  槽2 ButcherRavage
///   公羊 (-Mk/-zPb.cs:96-100)   槽0 RamStrike(基础)     槽1 RamCharge      槽2 RamStomp
///   飘浮 (-Mk/-iPb.cs:97-100)   槽0 FloaterStrike(基础) 槽1 FloaterRush    槽2 FloaterSpit
///   尖叫 (-Mk/-lPb.cs:110-113)  槽0 ScreamerStrike(基础)槽1 ScreamerSummon 槽2 ScreamerFear
///   自爆 (-Mk/-wPb.cs:102-104)  槽0 SuiciderAttack      槽1 SuiciderStrike(基础)
///   行尸 (-Mk/-rPb.cs:123-127)  槽1 WalkerRush
///
/// 所以这**不是屠夫一只的毛病**: 除 Puller (钩舌上一轮单独特判过) 之外,
/// 六种僵尸的特殊技能一个都没放过。自爆者最离谱 —— 它的基础攻击是近战挠一下,
/// "扑上来自爆"在槽 0 的 <c>SuiciderAttack</c> 上, 于是私服里的自爆者从来不炸。
///
/// ## 为什么按**技能编号**记, 而不按僵尸类型记
///
/// 精英 / Hoarder / Boss 变体**不是**基础类的子类, 全是 <c>_0023iO</c> 的
/// 兄弟类 (<c>IsButcherElite =&gt; this is _0023hPb</c>, 而基础屠夫是
/// <c>_0023fPb</c>)。按类型写表的话精英屠夫一条都匹配不上, 而它的技能栏
/// (-Mk/-hPb.cs:97-101) 和基础屠夫**只差基础攻击的名字**。
///
/// 按技能编号记就不挑类型: 谁的技能栏里装了它谁就用得上。以后精英和 Boss
/// 接进刷怪表 (<c>EventSpawnDirector</c> 的 Elites / SpecialZombieType 至今
/// "映射未定") 时这边一行都不用改。
///
/// ## 射程从哪来
///
/// 一律取自原厂的技能数据, 不是我们拍的 —— 每条都注了出处。三类形状:
///
///   - **冲刺 / 跳跃**: 位移距离 <c>_0023RE</c> (基类按
///     <c>Lerp(0, _0023RE, Age/_00237G)</c> 推自己, -EF/-zX.cs:296)
///   - **投射物**: 存活时长 × 速度 (<c>_0023jE × _0023kE</c>)
///   - **原地 AoE**: 判定半径 (锥形的 <c>_0023FH</c> / 圆形的 <c>_00235j</c>)
///
/// <c>AbilityData.StopDistance</c> **不能**当射程用 —— 钩舌那条写的是 5,
/// 而它真射程 224.75。那个字段的意思是"走到多近就停下来打", 不是技能够多远。
/// </summary>
public static class ZombieSpecials
{
    /// <summary>
    /// 一条特殊技能。
    /// </summary>
    /// <param name="Ability">技能编号。装在哪个槽由僵尸类自己决定, 这里不关心。</param>
    /// <param name="MaxRange">最远打到多远 (圆心到圆心, 判定时再加目标半径)。</param>
    /// <param name="WastedAtMelee">
    /// true = 贴脸时不放。冲刺贴脸放会把自己顶过头, 投射物贴脸放会从目标身上飞过 ——
    /// 两者都是白扔一个长冷却。原地 AoE (横扫 / 跺地 / 恐惧) 是 false, 那些本来就该贴脸放。
    /// </param>
    /// <param name="Source">数字出处, 反编译树里的文件和行号。</param>
    /// <param name="Gate">额外条件, null = 无。第一个参数是施法者, 第二个是目标。</param>
    public sealed record Special(
        AbilityIdentifier Ability,
        float MaxRange,
        bool WastedAtMelee,
        string Source,
        Func<NPC, Entity, bool> Gate = null)
    {
        /// <summary>
        /// **走到多近才放** —— 不是技能够多远 (<see cref="MaxRange"/>)。见 <see cref="Engage"/>。
        /// </summary>
        public float EngageRange =>
            Engage.TryGetValue(Ability, out float engage) ? Math.Min(engage, MaxRange) : MaxRange;
    }

    /// <summary>
    /// 远程/突进技能**开打的距离**, 比理论射程近得多。**这张表是我们定的。**
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "puller 应该走到技能范围内再吐舌头, 现在只要看见了玩家就会原地吐舌头, 导致根本吐不中,
    /// 这个问题可能在其他僵尸的技能上也有, 要走到技能范围内时再放技能, 而不是吸引仇恨了就
    /// 原地放技能。"
    ///
    /// ## 为什么"够得着"不等于"该放"
    ///
    /// 上一版的判据是 距离 ≤ <see cref="MaxRange"/>。钩舌的 MaxRange 是舌头飞满全程的距离
    /// 224.75 (存活 1.55 秒 × 速度 145) —— 远大于仇恨半径 55 和脱战半径 90, 而事件激怒的
    /// 僵尸更是隔着半张图就锁定了目标。于是 Puller 一有目标就原地开嘴: 起手 1.5 秒
    /// (<c>CastTime</c>, -fZ/-aPb.cs) + 舌头再飞一秒多, 人早走开了。
    ///
    /// 原厂 AI 用什么距离开打, 那一半不在了。这里按"起手 + 飞行期间目标来不及走出判定"
    /// 给个开打距离, 远了就**继续往前走** (<c>GoalTarget</c> 一直指着人, 不用另外下命令):
    ///
    ///   PullerHook / 精英钩舌  70    舌头 145/秒, 半秒内到; 远端 224 只剩兜底
    ///   PullerCrawl           70    位移 100 的扑击, 留点余量才扑得中
    ///   FloaterSpit           60    投射物 120/秒, 射程 108
    ///   ScreamerFear          50    投射物, 射程 66
    ///   RamCharge             90    冲 125, 冲太远会冲过头或撞墙
    ///
    /// 其余 (横扫、跺地、跳劈、行尸扑、自爆) 本来射程就短, 用理论射程。
    ///
    /// 另外所有特殊技能都要**看得见**才放 (<see cref="ZombieBrain.CanSee"/>) —— 隔墙吐舌头
    /// 必然打在墙上。
    /// </summary>
    private static readonly Dictionary<AbilityIdentifier, float> Engage = new Dictionary<AbilityIdentifier, float>
    {
        [AbilityIdentifier.PullerHook] = 70f,
        [AbilityIdentifier.PullerEliteHook] = 70f,
        [AbilityIdentifier.PullerCrawl] = 70f,
        [AbilityIdentifier.FloaterSpit] = 60f,
        [AbilityIdentifier.ScreamerFear] = 50f,
        [AbilityIdentifier.RamCharge] = 90f,

        // 2026-09-14 补的囤积者 / 精英 / 巨型, 同一个道理
        [AbilityIdentifier.PullerLeechHook] = 70f,
        [AbilityIdentifier.PullerHoarderCrawl] = 70f,
        [AbilityIdentifier.PullerHoarderSpit] = 60f,
        [AbilityIdentifier.FloaterHoarderSpit] = 60f,
        [AbilityIdentifier.FloaterEliteLob] = 80f,
    };

    /// <summary>
    /// 表本身。
    ///
    /// **没有优先级字段**: <see cref="ZombieBrain"/> 按**技能槽顺序**从前往后试,
    /// 第一条够得着又不在冷却的就放。原厂摆的槽序本身就是优先级 —— 屠夫的
    /// 横扫 (槽 1, 射程 22) 在跳劈 (槽 2, 射程 50) 前面, 于是贴脸横扫、
    /// 拉开了跳劈, 不用我们另编一套排序。
    /// </summary>
    private static readonly Dictionary<AbilityIdentifier, Special> Table = Build();

    private static Dictionary<AbilityIdentifier, Special> Build()
    {
        Dictionary<AbilityIdentifier, Special> table = new Dictionary<AbilityIdentifier, Special>();

        void Add(Special special) => table[special.Ability] = special;

        // ---------------------------------------------------------------- 行尸

        // 扑击: 前冲 35 (1.4 秒), 前方 4 半径 90° 锥形判定。冷却 7 秒。
        Add(new Special(AbilityIdentifier.WalkerRush, 35f, true,
            "-fZ/-sOb.cs:54 位移 _0023RE=35"));

        // ---------------------------------------------------------------- 屠夫

        // 横扫: 前方 22 半径、170° 的两段挥砍 (第 0.2 秒和第 0.45 秒各一下),
        // 挥的同时自己还往前推 36。冷却 7 秒。锥形这么宽, 贴脸放正合适。
        Add(new Special(AbilityIdentifier.ButcherCharge, 22f, false,
            "-fZ/-YNb.cs:48 锥形半径 _0023FH=22 (角度 _0023GH=170)"));

        // 跳劈: 跳 50 (0.7 秒), 落点 10 半径判定。冷却 12 秒 —— 拉开距离时的突进手段。
        Add(new Special(AbilityIdentifier.ButcherRavage, 50f, true,
            "-fZ/-LNb.cs:41 位移 _0023RE=50"));

        // Hoarder 版横扫: 判定和基础版一样 (22 / 170), 位移 48, 冷却 6 秒。
        //
        // ⚠️ 只给 Hoarder 屠夫用。基础屠夫的技能栏里**也装着它** (槽 3,
        // -Mk/-fPb.cs:99), 但整套 Hoarder 数值是按 <c>IsButcherHoarder</c>
        // 分岔的 (跳劈那条就在 -EF/-mvc.cs:122 按它选表)。不拦的话普通屠夫
        // 会在横扫冷却期间再甩一次 Hoarder 横扫, 等于凭空多一倍输出。
        Add(new Special(AbilityIdentifier.ButcherHoarderCharge, 22f, false,
            "-fZ/-QOb.cs:48 锥形半径 _0023FH=22",
            (npc, _) => npc.IsButcherHoarder));

        // -------------------------------------------------------------- 飘浮者

        // 冲撞: 冲 65 (1 秒), 前方 6 半径 120° 判定。冷却 10 秒。
        Add(new Special(AbilityIdentifier.FloaterRush, 65f, true,
            "-fZ/-jOb.cs:56 位移 _0023RE=65"));

        // 吐酸: 投射物, 存活 0.9 秒 × 速度 120 = 108。冷却只有 2 秒, 是它的常规输出。
        Add(new Special(AbilityIdentifier.FloaterSpit, 0.9f * 120f, true,
            "-fZ/-COb.cs:52-54 存活 _0023jE=0.9 × 速度 _0023kE=120"));

        // ---------------------------------------------------------------- 公羊

        // 冲撞: 冲 125 (1.1 秒), 20 半径撞人。冷却 6 秒 —— 表里射程最长的一招。
        Add(new Special(AbilityIdentifier.RamCharge, 125f, true,
            "-V2b/-xNb.cs:86 位移 _0023RE=125"));

        // 跺地: 原地 28 半径 AoE。冷却 15 秒。
        Add(new Special(AbilityIdentifier.RamStomp, 28f, false,
            "-V2b/-YOb.cs:43 判定半径 _00235j=28"));

        // -------------------------------------------------------------- 尖叫者

        // 恐惧: 投射物, 存活 0.55 秒 × 速度 120 = 66。冷却 3 秒。
        Add(new Special(AbilityIdentifier.ScreamerFear, 0.55f * 120f, false,
            "-fZ/-POb.cs:51-53 存活 _0023jE=0.55 × 速度 _0023kE=120"));

        // ScreamerSummon (尖叫者槽 1 / 公羊槽 3) **故意不进表**, 见 <see cref="WhyNoSummon"/>。

        // -------------------------------------------------------------- 自爆者

        // 扑爆: 扑 40 (0.7 秒) 然后炸, 爆炸 28 半径。冷却 4 秒。
        //
        // 这是自爆者存在的**全部意义**, 而它的基础攻击 (TauntAbilityIdentifier)
        // 是槽 1 的近战 SuiciderStrike —— 只放基础攻击的话它会走过来挠你。
        Add(new Special(AbilityIdentifier.SuiciderAttack, 40f, true,
            "-PO/-73.cs:43 位移 _0023RE=40 (爆炸半径 _00235j=28)"));

        // --------------------------------------------------------------- Puller

        // 钩舌: 投射物, 存活 1.55 秒 × 速度 145 = 224.75。命中挂拖拽 buff 把人拉过来。
        // 之后的事 (投射物自己飞、命中挂 buff、buff 拉人掉血) 原厂链子全在, 我们只负责起手。
        Add(new Special(AbilityIdentifier.PullerHook, 1.55f * 145f, true,
            "-fZ/-aPb.cs:45-47 存活 _0023jE=1.55 × 速度 _0023kE=145",
            (_, target) => !target.HasBuff(_0023aD._0023QBc)));

        // 精英版钩舌: 数值一模一样, 只是拖拽 buff 换了一类 (-EF/-OVb.cs:130)。
        // 精英 Puller 现在还刷不出来, 这条是给它接上刷怪表那天先备好的。
        Add(new Special(AbilityIdentifier.PullerEliteHook, 1.55f * 145f, true,
            "-fZ/-xOb.cs:45-47 存活 _0023jE=1.55 × 速度 _0023kE=145",
            (_, target) => !target.HasBuff(_0023aD._0023OVb)));

        // 爬行冲刺: 位移 100 (1.1 秒), 纯位移不带判定。冷却 10 秒。
        //
        // 教程里那只 Puller **装不到它** —— 教程模式把槽 1 换成了钩舌
        // (-Mk/-mPb.cs:109)。我们按技能槽查表, 所以那边自动不会用, 不用特判。
        Add(new Special(AbilityIdentifier.PullerCrawl, 100f, true,
            "-fZ/-zNb.cs:44 位移 _0023RE=100"));

        // ============================================= 囤积者 / 精英 / 巨型 (2026-09-14)
        //
        // 玩家实测: "BOSS 僵尸也不会放技能, 之前精英怪也有这个问题。" 那局的 BOSS 是 Puller 囤积者
        // (_0023dPb, 具名 "SeamusOran")。表里原本只有基础特殊怪的招 —— 囤积者 / 巨型各自换了一套
        // 技能编号, 精英飘浮者的投掷也是单独一招, 查不到就只会平 A (巨型连平 A 都没有: 它们没设
        // TauntAbilityIdentifier, 近战那一招也只能从这张表放)。

        // ---------------------------------------------------- Puller 囤积者 (-Mk/-dPb.cs 槽 0..3)

        // 吸血钩舌: 投射物, 存活 1.82 秒 × 速度 120 = 218.4。冷却 7 秒。
        Add(new Special(AbilityIdentifier.PullerLeechHook, 1.82f * 120f, true,
            "-fZ/-ZOb.cs 存活 _0023jE=1.82 × 速度 _0023kE=120"));

        // 爬行冲刺: 位移 120 (1.1 秒)。冷却 13 秒。
        Add(new Special(AbilityIdentifier.PullerHoarderCrawl, 120f, true,
            "-fZ/-1Nb.cs 位移 _0023RE=120"));

        // 吐酸: 投射物, 存活 1 秒 × 速度 100 = 100。冷却 5 秒。
        Add(new Special(AbilityIdentifier.PullerHoarderSpit, 1f * 100f, true,
            "-fZ/-YNb.cs (类 _0023yNb) 存活 _0023jE=1 × 速度 _0023kE=100"));

        // ---------------------------------------------------- 飘浮者 囤积者 (-Mk/-qPb.cs 槽 2/3)

        // 吐酸: 投射物, 0.9 秒 × 120 = 108。冷却 2 秒。
        Add(new Special(AbilityIdentifier.FloaterHoarderSpit, 0.9f * 120f, true,
            "-fZ/-nxb.cs 存活 _0023jE=0.9 × 速度 _0023kE=120"));

        // 炸弹: 抛到瞄准点, 最远 80 (落点封顶 _0023Gc)。冷却 20 秒。
        Add(new Special(AbilityIdentifier.FloaterBomb, 80f, true,
            "-fZ/-POb.cs (类 _0023pOb) 落点最远 _0023Gc=80"));

        // ---------------------------------------------------- 飘浮者 精英 (-Mk/-oPb.cs 槽 2)

        // 投掷: 抛到瞄准点, 最远 100。冷却 2 秒。
        Add(new Special(AbilityIdentifier.FloaterEliteLob, 100f, true,
            "-fZ/-BNb.cs 落点最远 _0023Gc=100"));

        // 巨型 BOSS 不在这张表里: 它们的全套技能 + 节奏 + 召唤归 MassiveBossBrain。

        return table;
    }

    /// <summary>
    /// 这个技能在表里吗。查不到 = 不是我们会主动放的特殊技能 —— 基础攻击、
    /// Boss 的整套剧本、以及 <see cref="WhyNoSummon"/> 那条都在表外。
    /// </summary>
    public static bool TryGet(AbilityIdentifier ability, out Special special) =>
        Table.TryGetValue(ability, out special);

    /// <summary>诊断用: 表里有几条。</summary>
    public static int Count => Table.Count;

    /// <summary>
    /// 为什么不放 <c>ScreamerSummon</c> (尖叫者槽 1, 公羊槽 3)。
    ///
    /// 名字叫召唤, 实际是**给周围的行尸加急**: 效果对象 <c>_002322b</c>
    /// (-EF/-22b.cs:34-46) 在自己身周 120 以内找目标, 筛选条件是
    ///
    ///     _0023Mb.IsWalker &amp;&amp; _0023Mb.IsAlly(Owner) &amp;&amp; _0023Mb.AsWalker._0023K3
    ///
    /// 命中的挂 <c>_0023Y2b</c> (攻速 +50% / 移速 +50%, 16 秒)。
    ///
    /// 关键在最后那个 <c>_0023K3</c>: 整棵树里它**只被读、从没被写成 true**
    /// (-Mk/-rPb.cs:132 只有复位成 false)。另一个读它的地方是击杀经验表
    /// (-bd/-Zt.cs:65) —— 带这个标志的行尸给 **0 经验**。所以它的语义是
    /// "这只行尸是被召出来的小兵, 不算战功", 而置它的那半边在服务端, 随服务端丢了。
    ///
    /// 于是在私服上这一招**永远找不到目标**: 放它只是白站 1 秒施法 + 吃 6 秒冷却,
    /// 而且它和恐惧互相压冷却 (双向把对方压到至少 1 秒, -EF/-22b.cs:30 与
    /// -EF/-W2b.cs:64) —— 尖叫者会一边对着空气加急, 一边少放恐惧。
    ///
    /// 等哪天做了"被召唤的小兵"并给它们置上这个标志, 再把这条加回表里。
    /// </summary>
    public const string WhyNoSummon =
        "ScreamerSummon 只对带 _0023K3 标志的行尸生效, 而那个标志我们从来没置过";
}
