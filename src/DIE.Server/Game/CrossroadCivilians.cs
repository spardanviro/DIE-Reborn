using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.MapObjects;
using ConductorGameLogic.Stats;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 十字路口的两类平民任务: **解救被 Goo 茧困住的平民** 和 **护送平民回基地**。
///
/// ## 玩家实测 (2026-09-13)
///
/// "剩下的任务还有拯救幸存者, 消灭病毒孢子等, 任务类型随机, 应该有个任务类型池, 随机抽取。"
///
/// ## 两样东西原厂都有, 而且十字路口的对象池里都有名额
///
///   平民       <c>_0023Mk._00237Y</c> (<c>GameObjectBase.IsCivilian</c>, 悬停名
///              "Civilian"), 十字路口模型缓存 16
///   Goo 茧     <c>_0023EK._0023iL</c> —— 悬停名 "PCStrings.GUI.Hover.GooCocoon",
///              8000 血, 掉到 80% 自己碎 (<c>_0023fL = 0.8</c>, -EK/-iL.cs), 碎的时候放
///              <c>Vfx_Event_CocoonOpenStash_SaveTheCivilian_Destory</c> —— 名字就写着
///              "打开茧, 救出平民"。玩家说的"消灭病毒孢子"就是它。
///
/// 客户端文案 (sharedassets0.assets):
///   FreeCivilians        "Free the civilians from their constraints, before they turn!"
///   FreeCiviliansReached "Get to the constrained civilians and help them!"
///   EscortCivilian       "Escort the civilians to Crossroads."
///
/// ## 解救 (FreeCivilians)
///
/// 在事件区域摆几个茧 -&gt; 玩家把茧打碎 -&gt; 平民从碎掉的地方出来, **跟着玩家走** (导航网格
/// 寻路, 同 Amber, 见 <see cref="NpcFollower"/>) -&gt; 带到基地 -&gt; 平民身上掉补给。
/// 救出来的都送到 (死了的不算) = 完成, 按送到人数评分。时限只管茧 ("before they turn"):
/// 到点还没打开的茧收走, 已经救出来的接着带; 一个都没送到才算失败。
/// **平民在茧里的时候不生成** —— 不然僵尸会去咬一个动不了、也碰不到的目标。
///
/// 玩家实测 (2026-09-13): "打破茧营救到幸存者后, 幸存者应该跟着玩家移动, 类似于 tutorial 里面
/// 的 amber 的自动跟随和自动寻路, 玩家把幸存者带到基地后, 幸存者身上掉落物资。" —— 上一版平民
/// 出茧就自己往基地跑, 茧全碎就算完成。
///
/// **完成判据是茧, 不是"送回基地"** (2026-09-22 改回原厂)。玩家报"从茧里解救幸存者和
/// 单纯解救幸存者的任务内容有重复" —— 上一版要求把救出来的人带回基地才算数, 于是后半段
/// 和护送任务一模一样。原厂两条文案分得很清楚: 解救是
/// "Free the civilians from their constraints, <b>before they turn!</b>" (就地救, 抢在变异前),
/// 护送才是 "Escort the civilians <b>to Crossroads</b>."。刷怪表也印证: 解救的目标是
/// <c>Delay</c> 定时 (30/60/120/150/180 秒, 守着茧打), 护送的全是 <c>OnlyForced</c>
/// (5/25/40 秒, 边走边被逼出来)。
///
/// 救出来的人照样跟着玩家走, 走到基地照样掉补给 —— 那是**额外**的, 不是完成条件。
///
/// ## 护送 (EscortCivilian)
///
/// 在事件区域刷几个平民 -&gt; 离玩家够近就跟着最近的玩家走 -&gt; 走到基地 (护送终点
/// <c>Trigger_EscortGoal</c>) 算送到。全部活着的都送到了 = 完成; 一个都没活下来 = 失败。
/// 僵尸会咬平民 (见 <see cref="ZombieBrain"/> 的平民仇恨), 所以护送要真的护。
///
/// ## 我们定的数
///
/// 几个茧 / 几个平民 (<see cref="CocoonCount"/> / <see cref="EscortCount"/>)、跟随距离、
/// 送到的判定半径 —— 原厂数据里都没有 (那一半在服务端)。茧的血量和碎裂比例是原厂的。
/// </summary>
public sealed class CrossroadCivilians
{
    private sealed class Cocoon
    {
        public Destructible Shell;
        public Vector2 Position;
        public bool Opened;

        /// <summary>摆茧时的对象池使用次数 —— 查"谁打碎的"要对上它。</summary>
        public int Uses;

        /// <summary>时限到了被收走的 (不是打开的) —— 不出平民。</summary>
        public bool Expired;
    }

    private sealed class Civilian
    {
        public Character Body;

        /// <summary>跟着谁 (救出他的 / 最近的活玩家)。null = 还没人来接。</summary>
        public Player Leader;

        /// <summary>送到了 (或者死了) —— 不再算"还要带"。</summary>
        public bool Delivered;

        /// <summary>送到之后过了多久 (到点收走)。</summary>
        public float Leaving;
    }

    private readonly List<Cocoon> _cocoons = new List<Cocoon>();
    private readonly List<Civilian> _civilians = new List<Civilian>();
    private readonly NpcFollower _follower = new NpcFollower();
    private Vector2 _home;
    private bool _escort;
    private bool _cocoonsExpired;

    /// <summary>解救任务摆几个茧。宿主按原厂人数设 (<c>MedalData.Start</c>)。</summary>
    public int CocoonCount { get; set; } = 3;

    /// <summary>护送任务刷几个平民。宿主按原厂人数设。</summary>
    public int EscortCount { get; set; } = 2;

    /// <summary>
    /// 护送的平民离玩家多近才开始跟 (第一次接上之前原地等, 不自己乱跑进怪堆)。
    /// 跟的人死了/掉线了, 也是在这个范围里找下一个。**我们定的。**
    /// </summary>
    public float FollowRadius { get; set; } = 110f;

    /// <summary>离基地 (护送终点) 多近算送到。</summary>
    public float DeliverRadius { get; set; } = 60f;

    /// <summary>
    /// 救出来的平民身边这么近有活僵尸就蹲下不走, 清完了才跟。
    ///
    /// 玩家实测 (2026-09-14): "幸存者不应该一上来就跟着玩家跑, 附近有僵尸时幸存者会蹲在地上,
    /// 把僵尸清完后才会跟着玩家走。"
    ///
    /// 蹲下的动作客户端现成: <c>CharacterModel.UpdateCivilian</c> 读平民的 <c>_00230Y</c>
    /// (同步字段, SerializeNPC 第一个 bool) 驱动动画参数 "Panic" —— 全树零赋值, 又是服务端丢的那半。
    /// **半径是我们定的** (原厂数值没留下)。
    /// </summary>
    public float CowerRadius { get; set; } = 80f;

    /// <summary>诊断: 现在有几个平民蹲着。</summary>
    public int Cowering { get; private set; }

    /// <summary>送到之后多久把平民收走 (在基地里站一会儿)。</summary>
    public float LeaveSeconds { get; set; } = 8f;

    /// <summary>
    /// 送到一个平民掉几包补给、每包多少。
    ///
    /// 玩家实测 (2026-09-13): "玩家把幸存者带到基地后, 幸存者身上掉落物资。"
    /// 抛撒器用的是占领补给点那个 (<see cref="SupplyThrower"/>), 包数**我们定的** ——
    /// 占领一个点抛 10 包, 一个平民给 3 包。
    /// </summary>
    public int CratesPerCivilian { get; set; } = 3;

    /// <inheritdoc cref="CratesPerCivilian"/>
    public int SuppliesPerCrate { get; set; } = SupplyThrower.DefaultSuppliesPerCrate;

    /// <summary>一共几个 (茧或平民)。</summary>
    public int Total { get; private set; }

    /// <summary>茧打开了几个 (解救)。</summary>
    public int Freed { get; private set; }

    /// <summary>送到基地几个。评分就按它。</summary>
    public int Saved { get; private set; }

    /// <summary>死了几个。</summary>
    public int Lost { get; private set; }

    /// <summary>送到之后掉了几次补给 (诊断/测试)。</summary>
    public int SupplyDrops { get; private set; }

    /// <summary>
    /// 茧这一段结束了 (全打开了, 或者时限到了剩下的变异了)。护送任务一开始就是 true。
    /// </summary>
    public bool CocoonsDone => _escort || _cocoonsExpired || _cocoons.All(c => c.Opened || c.Expired);

    /// <summary>这个任务做完了: 该带的都送到了 (死了的不算), 至少送到一个。</summary>
    public bool Completed { get; private set; }

    /// <summary>这个任务没救了: 该带的一个都没送到。</summary>
    public bool Failed { get; private set; }

    /// <summary>
    /// 这个任务记几分 —— 解救看**救出**几个, 护送看**送到**几个。
    /// 原厂奖牌线就是人数 (解救 4/2/1、护送 3/2/1)。
    /// </summary>
    public int Scored => _escort ? Saved : Freed;

    /// <summary>0..1, 给目标 UI。</summary>
    public float Progress => Total == 0 ? 0f : (float)Scored / Total;

    /// <summary>还有正跟着人走、没送到的平民。</summary>
    public bool AnyEnRoute => _civilians.Any(c => !c.Delivered && Alive(c.Body));

    /// <summary>护送中的平民在哪 (取第一个活着没送到的) —— 护送途中逼出来的怪冲着这里来。</summary>
    public Vector2? EscortAnchor =>
        _civilians.Where(c => !c.Delivered && Alive(c.Body)).Select(c => (Vector2?)c.Body.Position)
            .FirstOrDefault();

    /// <summary>这个平民现在跟着谁 (测试/诊断)。</summary>
    public Player LeaderOf(Character body) => _civilians.FirstOrDefault(c => c.Body == body)?.Leader;

    /// <summary>
    /// 本帧从没打开的茧里爬出来的僵尸 —— 交给宿主激怒 (它们是冲着队伍来的)。
    /// 见 <see cref="ExpireCocoons"/>。宿主读完自己清。
    /// </summary>
    public readonly List<Character> TurnedThisTick = new List<Character>();

    /// <summary>宿主读完打日志再清。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>
    /// 解救: 在区域里摆茧。<paramref name="spawns"/> 离中心近的在前。
    /// </summary>
    public void ArmFree(GameManager simulation, IReadOnlyList<Spawn_EventEntities> spawns,
                        Vector2 center, Vector2 home)
    {
        Reset(home, escort: false);

        foreach (Vector2 at in Spots(simulation, spawns, center, home, CocoonCount))
        {
            _0023EK._0023iL shell = simulation.GetGameObjectFromPool<_0023EK._0023iL>();
            if (shell == null || shell.IsDummyObject)
            {
                Events.Add("十字路口解救: Goo 茧没拿到 (池空?)");
                continue;
            }

            shell.Position = at;
            shell.TeamId = TeamId.Neutral;      // 玩家 (Team1) 打得动它
            simulation.SpawnGameObject(shell, null, null);

            _cocoons.Add(new Cocoon { Shell = shell, Position = at, Uses = shell.NumberOfGlobalCacheUses });
        }

        Total = _cocoons.Count;
        Events.Add($"十字路口解救: 摆了 {Total} 个 Goo 茧 (打碎 = 救出一个; 茧全处理完就结算)");

        if (Total == 0)
        {
            Completed = true;       // 一个都摆不出来就别卡死流程
        }
    }

    /// <summary>
    /// 护送: 在区域里刷平民。<paramref name="home"/> 是护送终点。
    /// </summary>
    public void ArmEscort(GameManager simulation, IReadOnlyList<Spawn_EventEntities> spawns,
                          Vector2 center, Vector2 home)
    {
        Reset(home, escort: true);

        foreach (Vector2 at in Spots(simulation, spawns, center, home, EscortCount))
        {
            Character body = SpawnCivilian(simulation, at);
            if (body != null)
            {
                _civilians.Add(new Civilian { Body = body });
            }
        }

        Total = _civilians.Count;
        Events.Add($"十字路口护送: 刷了 {Total} 个平民, 送回 ({home.X:F0},{home.Y:F0})");

        if (Total == 0)
        {
            Completed = true;
        }
    }

    /// <summary>每帧一次。</summary>
    public void Update(GameManager simulation, float deltaTime)
    {
        if (simulation != null && !Failed)
        {
            if (!_escort && !Completed)
            {
                OpenCocoons(simulation);
            }

            // 解救任务在茧处理完那一刻就算完成, 但救出来的人还在路上 —— 让他们接着走,
            // 走到基地照样掉补给 (额外的, 不影响已经定下的评分)。
            UpdateCivilians(simulation, deltaTime);
            Resolve();
        }

        TidyUp(deltaTime);
    }

    /// <summary>
    /// 时限到了, 茧还没打完 —— 原厂文案写的就是这一刻: <b>"before they turn!"</b>。
    /// 没救出来的人**变成僵尸**, 从茧的位置爬出来扑向队伍。
    ///
    /// ## 这一条是我们加的
    ///
    /// 原厂只留下文案和"刷怪表一直排到 180 秒"这两条旁证, 变异那一步的实现在服务端丢了。
    /// 上一版是把没打开的茧静静收走 (玩家看到的就是"茧自己爆炸了") —— 文案落了空,
    /// 救人也就没有了紧迫感。玩家 2026-09-22 定: 变僵尸。
    ///
    /// 一个都没救出来就是失败; 救出过人的, 已经在路上的照样往基地走。
    /// </summary>
    public void ExpireCocoons(GameManager simulation)
    {
        if (_escort || _cocoonsExpired)
        {
            return;
        }

        _cocoonsExpired = true;
        int turned = 0;

        foreach (Cocoon cocoon in _cocoons)
        {
            if (cocoon.Opened)
            {
                continue;
            }

            cocoon.Expired = true;

            if (cocoon.Shell == null || !cocoon.Shell.IsActive)
            {
                continue;
            }

            Vector2 at = cocoon.Position;
            cocoon.Shell.Destroy();     // 茧破开 (客户端那个碎裂特效)

            Character risen = Turn(simulation, at);
            if (risen != null)
            {
                TurnedThisTick.Add(risen);
                turned++;
            }
        }

        Events.Add($"十字路口解救: 时限到, {turned} 个没救出来的平民变成了僵尸; 已救出 {Freed} 个, "
                   + $"在路上 {_civilians.Count(c => !c.Delivered && Alive(c.Body))} 个");
        Resolve();
    }

    /// <summary>茧里的人变异 —— 在茧的位置放一只行尸。放不出来 (池空/缓存满) 就算了, 不卡流程。</summary>
    private Character Turn(GameManager simulation, Vector2 at)
    {
        if (simulation == null)
        {
            return null;
        }

        Type walker = CrossroadEventCatalog.ResolveClass(ZombieFactory.ObfuscatedNameFor("Walker"));
        if (walker == null)
        {
            return null;
        }

        return ZombieFactory.Spawn(simulation, walker, at, _0023Oq._0023WM._0023eCc, Vector2.UnitX);
    }

    /// <summary>任务结束 (失败/收尾): 茧和平民都收走。</summary>
    public void Disarm()
    {
        foreach (Cocoon cocoon in _cocoons)
        {
            if (!cocoon.Opened && cocoon.Shell != null && cocoon.Shell.IsActive)
            {
                cocoon.Shell.Destroy();
            }
        }

        foreach (Civilian civilian in _civilians)
        {
            if (Alive(civilian.Body))
            {
                civilian.Body.Destroy();
            }
        }

        _cocoons.Clear();
        _civilians.Clear();
        _follower.Clear();
    }

    /// <summary>
    /// 茧碎了 -&gt; 平民从那里出来, **跟上打碎茧的那个玩家** (他死了/掉线了才跟最近的活玩家)。
    ///
    /// 玩家实测 (2026-09-13): "茧里面救出的幸存者会跟着打破茧的那个玩家走。" 上一版跟的是"离茧最近的"。
    /// </summary>
    private void OpenCocoons(GameManager simulation)
    {
        foreach (Cocoon cocoon in _cocoons)
        {
            if (cocoon.Opened || cocoon.Expired || (cocoon.Shell.IsActive && !cocoon.Shell.IsDead))
            {
                continue;
            }

            cocoon.Opened = true;
            Freed++;

            Character body = SpawnCivilian(simulation, _follower.FindFreeSpot(simulation, cocoon.Position));
            if (body != null)
            {
                Player breaker = (simulation as GameManagerServer)?.LastPlayerToHit(cocoon.Shell, cocoon.Uses);
                bool breakerAlive = breaker != null && breaker.IsActive && !breaker.IsDead;

                _civilians.Add(new Civilian
                {
                    Body = body,
                    Leader = breakerAlive ? breaker : NearestLivingPlayer(simulation, cocoon.Position, float.MaxValue),
                });

                if (breakerAlive)
                {
                    Events.Add($"十字路口解救: 这个平民跟打碎茧的玩家 #{breaker.IndexPlayer}");
                }
            }

            Events.Add($"十字路口解救: 救出一个平民 ({Freed}/{Total}) @ ({body?.Position.X:F0},{body?.Position.Y:F0}), 茧在 "
                       + $"({cocoon.Position.X:F0},{cocoon.Position.Y:F0}), 跟着玩家走");
        }
    }

    private void UpdateCivilians(GameManager simulation, float deltaTime)
    {
        Cowering = 0;

        foreach (Civilian civilian in _civilians)
        {
            if (civilian.Delivered)
            {
                continue;
            }

            if (!Alive(civilian.Body))
            {
                civilian.Delivered = true;      // 从"还要带"里拿掉
                Lost++;
                _follower.Forget(civilian.Body as NPC);
                Events.Add($"十字路口平民: 一个平民死了 (死 {Lost}, 送到 {Saved}/{Total})");
                continue;
            }

            if (Vector2.Distance(civilian.Body.Position, _home) <= DeliverRadius)
            {
                Deliver(simulation, civilian);
                continue;
            }

            if (civilian.Body is not NPC npc)
            {
                continue;
            }

            // 附近有僵尸就蹲着等, 清完再走 —— 救出来的和护送的都一样。
            // 玩家实测 (2026-09-15): "只有三个幸存者的营救任务, 幸存者碰到僵尸还不会蹲下不动。" 以前护送的不走这条。
            if (Cower(simulation, npc))
            {
                Cowering++;
                _follower.Forget(npc);
                NpcFollower.Stop(npc);
                continue;
            }

            // 跟的人还在就接着跟; 不在了 (死了/掉线) 就在附近换一个。
            // 救出来的平民 Leader 一开始就有; 护送的等玩家走近 FollowRadius 才接上。
            if (civilian.Leader == null || !civilian.Leader.IsActive || civilian.Leader.IsDead)
            {
                civilian.Leader = NearestLivingPlayer(simulation, npc.Position, FollowRadius);
            }

            if (civilian.Leader == null)
            {
                NpcFollower.Stop(npc);
                continue;
            }

            _follower.Step(simulation, npc, civilian.Leader.Position, deltaTime);
        }
    }

    /// <summary>送到了: 计数、掉补给、在基地里站一会儿再收走。</summary>
    private void Deliver(GameManager simulation, Civilian civilian)
    {
        civilian.Delivered = true;
        Saved++;
        SetPanic(civilian.Body, false);

        if (civilian.Body is NPC npc)
        {
            _follower.Forget(npc);
            NpcFollower.Stop(npc);
        }

        bool thrown = CratesPerCivilian > 0
                      && SupplyThrower.Throw(simulation, civilian.Body.Position, CratesPerCivilian,
                          SuppliesPerCrate, scatter: 20f);
        if (thrown)
        {
            SupplyDrops++;
        }

        Events.Add($"十字路口平民: 送到基地一个 ({Saved}/{Total})"
                   + (thrown ? $", 掉了 {CratesPerCivilian} 包补给" : ", 抛撒器没拿到 (池空?)"));
    }

    /// <summary>
    /// 结算。两类任务判据不同 —— 这正是它们的区别所在:
    ///
    ///   解救  茧全处理完 (打碎救出 / 时限到变异) 就结束, 救出过人 = 完成。
    ///   护送  没人在路上了 (送到或死了) 才结束, 送到过人 = 完成。
    /// </summary>
    private void Resolve()
    {
        if (Completed || Failed)
        {
            return;
        }

        if (_escort)
        {
            if (AnyEnRoute)
            {
                return;
            }

            Completed = Saved > 0;
            Failed = !Completed;
            return;
        }

        if (!CocoonsDone)
        {
            return;
        }

        Completed = Freed > 0;
        Failed = !Completed;
    }

    /// <summary>离 <paramref name="position"/> 最近、在 <paramref name="radius"/> 以内的活玩家。</summary>
    /// <summary>身边 <see cref="CowerRadius"/> 内有活僵尸吗 —— 有就挂上惊慌 (蹲下), 没有就摘掉。</summary>
    private bool Cower(GameManager simulation, NPC civilian)
    {
        float radiusSquared = CowerRadius * CowerRadius;
        bool scared = false;

        List<GameObjectBase> objects = simulation.ActiveGameObjectList;
        for (int i = 0; i < objects.Count && !scared; i++)
        {
            scared = objects[i] is Character zombie && zombie.IsZombie && zombie.IsActive && !zombie.IsDead
                     && Vector2.DistanceSquared(zombie.Position, civilian.Position) <= radiusSquared;
        }

        SetPanic(civilian, scared);
        return scared;
    }

    private static void SetPanic(Character body, bool panic)
    {
        if (body is _0023Mk._00237Y civilian)
        {
            civilian._00230Y = panic;
        }
    }

    private static Player NearestLivingPlayer(GameManager simulation, Vector2 position, float radius)
    {
        Player best = null;
        float bestDistance = radius == float.MaxValue ? float.MaxValue : radius * radius;

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            float distance = Vector2.DistanceSquared(player.Position, position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = player;
            }
        }

        return best;
    }

    /// <summary>送到的平民在基地里站一会儿就收走 —— 基地里不该越攒越多。</summary>
    private void TidyUp(float deltaTime)
    {
        foreach (Civilian civilian in _civilians)
        {
            if (!civilian.Delivered || !Alive(civilian.Body))
            {
                continue;
            }

            civilian.Leaving += deltaTime;

            if (civilian.Leaving >= LeaveSeconds)
            {
                civilian.Body.Destroy();
            }
        }
    }

    private Character SpawnCivilian(GameManager simulation, Vector2 at)
    {
        Type type = CrossroadEventCatalog.ResolveClass("_00237Y");
        Character body = ZombieFactory.Spawn(simulation, type, at, _0023Oq._0023WM._0023Yc, Vector2.UnitX,
            beforeSpawn: DropUndeclaredBaseStats);

        if (body == null)
        {
            Events.Add("十字路口: 平民没刷出来 (模型缓存满 / 池空)");
            return null;
        }

        // 平民是玩家这边的 —— 僵尸会咬它, 玩家打不到它。
        body.TeamId = TeamId.Team1;
        return body;
    }

    /// <summary>
    /// 平民身上带着一条**它自己的属性表里没有**的基础属性 —— 不删的话一生成就抛异常。
    ///
    /// 实测: 从池里取出来的平民 <c>BaseStats</c> 是 MaxHealth / Power / **MaxRage**
    /// (照抄玩家角色那三条), 而平民类声明的属性表 (<c>_0023Mk._00237Y._0023qu</c>) 只有
    /// 十条, 没有 MaxRage。<c>SpawnNPC</c> 逐条 <c>ChangeStats</c> (-Mk/-7Y.cs:113),
    /// 查到 MaxRage 的下标是 -1 -&gt; <c>StatSystem.GetIndex</c> 抛
    /// <c>ArgumentOutOfRangeException</c>。
    ///
    /// 原厂服务端怎么生成平民的那段不在了 (全树零个 GetGameObjectFromPool&lt;_00237Y&gt;),
    /// 这里按判据本身修: 类型属性表里查不到下标的基础属性去掉 —— 那条属性本来就没地方放。
    /// 必须在生成**之前**删 (见 <see cref="ZombieFactory.Spawn"/> 的 beforeSpawn)。
    /// </summary>
    private static void DropUndeclaredBaseStats(Character civilian)
    {
        int typeId = GameManager.GetGameObjectTypeId(civilian.GetType());
        if (typeId < 0 || civilian.BaseStats == null)
        {
            return;
        }

        civilian.BaseStats.RemoveAll(stat => _0023z._0023h5._0023e5(typeId, stat._0023iu) < 0);

        // ## 血量底子是 0
        //
        // 平民的 InitializeNPC 写的是 BaseStat(MaxHealth, Vitality, **0f**) (-Mk/-7Y.cs:81) ——
        // 原厂服务端生成时一定另填了数, 那段不在了。0 的后果: 最大血量 0, 伤害打不进去,
        // 护送的平民永远死不了, 头顶血条也画不出来。
        //
        // 填一个**幸存者的血量底子** —— 取原厂 Berg 的 MaxHealth 评级
        // (<c>BalanceData._0023w7f._0023YGb</c>, 玩家角色 -Mk/-0bb.cs:159 用的同一个数)。
        // 平民长得就是幸存者 (CharacterModelType.Player), 血量跟玩家同一档说得过去。
        // **取哪个角色的是我们定的。**
        //
        // 再乘感染等级: 玩家实测 (2026-09-15) "这两种幸存者的血量也应该随着感染等级而变化"。
        // 乘数是感染等级曲线 ServerModifier (1 级 1.0 ... 17 级 7.52, 僵尸基础属性乘的就是它) ——
        // 平民挨的是随感染等级变强的僵尸, 两边同曲线。每次都按底子重算 (对象池会复用这个平民)。
        float infection = civilian.GameManager != null && civilian.GameManager.ServerModifier > 0f
            ? civilian.GameManager.ServerModifier
            : 1f;
        foreach (BaseStat stat in civilian.BaseStats)
        {
            if (stat._0023iu == StatType.MaxHealth)
            {
                stat._0023Guc = GameManager.BalanceData._0023w7f._0023YGb * infection;
            }
        }
    }

    /// <summary>离茧/平民之间至少隔多远。</summary>
    private const float SpotSpacing = 25f;

    /// <summary>
    /// 挑 <paramref name="count"/> 个摆茧/平民的地方: 站得下 (导航网格上、不压碰撞体, 见
    /// <see cref="NpcFollower.IsFree"/>)、互相隔开 <see cref="SpotSpacing"/>、不在基地送到圈里
    /// (一出茧就算送到)。先从区域的刷怪位里挑 (离中心近的在前), 不够就围着中心一圈圈找。
    ///
    /// ## 实测 map26 踩出来的
    ///
    /// 刷怪位里有一批是屋顶/墙后的"跳下来"点 (不在网格上), 还有网格上但压着车/杂物碰撞体的;
    /// 上一版不够的时候"围着中心补 + 吸附到最近三角形的重心" —— 几个点吸到同一个重心上,
    /// 四个平民叠在一个点, 互相顶住一步都走不动。
    ///
    /// 事件区域就在基地边上、怎么找都在送到圈里的话, 放宽这一条 (那也是"带回来了")。
    /// </summary>
    private List<Vector2> Spots(GameManager simulation, IReadOnlyList<Spawn_EventEntities> spawns,
                                Vector2 center, Vector2 home, int count)
    {
        List<Vector2> candidates = spawns
            .OrderBy(s => Vector2.DistanceSquared(s.Position, center))
            .Select(s => s.Position)
            .ToList();

        for (float ring = 0f; ring <= 150f; ring += 15f)
        {
            int around = ring == 0f ? 1 : 12;
            for (int i = 0; i < around; i++)
            {
                double angle = 2.0 * Math.PI * i / around;
                candidates.Add(center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * ring);
            }
        }

        List<Vector2> picked = new List<Vector2>();

        foreach (bool avoidHome in new[] { true, false })
        {
            foreach (Vector2 candidate in candidates)
            {
                if (picked.Count >= count)
                {
                    return picked;
                }

                if ((avoidHome && Vector2.Distance(candidate, home) <= DeliverRadius * 1.5f)
                    || picked.Any(p => Vector2.Distance(p, candidate) < SpotSpacing)
                    || !_follower.IsFree(simulation, candidate))
                {
                    continue;
                }

                picked.Add(candidate);
            }
        }

        // 实在找不到 (没有网格的实验图): 围着中心硬摆, 至少流程能走
        for (int i = picked.Count; i < count; i++)
        {
            double angle = 2.0 * Math.PI * i / Math.Max(1, count);
            picked.Add(center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 30f);
        }

        return picked;
    }

    private void Reset(Vector2 home, bool escort)
    {
        Disarm();
        _home = home;
        _escort = escort;
        Total = 0;
        Saved = 0;
        Lost = 0;
        Freed = 0;
        SupplyDrops = 0;
        Completed = false;
        Failed = false;
        _cocoonsExpired = false;
    }

    private static bool Alive(Character body) => body != null && body.IsActive && !body.IsDead;
}
