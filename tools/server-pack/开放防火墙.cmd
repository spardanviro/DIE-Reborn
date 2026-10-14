@echo off
REM ============================================================
REM  !! This file MUST stay GBK (codepage 936) with CRLF !!
REM ============================================================
REM 在 Windows 防火墙里放行服务器用的端口。只需运行一次, 要管理员权限。

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo  请右键这个文件, 选"以管理员身份运行"。
    echo.
    pause
    exit /b 1
)

netsh advfirewall firewall delete rule name="DIE_Reborn TCP" >nul 2>&1
netsh advfirewall firewall add rule name="DIE_Reborn TCP" dir=in action=allow protocol=TCP localport=1555,2555,8555
netsh advfirewall firewall delete rule name="DIE_Reborn UDP" >nul 2>&1
netsh advfirewall firewall add rule name="DIE_Reborn UDP" dir=in action=allow protocol=UDP localport=50000-50099

echo.
echo  已放行: TCP 1555, 2555 (大厅 / 匹配)、TCP 8555 (登陆器状态接口) 和 UDP 50000-50099 (战斗服)。
echo  别忘了阿里云控制台的"安全组"里也要放行同样的端口, 见 说明.txt。
echo.
pause
