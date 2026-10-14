# DIE Reborn

A from-scratch server for *Dead Island: Epidemic* (the 2014 Stunlock / Deep Silver game whose official servers closed in 2015), so the retail Steam client can be played again.

为 *Dead Island: Epidemic*（Stunlock / Deep Silver 2014 年的作品，官方服务器已于 2015 年关闭）从零重写的服务端，让 Steam 上的原版客户端重新可玩。

> **Unofficial fan project.** Not affiliated with, endorsed by, or associated with Deep Silver, Stunlock Studios, or any rights holder. All trademarks belong to their owners. **This repository contains no game files** — you need your own copy of the game.
>
> **非官方爱好者项目**，与 Deep Silver、Stunlock Studios 及任何权利方无关。**本仓库不包含任何游戏文件**，需要你自己拥有游戏。

This repository is the **server** only. Players need the launcher instead, which also installs the client patch: <https://github.com/spardanviro/DIElauncher>

本仓库只有**服务端**。玩家需要的是登陆器（客户端补丁也由它安装）：<https://github.com/spardanviro/DIElauncher>

## What the server does / 服务端做什么

One process, four listeners / 一个进程，四个监听：

| Port | |
|---|---|
| TCP 1555 | Login, menus, inventory, crafting, shop. 登录、菜单、背包、制造、商店。 |
| TCP 2555 | Matchmaking. 匹配。 |
| UDP 50000–50099 | Battle hosts, one per match. 战斗服，每局一个。 |
| TCP 8555 | Read-only status for the launcher (players online, matches) and a mirror of the client patch. 给登陆器的只读状态接口和补丁镜像。 |

Playable end to end: Steam login, the Crib, matchmaking, and the Scout, Crossroads and Scavenger modes.

已能完整游玩：Steam 登录、大厅、匹配，以及 Scout、十字路口、拾荒三种模式。

## Before you build / 编译前的准备

The server reuses the game's own simulation and wire-format code, which is not ours to publish. You supply four things from your own installation.

服务端复用了游戏自己的模拟和协议代码，这部分不归我们公开，需要从你自己的安装里准备四样。

**1. Decompiled game assemblies / 反编译的游戏程序集**

Decompile with **ILSpy 10.x** (not dnSpyEx) into folders *next to* this repository. 用 ILSpy 10.x（不要用 dnSpyEx）反编译到本仓库**旁边**：

| Folder next to the repo / 仓库旁边的文件夹 | Decompiled from / 来源 |
|---|---|
| `StunCore_SourceCode/` | `Dead Island Epidemic_Data/Managed/StunCore.dll` |
| `ConductorCrafting_SourceCode/` | `Dead Island Epidemic_Data/Managed/ConductorCrafting.dll` |
| `Dead Island Epidemic_Data_SourceCode/` | `Dead Island Epidemic_Data/Managed/Assembly-CSharp.dll` |

The paths are set in `Directory.Build.props`. Do not edit the decompiled trees by hand; `tools/sanitize_decompiled.py` patches them during the build.

路径在 `Directory.Build.props` 里。不要手改反编译出来的代码，编译时 `tools/sanitize_decompiled.py` 会自动处理。

**2. The item catalogue / 物品图鉴** — extracted from the game into `data/GameCraftingData.xml`:

```
python tools/extract_crafting_data.py
```

**3. Valve's Steamworks runtime / Valve 的 Steamworks 运行库** — put `steam_api64.dll` in `lib/steamworks/`. Take it from the [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET/releases) *Standalone* release that matches the `Steamworks.NET.dll` already there (see `lib/steamworks/README.md`).

**4. Maps and balance data / 地图和平衡数据** — read at run time from your game folder. The default is the standard Steam path; set `DIE_GAME_ROOT` if yours differs. 运行时从游戏目录读取，默认是 Steam 的标准安装路径，不同的话设置环境变量 `DIE_GAME_ROOT`。

## Build and run / 编译和运行

Requirements / 需要：Windows, .NET SDK 9, Python 3, the game installed through Steam.

```
dotnet build
run-server.cmd
```

`run-server.cmd` starts the server on this machine. Point a client at it with the launcher, or by editing `ip.cfg` in the game folder.

`run-server.cmd` 在本机起服。用登陆器或者改游戏目录里的 `ip.cfg` 让客户端连过来。

## Deploying to a Windows host / 部署到 Windows 主机

```
打包服务器.cmd
```

This produces a self-contained folder (no .NET install needed on the host) with a start script, a firewall script and a readme. Put your public IP or domain in `public-host.txt` at the repository root first. The package copies maps and data out of your own game installation, so it is not something to redistribute publicly.

生成一个自包含的文件夹（主机上不用装 .NET），带启动脚本、防火墙脚本和说明。打包前先在仓库根目录的 `public-host.txt` 里写上公网 IP 或域名。包里会带上从你自己游戏安装里拷出的地图和数据，不适合公开分发。

## About this repository / 关于这个仓库

This is a snapshot export of a private working repository, so the commit history is short. Much of the original server logic was lost with the official servers; a lot of behaviour here is reconstructed from the client, and some of it is a best guess. Comments in the code say which is which (they are in Chinese).

这里是私有开发仓库的快照导出，所以提交历史很短。原版服务端逻辑大多随官服一起丢失，这里的很多行为是照着客户端重建的，有些是推测；代码注释里标明了哪些是哪些。
