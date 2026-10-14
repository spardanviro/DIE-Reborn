using System;
using ConductorGameLogic;
using StunGameNetwork;
using _0023rye;

namespace DIE.Server.Game;

/// <summary>
/// 服务端的 <see cref="NetworkBase"/> 派生类 —— 对应客户端的 <c>GameClient</c>。
///
/// ## 为什么要自己写
///
/// 客户端程序集里只有 <c>GameClient</c>, 没有 GameServer —— 那半边从来没发布过。
/// 但 <see cref="NetworkBase"/> (940 行, 同步表、客户端表、定步长驱动全在里面)
/// 是**双端共享**的, 只留了三个抽象点给两侧各自填:
///
///   UpdateGame          —— 推进一逻辑帧
///   CreateGameMessage   —— 建出站缓冲
///   QueueGameMessage    —— 把缓冲投递给某个客户端
///
/// 也就是说服务端要补的只有传输层这三处, 定步长循环本身是现成的:
/// <c>NetworkBase.Update(totalSeconds)</c> 累积 delta, 按 <c>TargetDelta</c>
/// (= 1/FpsLimit) 切帧, 每帧调 <c>DoFrame</c> -> <c>UpdateGame</c>。
/// 我们不需要自己写时间累积和追帧逻辑, 那样反而会和客户端的节奏对不上。
/// </summary>
public sealed class GameServer : NetworkBase
{
    private readonly Func<int, _00237ze> _createBuffer;

    public GameServer(int maxPlayers, int maxObservers, float fpsLimit,
                      Func<int, IClient> createClient, Func<int, _00237ze> createBuffer)
        : base(maxPlayers, maxObservers, fpsLimit, createClient)
    {
        _createBuffer = createBuffer;
    }

    /// <summary>本局的模拟层。由 <c>LidgrenHost</c> 在同一个线程上建好后挂进来。</summary>
    public GameManager GameManager { get; set; }

    /// <summary>
    /// 整张同步表, 下标即线上的 index。
    ///
    /// 基类里是 protected —— 只有派生类能看到。出站帧的写入器需要它,
    /// 所以在这里以只读方式暴露出去。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<SynchronizableData> Synchronizables
        => _SynchronizableList;

    /// <summary>整张客户端表 (长度 = MaxClients), 出站帧的控制器段要按它逐个写。</summary>
    public System.Collections.Generic.IReadOnlyList<IClient> Clients => _ClientList;

    /// <summary>
    /// 把一条游戏消息排进某客户端的可靠通道。
    ///
    /// 通道对象由宿主 (LidgrenHost) 按连接持有 —— 它才知道哪个 IClient 对应哪条
    /// Lidgren 连接。所以这里只是转发。
    /// </summary>
    public System.Action<_00237ze, IClient, int, bool, float> QueueOutgoing { get; set; }

    /// <summary>
    /// 一逻辑帧结束、该把出站帧发出去了。
    ///
    /// 由宿主实现: 组装并投递。放在宿主侧是因为组帧要用到 Lidgren 连接和每连接的
    /// 可靠通道, 那些都是宿主的东西。
    /// </summary>
    public System.Action FlushFrames { get; set; }

    /// <summary>
    /// 分段计时 (宿主挂上来)。一逻辑帧拆成三段: 宿主的帧前逻辑 (刷怪 / AI 脑子)、
    /// 原厂模拟、组帧下发。见 <see cref="Match.FrameProfiler"/>。
    /// </summary>
    public Match.FrameProfiler Profiler
    {
        get => _profiler;
        set
        {
            _profiler = value;
            if (value != null)
            {
                ProfileBefore = value.Register("帧前逻辑");
                ProfileSimulation = value.Register("原厂模拟");
                ProfileFlush = value.Register("组帧下发");
            }
        }
    }

    private Match.FrameProfiler _profiler;
    private int ProfileBefore;
    private int ProfileSimulation;
    private int ProfileFlush;

    /// <summary>
    /// 建一个服务端 -> 客户端的游戏消息缓冲。
    ///
    /// ## 开头那个字节是必须的, 而且方向不对称
    ///
    /// 可靠通道上还有**一层自己的**消息类型, 在 <c>ConductorGameMessageType</c>
    /// 之外。客户端收到可靠消息后先读它 (<c>GameClient._0023KCe</c>:267):
    ///
    ///     switch ((_0023ZCe)buf.ReadByte())
    ///     {
    ///     case _0023ZCe._0023XCe:   // 0 —— 一批 ClientInfo
    ///         int n = buf.ReadByte();
    ///         for (i &lt; n) GetClient(buf.ReadByte()).ClientInfoServerToClientDeserialize(buf);
    ///     case _0023ZCe._0023YCe:   // 1 —— 游戏消息
    ///         GameManager.DeserializeGameMessage(buf);
    ///     }
    ///
    /// **不对称**: 客户端自己发消息给服务端时不写这一层 —— 它的
    /// <c>CreateGameMessage</c> (GameClient.cs:739) 只做了 EnableNetDebugging。
    /// 因为服务端那侧的处理器是自定义的, 而客户端的处理器是写死的。
    ///
    /// 这解释了一个我们踩了两轮的现象: **入站一直正常, 只有出站坏**。
    ///
    /// ## 漏掉它的后果
    ///
    /// 少这一个字节, 客户端就把 <c>ConductorGameMessageType.GameMessage</c>(0)
    /// 当成了 <c>_0023XCe</c>(0), 于是把下一个字节 (具体消息类型) 当成"有几条
    /// ClientInfo"去循环 <c>GetClient(...)</c> —— 直接
    /// <c>IndexOutOfRangeException</c>。
    ///
    /// 真机两端日志同一时刻对上了:
    ///   服务端 12:40:33  销毁通知 -> _0023rPb#8003 死亡=True HP=-2.0/35.0
    ///   客户端 11:40:33  deserializeReliableMessage exception: IndexOutOfRange
    ///                    at GameClient.#KCe
    ///
    /// ## 为什么不在这里做 EnableNetDebugging
    ///
    /// 客户端的 <c>CreateGameMessage</c> 会调它, 但那只是往缓冲对象上打个本地
    /// 标志 (<c>LidgrenNetBuffer._0023Xze</c> 就是个字段赋值), **不写线上的位**。
    /// 我们不用调试通道, 所以不需要。
    ///
    /// ## 要发 ClientInfo 批的话
    ///
    /// 那条走 <c>_0023XCe</c>(0), 不能用本方法 —— 需要另建一条路。目前没有用到。
    /// </summary>
    public override _00237ze CreateGameMessage(int initialSizeBytes)
    {
        _00237ze buffer = _createBuffer(initialSizeBytes);
        buffer?.Write((byte)_0023ZCe._0023YCe);
        return buffer;
    }

    /// <summary>
    /// 建一条 **ClientInfo 批**消息 —— 通道层的另一个分支。
    ///
    /// 客户端 <c>GameClient._0023KCe</c> (:269) 收到 <c>_0023XCe</c>(0) 时:
    ///
    ///     int n = buf.ReadByte();
    ///     for (i &lt; n) {
    ///         int index = buf.ReadByte();
    ///         GetClient(index).ClientInfoServerToClientDeserialize(buf);
    ///     }
    ///
    /// 所以载荷是 `数量 + N×(槽位号 + ClientInfo)`。
    ///
    /// ## 为什么必须有这条路
    ///
    /// <c>Client.ClientInfoData</c> 装着**装备**: 武器1/武器2/饰品1/饰品2、
    /// 相机朝向、队伍、显示名。服务端改了它 (比如教程发武器时
    /// <c>EquipHaxWeaponNew</c> 会写 <c>_0023lYb</c> 和 <c>_0023sYb</c>),
    /// 不发过去客户端就**一无所知**。
    ///
    /// 后果比"武器图标没更新"严重得多: 客户端按它以为的装备去发技能,
    /// 服务端按真实装备去校验, 两边的技能栏对不上 —— <c>TryUseAbility</c>
    /// 找不到那个技能, 攻击**静默失败**。表现是"挥手但什么都打不掉"。
    /// </summary>
    public _00237ze CreateClientInfoMessage(int initialSizeBytes)
    {
        _00237ze buffer = _createBuffer(initialSizeBytes);
        buffer?.Write((byte)_0023ZCe._0023XCe);
        return buffer;
    }

    /// <summary>
    /// 一逻辑帧。对应 <c>GameClient.cs:398</c>, 但去掉了客户端专有的部分
    /// (录像、输入预测)。
    /// </summary>
    /// <summary>
    /// 模拟推进**之前**跑的服务端逻辑 (僵尸 AI、刷怪触发)。
    ///
    /// 必须在前面: <c>NPC.UpdateCharacter</c> 当帧就消费 <c>GoalTarget</c>,
    /// 跑在后面的话每个决策都要晚一帧才生效。
    ///
    /// 参数是 (deltaTime, totalTime)。
    /// </summary>
    public System.Action<float, float> BeforeSimulation { get; set; }

    protected override void UpdateGame(float deltaTime, bool skipSendDataFrame = false)
    {
        Match.FrameProfiler profiler = Profiler;
        profiler?.BeginFrame();

        using (Match.FrameProfiler.Measure(profiler, ProfileBefore))
        {
            BeforeSimulation?.Invoke(deltaTime, Time);
        }

        using (Match.FrameProfiler.Measure(profiler, ProfileSimulation))
        {
            GameManager?.Update(deltaTime, Time, CurrentFrame);
        }

        // 顺序和客户端相反: 客户端是"先发再收再模拟" (GameClient.cs:398),
        // 服务端是"先模拟再发" —— 因为服务端发的是模拟结果, 客户端发的是输入。
        if (!skipSendDataFrame)
        {
            using (Match.FrameProfiler.Measure(profiler, ProfileFlush))
            {
                FlushFrames?.Invoke();
            }
        }

        profiler?.EndFrame();
    }

    /// <summary>
    /// 把出站缓冲投递给某个客户端的可靠通道。
    ///
    /// <paramref name="client"/> 为 null 表示广播 —— 客户端那侧的同名参数也是这个
    /// 约定 (<c>NetworkBase.SendGameMessage</c> 的默认值就是 null)。
    /// </summary>
    protected override void QueueGameMessage(_00237ze netBuffer, IClient client, int channel,
                                             bool inOrder, float resendRate)
    {
        QueueOutgoing?.Invoke(netBuffer, client, channel, inOrder, resendRate);
    }
}
