using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Entities.Destructibles;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 按地图上的 <see cref="Spawn_Destructible"/> 生成路障 / 栅栏。
///
/// ## 实体类一直都在, 只是不在我以为的地方
///
/// 先前我查 <c>ConductorGameLogic.Entities.Destructibles</c> 命名空间, 里面
/// 只剩一个 <c>BarricadePart</c> 枚举, 就下了"实体被链接器裁了"的结论 ——
/// **那是错的**。实体类是 <c>_0023EK._0023Yh</c> (668 行, 继承
/// <c>Destructible</c>), 在混淆命名空间里, 完整无缺:
/// 碰撞体、三档破损视觉、各 BarricadeType 的分支处理全在。
///
/// 教训: 命名空间里没有 ≠ 类不存在。混淆过的代码库要按**能力**搜
/// (这里是 <c>BarricadeType</c> 字段), 不能按命名空间搜。
///
/// ## 生成步骤照抄原厂
///
/// <c>HordeSpawner.SpawnBlockage</c> (:171) 是现成的模板:
///
///     从池取 -> 设 Position / 方向 / Part / Type -> SpawnGameObject
///     -> _0023Txc()          设成无敌 (Horde 准备阶段用)
///     -> _0023DZb(血量)
///
/// **教程不能调 <c>_0023Txc()</c>** —— 那个把 Defense 设成 1.0 (100% 减伤)
/// 并加 NoSpellCollision, 路障就打不掉了。Horde 模式在准备阶段这么用,
/// 教程要玩家打破它, 显然不能设。
///
/// ## 血量: 三张 Scout 图的实测 (2026-09-21 推倒重来)
///
/// <c>_0023Yh</c> 的 MaxHealth 默认值是 **0** (-EK/-Yh.cs:36), 必须外部设,
/// 而 <c>Spawn_Destructible</c> 里**没有血量字段**。真正的数在服务端那半,
/// 随服务端丢了。所以血量只有一个可靠来源: **玩家看原版视频数血条**。
///
/// | 图 | 玩家数到的 | 地图上的组 |
/// |---|---|---|
/// | Camp by Night (Scout_01_Outpost) | 420 / 70 / 70 / 420 | Military ×2 + WeakMilitary ×2 |
/// | Purple Moon Club (Scout_03_Club) | 147 / 147           | Wooden + Horde03Gate |
/// | Lab Raid (Scout_02_Lab)          | 147 / 25 / 49       | Door + Iron + WeakDoor (IronLab 锁着没数) |
/// | 教程 (Tutorial_01_Prologue)      | 200 / 1400          | Fence + WeakWooden (电栏打不破) |
///
/// 前两张**不用知道走位顺序**就能定死: 值和类型的重数一一对应 (420 出现两次只能
/// 落在 Military 上, Club 两道同值)。Lab 那三项要顺序, 玩家确认了第一道 (147)
/// 是**门**。
///
/// 这个答案有一条独立佐证: "普通 vs 弱化"在两张图上是同一个比例 ——
/// Door : WeakDoor = 147 : 25 ≈ **5.9 : 1**, Military : WeakMilitary = 420 : 70 = **6 : 1**。
/// 要是第一道是铁栅栏 (Door 就成了 49), 比例只有 2:1, 和 Camp by Night 对不上。
/// 剩下的 49 归 Iron —— 弱化门 (25) 不可能比铁栅栏硬。
///
/// ## 被推倒的: <c>ResetDestructible</c> 那八个常量
///
/// <c>_0023Yh.ResetDestructible</c> (-EK/-Yh.cs:142-149) 赋了八个**全树零读取方**的
/// 常量: 200 / 6000 / 1400 / 10000 / 2000 / 1000 / 1000 / 8000。09-18 我把它们按
/// "普通 / 弱化成对"分给了各个类型, 依据是"200 和 1400 正好等于玩家在教程里实测的
/// Fence 和 WeakWooden"。
///
/// **这个解读是错的。** 实测值全部落在 **25 ~ 420**, 而那八个常量最小 200、最大
/// 10000, 根本不是一个量纲:
///
///   - 420 / 70 / 147 / 49 / 25 **没有一个在那八个数里**, 怎么重排都凑不出来
///   - 最离谱的是 Wooden: 表里 6000, 实测 147 —— 差四十倍
///   - 200 当初"命中"是巧合 (八个数里本来就有小值)
///
/// 所以那八个常量**不是按 BarricadeType 分的血量表**, 到底是什么不知道, 也不必再查:
/// 零读取方意味着答案在丢掉的那半服务端里, 读客户端推不出来。
/// **有新的视频实测就按实测改, 不要回头去"对齐"那八个常量。**
///
/// ## 教程自成一套 (2026-09-21 补测)
///
/// 玩家重看教程视频, 两道路障是 **200 / 1400**, 和最早那次记录一字不差 —— 所以
/// 那两个值是真的, 不是误记。但它们和别的图凑不到一起:
///
///   - <c>WeakWooden</c> (1400, 教程) 比 <c>Wooden</c> (147, Club) **硬将近十倍**,
///     名字上完全说不通
///   - 全部九个实测值里, **只有教程这两个落在那八个常量里** (200 和 1400 都在),
///     别的图一个都不在
///
/// 两头都是实测, 没得挑。最可能的解释是教程的路障血量走的是另一条路
/// (教程本来就是全脚本化的关卡), 而那八个常量里至少这两个是给它用的。
/// 剩下六个常量 (1000/1000/2000/6000/8000/10000) 仍然不知道是给谁的。
///
/// **不要**为了"让表看起来合理"去调教程这两个值, 或者反过来调 Wooden ——
/// 九个值全都是玩家数出来的, 反常就照抄。
///
/// 这张表所有模式共用 —— 09-18 之前只有拾荒在用, 教程 / Scout 还吃着默认的 200。
/// 注意**类型是跨模式共享的**: Horde 三张图 (map 5/6/7) 和 Scout 三张图用的是同一批
/// 类型, 拾荒 Jungle 也有 MilitaryFence。所以改一个值会同时动到好几个模式,
/// 而实测只来自 Scout —— 目前没有证据说别的模式该用不同的数
/// (所有地图的 <c>BarricadeHitpointsModifier</c> **都是 1**, 已全量确认, 没有图级缩放)。
///
/// 没在表里的类型 (TutorialElectric / Car / Fog / Blockage / IronLab / ShedDoor / …)
/// 继续用 <see cref="DefaultHealth"/>; 它们要么打不破 (教程电栏、Blockage),
/// 要么由各自的导演单独设 (<see cref="CrossroadStash"/> 的两扇门)。
///
/// ## 当时查过并排除的地方 (留着省得重查)
///
///   - <c>Spawn_Destructible</c> 地图对象 —— 只有 BarricadeType / Part /
///     GroupID / Team / IsDisabledFromStart / DynamiclySpawned, **没有血量字段**
///   - 路障组 <c>_00235xc</c> —— 构造函数参数就叫 <c>childrenHealth</c> (明文),
///     但 <c>new _00235xc(...)</c> 在整个客户端树里**零调用者**
///   - <c>HordeSpawner.SpawnBlockage</c> —— 传 <c>float.MaxValue</c>, 那是无敌路障
///   - 平衡数据 <c>_0023j7f</c> (-cL/-j7f.cs) —— 那些 200f/300 是**计分板分值**
///   - <c>Maps/Tutorial_01_Prologue.gms</c> —— 只有 NumOfTeams / GameModeType /
///     <c>BarricadeHitpointsModifier = 1</c> (**修正系数**, 说明基础值在别处) / SuppliesToWin
///   - <c>cachedata.sexp</c> / <c>cacheattributs.sexp</c> —— 对象池清单, 无数值
/// </summary>
public static class DestructibleSpawner
{
    /// <summary>
    /// 表里没有的类型用多少血。200 来自玩家对原版的实测 (教程第一道栅栏),
    /// 和原厂表里最薄的那一档同值。
    /// </summary>
    public static float DefaultHealth { get; set; } = 200f;

    /// <summary>
    /// 按类型的血量 —— 原厂 <c>_0023Yh.ResetDestructible</c> 里那八个零调用方的常量
    /// (说明见类注释)。所有模式公用; 拾荒在这之上再乘地图的
    /// <c>BarricadeHitpointsModifier</c> (见 <see cref="ScavengerDirector.HealthFor"/>)。
    /// </summary>
    public static readonly Dictionary<BarricadeType, float> HealthByType = new()
    {
        // --- 实测 (玩家看原版视频数血条) ---

        // Camp by Night (Scout_01_Outpost): 420 / 70 / 70 / 420,
        // 那张图正好两组 Military + 两组 WeakMilitary, 对应唯一确定。
        [BarricadeType.MilitaryFenceBarricade] = 420f,
        [BarricadeType.WeakMilitaryFenceBarricade] = 70f,

        // Purple Moon Club (Scout_03_Club): 147 / 147。两组 (Wooden + Horde03Gate)
        // 同值, 所以这一张也不用管玩家的走位顺序。
        [BarricadeType.WoodenBarricade] = 147f,
        [BarricadeType.Horde03Gate] = 147f,

        // Lab Raid (Scout_02_Lab): 147 / 25 / 49。三组可打的 (Door / Iron / WeakDoor,
        // IronLab 那组锁着没数)。玩家确认第一道 (147) 是**门**, 剩下 25 / 49 按
        // "弱化门必定弱于铁栅栏"排 —— 见下面那条 6:1 的佐证。
        [BarricadeType.DoorBarricade] = 147f,
        [BarricadeType.IronBarricade] = 49f,
        [BarricadeType.WeakDoorBarricade] = 25f,

        // 教程 (Tutorial_01_Prologue): 200 / 1400。玩家 09-21 重看视频二次确认,
        // 和最早那次记录一字不差。三组里电栏打不破, 所以这两道就是 Fence 和
        // WeakWooden, 顺序也没得排。
        //
        // **WeakWooden (1400) 比 Wooden (147) 硬将近十倍** —— 名字上说不通,
        // 但两头都是实测, 而且教程这两个值是仅有的落在那八个常量里的
        // (200 和 1400 都在), 别的图一个都不在。像是教程自成一套。
        // 反常但照抄, 见类注释末尾那节。
        [BarricadeType.FenceBarricade] = 200f,
        [BarricadeType.WeakWoodenBarricade] = 1400f,
    };

    /// <summary>这一段路障该有多少血。</summary>
    public static float HealthFor(BarricadeType type) =>
        HealthByType.TryGetValue(type, out float health) ? health : DefaultHealth;

    public sealed record Result(int Spawned, IReadOnlyDictionary<BarricadeType, int> ByType,
                                IReadOnlyDictionary<TeamId, int> ByTeam);

    /// <summary>
    /// 强制路障的队伍。<c>null</c> = 照抄地图数据。
    ///
    /// ## 为什么要盖掉地图数据
    ///
    /// 地图给教程路障标的是 **Team2**。玩家原来是 Team1 时两边不同队, 路障算敌方,
    /// 打得动; 把玩家改成蓝队(Team2, 见 <see cref="TutorialEntitySpawner.PlayerTeam"/>)
    /// 之后路障就成了"己方建筑"—— <c>IsEnemy</c> 为假, **玩家和 Amber 都打不了它**,
    /// 教程当场卡死在第一道栅栏。
    ///
    /// 这个连锁反应是单元测试当场抓住的 (Amber_TargetsBarricades 报
    /// "路障对她不是敌人"), 没等到真机。
    ///
    /// 玩家看原版视频确认"第一个 fence 是中立的" —— 和这里的结论一致。
    /// 地图那个 Team2 大概是 Scavenger 模式的遗留 (那边路障确实分属两队),
    /// 教程里没有敌对队伍, 中立才对。
    ///
    /// 实际读到的地图值会打进日志 ("生成路障 … | 队伍 …"), 想核对随时能看。
    /// </summary>
    public static TeamId? ForcedTeam { get; set; } = TeamId.Neutral;

    /// <param name="rewards">
    /// 传进来的话每段生成出来的路障都会登记进去, 打破时发经验。
    /// 传 null 就只生成不登记 —— 测试和"我只想看这张图有几段路障"用得上。
    /// </param>
    /// <param name="skip">开局不放的段 (拾荒图上标 DynamiclySpawned 的 LockDownGauntletBlockage, 见 ScavengerDirector.SkipAtStart)。</param>
    public static Result SpawnAll(GameManager manager, DestructibleRewards rewards = null,
                                  Func<Spawn_Destructible, bool> skip = null)
    {
        Dictionary<BarricadeType, int> byType = new Dictionary<BarricadeType, int>();

        // 路障归谁, 完全照抄地图数据 (spawn.Team)。玩家反馈"第一个 fence 应该是
        // 中立的" —— 报一下实际值, 好分清是地图就这么写的, 还是我们读错了。
        Dictionary<TeamId, int> byTeam = new Dictionary<TeamId, int>();
        int total = 0;

        if (manager?.MapObjects == null)
        {
            return new Result(0, byType, byTeam);
        }

        // 按 GroupID 建路障组 —— 见 AttachToGroup 的说明。
        Dictionary<int, _0023EK._00235xc> groups = new Dictionary<int, _0023EK._00235xc>();

        foreach (MapObject mapObject in manager.MapObjects)
        {
            if (mapObject is not Spawn_Destructible spawn || (skip?.Invoke(spawn) ?? false))
            {
                continue;
            }

            if (Spawn(manager, spawn, rewards, groups))
            {
                total++;
                byType.TryGetValue(spawn.BarricadeType, out int seen);
                byType[spawn.BarricadeType] = seen + 1;

                byTeam.TryGetValue(spawn.Team, out int seenTeam);
                byTeam[spawn.Team] = seenTeam + 1;
            }
        }

        return new Result(total, byType, byTeam);
    }

    /// <summary>
    /// 把一段路障挂进它的组, 让整组**共享血量**。
    ///
    /// ## 这是原厂机制, 不是我们发明的
    ///
    /// <c>_0023Yh</c> 的属性变更钩子 (-EK/-Yh.cs:449-451):
    ///
    ///     if (_0023iu == StatType.Health &amp;&amp; _0023Exc != null)
    ///         _0023Exc._00232xc(_0023iu, _0023h, _0023kt, _0023lt, _0023ju);
    ///
    /// 组对象 <c>_00235xc._00232xc</c> (-EK/-5xc.cs:68-81) 把血量同步给组内每一段。
    /// 也就是说打任意一段等于打整组。
    ///
    /// ## 我先前说它"零调用者"是不准确的
    ///
    /// 我查过 `new _00235xc(...)` 在客户端树里没有调用者, 就把它归进"随服务端丢失"。
    /// **丢的是调用点, 类本身是完整可用的** —— 构造函数
    /// <c>public _00235xc(int id, int childrenHealth)</c> 和 <c>_00231xc</c> 都在。
    /// 这跟"被链接器裁成空壳"是两回事, 我该早点分清。
    ///
    /// ## GroupID 就是组号
    ///
    /// 教程地图的分组 (实测):
    ///
    ///   3003   TutorialElectricBarricade × 7   围在发电机周围 13.8~73.3
    ///   3002   FenceBarricade × 1
    ///   10103  WeakWoodenBarricade × 3
    ///
    /// 不建组的后果: 玩家要把 7 段电栅栏一段段打, 7 × 200 = 1400 血,
    /// 而原版应该是打一组 200。
    ///
    /// <c>_00231xc</c> 自己会设 <c>Health</c> 和 <c>_0023Exc</c>, 所以它必须在
    /// <c>_0023DZb</c>(设 MaxHealth) **之后**调 —— 反了的话 Health 会被 MaxHealth
    /// 的 clamp 压住。
    /// </summary>
    private static void AttachToGroup(_0023EK._0023Yh barricade, int groupId, float health,
                                      IDictionary<int, _0023EK._00235xc> groups)
    {
        if (groups == null)
        {
            return;
        }

        if (!groups.TryGetValue(groupId, out _0023EK._00235xc group))
        {
            // 组血量要和段血量一致 —— 组的 childrenHealth 会同步给每一段,
            // 传 DefaultHealth 的话木栅栏那组 (1400) 会被压回 200。
            group = new _0023EK._00235xc(groupId, (int)health);
            groups[groupId] = group;
        }

        group._00231xc(barricade);
    }

    /// <summary>
    /// 地图上没有、由服务端现场摆的一段路障 (十字路口补给设施的门, 见 <see cref="CrossroadStash"/>)。
    /// 步骤和按地图生成的完全一样; 同一个 <paramref name="groups"/> 里同 <paramref name="groupId"/> 的
    /// 几段共享血量。
    /// </summary>
    /// <returns>生成出来的那一段; 池空/没激活返回 null。</returns>
    public static _0023EK._0023Yh SpawnPiece(GameManager manager, Vector2 position, Vector2 direction,
                                             BarricadePart part, BarricadeType type, float health,
                                             int groupId, IDictionary<int, _0023EK._00235xc> groups)
    {
        _0023EK._0023Yh barricade = manager.GetGameObjectFromPool<_0023EK._0023Yh>();
        if (barricade == null || barricade.IsDummyObject)
        {
            return null;
        }

        barricade.Position = position;
        barricade._0023Gt = direction;
        barricade._00238Zb = part;
        barricade._00239Zb = type;
        barricade.TeamId = ForcedTeam ?? TeamId.Neutral;

        manager.SpawnGameObject(barricade, null, null);
        barricade._0023DZb(health);

        if (!barricade.IsActive)
        {
            return null;
        }

        AttachToGroup(barricade, groupId, health, groups);
        return barricade;
    }

    private static bool Spawn(GameManager manager, Spawn_Destructible spawn,
                              DestructibleRewards rewards,
                              Dictionary<int, _0023EK._00235xc> groups)
    {
        _0023EK._0023Yh barricade = manager.GetGameObjectFromPool<_0023EK._0023Yh>();
        if (barricade == null || barricade.IsDummyObject)
        {
            return false;
        }

        barricade.Position = spawn.Position;
        barricade._0023Gt = spawn.Direction;
        barricade._00238Zb = spawn.Part;
        barricade._00239Zb = spawn.BarricadeType;
        barricade.TeamId = ForcedTeam ?? spawn.Team;

        manager.SpawnGameObject(barricade, null, null);

        // 血量必须在 Spawn 之后 —— ChangeStats 要实体已经激活。
        float health = HealthFor(spawn.BarricadeType);
        barricade._0023DZb(health);

        // ## IsDisabledFromStart 是"打不破", 不是"不存在"
        //
        // 第一版看到这个标志就整段跳过不生成。玩家实测打脸:
        // "有一只 ram 会冲出来把一道门撞开, 但是现在进入游戏时那道门就没显示"。
        //
        // Scout_02_Lab 上 IsDisabledFromStart 的正是组 3005 那 3 段 IronLab ——
        // 就是被撞开的那道门。它**应该在那儿挡路**, 只是玩家打不破。
        //
        // 原厂给了现成的一对开关 (`-EK/-Yh.cs:640/648`):
        //
        //   _0023Txc()   Defense=1 + NoSpellCollision + 不显示悬停信息 = 打不破
        //   _0023Uxc()   解除
        //
        // <c>HordeSpawner.SpawnBlockage</c> 给挡路路障用的也是这一对
        // (HordeSpawner.cs:181), 所以语义是对得上的。
        //
        // 谁来解除: Ram 撞门那一下。那部分 (精英怪刷新 + 撞击) 还没做,
        // 现在门会一直立在那儿 —— 但**看得见的门**比凭空消失的门离对更近,
        // 而且它本来就该挡路。
        if (spawn.IsDisabledFromStart)
        {
            barricade._0023Txc();
        }

        if (!barricade.IsActive)
        {
            return false;
        }

        // 挂进组, 让同组共享血量。必须在 _0023DZb 之后 —— 见 AttachToGroup 的说明。
        AttachToGroup(barricade, spawn.GroupID, health, groups);

        // 登记在血量设好**之后** —— DestructibleRewards.Track 会记下当前存活状态,
        // 而血量还是 0 的那一瞬间 IsDead 是 true (Destructible.cs:85 `Health <= 0`)。
        // 顺序反了会把整段路障当成"一开始就死的", 打破时不发经验。
        rewards?.Track(barricade);

        return true;
    }

    /// <summary>
    /// 把某个类型的路障全部销毁。
    ///
    /// 教程用它做"发电机爆了 -> 电栅栏断电" (见
    /// <see cref="TutorialDirector"/> 里 CutPowerToElectricBarricades 的说明)。
    /// </summary>
    /// <returns>销毁的段数。</returns>
    public static int DestroyAllOfType(GameManager manager, BarricadeType type)
    {
        if (manager?.ActiveGameObjectList == null)
        {
            return 0;
        }

        int destroyed = 0;
        List<GameObjectBase> objects = manager.ActiveGameObjectList;

        // 倒序 —— Destroy() 把对象排进 DestroyedGameObjectList, 本帧末尾才清理,
        // 但倒序更明确, 以后有人改成立即移除也不会踩坑。
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            if (objects[i] is _0023EK._0023Yh barricade
                && barricade.IsActive
                && barricade._00239Zb == type)
            {
                barricade.Destroy();
                destroyed++;
            }
        }

        return destroyed;
    }

    /// <summary>
    /// 场上还有几段该类型的路障活着。教程状态机用它判断"栅栏破了没有"。
    /// </summary>
    public static int CountAlive(GameManager manager, BarricadeType type)
    {
        if (manager?.ActiveGameObjectList == null)
        {
            return 0;
        }

        int count = 0;
        List<GameObjectBase> objects = manager.ActiveGameObjectList;

        for (int i = 0; i < objects.Count; i++)
        {
            // ⚠️ 判据是**血量**, 不是 IsDead。_0023Yh 重写了 IsDead (-EK/-Yh.cs:105):
            //     public override bool IsDead => BarricadeStatus == _0023Yxc._0023Xxc;
            // 它看状态枚举, 而那个枚举在 UpdateDestructible 里按血量比例**每帧**刷
            // (:498-511)。血刚归零的那一帧 IsDead 还是 false。
            //
            // 真机上下一帧就对了 (所以教程状态机一直工作正常), 但用血量是即时的,
            // 而且单元测试里不跑 manager.Update 时只有血量判据有效。
            if (objects[i] is _0023EK._0023Yh barricade
                && barricade.IsActive && barricade.Health > 0f
                && barricade._00239Zb == type)
            {
                count++;
            }
        }

        return count;
    }
}
