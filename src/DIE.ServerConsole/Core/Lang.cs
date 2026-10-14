using System;
using System.Collections.Generic;

namespace DIE.ServerConsole.Core
{
    /// <summary>
    /// 控制台的两套文字 (中文 / English), 做法和登陆器一样 (launcher\DIE.Launcher\Core\Lang.cs):
    /// 一张表两列, 少翻一条时显示键名, 测试钉着两列都不空、占位符个数一致。
    /// </summary>
    public static class Lang
    {
        public const string Chinese = "zh";
        public const string English = "en";

        public static string Current { get; private set; } = Chinese;

        public static bool IsChinese => Current == Chinese;

        public static string Other => IsChinese ? English : Chinese;

        public static void Use(string language, string systemLanguage = null)
        {
            if (language == Chinese || language == English)
            {
                Current = language;
                return;
            }

            Current = string.Equals(systemLanguage, "zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;
        }

        public static string T(string key) =>
            Table.TryGetValue(key, out string[] pair) ? pair[IsChinese ? 0 : 1] : key;

        public static string T(string key, params object[] args) => string.Format(T(key), args);

        public static IEnumerable<string> Keys => Table.Keys;

        public static string Raw(string key, string language) => Table[key][language == Chinese ? 0 : 1];

        private static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>
        {
            // ---- 窗口 ----
            ["window.title"] = new[] { "DIE Reborn 服务器控制台", "DIE Reborn Server Console" },
            ["chrome.title"] = new[] { "DIE REBORN  ·  服务器控制台", "DIE REBORN  ·  SERVER CONSOLE" },
            ["lang.switch"] = new[] { "English", "中文" },
            ["dialog.crash"] = new[]
            {
                "控制台出错了 (服务器不受影响):\n{0}",
                "The console hit an error (the server is not affected):\n{0}",
            },
            ["dialog.stopWithPlayers"] = new[]
            {
                "服务器上还有 {0} 人在线, 停服会把他们都踢下线。确定吗?",
                "{0} player(s) are still online and will be disconnected. Stop the server?",
            },
            ["dialog.closeWhileRunning"] = new[]
            {
                "服务器正在运行。\n\n是 —— 停服并退出\n否 —— 退出控制台, 服务器在后台继续运行 (再打开控制台会自动接上)\n取消 —— 不退出",
                "The server is running.\n\nYes — stop the server and exit\nNo — exit the console and keep the server running in the background (reopen the console to reconnect)\nCancel — stay",
            },

            ["action.start"] = new[] { "开服", "Start" },
            ["action.restart"] = new[] { "重启", "Restart" },
            ["action.stop"] = new[] { "停服", "Stop" },

            ["nav.overview"] = new[] { "概览", "Overview" },
            ["nav.matches"] = new[] { "对局", "Matches" },
            ["nav.players"] = new[] { "玩家", "Players" },
            ["nav.log"] = new[] { "日志", "Log" },
            ["nav.network"] = new[] { "网络与端口", "Network" },
            ["nav.settings"] = new[] { "设置", "Settings" },

            // ---- 顶部状态 ----
            ["state.server.missing"] = new[] { "找不到服务端", "Server not found" },
            ["state.server.stopped"] = new[] { "已停止", "Stopped" },
            ["state.server.starting"] = new[] { "正在启动…", "Starting…" },
            ["state.server.running"] = new[] { "运行中", "Running" },
            ["state.server.stopping"] = new[] { "正在停止…", "Stopping…" },
            ["header.detail"] = new[] { "已运行 {0}  ·  版本 {1}", "Up {0}  ·  Version {1}" },
            ["header.devBuild"] = new[] { "开发版", "dev build" },
            ["header.missing"] = new[]
            {
                "{0} 里没有 DIE.Server.exe —— 到「设置」里选服务端所在的文件夹",
                "No DIE.Server.exe in {0} — choose the server folder under Settings",
            },
            ["header.unnamed"] = new[] { "未命名服务器", "Unnamed server" },

            // ---- 底栏消息 ----
            ["msg.attached"] = new[] { "服务器已经在运行, 控制台已接上。", "The server was already running; connected to it." },
            ["msg.starting"] = new[] { "正在启动服务器 (加载地图数据要十几秒)…", "Starting the server (loading map data takes a few seconds)…" },
            ["msg.startFailed"] = new[] { "启动失败: {0}", "Could not start the server: {0}" },
            ["msg.stopping"] = new[] { "正在停服…", "Stopping the server…" },
            ["msg.stopped"] = new[] { "服务器已停止。", "The server has stopped." },
            ["msg.killed"] = new[] { "服务器没有响应, 已强制结束。", "The server did not respond and was terminated." },
            ["msg.noAdmin"] = new[]
            {
                "服务器在运行, 但没有应答管理接口 (旧版服务端?) —— 看不到人数, 开停服照常可用。",
                "The server is running but its admin interface does not answer (older server?) — no player counts, start/stop still work.",
            },
            ["msg.running"] = new[] { "服务器已开好, 玩家可以连了。", "The server is up; players can connect." },
            ["msg.adminSilent"] = new[] { "管理接口暂时没有应答, 显示的是上一次的数据。", "The admin interface is not answering; showing the last data." },
            ["msg.crashedRestarting"] = new[] { "服务器意外退出 (代码 {0}), 正在自动重启…", "The server exited unexpectedly (code {0}); restarting…" },
            ["msg.crashedGaveUp"] = new[]
            {
                "服务器意外退出 (代码 {0}), 10 分钟内已经第 {1} 次以上, 不再自动重启 —— 原因看日志。",
                "The server exited unexpectedly (code {0}) more than {1} times in 10 minutes; not restarting — see the log.",
            },
            ["msg.crashed"] = new[] { "服务器意外退出 (代码 {0}), 原因看日志。", "The server exited unexpectedly (code {0}); see the log." },
            ["msg.copied"] = new[] { "已复制 {0}", "Copied {0}" },
            ["msg.fwFixing"] = new[] { "防火墙没有放行服务器端口, 正在放行 (请在弹出的窗口里点\"是\")…", "The firewall is blocking the server ports; opening them (click Yes in the prompt)…" },
            ["msg.fwOk"] = new[] { "防火墙已放行服务器端口。", "The firewall allows the server ports." },
            ["msg.fwDeclined"] = new[] { "没有管理员权限, 防火墙没改。可以到「网络与端口」里再点一次。", "No administrator permission; the firewall was not changed. Try again under Network." },
            ["msg.fwFixed"] = new[] { "防火墙端口已放行。", "Firewall ports opened." },
            ["msg.fwStillBad"] = new[] { "改过防火墙了, 但检查还是不通过 —— 看「网络与端口」。", "The firewall was changed but the check still fails — see Network." },
            ["msg.probeNeedsServer"] = new[] { "先开服再测 —— 服务器没开时端口上没人应答。", "Start the server first — nothing answers on the ports while it is stopped." },
            ["msg.probeOk"] = new[] { "外网能连进来。", "Reachable from the internet." },
            ["msg.probeBad"] = new[] { "外网连不进来 —— 看「网络与端口」。", "Not reachable from the internet — see Network." },
            ["msg.settingsFailed"] = new[] { "控制台设置保存失败: {0}", "Could not save console settings: {0}" },
            ["msg.stopBeforeFolder"] = new[] { "先停服再换服务端目录。", "Stop the server before changing its folder." },

            // ---- 概览: 三层 ----
            ["layer.lobby"] = new[] { "大厅  ·  TCP {0}", "LOBBY  ·  TCP {0}" },
            ["layer.queue"] = new[] { "匹配  ·  TCP {0}", "MATCHMAKING  ·  TCP {0}" },
            ["layer.battle"] = new[] { "战斗服  ·  UDP {0}-{1}", "GAME SERVERS  ·  UDP {0}-{1}" },
            ["layer.lobbyDetail"] = new[] { "在线总人数 (排队和局里的也算)\n其中只在大厅: {0} 人", "Everyone online (incl. queued and in matches)\nIn the lobby only: {0}" },
            ["layer.queueLine"] = new[] { "{0}: 排队 {1} · 准备中 {2}", "{0}: {1} queued · {2} readying" },
            ["layer.rooms"] = new[] { "自定义房间 {0} 个, {1} 人", "{0} custom room(s), {1} player(s)" },
            ["layer.queueEmpty"] = new[] { "没有人在排队", "Nobody is queueing" },
            ["layer.links"] = new[] { "匹配连接 {0} 条", "{0} matchmaking connection(s)" },
            ["layer.battleDetail"] = new[] { "在局里的人数\n{0} 局进行中, 最多同时 {1} 局", "Players in matches\n{0} match(es) running, up to {1} at once" },

            ["load.process"] = new[] { "服务器进程 CPU", "SERVER PROCESS CPU" },
            ["load.machine"] = new[] { "整机 CPU", "MACHINE CPU" },
            ["load.battle"] = new[] { "最忙的一局", "BUSIEST MATCH" },
            ["load.noMatches"] = new[] { "没有进行中的对局", "No match running" },
            ["load.battleDetail"] = new[]
            {
                "UDP {0} 的逻辑线程忙碌比例\n平均每帧 {1} ms, 最慢 {2} ms (超过 33 ms 会卡)",
                "Logic thread busy time on UDP {0}\n{1} ms per frame on average, worst {2} ms (over 33 ms lags)",
            },
            ["load.machineDetail"] = new[] { "内存 {0} / {1} GB  ·  {2} 核", "Memory {0} / {1} GB  ·  {2} cores" },
            ["load.processDetail"] = new[] { "内存 {0} MB  ·  {1} 个线程", "Memory {0} MB  ·  {1} threads" },
            ["load.notRunning"] = new[] { "服务器没在运行", "The server is not running" },

            ["chart.players"] = new[] { "在线人数 · 最近 30 分钟", "PLAYERS ONLINE · LAST 30 MIN" },
            ["chart.cpu"] = new[] { "服务器 CPU · 最近 30 分钟", "SERVER CPU · LAST 30 MIN" },
            ["chart.peak"] = new[] { "峰值 {0}", "Peak {0}" },

            ["checks.title"] = new[] { "开服检查", "CHECKLIST" },
            ["check.fwUnknown"] = new[] { "Windows 防火墙: 正在检查…", "Windows Firewall: checking…" },
            ["check.fwOk"] = new[] { "Windows 防火墙已放行服务器端口", "Windows Firewall allows the server ports" },
            ["check.fwBad"] = new[] { "Windows 防火墙没有放行服务器端口 —— 到「网络与端口」点\"一键放行\"", "Windows Firewall blocks the server ports — use \"Open ports\" under Network" },
            ["check.probeUnknown"] = new[] { "外网连通: 开服后自动检测", "Internet reachability: tested once the server is up" },
            ["check.probeError"] = new[] { "外网连通: {0}", "Internet reachability: {0}" },
            ["check.probeOk"] = new[] { "外网能连进来 (公网地址 {0})", "Reachable from the internet (public address {0})" },
            ["check.probeBad"] = new[] { "外网连不进来 —— 云服务器查安全组, 家里查路由器端口映射", "Not reachable from the internet — check the cloud security group or your router's port forwarding" },
            ["check.noHost"] = new[] { "没填公网地址 —— 外面的玩家能登录但进不了对局 (到「设置」里填)", "No public address set — outside players can log in but cannot join matches (set it under Settings)" },
            ["check.hostMismatch"] = new[] { "public-host.txt 填的是 {0}, 但外网看到的是 {1} —— 不一致的话玩家进不了对局", "public-host.txt says {0} but the internet sees {1} — players cannot join matches if this is wrong" },
            ["check.hostOk"] = new[] { "公网地址: {0}", "Public address: {0}" },
            ["check.listed"] = new[] { "会出现在登陆器的服务器列表里", "Listed in the launcher's server list" },
            ["check.unlisted"] = new[] { "不上服务器列表 (玩家要用 launcher.json 连)", "Not listed (players need launcher.json)" },

            // ---- 对局 ----
            ["matches.title"] = new[] { "对局", "MATCHES" },
            ["matches.note"] = new[]
            {
                "负载 = 这一局的逻辑线程在最近 30 秒里有多少时间在忙。50% 以上偏高, 80% 以上玩家会感觉卡。打完的局会保留几分钟再回收。",
                "Load = how much of the last 30 seconds this match's logic thread was busy. Above 50% is high; above 80% players feel lag. Finished matches stay a few minutes before being recycled.",
            },
            ["matches.summary"] = new[] { "{0} / {1} 局  ·  {2} 人在局里", "{0} / {1} matches  ·  {2} players in matches" },
            ["col.port"] = new[] { "端口", "Port" },
            ["col.mode"] = new[] { "模式", "Mode" },
            ["col.map"] = new[] { "地图", "Map" },
            ["col.players"] = new[] { "人数", "Players" },
            ["col.state"] = new[] { "状态", "State" },
            ["col.age"] = new[] { "已开", "Age" },
            ["col.load"] = new[] { "负载", "Load" },
            ["col.frame"] = new[] { "帧耗时 平均/最慢", "Frame avg / worst" },
            ["col.name"] = new[] { "名字", "Name" },
            ["col.where"] = new[] { "在哪", "Where" },
            ["col.online"] = new[] { "在线", "Online" },
            ["col.steam"] = new[] { "Steam ID", "Steam ID" },
            ["col.address"] = new[] { "IP", "IP" },

            // ---- 玩家 ----
            ["players.search"] = new[] { "搜索", "Search" },
            ["players.title"] = new[] { "玩家  ·  {0} 人", "PLAYERS  ·  {0}" },
            ["players.copySteam"] = new[] { "复制 Steam ID", "Copy Steam ID" },
            ["players.openProfile"] = new[] { "打开 Steam 个人资料", "Open Steam profile" },

            // ---- 日志 ----
            ["log.follow"] = new[] { "自动滚到最新", "Follow" },
            ["log.openFolder"] = new[] { "打开日志文件夹", "Open log folder" },
            ["log.all"] = new[] { "全部", "All" },
            ["log.info"] = new[] { "常规", "Normal" },
            ["log.problems"] = new[] { "警告和错误", "Warnings & errors" },
            ["log.search"] = new[] { "搜索", "Search" },
            ["log.noFile"] = new[] { "还没有日志 ({0})", "No log yet ({0})" },

            // ---- 网络: 防火墙 ----
            ["fw.title"] = new[] { "WINDOWS 防火墙", "WINDOWS FIREWALL" },
            ["fw.explain"] = new[]
            {
                "本机防火墙要放行下面这些端口, 外面的玩家才连得进来。控制台打开时会自动检查, 缺了自动放行 (需要点一次管理员确认)。",
                "These ports must be allowed through this machine's firewall for outside players to connect. The console checks on start and opens any that are missing (one administrator prompt).",
            },
            ["fw.fix"] = new[] { "一键放行", "Open ports" },
            ["fw.refix"] = new[] { "重新放行", "Re-apply" },
            ["fw.recheck"] = new[] { "重新检查", "Check again" },
            ["fw.checking"] = new[] { "正在检查…", "Checking…" },
            ["fw.readFailed"] = new[] { "读不了防火墙规则: {0}", "Could not read the firewall rules: {0}" },
            ["fw.portOk"] = new[] { "{0}  {1} —— 已放行", "{0}  {1} — allowed" },
            ["fw.portBad"] = new[] { "{0}  {1} —— 没有放行", "{0}  {1} — blocked" },
            ["fw.blocking"] = new[]
            {
                "有一条拦截 DIE.Server.exe 的规则 \"{0}\" (多半是第一次运行时在 Windows 弹窗里点了取消), 它会挡住所有端口 —— 一键放行会删掉它",
                "A rule \"{0}\" blocks DIE.Server.exe (usually from cancelling the Windows prompt on first run) and overrides every port — \"Open ports\" removes it",
            },
            ["port.lobby"] = new[] { "大厅", "lobby" },
            ["port.queue"] = new[] { "匹配", "matchmaking" },
            ["port.status"] = new[] { "状态接口 (登陆器)", "status (launcher)" },
            ["port.battle"] = new[] { "战斗服", "game servers" },

            // ---- 网络: 外网探测 ----
            ["probe.title"] = new[] { "外网连通", "INTERNET REACHABILITY" },
            ["probe.explain"] = new[]
            {
                "请 DIE Reborn 登记处从外面反过来连一下本机的端口, 看玩家连不连得进来, 顺便告诉你外面看到的公网 IP。要先开服。",
                "Asks the DIE Reborn registry to connect back to this machine's ports from the internet, the way players will, and reports the public IP it sees. The server must be running.",
            },
            ["probe.run"] = new[] { "检测", "Test" },
            ["probe.unsupported"] = new[] { "登记处暂时不支持这项检测。", "The registry does not support this test yet." },
            ["probe.registryDown"] = new[] { "连不上登记处 {0}, 测不了。", "Could not reach the registry at {0}." },
            ["probe.testing"] = new[] { "正在请 {0} 连过来…", "Asking {0} to connect back…" },
            ["probe.notYet"] = new[] { "还没测过。", "Not tested yet." },
            ["probe.open"] = new[] { "TCP {0}  {1} —— 外网能连进来", "TCP {0}  {1} — reachable" },
            ["probe.closed"] = new[] { "TCP {0}  {1} —— 外网连不进来", "TCP {0}  {1} — NOT reachable" },
            ["probe.udp"] = new[]
            {
                "UDP {0}-{1}  战斗服 —— UDP 没法从外面测, 请确认安全组 / 路由器也放行了 (没放行的话能进大厅、进不了对局)",
                "UDP {0}-{1}  game servers — UDP cannot be tested from outside; make sure the security group / router allows it too (otherwise players reach the lobby but not matches)",
            },
            ["probe.ip"] = new[] { "外面看到的公网 IP: {0}", "Public IP as seen from the internet: {0}" },
            ["probe.selfRegistry"] = new[]
            {
                "这台就是登记处 —— 自己连自己的公网 IP 在不少云上本来就不通, 这里的结果不作数。请用别的网络上的电脑试试登陆器。",
                "This machine is the registry itself — connecting to your own public IP often fails on cloud hosts, so this result is unreliable. Try the launcher from another network.",
            },
            ["probe.useIp"] = new[] { "把 {0} 填成公网地址", "Use {0} as public address" },

            // ---- 网络: UPnP ----
            ["upnp.title"] = new[] { "路由器自动映射 (UPnP)", "ROUTER PORT FORWARDING (UPnP)" },
            ["upnp.explain"] = new[]
            {
                "在家里开服时, 让路由器自动把端口转给这台电脑。需要路由器开了 UPnP, 而且运营商给的是公网 IP。云服务器不需要这一步 (云上看安全组)。",
                "When hosting at home, ask the router to forward the ports to this PC. Needs UPnP enabled on the router and a public IP from your ISP. Cloud servers do not need this (use the security group).",
            },
            ["upnp.run"] = new[] { "自动映射", "Forward ports" },
            ["upnp.working"] = new[] { "正在找路由器并映射 (UDP 有 100 个端口, 要等一会儿)…", "Looking for the router and forwarding (100 UDP ports, this takes a moment)…" },
            ["upnp.unsupported"] = new[] { "没找到支持 UPnP 的路由器 —— 只能到路由器管理页面里手动映射。", "No UPnP router found — forward the ports manually in your router's settings." },
            ["upnp.ok"] = new[] { "已映射 {0} 个端口到 {1}。", "Forwarded {0} ports to {1}." },
            ["upnp.partial"] = new[] { "映射了 {0} 个端口, {1} 个失败 (可能被路由器拒绝或已被别的设备占用)。", "Forwarded {0} ports; {1} failed (refused by the router or already in use)." },
            ["upnp.failed"] = new[] { "映射失败: {0}", "Forwarding failed: {0}" },

            // ---- 网络: 云 ----
            ["cloud.title"] = new[] { "云服务器安全组", "CLOUD SECURITY GROUP" },
            ["cloud.explain"] = new[]
            {
                "阿里云 / 腾讯云 / AWS 这类云服务器, 除了本机防火墙, 还要在云控制台的\"安全组\"里加入方向规则 (授权对象 0.0.0.0/0)。控制台改不了它, 照下面加:",
                "On cloud servers (Alibaba Cloud, Tencent Cloud, AWS…) you must also add inbound rules to the security group in the cloud dashboard (source 0.0.0.0/0). The console cannot do this for you; add:",
            },
            ["cloud.rule"] = new[] { "入方向  允许  {0}  {1}  ({2})", "Inbound  Allow  {0}  {1}  ({2})" },

            // ---- 设置 ----
            ["set.server"] = new[] { "服务器", "SERVER" },
            ["set.name"] = new[] { "服务器名称", "Server name" },
            ["set.nameHint"] = new[] { "显示在玩家登陆器的服务器列表里, 40 字以内。不填就显示 IP。", "Shown in players' launcher server list, up to 40 characters. Blank shows the IP." },
            ["set.location"] = new[] { "位置", "Location" },
            ["set.locationHint"] = new[] { "比如 \"华东 · 杭州\"。不填就按 IP 自动查一个大致地区。", "e.g. \"US West\". Blank = looked up from the IP automatically." },
            ["set.listed"] = new[] { "显示在登陆器的服务器列表里 (需要 TCP 8555 对外开放)", "Show in the launcher's server list (needs TCP 8555 open to the internet)" },
            ["set.host"] = new[] { "公网地址 (IP 或域名)", "Public address (IP or domain)" },
            ["set.detectHost"] = new[] { "自动检测", "Detect" },
            ["set.hostHint"] = new[] { "发给玩家的游戏, 用来连战斗服。填错了玩家能进大厅、进不了对局。", "Sent to players' games to reach the match servers. If wrong, players reach the lobby but cannot join matches." },
            ["set.save"] = new[] { "保存", "Save" },
            ["set.console"] = new[] { "控制台", "CONSOLE" },
            ["set.autoStart"] = new[] { "打开控制台时自动开服", "Start the server when the console opens" },
            ["set.autoRestart"] = new[] { "服务器意外退出时自动重启 (10 分钟内最多 3 次)", "Restart the server if it exits unexpectedly (up to 3 times in 10 minutes)" },
            ["set.dailyBackup"] = new[] { "每天自动备份账号库, 时间", "Back up the account database daily at" },
            ["set.backupHour"] = new[] { "点 (保留最近 30 份)", "o'clock (keeps the last 30)" },
            ["set.backupNow"] = new[] { "立即备份", "Back up now" },
            ["set.openBackups"] = new[] { "打开备份文件夹", "Open backups folder" },
            ["set.folder"] = new[] { "服务端目录", "SERVER FOLDER" },
            ["set.changeFolder"] = new[] { "更改…", "Change…" },
            ["set.openFolder"] = new[] { "打开文件夹", "Open folder" },
            ["set.lastBackup"] = new[] { "上次自动备份: {0}", "Last automatic backup: {0}" },
            ["set.badHost"] = new[] { "公网地址只能是 IP 或域名 (字母、数字、点、短横线)。", "The public address must be an IP or domain name (letters, digits, dots, hyphens)." },
            ["set.saveFailed"] = new[] { "保存失败: {0}", "Could not save: {0}" },
            ["set.savedNextStart"] = new[] { "已保存, 下次开服生效。", "Saved; takes effect when the server starts." },
            ["set.savedRestart"] = new[] { "已保存。公网地址 / 上不上列表要重启服务器才生效。", "Saved. The public address and listing take effect after a restart." },
            ["set.savedLive"] = new[] { "已保存, 名称和位置已经生效 (登陆器列表一分钟内更新)。", "Saved; the name and location are live (the launcher list updates within a minute)." },
            ["set.detecting"] = new[] { "正在检测…", "Detecting…" },
            ["set.hostFilled"] = new[] { "已填入检测到的公网 IP, 点\"保存\"生效。", "Filled in the detected public IP; click Save to apply." },
            ["set.backupNeedsServer"] = new[] { "服务器开着才能备份 (备份由服务端在线做, 不打断玩家)。", "The server must be running to back up (it backs up live without interrupting players)." },
            ["set.backupDone"] = new[] { "账号库已备份 ({0})。", "Account database backed up ({0})." },
            ["set.backupFailed"] = new[] { "备份失败: {0}", "Backup failed: {0}" },
            ["set.pickExe"] = new[] { "选择服务端程序 DIE.Server.exe", "Select the server program DIE.Server.exe" },

            // ---- 格式 ----
            ["time.dh"] = new[] { "{0} 天 {1} 小时", "{0}d {1}h" },
            ["time.hm"] = new[] { "{0} 小时 {1} 分", "{0}h {1}m" },
            ["time.m"] = new[] { "{0} 分钟", "{0} min" },
            ["time.s"] = new[] { "{0} 秒", "{0} s" },

            ["mode.tutorial"] = new[] { "新手教程", "Tutorial" },
            ["mode.practice"] = new[] { "练习", "Practice" },
            ["mode.crossroads"] = new[] { "十字路口", "Crossroads" },
            ["mode.scavenger"] = new[] { "拾荒", "Scavenger" },

            ["difficulty.0"] = new[] { "简单", "Easy" },
            ["difficulty.1"] = new[] { "普通", "Normal" },
            ["difficulty.2"] = new[] { "困难", "Hard" },
            ["difficulty.3"] = new[] { "不可能", "Impossible" },
            ["difficulty.4"] = new[] { "极难", "Extreme" },

            ["state.loading"] = new[] { "正在建", "Loading" },
            ["state.waiting"] = new[] { "等人进", "Waiting" },
            ["state.running"] = new[] { "进行中", "Running" },
            ["state.over"] = new[] { "已结束", "Finished" },
            ["state.error"] = new[] { "出错", "Error" },

            ["where.match"] = new[] { "对局 (UDP {0})", "Match (UDP {0})" },
            ["where.queue"] = new[] { "排队中", "Queueing" },
            ["where.lobby"] = new[] { "大厅", "Lobby" },
        };
    }
}
