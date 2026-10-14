using System;
using System.Collections.Generic;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Entities.GameModeObjects;
using ConductorGameLogic.MapObjects;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 补给点(<see cref="StorageRoom"/>, 教程里那个 Lifeguard Station)的占领流程。
///
/// ## 原厂在这条链上留下了什么
///
/// 按 X 到"读条结束"为止**全是原厂的, 双端都跑**, 一行都不用碰:
///
///   1. 走近 20 单位 -> <c>StorageRoomPoint._00230ob</c> 推出交互提示
///   2. 按 X -> 技能槽 19 (<c>Player.cs:547</c> 装的是
///      <c>AbilityIdentifier.CaptureFlag</c>) -> <c>_0023Sq._0023OZb</c> buff
///      引导 5 秒
///   3. 引导满 -> <c>GameMode.TriggerCapturePerformed</c>
///      -> <c>storageRoom.PerformedTriggerCapture = 队伍</c>
///
/// 那行赋值就是终点 —— <c>GameMode</c>(:960) 和 <c>HordeMode</c>(:188) 两个实现
/// 都只做了这一件事。玩家看到的"读条读完没反应"就是这么来的。
///
/// ## 地面那个环形进度条读的是什么
///
/// 客户端 HUD 是完整的 (<c>StorageRoomHUD</c> + <c>StorageRoomCaptureGUIScript</c>),
/// 它每 0.21 秒问一次 <c>AsTutorialGameMode._0023tvb(IndexStorageRoom)</c>,
/// 而那个方法读的是**教程 GameModeData 上的四个数组** (-z/-wcc.cs:23-31):
///
///     _0023atb        补给点数量 (0..15), 序列化的循环上界
///     _0023gtb[i]     占领百分比 0..1      -> 圆环填充 + "19%"
///     _0023jtb[i]     持有队伍             -> 点名颜色
///     _0023ktb[i]     正在占领的队伍       -> **Neutral 时整个进度条直接隐藏**
///     _0023htb[i]     占领速度 (-200..200) -> 客户端 /100 显示 "x1.5"
///
/// 这四个和教程状态 <c>_0023pcc</c> 在**同一个结构里**, 也就是走我们已经在用的
/// 那条同步通道 —— 填上就有, 不需要新的协议。
///
/// 没人填它们, <c>_0023ktb</c> 恒为 Neutral, 于是
/// <c>StorageRoomCaptureGUIScript.SetGUI</c>(:99-106) 把圆环、百分比、速度标签
/// 全部 SetActive(false)。玩家看到的"进度条没出现"就是这一行。
///
/// ## 哪些数是原厂的
///
///   - 速度语义: 100 = x1.0。<c>&lt;= 0</c> 客户端显示红色 "Contested",
///     <c>&lt;= -100</c> 显示 x0 (StorageRoomCaptureGUIScript.cs:118-129)
///   - <see cref="HoldRadius"/> 100 —— <c>StorageRoom.cs:121</c> 判"谁在这个点
///     附近"用的就是这个数
///   - 刷怪位置: <c>Spawn_EventEntities</c> 里 ID 等于 <c>Event_StorageRoom.ID</c>
///     的那组, 教程图实测 9 个
///
/// 其余(占领要多久、几波怪、每波几只、奖励多少)**全是我们定的** ——
/// <c>Event_StorageRoom</c> 整个类只有 <c>Name</c> 和 <c>ConstID</c>,
/// 没有任何数量/时长字段。都做成可调属性, 等玩家实测校准。
/// </summary>
public sealed class StorageRoomCaptureDirector
{
    /// <summary>
    /// 一个人独占时占满要几秒。
    ///
    /// **我们定的。** 第一版 20 秒, 玩家实测"占领速度过快", 加到 45 ——
    /// 两个人就是 30 秒, 正好够打完三波怪。人多了按
    /// <see cref="SpeedPerExtraHelper"/> 加速。
    /// </summary>
    public float SecondsToCapture { get; set; } = 45f;

    /// <summary>
    /// 站多远之内算"在场"。
    ///
    /// 原厂的数: <c>StorageRoom.DeserializeGameObjectBase</c>(:121) 判"谁该听到
    /// 占领音效"用的就是距离 100。HUD 那句 "REMAIN CLOSE UNTIL FULLY SECURED"
    /// 说的也是这个圈。
    /// </summary>
    public float HoldRadius { get; set; } = 100f;

    /// <summary>
    /// 圈里人数对应的速度倍率 —— 走 <see cref="CaptureSpeed"/> 那张**原版表**
    /// (1 / 1.5 / 1.8 / 2.0)。
    ///
    /// 09-21 之前这里是线性的"每多一人 +50%", 依据是玩家早先实测的
    /// "一个人是 100%, 每多一个人增加 50%" —— 那次只验到两人 (两人都是 1.5),
    /// 原版从第三人起是边际递减的, 我们三人四人分别快了 11% 和 25%。
    ///
    /// 客户端看得见这个数: <c>倍率 × 100</c> 同步下去显示成 "Capturing Speed x1.5"。
    /// </summary>
    public static float[] SpeedByHolders => CaptureSpeed.ByHolders;

    /// <summary>
    /// 占领过程中刷几波僵尸。波次按百分比均分 —— 3 波就是在 0% / 33% / 66%
    /// 各来一波, 这样刷怪和圆环进度是同步的。
    ///
    /// **我们定的。**
    /// </summary>
    public int Waves { get; set; } = 3;

    /// <summary>
    /// 每波刷几只。
    ///
    /// **我们定的** (玩家实测要求, 10 -> 9)。比摆位点还多也没关系 ——
    /// 点位轮转复用, 和 <see cref="EventSpawnDirector"/> 处理"怪比点多"
    /// 是同一个做法。
    /// </summary>
    public int ZombiesPerWave { get; set; } = 9;

    /// <summary>
    /// 占领成功掉几个补给箱。
    ///
    /// **我们定的**。玩家实测: "占领成功掉落大量物资箱" —— 第一版只掉一个,
    /// 观感差太远。散在补给点周围, 玩家得走一圈捡。
    /// </summary>
    public int CrateCount { get; set; } = 10;

    /// <summary>
    /// 每个补给箱装多少。
    ///
    /// **我们定的**, 但上限是硬的: 玩家身上一次最多背 100
    /// (<c>BalanceData._0023j7f._0023Etb</c>)。第一版给 100, 结果玩家**捡一个
    /// 箱子就满了**, 剩下九个全白掉 —— 而且教程状态 16 -&gt; 17 在占领完成后
    /// 30 毫秒就触发了 (日志实测), 引导语根本来不及看。
    ///
    /// 改成 10: 十个箱子加起来 100, 正好背满一趟, 玩家得绕着补给点走一圈。
    /// </summary>
    public int SuppliesPerCrate { get; set; } = 10;

    /// <summary>
    /// 补给包最远飞多远 (<c>_0023cyc._0023KP</c>)。
    ///
    /// 原厂默认 50 (-EF/-cyc.cs:56)。这里用 30 —— 玩家要的是"散在**补给点里**",
    /// 而占领圈 (<see cref="HoldRadius"/>) 是 100, 50 会把包扔到圈边上。
    /// 抛撒器自己会做落点碰撞检测, 不用担心卡进墙里。
    /// </summary>
    public float CrateScatter { get; set; } = 30f;

    private sealed class State
    {
        public StorageRoom Room;
        public readonly List<Spawn_EventEntities> Spawns = new List<Spawn_EventEntities>();
        public readonly List<Character> Alive = new List<Character>();
        public TeamId Capturer = TeamId.Neutral;
        public float Percent;
        public int Speed;
        public int WavesFired;
        public bool Done;
    }

    private readonly List<State> _points = new List<State>();
    private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();
    private bool _scanned;

    /// <summary>诊断: 认出几个补给点。</summary>
    public int ArmedPoints => _points.Count;

    /// <summary>本 tick 发生的事, 供宿主打日志。读完自行清空。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>
    /// 本 tick 刷出来的怪, 供宿主激怒它们。
    /// 理由见 <see cref="EventSpawnDirector.SpawnedThisTickList"/> ——
    /// 玩家点名说了补给点这一处 ("和补给点和 barricade 刷的僵尸一样")。
    /// </summary>
    public readonly List<Character> SpawnedThisTickList = new List<Character>();

    /// <summary>
    /// 扫地图, 把补给点和它的刷怪位配上。开局调一次。
    ///
    /// 关联键是 <c>SceneMapObject.ID</c>, 和 <see cref="EventSpawnDirector"/>
    /// 同一套三层结构 —— 教程图里 <c>Event_StorageRoom</c> 是 10038,
    /// 对应 9 个 <c>Spawn_EventEntities</c>。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        Dictionary<int, List<Spawn_EventEntities>> spawnsById =
            new Dictionary<int, List<Spawn_EventEntities>>();
        List<int> storageIds = new List<int>();

        foreach (MapObject mapObject in simulation.MapObjects)
        {
            switch (mapObject)
            {
                case Event_StorageRoom storage:
                    storageIds.Add(storage.ID);
                    break;

                case Spawn_EventEntities spawn:
                    if (!spawnsById.TryGetValue(spawn.ID, out List<Spawn_EventEntities> list))
                    {
                        list = new List<Spawn_EventEntities>();
                        spawnsById[spawn.ID] = list;
                    }

                    list.Add(spawn);
                    break;
            }
        }

        foreach (int id in storageIds)
        {
            State state = new State();
            if (spawnsById.TryGetValue(id, out List<Spawn_EventEntities> spawns))
            {
                state.Spawns.AddRange(spawns);
            }

            _points.Add(state);
        }

        foreach (KeyValuePair<string, Type> entry in ZombieFactory.ResolveKindTypes())
        {
            _types[entry.Key] = entry.Value;
        }

        _scanned = true;
    }

    public void Update(GameManager simulation, float deltaTime)
    {
        if (!_scanned || _points.Count == 0)
        {
            return;
        }

        SpawnedThisTickList.Clear();

        if (simulation?.GameMode is not _0023bd._00235cc tutorial)
        {
            return;
        }

        BindRooms(simulation);

        for (int i = 0; i < _points.Count; i++)
        {
            Advance(simulation, _points[i], deltaTime);
        }

        Publish(tutorial);
    }

    /// <summary>
    /// 把场上的 StorageRoom 实体认到各自的 State 上。
    ///
    /// 每帧重认而不是只认一次 —— 实体是 <see cref="StorageRoomSpawner"/> 在对局
    /// 开始时才放出来的, 而 <see cref="Scan"/> 可能更早跑。用
    /// <c>GetActiveObjectsFromCategory</c> 是因为 StorageRoom 是 GameObjectBase
    /// 而不是 Entity, 不在 ActiveEntityList 里 (原厂自己也是这么找的,
    /// GameMode.TriggerCapturePerformed:964)。
    /// </summary>
    private void BindRooms(GameManager simulation)
    {
        int next = 0;
        foreach (StorageRoom room in simulation.GetActiveObjectsFromCategory(ObjCat.StorageRoom))
        {
            if (room == null || !room.IsActive || next >= _points.Count)
            {
                continue;
            }

            _points[next].Room = room;
            next++;
        }
    }

    private void Advance(GameManager simulation, State state, float deltaTime)
    {
        if (state.Room == null || state.Done)
        {
            return;
        }

        // 边沿: 原厂的 TriggerCapturePerformed 在 GameMode 内部改这个字段,
        // 插不进钩子, 只能盯它由 Neutral 变成某个队伍 (踩坑第 9 条)。
        if (state.Capturer == TeamId.Neutral)
        {
            if (state.Room.PerformedTriggerCapture == TeamId.Neutral)
            {
                return;
            }

            state.Capturer = state.Room.PerformedTriggerCapture;

            // **这一行是"按 X 占领"提示消失的关键。**
            //
            // 客户端每帧判 (StorageRoomPoint._00230ob:122):
            //     if (DenyRoomHolder != 我方 && DenyTakeOverTeam != 我方) -> 推提示
            // 占领进行中两个都留 Neutral 的话, 提示会全程挂着 —— 玩家报的
            // "全程显示按 X 开始占领"就是这么来的。
            //
            // DenyTakeOverTeam 的语义正是"这个点正在被谁接管"。设上它还顺带接通了
            // 占领完成音效: 客户端 StorageRoom.Deserialize(:128) 要看到它**由非
            // Neutral 变回 Neutral**才播那声, 所以中间这一段必须真的设过。
            state.Room.DenyTakeOverTeam = state.Capturer;
            MarkDirty(simulation, state.Room);

            Events.Add($"补给点开始占领 (队伍 {state.Capturer}, 独占约 {SecondsToCapture:F0} 秒, " +
                       $"刷怪位 {state.Spawns.Count} 个)");
        }

        int helpers = CountHolders(simulation, state);

        // 没人守着就停在原地 —— HUD 那句 "REMAIN CLOSE UNTIL FULLY SECURED"。
        // 速度写 0 而不是负数: 负数在客户端是"被敌方争夺"(显示红色 Contested),
        // 语义不一样。
        if (helpers <= 0)
        {
            state.Speed = 0;
            return;
        }

        state.Speed = CaptureSpeed.Percent(helpers);

        float before = state.Percent;
        state.Percent = Math.Min(1f, state.Percent + deltaTime / SecondsToCapture * (state.Speed / 100f));

        FireWavesUpTo(simulation, state, before);

        if (state.Percent >= 1f)
        {
            Complete(simulation, state);
        }
    }

    /// <summary>
    /// 数一下这个点周围有几个占领方的活人。
    ///
    /// 教程 NPC (Amber) 也算 —— 她是 Team1, 和玩家同队, 而原版截图里正是
    /// "玩家 + Amber" 两个单位配 "Capturing Speed x1.5"。
    /// </summary>
    private int CountHolders(GameManager simulation, State state)
    {
        int count = 0;
        Vector2 position = state.Room.Position;

        foreach (Entity entity in simulation.ActiveEntityList)
        {
            if (entity == null || !entity.IsActive || entity.IsDead || !entity.IsCharacter)
            {
                continue;
            }

            if (entity.TeamId != state.Capturer)
            {
                continue;
            }

            if (Vector2.Distance(entity.Position, position) <= HoldRadius)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 百分比越过波次门槛就刷一波。
    ///
    /// 门槛按 <see cref="Waves"/> 均分 —— 3 波就是 0% / 33% / 66%, 刷怪和圆环
    /// 进度同步。用"越过"而不是"每隔几秒", 这样玩家人多占得快时怪也来得密,
    /// 不会占完了怪才刷。
    /// </summary>
    private void FireWavesUpTo(GameManager simulation, State state, float before)
    {
        while (state.WavesFired < Waves)
        {
            float threshold = (float)state.WavesFired / Waves;
            if (before > threshold || state.Percent < threshold)
            {
                break;
            }

            state.WavesFired++;
            int spawned = SpawnWave(simulation, state);
            Events.Add($"补给点第 {state.WavesFired}/{Waves} 波: 刷出 {spawned} 只 " +
                       $"(进度 {state.Percent * 100f:F0}%)");
        }
    }

    /// <summary>
    /// 刷一波。每波用摆位点的一个连续切片, 波与波之间不重复用点。
    ///
    /// 刷 Walker —— **我们定的**。教程这一段之前出现的都是 Walker, 而
    /// <c>Event_StorageRoom</c> 没有任何类型字段可依。
    /// </summary>
    private int SpawnWave(GameManager simulation, State state)
    {
        if (state.Spawns.Count == 0)
        {
            return 0;
        }

        Type walker = WalkerType();
        if (walker == null)
        {
            return 0;
        }

        // 起点错开一波的量, 这样相邻两波不会全挤在同一批点上。
        int start = (state.WavesFired - 1) * ZombiesPerWave;
        int spawned = 0;

        for (int i = 0; i < ZombiesPerWave; i++)
        {
            Spawn_EventEntities at = state.Spawns[(start + i) % state.Spawns.Count];

            Character zombie = ZombieFactory.Spawn(
                simulation, walker, at.Position, at.SpawnState, at.Direction);

            if (zombie == null)
            {
                continue;
            }

            state.Alive.Add(zombie);
            SpawnedThisTickList.Add(zombie);
            spawned++;
        }

        return spawned;
    }

    /// <summary>
    /// Walker 的运行时类型。
    ///
    /// 从 <see cref="ZombieFactory.Kinds"/> 按**可读名**反查混淆名, 而不是把
    /// "_0023rPb" 再抄一份 —— 那张表是唯一的真相来源。
    /// </summary>
    private Type WalkerType()
    {
        foreach ((string obfuscated, string readable) in ZombieFactory.Kinds)
        {
            if (readable == "Walker" && _types.TryGetValue(obfuscated, out Type type))
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>
    /// 占领完成: 记下持有者, 掉补给箱。
    ///
    /// <c>DenyRoomHolder</c> 是原厂表示"谁持有这个点"的字段, 客户端一看到它变化
    /// 就给该队 100 单位内的玩家播占领音效 (StorageRoom.cs:117-127) ——
    /// 那条反馈不用我们做。
    /// </summary>
    private void Complete(GameManager simulation, State state)
    {
        state.Done = true;
        state.Percent = 1f;
        state.Room.DenyRoomHolder = state.Capturer;
        state.Room.DenyTakeOverTeam = TeamId.Neutral;
        MarkDirty(simulation, state.Room);

        bool thrown = ThrowSupplies(simulation, state.Room.Position);

        Events.Add($"补给点占领完成 (队伍 {state.Capturer}) —— " +
                   (thrown
                       ? $"开始抛撒补给 {CrateCount} 包 × {SuppliesPerCrate}"
                       : "抛撒器没拿到 (池空?)"));
    }

    /// <summary>
    /// 让这个补给点的状态**下一帧就发出去**。
    ///
    /// <c>StorageRoom.PrioritizeGameObject</c> 把自己的优先级乘了 0.1
    /// (StorageRoom.cs:87) —— 平时是个低频对象, 状态变化要等两三秒才轮得上。
    /// 而"按 X 提示消失"和占领音效都靠客户端看到字段变化, 慢一拍玩家就会在
    /// 已经占上的点上又按一次 X。
    ///
    /// 上一版这里是空的, 因为我判断"<c>_SyncHelper</c> 是 protected,
    /// 从 GameManager 这侧够不着" —— **那是错的**:
    /// <c>NetworkManager.GetSyncHelper(SyncHelperIndex)</c> 是 public 的,
    /// 拿到的就是同一个对象, <see cref="ZombieFactory"/> 设同步间隔时一直在用。
    /// </summary>
    private static void MarkDirty(GameManager simulation, StorageRoom room)
    {
        simulation?.NetworkManager?.GetSyncHelper(room.SyncHelperIndex)?._00234Ce();
    }

    /// <summary>
    /// 从补给点中心把补给**喷出去**, 散落在周围。
    ///
    /// ## 用原厂的抛撒器, 不自己摆
    ///
    /// 第一版是我自己算角度均匀摆一圈箱子 —— 箱子凭空出现在地上, 玩家反馈
    /// "资源箱直接出现, 没有播放动画", 他要的是"从补给点中心喷出来, 然后散在
    /// 补给点里"。
    ///
    /// 原厂有现成的: <c>_0023EF._0023cyc</c> (日志里叫 BossSupplyAreaEffect)。
    /// 它每 <c>_0023ET</c>(0.2 秒) 扔一个包, 播 <c>Vfx_Boss_Supply_Spawn_Throw_01</c>
    /// 抛出特效, 生成飞行物 <c>_0023ci</c> 按随机方向飞 <c>_0023KP</c> 距离,
    /// **落点还会做碰撞检测**避免卡进墙里 (-EF/-cyc.cs:78-130)。
    ///
    /// 字段语义是从两处原厂用法交叉验证的, 不是猜的:
    ///
    ///   Character.cs:2917  囤积者僵尸死亡  _00237q=30 次, _0023Pec=5  -> 共 150
    ///                      (150 正是 BalanceData._0023j7f._0023Qtb)
    ///   Supplies.cs:411    通用抛撒       _00237q=总量/每包, _0023Pec=每包
    ///
    /// 也就是 <c>_00237q</c> = **包数**, <c>_0023Pec</c> = **每包补给量**。
    /// <c>UpdateGameObject</c> 里 <c>_00237q--</c> 每扔一个减一, 对得上。
    ///
    /// ## 实现搬到了 <see cref="SupplyThrower"/>
    ///
    /// Boss 掉落要的是同一件事 (玩家实测: "应该掉落和占领补给点后同样数量的
    /// 物资"), 与其两处各写一份迟早会漂移的代码, 不如共用一个入口。
    /// </summary>
    private bool ThrowSupplies(GameManager simulation, Vector2 center) =>
        SupplyThrower.Throw(simulation, center, CrateCount, SuppliesPerCrate, CrateScatter);

    /// <summary>
    /// 把状态写进教程 GameModeData —— 这一步做完客户端才画得出圆环。
    ///
    /// 下标是 <c>StorageRoom.IndexStorageRoom</c>, 客户端就是拿它去查
    /// (<c>StorageRoomHUD.cs:111</c> 传的正是这个)。<c>_0023atb</c> 必须等于
    /// 点数, 它是序列化那个 for 循环的上界 (-z/-wcc.cs:59) —— 写 0 的话四个数组
    /// 一个字节都不会发出去。
    /// </summary>
    private void Publish(_0023bd._00235cc tutorial)
    {
        int slots = 0;
        for (int i = 0; i < _points.Count; i++)
        {
            if (_points[i].Room != null)
            {
                slots = Math.Max(slots, _points[i].Room.IndexStorageRoom + 1);
            }
        }

        if (slots == 0)
        {
            return;
        }

        // 结构里是四个 List, 长度不够就补齐。GameModeData 是 struct, 但这几个
        // 字段是引用类型, 直接操作 List 内容即可 —— 不能整个赋值回去
        // (那会拿到副本, 踩坑见 TutorialState_WrittenThroughProperty_DoesNotStick)。
        Grow(tutorial._0023l9._0023gtb, slots, 0f);
        Grow(tutorial._0023l9._0023htb, slots, 0);
        Grow(tutorial._0023l9._0023jtb, slots, TeamId.Neutral);
        Grow(tutorial._0023l9._0023ktb, slots, TeamId.Neutral);
        tutorial._0023l9._0023atb = slots;

        for (int i = 0; i < _points.Count; i++)
        {
            State state = _points[i];
            if (state.Room == null)
            {
                continue;
            }

            int index = state.Room.IndexStorageRoom;
            if (index < 0 || index >= slots)
            {
                continue;
            }

            tutorial._0023l9._0023gtb[index] = state.Percent;
            tutorial._0023l9._0023htb[index] = state.Speed;
            tutorial._0023l9._0023jtb[index] = state.Room.DenyRoomHolder;

            // 占完之后 capturer 要归 Neutral —— 客户端靠它决定圆环显不显示,
            // 不归零的话占满了圆环还挂在那儿。
            tutorial._0023l9._0023ktb[index] = state.Done ? TeamId.Neutral : state.Capturer;
        }
    }

    private static void Grow<T>(List<T> list, int count, T fill)
    {
        while (list.Count < count)
        {
            list.Add(fill);
        }
    }
}
