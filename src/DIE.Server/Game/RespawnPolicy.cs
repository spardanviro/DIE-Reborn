using ConductorGameLogic;
using ConductorGameLogic.Entities;

namespace DIE.Server.Game;

/// <summary>
/// 玩家复活 —— 决定"能不能现在活过来"。
///
/// ## 客户端那侧是无条件的
///
/// <c>UnityClient.cs:2331</c> 里, 只要角色处于死亡态, 按下鼠标左键就直接
/// <c>SendRespawn()</c>, **没有任何本地冷却判定**:
///
///     if (FullGameplayInputActive &amp;&amp; (!IsActive || IsDead || IsDeathState))
///     {
///         if (Input.GetKeyDown(KeyCode.Mouse0))
///             gameManager.SendRespawn();
///     }
///
/// 消息本身也是空的 —— <c>SendRespawn</c> (GameManagerClient.cs:1280) 只写了
/// 消息头, 一个字节的载荷都没有。
///
/// 也就是说客户端表达的是"**我想复活**", 判不判得过完全由服务端说了算。
/// 玩家会连点, 所以这里必须自己拦, 不能假设客户端只在冷却结束后才发。
///
/// ## 判据是现成的, 而且已经在同步了
///
/// | 量 | 谁维护 | 说明 |
/// |---|---|---|
/// | <c>Character.TimeDead</c> | 原厂, 逐帧累加 (Character.cs:2578) | 死了多久 |
/// | <c>Player.GetRespawnTime()</c> | 原厂, 死亡时算好 (Player.cs:2045 调 SetRespawnTime) | 要等多久 |
///
/// 两者都**已经复制到客户端** (Player.cs:1690-1694 和 :1721) —— HUD 上那个
/// 复活倒计时读的就是它们。所以服务端只要按同一组数字判, 玩家看到的读秒
/// 归零的那一刻, 正好就是服务端放行的那一刻, 不需要我们再造一套时间。
///
/// <c>SetRespawnTime()</c> 本身也是完整的 (Player.cs:2239), 按模式分支:
/// 被"夺魂"、Scout、Raid、TvT、以及默认的 `基础 + 等级 × 系数`。我们一个
/// 分支都不用重写。
///
/// ## 丢的只有这一层判定
///
/// <c>Character.Respawn()</c> (Character.cs:2467) 和
/// <c>Player.RespawnCharacter()</c> (Player.cs:1839) 都是**完整**的 ——
/// 后者连教程里按进度发武器那段都在 (:1865-1874)。
///
/// 缺的就是"谁在什么条件下调它"。那是服务端的活, 随服务端丢失。
///
/// ## 教程后半段原厂其实不让自己复活
///
/// <c>UI_DeathPanelBindings</c> (:84-93) 在教程状态 &gt;= <c>_0023occ._0023hcc</c>
/// (第 8 个) 之后显示的是"等待队友救援" —— 那时候原厂要的是 Amber 走过来
/// 引导复活 (见 <see cref="TutorialNpcAI"/>), 不是玩家自己按左键。
///
/// **我们仍然保留自己复活**, 有意为之: Amber 万一被卡住 / 被打断 / 距离
/// 拉太远, 少了这条路玩家就永久躺在地上, 只能退出重开。原厂服务端敢关掉
/// 是因为那边 AI 是原装的; 我们这边她的行为层是自己写的, 留个兜底。
/// 代价只是那句提示语在她赶到之前显得不合时宜 —— 比软锁便宜得多。
/// </summary>
public static class RespawnPolicy
{
    /// <summary>拒绝的原因, 用于日志 —— 复活失败时玩家只看到"没反应", 得能查。</summary>
    public enum Result
    {
        /// <summary>复活成功。</summary>
        Respawned,

        /// <summary>这个客户端没有玩家实体。</summary>
        NoPlayer,

        /// <summary>没死, 不用复活 (玩家连点时的正常情况)。</summary>
        NotDead,

        /// <summary>还在读秒。</summary>
        StillOnCooldown,
    }

    /// <summary>
    /// 处理一条 <c>GameMessageType.Respawn</c>。
    /// </summary>
    /// <param name="spawnPoint">
    /// 复活地点。
    ///
    /// **必须由调用方给** —— <c>RespawnCharacter</c> 通篇没碰 <c>Position</c>,
    /// 它只负责"重新激活这个实体"。不设的话玩家会在**倒地的原地**站起来,
    /// 通常正是一群僵尸中间。选点属于服务端策略, 所以那段代码不在客户端里。
    /// </param>
    /// <param name="remaining">还要等几秒。仅在 <see cref="Result.StillOnCooldown"/> 时有意义。</param>
    public static Result TryRespawn(GameManager simulation, Client client,
                                    FarseerPhysics.Common.Vector2 spawnPoint,
                                    out float remaining)
    {
        remaining = 0f;

        Player player = client?.LocalPlayer as Player;
        if (player == null)
        {
            return Result.NoPlayer;
        }

        // IsDead 和 IsDeathState 是两个状态: 前者是"血空了", 后者含倒地动画。
        // 客户端在**两者任一**成立时就允许发消息 (UnityClient.cs:2329), 所以
        // 这里也按同样的口径判, 免得出现"客户端让按、服务端不认"的死局。
        if (!player.IsDead && !player.IsDeathState)
        {
            return Result.NotDead;
        }

        float required = player.GetRespawnTime();
        if (player.TimeDead < required)
        {
            remaining = required - player.TimeDead;
            return Result.StillOnCooldown;
        }

        // 位置要在 Respawn() **之前**设 —— 它内部会调 Spawn(), 而 Spawn 会把
        // 实体按当前 Position 塞回碰撞管理器 (Character.SpawnEntity)。
        // 之后再改 Position 的话, 逻辑位置和碰撞体就对不上了。
        player.Position = spawnPoint;

        // Respawn() 内部会先把两个持续效果摘掉, 再走 RespawnCharacter():
        // 从活动列表摘除 -> 清可见性位 -> 退出碰撞 -> Spawn() 重新激活
        // -> 教程按进度发武器 -> 把相机重新对准自己。
        //
        // 注意它自己会调 Spawn(), 所以**不要**在外面再调一次。
        player.Respawn();

        // RespawnCharacter 只重置了 LookAtCharacter (Player.cs:1876), **没碰**
        // ControlledCharacter。目前两者指向同一个实体, 所以补这一下是幂等的;
        // 但 Spawn() 走的是"先从活动列表摘除再重新激活"的路子, 显式重设一次
        // 才能保证控制权不会随着实体身份的变化而丢掉 —— 那个故障的表现是
        // "人复活了但按键全无响应", 和最初的黑屏是同一类。
        client.SetControlledCharacter(player);

        return Result.Respawned;
    }
}
