// _0023z._0023O9f —— 原名大概是 SteamWrapperClient (见 -z\-O9f.cs:525 的报错文案
// "Do not create additional SteamWrapperClients! Use .Instance instead.")。
//
// ## 为什么需要这个桩
//
// 原文件 `-z\-O9f.cs` 顶部 `using ManagedSteam;`, 被 sanitize_decompiled.py 的
// CLIENT_ONLY_MARKERS 过滤掉了 —— ManagedSteam 是 Steamworks 的托管封装, 只存在于
// 客户端进程, 服务端没有也不该有。但 GameManagerClient 还持有对它的引用, 于是 CS0246。
//
// 切掉是对的 (不切根本编不了), 补个桩也是对的 —— 遵循"只增不删"。
//
// ## 需要哪些成员
//
// 注意: grep 类型名 `_0023O9f` 只有 4 处 (字段声明 / 构造参数 / 两处 Instance.xxx),
// **但那不是用量**。真正的使用全走持有它的字段 _0023yhd, 共 37 处:
//
//   19  _0023yhd._0023p8f        解锁成就
//   13  _0023yhd._0023x8f        上报成就进度
//    2  _0023yhd._0023w8f        统计项加值
//    2  _0023yhd._0023G2f        读统计项
//    1  _0023yhd.Initialized     Steam 是否就绪
//    1  _0023yhd._0023G8f        StoreStats 落盘
//
// 第一版桩只写了 3 个成员就是漏数了这一层 —— 当时 CS0102 还没修完, Roslyn
// 在声明阶段有错时不绑定方法体, 这 37 处 CS1061 全被挡住了没报出来。
//
// ## 语义对齐
//
// 原实现里每个成员开头都是同一组守卫:
//   if (!_0023L7f) ...            _0023L7f = "Steam 功能已启用"
//   if (_0023jbc == null || !Initialized) ...   _0023jbc = Steam 实例
//
// 服务端进程没有 Steam, 这两个条件恒不成立, 原逻辑本身就是空转。
// 桩照抄这条 not-available 分支 —— 不是"假装成功", 而是复刻 Steam 不可用时的真实行为。
//
// Initialized 尤其重要: 原式是 `_0023ee ? (!_0023obc || _0023pbc) : false`,
// _0023ee 是"Steam 已启动", 没启动就直接 false。GameManagerClient:1068 用它做
// 短路守卫 —— 返回 false 正好让后面整段成就逻辑不执行, 与真实行为一致。
//
// Instance 不能返回 null —— :950 无条件解引用。

namespace _0023z
{
    public class _0023O9f
    {
        private static _0023O9f _instance;

        public static _0023O9f Instance => _instance ?? (_instance = new _0023O9f());

        private _0023O9f()
        {
        }

        /// <summary>原: `_0023ee ? (!_0023obc || _0023pbc) : false`。没有 Steam 进程时 _0023ee 恒 false。</summary>
        public bool Initialized => false;

        /// <summary>原: 给指定统计项加值 (Steam Stats)。Steam 不可用时返回 false。</summary>
        public bool _0023w8f(string statName, int delta)
        {
            return false;
        }

        /// <summary>原: Stats.GetStat(name, out value)。Steam 不可用时 out 置 0 并返回 false。</summary>
        public bool _0023G2f(string statName, out int value)
        {
            value = 0;
            return false;
        }

        /// <summary>原: 若尚未达成则 Stats.SetAchievement(name)。守卫 `if (_0023L7f)` 不成立时整体空转。</summary>
        public void _0023p8f(string achievementName)
        {
        }

        /// <summary>原: Stats.IndicateAchievementProgress(name, current, max)。Steam 不可用时返回 false。</summary>
        public bool _0023x8f(string achievementName, uint current, uint max)
        {
            return false;
        }

        /// <summary>原: SteamAPI Stats.StoreStats()。Steam 不可用时空转。</summary>
        public void _0023G8f()
        {
        }
    }
}
