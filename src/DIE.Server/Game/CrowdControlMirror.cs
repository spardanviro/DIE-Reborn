using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using _0023Oq;
using _0023Sq;

namespace DIE.Server.Game;

/// <summary>
/// 让玩家脚下那条"控制名称 + 剩余时间"的条**画得出来**。
///
/// ## 玩家实测 (2026-09-13)
///
/// "被控制还是要显示控制类型名称和引导条, 被屠夫扑是 incapacitated, 被 puller 拽是
/// stunned, 被 ram 冲撞是 stunned, 要在角色下方显示控制名称和持续时间条。"
///
/// ## 为什么没画: 客户端是"先按类别判, 再按编号取"
///
/// <c>UI_CharacterHUDsBinding.cs:409-437</c>, 以眩晕为例:
///
///     else if (character.HasBuff(_0023fGc._0023sab))          // 类别标志: 有没有"眩晕类"的 buff
///     {
///         buff = character.GetBuff(_0023aD._0023sab);          // 编号: 取"通用眩晕"那个 buff
///         text = "PCStrings.GUI.StatusEffects.Stunned";
///     }
///     ...
///     if (buff != null) CCBarData = { Text, TimeLeft = _0023fq - Age, TotalTime = _0023fq };
///
/// 第一句看的是 <c>Buff._0023hq</c> (类别标志), 第二句看的是 <c>Buff._0023iq</c> (编号) ——
/// **两个不是一回事**。僵尸那几招挂的都是**自己专属**的 buff, 类别对、编号不对:
///
/// | 招式 | 挂的 buff | 类别标志 | 编号 | 客户端取通用编号 |
/// |---|---|---|---|---|
/// | Puller 钩舌 (PullerHook) | <c>_0023xBc</c> ZombiePinDown | 眩晕 | <c>_0023QBc</c> | null -&gt; 不画 |
/// | Puller 精英钩舌 | <c>_0023PVb</c> ZombiePinDown | 眩晕 | <c>_0023OVb</c> | null -&gt; 不画 |
/// | 屠夫扑倒 (ButcherRavage) | <c>_0023nvc</c> | 失去行动能力 | <c>_0023nvc</c> | null -&gt; 不画 |
/// | 公羊冲撞 (RamCharge) | 击退 + **通用眩晕** <c>_0023sab</c> | 眩晕 | <c>_0023sab</c> | 能画, 但只有 1 秒 |
///
/// 类别判断过了、编号取不到 -&gt; <c>buff == null</c> -&gt; 条不画。这是客户端里的一个坑,
/// 不是同步丢了 (buff 的 <c>_0023fq</c> 本来就在同步字段里, Buff.cs:301-304)。
///
/// ## 做法: 挂一个同类别的**通用** buff 当"镜像"
///
/// 专属控制 buff 挂到玩家身上时, 顺手再挂一个同类别的通用 buff, 时长 = 专属那个的时长;
/// 专属那个结束 (挣脱、施法者死了、到时间) 镜像立刻摘掉。客户端取编号取到的是镜像,
/// 名称和时间条就出来了 —— 文案 "Stunned" / "Incapacitated" 是客户端按类别自己挑的,
/// 正好是玩家要的那两个。
///
/// 镜像不能有副作用:
///
///   - 通用眩晕 <c>_0023sab</c> 自带两个开关 (都在同步字段里, -Sq/-sab.cs:98-103):
///     <c>_0023g0</c> 头顶星星特效、<c>_0023fab</c> 播 Stunned 动画 —— **全关**。
///     它剩下的唯一效果是"眩晕"状态, 而专属那个本来就在眩晕, 重复无害。
///   - 通用失去行动能力 <c>_0023zBc</c> 没有开关: 会放头顶那圈 Incapacitate 特效
///     (正好是原版这个状态的样子)、播 Stunned 动画、**挨打就碎** (-Sq/-zBc.cs 的
///     血量监听)。屠夫扑倒期间一直在挨咬, 所以镜像碎了会补 —— 见 <see cref="Update"/>。
///
/// ## 只管玩家
///
/// 条只画在玩家 (和教程 NPC) 身上 (<c>GetPlayerHUD</c> / <c>GetCharacterPlayerData</c>),
/// 僵尸身上挂镜像纯属浪费同步。
/// </summary>
public sealed class CrowdControlMirror
{
    private sealed class Pair
    {
        public Buff Source;
        public _0023aD SourceId;
        public Entity Owner;
        public Entity Target;
        public _0023fGc Category;
        public Buff Mirror;
        public float LastApplied;
    }

    private readonly List<Pair> _pairs = new List<Pair>();

    /// <summary>自己的钟 (秒), 由 <see cref="Update"/> 推。只用来给补挂限速。</summary>
    private float _now;

    /// <summary>
    /// 本身就是客户端取得到的通用编号 —— 不需要镜像 (公羊冲撞那个 1 秒眩晕就是这种)。
    /// </summary>
    private static readonly HashSet<_0023aD> GenericIds = new HashSet<_0023aD>
    {
        _0023aD._0023PDc, _0023aD._0023sab, _0023aD._0023p9b, _0023aD._0023q9b,
        _0023aD._0023tZb, _0023aD._0023zBc, _0023aD._0023GZb,
    };

    /// <summary>
    /// 镜像碎了 (失去行动能力挨打就碎) 之后最快多久补一次 —— 免得屠夫每咬一口就
    /// 刷一个新 buff 出去。**我们定的。**
    /// </summary>
    public float ReapplySeconds { get; set; } = 0.5f;

    /// <summary>诊断: 累计挂过多少个镜像。</summary>
    public int MirrorsApplied { get; private set; }

    /// <summary>当前挂着的镜像数 (测试/诊断)。</summary>
    public int Active => _pairs.Count;

    /// <summary>
    /// 一个 buff 刚挂到目标身上。是需要镜像的专属控制就挂一个。
    /// </summary>
    public void OnApplied(GameManager simulation, Buff buff, Entity target)
    {
        if (simulation == null || buff == null || target == null || !target.IsPlayer)
        {
            return;
        }

        if (GenericIds.Contains(buff._0023iq))
        {
            return;     // 客户端取得到, 也防止镜像自己再触发镜像
        }

        _0023fGc category = CategoryOf(buff._0023hq);
        if (category == _0023fGc._0023Yc)
        {
            return;
        }

        Pair pair = new Pair
        {
            Source = buff,
            SourceId = buff._0023iq,
            Owner = buff.Owner,
            Target = target,
            Category = category,
        };

        _pairs.Add(pair);
        Apply(simulation, pair, _now);
    }

    /// <summary>
    /// 每帧一次: 专属那个没了就摘镜像; 镜像碎了而专属还在就补。
    /// </summary>
    public void Update(GameManager simulation, float deltaTime)
    {
        _now += deltaTime;
        float now = _now;

        for (int i = _pairs.Count - 1; i >= 0; i--)
        {
            Pair pair = _pairs[i];

            if (!SourceStillOn(pair))
            {
                RemoveMirror(pair);
                _pairs.RemoveAt(i);
                continue;
            }

            if (!MirrorStillOn(pair) && now - pair.LastApplied >= ReapplySeconds)
            {
                Apply(simulation, pair, now);
            }
        }
    }

    /// <summary>
    /// 客户端按什么顺序认类别, 这里就按什么顺序挑 (UI_CharacterHUDsBinding.cs:409-437)。
    /// 只做我们能挂出**干净**镜像的两类: 眩晕、失去行动能力。
    /// </summary>
    private static _0023fGc CategoryOf(_0023fGc flags)
    {
        if ((flags & _0023fGc._0023sab) != 0)
        {
            return _0023fGc._0023sab;
        }

        if ((flags & _0023fGc._0023zBc) != 0)
        {
            return _0023fGc._0023zBc;
        }

        return _0023fGc._0023Yc;
    }

    private void Apply(GameManager simulation, Pair pair, float now)
    {
        float remaining = pair.Source._0023fq - pair.Source.Age;
        if (remaining <= 0.05f)
        {
            return;
        }

        Buff mirror;

        if (pair.Category == _0023fGc._0023sab)
        {
            _0023sab stun = simulation.GetGameObjectFromPool<_0023sab>();
            if (stun == null)
            {
                return;
            }

            stun._0023g0 = false;       // 不要头顶星星 —— 专属那个自己有表现
            stun._0023fab = false;      // 不要 Stunned 动画 —— 会盖掉被拖拽的动画
            mirror = stun;
        }
        else
        {
            _0023zBc incapacitated = simulation.GetGameObjectFromPool<_0023zBc>();
            if (incapacitated == null)
            {
                return;
            }

            mirror = incapacitated;
        }

        // 整段时长给剩余量 —— 客户端条的"总长"读 _0023fq, 进度读 Age。
        mirror._0023fq = remaining;

        simulation.ApplyBuff(mirror, pair.Target, pair.Owner, pair.Source);

        pair.Mirror = mirror;
        pair.LastApplied = now;
        MirrorsApplied++;
    }

    private static bool SourceStillOn(Pair pair) =>
        pair.Target != null && pair.Target.IsActive && !pair.Target.IsDead
        && ReferenceEquals(pair.Target.GetBuff(pair.SourceId, pair.Owner), pair.Source);

    private static bool MirrorStillOn(Pair pair) =>
        pair.Mirror != null && pair.Target.BuffList.Contains(pair.Mirror);

    private static void RemoveMirror(Pair pair)
    {
        if (MirrorStillOn(pair))
        {
            pair.Mirror.Destroy();
        }

        pair.Mirror = null;
    }
}
