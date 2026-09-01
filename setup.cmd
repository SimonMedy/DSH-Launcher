@echo off
setlocal
title DSH Launcher - Setup

set "PROJECT=%~dp0src\DSHLauncher\DSHLauncher.csproj"
set "INSTALL_DIR=%~dp0dist"
set "EXE=%INSTALL_DIR%\DSHLauncher.exe"

echo DSH Launcher setup
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET SDK was not found.
    echo Install the .NET 10 SDK, then run setup.cmd again.
    echo.
    pause
    exit /b 1
)

dotnet --list-sdks | findstr /R /B "10\." >nul 2>&1
if errorlevel 1 (
    echo .NET 10 SDK was not found.
    echo Install the .NET 10 SDK, then run setup.cmd again.
    echo.
    pause
    exit /b 1
)

if not exist "%PROJECT%" (
    echo Project not found:
    echo %PROJECT%
    echo.
    pause
    exit /b 1
)

echo Building DSH Launcher...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -o "%INSTALL_DIR%"
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo.
    echo Build completed but DSHLauncher.exe was not found.
    pause
    exit /b 1
)

powershell.exe -NoProfile -Command "& { param([string]$Target); $ErrorActionPreference='Stop'; $desktop=[Environment]::GetFolderPath('Desktop'); $shortcutPath=Join-Path $desktop 'DeepSeek Harness.lnk'; $shell=New-Object -ComObject WScript.Shell; $shortcut=$shell.CreateShortcut($shortcutPath); $shortcut.TargetPath=$Target; $shortcut.WorkingDirectory=Split-Path $Target -Parent; $shortcut.IconLocation=$Target + ',0'; $shortcut.Description='DSH Launcher for DeepSeek Harness'; $shortcut.Save(); Write-Host ''; Write-Host 'Shortcut created:' -ForegroundColor Green; Write-Host $shortcutPath }" "%EXE%"
if errorlevel 1 (
    echo.
    echo Shortcut creation failed.
    pause
    exit /b 1
)

echo.
echo DSH Launcher is ready.
echo.
pause
endlocal
