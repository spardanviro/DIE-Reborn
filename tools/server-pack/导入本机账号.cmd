@echo off
REM ============================================================
REM  !! This file MUST stay GBK (codepage 936) with CRLF !!
REM  (same reason as run-server.cmd)
REM ============================================================
REM 把 import\die_reborn.db (打包时带上的本机账号库) 按 Steam ID 合并进本服账号库。
REM 同一个 Steam 账号: 用本机的进度覆盖; 本服没有的: 新建; 其他人: 不动。
REM 先预演一遍给你看, 确认后才真正写入。写入前会自动备份 data\die_reborn.db。

title DIE_Reborn 导入本机账号
cd /d "%~dp0"

tasklist /FI "IMAGENAME eq DIE.Server.exe" | find /I "DIE.Server.exe" >nul
if not errorlevel 1 (
    echo  服务器还开着 —— 先关掉「启动服务器.cmd」那个窗口, 再运行这个。
    pause >nul
    exit /b 1
)

if not exist "import\die_reborn.db" (
    echo  找不到 import\die_reborn.db —— 这个包打包时没有带本机账号库。
    echo  在本机关掉服务端, 双击仓库根「打包服务器^(带本机账号^).cmd」重新打包, 再上传这个包。
    pause >nul
    exit /b 1
)

echo  ===== 预演 (不写入) =====
DIE.Server.exe --import-accounts "import\die_reborn.db" --dry-run
if errorlevel 1 (
    echo  预演失败, 什么都没改。按任意键关闭。
    pause >nul
    exit /b 1
)

echo.
set /p CONFIRM= 上面列出的账号会被导入/覆盖。确认导入请输入 Y 回车:
if /I not "%CONFIRM%"=="Y" (
    echo  已取消, 什么都没改。
    pause >nul
    exit /b 0
)

if exist "data\die_reborn.db" (
    copy /Y "data\die_reborn.db" "data\die_reborn.db.导入前备份" >nul
    echo  已备份到 data\die_reborn.db.导入前备份
)

DIE.Server.exe --import-accounts "import\die_reborn.db"
echo.
echo  完成。现在可以双击「启动服务器.cmd」开服。按任意键关闭窗口。
pause >nul
