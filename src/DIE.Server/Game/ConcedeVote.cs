using System;
using System.Collections.Generic;
using System.Linq;
using ConductorGameLogic;
using ConductorGameLogic.Entities;
using StunGameNetwork;

namespace DIE.Server.Game;

/// <summary>
/// 十字路口的**认输投票** (Options 菜单里的 Concede)。
///
/// ## 客户端整套现成, 服务端一行都没有
///
///   发起   <c>UI_OptionsMenuBindings.OnConcede</c>: 十字路口允许 (Scavenger/教程/Scout 不允许), 开局满
///          <c>_0023Sc._0023Rc</c> = 300 秒才能发, 个人冷却 90 秒 -&gt; 上行 <c>GameMessageType.Concede</c> + Concede
///   投票   <c>GUI_ConcedeMenu</c>: 收到下行 Concede -&gt; 弹 Yes/No + 15 秒倒计时 (<c>_0023Sc._0023Pc</c>),
///          发起人自动算一票 Yes; 每人投票上行 Yes/No, 下行 Yes/No 点亮对应的灯;
///          **倒计时走完客户端自己发 No**
///   结果   下行 Success -&gt; "ConcedeSucceeded" + <c>GameMode.WasConceded = true</c>; Failed -&gt; "ConcedeFailed"
///
/// 下行格式 (<c>GameManagerClient</c> :693): <c>[bool 带发起人][byte 客户端编号][byte 类型]</c>。
/// 服务端丢了: 收票、转发、判定、以及通过之后一局怎么收。
///
/// ## 我们定的
///
///   - 通过线: **赞成票严格过半** (在场玩家里)。一个人玩, 发起就通过
///   - 超时: 客户端 15 秒自己投 No, 服务端再宽 3 秒兜底 (掉线的人不投)
/// </summary>
public sealed class ConcedeVote
{
    /// <summary>客户端投票倒计时 (<c>_0023Sc._0023Pc</c>)。</summary>
    public const float ClientVoteSeconds = 15f;

    /// <summary>客户端要求开局满多久才能发起 (<c>_0023Sc._0023Rc</c>)。服务端也照这个挡。</summary>
    public const float MinimumMatchSeconds = 300f;

    private readonly Dictionary<int, bool> _votes = new Dictionary<int, bool>();
    private float _timeLeft;

    /// <summary>正在投票。</summary>
    public bool Active { get; private set; }

    /// <summary>认输通过了 (一局要收了)。</summary>
    public bool Passed { get; private set; }

    /// <summary>服务端兜底多等几秒。</summary>
    public float GraceSeconds { get; set; } = 3f;

    /// <summary>通过时回调 —— 宿主让十字路口导演收局。</summary>
    public Action<GameManager> OnPassed { get; set; }

    /// <summary>宿主读完打日志再清。</summary>
    public readonly List<string> Events = new List<string>();

    /// <summary>收到一条上行 Concede 消息 (类型那个字节已经读出来)。</summary>
    public void Receive(GameManager simulation, GameServer network, IClient from, ConcedeMessageType type)
    {
        if (simulation?.GameMode == null || from == null || Passed)
        {
            return;
        }

        switch (type)
        {
            case ConcedeMessageType.Concede:
                Start(simulation, network, from);
                break;

            case ConcedeMessageType.Yes:
            case ConcedeMessageType.No:
                Vote(simulation, network, from, type == ConcedeMessageType.Yes);
                break;
        }
    }

    /// <summary>每帧一次: 超时收票。</summary>
    public void Update(GameManager simulation, GameServer network, float deltaTime)
    {
        if (!Active)
        {
            return;
        }

        _timeLeft -= deltaTime;
        if (_timeLeft <= 0f)
        {
            Resolve(simulation, network, timedOut: true);
        }
    }

    private void Start(GameManager simulation, GameServer network, IClient from)
    {
        if (Active)
        {
            Events.Add($"认输: #{from.Index} 想发起, 但已经在投票了");
            return;
        }

        if (!simulation.GameMode.IsRaidMission)
        {
            Events.Add($"认输: #{from.Index} 想发起, 这个模式不许认输");
            return;
        }

        if (simulation.GameMode.MatchLength < MinimumMatchSeconds)
        {
            Events.Add($"认输: #{from.Index} 想发起, 开局才 {simulation.GameMode.MatchLength:F0} 秒");
            return;
        }

        Active = true;
        _votes.Clear();
        _timeLeft = ClientVoteSeconds + GraceSeconds;

        Send(network, from, ConcedeMessageType.Concede);
        _votes[from.Index] = true;          // 发起人算一票赞成 (客户端 ConcedeStarted 也这么画)

        Events.Add($"认输: #{from.Index} 发起投票 (在场 {Voters(simulation)} 人)");
        Resolve(simulation, network, timedOut: false);
    }

    private void Vote(GameManager simulation, GameServer network, IClient from, bool yes)
    {
        if (!Active || _votes.ContainsKey(from.Index))
        {
            return;
        }

        _votes[from.Index] = yes;
        Send(network, from, yes ? ConcedeMessageType.Yes : ConcedeMessageType.No);
        Events.Add($"认输: #{from.Index} 投{(yes ? "赞成" : "反对")} ({_votes.Count}/{Voters(simulation)})");

        Resolve(simulation, network, timedOut: false);
    }

    /// <summary>赞成过半 -&gt; 通过; 剩下的人全投赞成也过不了半 (或超时) -&gt; 不通过。</summary>
    private void Resolve(GameManager simulation, GameServer network, bool timedOut)
    {
        int voters = Math.Max(1, Voters(simulation));
        int yes = _votes.Values.Count(v => v);
        int undecided = Math.Max(0, voters - _votes.Count);

        if (yes * 2 > voters)
        {
            Active = false;
            Passed = true;
            Send(network, null, ConcedeMessageType.Success);
            simulation.GameMode.WasConceded = true;
            Events.Add($"认输: 通过 ({yes}/{voters} 赞成)");
            OnPassed?.Invoke(simulation);
            return;
        }

        if (timedOut || (yes + undecided) * 2 <= voters)
        {
            Active = false;
            Send(network, null, ConcedeMessageType.Failed);
            Events.Add($"认输: 没通过 ({yes}/{voters} 赞成{(timedOut ? ", 超时" : string.Empty)})");
        }
    }

    /// <summary>在场、连着的玩家数。</summary>
    private static int Voters(GameManager simulation) =>
        simulation.ActivePlayerList.Count(p => p != null && p.IsActive && p.Client != null);

    /// <summary>下行一条 Concede (<c>GameManagerClient</c> :693 的格式)。</summary>
    private static void Send(GameServer network, IClient from, ConcedeMessageType type)
    {
        if (network == null)
        {
            return;
        }

        foreach (Client recipient in network.ConnectedClientsList.OfType<Client>())
        {
            _0023rye._00237ze buffer = network.CreateGameMessage(16);
            if (buffer == null)
            {
                continue;
            }

            buffer.Write((byte)ConductorGameMessageType.GameMessage);
            buffer.Write((byte)GameMessageType.Concede);
            buffer.Write(from != null);
            if (from != null)
            {
                buffer.Write((byte)from.Index);
            }

            buffer.Write((byte)type);
            network.SendGameMessage(buffer, recipient);
        }
    }
}
