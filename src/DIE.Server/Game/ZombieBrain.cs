using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Collision;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Stats;
using FarseerPhysics.Common;
using _0023Mk;
using _0023Oq;

namespace DIE.Server.Game;

/// <summary>
/// 僵尸的"脑子" —— 每帧给活着的 NPC 挑目标、下达攻击。
///
/// ## 客户端里还剩多少
///
/// 出乎意料地多。<c>NPC.UpdateCharacter</c> (NPC.cs:441) 是**完整的**, 包括:
///
///   - 朝 <c>GoalTarget</c> / <c>GoalPosition</c> 转向并移动
///   - 局部绕障 (<c>_00235M</c> / <c>_00236M</c>, NPC.cs:526-746) —— 从
///     <c>CollisionManager</c> 拉多边形、算切线外扩、找绕行点, 整整 220 行
///   - <c>_00236M</c> 内层还带着 <c>if (GameManager.IsServer && ...)</c> 的
///     服务端专属分支 —— 它没被裁掉, 因为整个 NPC 类客户端渲染要用
///
/// 换句话说僵尸的**身体**是齐的, 丢的只有**脑子**: 谁来写 <c>GoalTarget</c>、
/// 谁来调 <c>Attack()</c>。那部分挂在 <c>GameManager.ConnectToAIUnit</c>
/// (GameManager.cs:2063) 上, 是个空的 virtual —— 典型的"服务端权威逻辑被裁"。
///
/// 全代码树搜 AIUnit / BehaviorTree / AIState 一个都没有, 所以行为树本身
/// 无从还原。本类是**我们写的**一个最小可用替代, 不是复原。
///
/// ## 三件事
///
/// 1. <see cref="FinishEmerge"/>  —— 出场动画放完后把 SpawnState 归零
/// 2. <see cref="PickTarget"/>    —— 选最近的活人当 GoalTarget
/// 3. <see cref="TrySpecial"/>    —— 够得着就放特殊技能 (见 <see cref="ZombieSpecials"/>)
/// 4. <see cref="TryAttack"/>     —— 进了近战距离就放基础攻击
///
/// ## 为什么必须在 GameManager.Update **之前**跑
///
/// <c>NPC.UpdateCharacter</c> 当帧就会消费 <c>GoalTarget</c>。跑在后面的话
/// 每个决策都会晚一帧生效, 追人时表现为僵尸总差半步。
/// </summary>
public sealed class ZombieBrain
{
    /// <summary>
    /// 进入这个半径就开始追。
    ///
    /// 地图数据和代码里**都没有**这个值 —— 仇恨半径属于被裁掉的 AI 配置。
    /// 取 55 的依据是可视范围: <c>Maps/camera.cfg</c> 给的相机偏移是
    /// (67.175, 67.175), 模长约 95, 也就是玩家大致能看到身周 60~90 单位。
    /// 仇恨半径压在可视范围内一点, 这样僵尸"被看见之后才动", 不会从屏幕外
    /// 莫名其妙冲过来。
    ///
    /// 这是我们定的, 不是还原; 手感不对就调这里。
    /// </summary>
    public float AggroRadius { get; set; } = 55f;

    /// <summary>
    /// 超出这个半径就放弃追击。比仇恨半径大, 免得目标在边界上反复进出、
    /// 僵尸原地抽搐。
    /// </summary>
    public float LeashRadius { get; set; } = 90f;

    /// <summary>
    /// 攻击距离的基准。取自游戏自己的技能数据:
    /// <c>WalkerStrikeNormal</c> 的 <c>abilityData.StopDistance = 5f</c>
    /// (-fZ/-gOb.cs:198) —— 那正是"该停下来打了"的距离。
    ///
    /// 实际判定还要加上双方的 <c>Radius</c>, 因为 Position 是圆心。
    /// </summary>
    public float AttackStopDistance { get; set; } = 5f;

    // 特殊技能 (钩舌、冲撞、横扫、扑爆 ...) 的射程**不在这里** —— 每一招都取自
    // 原厂的技能数据, 集中在 ZombieSpecials 那张表里。
    //
    // 不管表里写多远, 真实起手距离都受 AggroRadius (55) 约束: 没有 GoalTarget
    // 就走不到放技能这一步。想让 Puller 更早吐舌头、公羊更早起冲撞, 调的是
    // AggroRadius, 不是那张表。

    // ------------------------------------------------------------------
    // 威胁表 (仇恨) —— 见 PickTarget 上面那段"原版留下了什么"
    // ------------------------------------------------------------------

    /// <summary>
    /// 威胁的量纲: **打掉它 1% 的最大生命 = 1 点威胁**。
    ///
    /// 满血打死 = 100 点, 所以 <see cref="MaxThreat"/> 就是 100。
    ///
    /// 为什么用比例而不是伤害绝对值: 原厂唯一还活着的加分方
    /// (<c>-Sq/-Ryc.cs</c>, 策反 buff) 给的是 **20 / 10** 这种个位数, 而一次
    /// 普通攻击的伤害是几十上百 —— 两者不在一个刻度上。按比例归一之后,
    /// 20 正好落在"打掉它五分之一血"的位置, 量纲对得上。
    ///
    /// 这个换算是**我们定的**, 原厂没留下伤害到威胁的公式。
    /// </summary>
    public float ThreatPerPercentOfMaxHealth { get; set; } = 1f;

    /// <summary>威胁上限。满血打死正好到顶, 见 <see cref="ThreatPerPercentOfMaxHealth"/>。</summary>
    public float MaxThreat { get; set; } = 100f;

    /// <summary>
    /// 站在仇恨半径里每秒攒多少威胁 —— 数值取自原厂那个 buff 的"非玩家"档 (10)。
    ///
    /// 实际按距离打折 (贴脸拿满, 半径边缘拿 0), 这样"最近的那个先被盯上"这条
    /// 老行为在威胁模型下自然成立, 不用另写一条规则。
    /// </summary>
    public float ProximityThreatPerSecond { get; set; } = 10f;

    /// <summary>
    /// 光靠站着能攒到的威胁上限 —— 取自原厂那个 buff 的"敌方玩家"档 (20)。
    ///
    /// 压得比 <see cref="MaxThreat"/> 低得多是故意的: 动过手的人永远排在
    /// 只是路过的人前面。
    /// </summary>
    public float ProximityThreatCap { get; set; } = 20f;

    /// <summary>目标还在 <see cref="LeashRadius"/> 以内时, 威胁每秒掉多少。</summary>
    public float ThreatDecayPerSecond { get; set; } = 2f;

    /// <summary>
    /// **只是路过**的人跑出 <see cref="LeashRadius"/> 之后, 多久被忘掉。
    ///
    /// **玩家定的数** (2026-09-09, 实测校准前的起始值)。
    ///
    /// ## 它不是"所有人的脱战时间"
    ///
    /// 出圈之后威胁按 <c>ProximityThreatCap / GiveUpSeconds</c> 的速度掉, 于是
    ///
    ///     实际追多久 = 你欠它的威胁 / 那个速度
    ///
    /// 只是走近过 (威胁到顶也就 <see cref="ProximityThreatCap"/>) 的人正好被追
    /// <c>GiveUpSeconds</c> 秒; 真打过它的人记得更久, 打得越狠越久。这是故意的:
    /// "捅了它一刀就跑"和"从它旁边走过"不该是同一个待遇。
    ///
    /// ## 为什么不拿 MaxThreat 折算
    ///
    /// 那样出圈衰减是 <c>100/5 = 20</c>/秒, 意味着**每秒打掉它两成血**才拉得住
    /// 仇恨 —— 隔着距离细水长流地打, 威胁反而一路往下掉, 僵尸边挨打边回家。
    /// 单元测试当场撞到 (Brain_KeepsChasing_WhileYouKeepShooting)。
    /// </summary>
    public float GiveUpSeconds { get; set; } = 5f;

    /// <summary>
    /// 走近要**看得见**才起仇恨 (视野系统)。默认开。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "隔着墙的僵尸也能看见玩家, 甚至在地图上不相连的区域的僵尸也能看见玩家,
    /// 只要进入他们的仇恨范围他们就能看见玩家。"
    ///
    /// 之前的判据只有距离 (<see cref="AggroRadius"/>)。
    ///
    /// ## 判据是原厂的"看得见"
    ///
    /// 原厂没有给僵尸留视野代码 (那一半随服务端丢了), 但**同一个问题**它在别处
    /// 回答过好几次, 而且答案一模一样 —— 炮台挑目标 (-EK/-Zuc.cs:310)、
    /// 可破坏物的索敌 (-EF/-fK.cs:461/478)、三种范围判定形状 (-6/-Gjc/-Hjc/-Ijc):
    ///
    ///     !CollisionManager._0023c8._00233b(自己, 目标,
    ///          GeometryCollisionFlags._00237jd | _00239jd, false, false)
    ///
    /// 就是一条对**静态地图几何**的射线, 两个标志是会挡视线的那两类几何。
    /// 移动碰撞用的 <c>| _0023ckd</c> 那一版**不用** —— 那是"走不走得过去",
    /// 矮栏杆挡路不挡眼。
    ///
    /// "地图上不相连的区域"也由它管住: 两片区域之间没有通路, 中间就是墙/崖边的
    /// 几何, 射线过不去。
    ///
    /// ## 管的只是"起仇恨", 不管"继续追"
    ///
    ///   - 已经盯上的人拐过墙角 -&gt; **照追** (威胁还在, 按原来的规则衰减)。
    ///     不然玩家绕根柱子就能甩掉整群怪, 那才是作弊。
    ///   - 挨打拉仇恨 (<see cref="OnDamaged"/>) 不看视线 —— 打得到你, 就是看得见。
    ///   - 被激怒的 (事件/埋伏刷出来的, <see cref="Provoke(NPC)"/>) 不看视线 ——
    ///     它们本来就是冲着玩家来的, 刷出来往往在墙后。
    /// </summary>
    public bool RequireLineOfSight { get; set; } = true;

    /// <summary>诊断计数: 本秒内因为隔墙没起仇恨的次数。宿主读完自行清零。</summary>
    public int SightBlocked;

    /// <summary>诊断计数: 被伤害拉起过多少次仇恨。</summary>
    public int DamagePulls;

    /// <summary>诊断计数: 有多少只因为脱战正在往回走。</summary>
    public int LastGoingHome { get; private set; }

    /// <summary>诊断计数, 由宿主每秒打一次日志。</summary>
    public int LastChasing { get; private set; }

    /// <summary>诊断计数: 本秒内发起过多少次攻击。宿主读完自行清零。</summary>
    public int AttacksIssued;

    /// <summary>诊断计数: 本秒内 Puller 发起过多少次钩舌。宿主读完自行清零。</summary>
    public int HooksIssued;

    /// <summary>
    /// 诊断计数: 本秒内放出过多少次**钩舌以外**的特殊技能 (横扫、跳劈、冲撞、
    /// 跺地、吐酸、恐惧、扑爆 ...)。宿主读完自行清零。
    ///
    /// 单独记一个数是为了下次真机能一眼看出"特殊技能到底放没放" —— 这次的
    /// 病就是它恒为 0 而没人知道 (玩家只看到屠夫一路普攻)。
    /// </summary>
    public int SpecialsIssued;

    /// <summary>
    /// 每帧一次。<paramref name="totalTime"/> 用模拟层自己的时钟
    /// (<c>NetworkBase.Time</c>), 不要用挂钟 —— 出场动画时长是按模拟时间算的。
    /// </summary>
    public void Update(GameManager simulation, float deltaTime, float totalTime)
    {
        if (simulation?.ActiveGameObjectList == null)
        {
            return;
        }

        List<Player> players = simulation.ActivePlayerList;
        CollectCivilians(simulation);
        Pathing.BeginFrame();
        _aliveThisFrame.Clear();
        int chasing = 0;
        int goingHome = 0;

        // 用下标遍历而不是 foreach: 攻击会往 ActiveGameObjectList 里塞新的
        // 攻击判定对象, foreach 会抛 InvalidOperationException。
        List<GameObjectBase> objects = simulation.ActiveGameObjectList;
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not NPC npc || !npc.IsZombie)
            {
                continue;
            }

            if (!npc.IsActive || npc.IsDead)
            {
                Forget(npc);   // 死了就把激怒记录清掉, 免得表无限增长
                continue;
            }

            _aliveThisFrame.Add(Key(npc));
            RememberHome(npc);
            EliteCastArmor(npc);

            if (!FinishEmerge(simulation, npc, totalTime))
            {
                continue;   // 还在爬出地面, 这一帧什么都不做
            }

            SeedProximityThreat(npc, players, deltaTime);
            SeedCivilianThreat(npc, deltaTime);
            DecayThreat(npc, deltaTime);

            Entity target = LockedTarget(npc) ?? PickTarget(npc, players);
            if (target == null)
            {
                // 注意: UseGoalTarget = true 且 GoalTarget == null 会让
                // NPC.SerializeCharacter (NPC.cs:818) 直接 NRE ——
                // 那行没有空判。所以清目标必须连着清标志。
                npc.UseGoalTarget = false;
                npc.GoalTarget = null;

                if (WalkHome(simulation, npc, deltaTime))
                {
                    goingHome++;
                }

                continue;
            }

            npc.GoalTarget = target;
            npc.UseGoalTarget = true;
            chasing++;

            // 直线被墙挡着就沿网格绕 (接管时会关掉 UseGoalTarget、改走拐点), 见 ZombiePathing
            if (!npc.IsPerformingAnAttack)
            {
                Pathing.Steer(simulation, npc, target.Position, deltaTime);
                FaceWhereItIsGoing(npc, target);
            }

            // 巨型 BOSS 走自己那套全技能出招 (它们没有基础攻击, 招全在 MassiveBossBrain 里)
            if (MassiveBossBrain.Handles(npc))
            {
                Bosses.Act(simulation, npc, target, totalTime);
                foreach ((Character zombie, Entity rallyTarget) in Bosses.Rallied)
                {
                    if (zombie is NPC rallied)
                    {
                        Provoke(rallied, rallyTarget);
                    }
                }

                Bosses.Rallied.Clear();
                continue;
            }

            // 特殊技能优先于近战 —— 够得着就先放。放成功了这一帧不再走近战,
            // 否则 AbilityBar 的公共冷却会把近战也一起吃掉, 白白空一拍。
            if (!TrySpecial(npc, target))
            {
                TryAttack(npc, target);
            }
        }

        LastChasing = chasing;
        LastGoingHome = goingHome;
        ForgetMissing();
    }

    /// <summary>这一帧活着 (在场、没死) 的僵尸。帧末据此清掉别的记录, 见 <see cref="ForgetMissing"/>。</summary>
    private readonly HashSet<(int Index, int Uses)> _aliveThisFrame = new HashSet<(int, int)>();

    /// <summary>
    /// 这一帧没出现在活着的名单里的僵尸, 它的记录 (家 / 激怒 / 优先目标 / 出场 / 精英护甲 /
    /// 寻路 / BOSS 节奏) 全部清掉。
    ///
    /// ## 为什么不能只靠"看到它死了"那一刻 (<see cref="Forget"/>)
    ///
    /// 玩家实测 (拾荒, 2026-09-23): "looter 无论地图上显示刷新在哪个点, 都只会出现在其中一个点。"
    /// 刷出来的位置其实是对的 (小地图图标和刷新用的是同一个坐标), 是刷出来之后**自己走回了
    /// 第一波的刷新点**。
    ///
    /// 这些表的键是 (全局下标, <c>NumberOfGlobalCacheUses</c>), 本意是"池复用一次计数就加一,
    /// 组合起来唯一"。可那个计数**只在全局池那条路上加** (GameManager.cs:2027), 刷僵尸走的按类型
    /// 取池 (<c>GetGameObjectFromPool&lt;T&gt;</c>) 从来不加 —— 同一个对象复用十次, 键都是 (#6717, 0)。
    /// 而尸体离开活动列表很快, 这里常常一帧都没看到它"死着", <see cref="Forget"/> 就没跑到。
    /// 下一只复用同一个对象, 就继承了上一只的家 (以及激怒状态、优先目标……), 一路走回去。
    ///
    /// 这不只是 Looter 的事 —— 所有复用出来的僵尸都会这样。按"这一帧活着的名单"收尾之后,
    /// 对象只要在池里待过一帧 (复用之前必然如此), 旧记录就清掉了。回归测试见 ZombieBrainTests。
    /// </summary>
    private void ForgetMissing()
    {
        _provoked.RemoveWhere(k => !_aliveThisFrame.Contains(k));
        RemoveMissing(_home);
        RemoveMissing(_preferredTarget);
        RemoveMissing(_emergeDeadline);
        RemoveMissing(_eliteArmor);
        RemoveMissing(_onlyTeam);
        Pathing.ForgetMissing(_aliveThisFrame);
        Bosses.ForgetMissing(_aliveThisFrame);
    }

    private void RemoveMissing<T>(Dictionary<(int Index, int Uses), T> table)
    {
        if (table.Count == 0)
        {
            return;
        }

        foreach ((int, int) key in table.Keys.Where(k => !_aliveThisFrame.Contains(k)).ToList())
        {
            table.Remove(key);
        }
    }

    /// <summary>
    /// 出场动画放完 -> 把 <c>SpawnState_Current</c> 归零。
    ///
    /// ## 不做会怎样
    ///
    /// 僵尸**永远无法移动**。<c>NPC.ChangeDirection</c> (NPC.cs:1012) 第一个
    /// 条件就是 <c>SpawnState_Current == _0023WM._0023Yc</c>, 不满足就直接
    /// 不写 MoveDirection。
    ///
    /// 而客户端里**没有任何一处**把它归零 —— <c>SpawnCharacter</c> (:363) 只负责
    /// 把待生效值推进去, <c>_00233M()</c> (:380) 只负责播动画。归零那步是服务端的,
    /// 随服务端丢失。这也是上一轮"僵尸戳在原地"的真正原因之一, 光有脑子还不够。
    ///
    /// ## 计时
    ///
    /// <c>_00233M()</c> 播动画时会把 <c>SpawnAnimationDuration</c> 填好
    /// (NPC.cs:407), 直接用它, 不自己编时长。
    ///
    /// ## ⚠️ 这段已经在这里了, 不要再造第二份
    ///
    /// 2026-08-03 我在查"看不见的僵尸"时把这个机制**又推导了一遍**, 写了个
    /// 独立的 <c>SpawnStateReleaser</c> 加四条测试, 接进帧循环之后真机日志里
    /// 一次都没触发 —— 因为 <see cref="Update"/> 跑在前面, 每只僵尸都已经被
    /// 这里放行了。纯粹的重复劳动, 已删。
    ///
    /// 教训: 动手补一个"服务端丢失的机制"之前, 先在 <c>src/DIE.Server</c> 里
    /// 搜一遍那个字段名。这已经是第二次了 (上一次是 Amber 的局部避障)。
    ///
    /// 顺带记下一个**尚未验证**的差异: 出场状态分两类, 一次性动画
    /// (钻地/爬出/跳下) 和持续姿势 (<c>_0023z._0023EWb</c>: 坐着/吃东西/趴着)。
    /// 这里对两类一视同仁, 都按时长放行。原厂的持续姿势本意可能是"一直保持到
    /// 被惊动"。玩家没报告过"坐着的僵尸自己站起来了", 所以**不要凭这段推测去改** ——
    /// 等真有人反馈再说。
    /// </summary>
    /// <returns>true = 已经能动了</returns>
    private bool FinishEmerge(GameManager simulation, NPC npc, float totalTime)
    {
        if (npc.SpawnState_Current == _0023WM._0023Yc)
        {
            return true;
        }

        var key = Key(npc);

        if (!_emergeDeadline.TryGetValue(key, out float deadline))
        {
            // SpawnAnimationDuration 为 0 说明这个出场方式没配动画, 立刻放行。
            deadline = totalTime + npc.SpawnAnimationDuration;
            _emergeDeadline[key] = deadline;
        }

        if (totalTime < deadline)
        {
            return false;
        }

        _emergeDeadline.Remove(key);
        npc.SpawnState_Current = _0023WM._0023Yc;

        // **强制立刻下发这一次状态变化。**
        //
        // 客户端**没有任何代码会自己复位 SpawnState_Current**
        // (见 StaticZombiePlacer 的说明) —— 它完全等服务端告诉它"出场结束了"。
        // 而在那之前, 播的是钻地/爬行动画, 僵尸在视觉上是**埋在地里的**。
        //
        // 所以这一帧的同步一旦排不上队, 那只怪就一直看不见, 但服务端这边一切
        // 正常: IsActive、同步位、坐标全对。玩家报的"看不见的僵尸"查了好几轮
        // 都停在"服务端侧证据干净", 症结就在这个**单次**状态变化上。
        //
        // 决定性的线索是玩家这一句: "隐身只发生在刷出来的僵尸身上, 摆的僵尸
        // 没这个问题" —— 摆的用的是坐/吃这类**持续姿势**, 本来就不需要谁来
        // 复位; 刷的用钻地/爬行, 全靠这一次通知。
        //
        // GetSyncHelper 是 public 的 (ZombieFactory.Spawn 设同步间隔时就在用),
        // _00234Ce() 是原厂自己在同样场合用的调用 (HordeSpawner.StartCapture:157)。
        //
        // **这一行没有单元测试**, 而且不是漏写: _00234Ce 干的事是把"下次该同步的
        // 时刻"往前拨到当前时间 (SynchronizableData.cs:152-165), 既不改同步位
        // 也不置任何可读的标志 —— 服务端进程内没有任何可断言的痕迹。
        // 能证伪它的只有真机: 刷出来的僵尸还隐不隐身。
        simulation?.NetworkManager?.GetSyncHelper(npc.SyncHelperIndex)?._00234Ce();

        return true;
    }

    /// <summary>
    /// 被激怒的僵尸 —— 无视仇恨半径, 一直追着玩家跑。
    ///
    /// 键是 (全局下标, 池复用次数)。只用下标会**串号**: 僵尸死了回池,
    /// 下一个从同一个槽位取出来的会继承前一个的激怒状态。
    /// <c>NumberOfGlobalCacheUses</c> 每次从池里取出都会加一, 组合起来才唯一。
    /// (<see cref="DestructibleRewards"/> 用的是同一招。)
    /// </summary>
    private readonly HashSet<(int Index, int Uses)> _provoked = new HashSet<(int, int)>();

    /// <summary>
    /// 把一只僵尸设成"一出生就追着玩家打"。
    ///
    /// ## 为什么需要它
    ///
    /// 普通僵尸靠 <see cref="AggroRadius"/>(55) 被动仇恨 —— 玩家走近才动。
    /// 但**埋伏刷出来的**不一样: 玩家的原话是"这种刷出来的僵尸不需要走到
    /// 仇恨范围内才能吸引仇恨, 它一刷出来就应该攻击玩家"。
    ///
    /// 而埋伏点离战场往往超过 <see cref="LeashRadius"/>(90) ——
    /// 光在生成时设一次 GoalTarget 没用, 下一帧 <see cref="PickTarget"/>
    /// 就会因为超距把目标清掉。所以要一个持久标记。
    ///
    /// 表不会无限涨: <see cref="Forget"/> 在僵尸死掉时清掉它。
    /// </summary>
    public void Provoke(NPC zombie) => Provoke(zombie, null);

    /// <summary>
    /// 这只僵尸**只打这一队** (这队的玩家和召唤物), 别的队的人路过、甚至动手打它都不理。
    ///
    /// 玩家定 (拾荒, 2026-09-23): 夜袭只有装满卡车的那一队挨, 刷出来的那一波只对这一队发起攻击。
    /// 死了 / 回池由 <see cref="ForgetMissing"/> 一起清掉。
    /// </summary>
    public void RestrictToTeam(NPC zombie, TeamId team)
    {
        if (zombie != null)
        {
            _onlyTeam[Key(zombie)] = team;
        }
    }

    /// <summary>只打某一队的僵尸。键同 <see cref="_provoked"/>。</summary>
    private readonly Dictionary<(int Index, int Uses), TeamId> _onlyTeam =
        new Dictionary<(int, int), TeamId>();

    /// <summary>
    /// 激怒并**指定优先目标**。
    ///
    /// 埋伏怪要"谁在打路障就打谁"(玩家的要求)。指定的目标只要还活着、还够得着,
    /// 就一直优先于"最近的玩家" —— 否则玩家躲远一点, 仇恨就会跳到旁边的队友身上。
    ///
    /// 目标死了或离场就退回普通的"追最近的玩家", 不会卡死。
    /// </summary>
    public void Provoke(NPC zombie, Entity preferredTarget)
    {
        if (zombie == null)
        {
            return;
        }

        var key = Key(zombie);
        _provoked.Add(key);

        if (preferredTarget != null)
        {
            _preferredTarget[key] = preferredTarget;
        }
    }

    /// <summary>
    /// **挨打就拉仇恨** —— 不管打它的人站得多远。
    ///
    /// ## 为什么需要
    ///
    /// <see cref="PickTarget"/> 纯看距离: 站在 <see cref="AggroRadius"/>(55)
    /// 之外的玩家它一眼都不会看。于是任何有射程的手段都能无伤刷怪 ——
    /// 玩家原话: "在僵尸的仇恨距离外攻击僵尸不会获得他们的仇恨, 可以无伤刷怪"。
    ///
    /// 这不是"僵尸太笨", 是仇恨表根本没接上伤害。原厂的 NPC 有一份
    /// <c>AggroList</c> (NPC.cs:219) 和 <c>AddAggroToList</c> (:1049), 但全代码树
    /// 里只有嘲讽技能那一处在往里加 —— 把伤害记进仇恨的那一半在服务端, 随
    /// 服务端一起没了。
    ///
    /// ## 加多少
    ///
    /// 按**打掉了它百分之几的最大生命**算 (见
    /// <see cref="ThreatPerPercentOfMaxHealth"/>) —— 满血打死正好 100 点,
    /// 也就是 <see cref="MaxThreat"/>。用比例而不是伤害绝对值, 是为了和原厂
    /// 那个 20/10 的量纲对得上。
    ///
    /// **不看距离**。这就是堵住"隔着仇恨半径无伤刷怪"的那一下: 打了就有威胁,
    /// 有威胁就是候选, 不管站多远。
    ///
    /// 跑远之后忘掉这件事交给 <see cref="DecayThreat"/> ——
    /// 出了脱战半径威胁掉得很快, <see cref="GiveUpSeconds"/> 秒后归零。
    ///
    /// ## 谁调
    ///
    /// <c>GameManagerServer.Damaged</c> —— 那是所有伤害的唯一汇合点。
    /// 非僵尸 (玩家、路障、友方 NPC) 挨打时也会走到这里, 在这里筛掉。
    /// </summary>
    public void OnDamaged(Entity victim, Entity attacker, float damage)
    {
        if (victim is not NPC zombie || !zombie.IsZombie)
        {
            return;
        }

        attacker = AggroSource(attacker);
        if (!CanBeTargeted(zombie, attacker) || !zombie.IsActive || zombie.IsDead)
        {
            return;
        }

        // 最大生命为 0 的情况真的出现过 (漏掉 Reset 时 ServerModifier 为 0,
        // 所有僵尸属性恒为 0) —— 那时候除法会变成 Infinity, 威胁表就废了。
        float maxHealth = zombie.MaxHealth;
        float percent = maxHealth > 0f ? 100f * damage / maxHealth : 100f;

        DamagePulls++;
        AddThreat(zombie, attacker, percent * ThreatPerPercentOfMaxHealth, cap: MaxThreat);
    }

    /// <summary>
    /// 记住出生点 —— 脱战之后往回走。
    ///
    /// 原厂的"没目标时干什么"就是走回 <c>GoalPosition</c>:
    ///
    ///     if (!UseGoalTarget || GoalTarget == null) {
    ///         if (_0023ZL) { 朝 GoalPosition 走, 进 3 以内停 }
    ///         else ChangeDirection(Vector2.Zero);       // 原地不动
    ///     }
    ///
    /// (NPC.cs:481)。<c>SetGoalPosition</c> 顺带把 <c>_0023ZL</c> 置好, 所以
    /// 只要给它一个位置, 回家这件事原厂代码自己会做 —— 我们只负责记住家在哪。
    ///
    /// 第一次见到这只僵尸 (爬出地面之前也算) 时记下它站的地方。
    /// </summary>
    private void RememberHome(NPC zombie)
    {
        var key = Key(zombie);

        if (!_home.ContainsKey(key))
        {
            _home[key] = zombie.Position;
        }
    }

    /// <summary>
    /// 没目标了就往家走。
    ///
    /// 已经到家 (或者不知道家在哪) 就什么都不做 —— 让原厂那句
    /// <c>ChangeDirection(Vector2.Zero)</c> 把它停住。
    /// </summary>
    /// <returns>true = 这一帧在往回走</returns>
    private bool WalkHome(GameManager simulation, NPC zombie, float deltaTime)
    {
        if (!_home.TryGetValue(Key(zombie), out Vector2 home))
        {
            return false;
        }

        // 原厂到家的判据是 3 (NPC.cs:485 那句 _0023sHc(..., 3f)), 这里用同一个数
        if (Vector2.DistanceSquared(zombie.Position, home) <= HomeArrivalDistance * HomeArrivalDistance)
        {
            return false;
        }

        if (!Pathing.Steer(simulation, zombie, home, deltaTime))
        {
            zombie.SetGoalPosition(home);
        }

        if (!zombie.IsPerformingAnAttack && zombie.MoveDirection != Vector2.Zero)
        {
            zombie.ChangeAimDirection(Vector2.Normalize(zombie.MoveDirection));
        }

        return true;
    }

    /// <summary>绕墙寻路 (追人和回家都用)。见 <see cref="ZombiePathing"/>。</summary>
    public ZombiePathing Pathing { get; } = new ZombiePathing();

    /// <summary>巨型 BOSS 的出招。见 <see cref="MassiveBossBrain"/>。</summary>
    public MassiveBossBrain Bosses { get; } = new MassiveBossBrain();

    /// <summary>
    /// 离家多近算到了。<c>3</c> 是原厂的数 —— <c>NPC.UpdateCharacter</c> 判"走到了"
    /// 用的就是它 (NPC.cs:485)。
    /// </summary>
    private const float HomeArrivalDistance = 3f;

    /// <summary>每只僵尸的出生点。键同 <see cref="_provoked"/>, 死掉时由 Forget 清掉。</summary>
    private readonly Dictionary<(int Index, int Uses), Vector2> _home =
        new Dictionary<(int, int), Vector2>();

    /// <summary>激怒时指定的优先目标。键同 <see cref="_provoked"/>。</summary>
    private readonly Dictionary<(int Index, int Uses), Entity> _preferredTarget =
        new Dictionary<(int, int), Entity>();

    /// <summary>
    /// 优先目标还能不能打。目标没了就返回 null, 调用方退回普通选目标。
    /// </summary>
    private Entity PreferredTargetFor(NPC zombie)
    {
        if (!_preferredTarget.TryGetValue(Key(zombie), out Entity target))
        {
            return null;
        }

        if (target == null || !target.IsActive || target.IsDead
            || target.IsNonTargetable(onlyForZombies: true))
        {
            return null;
        }

        return target;
    }

    /// <summary>僵尸没了就把它的激怒记录清掉, 免得表无限增长。</summary>
    private void Forget(NPC zombie)
    {
        var key = Key(zombie);
        _provoked.Remove(key);
        _preferredTarget.Remove(key);
        _emergeDeadline.Remove(key);
        _home.Remove(key);
        _eliteArmor.Remove(key);
        _onlyTeam.Remove(key);
        Pathing.Forget(zombie);
        Bosses.Forget(zombie);
    }

    private bool IsProvoked(NPC zombie) => _provoked.Contains(Key(zombie));

    private static (int Index, int Uses) Key(NPC zombie) =>
        (zombie.IndexGlobal, zombie.NumberOfGlobalCacheUses);

    /// <summary>
    /// 威胁最高的那个可仇恨活人。
    ///
    /// ## 原版留下了什么
    ///
    /// 原版**不是**"追最近的玩家", 是一张威胁表:
    ///
    ///   NPC.AggroList : List&lt;{Entity, float}&gt;    (NPC.cs:219)
    ///   AddAggroToList(enemy, aggro)             (:1049)
    ///   ClearAggros() / RemoveFromAggro(entity)  (:1105 / :1110)
    ///
    /// 读这张表的那一半在服务端, 随服务端丢了 —— 全代码树里**零个读者**。
    /// 还活着的加分方只有一个: 策反 buff (<c>-Sq/-Ryc.cs</c>), 它每 tick 扫
    /// 半径内的实体重新播种, **敌方玩家 20 / 其他 10**, buff 结束时
    /// <c>ClearAggros()</c> 再把自己从所有 NPC 的表里摘掉。
    ///
    /// 从这一处能读出三件事:
    ///
    ///   1. 威胁是**在候选之间排序**用的
    ///   2. 量纲是**个位数**, 不是伤害绝对值 (一次普攻几十上百)
    ///   3. 威胁是**可撤销**的 —— 有明确的清空和移除接口
    ///
    /// 另有 <c>NPC.HaveExtendedAggro</c> (:151, 只写不读) —— 名字直译是
    /// "**扩大的**仇恨**范围**", 说明仇恨本来就按范围判, 且可以按 NPC 放大。
    ///
    /// 半径的数、衰减率、伤害换算成威胁的公式 —— 一个都没留下, 全是我们定的。
    ///
    /// ## 这里怎么算
    ///
    ///   站在仇恨半径里     -&gt; 每秒攒威胁, 按距离打折, 封顶 ProximityThreatCap
    ///   挨打               -&gt; 按"掉了百分之几的血"加威胁, **不看距离**
    ///   在脱战半径内       -&gt; 每秒掉 ThreatDecayPerSecond
    ///   在脱战半径外       -&gt; 每秒掉 MaxThreat / GiveUpSeconds (掉完就忘)
    ///
    /// 于是:
    ///
    ///   * 远程点它 -&gt; 有威胁了 -&gt; 追过来 (堵住无伤刷怪)
    ///   * 不打了跑开 -&gt; 出圈快速掉 -&gt; GiveUpSeconds 秒后忘掉, 回家
    ///   * 一直隔着打 -&gt; 威胁一直被刷新 -&gt; 一直追 (这是**对的**, 你还在打它)
    ///   * 贴脸的和远处偷袭的 -&gt; 谁累积的威胁高听谁的
    ///
    /// 距离打折那一手是为了保住旧行为: 没人动过手时, 贴得最近的攒得最快,
    /// 结果还是"追最近的", 不用另写一条规则。
    ///
    /// <c>IsNonTargetable(onlyForZombies: true)</c> 是游戏自己的判据
    /// (Entity.cs:1226), 覆盖 <c>NonAggroAble</c> 和 <c>NonAggroAbleForZombies</c>
    /// 两种状态 —— 不要自己判 "隐身/无敌"。
    /// </summary>
    /// <summary>
    /// 被游戏自己锁定的目标 —— 目前只有肉块诱饵 (Meat Lump, -EF/-qmb.cs) 用 <c>NPC.LockGoalTarget</c>:
    /// "僵尸会冲向肉块去啃它"。
    ///
    /// 锁上之后 <c>GoalTarget</c> 的 setter 什么都不写 (NPC.cs:140), 所以僵尸身体确实朝肉块走;
    /// 但以前这里照样挑威胁最高的玩家, 拿它去判攻击距离、放技能 —— 玩家站在肉块边上照样挨咬
    /// (2026-09-15 审核消耗品查出)。肉块没了原厂自己解锁 (DestroyEntity)。
    /// </summary>
    private static Entity LockedTarget(NPC npc) =>
        npc.GoalTargetLocked && npc.GoalTarget is Entity locked && locked.IsActive && !locked.IsDead ? locked : null;

    private Entity PickTarget(NPC npc, List<Player> players)
    {
        // 激怒时指定的优先目标压过一切 —— "谁在打路障就打谁"。
        // 这一层对应原厂的 LockGoalTarget/ForceGoalTarget (肉块诱饵用的那对),
        // 是**硬锁**, 不走威胁表。
        Entity preferred = PreferredTargetFor(npc);
        if (preferred != null)
        {
            return preferred;
        }

        Entity best = null;
        float bestThreat = 0f;
        float bestDistanceSquared = float.MaxValue;

        List<_0023Oq._0023Ned> threats = npc.AggroList;

        for (int i = 0; i < threats.Count; i++)
        {
            Entity candidate = threats[i]._0023nk;
            float threat = threats[i]._0023Med;

            if (threat <= 0f || !CanBeTargeted(npc, candidate) || !AllowedTeam(npc, candidate))
            {
                continue;
            }

            float distanceSquared = Vector2.DistanceSquared(npc.Position, candidate.Position);

            // 威胁一样高时挑近的 —— 两个人都只是站在旁边没动手时,
            // 表现和以前的"追最近的"一致。
            bool better = threat > bestThreat + ThreatTieEpsilon
                          || (threat > bestThreat - ThreatTieEpsilon
                              && distanceSquared < bestDistanceSquared);

            if (better)
            {
                bestThreat = threat;
                bestDistanceSquared = distanceSquared;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// 威胁差在这个数以内就算"一样高", 改按距离挑。
    ///
    /// 需要它是因为站着攒威胁会撞到同一个上限 —— 两个人都封顶时浮点数不会
    /// 正好相等, 不给容差的话目标会在他们之间每帧乱跳。
    /// </summary>
    private const float ThreatTieEpsilon = 0.01f;

    /// <summary>挂了 <see cref="RestrictToTeam"/> 的僵尸只认那一队。</summary>
    private bool AllowedTeam(NPC npc, Entity candidate) =>
        _onlyTeam.Count == 0
        || !_onlyTeam.TryGetValue(Key(npc), out TeamId team)
        || candidate.TeamId == team;

    /// <summary>这个实体现在能不能被这只僵尸当目标。</summary>
    private static bool CanBeTargeted(NPC npc, Entity candidate) =>
        candidate != null
        && candidate.IsActive
        && !candidate.IsDead
        && candidate.TeamId != npc.TeamId
        && !candidate.IsNonTargetable(onlyForZombies: true);

    /// <summary>
    /// 站在仇恨半径里的人每帧攒一点威胁 —— 形状照抄原厂那个策反 buff
    /// (每 tick 重新播种), 只是多了个按距离打折。
    ///
    /// 被激怒的 (埋伏刷出来的) 无视半径, 见 <see cref="Provoke(NPC)"/>。
    /// </summary>
    private void SeedProximityThreat(NPC npc, List<Player> players, float deltaTime)
    {
        bool provoked = IsProvoked(npc);

        for (int i = 0; i < players.Count; i++)
        {
            SeedOne(npc, players[i], provoked, deltaTime);
        }
    }

    /// <summary>
    /// 一个候选人攒一次威胁: 距离打折 + 视线。玩家和平民走同一条。
    /// </summary>
    private void SeedOne(NPC npc, Entity candidate, bool provoked, float deltaTime)
    {
        if (!CanBeTargeted(npc, candidate))
        {
            return;
        }

        float distance = Vector2.Distance(npc.Position, candidate.Position);

        // 贴脸拿满, 半径边缘拿 0 —— "最近的先被盯上"就是从这来的
        float closeness = provoked ? 1f : 1f - distance / AggroRadius;
        if (closeness <= 0f)
        {
            return;
        }

        // 视线放在距离之后 —— 射线比距离贵, 半径外的根本不用查。
        // 已经在威胁表里的人不再查 (拐过墙角照追, 见 RequireLineOfSight)。
        if (!provoked && RequireLineOfSight && !HasThreatOn(npc, candidate)
            && !CanSee(npc, candidate))
        {
            SightBlocked++;
            return;
        }

        AddThreat(npc, candidate, ProximityThreatPerSecond * closeness * deltaTime,
            cap: ProximityThreatCap);
    }

    /// <summary>
    /// **平民**也会被咬 —— 护送/解救平民的任务里, 僵尸只追玩家的话护送就没有意义。
    ///
    /// 和玩家**完全同一套** —— 包括被激怒的僵尸: 它们对平民也无视半径, 威胁一样高时追近的那个。
    ///
    /// 玩家实测 (2026-09-14): "营救的幸存者没有像玩家一样吸引僵尸仇恨。" 上一版"被激怒的不管平民",
    /// 可十字路口的僵尸几乎都是激怒的 (任务波次、夜袭、沿途), 于是平民等于隐身。
    /// 带优先目标的 (埋伏"谁打路障打谁") 照旧先认那个目标, 见 <see cref="PickTarget"/>。
    /// </summary>
    private void SeedCivilianThreat(NPC npc, float deltaTime)
    {
        if (_civilians.Count == 0)
        {
            return;
        }

        bool provoked = IsProvoked(npc);
        for (int i = 0; i < _civilians.Count; i++)
        {
            SeedOne(npc, _civilians[i], provoked, deltaTime);
        }
    }

    /// <summary>
    /// 不出手的时候朝向跟着走: 在走就朝走的方向, 站着就朝目标。
    ///
    /// 玩家实测 (2026-09-14): "僵尸放完技能后好些方向被锁住了, 导致放完技能后朝向和移动方向不一致。"
    ///
    /// 同步给客户端的朝向是 <c>TargetAimDirection</c> (Character.cs:3195), 原厂只有两处写它:
    /// <c>NPC.Attack</c> 和 <c>NPC.ChangeAimDirection</c> (NPC.cs:969/1022) —— 每帧把它转向移动方向的那段
    /// 在原厂服务端 AI 里, 丢了。以前只在出手那一刻瞄一下, 出完手朝向就停在出手方向, 人往别处走。
    /// 出手/引导中不动它 (<c>CanRotateWhileCasting</c> 那些技能要锁朝向)。
    /// </summary>
    private static void FaceWhereItIsGoing(NPC npc, Entity target)
    {
        if (npc.IsChanneling)
        {
            return;
        }

        Vector2 facing = npc.MoveDirection;
        if (facing == Vector2.Zero)
        {
            facing = target.Position - npc.Position;
        }

        if (facing == Vector2.Zero)
        {
            return;
        }

        facing.Normalize();
        npc.ChangeAimDirection(facing);
    }

    /// <summary>
    /// 出手前把**当前朝向**直接转到位, 不等它按转速慢慢转。
    ///
    /// 玩家实测 (2026-09-14): "BOSS 突然对着别的一个方向一直前进和攻击……技能不能造成伤害和施加 debuff。"
    ///
    /// 技能出手方向读的是当前朝向 <c>AimDirection</c> (例: -fZ/-1Nb.cs:80 爬行冲刺 <c>_0023Gt = AimDirection</c>,
    /// 钩舌/吐酸同样), 而 <c>NPC.Attack</c> / <c>ChangeAimDirection</c> 只写目标朝向, 当前朝向每帧按
    /// <c>RotationSpeed</c> 转 (Character.cs:2667)。人一换位置, 招就朝旧方向放: 探针 16 招里 12 招偏, 好几招正背对人。
    /// 另一半是身上挂着没摘的外力朝向 (AimDirection 优先取它), 见 <see cref="IsDirectionLocked"/> 和 GameManagerServer.ChangeStats。
    /// 服务端直接写当前朝向; 客户端照旧收目标朝向自己转。
    /// </summary>
    public static void AimNow(NPC npc, Vector2 aim)
    {
        if (aim == Vector2.Zero || npc.IsDead)
        {
            return;
        }

        // 不看"能不能动": ChangeAimDirection 在不能动时 (刚冲刺完的移动压制等) 不收, 可 NPC.Attack 照样
        // 把目标朝向写进去、照样出手 —— 出手方向就停在旧的当前朝向上 (探针里剩下偏的全是这种)。
        npc.TargetAimDirection = aim;
        npc.AimDirection = aim;
    }

    /// <summary>
    /// 身上挂着外力朝向 (钩舌还在飞、被拖拽、冲锋中) —— 这时 <c>AimDirection</c> 取的是外力 (Character.cs:541),
    /// 出招只会朝外力方向放, 等外力摘了再出手。原厂 Puller 吐舌头期间也是被 Control 住的。
    /// </summary>
    internal static bool IsDirectionLocked(NPC npc) => npc.ForceDirection != Vector2.Zero;

    /// <summary>
    /// 这一帧场上活着的平民和**玩家的召唤物** —— 和玩家同一套仇恨 (离得近攒威胁、打了加威胁)。
    /// 一帧收一次, 别每只僵尸扫一遍对象表。
    ///
    /// 召唤物: 玩家实测 (2026-09-15) "Armored Isys 的机枪不会吸引僵尸仇恨, 同时检查一下其他角色的召唤物"。
    /// 查下来所有召唤物 (Isys 炮塔、Amber 的感染者、XianMei 分身、Allie 幽灵、Bryce 孢子) 都拉不到仇恨:
    /// 以前这里只收平民, 召唤物站在僵尸脸上也不算候选。判据: 自己有血 (角色或可破坏物) 且主人链上是玩家。
    /// 不可被选中的 (Rico 的 Marco 自带 NonAggroAble) 由 <see cref="CanBeTargeted"/> 照原厂状态筛掉。
    /// </summary>
    private void CollectCivilians(GameManager simulation)
    {
        _civilians.Clear();
        List<GameObjectBase> objects = simulation.ActiveGameObjectList;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is Character character && character.IsCivilian
                && character.IsActive && !character.IsDead)
            {
                _civilians.Add(character);
            }
            else if (objects[i] is Entity summon && IsPlayerSummon(summon))
            {
                _civilians.Add(summon);
            }
        }
    }

    private readonly List<Entity> _civilians = new List<Entity>();

    /// <summary>玩家的召唤物: 不是玩家、不是僵尸, 自己有血, 主人链上有玩家。</summary>
    private static bool IsPlayerSummon(Entity entity) =>
        entity is not Player
        && (entity is Character || entity is Destructible)
        && !(entity is Character c && (c.IsZombie || c.IsCivilian))
        && entity.IsActive && !entity.IsDead
        && entity.Stats.GetValue(StatType.MaxHealth) > 0f
        && OwnedByPlayer(entity);

    private static bool OwnedByPlayer(GameObjectBase gameObject)
    {
        GameObjectBase owner = gameObject.Owner;
        for (int depth = 0; owner != null && owner != gameObject && depth < 8; depth++, owner = owner.Owner)
        {
            if (owner is Player)
            {
                return true;
            }

            if (owner.Owner == owner)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// 伤害记在谁头上。<c>GameManagerServer</c> 给的是主人链上第一个实体 —— 投射物自己就是实体,
    /// 于是炮塔的子弹 (-EF/-nHc) 把仇恨记到了一颗飞一下就没的子弹上, 炮塔本身永远拉不到仇恨。
    /// 这里顺着主人链往上找到**玩家的召唤物或玩家**; 都不是 (地图炮塔的子弹之类) 就照旧。
    /// </summary>
    public static Entity AggroSource(Entity attacker)
    {
        GameObjectBase current = attacker;
        for (int depth = 0; current != null && depth < 8; depth++)
        {
            if (current is Player player)
            {
                return player;
            }

            if (current is Entity entity && IsPlayerSummon(entity))
            {
                return entity;
            }

            if (current.Owner == current)
            {
                break;
            }

            current = current.Owner;
        }

        return attacker;
    }

    /// <summary>
    /// 精英怪**施法期间霸体** —— 放技能时打不断。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "精英怪使用技能时会有霸体, 无法被打断, 身上发金光。"
    ///
    /// ## 原厂那条规矩漏了精英
    ///
    /// 施法开始时原厂自己就会上霸体 (Ability.cs:1462):
    ///
    ///     if (IsImmuneToWeakInterruptsDuringCast
    ///         &amp;&amp; (Owner.IsSpecialZombie || Owner.IsHoarderZombie))
    ///         _00237Z = ApplyStatEffect(this, Owner, StatEffectType.MeleeKnockbackImmune);
    ///
    /// 可精英类在初始化里写的是 <c>IsElite = true; IsSpecialZombie = false;</c>
    /// (-Mk/-hPb.cs:102-103, 五个精英类都这样) —— 两个条件都不满足, 精英放技能照样被一刀
    /// 砍断。这里补上: 精英正在施法 -&gt; 挂同一个 <c>MeleeKnockbackImmune</c>
    /// (就是"近战击退免疫", 原厂对"弱打断"的定义); 施法结束 -&gt; 摘掉。
    ///
    /// 用的判据和效果都是原厂那句里的, 只是把"谁有资格"补全。金光见
    /// <see cref="ZombieFactory.MarkElite"/>。
    /// </summary>
    private void EliteCastArmor(NPC npc)
    {
        if (!npc.IsElite)
        {
            return;
        }

        (int Index, int Uses) key = Key(npc);
        bool casting = npc.AbilityBar?.CastingAbility != null;
        bool armored = _eliteArmor.TryGetValue(key, out ConductorGameLogic.Stats.StatEffectChange effect);

        if (casting && !armored)
        {
            _eliteArmor[key] = npc.GameManager.ApplyStatEffect(npc, npc,
                ConductorGameLogic.Stats.StatEffectType.MeleeKnockbackImmune);
            EliteArmorApplied++;
        }
        else if (!casting && armored)
        {
            effect.Remove();
            _eliteArmor.Remove(key);
        }
    }

    private readonly Dictionary<(int Index, int Uses), ConductorGameLogic.Stats.StatEffectChange> _eliteArmor =
        new Dictionary<(int, int), ConductorGameLogic.Stats.StatEffectChange>();

    /// <summary>诊断计数: 精英施法时上过几次霸体。</summary>
    public int EliteArmorApplied;

    /// <summary>
    /// 这只僵尸看不看得见那个人 —— 原厂"看得见"的同一句几何射线,
    /// 理由见 <see cref="RequireLineOfSight"/>。
    ///
    /// 查不了 (没有碰撞管理器) 就当看得见 —— 宁可退回老行为, 也别让僵尸全瞎。
    /// </summary>
    public static bool CanSee(Entity looker, Entity target)
    {
        _00236._0023Cc geometry = looker?.CollisionManager?._0023c8;
        if (geometry == null || target == null)
        {
            return true;
        }

        return !geometry._00233b(looker.Position, target.Position,
            GeometryCollisionFlags._00237jd | GeometryCollisionFlags._00239jd,
            _00237b: false, _00238b: false);
    }

    /// <summary>威胁表里已经有这个人了吗 (已经盯上了)。</summary>
    private static bool HasThreatOn(NPC npc, Entity entity)
    {
        List<_0023Oq._0023Ned> threats = npc.AggroList;

        for (int i = 0; i < threats.Count; i++)
        {
            if (ReferenceEquals(threats[i]._0023nk, entity) && threats[i]._0023Med > 0f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 出了脱战半径之后威胁每秒掉多少 —— 由 <see cref="GiveUpSeconds"/> 折算,
    /// 见那里的说明。
    /// </summary>
    private float OutOfLeashDecayPerSecond =>
        GiveUpSeconds > 0f ? ProximityThreatCap / GiveUpSeconds : ProximityThreatCap;

    /// <summary>
    /// 威胁随时间掉。
    ///
    /// 在脱战半径**外**的掉得快得多 (见 <see cref="OutOfLeashDecayPerSecond"/>),
    /// 这样"打完就跑"才会被放弃; 在圈内只按
    /// <see cref="ThreatDecayPerSecond"/> 慢慢掉, 免得站着对峙时目标乱跳。
    /// </summary>
    private void DecayThreat(NPC npc, float deltaTime)
    {
        List<_0023Oq._0023Ned> threats = npc.AggroList;
        float leashSquared = LeashRadius * LeashRadius;
        float awayDecay = OutOfLeashDecayPerSecond;

        for (int i = threats.Count - 1; i >= 0; i--)
        {
            _0023Oq._0023Ned entry = threats[i];

            if (entry._0023nk == null || !entry._0023nk.IsActive || entry._0023nk.IsDead)
            {
                threats.RemoveAt(i);
                continue;
            }

            bool away = Vector2.DistanceSquared(npc.Position, entry._0023nk.Position) > leashSquared;
            float rate = away ? awayDecay : ThreatDecayPerSecond;

            // 上限也在这里兜一次 —— 原厂的 AddAggroToList 不封顶, 策反 buff
            // 每 tick 加 20 会一路涨上去。
            entry._0023Med = Math.Min(MaxThreat, entry._0023Med - rate * deltaTime);

            if (entry._0023Med <= 0f)
            {
                threats.RemoveAt(i);
            }
            else
            {
                threats[i] = entry;
            }
        }
    }

    /// <summary>
    /// 加威胁, 带上限。
    ///
    /// 不直接用原厂的 <c>AddAggroToList</c> 是因为那个不封顶 —— 站着不动会一路
    /// 涨到把"动过手的人"压下去。语义其余部分一样 (有就累加, 没有就新建)。
    /// </summary>
    private static void AddThreat(NPC npc, Entity entity, float amount, float cap)
    {
        if (entity == null || amount <= 0f)
        {
            return;
        }

        List<_0023Oq._0023Ned> threats = npc.AggroList;

        for (int i = 0; i < threats.Count; i++)
        {
            if (threats[i]._0023nk != entity)
            {
                continue;
            }

            _0023Oq._0023Ned existing = threats[i];

            // 已经超过这一档的上限就别再加 —— 但也**不要往下压**,
            // 伤害攒起来的高威胁不能被"站着"这一档的低上限削掉。
            if (existing._0023Med < cap)
            {
                existing._0023Med = Math.Min(cap, existing._0023Med + amount);
                threats[i] = existing;
            }

            return;
        }

        npc.AddAggroToList(entity, Math.Min(cap, amount));
    }

    /// <summary>
    /// 放一个特殊技能 —— 屠夫横扫 / 跳劈、公羊冲撞 / 跺地、飘浮者吐酸、
    /// 自爆者扑爆、尖叫者恐惧、Puller 钩舌。
    ///
    /// ## 为什么基础攻击那条路够不到它们
    ///
    /// <c>TauntAbilityIdentifier</c> 的语义就是"基础攻击", 每个 NPC 类都把它
    /// 设成了自己的近战那一招。特殊技能全在**别的技能槽**上, 只放基础攻击的话
    /// 一辈子都碰不到 —— 玩家看到的就是"屠夫只会普攻"、"自爆者不炸"。
    /// 哪一招该在什么距离放, 见 <see cref="ZombieSpecials"/>。
    ///
    /// ## 按技能槽顺序试, 第一条能用的就放
    ///
    /// 槽序是原厂摆的, 本身就是优先级 (屠夫: 横扫 22 在前, 跳劈 50 在后),
    /// 所以贴脸横扫、拉开了跳劈, 不用我们另编排序。
    ///
    /// 遍历的是<b>装在槽里</b>的技能 (<c>AbilityBar.AbilitySlots</c>), 不是缓存表 ——
    /// 于是教程模式换过槽的 Puller 自动只有钩舌没有爬行冲刺 (-Mk/-mPb.cs:109),
    /// 不用特判教程。
    ///
    /// ## 原厂链子全在, 我们只负责"起手"
    ///
    /// <c>Attack()</c> 之后的事一件都不用管。以钩舌为例: 投射物
    /// <c>_0023QBc</c> (-EF/-QBc.cs) 自己飞、自己判定命中, 命中后在
    /// <c>_0023CF</c> 里挂 <c>_0023xBc</c> 拖拽 buff (:131-136), buff 再自己
    /// 播舌头特效、拉人、按 1.2 秒一跳掉血。客户端那两截舌头也是这条链子画的
    /// (飞行段 -EF/-QBc.cs:51, 拖拽段 -Sq/-xBc.cs:121)。
    ///
    /// ## 冷却 / 资源 / 打断
    ///
    /// <c>Ability.CanUse()</c> 一次问清 (Ability.cs:833 转 <c>CanUseWithResult</c>,
    /// 冷却、公共冷却、施法中、消耗全在里面)。先问一遍再决定"这一帧算不算出手了",
    /// 否则一招在冷却里会让整个 <see cref="TrySpecial"/> 白占一帧、近战也跟着不放。
    ///
    /// 施法中 / 引导中的整只跳过: 教程那只 Puller 正拉着 Amber, 它再去出手会
    /// 打断自己的引导, Amber 身上的钩子跟着就断。
    /// </summary>
    /// <returns>true 表示这一帧已经出手, 不要再走近战。</returns>
    private bool TrySpecial(NPC npc, Entity target)
    {
        if (npc.IsPerformingAnAttack || npc.IsChanneling || IsDirectionLocked(npc))
        {
            return false;
        }

        Ability[] slots = npc.AbilityBar?.AbilitySlots;
        if (slots == null)
        {
            return false;
        }

        Vector2 toTarget = target.Position - npc.Position;
        float distance = toTarget.Length();
        float melee = AttackStopDistance + npc.Radius + target.Radius;

        for (int slot = 0; slot < slots.Length; slot++)
        {
            Ability ability = slots[slot];
            if (ability == null)
            {
                continue;
            }

            // 基础攻击归 TryAttack 管 —— 它那边还要判近战距离。
            if (ability.AbilityIdentifier == npc.TauntAbilityIdentifier)
            {
                continue;
            }

            if (!ZombieSpecials.TryGet(ability.AbilityIdentifier, out ZombieSpecials.Special special))
            {
                continue;
            }

            // 射程按圆心到圆心算, 再加目标半径 —— Position 是圆心, 而判定
            // (锥形/圆形/冲刺撞人) 打的是目标的碰撞体。
            // 开打距离, 不是理论射程 —— 远了就继续走过去, 见 ZombieSpecials.Engage
            if (distance > special.EngageRange + target.Radius)
            {
                continue;
            }

            // 隔着墙放技能必然打在墙上 (钩舌/吐酸撞几何就停)
            if (!CanSee(npc, target))
            {
                continue;
            }

            if (special.WastedAtMelee && distance <= melee)
            {
                continue;
            }

            if (special.Gate != null && !special.Gate(npc, target))
            {
                continue;
            }

            if (!ability.CanUse())
            {
                continue;
            }

            Vector2 aim = toTarget;
            if (aim != Vector2.Zero)
            {
                aim.Normalize();
            }

            AimNow(npc, aim);

            // 瞄准距离也给上: 抛物类 (飘浮者炸弹/精英投掷) 的落点、爬行冲刺的位移都读 AimLength
            // (-fZ/-BNb.cs:112 Position + AimDirection * AimLength), 不给就落在自己脚下
            if (!npc.Attack(aim, ability.AbilityIdentifier, distance))
            {
                continue;
            }

            if (special.Ability == AbilityIdentifier.PullerHook ||
                special.Ability == AbilityIdentifier.PullerEliteHook)
            {
                HooksIssued++;
            }
            else
            {
                SpecialsIssued++;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 够近就放基础攻击。
    ///
    /// ## 用哪个技能
    ///
    /// <c>Entity.TauntAbilityIdentifier</c>。每个 NPC 类都在自己的
    /// <c>_00237N()</c> 里把它设成了本体的基础攻击 (Walker -> WalkerStrikeNormal,
    /// Butcher -> ButcherStrike, Floater -> FloaterStrike ...), 一个不落。
    /// 嘲讽技能也是靠它反查 NPC 该打什么 (-Sq/-cib.cs:240), 语义就是"基础攻击"。
    ///
    /// 所以不需要我们维护一张"类型 -> 技能"的表 —— 那种表迟早会和实际不同步。
    ///
    /// ## 冷却/资源/打断
    ///
    /// 一概不管。<c>AbilityBar.TryUseAbility</c> (AbilityBar.cs:548) 里
    /// <c>CanUseWithResult()</c> 已经把冷却、公共冷却、施法中、消耗全查了,
    /// 用不了就返回 false。我们只负责"想打"。
    /// </summary>
    private void TryAttack(NPC npc, Entity target)
    {
        // 引导中的也要跳过 —— 教程那只 Puller 正拉着 Amber, 它再去近战玩家
        // 会把自己的引导打断, Amber 身上的钩子跟着就没了。
        if (npc.IsPerformingAnAttack || npc.IsChanneling || IsDirectionLocked(npc))
        {
            return;
        }

        Vector2 toTarget = target.Position - npc.Position;
        float reach = AttackStopDistance + npc.Radius + target.Radius;
        if (toTarget.LengthSquared() > reach * reach)
        {
            return;
        }

        if (toTarget != Vector2.Zero)
        {
            toTarget.Normalize();
        }

        AimNow(npc, toTarget);
        if (npc.Attack(toTarget, npc.TauntAbilityIdentifier))
        {
            AttacksIssued++;
        }
    }

    /// <summary>
    /// 出场动画的到期时刻, 按 NPC 实例记。
    ///
    /// 用字典而不是往 NPC 上加字段, 因为那棵树不能手改 (改了下次 sanitize 就没了)。
    /// NPC 对象是池化复用的, 所以 <see cref="FinishEmerge"/> 到期后必须 Remove ——
    /// 否则同一个实例下次从池里出来会带着上一条命的到期时间。
    /// </summary>
    /// <summary>
    /// 出场动画的结束时刻。
    ///
    /// ## 键必须带池复用次数
    ///
    /// 原本用 <c>NPC</c> 实例当键 —— **会串号**: 僵尸死了回对象池, 下一只从
    /// 同一个槽位取出来还是那个实例, 字典里残留的 deadline 是**过去的时间**,
    /// 于是它一出生就被判定"动画已经放完", 在出场动画期间就能转向和移动。
    ///
    /// 玩家实测: "僵尸播放刷新动画时还会旋转, 应该播放完刷新动画后才能旋转和移动。"
    ///
    /// <c>NumberOfGlobalCacheUses</c> 每次从池里取出都加一, 组合起来才唯一
    /// (和 <see cref="_provoked"/> 同一招)。
    /// </summary>
    private readonly Dictionary<(int Index, int Uses), float> _emergeDeadline =
        new Dictionary<(int, int), float>();
}
