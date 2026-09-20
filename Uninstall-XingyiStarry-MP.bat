@echo off
setlocal EnableExtensions
title XingyiStarry MP Uninstaller

set "XINGYI_UNINSTALL_SCRIPT=%~dp0Uninstall-XingyiStarry-MP.ps1"
if not exist "%XINGYI_UNINSTALL_SCRIPT%" (
    echo Uninstaller component is missing: Uninstall-XingyiStarry-MP.ps1
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$p=$env:XINGYI_UNINSTALL_SCRIPT; $c=[IO.File]::ReadAllText($p,[Text.UTF8Encoding]::new($false)); & ([ScriptBlock]::Create($c))"
set "XINGYI_UNINSTALL_RESULT=%ERRORLEVEL%"

if "%XINGYI_UNINSTALL_RESULT%"=="0" (
    if exist "%XINGYI_UNINSTALL_SCRIPT%" del /f /q "%XINGYI_UNINSTALL_SCRIPT%" >nul 2>nul
    (goto) 2>nul & del /f /q "%~f0"
)

exit /b %XINGYI_UNINSTALL_RESULT%
