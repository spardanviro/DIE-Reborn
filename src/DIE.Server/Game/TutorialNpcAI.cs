using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Stats;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 教程 NPC (Amber 等) 的行为驱动 —— 被救之后跟着玩家走、顺手打僵尸。
///
/// ## 原厂没有"被裁掉的 AI"可以找回
///
/// 教程 NPC 的基类 <c>_0023Mk._0023YO</c> 把两个 AI 钩子**覆盖成空**:
///
///     protected override void _00239N(float dt, float t) { }   // 每帧
///     protected override void _0023aO() { }                    // 销毁
///
/// 这不是链接器裁的 —— 同基类 (<c>_0023iO</c>) 的 Boss 们
/// (<c>_002302</c> MassiveScreamer, <c>_0023APb</c> RamBoss) 那两个都有实现,
/// 在同一棵树里完好无损。也就是说**教程 NPC 原厂就没有自主 AI**:
/// 她们的行为由服务端的教程逻辑逐条驱动, 而那部分随服务端丢失。
///
/// 所以这个文件是**我们写的决策层**, 不是还原。用的原语全是原厂的,
/// 而且和 <see cref="MinionBrain"/> 是同一套 (寻路走 GoalTarget, 攻击走
/// TryUseAbility) —— 两边都是"跟着一个人, 顺手打附近的敌人", 没理由两种写法。
///
/// ## ⚠️ <see cref="Prime"/> 必须先跑, 否则她**根本动不了**
///
/// <c>_0023YO._0023vO()</c> (-Mk/-YO.cs:89) 是给移动参数赋值的地方:
///
///     _0023ON = 900f; _0023PN = 100f; _0023QN = 35f;    // 走: 加速度/摩擦/速度因子
///     _0023RN = 900f; _0023SN = 100f; _0023TN = 45f;    // 跑
///
/// 而它在整棵客户端树里**零调用者** —— 调它的是服务端。不调的话
/// <c>_0023iO.ResetNPC</c> 里那句 `_Acceleration = _0023ON` 拿到的是 0,
/// 加速度为 0 就是站着不动, 跟有没有决策层无关。
///
/// 这是 Amber 一直杵在原地的**第一个**原因; 第二个才是没人告诉她去哪。
///
/// ## 她的血量和武器是自带的, 不用我们发
///
/// <c>_0023YO._00238N()</c> 是 <c>sealed override</c>, 生成时由
/// <c>_0023iO.SpawnNPC()</c> 自动跑, 里面已经:
///
///     AddWeapon(0, 1006, ...);   // 近战
///     AddWeapon(1, 1009, ...);   // 霰弹枪
///     _0023xz(0);                // 装备槽 0
///
/// 技能条也注册好了 (LightPrimary/LightSecondary/ShotgunPrimary/ShotgunSecondary/
/// Dash/TutorialNPCResurrectChannel), 血量由 Vitality rating 算。
///
/// ## Puller 的钩子不用我们解
///
/// <c>_0023Sq._0023xBc</c> (PullerHookDebuff) 自带解除条件
/// (-Sq/-xBc.cs:232): `if (Owner.AsCharacter.IsDead) Destroy();`
/// —— Puller 一死 buff 就掉, <c>_0023Jq()</c> 完整清理 Stun / 强制朝向 /
/// 动画 / 语音。我们只要**别在她还被 Stun 时瞎指挥**。
///
/// 顺带一提, 那个 buff 对教程 NPC 特判过: `if (Target.IsTutorialNPC) _0023fq = 9999f`
/// (:115-118) —— 永不超时, 也就是"玩家不来救她就一直被拉着"。原厂设计。
/// </summary>
public sealed class TutorialNpcAI
{
    /// <summary>
    /// 玩家走出这么远才起步追。
    ///
    /// ## 这个值调过四轮
    ///
    ///   15  贴着玩家挪, 每一小步都重新起步 —— "一直紧跟"
    ///   50  起步了但追不上 (跑档 45 慢过玩家), 贴在圈边界上, 观感同上
    ///  250  一个屏幕。不用追了, 但玩家嫌远: 她掉队太久, 而且**没有寻路**,
    ///       距离一拉开就会顶着地图边一直走
    ///  150  当前值。够远不至于跟屁虫, 又不至于让她走出视野去撞墙
    ///
    /// 上限受限于**她不会寻路**: <c>GoalTarget</c> 是直奔目标的, 中间隔着
    /// 建筑或地形就卡住。圈画得越大, 她掉队时要穿越的地形越复杂, 撞墙的
    /// 概率越高。所以这个值不能一味往大调 —— 真正的解法是寻路, 那是另一件事。
    ///
    /// 配 <see cref="StopDistance"/>(20) 形成迟滞区: 越过 150 进入归队,
    /// 一路跑到 20 才停 (状态机见 <see cref="Steer"/>)。
    /// </summary>
    public float FollowDistance { get; set; } = 150f;

    /// <summary>在这个半径内的敌人才追。</summary>
    public float HuntRadius { get; set; } = 40f;

    /// <summary>
    /// 出手距离 (再加上双方碰撞半径, 见 <see cref="TryAttack"/>)。
    /// 和 <see cref="MinionBrain.AttackStopDistance"/> 一个量级 ——
    /// 那边是召唤物, 这边是人形角色, 稍大一点。
    /// </summary>
    public float AttackStopDistance { get; set; } = 6f;

    /// <summary>
    /// 跟到这么近就停。
    ///
    /// 比 <see cref="FollowDistance"/> 小, 中间那段是**迟滞区** ——
    /// 少了它, NPC 会在阈值上反复起步/刹车, 看起来像抽搐。
    /// </summary>
    public float StopDistance { get; set; } = 20f;

    /// <summary>诊断: 上一帧驱动了几个 NPC。</summary>
    public int LastActive { get; private set; }

    /// <summary>
    /// 给场上所有教程 NPC 装上移动参数。**生成之后必须调一次**, 否则她们动不了。
    ///
    /// 幂等 (只是重新赋同样的值), 但**别每帧调** —— 末尾那句 <c>_0023dO()</c>
    /// 会把档位重设成"走", 每帧调就等于把 <see cref="Update"/> 里选的"跑"档
    /// 一直冲掉。
    /// </summary>
    /// <returns>装上的个数。</returns>
    public static int Prime(GameManager simulation)
    {
        if (simulation?.ActiveEntityList == null)
        {
            return 0;
        }

        int primed = 0;

        foreach (Entity entity in simulation.ActiveEntityList)
        {
            if (entity is _0023Mk._0023YO npc && npc.IsActive)
            {
                npc._0023vO();
                npc._0023dO();   // 把 _Acceleration/_Friction/_TargetSpeedFactor 刷成非零
                primed++;
            }
        }

        return primed;
    }

    /// <summary>
    /// 每帧一次。
    /// </summary>
    /// <param name="leader">要跟随的玩家。传 null 她们就只留在原地。</param>
    /// <param name="deltaTime">这一帧多久。卡住检测要用。</param>
    public void Update(GameManager simulation, Player leader, float deltaTime = 1f / 30f)
    {
        if (simulation?.ActiveEntityList == null)
        {
            LastActive = 0;
            return;
        }

        int active = 0;
        List<Entity> entities = simulation.ActiveEntityList;

        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i] is not _0023Mk._0023YO npc || !npc.IsActive || npc.IsDead)
            {
                continue;
            }

            // **三类教程 NPC 走三条路。**
            //
            // 判据直接和队伍常量比, 不看 leader —— leader 可能是 null
            // (玩家还没进来 / 刚断线), 那时跟随分支会走"原地待着"那一支,
            // 但**敌人仍然不该被跟随逻辑碰**。拿 leader.TeamId 当判据的话
            // 这里会 NRE, NpcWithoutALeader_Idles 当场抓到了。
            if (npc.TeamId == TutorialEntitySpawner.EnemyTeam)
            {
                active++;
                SteerHostile(simulation, npc, leader, deltaTime);
                continue;
            }

            // 卡车那两个 (Wrench/Trapper) 汇合**之前**是站桩剧情人物。
            // 玩家实测: "卡车那的队友现在直接跑过来了, 应该在卡车那里等着"。
            // 汇合之后玩家要求"和 amber 一样行动" —— 见 HasJoined。
            //
            // 判据是**类名**不是队伍: 他们和 Amber 同队 (蓝), 队伍分不开。
            if (TutorialEntitySpawner.IsTruckCrew(npc))
            {
                if (!HasJoined(simulation, npc, leader))
                {
                    Idle(npc);
                    continue;
                }

                active++;
                Steer(simulation, npc, leader, deltaTime);
                continue;
            }

            if (npc.TeamId != TutorialEntitySpawner.PlayerTeam)
            {
                continue;
            }

            active++;
            Steer(simulation, npc, leader, deltaTime);
        }

        LastActive = active;
    }

    /// <summary>
    /// 敌对 NPC 的仇恨半径。
    ///
    /// **这是我们定的**, 没有原厂依据 —— 和 <see cref="ZombieBrain.AggroRadius"/>
    /// 一样, 仇恨配置属于被裁掉的 AI 数据。
    ///
    /// ## 必须小于对话触发半径
    ///
    /// 玩家要的顺序是: 走近 -> **先播遭遇战对话** -> 再进仇恨范围 -> 才开打。
    /// (原话: "敌对NPC应该站在触发对话的地方, 玩家进入仇恨范围后开始攻击。")
    ///
    /// 对话由客户端在教程状态 18 播, 而进入状态 18 的门槛是
    /// <see cref="TutorialDirector.AmbushApproachRadius"/>(120)。所以这个值
    /// 必须比它小, 否则敌人会在对话之前就冲出来 —— 上一版取 200, 玩家看到的
    /// 正是"没有站在指定位置等待遭遇"。
    ///
    /// 105 是玩家实测校准的第二版 (80 -> "再增加一点")。仍然卡在 120 以下,
    /// 那条大小关系有测试钉着。手感不对调这里, 别去动僵尸那个 ——
    /// 两者是不同的东西。
    /// </summary>
    public float HostileAggroRadius { get; set; } = 105f;

    /// <summary>
    /// 敌对 NPC 追出去多远就回防。
    ///
    /// 比仇恨半径大一截, 免得玩家在边界上进出时他反复起步/刹车。超出之后
    /// 走回自己的站位 (<see cref="_post"/>) —— 遭遇战是**守在那片空地**,
    /// 不是一路追杀到卡车。
    /// </summary>
    public float HostileLeashRadius { get; set; } = 150f;

    /// <summary>
    /// 敌对 NPC 的初始站位。第一次见到他时记下来, 脱战后回这里。
    ///
    /// 键带池复用次数, 免得实体回池后串号 (和 <see cref="_returning"/> 同理)。
    /// </summary>
    private readonly Dictionary<(int Index, int Uses), Vector2> _post =
        new Dictionary<(int, int), Vector2>();

    /// <summary>
    /// 敌对 NPC (遭遇战的 Charlie / Fuse): 守在站位上, 进了仇恨半径就打。
    ///
    /// ## 他们原厂也没有 AI
    ///
    /// 和 Amber 一样是 <c>_0023Mk._0023YO</c> 的子类, 两个 AI 钩子被覆盖成空
    /// (见类说明)。所以这套决策是**我们写的**, 用的原语和 <see cref="Steer"/>
    /// 完全相同 (GoalTarget 寻敌 + <c>Attack</c> 出手), 只是目标换成玩家一侧。
    /// </summary>
    private void SteerHostile(GameManager simulation, _0023Mk._0023YO npc, Player leader,
                              float deltaTime)
    {
        var key = Key(npc);

        if (!_post.TryGetValue(key, out Vector2 post))
        {
            post = npc.Position;
            _post[key] = post;
        }

        if (npc.StatEffects._0023Kld(StatEffectType.Stun))
        {
            Idle(npc);
            return;
        }

        // 离站位太远就先回去 —— 守点, 不是追杀。
        if (Vector2.Distance(npc.Position, post) > HostileLeashRadius)
        {
            npc.UseGoalTarget = false;
            npc.GoalTarget = null;
            npc.SetGoalPosition(post);
            npc._0023eO();
            return;
        }

        Entity target = NearestPlayerSideTarget(simulation, npc, HostileAggroRadius);
        if (target == null)
        {
            Idle(npc);
            return;
        }

        npc.GoalTarget = target;
        npc.UseGoalTarget = true;
        npc._0023eO();

        if (ReadyToSwing(npc, deltaTime))
        {
            TryAttack(npc, target);
        }
    }

    /// <summary>
    /// 敌对 NPC 两次挥击之间至少隔这么久。
    ///
    /// ## 为什么要额外加一道节流
    ///
    /// 他们用的 <c>LightPrimary</c> 是**玩家角色的**基础攻击, 冷却也是玩家的
    /// (<c>BalanceData._0023f5f._0023Fab</c>)。真机日志实测: 03:55:26.363 /
    /// 26.802 / 27.224 —— **每 0.42 秒一下**, 而僵尸是 1.2 秒左右。
    /// 两个人一起打就是每 0.2 秒一次命中。
    ///
    /// 玩家实测: "只要被NPC攻击就会无法操作个两三秒", 后来他自己找到了关键
    /// 线索 —— 挨打时头上飘 **interrupt**。那条线索把成因坐实了:
    ///
    /// ## 每一次近战命中都会给玩家挂 1 秒的击退眩晕 (原厂行为)
    ///
    /// <c>_0023EF._00233wc</c> (近战判定对象, 日志里的 changer) 命中后
    /// **无条件**施加击退 buff <c>_0023Sq._0023nE</c> (:284-296):
    ///
    ///     bool flag2 = true;
    ///     if (!target.CanBeMeleeKnockBacked &amp;&amp; target.IsNPC) flag2 = false;  // 只豁免 NPC
    ///     if (flag2) { ...._0023PE = true; _0023ME = false;
    ///                  _0023fq = _0023AF; ... }                    // _0023AF = **1f**
    ///
    /// 而那个 buff 挂上时 (`-Sq/-nE.cs:117-128`):
    ///
    ///     if (_0023PE) {
    ///         if (target.IsPlayer &amp;&amp; target.AsPlayer.IsCasting)
    ///             SendInterruptText(...);                          // ← 玩家看到的 interrupt
    ///         ApplyStatEffect(..., _0023ME ? KnockbackStun : WeakKnockbackStun);
    ///     }
    ///
    /// 也就是说 **1 秒的 WeakKnockbackStun**, 玩家**没有豁免路径**
    /// (那个 `CanBeMeleeKnockBacked` 守卫只管 NPC)。这是原厂设计。
    ///
    /// ## ⚠️ 眩晕已经从机制上根除了, 这个值不再背那个锅
    ///
    /// 我一度靠拉长间隔来缓解定身 (0.42 -> 1.5 -> 3 秒)。那是**治标**, 而且
    /// 1.5 那一版还算错了人数 —— 两个人错开就是每 0.75 秒挨一下, 1 秒的眩晕
    /// 被反复刷新, 等于没改。
    ///
    /// 真正的根因是近战击退无条件落在玩家身上 —— 原厂靠
    /// <c>CanBeMeleeKnockBacked</c> 拦它, 我们没接。现在接上了
    /// (见 <c>GameManagerServer.ResistsMeleeKnockback</c>), 挨打不再定身,
    /// 间隔就只是**手感**问题了。
    ///
    /// 2 秒: 两人错开每 1 秒挨一下。他们毕竟是剧情杂兵, 不该有玩家的攻速
    /// (LightPrimary 的玩家冷却是 0.42 秒)。**这是我们定的**, 原厂那两个
    /// 该用什么节奏无从考证 (服务端 AI 随服务端丢失)。手感不对调这里。
    /// </summary>
    public float HostileSwingInterval { get; set; } = 2f;

    /// <summary>每个敌对 NPC 距离下一次挥击还有多久。</summary>
    private readonly Dictionary<(int Index, int Uses), float> _swingCooldown =
        new Dictionary<(int, int), float>();

    /// <summary>攒够间隔了吗。攒够就归零重来。</summary>
    private bool ReadyToSwing(_0023Mk._0023YO npc, float deltaTime)
    {
        var key = Key(npc);

        _swingCooldown.TryGetValue(key, out float remaining);
        remaining -= deltaTime;

        if (remaining > 0f)
        {
            _swingCooldown[key] = remaining;
            return false;
        }

        _swingCooldown[key] = HostileSwingInterval;
        return true;
    }

    /// <summary>
    /// 敌对 NPC 的目标: **只认玩家一侧**, 不打僵尸。
    ///
    /// ## 为什么不能用 <see cref="NearestEnemy"/>
    ///
    /// 僵尸是 Neutral, 对 EnemyTeam 也是敌人。上一版让敌对 NPC 用通用的
    /// "最近的敌人", 结果他们跑去和路过的僵尸打架 —— 真机日志里
    /// 17:08:48~53 两个人被僵尸的普通攻击 (`_00233wc` / `_0023DH`) 打死,
    /// 而玩家 17:09:01 才走到。状态 18 进去 48 毫秒就判"打赢遭遇战"推到 19,
    /// 玩家眼里就是"敌对 NPC 没有站在指定位置等待遭遇"。
    ///
    /// 遭遇战是**剧情桥段**: 那两个人在那儿等玩家, 不是在那儿求生。
    /// </summary>
    private static Entity NearestPlayerSideTarget(GameManager simulation,
                                                  _0023Mk._0023YO npc, float radius)
    {
        Entity best = null;
        float bestDistanceSquared = radius * radius;

        List<Entity> entities = simulation.ActiveEntityList;
        for (int i = 0; i < entities.Count; i++)
        {
            Entity candidate = entities[i];

            if (candidate == null || !candidate.IsActive || candidate.IsDead
                || ReferenceEquals(candidate, npc))
            {
                continue;
            }

            // 玩家本人, 或者玩家那一队的教程 NPC (Amber / 卡车队友)。
            bool playerSide = candidate.IsPlayer
                              || (candidate.IsTutorialNPC
                                  && candidate.TeamId == TutorialEntitySpawner.PlayerTeam);

            if (!playerSide || candidate.IsNonTargetable())
            {
                continue;
            }

            float distanceSquared = Vector2.DistanceSquared(npc.Position, candidate.Position);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// 卡车队友和玩家汇合了没有。
    ///
    /// 玩家要求: "卡车那里的队友汇合后应该和 amber 一样行动, 而不是站在原地不动。"
    ///
    /// ## 门槛就是教程状态推过 19
    ///
    /// 状态 19 (<c>_00232sb</c>) -> 20 的推进条件本身就是"玩家走到卡车队友
    /// 身边" (<c>TutorialDirector.ReachedTruckCrew</c>)。所以**状态到了 20
    /// 就等于汇合发生过了**, 这里直接用它, 不必再各自判一次距离。
    ///
    /// ## 各自判距离是错的 —— 有一个队友永远汇合不了
    ///
    /// 第一版要求玩家走到**这个 NPC** 30 单位内。可地图上那两个 NPC_TRUCK
    /// 标记分别在 (-162,3335) 和 (-210,3460), 而卡车在 (-181,3353) ——
    /// 后者离卡车 **110 单位**。玩家守在卡车边交货、打怪, 从头到尾够不到他,
    /// 于是他一直站桩。玩家实测: "卡车附近的队友不帮忙打僵尸"。
    ///
    /// 而状态机那边只要**任意一个**够到就推进 (它俩算一伙人), 两边判据不一致
    /// 正是这个洞的来源。现在统一到状态上。
    ///
    /// 状态 19 之前不跟 —— 那时 AMBUSH 的引导箭头**就指着他们**
    /// (-bd/-5cc.cs:476-488), 他们要是提前跑过来迎接, 箭头指的位置和
    /// "去卡车那边"这个意图就对不上了。
    /// </summary>
    private static bool HasJoined(GameManager simulation, _0023Mk._0023YO npc, Player leader)
    {
        return simulation?.GameMode is _0023bd._00235cc tutorial
               && tutorial._0023l9._0023pcc >= _0023z._0023occ._0023mcc;
    }

    /// <summary>
    /// 卡车那片场地的半径。
    ///
    /// **我们定的。** 依据是地图上那几个点围出来的范围 (实测 dump):
    ///
    ///   卡车 (TRUCK 标记)      (-188, 3405)
    ///   交付点 (Trucker)       (-172, 3363)
    ///   队友甲 (NPC_TRUCK)     (-162, 3335)
    ///   队友乙 (NPC_TRUCK)     (-210, 3460)   ← 离下面那个中心最远, 约 88
    ///   客户端箭头指的坐标      (-181, 3353)   ← 拿它当中心
    ///
    /// 取 130 —— 把两个队友和交付点都圈进去, 还留一点余量, 而遭遇战那片地
    /// (60.7, 3533) 离中心 294, 稳稳在外。
    /// </summary>
    public static float TruckAreaRadius { get; set; } = 130f;

    /// <summary>
    /// 这个位置在卡车那片场地里吗。
    ///
    /// **状态机和 AI 读的是这同一个函数** —— 先前两边各判各的 (状态机判
    /// "任一队友 30 内", AI 判"这个队友 30 内"), 结果有个队友永远不入队。
    /// 判据只留一份就不会再错开。
    /// </summary>
    public static bool IsInsideTruckArea(Vector2 position) =>
        Vector2.Distance(position, TutorialFinaleDirector.TruckPosition) <= TruckAreaRadius;

    private void Steer(GameManager simulation, _0023Mk._0023YO npc, Player leader, float deltaTime)
    {
        // 还没被救出来 —— 她就该在营救现场待着, 哪也不去。
        //
        // ## 为什么不能只靠 Stun 判
        //
        // Stun 来自 PullerHookDebuff, 也就是说"她不动"依赖于**钩子已经挂上**。
        // 而钩子现在是玩家走近才挂的 (LidgrenHost.TryHookTutorialNpc —— 为了让
        // 客户端把舌头画出来, 必须走真技能链路, 而技能得有人在场看), 于是中间
        // 空出一段没人管的时间: 玩家还在海滩, Amber 已经自由了, 下面那条跟随
        // 逻辑就让她一路跑到玩家身边。玩家报告的正是这个 —— "打第一道 fence 时
        // 发现 Amber 已经寻路到我面前了"。
        //
        // 门控的判据是"**她还没被困过**", 不是剧情状态 —— 第一版按状态 8 一刀切,
        // 结果玩家用技能打断 Puller 的引导时她被按在原地不还手。详见 ShouldStayPut。
        if (ShouldStayPut(simulation, npc))
        {
            Idle(npc);
            return;
        }

        // 还被 Puller 拉着 —— 别动她。
        //
        // 判据是 Stun 而不是"有没有那个 buff": PullerHookDebuff 靠
        // ApplyStatEffect(Target, StatEffectType.Stun) 定住她 (-Sq/-xBc.cs:104),
        // 判 Stun 就同时覆盖了别的定身来源 (Screamer 的恐惧之类), 而且
        // NPC 的移动守卫 IsAllowedToMove() 看的本来也是 StatEffect。
        if (npc.StatEffects._0023Kld(StatEffectType.Stun))
        {
            Idle(npc);
            return;
        }

        // 主人没了就原地待着, 别去追一个空引用 —— UseGoalTarget = true 且
        // GoalTarget == null 会让 NPC.SerializeCharacter (:818) 直接 NRE,
        // 那行没有空判。(和 MinionBrain 踩的是同一个坑。)
        if (leader == null || !leader.IsActive)
        {
            Idle(npc);
            return;
        }

        // 玩家倒下了 —— 走过去把他扶起来, 别在旁边继续打僵尸。
        if (leader.IsDead && ShouldRevive(simulation))
        {
            Revive(simulation, npc, leader, deltaTime);
            return;
        }

        // ## 归队是个**显式状态**, 不能靠 GoalTarget 反推
        //
        // 玩家要的规则: "如果超出距离, 首要执行跟随, 移动到人物身边,
        // 路上就算遇到僵尸也不会打; 在跟随距离内首要攻击敌人。"
        // 而且"触发跟随后应该直接跑到玩家身边很近的范围内"。
        //
        // 先前用 `GoalTarget == leader` 当作"她正在归队"的标志 —— **不可靠**:
        // 归队路上只要打架那一支抢过一次 GoalTarget (哪怕一帧), 标志就断了,
        // 下一帧阈值退回 FollowDistance, 她就地停下。玩家两轮都报告
        // "只走到圈内就马上停了", 根子在这。
        //
        // 现在用一张显式的表: 越过 FollowDistance 进入归队, 一路跑到
        // StopDistance 才退出, 中间不受别的分支影响。
        float distance = Vector2.Distance(npc.Position, leader.Position);
        var key = Key(npc);
        bool returning = _returning.Contains(key);

        if (!returning && distance > FollowDistance)
        {
            returning = true;
            _returning.Add(key);
        }

        if (returning)
        {
            if (distance <= StopDistance)
            {
                _returning.Remove(key);
                _detour.Remove(key);
                Idle(npc);
                return;
            }

            Follow(simulation, npc, leader, deltaTime);
            return;
        }

        // 不在归队 —— 卡住状态没意义了, 清掉免得下次归队一上来就以为自己卡着
        _detour.Remove(key);

        Entity enemy = NearestEnemy(simulation, npc, HuntRadius);
        if (enemy == null)
        {
            Idle(npc);
            return;
        }

        npc.GoalTarget = enemy;
        npc.UseGoalTarget = true;
        npc._0023eO();          // 扑向敌人用跑档
        TryAttack(npc, enemy);
    }

    /// <summary>
    /// 走到离尸体这么近就开始施法。
    ///
    /// **上限是 20** —— <c>GameMode.GetResurrectDirection</c> (:1273) 只认
    /// 20 以内的倒地队友, 超出就返回 <c>Vector2.Zero</c>, 引导 buff
    /// 0.3 秒后自毁 (<c>_0023Sq._0023jyc._0023Iq</c>)。留点余量取 15。
    /// </summary>
    public float ReviveRange { get; set; } = 15f;

    /// <summary>
    /// 现在该由她来救人吗 —— 判据抄客户端死亡面板, 一个字都不改。
    ///
    /// <c>UI_DeathPanelBindings.Update()</c> (:84-93) 在教程里这样选提示语:
    ///
    ///     if (GameModeData._0023pcc &lt; _0023occ._0023hcc)  -> "在起点复活"
    ///     else                                              -> "等待队友救援"
    ///
    /// 也就是说**状态 8 起客户端就在让玩家等 NPC**。玩家实测看到的正是
    /// 这句"等队友救援", 他以为是显示错了 —— 其实是显示对了, 缺的是
    /// Amber 真的走过来这件事。所以门槛必须和这句话同一个: 客户端说等她,
    /// 她就得来; 客户端说自己在起点复活 (状态 8 之前), 她就别掺和。
    /// </summary>
    private static bool ShouldRevive(GameManager simulation)
    {
        if (simulation?.GameMode is not _0023bd._00235cc tutorial)
        {
            return false;
        }

        return tutorial._0023l9._0023pcc >= _0023z._0023occ._0023hcc;
    }

    /// <summary>
    /// 她**已经被钩过**了 —— 也就是说困住她的那件事已经发生过。
    ///
    /// 见 <see cref="ShouldStayPut"/>: 光看剧情状态会把"钩子中途被打断"那种
    /// 情况判错。这个标志一旦立起来就不再放下, 因为"她被困过"是既成事实。
    /// </summary>
    private bool _wasHooked;

    /// <summary>
    /// 她还被困在营救现场吗。
    ///
    /// ## 为什么不能只看剧情状态
    ///
    /// 第一版写的是"状态 &lt; 8 就别动", 分界和 <see cref="ShouldRevive"/> 一样。
    /// 玩家实测打出了漏洞: **用技能控制 Puller**(而不是普通攻击)打断它的引导时,
    /// 钩子当场就掉了, 她已经自由了 —— 可这时 Puller 还没死, 状态仍然是 7,
    /// 于是她被这个门控按在原地, 不会像原版那样立刻回身打 Puller。
    /// (玩家原话: "如果不是通过普通攻击的方式停止 amber 的 debuff, 而是使用技能
    /// 控制 puller, 会导致 amber 不会马上自动攻击 puller"。)
    ///
    /// 门控真正要表达的是"**她还没被困过**" —— 那段时间是从开局到 Puller 钩上
    /// 她为止, 玩家还在海滩, 她不该自己跑掉 (钩子现在是玩家走近才挂的,
    /// 见 LidgrenHost.TryHookTutorialNpc)。一旦钩子上过身, 后面无论是被打断还是
    /// Puller 死了, 都该交回给 Stun 那道守卫判 —— 有钩子就不动, 没钩子就自由。
    ///
    /// 只在教程里成立 —— 别的模式没有"被困"这个前提, 返回 false 不干涉。
    /// </summary>
    private bool ShouldStayPut(GameManager simulation, _0023Mk._0023YO npc)
    {
        if (simulation?.GameMode is not _0023bd._00235cc tutorial)
        {
            return false;
        }

        if (npc.HasBuff(_0023Oq._0023aD._0023QBc))
        {
            _wasHooked = true;
        }

        if (_wasHooked)
        {
            return false;   // 困过了, 之后交给 Stun 那道守卫
        }

        return tutorial._0023l9._0023pcc < _0023z._0023occ._0023hcc;
    }

    /// <summary>
    /// 去救倒下的玩家。
    ///
    /// ## 整条链子都是原厂的, 我们只负责"站过去 + 起手"
    ///
    ///   1. <c>AbilityIdentifier.TutorialNPCResurrectChannel</c> 在
    ///      <c>_0023YO._00237N()</c> (-Mk/-YO.cs:114) 里就绑在她的技能槽 2 上,
    ///      不用我们装
    ///   2. 起手 (<c>-AO/-SO.cs</c>): CastTime 1 秒, 施法时算
    ///      <c>_0023Gt = GetResurrectDirection(她)</c> = 指向尸体的单位向量
    ///   3. 引导 (<c>_0023Sq._0023jyc</c>): 4 秒, 每帧校验尸体还在锥形内
    ///   4. 引导满 (<c>_0023EF._0023Gpc._0023blb</c>): `Mb.AsPlayer.Respawn()`
    ///      + 回 <c>(1 - _00235kb)</c> 比例的血
    ///
    /// 也就是说"NPC 救人"这个功能**客户端里是完整的**, 缺的只有决策 ——
    /// 和这个文件里别的东西一样, 随服务端一起丢了。
    ///
    /// ## 引导期间必须一动不动
    ///
    /// 起手的 <c>AbilityData.InterruptOnMovement = true</c>, 引导的
    /// <c>StartChanneling</c> 也是默认的 `interruptedByMovement: true`。
    /// 所以只要 <c>IsPerformingAnAttack</c> 或 <c>IsChanneling</c> 成立,
    /// 这里就只调 <see cref="Idle"/> —— 连重算路径都不做, 免得
    /// <c>SetGoalPosition</c> 把她推出去半步就把 5 秒白读掉。
    /// </summary>
    private void Revive(GameManager simulation, _0023Mk._0023YO npc, Player leader, float deltaTime)
    {
        var key = Key(npc);
        _returning.Remove(key);   // 救人期间"归队"没有意义

        if (npc.IsPerformingAnAttack || npc.IsChanneling)
        {
            Idle(npc);
            return;
        }

        if (Vector2.Distance(npc.Position, leader.Position) > ReviveRange)
        {
            Follow(simulation, npc, leader, deltaTime);
            return;
        }

        Idle(npc);
        _detour.Remove(key);

        Vector2 toLeader = leader.Position - npc.Position;
        if (toLeader != Vector2.Zero)
        {
            toLeader.Normalize();
        }

        // 朝向要自己转 —— 引导 buff 会 ChangeForceDirection 锁住朝向,
        // 但那是**起手之后**的事, 锁的正是我们现在这一帧给的方向。
        npc.ChangeAimDirection(toLeader);
        npc.Attack(toLeader, AbilityIdentifier.TutorialNPCResurrectChannel);
    }

    /// <summary>
    /// 正在归队的 NPC。键带池复用次数, 免得实体回池后串号
    /// (<see cref="ZombieBrain"/> 在这上面栽过三次)。
    /// </summary>
    private readonly HashSet<(int Index, int Uses)> _returning =
        new HashSet<(int, int)>();

    private static (int Index, int Uses) Key(_0023Mk._0023YO npc) =>
        (npc.IndexGlobal, npc.NumberOfGlobalCacheUses);

    /// <summary>
    /// 朝玩家跑, 卡住了就绕。什么时候开始/停止由 <see cref="Steer"/> 决定。
    /// </summary>
    private void Follow(GameManager simulation, _0023Mk._0023YO npc, Player leader, float deltaTime)
    {
        // 先走导航网格。这张图没网格 / 网格不连通才退回直奔。
        if (TryFollowPath(simulation, npc, leader, deltaTime))
        {
            return;
        }

        npc.GoalTarget = leader;
        npc.UseGoalTarget = true;

        // 跑档。
        //
        // ## ⚠️ 绝对不要用 _0023gO() 自定义速度
        //
        // 上一轮我用 _0023gO(加速度, 摩擦, 目标速度) 想让她跑得比玩家快,
        // 结果玩家实测: "amber 没有移动动画了, 现在人物直接平移, 如果角色
        // 一下移动太远她甚至会瞬移。"
        //
        // 原因在同步: _0023gO() 和 _0023fO() 一样会设 `_0023ZN = true`,
        // 而 _0023ZN 是**同步字段** (_0023iO.SerializeNPC:129)。客户端
        // DeserializeNPC (:166-177) 读到它之后按档位重算自己的速度:
        //
        //     if (_0023ZN)      _0023fO();   // 冲刺档: _0023UN/_0023VN/_0023WN
        //     else if (Sprint)  _0023eO();   // 跑档:   _0023RN/_0023SN/_0023TN
        //     else              _0023dO();   // 走档
        //
        // 而教程 NPC 的 _0023vO() **没给冲刺档那三个赋值** (全是 0) ——
        // 客户端于是算出速度 0: 播站立动画、自己不移动, 位置全靠服务端同步
        // 硬拽过去。那就是"没有动画的平移", 拽得太远就是瞬移。
        //
        // 教训: 凡是会写进同步字段的档位切换, 两端算出来的必须是同一个数。
        // 服务端单方面提速做不到这一点。
        //
        // 所以老老实实用原厂档位。追不上玩家没关系 —— 玩家总会停下来,
        // 而归队状态一旦进入就一路跑到身边为止 (见 Steer)。
        //
        // "在跟随距离内用走的"那条仍然成立 —— 距离内她压根不走这个分支,
        // 而是去打架或站住 (见 Steer)。
        npc._0023eO();
    }

    /// <summary>每个 NPC 的导航状态。</summary>
    private sealed class DetourState
    {
        public Vector2 LastPosition;
        public bool HasLastPosition;

        /// <summary>连续多久几乎没挪动。</summary>
        public float StuckFor;

        /// <summary>还要绕多久。&gt; 0 表示正在盲绕 (寻路失败时的兜底)。</summary>
        public float DetourFor;

        /// <summary>往左还是往右绕。撞一次换一边。</summary>
        public float Side = 1f;

        /// <summary>当前这条路径的剩余拐点。</summary>
        public List<Vector2> Path;

        /// <summary>算这条路时玩家在哪 —— 玩家走远了就得重算。</summary>
        public Vector2 PathTarget;
    }

    /// <summary>
    /// 导航网格寻路器。第一次用到时按当前地图建, 之后复用。
    ///
    /// 建一次要遍历 899 个三角形算顶点和重心, 不该每帧做。
    /// </summary>
    private NavMeshPathfinder _pathfinder;
    private int _pathfinderMap = -1;

    /// <summary>玩家挪动超过这么远就重算路径。</summary>
    public float PathRecomputeDistance { get; set; } = 60f;

    /// <summary>走到拐点这么近就算过了, 切下一个。</summary>
    public float WaypointReachedDistance { get; set; } = 18f;

    private NavMeshPathfinder PathfinderFor(GameManager simulation)
    {
        int map = simulation.MapIndex;

        if (_pathfinder != null && _pathfinderMap == map)
        {
            return _pathfinder;
        }

        ConductorGameLogic.Pathfinding.NavMesh mesh = MapManager.GetNavMesh(map);
        if (mesh?.Triangles == null || mesh.Triangles.Length == 0)
        {
            return null;
        }

        _pathfinder = new NavMeshPathfinder(mesh);
        _pathfinderMap = map;
        return _pathfinder;
    }

    private readonly Dictionary<(int Index, int Uses), DetourState> _detour =
        new Dictionary<(int, int), DetourState>();

    /// <summary>判定"卡住"的速度阈值 —— 每秒挪不到这么远就算没在动。</summary>
    public float StuckSpeedThreshold { get; set; } = 8f;

    /// <summary>卡住多久才开始绕。太短会把正常的减速/转身误判成卡住。</summary>
    public float StuckGracePeriod { get; set; } = 0.6f;

    /// <summary>一次绕行持续多久。</summary>
    public float DetourDuration { get; set; } = 1.2f;

    /// <summary>绕行时把目标点放在前方多远。够远才不会走两步就判定"到了"。</summary>
    public float DetourStepDistance { get; set; } = 80f;

    /// <summary>
    /// 她是不是"想走却没挪动"。返回 true 表示刚判定卡住 (只在跨过阈值那一帧)。
    ///
    /// ## 寻路解决不了这种卡住
    ///
    /// 有了导航网格之后, 撞墙那一类已经由 <see cref="TryFollowPath"/> 绕开了。
    /// 剩下的是**物理挤住**: 路径完全正确, 但她被僵尸群、玩家或者路障顶着,
    /// 推不动。重算路径没用 —— 算出来还是同一条。
    ///
    /// 所以脱困要横着挪一段, 换个位置再走。<see cref="TryFollowPath"/> 里
    /// 拿到 true 之后做两件事: 清掉路径 (换个位置可能算出另一条) +
    /// 侧向走 <see cref="DetourDuration"/> 秒。
    ///
    /// ## 原厂那个撞墙反应对她不生效
    ///
    /// <c>_0023iO.DoStaticReaction</c> 里有:
    ///
    ///     if (!IsSprinting &amp;&amp; MoveDirection != Vector2.Zero &amp;&amp; _0023XN &lt; 0f)
    ///         GoalDirection = -MoveDirection;   // 退开 1.5 秒
    ///
    /// 第一个条件是 <c>!IsSprinting</c>, 而归队全程在跑 —— 一次都不会触发。
    /// 而且"原路退回"只能脱离贴墙, 退开之后还是朝同一个方向撞回去。
    /// </summary>
    private bool UpdateStuck(DetourState state, Vector2 position, float deltaTime)
    {
        bool justStuck = false;

        if (state.HasLastPosition && deltaTime > 0f && state.DetourFor <= 0f)
        {
            float moved = Vector2.Distance(position, state.LastPosition);
            bool crawling = moved / deltaTime < StuckSpeedThreshold;

            state.StuckFor = crawling ? state.StuckFor + deltaTime : 0f;

            if (state.StuckFor >= StuckGracePeriod)
            {
                state.StuckFor = 0f;
                justStuck = true;
            }
        }

        state.LastPosition = position;
        state.HasLastPosition = true;
        return justStuck;
    }

    /// <summary>
    /// 沿导航网格的路径走。返回 true 表示这一帧由寻路接管。
    ///
    /// ## 为什么不能直接把路径点交给 GoalTarget
    ///
    /// <c>GoalTarget</c> 只接受实体, 而且 NPC 每帧自己朝它算方向 —— 那正是
    /// "直奔目标, 撞墙也不拐"的来源。沿路径走必须关掉它, 改用
    /// <c>ChangeDirection</c> 手动指方向。
    ///
    /// ## 重算时机
    ///
    /// 玩家在动, 路径会过期。三种情况重算: 没有路径、玩家比算路时挪开了
    /// <see cref="PathRecomputeDistance"/>、路径走完了。
    /// 不每帧重算 —— A* 要遍历三角形图, 每帧跑纯属浪费。
    /// </summary>
    private bool TryFollowPath(GameManager simulation, _0023Mk._0023YO npc,
                               Player leader, float deltaTime)
    {
        NavMeshPathfinder pathfinder = PathfinderFor(simulation);
        if (pathfinder == null)
        {
            return false;   // 这张图没有导航网格 —— 退回盲绕
        }

        var key = Key(npc);
        if (!_detour.TryGetValue(key, out DetourState state))
        {
            state = new DetourState();
            _detour[key] = state;
        }

        // 路径对但推不动 —— 脱困。见 UpdateStuck。
        if (UpdateStuck(state, npc.Position, deltaTime))
        {
            state.Path = null;                    // 换个位置重算, 可能走另一条
            state.DetourFor = DetourDuration;
            state.Side = -state.Side;
        }

        if (state.DetourFor > 0f)
        {
            state.DetourFor -= deltaTime;

            Vector2 sideways = DetourDirection(npc, leader, state.Side);

            npc.UseGoalTarget = false;
            npc.GoalTarget = null;
            npc.SetGoalPosition(npc.Position + sideways * DetourStepDistance);
            npc._0023eO();

            return true;
        }

        bool stale = state.Path == null
                     || state.Path.Count == 0
                     || Vector2.Distance(state.PathTarget, leader.Position) > PathRecomputeDistance;

        if (stale)
        {
            state.Path = pathfinder.FindPath(npc.Position, leader.Position);
            state.PathTarget = leader.Position;
        }

        if (state.Path == null || state.Path.Count == 0)
        {
            return false;   // 网格不连通 —— 退回盲绕
        }

        // 掐掉已经走过的拐点
        while (state.Path.Count > 0
               && Vector2.Distance(npc.Position, state.Path[0]) <= WaypointReachedDistance)
        {
            state.Path.RemoveAt(0);
        }

        if (state.Path.Count == 0)
        {
            return false;   // 到终点了, 交给直奔那一支收尾
        }

        Vector2 waypoint = state.Path[0];

        // ## 走路径点要用 SetGoalPosition, **不能**用 ChangeDirection
        //
        // 第一版我关掉 GoalTarget 之后拿 ChangeDirection 手动指方向 ——
        // 结果 Amber **彻底不动了**。
        //
        // ChangeDirection 只写 MoveDirection (NPC.cs:1010-1016), 而 NPC 的
        // 移动系统根本不读它: 它读的是 _00234L (目标位置) 和 _0023ZL
        // ("有目标要走"的开关)。两个都得设, 而设它们的入口是:
        //
        //     SetGoalPosition(pos):  _00234L = pos;
        //                            _0023ZL = 距离 > 0.1;
        //
        // 直接给 GoalPosition 属性赋值也不行 —— 那个 setter 只写 _00234L,
        // **不碰 _0023ZL**, 移动照样不激活。
        //
        // UseGoalTarget 仍然要关: 开着的话 NPC 每帧朝 GoalTarget 重算目标位置,
        // 会把我们设的路径点冲掉。
        npc.UseGoalTarget = false;
        npc.GoalTarget = null;
        npc.SetGoalPosition(waypoint);
        npc._0023eO();

        return true;
    }

    /// <summary>绕行方向: 朝目标的方向拧 90°。</summary>
    private static Vector2 DetourDirection(_0023Mk._0023YO npc, Player leader, float side)
    {
        Vector2 toLeader = leader.Position - npc.Position;

        if (toLeader == Vector2.Zero)
        {
            return new Vector2(side, 0f);
        }

        toLeader.Normalize();

        // 垂线。side 决定往哪一侧。
        return new Vector2(-toLeader.Y * side, toLeader.X * side);
    }

    /// <summary>
    /// 停下。两个字段必须一起清 —— 只清一个就是下一次序列化 NRE。
    /// 顺带切回走档, 免得下次起步时还挂着跑档的速度。
    /// </summary>
    private static void Idle(_0023Mk._0023YO npc)
    {
        npc.UseGoalTarget = false;
        npc.GoalTarget = null;

        // 目标位置设成她自己 —— SetGoalPosition 会因为距离 ≈ 0 把
        // "有目标要走"的开关关掉 (NPC.cs:1176)。不清的话她会继续朝
        // 上一个路径点走。
        npc.SetGoalPosition(npc.Position);

        npc._0023dO();
    }

    /// <summary>
    /// 最近的敌对目标。
    ///
    /// 判据用游戏自己的 <c>Entity.IsEnemy</c> —— 不要自己比 TeamId。
    /// Amber 是 <c>TeamId.Team1</c> 而僵尸是 <c>Neutral</c>, 手写比较很容易判反;
    /// <c>IsNonTargetable</c> 覆盖隐身/无敌类状态。和 <see cref="MinionBrain"/> 一致。
    /// </summary>
    private static Entity NearestEnemy(GameManager simulation, _0023Mk._0023YO npc, float radius)
    {
        Entity best = null;
        float bestDistanceSquared = radius * radius;

        List<GameObjectBase> objects = simulation.ActiveGameObjectList;
        for (int i = 0; i < objects.Count; i++)
        {
            // 候选是 **Entity** 而不是 Character —— 路障 (_0023EK._0023Yh) 继承
            // Destructible, 不是 Character, 只收 Character 的话她永远不会去打栅栏。
            // 玩家反馈过"Amber 不会主动攻击 Barricade"。
            if (objects[i] is not Entity candidate || ReferenceEquals(candidate, npc))
            {
                continue;
            }

            if (!candidate.IsActive || candidate.IsDead)
            {
                continue;
            }

            // 路障的 IsDead 看的是状态枚举而不是血量 (见 DestructibleSpawner),
            // 血刚归零的那一帧还是 false —— 补一道血量判据, 免得她对着
            // 已经打烂的栅栏继续挥。
            if (candidate.IsDestructible && candidate.AsCharacter == null
                && candidate is _0023EK._0023Yh { Health: <= 0f })
            {
                continue;
            }

            if (!npc.IsEnemy(candidate) || candidate.IsNonTargetable())
            {
                continue;
            }

            // ## 发电机不许打
            //
            // 玩家实测: "刚救完 Amber 那里需要用枪打爆发电机的地方, amber
            // 隔着水使用近战攻击攻击空气"。她把发电机选成了目标 ——
            // 它是 Destructible 而且对她是敌对的, 上面那些判据都拦不住。
            //
            // 而那一步的设计就是"**隔着电栅栏用枪打**" (状态 9 的语音是
            // TutorialRangedObjective01, Ranged), 发电机被七段电栅栏围着,
            // 她走不过去, 于是站在水边对空挥近战。
            //
            // 更重要的是: 打爆发电机是**玩家的任务**, 不是她的。
            if (candidate is _0023EK._0023p0b)
            {
                continue;
            }

            // 教程 NPC 里只有敌对那一方能被打。
            //
            // 现在卡车队友和 Amber 同队, 上面的 IsEnemy 已经把他们排掉了 ——
            // 这道守卫是**多一层保险**: 以后要是又有人为了迁就客户端某段硬编码
            // 去动队伍编号 (那件事已经干过一次, 见 TutorialEntitySpawner.EnemyTeam
            // 里的说明), 至少 Amber 不会当场开始砍队友。
            if (candidate.IsTutorialNPC && candidate.TeamId != TutorialEntitySpawner.EnemyTeam)
            {
                continue;
            }

            float distanceSquared = Vector2.DistanceSquared(npc.Position, candidate.Position);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// 够近就放基础攻击。
    ///
    /// 用 <c>LightPrimary</c>: <c>_0023YO._00238N()</c> 给她装的槽 0 是武器 1006
    /// 并且 <c>_0023xz(0)</c> 装备了它, 而 <c>_00236N()</c> 注册的技能里
    /// 对应近战的正是 LightPrimary/LightSecondary。
    ///
    /// 冷却、消耗、施法中都交给 <c>AbilityBar.TryUseAbility</c> 自己判
    /// (<c>NPC.Attack</c> 内部就是它), 所以每帧调是安全的。
    /// </summary>
    private void TryAttack(_0023Mk._0023YO npc, Entity target)
    {
        if (npc.IsPerformingAnAttack)
        {
            return;
        }

        Vector2 toTarget = target.Position - npc.Position;

        // 距离要加上双方的碰撞半径 —— 两个体积不为零的角色贴在一起时,
        // 中心点距离仍然有 r1 + r2, 只比中心距会永远够不着。
        float reach = AttackStopDistance + npc.Radius + target.Radius;
        if (toTarget.LengthSquared() > reach * reach)
        {
            return;
        }

        if (toTarget != Vector2.Zero)
        {
            toTarget.Normalize();
        }

        npc.ChangeAimDirection(toTarget);
        npc.Attack(toTarget, AbilityIdentifier.LightPrimary, reach);
    }
}
