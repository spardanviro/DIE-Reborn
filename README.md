# DIE Reborn — server

**English** | [简体中文](README.zh-CN.md)

A from-scratch server for *Dead Island: Epidemic* (the 2014 Stunlock / Deep Silver game whose official servers closed in 2015), so the retail Steam client can be played again.

> **Unofficial fan project.** Not affiliated with, endorsed by, or associated with Deep Silver, Stunlock Studios, or any rights holder. All trademarks belong to their owners. **This repository contains no game files** — you need your own copy of the game.

| You want to… | Go to |
|---|---|
| **Play** on a server | The launcher: **<https://github.com/spardanviro/DIElauncher>** |
| **Host** a server | This page |

Playable end to end: Steam login, the Crib (inventory, crafting, shop, progression), matchmaking, and the Scout, Crossroads and Scavenger modes.

---

## Hosting guide

1. [What you need](#1-what-you-need)
2. [Prepare the game code and data](#2-prepare-the-game-code-and-data)
3. [Build and run on your own PC](#3-build-and-run-on-your-own-pc)
4. [Connect a client to your local server](#4-connect-a-client-to-your-local-server)
5. [Put it on a public server](#5-put-it-on-a-public-server)
6. [Let players in](#6-let-players-in)
7. [Running it day to day](#7-running-it-day-to-day)
8. [Settings](#8-settings)
9. [Troubleshooting](#9-troubleshooting)

### 1. What you need

On the PC where you build:

- Windows 10 / 11, 64-bit
- *Dead Island: Epidemic* installed through Steam. The game is no longer in the store, so it must already be in your Steam library
- [.NET SDK 9](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Python 3](https://www.python.org/downloads/) on `PATH` (the build runs a script)
- [ILSpy](https://github.com/icsharpcode/ILSpy/releases) 10.x — **not** dnSpyEx, whose output does not compile
- Git, to clone this repository

On the machine that will host (only if it is a different one): Windows Server 2016 or later, or Windows 10 / 11. Nothing needs to be installed there — the package you build carries its own .NET runtime and game data. Size it by players: each match uses roughly 20 MB of memory and a few percent of one CPU core; a 4-core / 8 GB machine has run about 25 matches at once in testing.

### 2. Prepare the game code and data

The server reuses the game's own simulation and network code. That code is not ours to publish, so you extract it from your own installation. This is a one-time job of about ten minutes.

In what follows, `GAME` is the game folder — in Steam, right-click the game → *Manage* → *Browse local files*. Usually:

```
C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic
```

**2.1 Clone this repository into a folder of its own**

The decompiled code goes *next to* the repository, not inside it, so start with an empty parent folder:

```
mkdir C:\DIE
cd C:\DIE
git clone https://github.com/spardanviro/DIE-Reborn.git
```

**2.2 Decompile three game assemblies**

Open ILSpy. For each row below: *File → Open…* the DLL, select the assembly in the tree on the left, then *File → Save Code…*, choose **C# project** as the type and save it into a **new folder with exactly this name** inside `C:\DIE`.

| DLL in `GAME\Dead Island Epidemic_Data\Managed\` | Save into |
|---|---|
| `StunCore.dll` | `C:\DIE\StunCore_SourceCode\` |
| `ConductorCrafting.dll` | `C:\DIE\ConductorCrafting_SourceCode\` |
| `Assembly-CSharp.dll` | `C:\DIE\Dead Island Epidemic_Data_SourceCode\` |

Leave ILSpy's settings at their defaults. You should end up with:

```
C:\DIE\
├─ DIE-Reborn\                              ← this repository
├─ StunCore_SourceCode\
├─ ConductorCrafting_SourceCode\
└─ Dead Island Epidemic_Data_SourceCode\
```

Do not edit the decompiled files. The build copies them, patches the copies (`tools\sanitize_decompiled.py`), and compiles those. If you rename the repository folder or put the trees elsewhere, change the paths in `Directory.Build.props`.

**2.3 Extract the item catalogue**

```
cd C:\DIE\DIE-Reborn
python tools\extract_crafting_data.py
```

This writes `data\GameCraftingData.xml`. If your game is not in the default Steam location, pass the path to `GAME\Dead Island Epidemic - Crib_Data\resources.assets` as the first argument.

**2.4 Add Valve's Steamworks runtime**

The server signs in to Steam as an anonymous game server to verify who each player is. Download the *Standalone* zip of [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET/releases) — the version named in `lib\steamworks\README.md` — and copy `Windows-x64\steam_api64.dll` from it into `lib\steamworks\`.

### 3. Build and run on your own PC

```
cd C:\DIE\DIE-Reborn
dotnet build
run-server.cmd
```

The first build takes a few minutes. When the server is up, the window shows lines like these (the log text is in Chinese):

```
游戏数据已加载: …                       game data loaded
Request 服务器已监听 0.0.0.0:1555         login / menu server listening
匹配服务器已监听 0.0.0.0:2555             matchmaking server listening
状态接口已监听 TCP 8555                   launcher status endpoint listening
已以匿名游戏服务器登录 Steam …            signed in to Steam (a few seconds later)
```

Check it from a browser: <http://127.0.0.1:8555/status> should answer with a short JSON document. Close the window, or press Ctrl+C, to stop the server.

### 4. Connect a client to your local server

Use the launcher from <https://github.com/spardanviro/DIElauncher> (version 1.1.0 or later):

1. Put `DIE-Launcher.exe` in a folder of its own.
2. Next to it, create a text file named `launcher.json` containing:
   ```json
   { "serverHost": "127.0.0.1" }
   ```
3. Run the launcher. The server card should say **Online**. Press **Install patch**, then **Play**.

The launcher writes that address into the game's `ip.cfg` every time you press Play, so the game connects to your server rather than the one the patch points at by default. Steam must be running and signed in.

To have a second PC on your home network join, use the LAN address of the server PC (for example `192.168.1.20`) instead of `127.0.0.1`, and allow the ports from step 5.3 through Windows Firewall on the server PC.

### 5. Put it on a public server

**5.1 Say what address players will use**

Create `public-host.txt` in the repository root with one line: the public IP or domain name of the host.

```
203.0.113.10
```

The server sends this address to players when a match starts, so it must be the one they can reach from the internet — not a private address like `10.x`, `172.16.x` or `192.168.x`.

**5.2 Build the package**

Double-click `打包服务器.cmd` ("package the server"), or run:

```
powershell -ExecutionPolicy Bypass -File tools\make-server-pack.ps1
```

It creates, next to the repository, a folder `DIE_服务器包` and a zip of it. Inside is `DIE_Server\`: the server with its own .NET runtime, the maps and data copied from your game, the Steam runtime files copied from your Steam installation, and three scripts. The build PC needs Steam installed for this step.

Because the package contains files from the game and from Steam, keep it for your own server; do not publish it.

**5.3 Open the ports**

| Protocol | Port | For |
|---|---|---|
| TCP | 1555 | Login, menus, inventory, shop |
| TCP | 2555 | Matchmaking |
| TCP | 8555 | Launcher status and patch mirror (read-only) |
| UDP | 50000–50099 | Matches, one port each |

Open them in two places:

- **The cloud provider's firewall** (called a *security group* on most providers): add inbound rules for the four entries above, source `0.0.0.0/0`.
- **Windows Firewall on the host**: right-click `开放防火墙.cmd` ("open the firewall") in `DIE_Server` → *Run as administrator*. It adds the same four rules.

If the host is behind a home router instead, forward the same ports to it.

**5.4 Start it**

Copy the `DIE_Server` folder to the host (a path without non-ASCII characters is safest, for example `C:\DIE_Server`) and double-click `启动服务器.cmd` ("start the server"). You should see the same lines as in step 3, plus one showing the public address.

From any other computer, open `http://<your address>:8555/status`. If it answers, players can reach you.

Closing the window stops the server. If you connect by Remote Desktop, *disconnect* rather than *sign out* — signing out closes the server with your session.

### 6. Let players in

Send players two things: the launcher link, **<https://github.com/spardanviro/DIElauncher>**, and a `launcher.json` containing your address:

```json
{ "serverHost": "203.0.113.10" }
```

They put `launcher.json` in the same folder as `DIE-Launcher.exe`, run the launcher, press **Install patch**, then **Play**. The launcher page has the full player-side walkthrough.

Each player's identity is their Steam account, verified through Steam; there is nothing to register and no passwords. An account is created on your server the first time a player logs in.

### 7. Running it day to day

**Where things are** (inside `DIE_Server`):

| Path | What it is |
|---|---|
| `data\die_reborn.db` | Every account: progress, inventory, currency. **Backing this file up backs up everything.** |
| `logs\` | One log file per day |
| `public-host.txt` | The public address; edit it and restart if the address changes |
| `leave-punish-exempt.txt` | Optional. One SteamID64 per line: accounts that are not penalised for leaving a match (useful for testing) |
| `patch\` | Optional. A copy of the launcher's client patch, served on port 8555 for players who cannot reach GitHub |

**Updating the server**

1. `git pull` in the repository, then run `打包服务器.cmd` again.
2. Stop the server. Copy `data\die_reborn.db` somewhere safe.
3. Overwrite the `DIE_Server` folder on the host with the new one. A normal package contains no database, so accounts are untouched.
4. Start the server.

**After a game update on Steam** (unlikely for this game, but if the client version changes): decompile the three DLLs again, run `tools\extract_crafting_data.py` again, and rebuild.

### 8. Settings

Set these as environment variables before starting the server.

| Variable | Effect |
|---|---|
| `DIE_GAME_ROOT` | Where to read maps and data from. Default: the `gamedata` folder beside the server, then the Steam install of the game. |
| `DIE_PUBLIC_HOST` | The public address; overrides `public-host.txt`. |
| `DIE_STATUS_PORT` | Port of the status endpoint. `0` turns it off. Default `8555`. |
| `DIE_STEAM_AUTH=trust` | **Development only.** Skips Steam verification: anyone can claim to be any Steam account. Never use it on a server others can reach. |

The login port can be given as the first command-line argument (default `1555`). Clients look for `1555` and `2555` unless their `ip.cfg` says otherwise, so changing them means changing every client too.

### 9. Troubleshooting

| Symptom | Likely cause |
|---|---|
| Build fails with thousands of errors about missing types | A decompiled tree is missing, misnamed, or was saved in the wrong folder. Compare with the layout in step 2.2. |
| Build fails in `sanitize_decompiled.py` | Python is not on `PATH`, or the trees were decompiled with something other than ILSpy 10.x. |
| `GameCraftingData.xml` not found | Step 2.3 was skipped. |
| Server exits at once with a `steam_api64` error | Step 2.4 was skipped, or the DLL does not match the `Steamworks.NET.dll` version. |
| No "signed in to Steam" line | The host cannot reach Steam. Outbound traffic must be allowed. |
| Launcher says **Offline** | Port 8555 is closed somewhere (provider firewall or Windows Firewall), or the server is not running. |
| Launcher says Online, but the game cannot log in | TCP 1555 is closed, or the player's `launcher.json` has the wrong address. |
| Players log in but matches never start, or everyone times out on loading | UDP 50000–50099 is closed, or `public-host.txt` holds a private address. |
| The game shows a login error straight away | Steam is not running on the player's PC, or the game was not started through Steam. |

When reporting a problem, include the day's file from `logs\`.

---

## About this repository

This is a snapshot export of a private working repository, so the commit history is short. Much of the original server logic was lost with the official servers; a lot of behaviour here is reconstructed from the client, and some of it is a best guess. Comments in the code say which is which. They are written in Chinese.

## License

The code in this repository is released under the [MIT License](LICENSE). The license covers this project's own code only — not the game, its assets, or the code you decompile from it, which remain the property of their owners.
