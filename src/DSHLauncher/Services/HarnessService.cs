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
        LogLauncher($"Stopping Harness process tree (PID {_process.Id}).");

        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            LogLauncher($"Failed to stop Harness cleanly: {ex.Message}");
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _stopping = false;
            SetState(HarnessState.Stopped);
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
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort during process shutdown.
            }
        }

        _process?.Dispose();
        _httpClient.Dispose();
        LogLauncher("WPF launcher stopped.");
    }
}
