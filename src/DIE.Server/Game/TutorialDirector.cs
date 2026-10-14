using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Entities.GameModeObjects;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;
using _0023z;

namespace DIE.Server.Game;

/// <summary>
/// 教程状态机的推进器。
///
/// ## 丢的是什么
///
/// 客户端 (-bd/-5cc.cs:296-560) 留着**状态变化后播什么**: 引导文字、语音、
/// 镜头、箭头指向哪。25 个状态一个不少。
///
/// 丢的是**每一步靠什么推进** —— 那是服务端知识。本类逐步把它补回来。
///
/// ## 推进条件从哪来 (不是凭空编的)
///
/// 1. **客户端每个状态都设了目标** —— <c>_0023Lcc</c> (目标位置) 或
///    <c>_0023Mcc</c> (目标实体)。那就是"这一步要你去哪/找谁"。
///
/// 2. **到达阈值 = 30**。引导箭头 <c>_00232cc</c> (:586) 的判定是
///
///        if (Vector2.Distance(target, player) > 30f)  显示箭头
///
///    也就是说原厂认为**离目标 30 以内就算到了** (箭头功成身退)。
///    位置类步骤直接用这个数, 不用我们拍脑袋。
///
/// 3. **击杀类**看客户端指的是哪个实体 —— 比如状态 <c>_00237bc</c> 指向
///    Puller, 下一状态 <c>_0023hcc</c> 就把 RESCUE 引导藏起来, 那这一步
///    显然是"杀掉那只 Puller"。
///
/// ## 已实现到哪一步
///
/// 见 <see cref="Steps"/>。没实现的步骤**不会**乱推进 —— 状态机停在那里,
/// 而不是跳过去。宁可卡住也不要假装走完了。
/// </summary>
public sealed class TutorialDirector
{
    /// <summary>
    /// "到了"的距离阈值。取自客户端引导箭头的判定 (-bd/-5cc.cs:586) ——
    /// 离目标 30 以内箭头就不显示了, 那就是原厂认定的"到达"。
    /// </summary>
    public const float ArrivalRadius = 30f;

    /// <summary>
    /// 一步的定义: 什么时候算完成, 以及进入下一步时要做什么。
    /// </summary>
    private sealed record Step(
        _0023occ State,
        string Description,
        Func<Context, bool> IsComplete,
        Action<Context> OnEnterNext = null);

    /// <summary>推进判定要用到的东西, 打包传进去省得每个条件都写一长串。</summary>
    private sealed record Context(GameManager Simulation, Player Player, float TotalTime, float StateAge);

    private readonly List<Step> _steps;

    public TutorialDirector()
    {
        _steps = BuildSteps();
    }

    /// <summary>当前状态已经持续了多久 —— 定时类步骤要用。</summary>
    private float _stateEnteredAt;
    private _0023occ _lastSeenState = (_0023occ)(-1);

    /// <summary>诊断: 上一次判定时当前步骤有没有实现。</summary>
    public bool CurrentStepImplemented { get; private set; }

    /// <summary>
    /// 这局里有没有见过活着的 Puller。
    ///
    /// "杀掉 Puller 了所以营救这步算过"必须靠它把关 —— 光判"场上没有 Puller"
    /// 的话, **Puller 压根没生成出来**时判据也为真, 状态机会静默跳过整个营救环节。
    /// 而生成失败正是最需要看见的故障 (地图标记点认不出实体类时
    /// TutorialEntitySpawner 会打 WRN)。卡住是能被观察到的, 悄悄推进不是。
    /// </summary>
    private bool _sawPullerAlive;

    /// <summary>状态推进时的回调, 供宿主打日志。参数: (从, 到, 触发原因)。</summary>
    public Action<_0023occ, _0023occ, string> OnAdvanced { get; set; }

    /// <summary>
    /// 每帧一次。
    /// </summary>
    public void Update(GameManager simulation, float totalTime)
    {
        if (simulation?.GameMode is not _0023bd._00235cc tutorial)
        {
            return;
        }

        if (simulation.ActivePlayerList.Count == 0)
        {
            return;
        }

        Player player = simulation.ActivePlayerList[0];
        if (player == null || !player.IsActive)
        {
            return;
        }

        _0023occ current = tutorial._0023l9._0023pcc;

        if (current != _lastSeenState)
        {
            _lastSeenState = current;
            _stateEnteredAt = totalTime;
        }

        Step step = _steps.FirstOrDefault(s => s.State == current);
        CurrentStepImplemented = step != null;

        if (step == null)
        {
            // 这一步还没实现 —— **停在这里**, 不要瞎跳。
            // 卡住是能被观察到的故障; 乱推进会让后面的状态全部错位, 而且
            // 客户端的引导文字会指向错误的目标, 更难查。
            return;
        }

        Context context = new Context(simulation, player, totalTime, totalTime - _stateEnteredAt);

        // 记下"这局确实有过 Puller" —— 见 _sawPullerAlive 的说明。
        if (!_sawPullerAlive && AnyAlive(context, e => e.IsPuller))
        {
            _sawPullerAlive = true;
        }

        // 同理: 遭遇战那个敌人。判据必须带"见过"这个前提, 否则它生成失败时
        // "场上没有敌人"也为真, 整段遭遇战会被静默跳过。
        if (!SawHostileNpc && AnyHostileTutorialNpcAlive(context))
        {
            SawHostileNpc = true;
        }

        // 同理: 终局那只 Floater。
        if (!SawFloater && AnyAlive(context, e => e.IsFloater))
        {
            SawFloater = true;
        }

        NoticeDeliveryStarted(context, current);

        if (!step.IsComplete(context))
        {
            return;
        }

        _0023occ next = current + 1;

        // GameModeData 是 struct 属性, 必须改字段本身 —— 改属性返回的副本
        // 是没有效果的。字段可见性由 sanitizer 放开, 理由见那里。
        tutorial._0023l9._0023pcc = next;
        _stateEnteredAt = totalTime;
        _lastSeenState = next;

        step.OnEnterNext?.Invoke(context);
        OnAdvanced?.Invoke(current, next, step.Description);
    }

    /// <summary>
    /// 各步骤的推进条件。
    ///
    /// **只列已经想清楚的** —— 没列的步骤状态机会停住, 那是刻意的。
    /// </summary>
    private List<Step> BuildSteps() => new List<Step>
    {
        // ── 0 -> 1 开场 ────────────────────────────────────────────────
        // 由 SkipTutorialIntro / TutorialCameraComplete 消息驱动, 不走这里
        // (见 LidgrenHost.AdvanceTutorialPastIntro)。玩家按 SPACE 或镜头自然
        // 放完都会发消息, 用消息比用定时准。

        // ── 1 -> 2 醒来 ────────────────────────────────────────────────
        // 客户端进入状态 1 时播 TutorialWakeUp 语音 + Tutorial_WakeUp 动画,
        // 状态 2 才显示"移动"引导。所以这一步是**等醒来动画放完**。
        //
        // 时长没有数据可依 —— 客户端那行是 PlayAnimation(AnimationEnum.Tutorial_WakeUp)
        // (-bd/-5cc.cs:305), 传的是枚举而不是 AnimationData, 时长藏在动画资源里,
        // 服务端读不到。
        //
        // **这个数同时决定玩家什么时候能动** —— TutorialIntroLock 锁到状态 2 为止。
        // 第一版 3 秒, 玩家实测"从沙滩醒来的动画还没播放完就可以移动了", 加到 5。
        new Step(
            _0023occ._0023ecc,
            "醒来动画放完",
            ctx => ctx.StateAge >= 5f),

        // ── 2 -> 3 学会移动 ────────────────────────────────────────────
        // 状态 2 显示"移动"引导, 状态 3 把它藏起来并开始指船桨。
        // 所以条件是**玩家真的动了**。
        //
        // 用"离出生点的距离"而不是"按了键" —— 服务端看得到位置, 看不到按键;
        // 而且位置是权威数据, 按键是客户端说了算的。
        new Step(
            _0023occ._0023Ybc,
            "玩家学会移动",
            ctx => MovedFromStart(ctx) >= 25f),

        // ── 3 -> 4 走到船桨 ────────────────────────────────────────────
        // 状态 3 指向 Spawn_Tutorial(PADDLE), 状态 4 把 PADDLE 引导藏起来。
        // 条件是走到那儿。
        //
        // 进入状态 4 时发第一把武器 —— Player.RespawnCharacter (:1867) 在
        // 状态 >= _00230bc 时补发 1005, 说明 1005 就是教程第一把武器,
        // 而它必须在**这一步之前**已经在手上, 否则复活补发就没有意义。
        new Step(
            _0023occ._002345b,
            "拾取船桨",
            ctx => PickedUpPaddle(ctx),
            ctx => GiveTutorialWeapon(ctx.Player, slot: 0, schematicId: PaddleSchematicId)),

        // ── 4 -> 5 走到栅栏 ────────────────────────────────────────────
        // 状态 4 指向 FenceBarricade, 状态 5 显示"栅栏"引导。
        //
        // ## 光判"走到 30 以内"会把玩家钉死 (玩家实测)
        //
        // 玩家原话: "打破第一道门后任务指引箭头没有指向下一个任务点。"
        //
        // 路障自己半径 12, 加上玩家半径和近战挥击范围, **贴着打也常在 30 开外**。
        // 于是玩家把栅栏打没了, 状态还卡在第 4 步 —— 而箭头目标由客户端按
        // 当前状态决定, 状态不动箭头就一直指着那堆已经不存在的栅栏。
        // (复现: 站 40 外打破, 状态原地不动。)
        //
        // 所以补一条"已经打破了也算到场" —— 和状态 6->7 认"Puller 已经死了"、
        // 状态 10 认"已经摸到木栅栏"是同一个道理: 玩家已经做完了后面那件事,
        // 还卡在前一步毫无意义。
        new Step(
            _0023occ._0023fcc,
            "走到栅栏",
            ctx => ReachedDestructible(ctx, BarricadeType.FenceBarricade)
                   || BarricadeCleared(ctx, BarricadeType.FenceBarricade)),

        // ── 5 -> 6 打破栅栏 ────────────────────────────────────────────
        // 状态 5 显示"打破栅栏"引导, 状态 6 把它藏起来并指向下一个坐标。
        //
        // 判据是**场上还有没有 FenceBarricade 活着** —— 栅栏由
        // DestructibleSpawner 按地图数据生成, 打光了就推进。
        new Step(
            _0023occ._00230bc,
            "打破栅栏",
            ctx => DestructibleSpawner.CountAlive(ctx.Simulation, BarricadeType.FenceBarricade) == 0),

        // ── 6 -> 7 走到营救现场 ────────────────────────────────────────
        // 状态 6 的箭头指向硬编码坐标 (1588, 3072) —— 那个数字直接写在客户端里
        // (-bd/-5cc.cs:347)。
        //
        // ## 光判"到没到那个点"是不够的 (玩家实测)
        //
        // 那个坐标只是**观察点**, 真正的目标在它旁边:
        //
        //     集合点 (1588.0, 3072.0)   箭头指向这里
        //     PULLER (1622.8, 3106.6)   距 49
        //     NPC    (1658.9, 3108.5)   距 80   ← Amber, 玩家眼里的目标
        //
        // 而 ArrivalRadius 是 30。玩家看到的是"Amber 被 Puller 拉着", 自然直奔她去,
        // 很容易**擦不进那个 30 半径的圈** —— 于是状态机卡在第 6 步,
        // 玩家把 Puller 杀了也不发枪, 非得回头走进那片空地才行。
        //
        // 玩家的原话: "杀掉 puller 后没有马上获得枪, 还要走到指定位置获得;
        // 我先不击杀 puller, 先走到这个指定的位置会发现触发了 Ember 的求救对话,
        // 然后我再把 puller 打死就会马上获得枪。"
        //
        // ## 三条路都算到场
        //
        // 状态 7 做的事是显示 RESCUE 引导 + 把箭头转向 Puller 本体
        // (-5cc.cs:353-363)。玩家只要已经在这块区域, 那件事就该发生 ——
        // 已经在打 Puller 甚至打死了还卡在第 6 步, 毫无意义。
        //
        // 第三条 (Puller 死了) 会让状态 6->7 和 7->8 在相邻两帧连推,
        // 玩家体验就是"杀掉 Puller 立刻拿枪", 正是原版的样子。
        // 它带 _sawPullerAlive 前提 —— 没那个前提的话 Puller 生成失败时
        // 判据也为真, 营救环节会被静默跳过。
        new Step(
            _0023occ._0023gcc,
            "走到营救现场",
            ctx => Reached(ctx, RallyPoint)
                   || NearRescueScene(ctx)
                   || PullerWasKilled(ctx)),

        // ── 7 -> 8 杀掉 Puller ────────────────────────────────────────
        // 状态 7 显示 RESCUE 引导并指向场上的 Puller, 状态 8 把 RESCUE 和
        // PULLERKILL 两个引导都藏起来 —— 那就是"Puller 死了"。
        //
        // Puller 由 TutorialEntitySpawner 按 Spawn_Tutorial(PULLER) 生成。
        //
        // 进入状态 8 时发霰弹枪 1009。槽位和是否装备见 ShotgunSlot 的说明。
        new Step(
            _0023occ._00237bc,
            "杀掉 Puller",
            ctx => !AnyAlive(ctx, e => e.IsPuller),
            ctx => GrantShotgun(ctx)),

        // ── 8 -> 9 走到发电机 ──────────────────────────────────────────
        // 状态 8 (-bd/-5cc.cs:365) 藏 RESCUE/PULLERKILL 引导、播 TutorialPuller02
        // 语音, 箭头指向 Trigger_TutorialLightingGenerator。**没有新引导文字。**
        //
        // 状态 9 (:378) 才显示 SWITCH_WEAPON ("切换武器") + GENERATOR 引导。
        //
        // 这个区分很重要: 玩家反馈"箭头指着某处但按 C 没反应" —— 因为教玩家
        // 切武器是状态 9 的事, 而我们卡在 8, 引导文字压根没出来。
        //
        // 推进条件是走到发电机跟前。用 ArrivalRadius (30) —— 和客户端引导箭头
        // 的消失阈值同源。
        new Step(
            _0023occ._0023hcc,
            "走到发电机",
            ctx => ReachedGenerator(ctx),
            // 状态 9 的引导是 SWITCH_WEAPON("按 C 切换武器") + GENERATOR("打这个发电机")
            // —— 玩家自己按 C 切枪, 这是原版设计, 见 AutoSwapToShotgun 的说明。
            ctx => SwapToShotgun(ctx)),

        // ── 9 -> 10 打爆发电机 ─────────────────────────────────────────
        // 状态 9 显示"切换武器"和"发电机"两条引导, 状态 10 (:391) 把它们藏起来
        // 并把箭头指向 PREBARRICADE —— 所以 9->10 就是"发电机没了"。
        //
        // 变压器自带 60 血 (-EK/-p0b.cs:19), 霰弹枪几下就掉。
        // 判据用血量而不是 IsDead, 理由见 TutorialWorldSpawner.CountAliveGenerators。
        new Step(
            _0023occ._00234bc,
            "打爆发电机",
            ctx => TutorialWorldSpawner.CountAliveGenerators(ctx.Simulation) == 0,
            ctx => CutPowerToElectricBarricades(ctx.Simulation)),

        // ── 10 -> 11 走到木栅栏跟前 ────────────────────────────────────
        // 状态 10 (-5cc.cs:391) 藏 SWITCH_WEAPON/GENERATOR, 箭头指向
        // Spawn_Tutorial(PREBARRICADE) @ (1493.6, 3644.5)。
        // 状态 11 显示 ZOMBIEAMBUSH + BARRICADE 引导并播 TutorialBarricade01。
        //
        // ## 判据是"走到栅栏跟前", 不是"走到 PREBARRICADE"
        //
        // PREBARRICADE 距木栅栏 (x≈1130, y 3681~3724) 有 366 单位 ——
        // 光判那个标记点的话, 玩家刚离开发电机就触发了栅栏对话和埋伏,
        // 而栅栏还在三百多单位开外。玩家反馈"触发 barricade 对话的位置不对,
        // 离 barricade 太远了"。
        //
        // 那个标记点是**箭头目标**(客户端拿它指路), 不是剧情触发点 ——
        // 和状态 6 的 (1588, 3072) 是同一类东西, 上一轮已经栽过一次:
        // 把箭头目标当成到达判据, 玩家的实际路线就对不上。
        //
        // 半径用 BarricadeApproachRadius (100) 而不是 ArrivalRadius (30):
        // 三段栅栏横着排开, 判的是"进了这片区域", 不是"踩到某一段的脚下"。
        //
        // ## 两个条件取或 —— 上一轮只留后者是错的
        //
        // 我上一轮把判据换成"只认走到栅栏跟前", 结果玩家站在 PREBARRICADE 上
        // 状态不推进, 客户端箭头就一直钉在那个空地上:
        // "箭头会指向一处什么都没有的地方, 走过了还是指回那里"。
        //
        // 箭头目标由客户端按**当前状态**决定 —— 状态不动, 箭头就不动。
        // 所以凡是客户端拿来指路的点, 到了就必须推进, 否则玩家会被钉在原地。
        // 而"走到栅栏跟前"那条留着, 是为了玩家绕开标记点直接摸到栅栏时也能过。
        new Step(
            _0023occ._0023icc,
            "走到木栅栏跟前",
            ctx => ReachedTutorialMarker(ctx, TutorialType.PREBARRICADE)
                   || NearDestructible(ctx, BarricadeType.WeakWoodenBarricade,
                                       BarricadeApproachRadius)
                   || BarricadeCleared(ctx, BarricadeType.WeakWoodenBarricade)),

        // ── 11 -> 12 打破木栅栏 ────────────────────────────────────────
        // 状态 11 箭头指向 WeakWoodenBarricade (三段, GroupID 10103,
        // @ x≈1130, y 3681~3724), 显示 ZOMBIEAMBUSH("僵尸埋伏") + BARRICADE 引导,
        // 播 TutorialBarricade01。状态 12 把那两条藏起来。
        //
        // 判据同栅栏那步: 场上还有没有该类型活着。三段共享血量 (见 AttachToGroup),
        // 所以打一段就整组掉。
        new Step(
            _0023occ._0023Yh,
            "打破木栅栏",
            ctx => DestructibleSpawner.CountAlive(ctx.Simulation,
                                                 BarricadeType.WeakWoodenBarricade) == 0),

        // ── 12 -> 13 路过 (1077, 3792) ─────────────────────────────────
        // 硬编码坐标, 抄自客户端 (-5cc.cs:406)。
        //
        // ## 这是**路径点**, 玩家未必踩得到
        //
        // 玩家实测: "击破 barricade 之后箭头又指向可移动地图之外的地方,
        // 走到了 supply point 了箭头还指着那里"。也就是说那个坐标落在
        // 不可行走的区域里 —— 拿"走到 30 单位内"当判据等于永远卡住,
        // 而状态不推进客户端箭头就一直钉在那儿。
        //
        // 改成"到达**或已越过**": 玩家离下一个目标比这个路径点更近, 就算过了。
        // 见 PassedWaypoint。
        new Step(
            _0023occ._0023jcc,
            "路过集合点二",
            ctx => PassedWaypoint(ctx, new Vector2(1077f, 3792f), SupplyPointHint)),

        // ── 13 -> 14 走到补给点 ────────────────────────────────────────
        // 客户端箭头指硬编码的 (493, 3660), 那紧挨着 lifeguard station
        // (505.6, 3611.8) —— 相距不到 50 单位, 就是"补给点门口"。
        // 状态 14 (_0023acc) 显示 SECURE 引导并把箭头转到 StorageRoom 本体。
        new Step(
            _0023occ._0023kcc,
            "走到补给点",
            ctx => PassedWaypoint(ctx, new Vector2(493f, 3660f), SupplyPointHint)),

        // ── 14 -> 15 开始占领 ─────────────────────────────────────────
        // 客户端这两个状态的引导文本把分界说得很清楚:
        //   14 (_0023acc)  SECURE  "去占领这个点"
        //   15 (_00235bc)  SECURE2 "StayNear" —— 也就是圆环 HUD 上那句
        //                  "REMAIN CLOSE UNTIL FULLY SECURED"
        // 所以 14->15 是"占领**开始**了"。判据用 DenyTakeOverTeam ——
        // StorageRoomCaptureDirector 在读条读完那一刻把它设成占领方,
        // 那正是原厂表示"这个点正在被接管"的字段。
        new Step(
            _0023occ._0023acc,
            "开始占领补给点",
            ctx => AnyStorageRoom(ctx, room => room.DenyTakeOverTeam != TeamId.Neutral)),

        // ── 15 -> 16 占领完成 ─────────────────────────────────────────
        // 状态 16 (_0023l2) 把 SECURE2 收起来, 改显示 SUPPLY "PickupSupplies"
        // 并把箭头挪到补给箱那边 —— 也就是玩家说的"占领完箭头应该指向下一个地点"。
        //
        // 判据是 DenyRoomHolder 被设上, 那是原厂表示"谁持有这个点"的字段。
        new Step(
            _0023occ._00235bc,
            "占领完成",
            ctx => AnyStorageRoom(ctx, room => room.DenyRoomHolder != TeamId.Neutral)),

        // ── 16 -> 17 捡起补给 ─────────────────────────────────────────
        // **状态 16 的箭头还是指补给点** (-5cc.cs:459 拿 StorageRoom.Position),
        // 那是原厂行为, 不是 bug —— 箱子就掉在那儿, 引导语也是 "PickupSupplies"。
        // 箭头挪走要到状态 17 (_0023lcc, :466 指向硬编码的 -181,3353)。
        //
        // 玩家反馈"占领结束后目标箭头还是没有指向下一个位置"说的就是这一步没做。
        //
        // 判据: 玩家身上有补给了。原厂读法是
        // GameMode.AsISupplies._00239c()._0023Fj(IndexPlayer) (Player.cs:879),
        // 教程模式把那个对象暴露成了 public 的 SupplyData。
        new Step(
            _0023occ._0023l2,
            "捡起补给",
            ctx => CarriedSupplies(ctx) > 0),

        // ── 17 -> 18 走到遭遇战地点 ───────────────────────────────────
        // 状态 17 (_0023lcc) 唯一做的事就是把箭头挪到硬编码的 (-181, 3353)
        // (-5cc.cs:466-468), 没有引导文本 —— 它是个纯粹的"走过去"。
        //
        // 状态 18 (_0023bcc) 才是遭遇战: 显示 AMBUSH 引导 + TutorialAmbush01 语音,
        // 并把箭头转到场上的 Wrench/Trapper NPC 身上 (:471-489)。
        //
        // 玩家反馈"箭头直接通到卡车那里了, 中间应该还有一个遭遇敌对敌人的战斗"
        // —— 说的就是缺了这一步, 状态卡在 17 时箭头一直指着那个坐标。
        // ## 判据不能用客户端那个箭头坐标
        //
        // 上一版写的是 `Reached(ctx, (-181, 3353))` —— 那是**卡车**, 遭遇战
        // 在半路上。真机日志里状态从 02:18:01 起一直停在 17, 玩家一路打到卡车
        // 都没进 18, 于是 TutorialAmbush01 那段对话也没播 (它是客户端进状态 18
        // 时自己播的, -5cc.cs:473)。玩家反馈"碰到敌对 NPC 时还是没有播放对话"
        // 就是这个。
        //
        // 改成判**敌人站的那片地**: 地图 ENEMY 标记在 (60.7, 3533.7)
        // (实测 dump), 正在补给点到卡车的路上。半径用
        // AmbushApproachRadius(120) —— 判"进了这片空地", 和木栅栏那步同一个思路。
        //
        // 后面那条"已经越过"是防死锁的兜底: 玩家绕开那片地直奔卡车时,
        // 状态不推进的话客户端箭头会一直钉在原地 (状态 12 踩过这个坑)。
        new Step(
            _0023occ._0023lcc,
            "走到遭遇战地点",
            ctx => NearTutorialMarker(ctx, TutorialType.ENEMY, AmbushApproachRadius)
                   || PassedWaypoint(ctx, AmbushPoint, TutorialFinaleDirector.TruckPosition)),

        // ── 18 -> 19 打赢遭遇战 ───────────────────────────────────────
        // 状态 18 (_0023bcc) 显示 AMBUSH 引导 + TutorialAmbush01 语音, 箭头指向
        // 场上的 Wrench/Trapper (-5cc.cs:471-489)。玩家要的"遭遇敌对 NPC 战斗"
        // 就是这一段。
        //
        // 判据: 那个敌对 NPC 死了。地图上 ENEMY 标记只有一个
        // (日志实测 "ENEMY×1"), 由 TutorialEntitySpawner 生成成敌对队伍。
        //
        // ⚠ 它的**类**是我们选的, 没有直接依据 —— 见 TutorialEntitySpawner.Known
        // 里那段说明。判据本身不受影响 (只看"敌对教程 NPC 死没死"), 但如果玩家
        // 说那个人物不对, 换个类就行, 这一步不用动。
        new Step(
            _0023occ._0023bcc,
            "打赢遭遇战",
            ctx => SawHostileNpc && !AnyHostileTutorialNpcAlive(ctx)),

        // ── 19 -> 20 和卡车队友汇合 ───────────────────────────────────
        // 状态 19 (_00232sb) 客户端只做一件事: 把箭头挂到场上任意一个**活着的
        // Team1 实体**上 (-5cc.cs:491-502)。而 Team1 在这张图上就是卡车那两个
        // 队友 Wrench/Trapper (见 TutorialEntitySpawner.TruckCrewTeam) ——
        // 所以这一步是"去卡车那边和他们会合"。
        //
        // 半径用客户端箭头自己的消失半径 30 (:498 _0023Ncc = 30f), 两边同一个数
        // 就不会出现"箭头收了但状态没过"。
        //
        // 汇合同时也是 TutorialNpcAI 让那两个队友起身跟随的门槛 ——
        // 玩家要求"卡车那里的队友汇合后应该和 amber 一样行动"。
        new Step(
            _0023occ._00232sb,
            "和卡车队友汇合",
            ReachedTruckCrew),

        // ── 20 -> 21 走到卡车 ─────────────────────────────────────────
        // 状态 20 (_0023mcc) 把 AMBUSH/ENEMYKILL 引导收起来, 箭头转到硬编码的
        // 卡车坐标 (-5cc.cs:503-511)。纯粹的"走过去"。
        //
        // OnEnterNext 记下玩家身上背了多少 —— 下一步要靠它判"交完了没有"。
        new Step(
            _0023occ._0023mcc,
            "走到卡车",
            ctx => Reached(ctx, TutorialFinaleDirector.TruckPosition),
            ctx => _suppliesOnArrival = CarriedSupplies(ctx)),

        // ── 21 -> 22 把补给交到卡车上 ─────────────────────────────────
        // 状态 21 (_0023Q8b) 显示 DELIVER 引导 + 播 TutorialSinamoiSupplies
        // (-5cc.cs:512-522)。
        //
        // ## 交付链路**是完整的**, 我们一行都不用补
        //
        //   1. 交付点 _0023edb 由 GameMode.SpawnDeliveryPoints() 从地图的
        //      TruckerSpawnPoint 生成 (GameMode.cs:799) —— 那是共享代码, 服务端
        //      照样跑。地图实测: 那个点 Team=Team2, 和玩家同队 ✓
        //   2. 客户端在半径 15 内且**教程状态恰好是 _0023Q8b**、且玩家身上有补给时
        //      推 DeliverSupplies 交互提示 (-7P/-edb.cs:54-57) —— 所以必须先走到
        //      这一步, 提示才会出来
        //   3. AbilityIdentifier.DeliverSupplies 早就绑在技能槽 17 上
        //      (Player.cs:546)
        //   4. 交付 buff _0023Sq._0023ajc 的每帧钩子是**服务端权威**的
        //      (`if (!GameManager.IsServer ...) return`, :200), 自己扣玩家身上的
        //      补给、加队伍补给、发经验和积分
        //
        // 也就是说这一步唯一的前提是 SupplyRegistrar 把玩家登记进补给表 ——
        // 那个上一轮已经做了 (不登记的话第 2 步的 _0023Fj 恒为 0, 提示压根不出)。
        //
        // 判据: 身上背的清零了。进这一步时必然 > 0 (状态 16 的门槛就是"捡起补给"),
        // 而清零的唯一途径就是交付。
        //
        // OnEnterNext 拉起终局: 放 Floater + 围着卡车不停刷怪。
        new Step(
            _0023occ._0023Q8b,
            "把补给交到卡车上",
            ctx => _suppliesOnArrival > 0 && CarriedSupplies(ctx) <= 0,
            ctx => OnDeliveryComplete?.Invoke(ctx.Simulation)),

        // ── 22 -> 23 打死 Floater ─────────────────────────────────────
        // 状态 22 (_0023iPb) 显示 FLOATER 引导 + 播 TutorialFloater01, 并把箭头
        // 挂到场上 IsFloater 的实体上 (-5cc.cs:523-537)。那只 Floater 由
        // TutorialFinaleDirector 在上一步放出来。
        //
        // ## 判据只认**剧情那一只**, 不是"场上没有 Floater 了"
        //
        // 玩家实测: "场上有两只 floater, 杀掉一只后教程提示还在, 要杀掉另外
        // 一只才行"。卡车周边那三组 Event_Assault 自己也会刷 Floater
        // (它们有 Floaters_Min/Max 字段), 所以"清干净"这个判据要玩家多杀一只。
        //
        // 判据委托给 TutorialFinaleDirector —— 它知道自己放的是哪一只。
        // 没接上宿主时 (单测) 退回旧判据, 那时场上只有我们生成的那只。
        new Step(
            _0023occ._0023iPb,
            "打死 Floater",
            ctx => StoryFloaterIsDead != null
                ? StoryFloaterIsDead()
                : SawFloater && !AnyAlive(ctx, e => e.IsFloater)),

        // ── 23 -> 24 撑住最后一波 ─────────────────────────────────────
        // 状态 23 (_0023o2b) 客户端只是把引导全收起来 (-5cc.cs:539-544),
        // 没有新目标 —— 它是收尾前的最后一段。
        //
        // 判据用**时长**: 玩家要的是"不停刷怪, 然后自动结束", 而这一段没有
        // 任何可判的目标物 (怪是无限的, 判"清完"就永远不成立)。
        // FinaleHoldSeconds 是我们定的。
        //
        // 进入状态 24 (_0023ncc) 之后就交给客户端了: 播 Sinamoi 离场音效、
        // 关 HUD、8 秒淡出, 淡黑之后再 5 秒 ShutDownApplication()
        // (-5cc.cs:162-175, 545-556)。**服务端不需要做任何收尾**。
        new Step(
            _0023occ._0023o2b,
            "撑住最后一波",
            ctx => ctx.StateAge >= FinaleHoldSeconds,
            _ => OnFinaleOver?.Invoke()),
    };

    /// <summary>
    /// 补给交付完成 (状态 21 -> 22) 时的回调 —— 宿主拿它拉起
    /// <see cref="TutorialFinaleDirector"/>。
    /// </summary>
    public Action<GameManager> OnDeliveryComplete { get; set; }

    /// <summary>教程收尾 (状态 23 -> 24) 时的回调 —— 宿主拿它停掉终局刷怪。</summary>
    public Action OnFinaleOver { get; set; }

    /// <summary>
    /// 玩家**开始**往卡车上搬补给时的回调 —— 宿主拿它开始按波次刷怪。
    ///
    /// 玩家要求: "开始按X给卡车输送物资后才会像补给点那样按波次开始刷僵尸"。
    /// 和补给点占领同一个节奏 —— 读条一开始怪就来。
    ///
    /// 只回调一次。
    /// </summary>
    public Action<GameManager> OnDeliveryStarted { get; set; }

    private bool _deliveryStartedFired;

    /// <summary>
    /// 玩家身上挂上交付 buff 了吗 —— 那就是"开始搬"。
    ///
    /// ## 判据是原厂的 buff 本体
    ///
    /// 按 X 触发 <c>AbilityIdentifier.DeliverSupplies</c> (技能槽 17),
    /// 起手 1 秒后挂上 <c>_0023Sq._0023ajc</c>, 枚举是
    /// <c>_0023Oq._0023aD._0023ajc</c> (那个类 <c>_0023Cq()</c> 里
    /// <c>_0023iq = _0023aD._0023ajc</c>)。buff 在, 就是正在搬。
    ///
    /// 不判"补给数变少": 那是 buff 每隔一段时间扣一次的结果, 第一次扣要等
    /// <c>_0023fq / _00237q</c> 秒, 刷怪会晚半拍。
    /// </summary>
    private void NoticeDeliveryStarted(Context ctx, _0023occ current)
    {
        if (_deliveryStartedFired || current != _0023occ._0023Q8b)
        {
            return;
        }

        if (ctx.Player == null || !ctx.Player.HasBuff(_0023Oq._0023aD._0023ajc))
        {
            return;
        }

        _deliveryStartedFired = true;
        OnDeliveryStarted?.Invoke(ctx.Simulation);
    }

    /// <summary>
    /// 剧情那只 Floater 死了没有 —— 由宿主接到
    /// <see cref="TutorialFinaleDirector.TutorialFloaterIsDead"/>。
    ///
    /// 没接的话状态 22 退回"场上没有 Floater 了"这个旧判据, 见那一步的说明。
    /// </summary>
    public Func<bool> StoryFloaterIsDead { get; set; }

    /// <summary>玩家走到卡车时身上背了多少补给。见状态 20/21 那两步。</summary>
    private int _suppliesOnArrival;

    /// <summary>
    /// 打完 Floater 之后还要停多久才收尾。**我们定的** —— 客户端状态 23 没有
    /// 任何可判的目标, 它只是把引导全收起来 (-5cc.cs:539-544), 是收尾前的过渡。
    ///
    /// ## 从 30 秒改到 3 秒 (2026-09-22)
    ///
    /// 玩家原话: "杀完 floater 后应该很快结束, 但是现在会延迟很久才结束。"
    ///
    /// 原来 30 秒是配
    /// <see cref="TutorialFinaleDirector.ExtraWaves"/> 那套额外补怪定的 ——
    /// 每 5 秒一波, 30 秒正好六波, 算是"守住卡车"的一段。但那套补怪已经默认
    /// 关掉了 (原厂事件自己够, 见 ExtraWaves 的说明), 于是这 30 秒变成了
    /// **连怪都没有的干等**。
    ///
    /// 加上收尾那一段, 玩家从打死 Floater 到真正退出要等:
    ///
    ///     30 秒 (这里干等)
    ///   +  6 秒 (进状态 24 之后服务端送客, TutorialFarewellSeconds)
    ///   = 36 秒
    ///
    /// 改成 3 秒之后是 9 秒。送客那 6 秒别动 —— 它是玩家实测校准过的, 而且
    /// 客户端那条"自己退出"的链在我们这边是断的 (拉到 35 秒做过对照实验),
    /// 只能靠服务端踢, 详见 <c>LidgrenHost.TutorialFarewellSeconds</c>。
    ///
    /// 3 秒: 够客户端把引导收干净、玩家看清"打完了", 又不拖沓。原厂这个数
    /// 和状态机那一半一起丢了, 查不到。
    ///
    /// 要回到"守住卡车"那种玩法, 把 <c>ExtraWaves</c> 打开再把这个数调回去 ——
    /// 两个是配套的。
    /// </summary>
    public static float FinaleHoldSeconds { get; set; } = 3f;

    /// <summary>
    /// 这局里有没有见过活着的 Floater。理由同 <see cref="SawHostileNpc"/>。
    /// </summary>
    private bool SawFloater { get; set; }

    /// <summary>
    /// 玩家走到卡车队友身边了没有。
    ///
    /// ## 判据是"**走进卡车这片场地**", 不是"走到某个队友脚下"
    ///
    /// 玩家要求: "把卡车附近队友改成走进卡车场景区域就触发队友汇合"。
    ///
    /// 先前判的是"走到任一卡车队友 30 单位内"。那有两个毛病:
    ///
    ///   1. 两个 NPC_TRUCK 标记在 (-162,3335) 和 (-210,3460), 后者离卡车
    ///      **110 单位** —— 玩家在卡车边根本够不到他
    ///   2. 状态机这边"任一个够到就推进", 而 AI 那边"各自判各自的",
    ///      两套判据不一致, 于是有一个队友永远不入队
    ///
    /// 现在统一成一个区域判据 (<see cref="TutorialNpcAI.IsInsideTruckArea"/>),
    /// 状态机和 AI 读的是**同一个函数**。
    /// </summary>
    private static bool ReachedTruckCrew(Context ctx) =>
        TutorialNpcAI.IsInsideTruckArea(ctx.Player.Position);

    /// <summary>
    /// 这局里有没有见过活着的敌对教程 NPC。
    ///
    /// 和 <c>_sawPullerAlive</c> 同一个道理: 光判"场上没有敌人"的话,
    /// **敌人压根没生成出来**时判据也为真, 遭遇战会被静默跳过。而生成失败正是
    /// 最需要被看见的故障 (TutorialEntitySpawner 认不出类时会打 WRN)。
    /// 卡住是能观察到的, 悄悄推进不是。
    /// </summary>
    private bool SawHostileNpc { get; set; }

    /// <summary>
    /// 场上还有活着的敌对教程 NPC 吗。
    ///
    /// 判据是"教程 NPC + 敌对队伍", 不看具体是哪个类 —— 那个类是我们选的
    /// (见 <c>TutorialEntitySpawner.Known</c> 里 ENEMY 那条), 以后换掉时
    /// 这里不用跟着改。
    /// </summary>
    private static bool AnyHostileTutorialNpcAlive(Context ctx) =>
        AnyAlive(ctx, e => e.IsTutorialNPC && e.TeamId == TutorialEntitySpawner.EnemyTeam);

    /// <summary>
    /// 玩家身上背了多少补给。
    ///
    /// 读法照抄原厂 (<c>Player.cs:879</c>):
    /// <c>GameMode.AsISupplies._00239c()._0023Fj(IndexPlayer)</c> ——
    /// 教程模式把那个 <c>Supplies</c> 对象暴露成了 public 的 <c>SupplyData</c>,
    /// 所以这里直接取, 不用绕接口。
    /// </summary>
    private static int CarriedSupplies(Context ctx)
    {
        if (ctx.Simulation?.GameMode is not _0023bd._00235cc tutorial || ctx.Player == null)
        {
            return 0;
        }

        return tutorial.SupplyData?._0023Fj(ctx.Player.IndexPlayer) ?? 0;
    }

    /// <summary>
    /// 场上有没有补给点满足某个条件。
    ///
    /// 用 <c>GetActiveObjectsFromCategory</c> 而不是遍历实体表 —— StorageRoom 是
    /// GameObjectBase 而不是 Entity, 不在 ActiveEntityList 里 (原厂自己也是这么
    /// 找的, GameMode.TriggerCapturePerformed:964)。
    /// </summary>
    private static bool AnyStorageRoom(Context ctx, Func<StorageRoom, bool> predicate)
    {
        if (ctx.Simulation?.GameMode == null)
        {
            return false;
        }

        foreach (StorageRoom room in ctx.Simulation.GetActiveObjectsFromCategory(ObjCat.StorageRoom))
        {
            if (room != null && room.IsActive && predicate(room))
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>
    /// 杀掉 Puller 之后: 发霰弹枪 + 解锁按 C 切武器。
    ///
    /// 两件事必须一起做。玩家的原版记忆是"打败 Puller 直接获得枪, 然后自己按 C 切换",
    /// 而枪到手却切不动的话, 玩家根本察觉不到自己已经拿到枪了 ——
    /// 真机上就是这样: 枪进了副槽 (槽 1), 技能栏还是船桨的, 唯一的反馈是
    /// 血量上限 +38 (霰弹枪的 Vitality globals), 玩家把那当成了"走过去回血的隐形拾取物"。
    /// </summary>
    private static void GrantShotgun(Context ctx)
    {
        GiveTutorialWeapon(ctx.Player, slot: ShotgunSlot,
                           schematicId: ShotgunSchematicId,
                           equip: ShotgunSlot == 0);

        AllowWeaponSwapInput(ctx.Simulation);
    }

    /// <summary>
    /// 打开教程的"允许按 C 切武器"闸门。
    ///
    /// ## 这是按 C 全程没反应的根因
    ///
    /// 客户端的按键入口 <c>UnityClient.cs:2473</c> 读的是
    /// <c>PCKeybinds.Equipment.SwapWeapon</c>, 转手调
    /// <c>GameManagerClient.SendSwapWeapon</c> (:1493), 而那里第一句就是:
    ///
    ///     if ((!GameMode.IsTutorialGameMode || GameMode.AsTutorialGameMode._0023Kcc)
    ///         &amp;&amp; LocalPlayer != null)
    ///         LocalPlayer.SwapWeapon();
    ///
    /// **教程模式下必须 <c>_0023Kcc</c> 为真**, 否则按键被整条吞掉 ——
    /// 连 <c>_0023cC</c> 标志都不会置上, 更不会走到 <c>_0023oD()</c> 里那个
    /// "目标槽为空就 return" 的守卫。我们从没设过它, 所以按 C 从进游戏起
    /// **一次都没生效过**, 玩家报告的"按 C 没反应""切过去了切不回来"是同一件事。
    ///
    /// ## 它是复制字段, 所以纯服务端就能开
    ///
    /// <c>_00235cc</c> (-bd/-5cc.cs):
    ///
    ///     public bool _0023Kcc;                      // :49  public, 不用改反编译树
    ///     _0023Kcc = false;                          // :113 ResetGameMode 时清掉
    ///     _00237d.Write(_0023Kcc);                   // :737 SerializeGameMode
    ///     _0023Kcc = _00237d._0023Fae();             // :743 DeserializeGameMode
    ///
    /// 而 <c>SerializeGameMode</c> 是 <c>GameMode.Serialize</c> 的一部分
    /// (GameMode.cs:1544) —— 也就是**世界状态同步**, 每次同步都带上。
    /// 所以这里赋值就够了, 不需要额外发消息。
    ///
    /// ## 为什么在发枪这一刻开, 而不是状态 9
    ///
    /// 客户端到状态 9 才显示 SWITCH_WEAPON 引导, 所以原厂**也可能**是在状态 9 开的。
    /// 没有证据能区分, 而在发枪时开更宽松: 玩家拿到枪就能试着按 C,
    /// 到了状态 9 提示出来时功能已经在了。反过来 (状态 9 才开) 会让
    /// "拿到枪但按 C 没反应"那段体验重现, 那正是这次要修的问题。
    /// </summary>
    /// <returns>闸门是否成功打开 (拿不到教程模式对象时 false)。</returns>
    public static bool AllowWeaponSwapInput(GameManager simulation)
    {
        if (simulation?.GameMode is not _0023bd._00235cc tutorial)
        {
            return false;
        }

        tutorial._0023Kcc = true;
        OnWeaponSwapUnlocked?.Invoke();
        return true;
    }

    /// <summary>闸门打开时的回调, 供宿主打日志。</summary>
    public static Action OnWeaponSwapUnlocked { get; set; }

    /// <summary>
    /// 服务端要不要在状态 9 主动帮玩家切到霰弹枪。
    ///
    /// **默认关**, 因为那是个 workaround: 按 C 的闸门 (<see cref="AllowWeaponSwapInput"/>)
    /// 修好之后, 切武器就该由玩家自己做 —— 原版就是
    /// "打败 Puller 获得枪, 走到发电机, 按 C 切枪, 打爆它"。
    ///
    /// 自动切的时候玩家反馈"按 C 切不回船桨", 因为闸门锁着,
    /// 服务端切过去就再也切不回来 —— 比不切更糟。
    ///
    /// 打开它只有一个用途: 万一按 C 还是不通, 这是让流程能继续走完的兜底,
    /// 同时 <see cref="WeaponSwapper"/> 那个观测点还能告诉我们客户端认不认槽 1。
    /// </summary>
    public static bool AutoSwapToShotgun { get; set; }

    /// <summary>
    /// 切到霰弹枪。<see cref="OnWeaponSwapped"/> 会把结果报给宿主打日志。
    /// </summary>
    private static void SwapToShotgun(Context ctx)
    {
        if (!AutoSwapToShotgun)
        {
            return;
        }

        bool swapped = WeaponSwapper.SwapTo(ctx.Simulation, ctx.Player, ShotgunSlot);
        OnWeaponSwapped?.Invoke(ShotgunSlot, swapped);
    }

    /// <summary>
    /// 服务端主动切武器之后的回调, 供宿主打日志。参数: (目标槽, 是否成功生成)。
    /// </summary>
    public static Action<int, bool> OnWeaponSwapped { get; set; }

    /// <summary>
    /// 发电机爆了 -> 电栅栏断电消失。
    ///
    /// ## 依据是类型名和地理位置
    ///
    /// 那七段的类型就叫 <c>TutorialElectricBarricade</c> —— **Electric**,
    /// 而且它们 GroupID 全是 3003, 围在发电机 (1378.5, 3282.2) 周围 13.8 ~ 73.3
    /// 单位处。发电机的悬浮名是 <c>PCStrings.GUI.Hover.LightingGenerator</c>。
    ///
    /// 一圈"通电栅栏"围着一台"发电机", 而教程要玩家用枪打爆发电机 ——
    /// 打爆之后栅栏断电、玩家通过, 是这组数据唯一说得通的读法。
    ///
    /// ## 原厂怎么做的看不到
    ///
    /// 消费 <c>Spawn_Destructible.GroupID</c> 的那段是服务端代码 ——
    /// 路障组类 <c>_00235xc</c> 在客户端树里**零调用者** (连构造函数都没人调)。
    /// 所以"断电 = 销毁"是**我们的做法**; 原版也可能是播个断电特效然后禁用碰撞。
    ///
    /// 销毁最简单, 而且效果对玩家是一样的: 路通了。
    /// </summary>
    private static void CutPowerToElectricBarricades(GameManager simulation)
    {
        DestructibleSpawner.DestroyAllOfType(simulation, BarricadeType.TutorialElectricBarricade);
    }

    /// <summary>
    /// 玩家走到发电机跟前了吗。
    ///
    /// 位置取**地图标记**而不是实体 —— 客户端的引导箭头指的就是
    /// <c>mapObject.Position</c> (-bd/-5cc.cs:373), 所以判据要和它同源。
    /// 拿实体位置的话, 万一生成偏了, 箭头和判定就会指向两个地方。
    /// </summary>
    private static bool ReachedGenerator(Context ctx)
    {
        if (ctx.Simulation?.MapObjects == null || ctx.Player == null)
        {
            return false;
        }

        foreach (MapObject mapObject in ctx.Simulation.MapObjects)
        {
            if (mapObject is Trigger_TutorialLightingGenerator generator
                && Vector2.Distance(ctx.Player.Position, generator.Position)
                   <= GeneratorApproachRadius)
            {
                return true;
            }
        }

        return false;
    }

    // ────────────────────────────────────────────────────────────────
    // 判定辅助
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 出生点, 第一次判定时记下。
    ///
    /// **实例字段而不是 static** —— 一个进程会同时跑多局, static 会让它们
    /// 互相覆盖出生点, 表现是某一局的"移动引导"莫名其妙立刻通过。
    /// </summary>
    private Vector2 _startPosition;
    private bool _startCaptured;

    /// <summary>玩家离开出生点多远。</summary>
    private float MovedFromStart(Context ctx)
    {
        if (!_startCaptured)
        {
            _startPosition = ctx.Player.Position;
            _startCaptured = true;
        }

        return Vector2.Distance(ctx.Player.Position, _startPosition);
    }

    /// <summary>
    /// 船桨拾取物的拾取半径。
    ///
    /// 取自 <c>_0023e0b._0023VW</c> 里的 <c>_0023Vb = 4f</c> —— 拾取物基类
    /// <c>_00235W.UpdateGameObject</c> (-6W/-5W.cs:50) 就是拿这个半径做碰撞
    /// 查询来判断"够近可以拿了"。
    ///
    /// ⚠️ 和 <see cref="ArrivalRadius"/>(30) 是两码事, 我先前混用了:
    ///   30 = 引导箭头"不用再指了"的距离
    ///    4 = 拾取物"够近可以拿了"的距离
    ///
    /// 用 30 的后果是玩家还在 100 多单位外就"拾取"了船桨 —— 状态推进了,
    /// 但玩家眼里什么都没发生。真机日志里那两步只隔 0.5 秒就是这个。
    /// </summary>
    public const float PaddlePickupRadius = 4f;

    /// <summary>
    /// 走到木栅栏"跟前"的半径。
    ///
    /// 100 是我们定的。三段栅栏横排 (x≈1130, y 3681~3724), 判的是"进了这片区域"
    /// 而不是"踩到某一段脚下", 所以比 <see cref="ArrivalRadius"/>(30) 大。
    /// 同时它远小于 PREBARRICADE 到栅栏的 366 —— 玩家必须真的走过来才触发。
    /// </summary>
    public const float BarricadeApproachRadius = 100f;

    /// <summary>
    /// 走到发电机算"到了"的距离。
    ///
    /// ## 为什么不能用 <see cref="ArrivalRadius"/>(30)
    ///
    /// 发电机 (1378.5, 3282.2) 被**七段 `TutorialElectricBarricade` 围着**
    /// (GroupID 3003, 距发电机 13.8 ~ 73.3)。玩家从东边过来会被最外层挡住,
    /// 站在 x≈1460 附近, 到发电机 80+ 单位 —— **30 永远够不到, 状态 8 死锁**。
    ///
    /// 玩家实测正是如此: 箭头指着发电机, 走过去但状态不动, 引导文字也不出来
    /// (那两条在状态 9)。
    ///
    /// ## 100 的依据
    ///
    /// 状态 9 的语音是 <c>TutorialRangedObjective01</c> —— **Ranged**, 远程目标。
    /// 也就是说这一步本来就是"隔着栅栏用枪打", 玩家**不需要**走到发电机跟前。
    ///
    /// 取 100 覆盖"玩家站在最外层电栅栏外侧"这个位置 (最远那段 73.3 + 余量)。
    ///
    /// **这是我们的取值** —— 原厂的转换条件没留下痕迹, 但"够不到就死锁"是硬约束。
    /// </summary>
    public const float GeneratorApproachRadius = 100f;

    /// <summary>
    /// 霰弹枪发到哪个武器槽。**0 = 直接拿在手上, 1 = 副武器 (要按键切)**。
    ///
    /// ## 原厂是 1, 我们暂时用 0
    ///
    /// 原厂 <c>RespawnCharacter:1873</c> 是 `EquipHaxWeaponNew(1, 1009, equip: false)`
    /// —— 进副武器槽, 玩家自己按键切。教程状态 9 还专门有
    /// <c>TutorialGuideText.SWITCH_WEAPON</c> 引导教这件事。
    ///
    /// 但真机上**切不过去**, 玩家反馈按键无反应。已查清的链条:
    ///
    ///   1. 客户端按键 -> <c>Player.SwapWeapon()</c> (:674) 只置标志 `_0023cC`
    ///   2. <c>Player._0023oD()</c> (:1452) 每帧消费它, 而里面有教程守卫:
    ///
    ///          Item weapon = GetWeapon(num);
    ///          if (IsTutorialGameMode &amp;&amp; weapon == null) return;   // ← 目标槽空就不切
    ///          EquipWeapon(num);
    ///
    ///   3. 服务端跟随: 客户端每帧在控制器数据里上报 `_00230z`(= `weapon1Equipped`,
    ///      构造参数名是明文的), 服务端 <c>Character.UpdateSwapWeapon</c> (:3944)
    ///      据此调 <c>SwapWeapon()</c>。**所以客户端是权威**, 服务端强行装备槽 1
    ///      会被下一帧的上报拉回去。
    ///
    /// 服务端能看到的观测点是每秒诊断行里的 `武器槽=` —— 玩家按了键而它一直是 0,
    /// 说明**客户端从没上报过切换**, 也就是第 2 步的 `_0023cC` 压根没被置上。
    /// 剩下两种可能 (按键不是 C / 客户端 `GetWeapon(1)` 仍是 null) 都只能在客户端观测,
    /// 而那条路本项目已经证明是死路。
    ///
    /// ## 所以先让玩家能玩
    ///
    /// 发到槽 0 走的是**船桨那条已经验证能用的路**: 客户端的
    /// <c>EquipWeaponsAndAbilities(forceSetSlot: true)</c> 会
    /// `EquipWeapon(_EquippedWeaponSlot)`, 而那个值已经是 0 —— 槽 0 换成霰弹枪,
    /// 技能栏就变成 ShotgunPrimary/Secondary, 枪直接在手上。
    ///
    /// **代价**: 船桨被顶掉, 玩家手上只有枪。原版是两把都在、可切换。
    /// 教程后面的目标 (打爆发电机) 要枪, 而船桨的用处 (破栅栏) 那时已经过了,
    /// 所以流程能走通。
    ///
    /// ## 2026-07-30: 改回原厂的 1
    ///
    /// 槽 0 那版确实让玩家拿到了枪, 但代价是**船桨被顶掉**, 而且带来一个
    /// "走过去加血又减血"的闪烁 —— 那是 `SetWeaponAndEquipGlobals(0, 霰弹枪, ...)`
    /// 先撤掉船桨的 globals (−38) 再应用霰弹枪的 (+60), 同一帧内两次属性变更。
    /// 玩家先前在船桨那一步也见过同样的现象 (那次是我多调了
    /// `EquipWeaponsAndAbilities`)。
    ///
    /// 发到槽 1 就没有这个问题: 船桨的 globals 不动, 只加霰弹枪的。
    ///
    /// 切换由 <see cref="WeaponSwapper"/> 在状态 9 (教切武器那步) 服务端主动做一次
    /// —— 走的是原厂的 `SwapWeapon` 实体, 两端各跑一次 `EquipWeapon`。
    /// 那同时也是个观测点, 见 WeaponSwapper 的说明。
    /// </summary>
    public static int ShotgunSlot { get; set; } = 1;

    /// <summary>
    /// 玩家有没有走到船桨拾取物的拾取半径内。
    ///
    /// 判的是**场上那个拾取物实体**而不是地图标记点 —— 拾取物由
    /// <see cref="TutorialEntitySpawner"/> 生成, 位置在它自己的
    /// <c>_0023B</c> 字段里 (拾取物基类不用 <c>Position</c>)。
    ///
    /// 拾取物不存在时返回 false, 状态机停住 —— 那说明生成那步失败了,
    /// 卡住比悄悄推进好。
    /// </summary>
    private static bool PickedUpPaddle(Context ctx)
    {
        List<GameObjectBase> objects = ctx.Simulation.ActiveGameObjectList;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is _0023aX._0023e0b { IsActive: true } paddle
                && Vector2.Distance(ctx.Player.Position, paddle._0023B) <= PaddlePickupRadius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 补给点 (lifeguard station) 的坐标 —— 路径点的"下一站"。
    ///
    /// 硬编码而不是去场上查 <c>StorageRoom</c>: 判据要在实体还没配对好的时候
    /// 也能用, 而且这个值本来就是地图里写死的
    /// (<c>Event_TriggerPosition</c> ID 10038)。
    /// </summary>
    public static readonly Vector2 SupplyPointHint = new Vector2(505.6f, 3611.8f);

    /// <summary>路过路径点的判定半径。比 <see cref="ArrivalRadius"/> 宽松。</summary>
    public const float WaypointRadius = 60f;

    /// <summary>
    /// 走到路径点, **或者已经越过它**。
    ///
    /// ## 为什么不能只判"到达"
    ///
    /// 教程状态 12/13 的箭头目标是客户端硬编码的两个坐标, 而它们是**路径点**
    /// 不是目的地 —— 其中 (1077, 3792) 玩家实测落在不可行走的区域里。
    /// 只判到达的话玩家永远推不动那一步, 而状态不推进客户端箭头就一直钉在
    /// 那个够不到的地方: "走到了 supply point 了箭头还指着那里"。
    ///
    /// 越过的判据是"玩家离下一站比这个路径点离下一站更近" —— 纯几何,
    /// 不需要知道地图的可行走区域 (那份数据我们没有)。
    /// </summary>
    private static bool PassedWaypoint(Context ctx, Vector2 waypoint, Vector2 next)
    {
        if (Vector2.Distance(ctx.Player.Position, waypoint) <= WaypointRadius)
        {
            return true;
        }

        return Vector2.Distance(ctx.Player.Position, next)
               < Vector2.Distance(waypoint, next);
    }

    private static bool Reached(Context ctx, Vector2 target) =>
        Vector2.Distance(ctx.Player.Position, target) <= ArrivalRadius;

    /// <summary>走到某个 <see cref="Spawn_Tutorial"/> 标记点。</summary>
    private static bool ReachedTutorialMarker(Context ctx, TutorialType type) =>
        NearTutorialMarker(ctx, type, ArrivalRadius);

    /// <summary>走到某个 <see cref="Spawn_Tutorial"/> 标记点附近 —— 半径自定。</summary>
    private static bool NearTutorialMarker(Context ctx, TutorialType type, float radius)
    {
        foreach (MapObject mapObject in ctx.Simulation.MapObjects)
        {
            if (mapObject is Spawn_Tutorial marker && marker.TutorialType == type
                && Vector2.Distance(ctx.Player.Position, marker.Position) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 遭遇战那片空地 —— 地图 <c>ENEMY</c> 标记的位置 (实测 dump)。
    ///
    /// 硬编码只用在"已经越过"那条兜底判据上 (<see cref="PassedWaypoint"/> 要一个
    /// 坐标)。正经判据走的是 <see cref="NearTutorialMarker"/>, 读的是地图本体。
    /// </summary>
    public static readonly Vector2 AmbushPoint = new Vector2(60.7f, 3533.7f);

    /// <summary>
    /// 走到遭遇战那片空地算"到了"的半径。
    ///
    /// **我们定的**, 和 <see cref="BarricadeApproachRadius"/> 同一个思路: 判的是
    /// "进了这片区域"而不是"踩到标记点脚下"。取 120 —— 敌人就站在标记点上,
    /// 玩家从补给点走向卡车的路线会从旁边经过, 太小的话会擦肩而过不触发,
    /// 太大又会在玩家还看不见他们时就播 AMBUSH 语音。
    /// </summary>
    public const float AmbushApproachRadius = 120f;

    /// <summary>
    /// 走到某类路障附近 —— 半径自定, 判的是"进了这片区域"。
    ///
    /// 和 <see cref="ReachedDestructible"/> 的区别只有半径可调: 那个用
    /// <see cref="ArrivalRadius"/>(30), 适合"踩到这一段脚下"; 这个给
    /// 横排的多段路障用。
    /// </summary>
    private static bool NearDestructible(Context ctx, BarricadeType type, float radius)
    {
        foreach (MapObject mapObject in ctx.Simulation.MapObjects)
        {
            if (mapObject is Spawn_Destructible spawn && spawn.BarricadeType == type
                && Vector2.Distance(ctx.Player.Position, spawn.Position) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>这局里见过哪些类型的路障活着。见 <see cref="BarricadeCleared"/>。</summary>
    private readonly HashSet<BarricadeType> _sawBarricade = new HashSet<BarricadeType>();

    /// <summary>
    /// 这一类路障已经被打光了吗 —— 用来让"走到 X 跟前"那种步骤在玩家
    /// **已经把 X 打掉**时也能过去。
    ///
    /// 带"见过它活着"的前提: 路障还没生成时 <c>CountAlive</c> 同样是 0,
    /// 没这个前提的话整段会被静默跳过。这和 <c>_sawPullerAlive</c>、
    /// <see cref="SawFloater"/> 是同一个模式 —— "场上没有"必须先排除
    /// "从来就没有过"。
    /// </summary>
    private bool BarricadeCleared(Context ctx, BarricadeType type)
    {
        if (DestructibleSpawner.CountAlive(ctx.Simulation, type) > 0)
        {
            _sawBarricade.Add(type);
            return false;
        }

        return _sawBarricade.Contains(type);
    }

    private static bool ReachedDestructible(Context ctx, BarricadeType type)
    {
        foreach (MapObject mapObject in ctx.Simulation.MapObjects)
        {
            if (mapObject is Spawn_Destructible spawn && spawn.BarricadeType == type
                && Reached(ctx, spawn.Position))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 状态 6 的箭头目标 —— 客户端硬编码 (-bd/-5cc.cs:347)。
    /// 它是个**观察点**, 不是目标本身: Puller 在它 49 单位外, Amber 在 80 外。
    /// </summary>
    public static readonly Vector2 RallyPoint = new Vector2(1588f, 3072f);

    /// <summary>
    /// 被 Puller 拉着的那个教程 NPC (Amber) 的类名。
    /// 依据见 <see cref="TutorialEntitySpawner"/> —— 状态机自己点了名
    /// (-5cc.cs:182 <c>is _0023s3b &amp;&amp; HasBuff(PullerHook)</c>)。
    /// </summary>
    private const string AmberTypeName = "_0023s3b";

    /// <summary>
    /// 玩家到没到"营救现场"这块区域。
    ///
    /// 判的是**场上的 Puller 和被钩住的教程 NPC 本体**, 不是地图标记点 ——
    /// Puller 是活的, 它会朝玩家挪动 (PullerCrawl), 位置和标记点会分开。
    ///
    /// 半径用 <see cref="ArrivalRadius"/>(30)。客户端在状态 7 把箭头挂到 Puller
    /// 身上时用的消失半径是 20 (-5cc.cs:361 <c>_0023Ncc = 20f</c>) —— 比这个小,
    /// 也就是说玩家走到能让服务端推进的距离时, 客户端箭头还没消失, 不会出现
    /// "箭头还指着但状态已经过了"的错位。
    /// </summary>
    /// <summary>
    /// Puller 曾经在场, 而现在场上没有活的了 —— 也就是**玩家把它杀了**。
    ///
    /// 两个条件缺一不可, 理由见 <see cref="_sawPullerAlive"/>。
    /// </summary>
    private bool PullerWasKilled(Context ctx) =>
        _sawPullerAlive && !AnyAlive(ctx, e => e.IsPuller);

    private static bool NearRescueScene(Context ctx)
    {
        foreach (Entity entity in ctx.Simulation.ActiveEntityList)
        {
            if (entity == null || !entity.IsActive || entity.IsDead)
            {
                continue;
            }

            // 按**类型名**认 Amber, 和 TutorialEntitySpawner.Known 的做法一致
            // (那里也是 "_0023s3b" 字符串) —— 教程 NPC 那六个类在混淆命名空间里,
            // 直接写类型引用要猜命名空间, 而类型名随程序集固定。
            bool isRescueTarget = entity.IsPuller
                                  || entity.GetType().Name == AmberTypeName;

            if (isRescueTarget && Reached(ctx, entity.Position))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyAlive(Context ctx, Func<Entity, bool> predicate)
    {
        foreach (Entity entity in ctx.Simulation.ActiveEntityList)
        {
            if (entity != null && entity.IsActive && !entity.IsDead && predicate(entity))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 发教程武器。
    ///
    /// <c>EquipHaxWeaponNew</c> (Player.cs:2590) 是原厂方法, 自带
    /// <c>if (!IsServer) return;</c> 守卫 —— 说明它本来就是服务端调的。
    ///
    /// 武器 ID 1005/1009 取自 <c>RespawnCharacter</c> (:1869/:1873) 里按教程
    /// 进度补发的那两行, 不是我们挑的。
    /// </summary>
    /// <summary>
    /// 要不要真的把教程武器装到手上。
    ///
    /// ## 默认关掉, 而且这是经过论证的
    ///
    /// 装备武器会把 <c>FistsPrimary</c> 从技能栏槽 0 上撤下来 (换成
    /// <c>HeavyPrimary</c>)。而**客户端不知道装备变了**, 它仍然按 FistsPrimary
    /// 的缓存索引请求攻击:
    ///
    ///     Character._00232z (:3976)  服务端处理客户端的技能按下
    ///       Ability a = AbilityBar.GetCachedAbility(controllerData._0023Pkd);
    ///       if (a.SlotIndex >= 0) TryUseAbility(a.SlotIndex, ...);
    ///       else MarkAbilityAsInvalid(...);          // ← 静默作废
    ///
    /// FistsPrimary 被撤下之后 <c>SlotIndex</c> 是 -1, 于是走 else ——
    /// **攻击静默作废, 连 DebugInterruptLogs 都不会打** (那条日志在
    /// TryUseAbility 内部, 而我们根本没走进去)。
    ///
    /// 真机症状精确对应: 伤害次数 0, 而 TryUseAbility failed 一条都没有。
    /// 这一对矛盾的证据是定位的关键。
    ///
    /// ## 为什么服务端绕不过去
    ///
    /// 客户端要更新技能栏, 就得先有武器物品。而教程图上它拿不到:
    ///
    ///   Client.OnGameManagerCreated (:572)
    ///     if (!IsObserver &amp;&amp; gameInfo.MapIndex != MapManagerData.TutorialMapIndex)
    ///         LocalPlayer.Weapon1 = GetWeapon(ClientInfoData._0023lYb, ...);
    ///
    /// <c>TutorialMapIndex = 4</c> (MapManagerData.cs:25), 正是教程图 ——
    /// 那个 if 在教程里**必然跳过**。ClientInfoData 发过去也没人读。
    ///
    /// <c>ReceivedTutorialPaddle</c> 确实同步到客户端了, 但整个客户端树里只有
    /// GUI_AbilityIcon.cs:839 (武器图标 tooltip) 和 Player.cs:752 (本地玩家要不要
    /// 自动选中槽 0) 用它。**它不是发武器的开关** —— 我先前当成开关是错的。
    ///
    /// ## 2026-07-30 修正: 判据是教程状态, 而且这段代码没被裁
    ///
    /// <c>Player.RespawnCharacter</c> (:1865-1875) 明确写了原厂的发放规则:
    ///
    ///     if (GameMode.IsTutorialGameMode)
    ///     {
    ///         if (状态 &gt;= _00230bc)  EquipHaxWeaponNew(0, 1005);               // 船桨
    ///         if (状态 &gt;= _0023hcc)  EquipHaxWeaponNew(1, 1009, equip: false);  // 霰弹枪
    ///     }
    ///
    /// 判据是**教程状态**, 不是那两个 bool。<c>_0023hcc</c> 正是发电机那一步 ——
    /// 也就是解救 Amber 之后, 和玩家记的"她会给角色发枪"对得上。
    ///
    /// ID 对照 (从 -Mk/-E3.cs:160 和 -Mk/-YO.cs:107 交叉确认):
    /// **1005 船桨 / 1006 通用近战 / 1009 霰弹枪**。
    ///
    /// ## 缓存表错位假设: 已实测否定
    ///
    /// 我原以为卡口是"服务端装备武器往技能栏缓存表里加了客户端没有的条目,
    /// 于是两边下标错位"。<see cref="TutorialDirectorTests"/> 里量过, 也在真机
    /// 日志里量过: **72 -> 72 / 73 -> 73, 一条都没多**。
    ///
    /// 原因是 <c>CacheGame</c> 开局就把该角色能用的技能全缓存了, 包括武器技能。
    /// 装备武器只是把**已缓存**的技能换到槽位上。两边下标一致。
    ///
    /// ## 真正的卡口: 武器技能的槽位是**客户端权威**的
    ///
    /// <c>AbilityBar.Deserialize</c> (:906-932) 明确拒绝服务端对武器技能槽的赋值:
    ///
    ///     for (int i = 0; i &lt; _00237gc.Count; i++)
    ///         if (!_00237gc[i].IsWeaponAbility)
    ///             _00237gc[i].SlotIndex = -1;          // 只清非武器技能
    ///
    ///     Ability ability2 = _00236gc[j];              // 客户端当前槽 j
    ///     if (ability2 == null || !ability2.IsWeaponAbility)   // ← 是武器技能就整段跳过
    ///     {
    ///         _00236gc[j] = ability;
    ///         ability.SlotIndex = j;
    ///     }
    ///
    /// 而 <c>FistsPrimary</c> (-kL/-lOb.cs:27) 和 <c>HeavyPrimary</c>
    /// (-kL/-7Nb.cs:27) **都是 <c>IsWeaponAbility = true</c>**。
    ///
    /// 于是真机上发生的是 (2026-07-30 日志逐条对上):
    ///
    ///   1. 服务端槽 0: FistsPrimary#56 -> HeavyPrimary#55, 且 FistsPrimary.SlotIndex = -1
    ///   2. 服务端把"槽 0 = 缓存表[55]"发过去
    ///   3. 客户端槽 0 当前是 FistsPrimary, IsWeaponAbility 为真 -> **整段跳过**,
    ///      客户端槽 0 保持 FistsPrimary
    ///   4. 玩家按攻击 -> 客户端发 FistsPrimary 的下标 56
    ///   5. 服务端 Character._00232z (:3976): GetCachedAbility(56) = FistsPrimary,
    ///      但它的 SlotIndex 已是 -1 -> MarkAbilityAsInvalid -> **静默作废, 零日志**
    ///
    /// 玩家的描述精确对应: "靠近船桨后还是空手, 然后再攻击敌人就不掉血了,
    /// 没靠近之前还能正常造成伤害"。
    ///
    /// ## 所以关掉, 而且这次结论有硬证据
    ///
    /// 服务端**没有**任何办法让客户端把 HeavyPrimary 放进槽 0 —— 那是设计上的
    /// 客户端权威。客户端要自己放, 就得先有武器物品; 而教程图上它拿不到
    /// (Client.cs:572 把教程图排除在 ClientInfoData 的武器读取之外)。
    ///
    /// 出路是客户端插件: 在客户端上让 Player 走
    /// <c>AddWeapon(0, 1005, GetLevel(), ..., onClient: true)</c>
    /// (Character.cs:1597; 教程 NPC 走的正是这条, -Mk/-YO.cs:107)。
    /// <c>AddWeapon</c> 是 <c>protected</c>, 插件用 Harmony 或反射调。
    ///
    /// ## 2026-07-30: `Respawn()` 那条路也试过了, 不通
    ///
    /// <c>Deserialize</c> 的条件 <c>ability2 == null || !ability2.IsWeaponAbility</c>
    /// 有一个 **null 分支** —— 只要客户端槽 0 是空的, 服务端的赋值就能落地。
    /// 而客户端清空槽位只发生在 spawn 流程里 (<c>EquipWeaponsAndAbilities</c> 开头
    /// 的 <c>ClearWeapons()</c>, Player.cs:579/584 服务端和客户端都调)。
    ///
    /// 所以我试了在发武器前调 <c>player.Respawn()</c>。**实测不通**:
    ///
    ///   - 真机日志里玩家**没有销毁通知**。销毁消息只对走过 <c>Destroy()</c> 的对象
    ///     发 (GameManager.cs:1620-1631 只遍历 <c>DestroyedGameObjectList</c>)。
    ///   - <c>Respawn()</c> 不销毁对象, 它 <c>_00235Ce(index, false)</c> 然后
    ///     <c>Spawn()</c> 又设回 true, **同一帧内**净变化为零, 客户端毫不知情。
    ///   - 唯一的可见效果是噪声: 玩家看到"播了一个升级特效但没升级"
    ///     (Spawn 流程重设 BaseStats / SetAccountLevelData)。
    ///
    /// ## 现在的判断
    ///
    /// 要让客户端重跑 spawn 流程, 只剩真的 <c>Destroy()</c> + 重新生成这一条 ——
    /// 那是大锤: 客户端会丢掉相机/输入目标, 还要重新 <c>SetControlledCharacter</c>,
    /// 副作用面积远大于收益。
    ///
    /// ## 2026-07-30 定案: 我一直发错了通道, 服务端完全够用
    ///
    /// 上面那些"绕不过去"的分析全都建立在一个错误前提上: 我以为客户端在教程图上
    /// 拿不到武器物品。**错的。**
    ///
    /// <c>GameManagerClient.cs:569-587</c> 处理 <c>GameMessageType.ClientInfo</c>:
    ///
    ///     case GameMessageType.ClientInfo:
    ///         client2.ClientInfoServerToClientDeserialize(buffer);
    ///         Player localPlayer = client2.GetLocalPlayer();
    ///         localPlayer.Weapon1 = GetWeapon(client2.ClientInfoData._0023lYb, localPlayer);
    ///         localPlayer.Weapon2 = GetWeapon(client2.ClientInfoData._0023mYb, localPlayer);
    ///         localPlayer.Gadget1 = GetTrinket(...);
    ///         localPlayer.Gadget2 = GetTrinket(...);
    ///         localPlayer.EquipWeaponsAndAbilities(forceSetSlot: true);
    ///
    /// 两个关键点:
    ///   - 它 <c>GetWeapon(...)</c> **构造武器物品** —— 客户端 `_Weapons[slot]`
    ///     唯一的来源 (`Character.SetWeaponAndEquipGlobals`, :1623)。
    ///   - <c>forceSetSlot: true</c> 绕过 <c>Player.cs:752</c> 那个教程判断。
    ///     那个参数在整个树里**只有这一处**传 true —— 它就是为这里存在的。
    ///
    /// **这条路径没有教程地图排除。** 我先前引用的 <c>Client.cs:572</c>
    /// (<c>MapIndex != TutorialMapIndex</c>) 在 <c>Client.OnGameManagerCreated</c> 里,
    /// 那是**初始化**路径 —— 开局玩家本来没武器, 跳过是对的。运行时这条消息的
    /// 处理顺序刚好: 先 OnGameManagerCreated (教程跳过, 无害), 再 :581-585
    /// 重新设武器并强制装备。
    ///
    /// 而我的实现发的是 <c>_0023XCe</c>(ClientInfo 批) 通道, 接收方
    /// <c>GameClient._0023KCe</c> 只更新字段、不碰武器 —— 但日志打的是
    /// "ClientInfo 已下发 1 份", 看着像成功。**一条误导性的日志让我在这上面
    /// 绕了三轮**, 期间还两次得出"必须写客户端插件"的错误结论。
    ///
    /// 修法见 <see cref="ClientInfoSender.SendEquipUpdate"/>。
    /// </summary>
    public static bool GrantWeapons { get; set; } = true;

    private static void GiveTutorialWeapon(Player player, int slot, int schematicId, bool equip = true)
    {
        if (player == null)
        {
            return;
        }

        OnAbilityBarSnapshot?.Invoke("装备前", SnapshotAbilityBar(player));

        if (!GrantWeapons)
        {
            // 标志还是要设 —— 它同步到客户端, GUI 靠它决定武器图标的显示,
            // 而且复活时 EquipWeaponsAndAbilities 会读它。设了不会有副作用:
            // 没有武器物品时那条路什么都不做。
            MarkWeaponReceived(player, schematicId);

            OnWeaponGranted?.Invoke(slot, schematicId, false);
            return;
        }

        // 教程武器的两个标志。它们**复制到客户端** (Player.cs:1711/:1776),
        // 而且复活时 EquipWeaponsAndAbilities (:752) 会读它们决定要不要选中
        // 槽位 —— 不设的话玩家一死就变回空手。
        //
        // 必须在发武器**之前**设: EquipHaxWeaponNew 内部会走到
        // EquipWeaponsAndAbilities 那条判断上。
        MarkWeaponReceived(player, schematicId);

        // ## 装备走原厂方法, 别在前后加动作
        //
        // <c>EquipHaxWeaponNew</c> (Player.cs:2590) 自带 `if (!IsServer) return;`,
        // 是服务端方法。它做三件事: 造 Item、写 `ClientInfoData._0023lYb/_0023mYb`
        // 并置脏、`if (equip) EquipWeapon(slot)`。
        //
        // ⚠️ **不要**在这之后调 EquipWeaponsAndAbilities() —— 它开头两句
        // (`_EquippedWeapon.Remove()` + `ClearWeapons()`) 会把刚装好的拆掉。
        // 也**不要**调 Respawn() —— 试过, 客户端收不到任何信号 (无销毁通知),
        // 只带来一个假的升级特效。
        //
        // 客户端真正装备武器靠的是 `GameMessageType.ClientInfo` 消息, 由宿主在
        // OnWeaponGranted 回调里发 (ClientInfoSender.BroadcastEquipUpdate)。
        player.EquipHaxWeaponNew(slot, schematicId, equip);

        // 发完立刻回读 —— 发武器是静默的, 失败了 (图鉴里没这个 ID、
        // ClientInfoData 为空被吞掉) 玩家只会觉得"手上还是空的", 服务端
        // 一行日志都没有。这里把结果变成可观测的。
        // 判据用**已装备的槽位**而不是"背包里有没有" —— 上一版只查了
        // Item.ItemID, 结果武器确实在背包里 (日志报"已到手"), 但
        // _EquippedWeaponSlot 还是 -1, 玩家仍然是空手。
        //
        // 现在两个都查: 物品在 + 槽位选上了, 才算真的到手。
        // ## 回读判据要分 equip 真假两种情况
        //
        // 先前无条件要求 `EquippedWeaponSlot == slot`。对槽 0 (船桨, equip: true)
        // 没问题, 但**霰弹枪是 `equip: false`** —— 那是照原厂
        // RespawnCharacter:1873 抄的 (副武器给了但不切到手上, 玩家自己按键换)。
        //
        // 于是槽 1 永远判定失败, 宿主走 else 分支, **ClientInfo 一次都没发** ——
        // 而那是客户端构造武器物品的唯一途径。
        //
        // 真机日志: `教程发武器**失败**: 槽位 1 图纸 1009 —— 玩家手上仍然是空的`。
        // 玩家的描述是"该捡到 shotgun 的地方没有枪模型, 走近回血" ——
        // 回血就是 SetWeaponAndEquipGlobals 应用了霰弹枪的 Vitality, 说明服务端
        // 那半边其实成了, 只差下发。
        //
        // 判据改成: equip 为真才要求槽位被选中; 否则只要物品进了槽就算成功。
        ConductorGameLogic.Crafting.Item weapon = player.GetWeapon(slot);
        bool inBag = weapon != null && weapon.ItemID != 0;
        bool landed = inBag && (!equip || player.EquippedWeaponSlot == slot);

        OnWeaponGranted?.Invoke(slot, schematicId, landed);
        OnAbilityBarSnapshot?.Invoke("装备后", SnapshotAbilityBar(player));

        // 拿到手了, 地上那把就该消失。判据用**武器身份**而不是槽位 ——
        // 霰弹枪现在也走槽 0 (见 ShotgunSlot), 按槽位判会在发枪时误删船桨拾取物
        // (那时它已经没了, 所以无害, 但判据本身是错的)。
        if (schematicId == PaddleSchematicId)
        {
            DestroyPaddlePickups(player.GameManager);
        }
    }

    /// <summary>船桨的图纸 ID。取自 <c>RespawnCharacter:1869</c>。</summary>
    public const int PaddleSchematicId = 1005;

    /// <summary>霰弹枪的图纸 ID。取自 <c>RespawnCharacter:1873</c>。</summary>
    public const int ShotgunSchematicId = 1009;

    /// <summary>
    /// 设"收到了教程武器"的标志。
    ///
    /// ## 判据是**武器身份**, 不是槽位
    ///
    /// 先前写的是 `if (slot == 0) 船桨标志 else 霰弹枪标志`。那在原厂布局下碰巧
    /// 对得上 (船桨槽 0 / 霰弹枪槽 1), 但 <see cref="ShotgunSlot"/> 改成 0 之后
    /// 就错了 —— 发霰弹枪时设的成了船桨标志, `ReceivedTutorialShotgun` 永远是 false。
    ///
    /// 那两个 bool 的语义是"玩家收到了哪把武器" (客户端拿它决定 GUI 武器图标的
    /// tooltip, GUI_AbilityIcon:839), 和放在哪个槽无关。
    /// </summary>
    private static void MarkWeaponReceived(Player player, int schematicId)
    {
        if (schematicId == PaddleSchematicId)
        {
            player.ReceivedTutorialPaddle = true;
        }
        else if (schematicId == ShotgunSchematicId)
        {
            player.ReceivedTutorialShotgun = true;
        }
    }

    /// <summary>
    /// 把场上的船桨拾取物销毁 —— 玩家已经拿到手了, 地上不该还躺着一把。
    ///
    /// ## 原厂的自毁判据晚了一步
    ///
    /// <c>_0023aX._0023e0b._0023ZW</c> (:41) 每帧检查
    /// `教程状态 == _00230bc`(状态 5) 才 <c>Destroy()</c>。而状态 5 是**打破栅栏**
    /// 那一步 —— 也就是说船桨模型会在玩家已经拿着它走到栅栏前之后才消失。
    ///
    /// 玩家实测: "船桨拾取后原来的船桨模型没有消失"。确实如此, 而且要等两步。
    ///
    /// 我们在发武器时就销毁它。原厂那个 == 判定大概是为了处理"玩家跳过了拾取"
    /// 之类的边界, 留着无害 (对象已经不在了, 那段每帧检查也就跑不到)。
    ///
    /// ## 为什么不改反编译树
    ///
    /// 改 <c>_0023ZW</c> 的判据要动 sanitizer 的 FILE_PATCHES, 而这件事在服务端
    /// 一行就能做完。改动越小越好。
    /// </summary>
    private static void DestroyPaddlePickups(GameManager manager)
    {
        if (manager?.ActiveGameObjectList == null)
        {
            return;
        }

        // 倒序遍历 —— Destroy() 会把对象排进 DestroyedGameObjectList, 而
        // ActiveGameObjectList 在本帧末尾才被清理。正序遍历时删除是安全的,
        // 但倒序更明确, 而且以后有人改成立即移除也不会踩坑。
        List<GameObjectBase> objects = manager.ActiveGameObjectList;
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            if (objects[i] is _0023aX._0023e0b paddle && paddle.IsActive)
            {
                paddle.Destroy();
            }
        }
    }

    /// <summary>发教程武器之后的回调, 供宿主打日志。参数: (槽位, 图纸ID, 是否真的到手)。</summary>
    public static Action<int, int, bool> OnWeaponGranted { get; set; }

    /// <summary>
    /// 技能栏快照回调, 供宿主打日志。参数: (时机, 描述)。
    /// </summary>
    public static Action<string, string> OnAbilityBarSnapshot { get; set; }

    /// <summary>
    /// 把技能栏的**缓存表**和槽位状态转成一行文本。
    ///
    /// ## 为什么要看缓存表, 而不是看槽位有没有武器技能
    ///
    /// <c>AbilityBar</c> 是 <c>ISynchronizable</c>, 但它序列化槽位时写的不是技能
    /// 本身, 而是**缓存表下标** (AbilityBar.cs:782):
    ///
    ///     netBuffer._0023Yae(0, _00237gc.Count, ability.CacheIndex);
    ///
    /// 而缓存表 <c>_00237gc</c> (公开访问器 <c>CachedAbilityList</c>) **本身不复制**
    /// —— 两边各自靠 <c>CacheAbility</c> 建。
    ///
    /// 所以只要服务端装备武器时往缓存表里加了客户端没有的技能, 两边的下标就错位,
    /// 客户端按错的下标解出错的技能 —— 表现正是"攻击被静默作废"
    /// (Character.cs:3976 拿不到有效 SlotIndex 就走 MarkAbilityAsInvalid)。
    ///
    /// **这条日志就是要验证这个假设**: 装备前后缓存表长度变了几个。
    /// 变了就证实错位, 而且差值就是偏移量; 没变说明原因在别处, 我该换方向。
    ///
    /// 我先前对同一个症状连猜四轮都错, 教训是"症状对不上单一假设时优先装观测点"。
    /// 这次先量再改。
    /// </summary>
    private static string SnapshotAbilityBar(Player player)
    {
        AbilityBar bar = player?.AbilityBar;
        if (bar == null)
        {
            return "无技能栏";
        }

        System.Text.StringBuilder text = new System.Text.StringBuilder();
        text.Append("缓存表=").Append(bar.CachedAbilityList?.Count ?? -1);
        text.Append(" 已装备槽=").Append(player.EquippedWeaponSlot);
        text.Append(" 槽位[");

        Ability[] slots = bar.AbilitySlots;
        if (slots != null)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(' ');
                }

                Ability ability = slots[i];
                text.Append(i).Append(':');
                text.Append(ability == null
                    ? "空"
                    : $"{ability.AbilityIdentifier}#{ability.CacheIndex}");
            }
        }

        return text.Append(']').ToString();
    }

}
