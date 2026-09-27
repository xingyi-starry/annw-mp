@echo off
setlocal EnableExtensions
chcp 65001 >nul
title XingyiStarry MP 卸载程序

for %%I in ("%~dp0.") do set "GAME_ROOT=%%~fI"
for %%I in ("%GAME_ROOT%") do set "ROOT_DRIVE=%%~dI"

if /I "%GAME_ROOT%"=="%ROOT_DRIVE%\" (
    echo [错误] 拒绝在磁盘根目录运行卸载。
    goto :failed
)
if not exist "%GAME_ROOT%\AnnW.exe" (
    echo [错误] 当前目录不是游戏目录：没有找到 AnnW.exe。
    echo 请把卸载脚本放到游戏根目录后再运行。
    goto :failed
)

tasklist /FI "IMAGENAME eq AnnW.exe" /NH 2>nul | findstr /I /C:"AnnW.exe" >nul
if not errorlevel 1 (
    echo [错误] 检测到游戏仍在运行。请完全退出所有 AnnW 进程后重试。
    goto :failed
)

:menu
cls
echo XingyiStarry MP 卸载程序
echo ==========================
echo.
echo [1] 只卸载 XingyiStarry MP ^(保留 BepInEx 和其他模组^)
echo [2] 卸载 XingyiStarry MP 和 BepInEx ^(删除全部 BepInEx 模组和配置^)
echo [Q] 取消
echo.
choice /C 12Q /N /M "请选择："
if errorlevel 3 goto :cancelled
if errorlevel 2 set "MODE=All"
if errorlevel 1 if not defined MODE set "MODE=Plugin"

echo.
if /I "%MODE%"=="All" (
    echo [警告] 此操作会删除整个 BepInEx 目录，包括其他模组、配置、缓存和日志。
) else (
    echo 将只卸载 XingyiStarry MP，并保留 BepInEx。
)
choice /C YN /N /M "确认继续？[Y/N] "
if errorlevel 2 goto :cancelled

set "FAILED=0"
echo.
echo 正在卸载...

if /I "%MODE%"=="All" (
    call :RemoveTree "BepInEx"
    call :RemoveFile ".doorstop_version"
    call :RemoveFile "doorstop_config.ini"
    call :RemoveFile "winhttp.dll"
    call :RemoveFile "changelog.txt"
) else (
    call :RemoveTree "BepInEx\plugins\XingyiStarry.Mp"
    call :RemoveTree "BepInEx\plugins\XingyiStarry.Mp.DebugTools"
    call :RemoveFile "BepInEx\patchers\XingyiStarry.Mp.EarlyPatcher.dll"
    call :RemoveFile "BepInEx\config\xingyistarry.mp.cfg"
)

call :RemoveFile "XingyiStarry.Mp.NoSteam"
call :RemoveFile "XingyiStarry.Mp-README.txt"
call :RemoveFile "XingyiStarry.Mp-LICENSE.txt"
call :RemoveFile "XingyiStarry.Mp-THIRD-PARTY-NOTICES.md"
call :RemoveTree "XingyiStarry.Mp-Licenses"
call :RemoveFile "Uninstall-XingyiStarry-MP.ps1"

if not "%FAILED%"=="0" (
    echo.
    echo [错误] 部分文件未能删除。请检查文件权限或占用情况后重试。
    goto :failed
)

echo.
if /I "%MODE%"=="All" (
    echo [完成] XingyiStarry MP 和 BepInEx 已卸载。
) else (
    echo [完成] XingyiStarry MP 已卸载，BepInEx 已保留。
)
echo 按任意键关闭此窗口...
pause >nul
(goto) 2>nul & del /f /q "%~f0"

:RemoveTree
if not exist "%GAME_ROOT%\%~1" exit /b 0
rmdir /s /q "%GAME_ROOT%\%~1" 2>nul
if exist "%GAME_ROOT%\%~1" (
    echo   [失败] %~1
    set "FAILED=1"
) else (
    echo   [已删除] %~1
)
exit /b 0

:RemoveFile
if not exist "%GAME_ROOT%\%~1" exit /b 0
del /f /q "%GAME_ROOT%\%~1" 2>nul
if exist "%GAME_ROOT%\%~1" (
    echo   [失败] %~1
    set "FAILED=1"
) else (
    echo   [已删除] %~1
)
exit /b 0

:cancelled
echo.
echo 已取消卸载。
exit /b 2

:failed
echo.
echo 按任意键退出...
pause >nul
exit /b 1
