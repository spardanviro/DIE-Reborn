using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Stats;
using _0023z;

namespace DIE.Server.Game;

/// <summary>
/// 开场那段(躺在沙滩上 + 醒来动画)期间锁住玩家的操作。
///
/// ## 动画本身不用我们播
///
/// 客户端全都有:
///
///   - <c>-bd/-5cc.cs:157</c> <c>UpdateGameMode</c> 里, LocalPlayer 一出现就播
///     <c>Tutorial_Sleep_Loop</c> (循环躺着), 只播一次
///   - 同文件 <c>:302</c> 状态切到 <c>_0023ecc</c>(1) 时:
///     <c>SkipTutorialIntro()</c> + TutorialWakeUp 语音 + <c>Tutorial_WakeUp</c> 起身
///
/// 服务端要做的只有两件: 把状态按时推进(<c>TutorialDirector</c> 已经在做),
/// 以及**在这期间别让玩家动**。后者原先没人管。
///
/// ## 为什么是这两个 StatEffect
///
/// <c>Ability.CanUse</c> 和 <c>Character.IsAllowedToMove</c> 查的是**两组不同的**
/// 状态效果, 少挂一个就漏一半:
///
///   - <c>GameplayLocked</c> —— <c>Ability.cs:917</c> 第一个检查项, 让所有技能
///     返回 <c>Disabled</c>。**翻滚也是技能** (技能槽 7, UnityClient.cs:2445),
///     所以这一个同时解决了玩家报的"按 SPACE 跳过 intro 时角色还翻滚了一下":
///     客户端那边 SPACE 确实两处都吃到 —— <c>:2324</c> 发 SkipTutorialIntro,
///     <c>:2445</c> 把槽 7 标记为按下, 而且后者**不在** intro 那个条件块里。
///     客户端改不了, 但技能放不放得出来是服务端说了算。
///   - <c>MovementImpair</c> —— <c>IsAllowedToMove</c> (Character.cs:2814) 查它。
///     <c>GameplayLocked</c> 在那一行**不被检查**, 只挂它的话人照样能走。
///
/// 两个是原厂技能里的常见组合(<c>-EF/</c> 下一大批技能对象同时挂这两个),
/// 语义是"施法期间暂时不能操作", 不是眩晕 —— 不会盖掉躺着/起身的动画。
///
/// ## 锁到什么时候
///
/// 状态 2 (<c>_0023Ybc</c>) 客户端才显示"移动"引导 (-bd/-5cc.cs:308), 那之前
/// 玩家本来就不该能动。而 1 -> 2 的判据正是"醒来动画放完"
/// (<c>TutorialDirector</c>, 3 秒), 刚好接上。
/// </summary>
public sealed class TutorialIntroLock
{
    /// <summary>
    /// 已经锁住的玩家 -> 两个待解除的状态效果。
    ///
    /// 键带 <c>NumberOfGlobalCacheUses</c> —— Player 是池化的, 只用下标会串号
    /// (踩坑总结第 1 条)。
    /// </summary>
    private readonly Dictionary<(int Index, int Uses), (StatEffectChange Locked, StatEffectChange Impaired)> _held =
        new Dictionary<(int, int), (StatEffectChange, StatEffectChange)>();

    /// <summary>诊断: 当前锁着几个玩家。</summary>
    public int Held => _held.Count;

    /// <summary>刚上锁/刚解锁的人数, 供宿主打日志。读完自行清零。</summary>
    public int LockedThisTick;
    public int ReleasedThisTick;

    /// <summary>
    /// 玩家"能不能动"的状态变了 —— 供宿主打日志。参数:
    /// (能不能移动, 技能被锁没, 移动被压没, 眩晕没)。
    ///
    /// ## 为什么要这个观测点
    ///
    /// 玩家实测: "碰到敌对NPC, 正在播放对话时无法控制角色, 有一小段时间
    /// 这两个 NPC 也不动了" —— 而且**世界照常跑**(僵尸、Amber、敌对 NPC
    /// 都在正常攻击移动), 只有自己动不了。
    ///
    /// 那就不是卡顿 (卡顿会让所有东西一起停)。服务端侧能让玩家动不了的只有
    /// StatEffect, 客户端侧则可能是 UI 抢焦点或镜头序列 —— 两者修法完全不同,
    /// 而现象一模一样。
    ///
    /// 这条日志能一刀切开: 对话那一刻服务端要是记到了锁, 是我们这边;
    /// 一条都没有, 就是客户端 (那条路本项目已证明是死路, 但至少不会白查)。
    ///
    /// ## 已经排除掉的客户端嫌疑
    ///
    /// 状态 18 客户端只做两件事 (-bd/-5cc.cs:471-490), 两件都查过了:
    ///
    ///   - <c>PlayTransmissionPackage</c> (ViewManager.cs:3136) —— 纯播音频,
    ///     遍历一张表找到包就播, **不碰输入**
    ///   - <c>ShowGuideText</c> (ViewManager.cs:2525) —— 实例化一个 3D UILabel。
    ///     那第二个 <c>Vector3</c> 是**文字的旋转**不是镜头角度, 我一度以为
    ///     它会推镜头
    ///
    /// ## 观测的四个维度
    ///
    /// 参数: (能不能动, 技能被锁没, 移动被压没, 眩晕没, 施法中没, 引导中没)。
    ///
    /// 最后一个是这一版补的 —— "动不了"还有一条路: 玩家卡在某个技能的**引导**里
    /// (<c>StartChanneling</c> 默认 <c>interruptedByMovement: true</c>,
    /// 引导期间本来就不能动), 而服务端没正常结束它。那种情况前四个维度全是正常值。
    ///
    /// "施法中"那一位**永远是 false** —— <c>IsPerformingAnAttack</c> 是 NPC 独有的
    /// 字段, Player 上没有等价的 public 标志。位子留着是为了以后找到入口时不用
    /// 改签名; 现在别把它当证据用。
    /// </summary>
    public System.Action<bool, bool, bool, bool, bool, bool> OnMobilityChanged { get; set; }

    /// <summary>
    /// 上一次看到的那六个维度打包成一个签名。变了才报, 免得每帧刷屏。
    /// </summary>
    private readonly Dictionary<(int Index, int Uses), int> _lastMobility =
        new Dictionary<(int, int), int>();

    /// <summary>
    /// 盯住玩家的行动能力, 变了就报一次。
    ///
    /// 和上锁/解锁那套**分开**跑 —— 它要盯的正是"不该锁的时候锁了", 挂在
    /// 上锁逻辑内部就会互相遮蔽。
    /// </summary>
    public void WatchMobility(GameManager simulation)
    {
        if (simulation?.ActivePlayerList == null || OnMobilityChanged == null)
        {
            return;
        }

        List<Player> players = simulation.ActivePlayerList;
        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null || !player.IsActive)
            {
                continue;
            }

            // Character.IsAllowedToMove() 是 protected, 够不着 —— 自己组合。
            // 它查的就是这两个 (Character.cs:2814 那一行 + Stun 那道通用守卫)。
            bool impairedNow = player.StatEffects._0023Kld(StatEffectType.MovementImpair);
            bool lockedNow = player.StatEffects._0023Kld(StatEffectType.GameplayLocked);

            // ## 击退眩晕**必须一起查** —— 漏了它, 观测点会撒谎
            //
            // 我第一版只查 Stun/MovementImpair/GameplayLocked, 于是遭遇战全程
            // "一条记录都没有", 我据此断言"服务端侧证据干净, 问题在客户端"。
            // **那个结论是错的**: 玩家挨打时挂的是 WeakKnockbackStun, 一个
            // 我没查的独立枚举值。
            //
            // 它由近战判定对象无条件施加 (见 TutorialNpcAI.HostileSwingInterval
            // 里那段考证), 玩家没有豁免路径。
            //
            // 教训: 观测点漏掉一个维度, 得到的"干净"比没有观测更有害 ——
            // 它会把排查方向整个带偏。
            bool stunnedNow = player.StatEffects._0023Kld(StatEffectType.Stun)
                              || player.StatEffects._0023Kld(StatEffectType.KnockbackStun)
                              || player.StatEffects._0023Kld(StatEffectType.WeakKnockbackStun);
            // 引导中不能动是 StartChanneling 的默认行为
            // (`interruptedByMovement: true`), 所以它是"失控"的另一条路。
            //
            // 施法中 (起手阶段) 够不着: `IsPerformingAnAttack` 是 **NPC 独有的**
            // 字段 (NPC.cs:46), Player 没有, 而 Character 上没有等价的 public 标志。
            // 引导这条覆盖了持续时间长的那一半 —— 玩家说"一段时间", 更像引导。
            bool channelingNow = player.IsChanneling;
            bool castingNow = false;
            bool canMove = !impairedNow && !stunnedNow && !channelingNow;

            int signature = (canMove ? 1 : 0)
                            | (lockedNow ? 2 : 0)
                            | (impairedNow ? 4 : 0)
                            | (stunnedNow ? 8 : 0)
                            | (castingNow ? 16 : 0)
                            | (channelingNow ? 32 : 0);

            var key = (player.IndexGlobal, player.NumberOfGlobalCacheUses);

            if (_lastMobility.TryGetValue(key, out int previous) && previous == signature)
            {
                continue;
            }

            _lastMobility[key] = signature;

            OnMobilityChanged(canMove, lockedNow, impairedNow, stunnedNow,
                              castingNow, channelingNow);
        }
    }

    public void Update(GameManager simulation)
    {
        if (simulation?.ActivePlayerList == null)
        {
            return;
        }

        if (!ShouldLock(simulation))
        {
            Release(simulation);
            return;
        }

        List<Player> players = simulation.ActivePlayerList;
        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            var key = (player.IndexGlobal, player.NumberOfGlobalCacheUses);
            if (_held.ContainsKey(key))
            {
                continue;
            }

            // changer 传玩家自己 —— 没有别的 GameObject 可归因, 而且原厂也有
            // 这么用的 (Player.cs:2782 给自己挂 Control)。
            StatEffectChange locked =
                simulation.ApplyStatEffect(player, player, StatEffectType.GameplayLocked);
            StatEffectChange impaired =
                simulation.ApplyStatEffect(player, player, StatEffectType.MovementImpair);

            _held[key] = (locked, impaired);
            LockedThisTick++;
        }
    }

    /// <summary>
    /// 开场还没过去吗。
    ///
    /// 只在教程里成立。判据是状态 &lt; 2 —— 状态 0 是 intro 镜头(躺着),
    /// 状态 1 是醒来动画, 状态 2 才轮到玩家动。
    /// </summary>
    private static bool ShouldLock(GameManager simulation)
    {
        if (simulation?.GameMode is not _0023bd._00235cc tutorial)
        {
            return false;
        }

        return tutorial._0023l9._0023pcc < _0023occ._0023Ybc;
    }

    /// <summary>
    /// 解锁所有人。
    ///
    /// <c>StatEffectChange</c> 是个带 entityID/changerID 的 struct, <c>Remove()</c>
    /// 按 ID 找回目标, 所以玩家中途走掉也不会出事。无条件清表, 免得残留项
    /// 让同一个池位的下一个玩家永远锁着。
    /// </summary>
    private void Release(GameManager simulation)
    {
        if (_held.Count == 0)
        {
            return;
        }

        foreach (var entry in _held)
        {
            var held = entry.Value;
            held.Locked.Remove();
            held.Impaired.Remove();
            ReleasedThisTick++;
        }

        _held.Clear();
    }
}
