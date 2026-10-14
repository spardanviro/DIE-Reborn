DIE Reborn 服务器 / DIE Reborn server
=====================================

完整教程 / Full guide: https://github.com/spardanviro/DIE-Reborn
玩家用的登陆器 / Player launcher: https://github.com/spardanviro/DIElauncher

非官方爱好者项目, 与 Deep Silver、Stunlock Studios 及任何权利方无关。
Unofficial fan project, not affiliated with Deep Silver, Stunlock Studios or any rights holder.


中文
----

需要: Windows 10 / 11 或 Windows Server 2016 以后 (64 位), 以及你自己 Steam 库里的 Dead Island: Epidemic。
服务端自带运行时, 不用另装 .NET。

1. 解压到一个路径里没有中文的地方, 比如 C:\DIE_Server。
2. 双击 DIE-ServerConsole.exe (服务器控制台)。
3. 第一次打开时, 控制台会:
   - 检查 Windows 防火墙, 缺了自动放行 (弹出管理员确认点"是");
   - 准备开服要的文件。这个包里不带游戏文件和 Steam 客户端库 (不能公开分发), 它们来自你自己的游戏和 Steam:
       * 这台电脑装着游戏和 Steam: 自动取好, 什么都不用做。
       * 云服务器上没装游戏: 把自己电脑上的游戏文件夹拷过去 (至少要 cachedata.sexp、cacheattributs.sexp、
         Maps 文件夹、"Dead Island Epidemic - Crib_Data\resources.assets"), 在控制台概览页点「选择游戏文件夹…」;
         再点「下载 Steam 客户端库」, 控制台会从 Valve 官方下载 SteamCMD 取得。
4. 在「设置」里填服务器名称, 公网地址点「自动检测」, 保存。
5. 点「开服」。

云服务器还要在云控制台的"安全组"里放行: TCP 1555、2555、8555, UDP 50000-50099
(控制台「网络与端口」页里有完整清单, 还能从外网反连测端口)。
开服一分钟后, 这台服务器会自动出现在所有玩家登陆器的服务器列表里。


English
-------

Needs: 64-bit Windows 10 / 11 or Windows Server 2016+, and Dead Island: Epidemic in your own Steam library.
The server brings its own runtime; there is no .NET to install.

1. Extract to a path without non-ASCII characters, for example C:\DIE_Server.
2. Double-click DIE-ServerConsole.exe (the server console).
3. The first time it opens, the console:
   - checks Windows Firewall and opens whatever is missing (click Yes on the administrator prompt);
   - prepares the files the server needs. This package contains no game files and no Steam client libraries
     (they cannot be redistributed); they come from your own game and Steam:
       * On a PC with the game and Steam installed this happens automatically.
       * On a cloud server without the game: copy the game folder over from your PC (at least cachedata.sexp,
         cacheattributs.sexp, the Maps folder and "Dead Island Epidemic - Crib_Data\resources.assets"), then press
         "Choose the game folder…" on the console's Overview page, and "Get the Steam libraries", which downloads
         Valve's official SteamCMD.
4. Under Settings, enter a server name, press Detect for the public address, and Save.
5. Press Start.

On a cloud server, also open these in the provider's security group: TCP 1555, 2555, 8555 and UDP 50000-50099
(the console's Network page lists them and can test the TCP ports from outside).
About a minute after starting, the server appears in every player's launcher server list.
