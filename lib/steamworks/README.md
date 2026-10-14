# Steamworks.NET (Standalone 2025.164.1, Windows x64)

服务端用它以"Steam 游戏服务器"身份匿名登录, 核验玩家的会话票据 (见 src/DIE.Server/Steam)。

- Steamworks.NET.dll —— MIT 许可 (Steamworks.NET-LICENSE.txt), 来自 github.com/rlabrecque/Steamworks.NET 的 Standalone 发布包
- steam_api64.dll —— Valve 的 Steamworks 运行库, 带 Valve Corp. 数字签名

只放 x64: 服务端是 64 位进程。升级时整目录替换, 两个文件版本必须配套。
