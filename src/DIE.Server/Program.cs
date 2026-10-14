using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using DIE.Server.Data;
using DIE.Server.Game;
using DIE.Server.Match;
using DIE.Server.Net;
using DIE.Server.Steam;
using Serilog;
using Serilog.Events;

namespace DIE.Server;

internal static class Program
{
    // 两个端口都取自 StunCore.GetIP 的默认值 —— ip.cfg 的 public 分支没有覆盖它们,
    // 所以客户端会用这两个数字, 无需玩家改任何配置。
    /// <summary>
    /// 游戏数据目录。模拟层要从这里读地图、缓存数据和事件配置。
    /// 查找顺序见 <see cref="GameDataLoader.DefaultGameRoot"/>。
    /// </summary>
    private static string GameRoot => GameDataLoader.DefaultGameRoot;

    private const int DefaultRequestServerPort = 1555;
    private const int DefaultMatchServerPort = 2555;

    private static int Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(baseDir, "logs", "die-server-.log"),
                rollingInterval: RollingInterval.Day,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            // 服务器包 (make-server-pack.ps1) 会写 version.txt; 开发机上没有, 不打
            string versionFile = Path.Combine(baseDir, "version.txt");
            if (File.Exists(versionFile))
            {
                Log.Information("服务端版本 {Version}", File.ReadAllText(versionFile).Trim());
            }

            int port = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : DefaultRequestServerPort;

            // **必须是碰 ConductorGameLogic 之前的第一件事。**
            //
            // GameManager 的静态构造每进程只跑一次, 由第一次碰到该类型触发。数据没装好
            // 就触发的话, 它会抛异常 -> 被自己的 try/catch 吞掉 -> 静态表永久为 null,
            // 而且不会重跑。详见 GameDataLoader 的注释。
            GameDataLoader.EnsureLoaded(GameRoot);

            CraftingCatalog catalog = new CraftingCatalog();
            catalog.Load(Path.Combine(baseDir, "data", "GameCraftingData.xml"));

            string dbPath = Path.Combine(baseDir, "data", "die_reborn.db");
            AccountStore accounts = new AccountStore(dbPath, catalog);
            accounts.ApplySchema(Path.Combine(baseDir, "Data", "Schema.sql"));

            // 离线合并账号: DIE.Server.exe --import-accounts <另一个 die_reborn.db> [--dry-run]
            // 按 Steam ID 把那个库里的账号进度合并进本库, 别的账号不动。必须先停服务端。见 AccountImporter。
            int importAt = Array.IndexOf(args, "--import-accounts");
            if (importAt >= 0)
            {
                int code = ImportAccounts(dbPath, importAt + 1 < args.Length ? args[importAt + 1] : null,
                    args.Contains("--dry-run"));

                // 搬进来的是旧库的账号 —— 再跑一遍迁移 (比如初始角色的变异版 -> 普通版)
                accounts.ApplySchema(Path.Combine(baseDir, "Data", "Schema.sql"));
                return code;
            }
            MatchStore matches = new MatchStore(dbPath);

            // 过期会话 / 匹配票据: 启动时清一次, 之后每小时一次 (过期满一天才删)
            using System.Threading.Timer purge = new System.Threading.Timer(_ => PurgeExpired(accounts, matches),
                null, TimeSpan.Zero, TimeSpan.FromHours(1));
            using MatchHostRegistry matchHosts = new MatchHostRegistry();
            matchHosts.StartReaper();
            Log.Information("账号库就绪: {Path}", dbPath);

            // 开发机: run-server.cmd 先 cd 到仓库根, public-host.txt 在它旁边。
            // 服务器包: public-host.txt 和 DIE.Server.exe 放在一起。两处都找。
            string publicHost = MatchHostAddress.LoadPublicHost(Directory.GetCurrentDirectory())
                                ?? MatchHostAddress.LoadPublicHost(baseDir);

            ISteamTicketVerifier steamAuth = StartSteamAuth(out IDisposable steamLifetime);
            using IDisposable steamScope = steamLifetime;

            using RequestServer server = new RequestServer(accounts, matches, catalog, steamAuth);
            if (!server.Start(IPAddress.Any, port))
            {
                return 1;
            }

            // 离局惩罚的免罚名单: 和 public-host.txt 一样两处都找
            LeavePunishmentStore punishments = new LeavePunishmentStore(dbPath);
            int exempt = punishments.LoadExemptions(Directory.GetCurrentDirectory());
            if (exempt == 0)
            {
                exempt = punishments.LoadExemptions(baseDir);
            }

            Log.Information("离局惩罚免罚名单: {Count} 个 Steam ID ({File})", exempt, LeavePunishmentStore.ExemptFile);

            // 匹配服局末要通知大厅刷新账号数据, 那条路在 Request 服上。
            using MatchServer matchServer = new MatchServer(
                accounts, matches, matchHosts, server.RefreshAccount, publicHost, punishments);
            if (!matchServer.Start(IPAddress.Any, DefaultMatchServerPort))
            {
                return 1;
            }

            // 登陆器的状态接口 (在线人数 / 局数 / 补丁镜像)。起不来不影响开服, 只是登陆器看不到。
            // DIE_STATUS_PORT=0 关掉, 别的数换端口。
            string serverVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : string.Empty;
            using StatusServer status = new StatusServer(() => StatusOf(server, matchHosts, serverVersion), baseDir);
            int statusPort = int.TryParse(Environment.GetEnvironmentVariable("DIE_STATUS_PORT"), out int asked)
                ? asked
                : StatusServer.DefaultPort;
            if (statusPort > 0)
            {
                status.Start(IPAddress.Any, statusPort);
            }

            LogReachableAddresses(publicHost, matchHosts);

            Log.Information("按 Ctrl+C 退出");

            using ManualResetEventSlim quit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                quit.Set();
            };
            quit.Wait();

            Log.Information("正在关闭…");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "服务器启动失败");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// 玩家身份的核验方式。默认交给 Steam (见 <see cref="SteamTicketVerifier"/>)。
    ///
    /// <c>DIE_STEAM_AUTH=trust</c>: 自称是谁就是谁, 不找 Steam —— 只给断网的本机开发用,
    /// 公网上开等于谁都能冒充任何人。
    /// </summary>
    /// <summary>状态接口每次被问到时现算的那几个数。</summary>
    private static StatusServer.Snapshot StatusOf(RequestServer server, MatchHostRegistry hosts, string version)
    {
        int matches = 0;
        int inMatch = 0;

        foreach (LidgrenHost host in hosts.Snapshot())
        {
            if (host.MatchOver)
            {
                continue;       // 打完的局还会空置几分钟才回收, 不算"正在进行"
            }

            matches++;
            inMatch += host.Roster.Count;
        }

        return new StatusServer.Snapshot(server.OnlineCount, inMatch, matches, hosts.PortCount, version);
    }

    /// <summary>--import-accounts 的实现: 打印每个账号搬了什么, 返回进程退出码。</summary>
    private static int ImportAccounts(string targetPath, string sourcePath, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            Log.Error("用法: DIE.Server.exe --import-accounts <要导入的 die_reborn.db> [--dry-run]");
            return 2;
        }

        try
        {
            var results = DIE.Server.Data.AccountImporter.Import(targetPath, sourcePath, dryRun);
            foreach (var r in results)
            {
                Log.Information("{Mode} Steam {Steam} \"{Name}\": 源账号 {Src} -> 本库账号 {Dst} ({Action}) | {Rows}",
                    dryRun ? "[预演]" : "[导入]", r.SteamId, r.Name, r.SourceUserId, r.TargetUserId,
                    r.Created ? "新建" : "覆盖进度",
                    string.Join(", ", r.Rows.Select(kv => $"{kv.Key} {kv.Value}")));
            }

            Log.Information(dryRun
                ? "预演完成, 共 {Count} 个账号, 没有写入。去掉 --dry-run 正式导入。"
                : "导入完成, 共 {Count} 个账号。", results.Count);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入账号失败, 本库没有改动");
            return 1;
        }
    }

    private static ISteamTicketVerifier StartSteamAuth(out IDisposable lifetime)
    {
        lifetime = null;

        if (string.Equals(Environment.GetEnvironmentVariable("DIE_STEAM_AUTH"), "trust",
                          StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("DIE_STEAM_AUTH=trust —— 不核验玩家身份, 谁都能冒充任何 SteamID。只用于断网的本机开发!");
            return new TrustClaimVerifier();
        }

        SteamTicketVerifier steam = SteamTicketVerifier.Start();
        lifetime = steam;

        if (steam.InitError != null)
        {
            Log.Error("Steam 身份核验起不来: {Error} —— 装了插件的玩家会一直停在\"正在连接服务器\"。"
                      + "检查程序目录里的 steam_api64.dll 和 steam_appid.txt; 没装 Steam 的服务器还要有 "
                      + "steamclient64.dll / tier0_s64.dll / vstdlib_s64.dll (服务器包自带); "
                      + "断网开发可设 DIE_STEAM_AUTH=trust",
                steam.InitError);
        }

        return steam;
    }

    /// <summary>
    /// 告诉开服的人: 公网地址配没配、路由器要映射哪些端口、本机有哪些地址。
    /// 见 <see cref="MatchHostAddress"/>。
    /// </summary>
    private static void LogReachableAddresses(string publicHost, MatchHostRegistry hosts)
    {
        if (string.IsNullOrWhiteSpace(publicHost))
        {
            Log.Warning("没配服务器公网地址 —— 本机和局域网能玩, 公网上的人开局会连不上战斗服。"
                        + "在 {File} 里填公网 IP 或域名 (或设环境变量 {Var})",
                Path.Combine(Directory.GetCurrentDirectory(), MatchHostAddress.PublicHostFile),
                MatchHostAddress.PublicHostVariable);
        }
        else if (MatchHostAddress.ParseOrLookUp(publicHost) is IPAddress resolved)
        {
            Log.Information("服务器公网地址 {Host} -> {Address} (发给从公网进来的客户端)", publicHost, resolved);
        }
        else
        {
            Log.Warning("服务器公网地址 {Host} 解析不出 IPv4 —— 公网上的人开局会连不上战斗服", publicHost);
        }

        Log.Information("路由器需要映射到本机: TCP 1555, TCP 2555, TCP 8555 (登陆器状态接口), UDP {First}-{Last} (最多同时 {Count} 局)",
            hosts.FirstPort, hosts.LastPort, hosts.PortCount);

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        Log.Information("本机地址 {Address} ({Nic})", ip.Address, nic.Name);

                        // 198.18.0.0/15 是 Clash 一类代理 TUN 模式的惯用段。TUN 会接管默认路由,
                        // 服务器回给公网玩家的包被拐进代理, 对方表现为"连上就断"或一直连不上。
                        byte[] b = ip.Address.GetAddressBytes();
                        if (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                        {
                            Log.Warning("网卡 {Nic} ({Address}) 像是代理软件的 TUN 模式 —— 它接管默认路由时, "
                                        + "公网玩家会连不上。开服时关掉 TUN (系统代理模式不影响)", nic.Name, ip.Address);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "枚举网卡失败, 不影响开服");
        }
    }

    /// <summary>清掉过期满一天的会话和匹配票据。出错只记日志, 不影响开服。</summary>
    private static void PurgeExpired(AccountStore accounts, MatchStore matches)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            int sessions = accounts.PurgeExpiredSessions(now, TimeSpan.FromDays(1));
            int tickets = matches.PurgeExpiredTickets(now, TimeSpan.FromDays(1));
            if (sessions + tickets > 0)
            {
                Log.Information("清理过期数据: 会话 {Sessions} 条, 匹配票据 {Tickets} 条", sessions, tickets);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "清理过期会话 / 匹配票据失败");
        }
    }
}
