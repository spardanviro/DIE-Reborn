using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Collision;
using ConductorGameLogic.Entities;
using ConductorGameLogic.Entities.Destructibles;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.MapObjects;
using ConductorGameLogic.View;
using FarseerPhysics.Common;

namespace DIE.Server.Game;

/// <summary>
/// 十字路口的**补给设施** (<c>SupplyFacility</c>): 把设施画出来, 门口堵一道路障;
/// 打开路障 -&gt; 进去 -&gt; 把里面的物资捡完 = 完成。
///
/// ## 玩家实测 (2026-09-13)
///
/// "supply facility 不是打破旁边的茧获得物资, 而是打开堵在门口的 barricade 然后进去拾取其中的物资。"
///
/// 上一版在设施旁边摆了个 Goo 茧当"物资堆" (从特效名 <c>Event_OpenStash_Cocoon</c> 推的), 推错了。
///
/// ## 门: 两栋都是普通木门 (DoorBarricade 两段式)
///
/// 玩家实测 (2026-09-14): "门的样式和设施的种类对不上"。上一版照名字配了 <c>BarricadeType</c> 枚举最后两项
/// (Shed -&gt; <c>ShedDoor</c> / Lab -&gt; <c>IronLab</c>)。拆资源 (sharedassets1.assets) 之后发现:
///
///   IronLab    <c>Vfx_Barricade_Lab_Iron_01</c> 里挂着 <c>ScoutMission_Barricade_RamDestroy_01</c> ——
///              是新手任务 Lab 那道给 Ram 撞的铁栅门, 不是设施的门
///   两栋设施   预制体 <c>Event_Facility_SmallWorkshop_01</c> / <c>Event_StorageFacility_Lab_01</c> 里都内嵌
///              一组门位标记 "Barricade 1", 序列化内容是 <c>DoorBarricade</c> 组 3001 LEFT + RIGHT, 间距 2.11 (×10)
///
/// 玩家定: 两栋都用普通木门, 照预制体里的标记。
///
/// ## 门摆在哪: 从设施的碰撞多边形量出来的
///
/// 客户端把设施的碰撞体注册进碰撞系统 (<c>UnityRaidEventView</c> -&gt; <c>ActivateCollision</c>),
/// 形状来自 <c>Maps/effectcollisions.data</c>, 以设施朝向旋转 (本地 +X = 设施朝向)。两栋房子都是
/// **C 形**, 缺口朝本地 +X (实测顶点, 见 <see cref="Layouts"/>):
///
///   Shed   墙体包住 x∈[-38,30]、y∈[-33,48], 屋里约 x∈[-10,15]、y∈[-13,17];
///          缺口在 x≈22, y 从 -21 到 19 (宽约 40)
///   Lab    屋里约 x∈[-8,31]、y∈[-22,22]; 缺口在 x≈31, y 从 -20 到 22 (宽约 42)
///
/// 几段怎么排照抄地图上现成的门 (探针读 Scout_02_Lab / Scavenger_01_Resort): 各段沿门的朝向的
/// 垂线排开, 左法线 = (-方向.Y, 方向.X)。
///
///   三段式 (木栅栏 / 铁栅栏)   间距 20, **LEFT 在左法线一侧**
///   两段式 (DoorBarricade 组 3001 / WeakDoorBarricade 组 5000)   间距 21.4, **LEFT 在右法线一侧**
///
/// 门类只有 LEFT 段有模型 (大概挂在门轴那头), 所以两段式那个反号要照抄。
///
/// 门的朝向取**指向屋里** —— Scout_03_Club 那道单向门的朝向正指着"里面" (见
/// <see cref="ScoutStoryDirector.IsDamageBlocked"/>)。
///
/// ## 我们定的
///
///   - 门的血量 (<see cref="ShedDoorHealth"/> / <see cref="LabDoorHealth"/>) —— 路障血量原厂值找不到,
///     见 <see cref="DestructibleSpawner"/> 的长注释
///   - 屋里放多少物资 (<see cref="Crates"/>) —— 用占领补给点那个抛撒器, 包数同占领
///   - 门口位置是按多边形量的, 实测要是偏了照日志里的坐标调 <see cref="Layouts"/>
/// </summary>
public sealed class CrossroadStash
{
    /// <summary>交互点 ID —— 不和 ScoutObjectiveDirector 的 1 撞。</summary>
    private const ushort PointId = 2;

    /// <summary>
    /// 设施朝向 = 事件位自带的朝向 (原版设置, 本地 +X = 门口)。
    ///
    /// 玩家 (2026-09-14): 先要"门总是朝右下", 固定成世界 (0,+1) 之后有的位子房子一转门口就怼上地形
    /// (map27 区域 110089), 随后改口: "supply facility 不再固定朝向, 保持原版设置"。
    /// 事件位朝向是关卡按地形摆的; 退化 (零向量) 时才兜底 +Y。
    /// </summary>
    public static Vector2 FacingFor(Vector2 areaDirection) =>
        areaDirection.LengthSquared() < 0.0001f ? new Vector2(0f, 1f) : Vector2.Normalize(areaDirection);

    /// <summary>门的路障组号 —— 不和地图上的组撞 (地图组号实测 3..10631)。</summary>
    private const int DoorGroupId = 900_002;

    /// <summary>一栋设施的门和屋里 (本地坐标, +X = 设施朝向)。</summary>
    /// <param name="LeftSide">LEFT 段在左法线 (+1) 还是右法线 (-1) 那一侧, 见类说明。</param>
    public sealed record Layout(BarricadeType Door, Vector2 DoorCenter, BarricadePart[] Parts,
                                float PartSpacing, float LeftSide, Vector2 Inside);

    /// <summary>两个视觉变体的门口/屋里。数值来自 <c>effectcollisions.data</c> 的多边形, 见类说明。</summary>
    public static readonly IReadOnlyDictionary<string, Layout> Layouts = new Dictionary<string, Layout>
    {
        ["Shed"] = new Layout(BarricadeType.DoorBarricade, new Vector2(22f, -1f),
            new[] { BarricadePart.LEFT, BarricadePart.RIGHT }, 21.4f, -1f, new Vector2(2f, 2f)),
        ["Lab"] = new Layout(BarricadeType.DoorBarricade, new Vector2(31f, 1f),
            new[] { BarricadePart.LEFT, BarricadePart.RIGHT }, 21.4f, -1f, new Vector2(10f, 0f)),
    };

    private readonly List<_0023EK._0023Yh> _door = new List<_0023EK._0023Yh>();
    /// <summary>飞在空中的补给箱 (<c>_0023EF._0023ci</c>) —— 落地就回池, 换成地上那个。</summary>
    private readonly List<(GameObjectBase Pickup, int Uses)> _crates = new List<(GameObjectBase, int)>();

    /// <summary>落在地上等人捡的补给 (<c>_0023aX._0023yGc</c>, -EF/-ci.cs:104 落地时生成)。</summary>
    private readonly List<(GameObjectBase Pickup, int Uses)> _pickups = new List<(GameObjectBase, int)>();

    private HashSet<GameObjectBase> _before;
    private GameObjectBase _thrower;
    private bool _hasPoint;

    /// <summary>实验室 (门血量按它算)。</summary>
    public static bool IsLab(string visualName) => visualName == "Lab";

    /// <summary>
    /// 木门 (小屋门) 感染等级 1 时整道门的血量。**我们定的。**
    /// 拾荒开局那两道路障也用这个数 (玩家 2026-09-23 定, 见 ScavengerDirector.BarricadeHealth)。
    /// </summary>
    public const float WoodenDoorBaseHealth = 800f;

    /// <summary>小屋门感染等级 1 时的血量。**我们定的。**</summary>
    public float ShedDoorHealth { get; set; } = WoodenDoorBaseHealth;

    /// <summary>实验室门感染等级 1 时的血量。**我们定的。**</summary>
    public float LabDoorHealth { get; set; } = 1500f;

    /// <summary>
    /// 门这一次的血 (测试/诊断) = 基础 × 感染等级乘数。
    ///
    /// 玩家实测 (2026-09-15): "补给设施的门的血量应该随感染等级而变化, 而不是随玩家人数变化。"
    /// (09-13 那版是按人数, 每多一人 +0.75 倍。) 乘数是感染等级曲线 <c>ServerModifier</c>
    /// (GameCraftingData.xml 的 InfectionLevelIncrease, 1 级 1.0 ... 17 级 7.52; 僵尸基础属性乘的就是它, 物资掉落量也跟它走)。
    /// 没用原厂的 <c>GetInfectionLevelModifiers</c>: 那个还乘了 EnemyModifier (0.35), 1 级的门会只剩三分之一。
    /// </summary>
    public float DoorHealth { get; private set; }

    /// <summary>设施墙体注册进服务端碰撞系统了 (测试/诊断)。见 <see cref="ActivateWalls"/>。</summary>
    public bool WallsActive { get; private set; }

    /// <summary>屋里抛几包补给 (每包 <see cref="SupplyThrower.DefaultSuppliesPerCrate"/>)。同占领补给点。</summary>
    public int Crates { get; set; } = SupplyThrower.DefaultCrateCount;

    /// <summary>抛多散 —— 小一点, 别飞出门外。**我们定的。**</summary>
    public float Scatter { get; set; } = 10f;

    /// <summary>这一局在跑。</summary>
    public bool Armed { get; private set; }

    /// <summary>门打开了 (所有段都破了)。</summary>
    public bool DoorOpen { get; private set; }

    /// <summary>屋里的物资抛出来了。</summary>
    public bool Stocked { get; private set; }

    /// <summary>物资捡完了 = 这个任务完成。</summary>
    public bool Opened { get; private set; }

    /// <summary>门的几段 (测试/诊断)。</summary>
    public IReadOnlyList<_0023EK._0023Yh> DoorParts => _door;

    /// <summary>这一局摆的是哪栋 ("Shed" / "Lab")。</summary>
    public string VisualName { get; private set; }

    /// <summary>屋里 (世界坐标)。物资抛在这里。</summary>
    public Vector2 Inside { get; private set; }

    /// <summary>门口中心 (世界坐标)。</summary>
    public Vector2 Doorway { get; private set; }

    /// <summary>屋里还剩几包没捡。</summary>
    public int PickupsLeft => _pickups.Count(p => Alive(p.Pickup, p.Uses));

    /// <summary>一共抛出来几包。</summary>
    public int PickupsTotal => _pickups.Count;

    /// <summary>0..1: 门占一半, 捡物资占一半。</summary>
    public float Progress
    {
        get
        {
            if (Opened)
            {
                return 1f;
            }

            if (!DoorOpen)
            {
                return 0f;
            }

            return PickupsTotal == 0 ? 0.5f : 0.5f + 0.5f * (PickupsTotal - PickupsLeft) / PickupsTotal;
        }
    }

    /// <summary>宿主读完打日志再清。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>摆设施和门。</summary>
    public void Arm(GameManager simulation, CrossroadEventCatalog.Mission mission)
    {
        Disarm(simulation);
        Armed = true;
        DoorOpen = false;
        Stocked = false;
        Opened = false;

        List<InteractionPoint> points =
            (simulation.GameMode as IRaidEventGameMode)?._0023ei(null)._0023Kmd;

        if (points != null)
        {
            points.Add(new InteractionPoint
            {
                ID = PointId,
                VisualID = (ushort)mission.Choice.VisualId,
                Position = mission.Area.Position,
                Direction = FacingFor(mission.Area.Direction),
                Event = RaidEventType.SupplyFacility,
                Type = InteractionPointType.ObjectPosition,

                // Inactive: 文案为空 (实测 5 个状态全空), 不会冒按键提示, 只画设施
                State = InteractionState.Inactive,
            });
            _hasPoint = true;
        }

        VisualName = mission.Choice.Name;
        Layout layout = LayoutFor(mission.Choice.Name);
        Vector2 center = mission.Area.Position;
        Vector2 facing = FacingFor(mission.Area.Direction);

        Doorway = ToWorld(center, facing, layout.DoorCenter);
        Inside = ToWorld(center, facing, layout.Inside);

        // 门朝屋里; 各段沿左法线排开
        Vector2 doorDirection = -facing;
        Vector2 left = new Vector2(-doorDirection.Y, doorDirection.X);
        float infection = simulation.ServerModifier > 0f ? simulation.ServerModifier : 1f;
        float health = (IsLab(mission.Choice.Name) ? LabDoorHealth : ShedDoorHealth) * infection;
        DoorHealth = health;

        ActivateWalls(simulation, mission);
        float half = (layout.Parts.Length - 1) / 2f;
        Dictionary<int, _0023EK._00235xc> groups = new Dictionary<int, _0023EK._00235xc>();

        for (int i = 0; i < layout.Parts.Length; i++)
        {
            // LEFT 排第一个; 它在哪一侧看门的类型 (LeftSide)
            float along = (half - i) * layout.PartSpacing * layout.LeftSide;
            _0023EK._0023Yh piece = DestructibleSpawner.SpawnPiece(simulation, Doorway + left * along,
                doorDirection, layout.Parts[i], layout.Door, health, DoorGroupId, groups);

            if (piece != null)
            {
                _door.Add(piece);
            }
        }

        Events.Add($"十字路口补给设施: {mission.Choice.Name} @ ({center.X:F0},{center.Y:F0}) 朝 "
                   + $"({facing.X:F2},{facing.Y:F2}), 门 {layout.Door} {_door.Count} 段 @ "
                   + $"({Doorway.X:F0},{Doorway.Y:F0}) 血 {health:F0} (感染乘数 {infection:F2}), 屋里 ({Inside.X:F0},{Inside.Y:F0}), "
                   + $"墙体碰撞 {(WallsActive ? "已注册" : "没注册上")}");

        if (_door.Count == 0)
        {
            Events.Add("十字路口补给设施: 门没摆上 (池空?), 直接放物资");
        }
    }

    /// <summary>每帧一次: 门破了 -&gt; 屋里抛物资; 物资捡完 -&gt; 完成。</summary>
    public void Update(GameManager simulation)
    {
        if (!Armed || Opened)
        {
            return;
        }

        if (!DoorOpen)
        {
            if (_door.Any(p => p.IsActive && p.Health > 0f))
            {
                return;
            }

            DoorOpen = true;
            ShowOpened(simulation);
            Stock(simulation);
            return;
        }

        // 抛撒器分几帧一包包往外扔 (-EF/-cyc.cs, 0.5 秒起、每 0.2 秒一包), 每包在空中飞
        // 0.6~1.5 秒 (<c>_0023ci</c>), 落地回池、换成地上等人捡的 <c>_0023yGc</c>。
        // 边扔边点名 —— 人站在屋里的话落地就被捡走了, 扔完再点名一包都点不到。
        if (_before != null)
        {
            CollectPickups(simulation);

            bool throwing = (_thrower != null && _thrower.IsActive)
                            || _crates.Any(c => Alive(c.Pickup, c.Uses));
            if (throwing)
            {
                return;
            }

            CollectPickups(simulation);     // 最后一包落地那一帧生成的
            _thrower = null;
            _before = null;
            Events.Add($"十字路口补给设施: 屋里落了 {_pickups.Count} 包补给 (剩 {PickupsLeft}), 捡完算完成");
        }

        if (PickupsLeft > 0)
        {
            return;
        }

        Opened = true;
        Events.Add("十字路口补给设施: 屋里的补给捡完了");
    }

    /// <summary>
    /// 门破了: 设施特效的 "Progress" / "Timer" 置满 —— 进屋的透视 (屋顶/墙淡掉) 才会生效。
    ///
    /// 玩家实测 (2026-09-14): "大门打破后, 在任务完成之前摄像机的透视效果一直不显示, 导致人在屋子里时
    /// 看不到, 完成任务后透视效果又出现了。"
    ///
    /// 客户端 <c>UnityRaidEventView.Update</c> 每帧把交互点的 <c>Progress</c> 喂给设施特效的 "Progress"
    /// 参数 (:118-122); 点被摘掉时它自己把 "Progress" 和 "Timer" 都置 1 (:143-144) —— 正好是"任务完成后
    /// 透视又出现了"那一刻。以前点一直挂着 Progress 0, 设施就一直是"门还关着"的样子。
    /// 特效预制体里参数具体控制什么拿不到, 这里照抄摘点时客户端做的那两个数。**需实测。**
    /// </summary>
    private void ShowOpened(GameManager simulation)
    {
        if (!_hasPoint || simulation?.GameMode is not IRaidEventGameMode mode)
        {
            return;
        }

        List<InteractionPoint> points = mode._0023ei(null)._0023Kmd;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].ID != PointId)
            {
                continue;
            }

            InteractionPoint point = points[i];
            point.Progress = 1f;
            point.Timer.SetValue(1f);
            points[i] = point;
        }
    }

    /// <summary>收尾: 摘设施、没破的门收走 (捡剩的物资留在地上)。</summary>
    public void Disarm(GameManager simulation)
    {
        if (_hasPoint && simulation?.GameMode is IRaidEventGameMode mode)
        {
            mode._0023ei(null)._0023Kmd.RemoveAll(p => p.ID == PointId);
        }

        foreach (_0023EK._0023Yh piece in _door)
        {
            if (piece.IsActive)
            {
                piece.Destroy();
            }
        }

        _door.Clear();
        _crates.Clear();
        _pickups.Clear();
        _before = null;
        _thrower = null;
        _hasPoint = false;
        Armed = false;
    }

    /// <summary>
    /// 把设施的墙注册进**服务端**的碰撞系统。
    ///
    /// 玩家实测 (2026-09-13): "增加设施的墙体碰撞。"
    ///
    /// ## 客户端一直有墙, 服务端没有
    ///
    /// <c>UnityRaidEventView.Update</c> 第一次画这个交互点时: 从 <c>InteractionsCollisionCache</c> 按
    /// (事件, 交互体类型, 视觉) 取多边形 (源头 <c>Maps/effectcollisions.data</c>) -&gt; 每块凸多边形
    /// 挪到交互点位置、按朝向转 -&gt; <c>ActivateCollision</c> = <c>CollisionManager._0023c8._0023yc</c>。
    /// 服务端没人做这一步, 于是玩家在客户端被墙挡着, 服务端的僵尸和平民直接穿墙。
    ///
    /// 这里照抄那几步, 用同一个缓存类、同一个调用。**摘交互点时不拆墙** —— 客户端也不拆
    /// (那边交互点消失只是把特效参数拨到 1), 两边保持一致。
    /// </summary>
    private void ActivateWalls(GameManager simulation, CrossroadEventCatalog.Mission mission)
    {
        WallsActive = false;

        try
        {
            _0023z._0023fS key = new _0023z._0023fS(RaidEventType.SupplyFacility, InteractionPointType.ObjectPosition,
                (ushort)mission.Choice.VisualId);
            InteractionsCollisionCache cache = new InteractionsCollisionCache();
            if (!cache._0023GS(key))
            {
                Events.Add("十字路口补给设施: 这个视觉没有碰撞数据, 墙不注册");
                return;
            }

            ConductorGameLogic.Collision.LevelGeometry.PolygonData walls = cache._0023FS(key);
            if (walls.ConvexPolygon == null || !walls.ConvexPolygon.Any())
            {
                return;
            }

            foreach (ConductorGameLogic.Collision.LevelGeometry.PolygonShapeGeometry shape in walls.ConvexPolygon)
            {
                shape._0023hkd.Position = mission.Area.Position;
                shape._0023hkd.Rotate(FacingFor(mission.Area.Direction));
            }

            simulation.CollisionManager._0023c8._0023yc(walls);
            WallsActive = true;
        }
        catch (Exception e)
        {
            Events.Add($"十字路口补给设施: 墙体碰撞注册失败 ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>
    /// 这个事件位 (按它自带朝向摆) 对设施来说太挤吗 —— 太挤就不办补给设施。
    ///
    /// 玩家实测 (2026-09-14): "补给设施刷新的位置有时候还是会卡在很狭窄的地方, 和前面卡车出现的问题一样,
    /// 导致路被堵死了。" 那局是 map30 区域 150031 的小屋。
    ///
    /// 探针 (8 张小图 66 个设施位, 自带朝向): 从设施中心往碰撞多边形每个顶点打射线, 另往 16 个方向量 90 以内有没有
    /// 挡走路的几何。挤的位子扎成堆, 全是同几块拼图 (号尾 26 / 32 / 31·33 / 34):
    ///
    ///   房子和地形重叠   墙体顶点射线被挡 4~5 条 (其余位子都是 0)
    ///   夹在窄道里       16 个方向里 11~14 个 90 以内就碰到几何 (开阔的位子 0~9 个)
    ///
    /// 两条任一成立就算挤。阈值 (0 / 11 个方向 / 90) 是**我们定的**, 从上面那道分界取的。
    /// 射线用挡走路的那组标志 (1|4|0x20, 同 <see cref="ZombiePathing"/>)。
    /// </summary>
    public static bool IsCramped(GameManager simulation, Vector2 center, Vector2 direction, int visualId,
                                 out string detail)
    {
        detail = null;
        var geometry = simulation?.CollisionManager?._0023c8;
        if (geometry == null)
        {
            return false;
        }

        const GeometryCollisionFlags walkBlocking =
            GeometryCollisionFlags._00237jd | GeometryCollisionFlags._00239jd | GeometryCollisionFlags._0023ckd;
        bool Ray(Vector2 to) => geometry._00233b(center, to, walkBlocking, _00237b: false, _00238b: false);

        Vector2 facing = FacingFor(direction);
        int overlapping = 0;
        try
        {
            InteractionsCollisionCache cache = new InteractionsCollisionCache();
            _0023z._0023fS key = new _0023z._0023fS(RaidEventType.SupplyFacility, InteractionPointType.ObjectPosition,
                (ushort)visualId);
            if (cache._0023GS(key) && cache._0023FS(key).ConvexPolygon is { } shapes)
            {
                foreach (ConductorGameLogic.Collision.LevelGeometry.PolygonShapeGeometry shape in shapes)
                {
                    overlapping += shape._0023hkd.Vertices.Count(vertex => Ray(ToWorld(center, facing, vertex)));
                }
            }
        }
        catch (Exception)
        {
            // 碰撞数据读不出来就只看周围
        }

        int hemmedIn = 0;
        for (int k = 0; k < CrampedDirections; k++)
        {
            double angle = k * Math.PI * 2 / CrampedDirections;
            if (Ray(center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * CrampedReach))
            {
                hemmedIn++;
            }
        }

        detail = $"墙体和地形重叠 {overlapping} 处, {CrampedDirections} 个方向里 {hemmedIn} 个 {CrampedReach:F0} 以内碰壁";
        return overlapping > 0 || hemmedIn >= CrampedHemmedIn;
    }

    private const int CrampedDirections = 16;
    private const float CrampedReach = 90f;
    private const int CrampedHemmedIn = 11;

    /// <summary>这个变体的门口布局。名字认不出来按小屋算。</summary>
    public static Layout LayoutFor(string visualName) =>
        visualName != null && Layouts.TryGetValue(visualName, out Layout layout) ? layout : Layouts["Shed"];

    private void Stock(GameManager simulation)
    {
        _before = new HashSet<GameObjectBase>(simulation.ActiveGameObjectList
            .Where(o => o is _0023EF._0023ci or _0023aX._0023yGc));

        _0023EF._0023cyc thrower = simulation.GetGameObjectFromPool<_0023EF._0023cyc>();
        if (thrower == null || thrower.IsDummyObject)
        {
            Stocked = true;
            Opened = true;
            _before = null;
            Events.Add("十字路口补给设施: 门开了, 抛撒器没拿到 (池空?), 直接算完成");
            return;
        }

        thrower._0023Fc = Inside;
        thrower._00237q = Crates;
        thrower._0023Pec = SupplyThrower.DefaultSuppliesPerCrate;
        thrower._0023KP = Scatter;
        simulation.SpawnGameObject(thrower, null, null);

        Stocked = true;
        _thrower = thrower;
        Events.Add("十字路口补给设施: 门打开了, 屋里放补给");
    }

    /// <summary>
    /// 抛的过程中场上新冒出来的补给箱/补给 = 这一屋的。地上的只认屋子附近的 ——
    /// 同一时刻别处 (占领点、平民) 也可能在抛。
    /// </summary>
    private void CollectPickups(GameManager simulation)
    {
        foreach (GameObjectBase item in simulation.ActiveGameObjectList)
        {
            List<(GameObjectBase Pickup, int Uses)> into = item switch
            {
                _0023EF._0023ci => _crates,
                _0023aX._0023yGc landed when Vector2.Distance(landed._0023B, Inside) <= Scatter + 60f => _pickups,
                _0023aX._0023yGc => null,
                _ => null,
            };

            if (into == null || !item.IsActive || (_before != null && _before.Contains(item))
                || into.Any(p => ReferenceEquals(p.Pickup, item) && p.Uses == item.NumberOfGlobalCacheUses))
            {
                continue;
            }

            into.Add((item, item.NumberOfGlobalCacheUses));
        }
    }

    /// <summary>还在地上 —— 捡走会回池 (不活跃), 回池再被取走会换一个使用次数。</summary>
    private static bool Alive(GameObjectBase pickup, int uses) =>
        pickup.IsActive && pickup.NumberOfGlobalCacheUses == uses;

    /// <summary>本地 (+X = 朝向) -&gt; 世界。和客户端 <c>PolygonShape.Rotate(direction)</c> 同一个旋转。</summary>
    private static Vector2 ToWorld(Vector2 origin, Vector2 facing, Vector2 local) =>
        origin + new Vector2(local.X * facing.X - local.Y * facing.Y,
                             local.X * facing.Y + local.Y * facing.X);
}
