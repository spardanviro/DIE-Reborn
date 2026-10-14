@echo off
rem 一键打服务器包: 发布服务端 + 游戏数据 + Steam 运行库 + 开服脚本, 再压成 zip。
rem 输出在 DIE_Reborn 旁边: DIE_服务器包\ 和 DIE_服务器包.zip。
rem 不带账号库, 上传覆盖不会冲掉云上的账号。
cd /d "%~dp0"

echo ============================================
echo   打包服务器
echo ============================================
echo.

rem 有 PowerShell 7 就用它 (zip 里的中文文件名带 UTF-8 标志, 哪里解压都不乱码)
set "PS=powershell"
where pwsh >nul 2>nul && set "PS=pwsh"
%PS% -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\make-server-pack.ps1"
if errorlevel 1 (
    echo.
    echo [失败] 打包没成功, 看上面的报错。
    pause
    exit /b 1
)

echo.
echo [完成] 把 DIE_服务器包.zip 传到服务器:
echo        1. 关掉服务器上正在跑的服务端
echo        2. 备份服务器上的 DIE_Server\data\die_reborn.db (保险)
echo        3. 用新包里的 DIE_Server 覆盖服务器上的文件夹 (包里没有账号库, 不会冲掉账号)
echo        4. 运行 DIE_Server\启动服务器.cmd
echo        部署完记得打标签 (上面最后一行有命令)。
echo.
explorer.exe /select,"%~dp0..\DIE_服务器包.zip"
pause
