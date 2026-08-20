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

    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private bool _stopping;
    private bool _disposed;

    public HarnessState State { get; private set; } = HarnessState.Stopped;

    public event EventHandler<HarnessState>? StateChanged;

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

    public async Task StartAsync(bool openBrowserWhenReady = false)
    {
        ThrowIfDisposed();

        if (_process is { HasExited: false })
        {
            return;
        }

        _stopping = false;
        SetState(HarnessState.Starting);

        AppendLine(_harnessLog, string.Empty);
        AppendLine(_harnessLog, new string('=', 64));
        AppendLine(_harnessLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting DeepSeek Harness");

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /s /c \"npx @deepseek-ai/dsh web\"",
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
            }
        };

        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendLine(_harnessErrorLog, args.Data);
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

        _monitorCts?.Cancel();

        if (_process is null || _process.HasExited)
        {
            SetState(HarnessState.Stopped);
            return;
        }

        _stopping = true;
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
        StateChanged?.Invoke(this, state);
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
