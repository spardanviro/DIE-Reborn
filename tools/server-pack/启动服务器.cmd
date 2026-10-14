@echo off
REM ============================================================
REM  !! This file MUST stay GBK (codepage 936) with CRLF !!
REM  (same reason as run-server.cmd)
REM ============================================================
REM 启动 DIE_Reborn 服务器。关掉这个窗口 = 关服。

title DIE_Reborn 服务器
cd /d "%~dp0"

DIE.Server.exe

echo.
echo  服务器已退出 (原因见上面几行, 完整日志在 logs 文件夹)。按任意键关闭窗口。
pause >nul
