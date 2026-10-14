using System;

namespace DIE.Server.Game;

/// <summary>
/// 占领 / 读条时"圈里几个人"对应的速度倍率 —— **原版视频数出来的**。
///
/// ## 数据来源
///
/// 玩家 2026-09-21 看原版视频数的: <b>1 人 ×1.0、2 人 ×1.5、3 人 ×1.8、4 人 ×2.0</b>。
///
/// 我们原来三处 (拾荒补给点 / 教程补给点 / Scout 和十字路口的任务点) 各写了一遍
/// **线性** 的"每多一人 +50%"(1 / 1.5 / 2.0 / 2.5)。那条的依据是玩家更早的一次实测
/// "一个人是 100%, 每多一个人增加 50%" —— 现在看来那次只验到两人, 而原版从第三人
/// 开始就是**边际递减**的。两人都是 1.5, 所以当时对得上; 三人四人我们分别快了
/// 11% 和 25%。
///
/// ## 四人封顶
///
/// 原版一队最多 4 人 (十字路口 4, 拾荒 4v4v4 每队 4, 教程 1), 所以表只有四格,
/// 超出的按最后一格算。真出现第五个人也只是不再更快, 不会外推出原版没有的数。
///
/// ## 客户端会看见这个数
///
/// 补给点那条会把 <c>倍率 × 100</c> 当整数同步下去 (<c>_0023htb</c>),
/// 客户端显示成 "Capturing Speed x1.5"。这张表算出来正好是
/// 100 / 150 / 180 / 200, 都是整数, 不会有取整误差。
/// </summary>
public static class CaptureSpeed
{
    /// <summary>
    /// 一人到四人的倍率。**原版的数**, 要改先拿新的实测来。
    ///
    /// 想整体调快慢请动各自的基准时长 (<see cref="StorageRoomCaptureDirector.SecondsToCapture"/> /
    /// <see cref="ScoutObjectiveDirector.SecondsToComplete"/>), 别动这张表 ——
    /// 那样"原版的数"和"我们的调整"才分得清。
    /// </summary>
    public static readonly float[] ByHolders = { 1f, 1.5f, 1.8f, 2f };

    /// <summary>
    /// 圈里 <paramref name="holders"/> 个人时的倍率。没人是 0 (进度不涨),
    /// 超过表长按最后一格。
    /// </summary>
    public static float For(int holders)
    {
        if (holders <= 0)
        {
            return 0f;
        }

        return ByHolders[Math.Min(holders, ByHolders.Length) - 1];
    }

    /// <summary>
    /// 同上, 但换成客户端那个百分制整数 (100 = ×1.0)。
    /// 没人时返回 0 —— 补给点那边用它表示"没人在占"。
    /// </summary>
    public static int Percent(int holders) => (int)Math.Round(For(holders) * 100f);
}
