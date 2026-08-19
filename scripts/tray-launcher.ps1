param(
    [string]$Url = "http://127.0.0.1:3080"
)

$ErrorActionPreference = "Stop"

$DataRoot = Join-Path $env:LOCALAPPDATA "DeepSeekHarness"
$LogDir = Join-Path $DataRoot "logs"
$LauncherLog = Join-Path $LogDir "launcher.log"
$HarnessOut = Join-Path $LogDir "harness.log"
$HarnessErr = Join-Path $LogDir "harness-error.log"

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

function Write-LauncherLog {
    param([string]$Message)

    $Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    Add-Content -LiteralPath $LauncherLog -Value "[$Timestamp] $Message"
}

function Show-Error {
    param(
        [string]$Message,
        [string]$Title = "DeepSeek Harness"
    )

    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
        [System.Windows.Forms.MessageBox]::Show(
            $Message,
            $Title,
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }
    catch {}
}

Write-LauncherLog "Launcher starting."

try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    [System.Windows.Forms.Application]::EnableVisualStyles()

    $ScriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Root = Split-Path -Parent $ScriptsDir
    $IconPath = Join-Path $Root "assets\DeepSeekHarness.ico"

    if (-not (Test-Path $IconPath)) {
        throw "Icon file not found: $IconPath"
    }

    # Prevent multiple tray launcher instances.
    $CreatedNew = $false
    $Mutex = New-Object System.Threading.Mutex(
        $true,
        "Local\DeepSeekHarnessTrayLauncher",
        [ref]$CreatedNew
    )

    if (-not $CreatedNew) {
        Write-LauncherLog "Existing tray launcher detected. Opening the web interface."
        try { Start-Process $Url } catch {}
        exit 0
    }

    $script:HarnessProcess = $null
    $script:IsExiting = $false
    $script:OpenedBrowser = $false

    function Test-HarnessReady {
        try {
            $Response = Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 1
            return ($Response.StatusCode -ge 200 -and $Response.StatusCode -lt 500)
        }
        catch {
            return $false
        }
    }

    function Open-WebInterface {
        try {
            Start-Process $Url
        }
        catch {
            Write-LauncherLog "Failed to open web interface: $($_.Exception.Message)"
        }
    }

    function Open-LauncherLogs {
        if (-not (Test-Path $LauncherLog)) {
            New-Item -ItemType File -Path $LauncherLog -Force | Out-Null
        }

        Start-Process notepad.exe -ArgumentList "`"$LauncherLog`""
    }

    function Open-HarnessLogs {
        if (-not (Test-Path $HarnessOut)) {
            New-Item -ItemType File -Path $HarnessOut -Force | Out-Null
        }

        Start-Process notepad.exe -ArgumentList "`"$HarnessOut`""
    }

    function Start-Harness {
        if ($script:HarnessProcess -and -not $script:HarnessProcess.HasExited) {
            Write-LauncherLog "Harness is already running."
            return
        }

        if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
            throw "npx was not found in PATH. Install Node.js and try again."
        }

        $script:OpenedBrowser = $false

        Add-Content -LiteralPath $HarnessOut -Value ""
        Add-Content -LiteralPath $HarnessOut -Value "============================================================"

        $Cmd = $env:ComSpec
        $Args = '/d /s /c "npx @deepseek-ai/dsh web"'

        Write-LauncherLog "Starting DeepSeek Harness."

        $script:HarnessProcess = Start-Process `
            -FilePath $Cmd `
            -ArgumentList $Args `
            -WorkingDirectory $Root `
            -WindowStyle Hidden `
            -RedirectStandardOutput $HarnessOut `
            -RedirectStandardError $HarnessErr `
            -PassThru

        Write-LauncherLog "Harness process started with PID $($script:HarnessProcess.Id)."
    }

    function Stop-Harness {
        param([switch]$NoPrompt)

        if ($script:HarnessProcess -and -not $script:HarnessProcess.HasExited) {
            if (-not $NoPrompt) {
                $Answer = [System.Windows.Forms.MessageBox]::Show(
                    "Stop DeepSeek Harness?",
                    "DeepSeek Harness",
                    [System.Windows.Forms.MessageBoxButtons]::YesNo,
                    [System.Windows.Forms.MessageBoxIcon]::Question
                )

                if ($Answer -ne [System.Windows.Forms.DialogResult]::Yes) {
                    return $false
                }
            }

            Write-LauncherLog "Stopping Harness process tree (PID $($script:HarnessProcess.Id))."

            try {
                & taskkill.exe /PID $script:HarnessProcess.Id /T /F *> $null
            }
            catch {
                Write-LauncherLog "taskkill failed: $($_.Exception.Message)"
                try {
                    Stop-Process -Id $script:HarnessProcess.Id -Force -ErrorAction SilentlyContinue
                }
                catch {}
            }

            try {
                $script:HarnessProcess.WaitForExit(5000) | Out-Null
            }
            catch {}
        }

        $script:HarnessProcess = $null
        return $true
    }

    function Restart-Harness {
        Write-LauncherLog "Restart requested."

        if (Stop-Harness -NoPrompt) {
            Start-Sleep -Milliseconds 700
            Start-Harness
        }
    }

    # Tray icon.
    $Icon = New-Object System.Drawing.Icon($IconPath)

    $Tray = New-Object System.Windows.Forms.NotifyIcon
    $Tray.Icon = $Icon
    $Tray.Visible = $true
    $Tray.Text = "DeepSeek Harness - Starting"

    # Tray context menu.
    $Menu = New-Object System.Windows.Forms.ContextMenuStrip

    $StatusItem = New-Object System.Windows.Forms.ToolStripMenuItem
    $StatusItem.Text = "DeepSeek Harness - Starting"
    $StatusItem.Enabled = $false
    [void]$Menu.Items.Add($StatusItem)

    [void]$Menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    $OpenItem = New-Object System.Windows.Forms.ToolStripMenuItem
    $OpenItem.Text = "Open DeepSeek Harness"
    $OpenItem.Enabled = $false
    $OpenItem.Add_Click({ Open-WebInterface })
    [void]$Menu.Items.Add($OpenItem)

    $HarnessLogsItem = New-Object System.Windows.Forms.ToolStripMenuItem
    $HarnessLogsItem.Text = "Open Harness Logs"
    $HarnessLogsItem.Add_Click({ Open-HarnessLogs })
    [void]$Menu.Items.Add($HarnessLogsItem)

    $LauncherLogsItem = New-Object System.Windows.Forms.ToolStripMenuItem
    $LauncherLogsItem.Text = "Open Launcher Logs"
    $LauncherLogsItem.Add_Click({ Open-LauncherLogs })
    [void]$Menu.Items.Add($LauncherLogsItem)

    $RestartItem = New-Object System.Windows.Forms.ToolStripMenuItem
    $RestartItem.Text = "Restart DeepSeek Harness"
    $RestartItem.Add_Click({ Restart-Harness })
    [void]$Menu.Items.Add($RestartItem)

    [void]$Menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    $StopItem = New-Object System.Windows.Forms.ToolStripMenuItem
    $StopItem.Text = "Stop DeepSeek Harness"
    $StopItem.Add_Click({
        if (Stop-Harness) {
            Write-LauncherLog "Stop requested from tray menu."
            $script:IsExiting = $true
            $Tray.Visible = $false
            [System.Windows.Forms.Application]::Exit()
        }
    })
    [void]$Menu.Items.Add($StopItem)

    $Tray.ContextMenuStrip = $Menu
    $Tray.Add_DoubleClick({ Open-WebInterface })

    # Monitor status.
    $Timer = New-Object System.Windows.Forms.Timer
    $Timer.Interval = 1000
    $Timer.Add_Tick({
        try {
            if ($script:IsExiting) {
                return
            }

            if ($script:HarnessProcess -and $script:HarnessProcess.HasExited) {
                $StatusItem.Text = "DeepSeek Harness - Stopped"
                $Tray.Text = "DeepSeek Harness - Stopped"
                $OpenItem.Enabled = $false
                return
            }

            if (Test-HarnessReady) {
                $StatusItem.Text = "DeepSeek Harness - Running"
                $Tray.Text = "DeepSeek Harness - Running"
                $OpenItem.Enabled = $true

                if (-not $script:OpenedBrowser) {
                    $script:OpenedBrowser = $true
                    Write-LauncherLog "Harness is ready."
                    Open-WebInterface
                }
            }
            else {
                $StatusItem.Text = "DeepSeek Harness - Starting"
                $Tray.Text = "DeepSeek Harness - Starting"
                $OpenItem.Enabled = $false
            }
        }
        catch {
            Write-LauncherLog "Timer error: $($_.Exception.Message)"
        }
    })

    Start-Harness
    $Timer.Start()

    Write-LauncherLog "Entering WinForms message loop."
    [System.Windows.Forms.Application]::Run()
}
catch {
    Write-LauncherLog "Fatal error: $($_.Exception.ToString())"
    Show-Error "DeepSeek Harness launcher failed.`n`n$($_.Exception.Message)`n`nLauncher log:`n$LauncherLog"
}
finally {
    try {
        if ($Timer) {
            $Timer.Stop()
            $Timer.Dispose()
        }
    }
    catch {}

    try {
        if (-not $script:IsExiting) {
            Stop-Harness -NoPrompt | Out-Null
        }
    }
    catch {}

    try {
        if ($Tray) {
            $Tray.Visible = $false
            $Tray.Dispose()
        }
    }
    catch {}

    try {
        if ($Icon) {
            $Icon.Dispose()
        }
    }
    catch {}

    try {
        if ($CreatedNew) {
            $Mutex.ReleaseMutex()
        }
    }
    catch {}

    try {
        if ($Mutex) {
            $Mutex.Dispose()
        }
    }
    catch {}

    Write-LauncherLog "Launcher stopped."
}
