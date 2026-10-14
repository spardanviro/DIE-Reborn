using System;
using ConductorGameLogic.GameModes;

namespace DIE.Server.Game;

/// <summary>
/// 拾荒模式的服务端壳子 —— 只为了把**原厂自己的开局倒计时**接上。
///
/// ## 为什么需要一个子类 (2026-09-19)
///
/// 原厂那段"在出生区等人 -&gt; 报秒 -&gt; 开局"写得完完整整, 在
/// <c>GameMode.UpdateGameCountDown</c> (GameMode.cs:1890) 里 —— 可它**全树零调用方**,
/// 因为调它的是 <c>UpdateGameMode</c> 的服务端覆盖, 随服务端丢了。
///
/// 先前我们的做法是在宿主里照着它重写一遍 (<c>MatchStartGate</c> + <c>LidgrenHost.UpdateMatchStart</c>)。
/// 玩家 2026-09-19: "GameMode.UpdateGameCountDown 这里让原厂代码自己跑。" 所以改成派生一个类,
/// 在 <see cref="UpdateGameMode"/> 里调那两个 <c>protected</c> 方法 —— 报秒的时机、公告的种类、
/// 强制开局时长的夹取, 全部回到原厂那一份, 我们只负责"怎么发出去"和"开局那一下做什么"。
///
/// 配套的三个发送钩子在 <see cref="GameManagerServer"/>: <c>SendChat</c> /
/// <c>SendCountdownStarted</c> / <c>SendMissionStartCharacterSound</c>。
///
/// ## 原厂那段是什么规则
///
///   人满 (<c>NumOfPlayersPerTeam × NumOfTeams</c>, 拾荒 4 × 3 = 12)
///     -&gt; <c>StartCountDown(20)</c>: 发 CountdownStarted, 20 秒倒计时
///        (若 <c>EnableForceStart</c>, 时长夹到 <c>ForceStartTime - Time</c> 以内),
///        剩 10 秒发 AllReadyAndMatchCountdown10 + 开场语音, 5..1 报秒, 到点 MatchStarted。
///   人不满 -&gt; 按 <c>ForceStartTime - totalTime</c> 在 45 / 30 / 15 秒各报一次,
///        剩 10 秒放开场语音, 5..1 报秒, 到点 MatchStarted。这一支**不发 CountdownStarted**。
///
/// 两支都以 <c>Start()</c> 收尾 —— 那也是个空虚方法, 见 <see cref="Start"/>。
///
/// ## 同步表不受影响
///
/// 同 <see cref="RaidMissionServer"/>: 这个子类**不加任何同步对象**
/// (<c>ScavengerHunt.InitializeGameMode</c> 是 <c>sealed</c>, 想加也加不了),
/// 所以客户端那份 <c>ScavengerHunt</c> 和服务端这份数出来一样多。
/// </summary>
public sealed class ScavengerHuntServer : ScavengerHunt
{
    /// <summary>
    /// 开局那一下 —— 宿主接它去置 <c>_Started</c> 并把世界布置起来。
    ///
    /// 原厂 <c>GameMode.Start()</c> (GameMode.cs:1839) 是**空虚方法**: 倒计时走完时
    /// <c>UpdateGameCountDown</c> 调它, 真正开局要做什么由服务端决定 —— 那一半丢了。
    /// </summary>
    public Action OnStart { get; set; }

    /// <summary>
    /// 现在在 20 秒倒计时里吗 (诊断 / 日志用)。
    ///
    /// 原厂那个标志是 private, 只能从外部观察: <c>StartCountDown</c> 发过
    /// <c>CountdownStarted</c> 就说明进了倒计时支。
    /// </summary>
    public bool CountdownRunning { get; private set; }

    public override void Start()
    {
        base.Start();
        CountdownRunning = false;
        OnStart?.Invoke();
    }

    /// <summary>
    /// 每帧推进。<c>GameMode.Update</c> 末尾调这里 —— 开局前也会调 (那一支同时还在
    /// 放 <c>Vfx_GameStart_01</c> 那圈墙、把人按在出生区里, 见 GameMode.cs:1376)。
    /// </summary>
    protected override void UpdateGameMode(float deltaTime, float totalTime)
    {
        base.UpdateGameMode(deltaTime, totalTime);

        if (_Started)
        {
            return;
        }

        // 人满了就起 20 秒倒计时。ShouldStartCountdown() 里的人数判据是原厂的
        // (ActivePlayerList 数量 >= NumOfPlayersPerTeam × NumOfTeams); StartCountDown
        // 自己会防重入, 所以每帧调是安全的。
        if (ShouldStartCountdown() && !CountdownRunning)
        {
            CountdownRunning = true;
            StartCountDown();
        }

        UpdateGameCountDown(deltaTime, totalTime);
    }
}
