using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 按 <see cref="Spawn_Tutorial"/> 生成教程用的实体。
///
/// ## 这张表是教程的"演员名单"
///
/// <c>TutorialType</c> 九个值:
///
///   PULLER / FLOATER / ENEMY        要杀的怪
///   NPC / NPC_TRUCK / TRUCK         剧情角色和载具
///   TRIGGER_PULLER                  触发点
///   PADDLE / PREBARRICADE           **纯位置标记**
///
/// 客户端只消费最后两个 (-bd/-5cc.cs:316/397), 而且只拿来指引导箭头 ——
/// 说明那两个不需要实体, 船桨和路障的视觉是场景美术。
///
/// 其余七个客户端一次都没读过。**它们是给服务端的生成指令**, 而读它们的
/// 那段代码随服务端丢失 —— 和 <see cref="RoamingZombieSpawner"/> 是同一个形状。
///
/// 后果: 教程要杀的 Puller、Floater、要保护的 NPC 全都不在场上, 对应的
/// 教程步骤永远完不成。
///
/// ## 只生成能确定的
///
/// PULLER 和 FLOATER 的实体类是明的 (<c>GameObjectBase.IsPuller =&gt; this is
/// _0023mPb</c>, <c>IsFloater =&gt; this is _0023iPb</c>), 而且教程状态 7 和 22
/// 就是指着它们让玩家杀。
///
/// NPC 生成 Amber (<c>_0023s3b</c>, Team1) —— 判据见 <see cref="Known"/>,
/// 教程状态机 (-5cc.cs:182) 自己点名了这个类, 不用猜。
///
/// NPC_TRUCK / TRUCK / ENEMY **仍不生成**: 卡车那一组有 2 个 NPC_TRUCK +
/// 1 个 TRUCK + 1 个 <c>TruckerSpawnPoint</c>, 而剩下五个 NPC 类
/// (Berg / Bryce / Isys / Fuse / Charlie) 里哪个站哪个位置没有任何代码点名。
/// 日志会把这些点报出来, 等有别的证据再补。
/// </summary>
public static class TutorialEntitySpawner
{
    public sealed record Result(
        int Spawned,
        IReadOnlyDictionary<TutorialType, int> ByType,
        IReadOnlyDictionary<TutorialType, int> SkippedByType,
        IReadOnlyList<string> Placed);

    /// <summary>
    /// 能确定实体类的类型 -> 混淆类名 + 队伍。
    ///
    /// ## PULLER / FLOATER 的判据
    ///
    /// <c>GameObjectBase</c> 里的类型判定 (:790/:802) —— 教程状态机正是用
    /// <c>IsPuller</c> / <c>IsFloater</c> 找目标的, 生成的类必须和那个判定
    /// 对得上, 否则状态机永远等不到。队伍 Neutral, 它们是怪。
    ///
    /// ## NPC = Amber, 而且必须是 Team1
    ///
    /// 我先前写"两个 NPC 类分不清哪个对应哪个标记点, 猜错比看不到更糟"就停手了。
    /// 那个判断错了两次:
    ///
    ///   1. **教程 NPC 有六个类**, 不是两个:
    ///        _0023s3b Amber   _0023t3b Berg   _0023u3b Bryce (Trapper)
    ///        _0023v3b Isys (Wrench)   _0023ZO Fuse   _00230O Charlie
    ///
    ///   2. **不用猜** —— 教程状态机自己点名了。<c>-bd/-5cc.cs:182</c>:
    ///
    ///          if (activeEntity is _0023s3b s && s.HasBuff(_0023aD._0023QBc))
    ///          {
    ///              _0023Occ = true;
    ///              PlayTransmissionPackage(TutorialPuller01);
    ///          }
    ///
    ///      被 Puller 拉着的那个 NPC 就是 <c>_0023s3b</c> = **Amber**。
    ///      对应的教程步骤 <c>_00237bc</c> 的引导文本是
    ///      <c>TutorialGuideText.RESCUE</c> / "TutorialReworkTexts.Rescue"
    ///      —— **解救**, 和玩家记的原版表现一致。
    ///
    /// 队伍跟玩家走 —— 见 <see cref="PlayerTeam"/>。给 Neutral 的话她既不是队友
    /// 也不是敌人, TutorialNpcAI 的 IsEnemy/IsAlly 判据全乱。
    ///
    /// ⚠ <c>-5cc.cs:476/483/494</c> 找 NPC 当箭头目标时**硬编码了
    /// <c>TeamId.Team1</c>**。那几处属于教程状态 18 之后 (Wrench/Trapper),
    /// 我们还没生成过那两个 NPC, 所以现在碰不到。做到那一步时如果箭头不指人,
    /// 先回来看这里 —— 可能得让那两个 NPC 单独用 Team1。
    ///
    /// 坐标本身也对得上: NPC 标记在 (1658.9, 3108.5), PULLER 在 (1622.8, 3106.6),
    /// **相距 36 个单位** —— 她就是站在 Puller 手边的那个人。
    ///
    /// ## 还差什么
    ///
    /// Amber 在场上了, 但**还没被拉住** —— 那需要 Puller 对她施放
    /// <c>AbilityIdentifier.PullerHook</c> (技能类 <c>_0023QBc</c>, 挂上的 buff
    /// 是 <c>_0023xBc</c>, 枚举 <c>_0023aD._0023QBc</c>)。没有那个 buff,
    /// <c>-5cc.cs:182</c> 那个条件不成立, TutorialPuller01 的语音不会播。
    /// 状态推进本身不依赖它 (推进条件是 Puller 死), 所以不是卡口。
    /// </summary>
    /// <summary>
    /// 玩家(和队友 NPC)的队伍。
    ///
    /// **Team2 = 蓝队。** <c>TeamId</c> 是 Neutral=0/Team1=1/Team2=2/Team3=3
    /// (TeamId.cs), 而 <c>GameMode.TeamHexColor</c> 是
    /// <c>{ffffff, FF7F2A, 317FFF, 60E400}</c> —— 也就是 Team1 橙、**Team2 蓝**。
    ///
    /// 玩家对着原版视频确认是蓝队, 而且他给的补给点截图里圆环和头顶旗帜都是蓝的
    /// (那个圆环用的正是 <c>_TeamColors[(int)captureTeam]</c>,
    /// StorageRoomCaptureGUIScript.cs:113)。
    ///
    /// 放在这里是为了**只有一个真相来源** —— 玩家、Amber、以及任何以后加的
    /// 队友 NPC 都从这里取, 免得改一处漏一处让 Amber 变成敌人。
    /// </summary>
    public const TeamId PlayerTeam = TeamId.Team2;

    /// <summary>
    /// 玩家离 Amber 多近, 就让 Puller 朝她起手钩舌 (<see cref="HookNpcWithPuller"/>
    /// 的触发门槛, 宿主每帧查)。
    ///
    /// ## 300 是错的 —— 玩家实测 Amber 被打死 (2026-09-21)
    ///
    /// 玩家原话: "我到那里时她早就死了, 我一开始就发现 amber 在掉血 …
    /// 应该在我走到附近 puller 才开始拉她。"
    ///
    /// 教程前半段的几个点离 Amber 有多远 (算出来的):
    ///
    ///     出生点  (1634.9, 3409.2)   302     ← 门槛 300, 擦边
    ///     船桨    (1760, 3347)       259     ← **进门槛了**, 那是教程第 3 步
    ///     栅栏    (2060, 3169)       406
    ///     集合点  (1588, 3072)        80     ← 营救现场, 玩家该在这附近才触发
    ///
    /// 也就是说玩家一去捡船桨, Puller 就把她钩住了 —— 而 <c>PullerHookDebuff</c>
    /// 带持续伤害, 实测 **每分钟约 15 点**, 她 120 血只能撑 **8 分钟**。
    /// 新手走完前半段教程很容易超过这个数, 于是玩家赶到营救现场时只看到一具尸体。
    ///
    /// 掉血是原厂 buff 自己的行为, **不该改** —— 原厂设定就是"快去救她"。
    /// 该改的是门槛: 拉她这件事必须发生在玩家已经快走到的时候。
    ///
    /// ## 为什么是 120
    ///
    ///   - 比教程前半段那几个点都小 (最近的船桨 259), 走前半段不会误触发
    ///   - 比集合点到 Amber 的 80 大, 所以玩家走向营救现场的路上一定会穿过它
    ///     —— 不会出现"永远不触发"
    ///   - 比相机可视范围 (约 60~90, 见 <c>ZombieBrain.AggroRadius</c> 的推算) 大,
    ///     加上 1.5 秒吟唱, 玩家走到看得清的地方时她已经被拉住了 —— 保住原版观感
    ///   - 远小于 <c>Buff.ShouldBeSynchronized</c> 的 400 剔除半径, buff 发得出去
    ///
    /// 触发晚了也不会卡教程: 状态推进条件是"Puller 死"加 <c>_sawPullerAlive</c>,
    /// 从来不看这个 buff。
    /// </summary>
    public static float HookDistance { get; set; } = 120f;

    private static readonly (TutorialType Type, string ClassName, TeamId Team)[] Known =
    {
        (TutorialType.PULLER, "_0023mPb", TeamId.Neutral),
        (TutorialType.FLOATER, "_0023iPb", TeamId.Neutral),
        (TutorialType.NPC, "_0023s3b", PlayerTeam),

        // 遭遇战那一段的敌人 —— 玩家实测确认是 **Charlie 和 Fuse** 两个人。
        // 第一版按名字猜的 Sledge(显示名 Berg) 是错的。
        // 对照表在 -t5/-APg.cs:55-60。
        (TutorialType.ENEMY, "_00230O", EnemyTeam),
    };

    /// <summary>
    /// 卡车那边的两个队友 NPC —— <c>NPC_TRUCK</c> 标记正好两个, 而客户端教程
    /// 状态 18 找箭头目标时要的正是 <c>IsTutorialNPC_Wrench</c> 和
    /// <c>IsTutorialNPC_Trapper</c> (-bd/-5cc.cs:476/483)。
    ///
    /// 明文名来自 <c>-t5/-APg.cs:59-60</c>:
    /// <c>_0023u3b</c> = TutorialNPC_Trapper, <c>_0023v3b</c> = TutorialNPC_Wrench。
    ///
    /// 按标记出现的顺序轮流用 —— 两个标记两个类, 谁在哪个位置无从得知,
    /// 反正都在卡车旁边。
    /// </summary>
    private static readonly string[] TruckCrew = { "_0023v3b", "_0023u3b" };

    /// <summary>
    /// 敌对 NPC 的队伍。
    ///
    /// 只要和 <see cref="PlayerTeam"/> 不同就是敌人 (<c>IsAlly</c> 是纯粹的
    /// TeamId 比较, GameObjectBase.cs:1036)。用 Team3 而不是 Neutral ——
    /// Neutral 是僵尸阵营, 敌对**幸存者**应该是另一支队伍。
    ///
    /// ## 队伍颜色是玩家实测定的, 不是从代码推的
    ///
    /// <c>GameMode.TeamHexColor</c> = <c>{ffffff, FF7F2A, 317FFF, 60E400}</c> ——
    /// Team1 橙 / Team2 蓝 / Team3 绿。玩家实测要求: **敌对 NPC 是橙队**,
    /// 卡车队友和自己一样是蓝队。
    ///
    /// ## 曾经改成 Team3, 是错的
    ///
    /// 客户端状态 18/19 找引导箭头目标时写死了 <c>TeamId.Team1</c>
    /// (-bd/-5cc.cs:476/483/494), 找的是 Wrench/Trapper。我据此把卡车队友改成
    /// Team1、敌人让位到 Team3 —— 结果玩家看到的是"队友橙、敌人绿", 两个都不对。
    ///
    /// 那段客户端代码在原版里多半也是找不到的 (地图上的 TruckerSpawnPoint 和
    /// PlayerSpawnPoint 都是 Team2, 没有任何 Team1 的东西), 而**找不到的后果很温和**:
    /// <c>_0023Lcc</c>/<c>_0023Mcc</c> 保持上一个状态的值, 也就是状态 17 设的
    /// (-181,3353) —— 箭头继续指卡车, 正是玩家该去的地方。状态 20 又会把它显式设回去。
    ///
    /// **教训: 客户端一段代码"要求某个值"不等于"原版就是那个值"。**
    /// 视觉表现以玩家实测为准。
    /// </summary>
    public const TeamId EnemyTeam = TeamId.Team1;

    /// <summary>
    /// 卡车那两个队友的队伍 —— 和玩家同队 (蓝)。
    ///
    /// 单独起个名字而不是直接写 <see cref="PlayerTeam"/>: 上面那段说明记录了
    /// 一次把它拆出去的失败尝试, 留个名字让"卡车队友的队伍"这件事有地方可查。
    /// </summary>
    public const TeamId TruckCrewTeam = PlayerTeam;

    /// <summary>
    /// 这个实体是不是卡车那边的队友 (Wrench / Trapper)。
    ///
    /// 队伍和 Amber 一样是蓝队, 所以**只能按类名区分** ——
    /// "站在卡车边等着"和"跟着玩家走"是两种行为。
    /// </summary>
    public static bool IsTruckCrew(GameObjectBase entity)
    {
        if (entity == null)
        {
            return false;
        }

        string name = entity.GetType().Name;

        foreach (string className in TruckCrew)
        {
            if (name == className)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 遭遇战的第二个敌人: TutorialNPC_Fuse (-t5/-APg.cs:57)。
    ///
    /// 地图上 <c>ENEMY</c> 标记**只有一个**, 而玩家实测说这一场是
    /// **Charlie 和 Fuse** 两个人 —— 所以从同一个标记生成两个, 第二个往旁边挪
    /// <see cref="SecondEnemyOffset"/>。
    /// </summary>
    private const string SecondEnemyClass = "_0023ZO";

    /// <summary>
    /// 第二个敌人相对标记点的偏移。**我们定的** —— 两个人重叠在一个点上不好看,
    /// 错开一点像是并排站着。
    /// </summary>
    private static readonly Vector2 SecondEnemyOffset = new Vector2(6f, 0f);

    /// <summary>纯位置标记, 不该生成任何东西 —— 客户端拿它们指箭头。</summary>
    private static readonly TutorialType[] MarkersOnly =
    {
        TutorialType.PREBARRICADE,
        TutorialType.TRIGGER_PULLER,
    };

    /// <summary>
    /// 在 PADDLE 标记点生成船桨**拾取物** <c>_0023aX._0023e0b</c>。
    ///
    /// ## 先前的判断错了
    ///
    /// 我原本把 PADDLE 归进"纯位置标记, 视觉是场景美术" —— 因为客户端只拿它
    /// 指引导箭头。玩家查了 YouTube 的教程视频, 那个位置**确实有一个船桨模型**,
    /// 而且要走过去才能拿。
    ///
    /// 实体是现成的: <c>_0023e0b</c> 带着两个专属特效 ——
    /// <c>Vfx_PaddlePickup_01</c> (躺在地上时) 和
    /// <c>Vfx_PaddlePickup_Pickup_01</c> (被拿走时), 还有个
    /// <c>_0023e0b(Vector2 pos)</c> 的构造函数。它一直在, 只是没人生成。
    ///
    /// ## 拾取半径是 4, 不是 30
    ///
    /// 拾取物基类 <c>_00235W.UpdateGameObject</c> (-6W/-5W.cs:50) 每帧做碰撞查询:
    ///
    ///     Entity e = CollisionManager._0023o8(new _0023Gjc(_0023B, _0023Vb), ...);
    ///     if (e != null &amp;&amp; (_0023TW == Neutral || e.TeamId == _0023TW))
    ///         _0023XW(GameMode, e.AsPlayer);       // 拾取回调
    ///
    /// <c>_0023e0b._0023VW</c> 里 <c>_0023Vb = 4f</c> —— **那才是拾取半径**。
    /// 我先前用的 30 是引导箭头的阈值 (客户端 -5cc.cs:586), 两码事:
    /// 30 是"箭头不用再指了", 4 是"够近可以拿了"。
    ///
    /// ## 拾取回调是空的
    ///
    /// <c>_0023e0b._0023XW</c> (:66) 什么都不做 —— 发武器那段随服务端丢失。
    /// 我们不改反编译树, 而是由 <see cref="TutorialDirector"/> 自己判距离。
    ///
    /// ## 自毁是现成的
    ///
    /// <c>_0023e0b._0023ZW</c> (:41) 每帧检查 `教程状态 == _00230bc(5)` 就
    /// <c>Destroy()</c> —— 也就是玩家走到栅栏那步时船桨自己消失, 不用我们管。
    /// </summary>
    private static bool SpawnPaddlePickup(GameManager manager, Spawn_Tutorial marker)
    {
        _0023aX._0023e0b paddle = manager.GetGameObjectFromPool<_0023aX._0023e0b>();
        if (paddle == null || paddle.IsDummyObject)
        {
            return false;
        }

        // 位置写的是 _0023B 而不是 Position —— 拾取物基类用自己的字段
        // (-6W/-5W.cs:12), 和 GameObjectBase.Position 不是一回事。
        paddle._0023B = marker.Position;

        // Neutral 让任何队伍都能拾取, 和基类那句
        // `_0023TW == TeamId.Neutral || entity.TeamId == _0023TW` 对应。
        paddle._0023TW = TeamId.Neutral;

        manager.SpawnGameObject(paddle, null, null);
        return paddle.IsActive;
    }

    /// <summary>
    /// 让 Puller 把 Amber 钩住 —— 教程状态 7 的引导文本是
    /// <c>TutorialGuideText.RESCUE</c>, 玩家要"解救"她。
    ///
    /// ## 判据
    ///
    /// <c>-bd/-5cc.cs:182</c> 检查的正是这个:
    ///
    ///     if (activeEntity is _0023s3b s &amp;&amp; s.HasBuff(_0023aD._0023QBc))
    ///     {
    ///         _0023Occ = true;
    ///         PlayTransmissionPackage(TutorialPuller01);   // 语音
    ///     }
    ///
    /// 枚举 <c>_0023aD._0023QBc</c> 对应的 buff 实现类是 <c>_0023xBc</c>
    /// (-Sq/-xBc.cs:84 `_0023iq = _0023aD._0023QBc`), 明文名
    /// **PullerHookDebuff** (GUID 42c41f42-…, 查表得来)。
    ///
    /// ## 为什么直接挂 buff 而不是让 Puller 施法
    ///
    /// 施法要走 <c>AbilityBar.TryUseAbility</c>, 而 PullerHook 有射程、冷却、
    /// 目标合法性判定 —— 那些都得先满足, 而且施完 buff 还会按自己的时长掉。
    /// 教程要的是"玩家来之前她一直被拉着", 那是个**持续状态**, 不是一次施法。
    ///
    /// 原厂怎么做的看不到 (那段服务端代码丢了), 所以这是**我们的做法**。
    /// buff 本身是原厂的, 挂的对象和判据也是原厂点名的, 只有"直接挂"这个手法是我们选的。
    ///
    /// buff 会不会自己掉: <c>_0023xBc</c> 有 <c>_0023Iq(deltaTime, totalTime)</c>
    /// 每帧钩子, 掉了就再挂一次 —— 由 <see cref="TutorialDirector"/> 那边维持,
    /// 这里只负责初次挂上。
    /// </summary>
    /// <returns>成功挂上返回 true。</returns>
    public static bool HookNpcWithPuller(GameManager manager)
    {
        if (manager?.ActiveEntityList == null)
        {
            return false;
        }

        Character puller = null;
        Character npc = null;

        foreach (Entity entity in manager.ActiveEntityList)
        {
            if (!entity.IsActive || entity.IsDead)
            {
                continue;
            }

            if (entity.IsPuller && puller == null)
            {
                puller = entity.AsCharacter;
            }
            else if (entity is _0023Mk._0023s3b amber && npc == null)
            {
                npc = amber;
            }
        }

        if (puller == null || npc == null)
        {
            return false;
        }

        if (npc.HasBuff(_0023Oq._0023aD._0023QBc))
        {
            return true;   // 已经挂着了, 幂等
        }

        // owner 和 spawner 都传 Puller —— buff 的伤害/表现要归因到它,
        // 而且客户端的绳索特效是按 owner 画的。
        _0023Sq._0023xBc hook =
            manager.GetAndApplyBuffFromPool<_0023Sq._0023xBc>(npc, puller, puller);

        return hook != null;
    }

    public static Result SpawnAll(GameManager manager)
    {
        // 卡车那两个队友按标记顺序轮流用 TruckCrew 里的类。
        int truckCrewNext = 0;

        List<string> placed = new List<string>();
        Dictionary<TutorialType, int> byType = new Dictionary<TutorialType, int>();
        Dictionary<TutorialType, int> skipped = new Dictionary<TutorialType, int>();
        int total = 0;

        if (manager?.MapObjects == null)
        {
            return new Result(0, byType, skipped, placed);
        }

        Dictionary<string, Type> types = ResolveTypes();

        foreach (MapObject mapObject in manager.MapObjects)
        {
            if (mapObject is not Spawn_Tutorial marker)
            {
                continue;
            }

            if (Array.IndexOf(MarkersOnly, marker.TutorialType) >= 0)
            {
                continue;
            }

            // 船桨走单独一条路 —— 它不是角色, 是拾取物 (_00235W 的子类)。
            if (marker.TutorialType == TutorialType.PADDLE)
            {
                if (SpawnPaddlePickup(manager, marker))
                {
                    total++;
                    byType.TryGetValue(marker.TutorialType, out int paddles);
                    byType[marker.TutorialType] = paddles + 1;
                }

                continue;
            }

            string className = null;
            TeamId team = TeamId.Neutral;

            // 卡车那两个队友走单独一条路 —— 一个标记类型要生成两个**不同的**类,
            // Known 那张一对一的表表达不了。
            if (marker.TutorialType == TutorialType.NPC_TRUCK)
            {
                className = TruckCrew[truckCrewNext % TruckCrew.Length];
                truckCrewNext++;

                // **不是 PlayerTeam** —— 客户端写死了 Team1, 见 TruckCrewTeam。
                team = TruckCrewTeam;
            }
            else
            {
                foreach ((TutorialType type, string name, TeamId knownTeam) in Known)
                {
                    if (type == marker.TutorialType)
                    {
                        className = name;
                        team = knownTeam;
                        break;
                    }
                }
            }

            if (className == null || !types.TryGetValue(className, out Type entityType))
            {
                // 认不出来的类型 —— 记下但不生成。见类说明。
                skipped.TryGetValue(marker.TutorialType, out int seen);
                skipped[marker.TutorialType] = seen + 1;
                continue;
            }

            if (Spawn(manager, entityType, marker, team))
            {
                total++;
                byType.TryGetValue(marker.TutorialType, out int count);
                byType[marker.TutorialType] = count + 1;

                // 逐个记下类型/类名/队伍/坐标。ENEMY 那个类是我们**猜**的
                // (见 Known 表里的说明), 卡车那两个队友也只知道"有两个"、
                // 不知道谁站哪 —— 这份清单是玩家核对的唯一依据。
                placed.Add($"{marker.TutorialType}={entityType.Name}/{team}" +
                           $"@({marker.Position.X:F0},{marker.Position.Y:F0})");

                // 遭遇战是两个人 —— 见 SecondEnemyClass 的说明。
                if (marker.TutorialType == TutorialType.ENEMY
                    && types.TryGetValue(SecondEnemyClass, out Type second)
                    && Spawn(manager, second, marker, team, SecondEnemyOffset))
                {
                    total++;
                    byType[marker.TutorialType] = byType[marker.TutorialType] + 1;
                    placed.Add($"{marker.TutorialType}={second.Name}/{team}" +
                               $"@({marker.Position.X + SecondEnemyOffset.X:F0}," +
                               $"{marker.Position.Y + SecondEnemyOffset.Y:F0})");
                }
            }
        }

        return new Result(total, byType, skipped, placed);
    }

    /// <summary>
    /// 生成一只。
    ///
    /// 和漫游僵尸不同, 教程怪**不设出场动画** —— 它们是剧情摆位, 玩家走到
    /// 那一步之前就该在那儿站着 (客户端的引导箭头会指向它们)。设了 Digging
    /// 的话玩家远远看去会看到一只怪凭空从地里钻出来, 和引导对不上。
    /// </summary>
    private static bool Spawn(GameManager manager, Type entityType, Spawn_Tutorial marker, TeamId team,
                              Vector2 offset = default)
    {
        if (GameManager.GetGameObjectTypeId(entityType) < 0)
        {
            return false;
        }

        GameObjectBase spawned = manager.GetGameObjectFromPool(entityType);
        if (spawned is not Character character || character.IsDummyObject)
        {
            return false;
        }

        character.Position = marker.Position + offset;

        // 队伍来自调用方 —— 怪是 Neutral, 队友是 PlayerTeam, 敌对 NPC 是 EnemyTeam。
        character.TeamId = team;

        // 朝向也用地图数据。Amber 站在 Puller 手边, 朝向决定她面朝哪边 ——
        // 摆错了看起来像背对着抓她的怪。
        if (marker.Direction != FarseerPhysics.Common.Vector2.Zero)
        {
            character.AimDirection = marker.Direction;
        }

        // ## 人形角色免疫近战击退, 僵尸不免疫
        //
        // 玩家实测: "PVP 和 PVE 的战斗不一样, 要分开来" —— 近战打**僵尸**
        // 有击退+眩晕, 打**人**没有。先前只给玩家上了免疫, 于是 Amber 砍
        // Charlie/Fuse 时把他们打得又退又晕。
        //
        // 判据用基类而不是队伍: <c>_0023Mk._0023YO</c> 是教程里所有**幸存者**
        // NPC 的基类 (Amber / 卡车队友 / 遭遇战那两个), 而 Puller、Floater
        // 走的是僵尸基类 <c>_0023iO</c> —— 同样由本方法生成, 但它们该被打退。
        //
        // 这条**不是**抗性系统的替代品: 击退 buff 强度 4 高过人形角色的抗性 3,
        // 抗性那条路会放行它 (算式见 <c>GameManagerServer.ResistsMeleeKnockback</c>)。
        // 原厂拦近战击退用的就是 <c>CanBeMeleeKnockBacked</c> 这个开关。
        // 这里是把和玩家一样的处理铺到人形 NPC 上。
        if (character is _0023Mk._0023YO)
        {
            character.PermanentMeleeKnockbackImmunity = true;
        }

        manager.SpawnGameObject(character, null, null);

        return character.IsActive;
    }

    /// <summary>
    /// 按混淆类名从注册表里找类型。
    ///
    /// 不硬编码 typeId —— 那是按注册顺序算的, 换版本就变。和
    /// <see cref="RoamingZombieSpawner.ResolveKindTypes"/> 同一个做法。
    /// </summary>
    private static Dictionary<string, Type> ResolveTypes()
    {
        Dictionary<string, Type> found = new Dictionary<string, Type>();

        foreach (KeyValuePair<int, Type> entry in GameManager.GameObjectTypeIDToType)
        {
            if (entry.Value == null)
            {
                continue;
            }

            foreach ((TutorialType _, string className, TeamId _) in Known)
            {
                if (entry.Value.Name == className)
                {
                    found[className] = entry.Value;
                }
            }

            // 卡车那两个队友不在 Known 表里 (一个标记类型两个类, 那张表表达不了),
            // 得单独解析。漏了这一步的表现是 NPC_TRUCK 照样被算成"认不出实体类",
            // 而日志里看不出是"没这个类"还是"没查这个类" —— 实测栽过一次。
            foreach (string className in TruckCrew)
            {
                if (entry.Value.Name == className)
                {
                    found[className] = entry.Value;
                }
            }

            // 遭遇战的第二个敌人同理。
            if (entry.Value.Name == SecondEnemyClass)
            {
                found[SecondEnemyClass] = entry.Value;
            }
        }

        return found;
    }
}
