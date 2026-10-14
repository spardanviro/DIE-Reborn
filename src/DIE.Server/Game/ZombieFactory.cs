using System;
using System.Collections.Generic;
using System.Reflection;
using ConductorGameLogic;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.Entities;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 统一的僵尸生成入口。三个刷怪源 (漫游 / 静态摆位 / 事件) 都走这里。
///
/// ## 为什么要抽出来
///
/// 生成一只僵尸有三个**顺序敏感**的步骤, 写错顺序就静默失效:
///
///   1. 位置、朝向、队伍  —— 必须在 Spawn 之前
///   2. <c>_00232L</c> (待生效出场状态) —— 也必须在 Spawn 之前
///   3. <c>SpawnGameObject</c> —— 内部调 <c>Spawn()</c>, 那时才读上面两项
///
/// 第 2 步的机制: <c>NPC.SpawnCharacter()</c> (:363) 把 <c>_00232L</c> 取出来赋给
/// <c>SpawnState_Current</c>, setter 触发 <c>_00233M()</c> 播出场动画, 然后把
/// <c>_00232L</c> 复位。所以它是个"一次性投递箱", 只在 Spawn 那一刻被读一次。
///
/// 原本 <c>_00232L</c> 是 private 且没有任何公开写入口 —— 写它的是服务端代码,
/// 随服务端丢失。可见性由 sanitizer 放开。
/// </summary>
public static class ZombieFactory
{
    /// <summary>
    /// 混淆类名 -> 人类可读的名字。
    ///
    /// 判据是每个类注册的技能名 (技能名没被混淆):
    ///
    ///   _0023rPb  Walker    WalkerRush / WalkerStrikeFast|Normal|Slow
    ///   _0023fPb  Butcher   ButcherCharge / ButcherRavage / ButcherStrike
    ///   _0023iPb  Floater   FloaterSpit / FloaterRush / FloaterStrike
    ///   _0023zPb  Ram       RamCharge / RamStomp / RamStrike
    ///   _0023lPb  Screamer  ScreamerFear / ScreamerSummon / ScreamerStrike
    ///   _0023wPb  Suicider  SuiciderAttack / SuiciderStrike
    ///   _0023mPb  Puller    PullerHook / PullerCrawl / PullerStrike
    ///
    /// 交叉验证: 这七个类在 <c>BelongsToCategoriesPerTypeID</c> 里的分类也对得上 ——
    /// Walker 带 <c>ObjCat.Walker</c>, Puller 带 <c>ObjCat.Puller</c>, 其余带
    /// <c>ObjCat.Specials</c>。
    /// </summary>
    public static readonly (string Obfuscated, string Readable)[] Kinds =
    {
        ("_0023rPb", "Walker"),
        ("_0023fPb", "Butcher"),
        ("_0023iPb", "Floater"),
        ("_0023mPb", "Puller"),
        ("_0023zPb", "Ram"),
        ("_0023lPb", "Screamer"),
        ("_0023wPb", "Suicider"),
    };

    /// <summary>
    /// 僵尸的状态同步间隔 (秒)。
    ///
    /// ## 为什么要改原厂的值
    ///
    /// <c>NPC._0023ry()</c> 设的是 <c>_SyncHelper._00233Ce(0.2f)</c> —— 每 0.2 秒
    /// 同步一次。而死亡走的是**可靠销毁消息**, 立即到。
    ///
    /// 后果 (玩家实测): "最后一击血量没变, 隔了半秒僵尸才死"。因为血条在等
    /// 下一次同步, 而销毁通知已经先到了。
    ///
    /// 另外客户端**不会**为普通僵尸本地调 <c>Die()</c> ——
    /// <c>Character.cs:3413</c> 的条件是
    /// <c>(IsPlayer || IsTutorialNPC || IsReplica || IsGhost) &amp;&amp; IsDead</c>,
    /// 僵尸不在其中。所以它的死亡表现完全依赖服务端消息, 血条更没得预测。
    ///
    /// ## 0.05 -> 0.2 -> 0.05: 中间那次退回是错的
    ///
    /// 0.05 确实修好了血条 —— 玩家确认"血条正常归零了"。我当时因为**死亡动画
    /// 还是晚一拍**就把它退回 0.2, 理由是"那部分和同步间隔无关, 白付带宽"。
    ///
    /// 那是把两件事混成一件了: 血条归零和死亡动画本来就是两条路
    /// (前者走逐帧世界状态, 后者搭在可靠销毁消息上), 修好的那条不该
    /// 因为另一条没修好而回滚。玩家随后再次报告"刷怪的血量显示有时候没有
    /// 正常更新, 但是能正常打死" —— 同一个症状又回来了。
    ///
    /// **教训: 一个改动修好了 A 没修好 B, 不构成回滚 A 的理由。**
    /// 带宽这边也算过账: 场上撑死几十只僵尸, 4 倍频率的代价在私服规模下
    /// 完全无所谓, 而血条是玩家每一秒都在看的东西。
    ///
    /// 死亡动画的服务端时序已经查清, **全在同一帧内**:
    ///
    ///   Character.Die() 末尾 `if (RemoveOnDeath) Destroy();`
    ///   而 NPC.cs:336 `RemoveOnDeath = GameManager.UseNewOptimizedDestroy` = true
    ///   -> 同帧进 DestroyedGameObjectList
    ///   -> GameManager.Update (:1620-1631) 同帧发销毁通知
    ///   -> 帧内顺序: BeforeSimulation -> GameManager.Update(发销毁) -> FlushFrames(世界状态)
    ///
    /// 真机日志也印证: 致死一击到销毁通知只隔 2 毫秒。
    ///
    /// 而死亡动画**搭在销毁消息上** —— `NPC._0023lN` (:913) 在
    /// <c>UseNewOptimizedDestroy</c> 为真时往销毁消息里写 isDead + 击杀者,
    /// 客户端读到才 <c>Die(killer)</c> -> <c>CharacterModel.OnDeath</c>。
    /// 客户端**不会**为普通僵尸本地预测死亡 (Character.cs:3413 的白名单里没有僵尸)。
    ///
    /// 所以剩下那一拍在客户端侧 (动画起手 / 模型切换), 从服务端观测不到。
    /// 要继续查得在客户端装观测点 —— 而那条路本项目已经证明是死路 (4 轮失败)。
    /// </summary>
    public static float SyncInterval { get; set; } = 0.05f;

    /// <summary>
    /// 普通僵尸。<see cref="ConductorGameLogic.MapObjects.Spawn_Static"/> 和
    /// <see cref="ConductorGameLogic.MapObjects.Spawn_EventEntities"/> 都**没有**
    /// 类型字段 —— 前者是场景摆位 (坐着/吃东西的都是 Walker), 后者的类型由所属
    /// 事件的 min/max 决定。所以只有静态摆位需要一个默认值。
    /// </summary>
    public const string DefaultKind = "Walker";

    /// <summary>
    /// 客户端模型缓存的余量。场上同类型留这么多空位再停手。
    ///
    /// 服务端的"这只还活着"和客户端的"模型已经还回池了"之间有延迟 (销毁通知
    /// 要跑一趟网络), 卡着上限刷会在边界上反复越线。
    ///
    /// ⚠️ 扣余量之后**至少要留 1** —— 小上限的类型扣不起: Floater 的
    /// tutorial 缓存是 **1**, 直接减 4 就是 -3, 场上一只没有也会被拦掉。
    /// 那正好把终局那只剧情 Floater 拦死, 状态 22 永远推不动 (单元测试当场抓到)。
    /// 见 <see cref="EffectiveLimit"/>。
    /// </summary>
    public static int ModelCacheHeadroom { get; set; } = 4;

    /// <summary>
    /// 扣掉余量之后真正允许的只数, 下限 1。
    ///
    /// 对 Floater (上限 1) 来说结果正好是 1 —— 和"终局只该有一只剧情 Floater"
    /// 这个设计撞在一起了, 算个意外的好处。
    /// </summary>
    private static int EffectiveLimit(int limit) =>
        limit == int.MaxValue ? int.MaxValue : Math.Max(1, limit - ModelCacheHeadroom);

    /// <summary>
    /// 因为客户端模型缓存放不下而被拦掉的生成次数。诊断用, 宿主读完自行清零。
    /// </summary>
    public static int BlockedByModelCache;

    /// <summary>
    /// 缓存上限按 (类型, 模式) 记 —— 同一个僵尸在不同模式下的上限不一样,
    /// 只按类型缓存会让先跑的那张图的值污染后面的。
    /// </summary>
    private static readonly Dictionary<(Type, GameModeType), int> _modelCacheLimits =
        new Dictionary<(Type, GameModeType), int>();

    /// <summary>
    /// 这个类型在**教程模式**下客户端缓存了几个模型。查不到返回
    /// <see cref="int.MaxValue"/> (不限制)。
    ///
    /// ## 这是原厂数据, 不是我们拍的
    ///
    /// 每个实体类上都挂着 <c>[_0023had(...)]</c>, 而它的构造函数参数名
    /// **没有被混淆** (`-Oq/-had.cs:19`):
    ///
    ///     _0023had(int debug, int scavenger, int resistance,
    ///              int tutorial, int crossroad, int scout)
    ///
    /// Walker (<c>-Mk/-rPb.cs:16</c>) 是 <c>(260, 260, 0, **40**, 0, 0)</c> ——
    /// 教程模式 40 个。Floater/Puller 是 1 个, Butcher 是 0 个。
    ///
    /// ## 超了会怎样
    ///
    /// 客户端 <c>ViewManager.cs:3962</c>:
    ///
    ///     if (stack.Count == 0) {
    ///         Debug.LogWarning("Failed to get model for ... Increase cache count!");
    ///         characterModel = CreateCharacterModel(...);   // ← 现场 Instantiate
    ///     }
    ///
    /// **有兜底**, 所以僵尸不会不渲染 —— 但每一只都要现场实例化一个角色
    /// prefab, 那在 Unity 里很贵。玩家实测的"客户端有很多报错"就是那行
    /// LogWarning 刷屏, 真机日志里连着几十条 <c>Failed to get model for
    /// #Mk.#rPb</c>。
    ///
    /// ## 按模式选栏, 而且字段是**乱序**的
    ///
    /// 构造函数的形参顺序和字段声明顺序对不上 (`-Oq/-had.cs:19-30` 里重新映射):
    ///
    ///     debug      -> _0023gad        tutorial   -> _0023fad
    ///     scavenger  -> _0023bad        crossroad  -> _0023cad
    ///     resistance -> _0023ead        scout      -> _0023dad
    ///
    /// 照声明顺序数第几个字段会全错位。另外 <c>resistance</c> 传 -1 时
    /// 回退成 <c>scavenger</c> 的值 —— 那是原厂的默认规则, 不是我们的。
    ///
    /// ## 0 是"不预缓存", 不是"不许有"
    ///
    /// Walker 是 <c>(260, 260, 0, 40, 0, 0)</c> —— scout 和 resistance 两栏
    /// 都是 0。而 Horde (resistance) 图上显然有 Walker, 所以 0 只能表示
    /// "客户端不为这个模式预留缓存", 超了就走上面那条现场 Instantiate 的兜底。
    ///
    /// 把 0 当上限会**一只都摆不出来**。玩家实测 Starter Mission
    /// "僵尸和教程一样都是提前摆好的", 而第一版只摆出 36 只
    /// (= tutorial 的 40 减去 headroom 4) —— 因为那时不分模式, 全读 tutorial 栏。
    /// </summary>
    public static int ModelCacheLimit(Type type, GameModeType mode)
    {
        if (type == null)
        {
            return int.MaxValue;
        }

        var key = (type, mode);
        if (_modelCacheLimits.TryGetValue(key, out int cached))
        {
            return cached;
        }

        int limit = int.MaxValue;
        object[] attributes = type.GetCustomAttributes(typeof(_0023Oq._0023had), inherit: true);

        if (attributes.Length > 0 && attributes[0] is _0023Oq._0023had attribute)
        {
            int configured = mode switch
            {
                GameModeType.Tutorial => attribute._0023fad,
                GameModeType.ScoutMission => attribute._0023dad,
                GameModeType.Horde => attribute._0023ead,
                GameModeType.EndlessHorde => attribute._0023ead,
                GameModeType.ScavengerHunt => attribute._0023bad,
                GameModeType.ScavengerHuntOld => attribute._0023bad,
                GameModeType.TeamvsTeam => attribute._0023cad,
                _ => attribute._0023gad,          // debug 栏兜底
            };

            // 0 或负数 = 这个模式没配预缓存, 不封顶 (见上面的说明)
            limit = configured > 0 ? configured : int.MaxValue;
        }

        _modelCacheLimits[key] = limit;
        return limit;
    }

    /// <summary>场上这个类型还活着几只。</summary>
    private static int CountAliveOfType(GameManager manager, Type type)
    {
        int count = 0;
        List<GameObjectBase> objects = manager.ActiveGameObjectList;

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is Character { IsActive: true, IsDead: false } character
                && character.GetType() == type)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 生成一只僵尸并放到场上。
    ///
    /// 场上同类型已经顶到客户端的模型缓存上限时**返回 null 不生成** ——
    /// 理由见 <see cref="TutorialModelCacheLimit"/>。
    /// </summary>
    /// <param name="spawnState">
    /// 出场状态。<c>_0023Yc</c> 是"无", 直接站着。
    /// 静态摆位传 <c>Spawn_Static.SpawnState</c>, 事件刷怪传
    /// <c>Spawn_EventEntities.SpawnState</c> —— 两个属性都是原厂的, 不用我们映射。
    /// </param>
    /// <param name="direction">
    /// 朝向。<c>Character.AimDirection</c> 的 setter 只写 <c>_0023Zv</c>,
    /// 没有副作用 (Character.cs:547), 所以生成前设是安全的。
    /// 传 <see cref="Vector2.Zero"/> 表示不指定。
    /// </param>
    /// <returns>成功放到场上的僵尸, 失败返回 null。</returns>
    /// <param name="beforeSpawn">
    /// 取出池对象之后、生成之前改点东西 (比如平民身上多带的基础属性, 见 CrossroadCivilians)。
    /// </param>
    public static Character Spawn(GameManager manager, Type type, Vector2 position,
                                  _0023Oq._0023WM spawnState, Vector2 direction,
                                  Action<Character> beforeSpawn = null)
    {
        if (manager == null || type == null)
        {
            return null;
        }

        if (GameManager.GetGameObjectTypeId(type) < 0)
        {
            return null;
        }

        // 客户端的模型缓存放不下了 —— 再刷就是每只现场 Instantiate + 一条
        // LogWarning。见 TutorialModelCacheLimit。
        int limit = EffectiveLimit(ModelCacheLimit(type, manager?.GameMode?.Type ?? GameModeType.None));
        if (limit != int.MaxValue && CountAliveOfType(manager, type) >= limit)
        {
            BlockedByModelCache++;
            return null;
        }

        GameObjectBase pooled = manager.GetGameObjectFromPool(type);
        if (pooled is not Character zombie || zombie.IsDummyObject)
        {
            return null;
        }

        // ## 复用计数自己加一
        //
        // 服务端好几处拿 (IndexGlobal, NumberOfGlobalCacheUses) 认"是不是同一只" (ZombieBrain 的各张表、
        // 刷怪点的"清完了没有"、Looter / 补给点的波次、击杀归属)。可原厂只在**全局池**那条路上给这个
        // 计数加一 (GameManager.cs:2027), 按类型取池 (就是这里) 从来不加 —— 同一个对象复用十次,
        // 计数一直是 0。后果 (拾荒, 2026-09-23 实测):
        //   - 复用出来的 Looter 继承了上一只的"家", 刷出来就往第一波的刷新点走
        //   - 刷怪点的旧实例被复用成别的怪之后还被当成"还活着", 这个点再也不重刷
        // 原厂代码里这个字段只有那一处写、没有别处读, 所以在这里补上加一不影响原厂逻辑,
        // 只是让它名副其实。
        System.Threading.Interlocked.Increment(ref zombie.NumberOfGlobalCacheUses);

        zombie.Position = position;
        zombie.TeamId = TeamId.Neutral;

        if (direction != Vector2.Zero)
        {
            // 目标朝向也要一起设。玩家实测 (2026-09-14): "僵尸刷出来的时候会出现突然转向的情况。"
            // 状态同步只下发 TargetAimDirection (Character.cs:3195), 当前朝向不发; 回池时它被清成 0
            // (:2386)。只设当前朝向的话客户端收到的是 0 —— 模型没有朝向可对, 出场完脑子第一次瞄人
            // 时才一下子扭过去。
            zombie.AimDirection = direction;
            zombie.TargetAimDirection = direction;
            zombie.LastMoveDirection = direction;
        }

        // 必须在 SpawnGameObject 之前 —— 见类说明。
        if (zombie is NPC npc)
        {
            npc._00232L = EmergeStateFor(type, spawnState);
        }

        beforeSpawn?.Invoke(zombie);

        // 走 GameManager.SpawnGameObject —— 服务端那份实现在 GameManagerServer 里,
        // 它负责接 owner 并调 GameObjectBase.Spawn()。
        manager.SpawnGameObject(zombie, null, null);

        if (!zombie.IsActive)
        {
            return null;
        }

        // 步态必须在 Spawn **之后**补 —— 它要读属性系统里的 Speed。
        EnsureCanMove(zombie);

        MarkElite(zombie);

        // 同步间隔必须在 Spawn **之后**设 —— NPC._0023ry() 是生成时跑的同步配置钩子,
        // 它会把间隔写回 0.2f, 在那之前设等于白设。
        //
        // 走 NetworkManager.GetSyncHelper(SyncHelperIndex) 而不是 _SyncHelper ——
        // 后者是 protected (GameObjectBase.cs:156), 但它本身就是
        // `GameManager.NetworkManager.GetSyncHelper(SyncHelperIndex)` 的转发,
        // 而 SyncHelperIndex 是 public (:142)。同一个对象, 不用改反编译树。
        manager.NetworkManager?.GetSyncHelper(zombie.SyncHelperIndex)?._00233Ce(SyncInterval);

        return zombie;
    }

    /// <summary>
    /// 带词条的僵尸身上的**边缘光** (按词条数)。
    ///
    /// ## 词条数画的是常亮的边缘光
    ///
    /// <c>ZombieModel.Activate</c> (客户端) 读 <c>_0023iO._00231N</c> (词条数, 同步字段 0..7,
    /// -Mk/-iO.cs:136) 给模型加一圈边缘光: 1 = 暗金 (0.51,0.35,0)、2 = 金橙 (0.82,0.43,0)、
    /// 3 = 红。**全代码树没有任何一处给它赋值** —— 又是服务端丢了的那一半。
    /// 只在模型激活那一下读一次, 之后改它客户端不会变 —— 所以它画不了"放技能时才亮"。
    ///
    /// ## 精英不再默认发光 (2026-09-14)
    ///
    /// 9-13 玩家说"精英怪使用技能时会有霸体, 无法被打断, 身上发金光", 我读成了"精英常亮", 给所有精英
    /// 至少 1。玩家更正: "精英怪只有在使用技能时才发金光, 代表霸体效果, 平常不发金光。"
    /// 现在只按真实词条数给 (具名目标由 <see cref="CrossroadTargetSpawner"/> 按挂上的词条数传进来)。
    /// 放技能时那道光不是这个字段画的 (见上), 霸体本身在 <c>ZombieBrain.EliteCastArmor</c>。
    ///
    /// 必须在生成**之后**、同一帧里设: <c>ResetNPC</c> 会把它清零 (-Mk/-iO.cs:88),
    /// 而客户端在第一次收到这只怪的时候才 Activate 读它 —— 同一帧设上就赶得上。
    /// </summary>
    public static void MarkElite(Character zombie, int attributes = 0)
    {
        if (zombie is not _0023Mk._0023iO body)
        {
            return;
        }

        body._00231N = Math.Min(3, Math.Max(body._00231N, Math.Max(0, attributes)));
    }

    /// <summary>
    /// 这种僵尸**有没有**这个出场动画 —— 没有就换一个它有的。
    ///
    /// ## 玩家实测 (2026-09-13)
    ///
    /// "很多刷出来的僵尸还是凭空出现的, 应该从刷怪点爬出来, 或者地里爬出来。"
    ///
    /// ## 刷怪点是带动画的, 但不是每种怪都会那个动画
    ///
    /// 十字路口 map26 的 390 个刷怪点**全都**带出场动画 (跳下屋顶 83、跳下 91、爬行 71、
    /// 跃出 61、挖地 58 ...)。可是每种怪自己的出场动画表 (<c>NPC._SpawnStateAnimation[类型]</c>)
    /// 差得很远, 实测 (原厂判据 <c>NPC.TypeSupportsSpawnState</c>):
    ///
    ///   行尸 / Runner / Veteran / Spitter   除"跃出"以外全都会
    ///   Looter                            挖地、跳下、踉跄
    ///   屠夫 / 尖叫者 / 自爆者 / 屠夫精英   跃出、爬行
    ///   Puller / Puller 精英               只会爬行
    ///   飘浮者 / 公羊 / 各囤积者 / 各巨型    只会跃出
    ///
    /// 客户端播动画时查不到 (<c>NPC._00233M</c> -&gt; <c>GetSpawnStateAnimation</c> 返回 null)
    /// 就什么都不放 —— 怪原地冒出来。特殊怪大半落在挖地/跳下屋顶的点上, 行尸落在跃出点上,
    /// 于是"很多刷出来的僵尸还是凭空出现"。
    ///
    /// ## 换哪个
    ///
    /// "没有出场动画" (<c>_0023Yc</c>) 原样放行 —— 那是开局就站好的静态摆放要的。
    /// 其余查不到的按 挖地 -&gt; 爬行 -&gt; 跃出 -&gt; 跳下 -&gt; 踉跄 挑它会的第一个:
    /// 前两个是"从地里/地上出来", 最贴近玩家说的样子。
    /// </summary>
    public static _0023Oq._0023WM EmergeStateFor(Type type, _0023Oq._0023WM requested)
    {
        if (requested == _0023Oq._0023WM._0023Yc || type == null)
        {
            return requested;
        }

        try
        {
            if (NPC.TypeSupportsSpawnState(type, requested))
            {
                return requested;
            }

            foreach (_0023Oq._0023WM fallback in EmergeFallbacks)
            {
                if (NPC.TypeSupportsSpawnState(type, fallback))
                {
                    return fallback;
                }
            }
        }
        catch (KeyNotFoundException)
        {
            // 这个类型压根没有动画表 (不是僵尸) —— 原样放行
        }

        return requested;
    }

    /// <summary>
    /// 从一串刷怪点里优先挑**这种怪会那个出场动画**的 —— 这样动画和地形也对得上
    /// (跳下屋顶的点刷行尸, 跃出的点刷公羊)。一个都没有就原样返回, 由
    /// <see cref="EmergeStateFor"/> 换一个它会的动画兜底。
    /// </summary>
    public static IReadOnlyList<ConductorGameLogic.MapObjects.Spawn_EventEntities> PreferAnimated(
        Type type, IReadOnlyList<ConductorGameLogic.MapObjects.Spawn_EventEntities> points)
    {
        if (type == null || points == null || points.Count == 0)
        {
            return points;
        }

        List<ConductorGameLogic.MapObjects.Spawn_EventEntities> fitting =
            new List<ConductorGameLogic.MapObjects.Spawn_EventEntities>();

        try
        {
            foreach (ConductorGameLogic.MapObjects.Spawn_EventEntities point in points)
            {
                if (NPC.TypeSupportsSpawnState(type, point.SpawnState))
                {
                    fitting.Add(point);
                }
            }
        }
        catch (KeyNotFoundException)
        {
            return points;
        }

        return fitting.Count > 0 ? fitting : points;
    }

    /// <summary>出场动画的替补顺序, 见 <see cref="EmergeStateFor"/>。</summary>
    private static readonly _0023Oq._0023WM[] EmergeFallbacks =
    {
        _0023Oq._0023WM._0023eCc,   // 挖地
        _0023Oq._0023WM._0023cCc,   // 爬行
        _0023Oq._0023WM._0023Llc,   // 跃出
        _0023Oq._0023WM._0023Oed,   // 跳下
        _0023Oq._0023WM._0023Ped,   // 踉跄
    };

    /// <summary>
    /// 加速度为 0 的僵尸 —— 补上步态参数, 否则它一辈子挪不了一步。
    ///
    /// ## 症状
    ///
    /// 玩家实测: "BOSS Looter 无法移动, 虽然显示在走动, 但是位置停在原地,
    /// 且无法攻击。"
    ///
    /// 诊断测试把每一环的中间量打出来后, 断点一眼可见:
    ///
    ///   GoalTarget    有 (脑子在工作)
    ///   MoveDirection (0.978, 0.208) 朝着玩家 (意图写进去了)
    ///   Speed         20 (属性系统正常)
    ///   Velocity      **恒为 0**   ← 断在这里
    ///   Position      纹丝不动
    ///
    /// <c>Character</c> 的积分式是
    /// <c>Velocity += MoveDirection * _Acceleration * deltaTime</c>
    /// (Character.cs:2606)。方向有、速度上限有, 那只能是**加速度是 0**。
    ///
    /// ## 为什么会是 0
    ///
    /// 僵尸的加速度/摩擦/速度因子存在 <c>_0023ON.._0023TN</c> 六个字段里,
    /// 由 <c>_0023vO(步态)</c> 从 <c>_0023JGc</c> 那张表查出来填。
    /// <c>ResetNPC</c> (-Mk/-iO.cs:78) 每次从池里取出时做
    /// <c>_Acceleration = _0023ON</c> —— 拷的是这几个字段。
    ///
    /// Walker 在 <c>_00236N</c> 末尾自己调了 <c>_0023vO(_0023J3)</c>
    /// (-Mk/-rPb.cs:103), 所以它有值。**Looter 没有** —— 它的 <c>_00237N</c>
    /// 只写了 <c>_0023J3 = _0023HGc._0023Xb</c> (-Mk/-nPb.cs:99), 光记下步态
    /// 却不查表。全代码树里 Looter 的 <c>_0023vO</c> 只被反序列化
    /// (<c>_0023cO</c>) 调用过, **服务端那个调用点随服务端丢失了**。
    ///
    /// 这不是 Looter 独有的形状: 好几个 NPC 类都把"设步态"留给了服务端 AI
    /// (Hoarder 类游荡时走、被发现时跑, 本来就该动态切)。所以这里不点名修
    /// Looter, 而是**按症状修** —— 谁的加速度是 0 就给谁补一档默认走路。
    ///
    /// ## 判据为什么用 _Acceleration == 0
    ///
    /// 它精确对应"这只怪根本动不了", 不多不少:
    ///
    ///   - Walker 这类已经有值的**一律不碰**, 手感是真机校准过的, 零风险
    ///   - 漏掉的那些补上, 而且补的是原厂自己那张表里的值, 不是我们拍的
    ///
    /// 换成"按类型列一张白名单"就会随版本漂移, 而且我没法保证列全。
    ///
    /// ## 为什么要调两个方法
    ///
    /// <c>_0023vO</c> 只填 <c>_0023ON.._0023TN</c> 六个字段, **不碰**
    /// <c>_Acceleration</c>。而 <c>_Acceleration</c> 是在 <c>ResetNPC</c>
    /// (已经跑完了) 里拷贝的, 所以还要一个 <c>_0023dO()</c> —— 它是原厂的
    /// "切到走路档", 把 <c>_Acceleration / _Friction / _TargetSpeedFactor</c>
    /// 三个都刷新 (-Mk/-iO.cs:194)。它读 <c>Stats</c> 里的 Speed, 所以必须在
    /// <c>Spawn()</c> 之后调。
    ///
    /// 传的步态是枚举的 0 档。<c>_0023JGc</c> 那张表 (-Mk/-JGc.cs) 三档的值
    /// **完全一样** (走: 加速 100 / 摩擦 50 / 速度因子 7), 所以挑哪档不影响结果。
    /// </summary>
    public static void EnsureCanMove(Character zombie)
    {
        if (zombie is not NPC npc || AccelerationField == null)
        {
            return;
        }

        if (AccelerationField.GetValue(npc) is not float acceleration || acceleration != 0f)
        {
            return;   // 已经有加速度了 —— 别碰
        }

        Type type = npc.GetType();

        if (!_gaitSetters.TryGetValue(type, out MethodInfo setGait))
        {
            setGait = FindGaitSetter(type);
            _gaitSetters[type] = setGait;
        }

        if (setGait == null)
        {
            return;   // 这个类根本没有步态方法, 不动它
        }

        Type gaitType = setGait.GetParameters()[0].ParameterType;
        setGait.Invoke(npc, new[] { Enum.ToObject(gaitType, 0) });

        // 把刚填好的字段刷进 _Acceleration / _Friction / _TargetSpeedFactor
        WalkGaitMethod?.Invoke(npc, Array.Empty<object>());

        GaitsRepaired++;
    }

    /// <summary>补过几只的步态。诊断用, 宿主打日志。</summary>
    public static int GaitsRepaired;

    /// <summary>
    /// 找类型上的 <c>_0023vO(某个枚举)</c>。
    ///
    /// 按**形状**找而不是按名字硬匹配参数类型: 每个 NPC 类的步态枚举都不一样
    /// (<c>_0023HGc</c> / <c>_0023Duc</c> / <c>_00232S</c> ...), 唯一的共性是
    /// "一个枚举参数、无返回值"。
    /// </summary>
    private static MethodInfo FindGaitSetter(Type type)
    {
        foreach (MethodInfo candidate in type.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (candidate.Name != "_0023vO" || candidate.ReturnType != typeof(void))
            {
                continue;
            }

            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum)
            {
                return candidate;
            }
        }

        return null;
    }

    private static readonly Dictionary<Type, MethodInfo> _gaitSetters =
        new Dictionary<Type, MethodInfo>();

    private static readonly FieldInfo AccelerationField = typeof(Character)
        .GetField("_Acceleration", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>僵尸基类的"切到走路档"。</summary>
    private static readonly MethodInfo WalkGaitMethod = typeof(_0023Mk._0023iO)
        .GetMethod("_0023dO", BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// 从 <c>GameObjectTypeIDToType</c> 里按混淆名找出那七个类。
    ///
    /// 不硬编码 typeId —— 它是按类型注册顺序算出来的, 换个版本就变。类名虽然
    /// 也是混淆产物, 但它随程序集固定, 而且我们有技能名作为交叉验证。
    /// </summary>
    public static Dictionary<string, Type> ResolveKindTypes()
    {
        Dictionary<string, Type> found = new Dictionary<string, Type>();

        foreach (KeyValuePair<int, Type> entry in GameManager.GameObjectTypeIDToType)
        {
            if (entry.Value == null)
            {
                continue;
            }

            foreach ((string obfuscated, string _) in Kinds)
            {
                if (entry.Value.Name == obfuscated)
                {
                    found[obfuscated] = entry.Value;
                }
            }
        }

        return found;
    }

    /// <summary>人类可读名 -> 混淆类名。找不到返回 null。</summary>
    public static string ObfuscatedNameFor(string readable)
    {
        foreach ((string obfuscated, string name) in Kinds)
        {
            if (name == readable)
            {
                return obfuscated;
            }
        }

        return null;
    }

    /// <summary>
    /// 基础特殊僵尸 -&gt; 精英版。地图上刷怪点 / 事件的 <c>Elites</c> 勾上时, 特殊僵尸换成这一栏。
    ///
    /// 判据: 带 <c>ObjCat.Elites</c> 的正好这五个类 (-Mk/-hPb、-jPb、-kPb、-oPb、-yPb), 悬停名和基础款相同
    /// (Butcher / Siren = 尖叫者 / Puller / Floater / Ram), 技能栏只差基础攻击的名字 (见 <see cref="ZombieSpecials"/>)。
    /// 行尸和自爆者没有精英版, 照旧。Runner / Veteran / Spitter 是 <c>ObjCat.Infected</c>, 不是精英。
    /// </summary>
    public static readonly (string Readable, string EliteObfuscated)[] EliteKinds =
    {
        ("Butcher", "_0023hPb"),
        ("Floater", "_0023oPb"),
        ("Puller", "_0023kPb"),
        ("Ram", "_0023yPb"),
        ("Screamer", "_0023jPb"),
    };

    /// <summary>某种基础僵尸的精英版类型; 没有精英版返回 null。</summary>
    public static Type EliteTypeFor(string readable)
    {
        foreach ((string name, string elite) in EliteKinds)
        {
            if (name == readable)
            {
                return CrossroadEventCatalog.ResolveClass(elite);
            }
        }

        return null;
    }

    /// <summary>
    /// 精英的等级: 原厂 <c>GameManager.CheckIngameLevelForEliteSpawn</c> —— 感染等级低于 <c>_0023sub</c> (4) 时 1 级,
    /// 否则全场玩家平均等级 - 1 (封顶 15); 拾荒入夜后直接 15。客户端树里零调用方, 是给服务端刷精英用的。
    /// 当 <see cref="Spawn"/> 的 beforeSpawn 传 (出池时 <c>ResetCharacter</c> 已经清过 <c>CustomLevel</c>)。
    /// </summary>
    public static void ApplyEliteLevel(Character zombie)
    {
        if (zombie is NPC npc && zombie.GameManager != null)
        {
            npc.SetInGameLevel(zombie.GameManager.CheckIngameLevelForEliteSpawn());
        }
    }
}
