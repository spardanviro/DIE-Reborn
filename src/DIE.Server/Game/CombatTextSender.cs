using ConductorGameLogic;
using ConductorGameLogic.Abilities;
using ConductorGameLogic.Entities;
using StunGameNetwork;

namespace DIE.Server.Game;

/// <summary>
/// 战斗飘字 (SCT = Scrolling Combat Text) 和击中特效。
///
/// ## 传输层是完整的, 只差一个转发
///
/// <c>SCTSynchronizer</c> (ConductorGameLogic.Abilities, 160 行) 全都在:
/// 每客户端一个队列、序列化、同步位维护。而且它已经被接好了 ——
///
///   GameManager.cs:243   protected SCTSynchronizer _SCTSynchronizer;
///   GameManager.cs:1196  new SCTSynchronizer(this)
///   GameManager.cs:1677  _SCTSynchronizer.Update()          每帧
///   GameManager.cs:2508  _SCTSynchronizer.Reset()
///   GameManager.cs:2727  NetworkManager.AddSynchronizable(_SCTSynchronizer)
///
/// **唯一断掉的是 <c>GameManager.SendSCT</c> 那两个空 virtual** (:2889/:2893)。
/// 它们本该把数据丢进同步器, 结果什么都不做 —— 于是伤害数字、暴击、闪避、
/// 免疫、升级提示、击中特效全部无声消失, 零报错。
///
/// 客户端那侧的接收是现成的 (<c>GameManagerClient.GotSCT</c>:1065)。
///
/// ## 击中特效也走这条路
///
/// <c>SCTData.ShowHitEffect</c> —— 打击感的那一下闪光/顿挫是 SCT 载荷的一个
/// 布尔位, 不是独立的特效消息。所以"没有击中特效"和"没有伤害数字"是**同一个**
/// 缺口, 修一处两个都回来。
///
/// ## 各 SendXxxText 到 SCTData 的映射
///
/// 是**字面对应**的, 不是猜的 —— <c>SCTData</c> 的字段名和方法名一一对上:
///
///   SendMissText      -&gt; IsMiss        SendEvadeText     -&gt; IsEvade
///   SendImmuneText    -&gt; IsImmune      SendAbsorbText    -&gt; IsAbsorb
///   SendInterruptText -&gt; IsInterrupt   SendLevelUpText   -&gt; LevelUp
///   SendSupplyText    -&gt; IsSupply + Value
///   SendCombatText    -&gt; Combat
/// </summary>
public static class CombatTextSender
{
    /// <summary>
    /// 诊断: 上一次伤害的 <c>ownerChanger</c> 解析结果。
    ///
    /// 击中特效不播时, 这两个值能立刻分清是
    ///   (a) Owner 链断了 -> ownerChanger 为 null
    ///   (b) 找到了但不是玩家 -> 客户端那句 `== LocalPlayer` 过不了
    ///   (c) 两者都正常 -> 问题在客户端的模型层 (CharacterModel 为 null 之类)
    ///
    /// 三种成因的修法完全不同, 而表现一模一样。
    /// </summary>
    public static bool LastOwnerChangerWasNull;
    public static bool LastOwnerChangerWasPlayer;

    /// <summary>
    /// 伤害/治疗飘字。由 <c>OnCharacterDamageTaken</c> 调。
    ///
    /// ## 发给谁
    ///
    /// 两个人: **挨打的**和**打人的**。原厂在护盾那段就是这么写的
    /// (Character.cs:4290-4294) —— 先发给受害者自己的 Client, 再发给
    /// 攻击者所属玩家的 Client。照抄那个模式。
    ///
    /// 只发给受害者的话, 玩家打怪时看不到自己造成的伤害数字 —— 而那正是
    /// 打击感的主要来源。
    /// </summary>
    public static void SendDamage(GameManager simulation, SCTSynchronizer synchronizer,
                                  Character victim, _00230k._0023Nh change)
    {
        if (synchronizer == null || victim == null || change == null)
        {
            return;
        }

        SCTData data = default;
        data.StatType = change.StatType;
        data.Value = change._00237zb;
        data.StartValue = change._00237zb;
        data.StatChangeReason = change.ChangeReason;
        data.StatChangeType = change.ChangeType;

        // 这四个标志的语义由 _0023tK 的构造函数参数名给出 (-0k/-tK.cs:60):
        //   (..., bool isTranced, bool isCrit, bool isPunctured, bool isBlocked)
        //     _0023gAc      _0023fwc     _0023eAc         _0023fAc
        data.IsTranced = change._0023gAc;
        data.IsCrit = change._0023fwc;
        data.IsPunctured = change._0023eAc;
        data.IsBlocked = change._0023fAc;

        // 致命一击 —— 客户端据此播更重的表现。
        data.IsKillingBlow = victim.IsDead;

        // 打击感的那一下。伤害 (负数) 才有, 治疗没有。
        data.ShowHitEffect = change._00237zb < 0f;

        // ⚠️ ownerChanger 是击中特效能不能播的**决定性**参数。
        //
        // 客户端 ViewManager.TakeHitEffect (ViewManager.cs:2171) 第一句就是:
        //
        //     if (ownerChanger == LocalPlayer || entity == LocalPlayer) flag = true;
        //     if (flag && ...) CharacterModel.OnHitEffect();
        //
        // 玩家打僵尸时 entity 是僵尸, 所以**只能靠 ownerChanger 等于本地玩家**
        // 这一条过关。它要是 null 或者不是玩家本人, 伤害数字照出、特效不播 ——
        // 正是真机上看到的现象。
        //
        // FindEntityInOwners (GameManager.cs:2330) 顺着 Owner 链往上找:
        // Changer 是技能生成的判定对象 (日志里的 _00233wc), 不是 Entity,
        // 所以走 FindCharacterInOwners(Changer.Owner) 那条分支。
        Entity ownerChanger = simulation.FindEntityInOwners(change.Changer);

        LastOwnerChangerWasNull = ownerChanger == null;
        LastOwnerChangerWasPlayer = ownerChanger is { IsPlayer: true };

        // 1) 挨打的人自己
        Send(synchronizer, victim.Client, victim, ownerChanger, change.Changer, data);

        // 2) 打人的那个玩家 —— 照抄 Character.cs:4291 的判定链
        if (change.Changer?.Owner is { IsPlayer: true } attacker
            && attacker.AsPlayer.Client != victim.Client)
        {
            Send(synchronizer, attacker.AsPlayer.Client, victim, ownerChanger, change.Changer, data);
        }
    }

    /// <summary>
    /// 只有一个 <c>bool</c> 要置的那一类飘字 (miss / evade / immune / ...)。
    /// </summary>
    public static void SendFlag(SCTSynchronizer synchronizer, Player player, Entity target,
                                Entity owner, GameObjectBase changer, SCTFlag flag, byte value = 0)
    {
        if (synchronizer == null || player == null || target == null)
        {
            return;
        }

        SCTData data = default;

        switch (flag)
        {
            case SCTFlag.Miss:      data.IsMiss = true;      break;
            case SCTFlag.Evade:     data.IsEvade = true;     break;
            case SCTFlag.Immune:    data.IsImmune = true;    break;
            case SCTFlag.Absorb:    data.IsAbsorb = true;    break;
            case SCTFlag.Interrupt: data.IsInterrupt = true; break;
            case SCTFlag.LevelUp:   data.LevelUp = true;     break;

            case SCTFlag.Supply:
                data.IsSupply = true;
                data.Value = value;
                break;

            case SCTFlag.Combat:
                data.Combat = value;
                break;
        }

        Send(synchronizer, player.Client, target, owner, changer, data);
    }

    public enum SCTFlag
    {
        Miss,
        Evade,
        Immune,
        Absorb,
        Interrupt,
        LevelUp,
        Supply,
        Combat,
    }

    /// <summary>
    /// 丢进同步器。
    ///
    /// <c>SCTSynchronizer.SendSCT</c> (:121) 自己判了 client 为 null 就丢弃,
    /// 但它会先解引用 <c>_0023Mb.GetSpawnIdForClient</c> —— 所以 target 不能为 null,
    /// 这个由调用方保证。
    /// </summary>
    private static void Send(SCTSynchronizer synchronizer, IClient client, Entity target,
                             Entity ownerChanger, GameObjectBase changer, SCTData data)
    {
        if (client == null)
        {
            return;
        }

        synchronizer.SendSCT(client, target, ownerChanger, changer, data);
    }
}
