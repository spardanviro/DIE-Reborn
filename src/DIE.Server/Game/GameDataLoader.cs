using System;
using System.IO;
using System.Runtime.CompilerServices;
using ConductorGameLogic;
using ConductorGameLogic.Collision;
using ConductorGameLogic.Gameplay;
using Serilog;

namespace DIE.Server.Game;

/// <summary>
/// 在构造 <see cref="ConductorGameLogic.GameManager"/> 之前, 把它依赖的全局游戏数据
/// 从游戏安装目录加载好。
///
/// ## 为什么必须在外面先做
///
/// <c>GameManager</c> 的**静态**构造函数会用到四份数据。它自己也有兜底加载分支,
/// 但那些分支调的是**无参**重载, 而无参重载用的是相对路径:
///
///   CacheManager.Load()                -> "cachedata.sexp" / "cacheattributs.sexp"
///   EffectCollisionData.Load()         -> "Maps/effectcollisions.data"
///   RaidEventSettingsData.Load()       -> "Maps/raideventsettings.data"
///   EnemyEventSettingsData.Load()      -> "Maps/enemyeventsettings.data"
///
/// 相对于**当前工作目录**。客户端进程的工作目录就是游戏根目录 (它自己日志里的
/// "Working directory: ...\Dead Island Epidemic" 就是这个), 所以在客户端里能work。
/// 服务端的工作目录是自己的输出目录, 找不到这些文件。
///
/// ## 为什么不直接改进程的工作目录
///
/// 那是全局状态, 会连带影响我们自己的数据文件 (账号库、物品图鉴) 的相对路径解析,
/// 而且一个进程里跑多局时没法按局隔离。这里改用**带路径的重载**先把数据装进
/// <c>_0023z._00232jd</c> 的那三个槽, 静态构造看到非 null 就不会再走无参兜底。
///
/// ## 失败必须是显式的
///
/// 静态构造函数整段包在 try/catch 里, 只往 StunLog 写一行就继续 —— 后果是
/// <c>TypeToGameObjectTypeID</c> 之类的静态表停在 null, 直到很久以后才以 NRE 的
/// 形式炸在 <c>GameManagerLargeObjects</c> 里, 完全看不出根因。
///
/// 所以这里逐个校验文件存在, 缺哪个就当场抛, 而不是让它漂到静态构造里去。
/// </summary>
public static class GameDataLoader
{
    /// <summary>程序旁边的游戏数据目录名 —— 服务器包 (tools\make-server-pack.ps1) 把数据放这里。</summary>
    public const string BundledDataFolder = "gamedata";

    /// <summary>
    /// 游戏数据从哪读 (cachedata.sexp、cacheattributs.sexp、Maps\), 按顺序:
    ///
    ///   1. 环境变量 DIE_GAME_ROOT
    ///   2. 程序旁边的 gamedata\ —— 云服务器上没装游戏, 服务器包自带这 7 MB 数据
    ///   3. 本机 Steam 的游戏安装目录 (开发机)
    /// </summary>
    public static string DefaultGameRoot
    {
        get
        {
            string fromEnv = Environment.GetEnvironmentVariable("DIE_GAME_ROOT");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv;
            }

            string bundled = Path.Combine(AppContext.BaseDirectory, BundledDataFolder);
            if (File.Exists(Path.Combine(bundled, "cachedata.sexp")))
            {
                return bundled;
            }

            return @"C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic";
        }
    }

    private static readonly object Gate = new object();
    private static bool _loaded;

    /// <summary>
    /// 幂等。<paramref name="gameRoot"/> 是游戏安装目录 (含 cachedata.sexp 和 Maps\)。
    /// </summary>
    public static void EnsureLoaded(string gameRoot)
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            Require(gameRoot, "cachedata.sexp");
            Require(gameRoot, "cacheattributs.sexp");
            string effects = Require(gameRoot, Path.Combine("Maps", "effectcollisions.data"));
            string raid = Require(gameRoot, Path.Combine("Maps", "raideventsettings.data"));
            string enemy = Require(gameRoot, Path.Combine("Maps", "enemyeventsettings.data"));

            // CacheManager: 静态构造里是 `if (!IsLoaded) Load()`, 抢先用带路径的重载加载好
            _0023z._0023y.Load(gameRoot);

            // 另外三个: 静态构造里是 `if (slot == null) slot = XxxLoad()`, 同理先把槽填上
            _0023z._00232jd._00230jd = EffectCollisionData._0023R(effects);
            _0023z._00232jd._0023DS = _0023z._0023DS.Load(raid);
            _0023z._00232jd._00231jd = EnemyEventSettingsData.Load(enemy);

            if (_0023z._00232jd._0023DS == null)
            {
                throw new InvalidOperationException($"raideventsettings.data 解析失败: {raid}");
            }

            if (_0023z._00232jd._00231jd == null)
            {
                throw new InvalidOperationException($"enemyeventsettings.data 解析失败: {enemy}");
            }

            // 到这里数据都装好了, 现在**主动**触发 GameManager 的静态构造并检查结果。
            //
            // 为什么必须主动做: 静态构造每进程只跑一次, 由"第一次碰到 GameManager 或
            // 它的伴生类型"触发。如果在数据装好之前就有代码碰了 ConductorGameLogic
            // (比如 `new Client(...)` 或 `new GameInfo()`), 静态构造会在缺数据的情况下
            // 抛异常 —— 而它整段包在 try/catch 里, 只写一行 StunLog 就继续。
            //
            // 后果是 BalanceData / TypeToGameObjectTypeID 等静态表**永久**为 null,
            // 而且没有第二次机会: 类型初始化器不会重跑。故障会以 NRE 的形式出现在
            // 任意远处 (实测过 GameManagerLargeObjects.cs:73 和 -bd/-98.cs:41),
            // 完全指不到根因。
            //
            // 所以在这里当场验一个哨兵字段, 让"顺序错了"变成启动期的显式失败。
            RuntimeHelpers.RunClassConstructor(typeof(GameManager).TypeHandle);
            if (GameManager.BalanceData == null)
            {
                throw new InvalidOperationException(
                    "GameManager 的静态构造已经在游戏数据加载之前跑过了, 静态表是空的, " +
                    "而且不会重跑。说明有代码在 GameDataLoader.EnsureLoaded 之前碰了 " +
                    "ConductorGameLogic 的类型。把 EnsureLoaded 提到进程启动最前面。");
            }

            _loaded = true;
            Log.Information("游戏数据已加载: {Root}", gameRoot);
        }
    }

    /// <summary>
    /// 每个**模拟线程**都要调一次, 在构造 GameManager 之前。
    ///
    /// ## 为什么进程级的 EnsureLoaded 不够
    ///
    /// <c>GameManager.BalanceData</c> 是 <c>[ThreadStatic]</c> 的 (GameManager.cs:321),
    /// 但它的赋值在**静态构造函数**里 (:897)。静态构造每进程只跑一次, 跑在"第一个碰到
    /// GameManager 的那个线程"上 —— 于是**其它每一个线程看到的都是 null**, 而且没有
    /// 任何机制会补上。
    ///
    /// 这不是原厂的缺陷: 客户端整个模拟跑在 Unity 主线程上, 一个线程就够了。
    /// ThreadStatic 在那里是有意为之 —— <c>AbilityManager</c> 承载可热更的平衡数值
    /// (<c>AbilityManager.Deserialize(GameplayChanges)</c> 会就地改它), 每个模拟线程
    /// 各持一份, 多局之间才不会互相串。
    ///
    /// 服务端一局一线程, 所以要自己补这一步。做的事和静态构造里那一行完全相同。
    ///
    /// ## 其它 ThreadStatic 字段为什么不用管
    ///
    ///   GameManager.ThreadStaticInstance —— 由 GameManager 的**实例**构造函数赋值,
    ///                                       在哪个线程建就在哪个线程有值, 天然正确
    ///   NetworkBase.Current              —— 全树无赋值点, 只在网络剖析里读且有 null 守卫
    /// </summary>
    public static void EnsureThreadState()
    {
        GameManager.BalanceData ??= new AbilityManager();
    }

    private static string Require(string root, string relative)
    {
        string full = Path.Combine(root, relative);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"游戏数据缺失: {full}。GameManager 的静态构造会吞掉这个错误并留下一堆 null 静态表, " +
                $"所以在这里显式失败。", full);
        }

        return full;
    }
}
