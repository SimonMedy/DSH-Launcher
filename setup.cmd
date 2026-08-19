@echo off
setlocal
title DeepSeek Harness - Setup

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$root='%~dp0'.TrimEnd('\');" ^
  "$desktop=[Environment]::GetFolderPath('Desktop');" ^
  "$wscript=Join-Path $env:SystemRoot 'System32\wscript.exe';" ^
  "$launcher=Join-Path $root 'scripts\launch-hidden.vbs';" ^
  "$icon=Join-Path $root 'assets\DeepSeekHarness.ico';" ^
  "$shortcutPath=Join-Path $desktop 'DeepSeek Harness.lnk';" ^
  "if(-not (Test-Path $launcher)){throw 'Launcher not found: ' + $launcher};" ^
  "if(-not (Test-Path $icon)){throw 'Icon not found: ' + $icon};" ^
  "$shell=New-Object -ComObject WScript.Shell;" ^
  "$shortcut=$shell.CreateShortcut($shortcutPath);" ^
  "$shortcut.TargetPath=$wscript;" ^
  "$shortcut.Arguments='\"' + $launcher + '\"';" ^
  "$shortcut.WorkingDirectory=$root;" ^
  "$shortcut.IconLocation=$icon + ',0';" ^
  "$shortcut.Description='DeepSeek Harness';" ^
  "$shortcut.Save();" ^
  "Write-Host ''; Write-Host 'Shortcut created:' -ForegroundColor Green; Write-Host $shortcutPath;" ^
  "Write-Host ''; Write-Host 'You can now pin it to the taskbar.' -ForegroundColor Cyan;"

if errorlevel 1 (
    echo.
    echo Setup failed.
    pause
    exit /b 1
)

echo.
pause
endlocal
