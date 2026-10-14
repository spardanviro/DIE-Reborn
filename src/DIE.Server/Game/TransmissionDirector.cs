using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using ConductorGameLogic.GameModes;
using ConductorGameLogic.MapObjects;
using ConductorGameLogic.View;


namespace DIE.Server.Game;

/// <summary>
/// 路径点语音 —— 玩家走进 <see cref="TransmissionSpawnPoint"/> 的区域就播一段无线电。
///
/// ## 玩家的质疑是对的
///
/// 我上一轮的结论是"服务端拿不到语音包 GUID, 这条路走不通"。玩家反问:
/// **"但是 tutorial 关卡里面的语音播报都正常还原了啊"** —— 顺着这条线查下去
/// 发现结论错了, 而且错在两个地方:
///
/// **一、教程语音根本不经服务端。** 教程 GameMode (双端共享代码)
/// 在状态切换时直接调 <c>ViewManager.PlayTransmissionPackage(
/// TransmissionEventID.TutorialXXX)</c> (`-bd/-5cc.cs:185/304/368...`),
/// 客户端自己播。我们只负责推进 <c>_0023l9._0023pcc</c>。
///
/// **二、Scout 这边有专门的消息, 不需要 GUID。**
/// 客户端 <c>GameManagerClient.cs:628</c>:
///
///     case GameMessageType.ObjectiveUpdate:
///         CrossroadsGUIUpdate update = new CrossroadsGUIUpdate();
///         update.Deserialize(netBuffer);
///         ViewManager.GetObjectiveManager().AddObjectiveItem(update);
///
/// 之后 <c>GUI_ObjectiveManager.StartNextObjective</c> 调
/// <c>SendTransmission(eventType, transmissionType, visualID, randomID)</c>
/// -&gt; <c>ViewManager.RaidEventTransmission</c> —— **客户端自己**去
/// <c>RaidEventView.Settings</c> (Unity 资源) 里找语音包。
/// 服务端只要发**三个枚举值**, GUID 那一步压根不归我们管。
///
/// 报文由原厂的 <see cref="CrossroadsGUIUpdate.AddTransmissionData"/> 写,
/// 我们只负责挑点、发消息。
///
/// ## 触发就是位置, 队列那一版是我想多了
///
/// 上上版是纯位置触发, 玩家报"时间对不上", 我就改成了"按
/// <see cref="TransmissionType"/> 枚举号排队, 只有队首能被触发, 打破路障也算
/// 推进"。**那一版是错的**, 玩家实测: "我按正常流程完成游戏走到卡车范围内
/// 还是没有语音播报, 说明不是我跳流程的问题。"
///
/// 把地图 dump 出来一比就清楚了 —— 编号顺序和玩家路线**完全错位**:
///
///   玩家实际走的区域顺序   06 → 07 → 09 → 04 → 08 → 05 → 03 → 02 → 卡车
///   队列强制的播放顺序     02 → 03 → 04 → 05 → 06 → 07 → 08 → 09 → 卡车
///
/// 玩家走到 06 的区域时队首是 02, 不播; 等队列被路障推到 02 时人早走远了。
/// 全程只有 3 组路障能推进, 队列最多走 3 步, 排在最后的
/// <c>DeliverSupplies</c> 永远轮不到 —— 这就是卡车语音不播的全部原因。
///
/// 现在回到位置触发, 也就是原厂的设计: 每个 <c>TransmissionSpawnPoint</c>
/// 自带一个 <c>Area</c>, 那个字段存在的意义就是"走进来就播这一条"。原厂
/// <c>GameMode.SpawnTransmissionPoints()</c> 把它们铺成静态对象
/// <c>_00237P._00239P</c>, 每个都带着自己的 <c>_00235j</c>(区域) ——
/// 那个类的三个方法在客户端树里被剥空了 (触发逻辑本来就在服务端),
/// 所以判定得我们自己写, 但**判据是它的字段告诉我们的**。
///
/// 代价照实说: 地图上有 8 条 <c>General_Transmission</c>, 而玩家报的流程里
/// 只有 5 条服务端语音。位置触发会把玩家路过的每一条都播出来, 可能多几条。
/// 多播哪一条玩家实测能直接报出来再删; 而队列那一版是**一条都到不了终点**,
/// 两害相权。
///
/// ## 每个点只播一次
///
/// 语音是一次性剧情, 反复播会盖掉后面的。另外一帧只播一条 ——
/// 两条挤在一起客户端会互相盖。
/// </summary>
public sealed class TransmissionDirector
{
    private sealed class Point
    {
        public int Id;
        public Event_Trigger Area;
        public RaidEventType EventType;
        public TransmissionType TransmissionType;
        public int VisualId;

        /// <summary>播过了就不再播。</summary>
        public bool Played;
    }

    private readonly List<Point> _points = new();

    private bool _scanned;

    /// <summary>登记了几个语音点。诊断用。</summary>
    public int ArmedPoints => _points.Count;

    /// <summary>还没播的有几条。诊断用。</summary>
    public int Remaining => _points.Count(p => !p.Played);

    /// <summary>播了几段。诊断用。</summary>
    public int Played { get; private set; }

    /// <summary>这一帧播了什么 —— 宿主拿去打日志。</summary>
    public readonly List<string> Events = new();

    /// <summary>
    /// 这两个不按位置播。
    ///
    /// <c>SpecialRamScoutTrigger</c> 是 <see cref="ScoutStoryDirector"/> 用来放
    /// Ram 的触发点, 不是剧情语音; <c>NotEnoughSupplies</c> 是"物资不够"的
    /// 条件提示 —— 它和 <c>DeliverSupplies</c> 共用卡车那个区域 (实测都在
    /// area=10014), 是一对互斥的条件分支, 按"走进来就播"处理的话玩家开局
    /// 站在卡车边上就会听到它。
    /// </summary>
    private static bool IsStoryTransmission(TransmissionType type) =>
        type != TransmissionType.SpecialRamScoutTrigger
        && type != TransmissionType.NotEnoughSupplies
        && type != TransmissionType.None;

    /// <summary>
    /// 认一遍语音点。幂等。
    ///
    /// 没有 <c>Area</c> 或者区域找不到的点直接丢掉 —— 它永远触发不了,
    /// 留着只会让诊断数字对不上。
    /// </summary>
    public void Scan(GameManager simulation)
    {
        if (_scanned || simulation?.MapObjects == null)
        {
            return;
        }

        _scanned = true;

        Dictionary<int, Event_Trigger> areas = simulation.MapObjects
            .OfType<Event_Trigger>()
            .GroupBy(t => t.ID)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (Event_Trigger area in areas.Values)
        {
            area.Polygon?.CalculateVerticesWorldSpace();
        }

        foreach (TransmissionSpawnPoint point in
                 simulation.MapObjects.OfType<TransmissionSpawnPoint>())
        {
            if (!IsStoryTransmission(point.TransmissionType))
            {
                continue;
            }

            if (!areas.TryGetValue(point.Area.ObjectID, out Event_Trigger area)
                || area.Polygon == null)
            {
                continue;
            }

            _points.Add(new Point
            {
                Id = point.ID,
                Area = area,
                EventType = point.EventType,
                TransmissionType = point.TransmissionType,
                VisualId = point.VisualID,
            });
        }
    }

    /// <summary>
    /// 走进哪个点的区域就播哪一条。一帧最多一条。
    /// </summary>
    public void Update(GameManager simulation, GameServer network)
    {
        Events.Clear();

        if (simulation == null || network == null)
        {
            return;
        }

        for (int i = 0; i < _points.Count; i++)
        {
            Point point = _points[i];

            if (point.Played || !AnyPlayerInside(simulation, point.Area))
            {
                continue;
            }

            // 卡车那一条要等玩家真的背着东西回来 —— 它的区域就在出生点旁边
            // (实测离出生点 35), 不加这个条件的话开局第一帧就播掉了。
            //
            // 判据是原厂客户端用的那一个: 交付点 _00237P._0023edb 在半径 15 内
            // 且 supplies._0023Fj(IndexPlayer) > 0 时才推 DeliverSupplies 交互提示
            // (-7P/-edb.cs:62-65)。语音和那个提示说的是同一件事。
            if (point.TransmissionType == TransmissionType.DeliverSupplies
                && !AnyPlayerCarryingSupplies(simulation))
            {
                continue;
            }

            point.Played = true;
            Played++;

            int sent = Broadcast(network, point);
            Events.Add($"语音点 #{point.Id} 触发: {point.EventType}/{point.TransmissionType} "
                       + $"visual={point.VisualId} (下发 {sent} 份, 还剩 {Remaining} 条没播)");

            return;   // 一帧只播一条
        }
    }

    /// <summary>换局时清干净。</summary>
    public void Reset()
    {
        _scanned = false;
        _points.Clear();
        Events.Clear();
        Played = 0;
    }

    /// <summary>
    /// 发 <c>GameMessageType.ObjectiveUpdate</c>。
    ///
    /// 载荷由原厂的 <see cref="CrossroadsGUIUpdate"/> 写 —— 我们只拼外面
    /// 那两个字节 (通道类别 + 消息类型), 和别的游戏消息一样。
    /// </summary>
    private static int Broadcast(GameServer network, Point point)
    {
        CrossroadsGUIUpdate update = new CrossroadsGUIUpdate();
        update.AddTransmissionData(point.EventType, point.TransmissionType, point.VisualId);

        int sent = 0;

        foreach (Client recipient in network.ConnectedClientsList.OfType<Client>())
        {
            _0023rye._00237ze buffer = network.CreateGameMessage(64);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.ObjectiveUpdate);
            update.Serialize(buffer);

            network.SendGameMessage(buffer, recipient);
            sent++;
        }

        return sent;
    }

    private static bool AnyPlayerInside(GameManager simulation, Event_Trigger area)
    {
        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            if (_00236._0023huc._00237tc(area, player.Position, player.Radius))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 有人身上背着补给吗。
    ///
    /// 认接口 <c>_0023bd._0023ad</c> 而不是具体模式 —— 理由和
    /// <see cref="SupplyRegistrar"/> 那边一样。
    /// </summary>
    private static bool AnyPlayerCarryingSupplies(GameManager simulation)
    {
        if (simulation.GameMode is not _0023bd._0023ad withSupplies)
        {
            return false;
        }

        Supplies supplies = withSupplies._00239c();
        if (supplies == null)
        {
            return false;
        }

        foreach (Player player in simulation.ActivePlayerList)
        {
            if (player == null || !player.IsActive || player.IsDead)
            {
                continue;
            }

            if (supplies._0023Fj(player.IndexPlayer) > 0)
            {
                return true;
            }
        }

        return false;
    }
}
