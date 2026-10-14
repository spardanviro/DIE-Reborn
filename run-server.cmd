@echo off
REM ============================================================
REM  !! This file MUST stay GBK (codepage 936) with CRLF !!
REM
REM  cmd.exe reads .cmd files in the system OEM codepage, not UTF-8.
REM  Saved as UTF-8, the Chinese bytes below decode into garbage that
REM  can contain '&' -- cmd's command separator -- which splits a REM
REM  line in two and runs the tail as a command. That produced:
REM      '釜' is not recognized as an internal or external command
REM      '"trying to connect to server"' is not recognized ...
REM ============================================================
REM
REM 起私服。双击即可, 或在终端里跑。
REM
REM 端口:
REM   TCP 1555  Request  (登录/菜单/商店)
REM   TCP 2555  Match    (匹配/开局)
REM   UDP 50000+         战斗服, 每局一个, 由 MatchPortAllocator 分配
REM
REM 客户端那侧只认 ip.cfg 里的地址。改端口的话两边都要改。
REM
REM 关掉服务端之后再进游戏, 客户端会卡在连不上服务器的提示 ——
REM 那不是故障, 是 1555 上没人监听 (客户端日志里是"目标计算机积极拒绝")。
REM 客户端每 7 秒重试一次, 所以服务端起来之后不用重启游戏。

cd /d "%~dp0"
dotnet run --project src\DIE.Server -c Debug
