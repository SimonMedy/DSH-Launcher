using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace DSHLauncher.Services;

public enum HarnessState
{
    Stopped,
    Starting,
    Running,
    Failed
}

public sealed class HarnessService : IDisposable
{
    public const string WebUrl = "http://127.0.0.1:3080";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(1)
    };

    private readonly object _logLock = new();
    private readonly string _logDirectory;
    private readonly string _launcherLog;
    private readonly string _harnessLog;
    private readonly string _harnessErrorLog;

    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private Process? _process;
    private Process? _installProcess;
    private CancellationTokenSource? _monitorCts;
    private bool _stopping;
    private bool _disposed;

    public HarnessState State { get; private set; } = HarnessState.Stopped;

    public string StatusMessage { get; private set; } = "Starting";

    public ConfigService Config { get; } = new();

    public event EventHandler<HarnessState>? StateChanged;
    public event EventHandler<string>? StatusMessageChanged;

    public HarnessService()
    {
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarness",
            "logs");

        Directory.CreateDirectory(_logDirectory);

        _launcherLog = Path.Combine(_logDirectory, "launcher.log");
        _harnessLog = Path.Combine(_logDirectory, "harness.log");
        _harnessErrorLog = Path.Combine(_logDirectory, "harness-error.log");

        LogLauncher("WPF launcher starting.");
    }

    public async Task UpdateAsync()
    {
        ThrowIfDisposed();
        if (!await _actionLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            await InstallOrUpdateHarnessAsync(isUpdate: true);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    public bool IsDshInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/d /s /c \"where.exe dsh\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(1000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task InstallOrUpdateHarnessAsync(bool isUpdate)
    {
        var actionName = isUpdate ? "Updating" : "Installing";
        LogLauncher($"{actionName} DeepSeek Harness globally via npm...");

        if (_process is { HasExited: false })
        {
            await StopAsync();
        }

        SetState(HarnessState.Starting);
        UpdateStatusMessage(isUpdate ? "Updating DeepSeek Harness..." : "Installing DeepSeek Harness...");

        AppendLine(_harnessLog, string.Empty);
        AppendLine(_harnessLog, new string('=', 64));
        AppendLine(_harnessLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {actionName} DeepSeek Harness: npm install -g @deepseek-ai/dsh@latest");

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /s /c \"npm install -g @deepseek-ai/dsh@latest\"",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendLine(_harnessLog, args.Data);
            }
        };
        proc.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendLine(_harnessErrorLog, args.Data);
            }
        };

        _installProcess = proc;

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
        }
        catch (OperationCanceledException)
        {
            LogLauncher($"{actionName} cancelled.");
            return;
        }
        catch (Exception ex)
        {
            if (_stopping)
            {
                return;
            }

            LogLauncher($"Error executing npm install: {ex.Message}");
            SetState(HarnessState.Failed);
            UpdateStatusMessage("Install failed");
            throw;
        }
        finally
        {
            _installProcess = null;
        }

        if (_stopping)
        {
            return;
        }

        if (proc.ExitCode != 0)
        {
            LogLauncher($"npm install failed with exit code {proc.ExitCode}.");
            SetState(HarnessState.Failed);
            UpdateStatusMessage("Install failed");
            throw new InvalidOperationException($"npm install failed with exit code {proc.ExitCode}. Check harness-error.log for details.");
        }

        LogLauncher($"DeepSeek Harness {(isUpdate ? "updated" : "installed")} successfully.");
        await StartAsync(openBrowserWhenReady: !isUpdate);
    }

    public async Task StartAsync(bool openBrowserWhenReady = false)
    {
        ThrowIfDisposed();

        if (_process is { HasExited: false })
        {
            return;
        }

        if (!IsDshInstalled())
        {
            LogLauncher("DeepSeek Harness not found globally. Running initial install...");
            await InstallOrUpdateHarnessAsync(isUpdate: false);
            return;
        }

        _stopping = false;
        SetState(HarnessState.Starting);
        UpdateStatusMessage("Starting");

        var config = Config.Load();
        var baseExe = IsDshInstalled() ? "dsh web" : "npx --yes @deepseek-ai/dsh web";
        var cmdBuilder = new System.Text.StringBuilder(baseExe);

        if (config.TrustedHosts is { Count: > 0 })
        {
            foreach (var host in config.TrustedHosts.Where(h => !string.IsNullOrWhiteSpace(h)))
            {
                cmdBuilder.Append($" --trusted-host {host.Trim()}");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.CustomArgs))
        {
            cmdBuilder.Append($" {config.CustomArgs.Trim()}");
        }

        var command = cmdBuilder.ToString();

        AppendLine(_harnessLog, string.Empty);
        AppendLine(_harnessLog, new string('=', 64));
        AppendLine(_harnessLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting DeepSeek Harness: {command}");
        LogLauncher($"Starting Harness command: {command}");

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /s /c \"{command}\"",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendLine(_harnessLog, args.Data);
                ProcessLogLine(args.Data);
            }
        };

        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendLine(_harnessErrorLog, args.Data);
                ProcessLogLine(args.Data);
            }
        };

        _process.Exited += (_, _) =>
        {
            if (_stopping)
            {
                return;
            }

            var exitCode = SafeExitCode(_process);
            LogLauncher($"Harness process exited unexpectedly with code {exitCode}.");
            SetState(exitCode == 0 ? HarnessState.Stopped : HarnessState.Failed);
        };

        try
        {
            KillProcessOnPort(3080);

            if (!_process.Start())
            {
                throw new InvalidOperationException("Windows could not start the Harness process.");
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            LogLauncher($"Harness process started with PID {_process.Id}.");
        }
        catch
        {
            SetState(HarnessState.Failed);
            _process.Dispose();
            _process = null;
            throw;
        }

        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = new CancellationTokenSource();

        await MonitorUntilReadyAsync(openBrowserWhenReady, _monitorCts.Token);
    }

    public async Task RestartAsync()
    {
        ThrowIfDisposed();
        LogLauncher("Restart requested.");
        await StopAsync();
        await Task.Delay(350);
        await StartAsync(openBrowserWhenReady: true);
    }

    public async Task StopAsync()
    {
        ThrowIfDisposed();
        _stopping = true;
        _monitorCts?.Cancel();

        if (_installProcess is { HasExited: false })
        {
            try
            {
                var installPid = _installProcess.Id;
                LogLauncher($"Stopping install/update process tree (PID {installPid}).");
                using var killProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/PID {installPid} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                killProcess?.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                LogLauncher($"taskkill on install process failed: {ex.Message}");
            }
        }

        if (_process is null || _process.HasExited)
        {
            SetState(HarnessState.Stopped);
            return;
        }

        var pid = _process.Id;
        LogLauncher($"Stopping Harness process tree (PID {pid}).");

        try
        {
            try
            {
                _process.CancelOutputRead();
                _process.CancelErrorRead();
            }
            catch
            {
                // Ignore if stream reading is not active.
            }

            // On Windows, taskkill /PID <pid> /T /F is the most reliable way to terminate cmd -> npx -> node tree
            try
            {
                using var killProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/PID {pid} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                if (killProcess is not null)
                {
                    using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await killProcess.WaitForExitAsync(killTimeout.Token);
                }
            }
            catch (Exception ex)
            {
                LogLauncher($"taskkill failed: {ex.Message}");
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process may have already exited via taskkill.
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                LogLauncher("WaitForExitAsync timed out, proceeding.");
            }
        }
        catch (Exception ex)
        {
            LogLauncher($"Failed to stop Harness cleanly: {ex.Message}");
        }
        finally
        {
            try
            {
                _process.Dispose();
            }
            catch
            {
                // Ignore disposal errors.
            }

            KillProcessOnPort(3080);

            _process = null;
            _stopping = false;
            SetState(HarnessState.Stopped);
            LogLauncher("Harness stopped successfully.");
        }
    }

    public void OpenWebInterface()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = WebUrl,
            UseShellExecute = true
        });
    }

    public void OpenHarnessLogs() => OpenFile(_harnessLog);

    public void OpenLauncherLogs() => OpenFile(_launcherLog);

    private async Task MonitorUntilReadyAsync(bool openBrowserWhenReady, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_process is null || _process.HasExited)
            {
                return;
            }

            try
            {
                using var response = await _httpClient.GetAsync(WebUrl, cancellationToken);
                if ((int)response.StatusCode is >= 200 and < 500)
                {
                    SetState(HarnessState.Running);
                    LogLauncher("Harness is ready.");

                    if (openBrowserWhenReady)
                    {
                        OpenWebInterface();
                    }

                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Harness is still starting.
            }

            try
            {
                await Task.Delay(700, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SetState(HarnessState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;

        if (state == HarnessState.Running)
        {
            UpdateStatusMessage("Running");
        }
        else if (state == HarnessState.Stopped)
        {
            UpdateStatusMessage("Stopped");
        }
        else if (state == HarnessState.Failed)
        {
            UpdateStatusMessage("Failed");
        }

        StateChanged?.Invoke(this, state);
    }

    private void ProcessLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Contains("Need to install", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("@deepseek-ai/dsh@", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("npm install", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("reify", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("npm notice", StringComparison.OrdinalIgnoreCase))
        {
            UpdateStatusMessage("Updating packages...");
        }
        else if (line.Contains("http://127.0.0.1:3080", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("dsh web:", StringComparison.OrdinalIgnoreCase))
        {
            SetState(HarnessState.Running);
            UpdateStatusMessage("Running");
        }
    }

    private void UpdateStatusMessage(string message)
    {
        if (StatusMessage == message)
        {
            return;
        }

        StatusMessage = message;
        StatusMessageChanged?.Invoke(this, message);
    }

    private void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void LogLauncher(string message)
    {
        AppendLine(_launcherLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    }

    private void AppendLine(string path, string text)
    {
        lock (_logLock)
        {
            File.AppendAllText(path, text + Environment.NewLine);
        }
    }

    private static int SafeExitCode(Process? process)
    {
        try
        {
            return process?.ExitCode ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();

        if (_installProcess is { HasExited: false })
        {
            try
            {
                var installPid = _installProcess.Id;
                using var kill = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/PID {installPid} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                kill?.WaitForExit(2000);
            }
            catch
            {
                // Best effort during shutdown
            }
        }

        if (_process is { HasExited: false })
        {
            try
            {
                using var kill = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/PID {_process.Id} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                kill?.WaitForExit(2000);
            }
            catch
            {
                // Best effort during shutdown.
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort during process shutdown.
            }
        }

        _process?.Dispose();
        _httpClient.Dispose();
        KillProcessOnPort(3080);
        LogLauncher("WPF launcher stopped.");
    }

    private static void KillProcessOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c for /f \"tokens=5\" %a in ('netstat -ano -p tcp ^| findstr \":{port}\" ^| findstr \"LISTENING\"') do @taskkill /PID %a /F /T",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
        }
        catch
        {
            // Ignore port cleanup errors.
        }
    }
}
