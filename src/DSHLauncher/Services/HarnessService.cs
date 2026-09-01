using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace DSHLauncher.Services;

public enum HarnessState { Stopped, Starting, Running, Failed }

public sealed class HarnessService : IDisposable
{
    public const string WebUrl = "http://127.0.0.1:3080";
    private const int WebPort = 3080;
    private static readonly Regex SemVerRegex = new(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1) };
    private readonly object _logLock = new();
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
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekHarness", "logs");
        Directory.CreateDirectory(logDirectory);
        _launcherLog = Path.Combine(logDirectory, "launcher.log");
        _harnessLog = Path.Combine(logDirectory, "harness.log");
        _harnessErrorLog = Path.Combine(logDirectory, "harness-error.log");
        LogLauncher("WPF launcher starting.");
    }

    public bool IsDshInstalled() => DshInstallationLocator.TryResolve(out _);

    public async Task UpdateAsync()
    {
        ThrowIfDisposed();
        if (!await _actionLock.WaitAsync(0)) return;
        try { await InstallOrUpdateHarnessAsync(isUpdate: true); }
        finally { _actionLock.Release(); }
    }

    private async Task InstallOrUpdateHarnessAsync(bool isUpdate)
    {
        var actionName = isUpdate ? "Updating" : "Installing";
        if (_process is { HasExited: false }) await StopAsync();

        var npm = DshInstallationLocator.FindNpmCommand()
                  ?? throw new InvalidOperationException("npm.cmd was not found on PATH. Install a supported Node.js/npm distribution first.");

        SetState(HarnessState.Starting);
        UpdateStatusMessage(isUpdate ? "Resolving latest DeepSeek Harness version..." : "Installing DeepSeek Harness...");

        var targetVersion = await ResolveLatestVersionAsync(npm);
        LogLauncher($"{actionName} DeepSeek Harness exact version {targetVersion} via npm.");
        AppendLine(_harnessLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {actionName} DeepSeek Harness: @deepseek-ai/dsh@{targetVersion}");

        var exitCode = await RunNpmAsync(npm, ["install", "-g", $"@deepseek-ai/dsh@{targetVersion}"]);
        if (exitCode != 0)
        {
            SetState(HarnessState.Failed);
            UpdateStatusMessage("Install failed");
            throw new InvalidOperationException($"npm install failed with exit code {exitCode}. Check harness-error.log for details.");
        }

        LogLauncher($"DeepSeek Harness {(isUpdate ? "updated" : "installed")} successfully to {targetVersion}.");
        await StartAsync(openBrowserWhenReady: !isUpdate);
    }

    private async Task<string> ResolveLatestVersionAsync(string npm)
    {
        var (exitCode, stdout) = await RunNpmCaptureAsync(npm, ["view", "@deepseek-ai/dsh", "dist-tags.latest"]);
        var version = stdout.Trim();
        if (exitCode != 0 || !SemVerRegex.IsMatch(version))
        {
            throw new InvalidOperationException("npm returned an invalid latest-version value for @deepseek-ai/dsh.");
        }
        return version;
    }

    public async Task StartAsync(bool openBrowserWhenReady = false)
    {
        ThrowIfDisposed();
        if (_process is { HasExited: false }) return;

        if (!DshInstallationLocator.TryResolve(out var installation) || installation is null)
        {
            LogLauncher("DeepSeek Harness not found globally. Running initial install.");
            await InstallOrUpdateHarnessAsync(isUpdate: false);
            return;
        }

        _stopping = false;
        SetState(HarnessState.Starting);
        UpdateStatusMessage("Starting");

        await WaitForPortToBecomeFreeAsync(TimeSpan.FromSeconds(3));
        if (IsPortOccupied(WebPort))
        {
            SetState(HarnessState.Failed);
            UpdateStatusMessage("Port 3080 is already in use");
            throw new InvalidOperationException("TCP port 3080 is already in use by another process. DSH Launcher will not terminate unrelated processes; stop the conflicting application or change its port.");
        }

        var config = Config.Load();
        if (Config.LastLoadWarning is not null) LogLauncher(Config.LastLoadWarning);

        var arguments = new List<string> { installation.EntryPoint, "web" };
        foreach (var rawHost in config.TrustedHosts.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            if (!TrustedAuthority.TryNormalize(rawHost, out var authority, out var error))
            {
                SetState(HarnessState.Failed);
                throw new InvalidOperationException($"Invalid trusted authority '{rawHost}': {error}");
            }
            arguments.Add("--trusted-host");
            arguments.Add(authority);
        }

        arguments.AddRange(WindowsCommandLine.ParseAdditionalArguments(config.CustomArgs));
        var display = WindowsCommandLine.RenderRedacted(arguments.Skip(1));
        AppendLine(_harnessLog, string.Empty);
        AppendLine(_harnessLog, new string('=', 64));
        AppendLine(_harnessLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting DeepSeek Harness: dsh {display}");
        LogLauncher($"Starting Harness: dsh {display}");

        var startInfo = new ProcessStartInfo
        {
            FileName = installation.NodeExecutable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) { AppendLine(_harnessLog, e.Data); ProcessLogLine(e.Data); } };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { AppendLine(_harnessErrorLog, e.Data); ProcessLogLine(e.Data); } };
        _process.Exited += (_, _) =>
        {
            if (_stopping) return;
            var exitCode = SafeExitCode(_process);
            LogLauncher($"Harness process exited unexpectedly with code {exitCode}.");
            SetState(exitCode == 0 ? HarnessState.Stopped : HarnessState.Failed);
        };

        try
        {
            if (!_process.Start()) throw new InvalidOperationException("Windows could not start the Harness process.");
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

        try
        {
            if (_installProcess is { HasExited: false }) await KillOwnedProcessAsync(_installProcess, "install/update");
            if (_process is { HasExited: false }) await KillOwnedProcessAsync(_process, "Harness");
        }
        finally
        {
            try { _process?.Dispose(); } catch { }
            _process = null;
            _stopping = false;
            SetState(HarnessState.Stopped);
            LogLauncher("Harness stopped.");
        }
    }

    private async Task KillOwnedProcessAsync(Process process, string label)
    {
        try
        {
            var pid = process.Id;
            LogLauncher($"Stopping owned {label} process tree (PID {pid}).");
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            LogLauncher($"Timed out waiting for {label} process to exit.");
        }
        catch (Exception ex)
        {
            LogLauncher($"Failed to stop owned {label} process: {ex.Message}");
        }
    }

    public void OpenWebInterface() => Process.Start(new ProcessStartInfo { FileName = WebUrl, UseShellExecute = true });
    public void OpenHarnessLogs() => OpenFile(_harnessLog);
    public void OpenLauncherLogs() => OpenFile(_launcherLog);

    private async Task MonitorUntilReadyAsync(bool openBrowserWhenReady, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_process is null || _process.HasExited) return;
            try
            {
                using var response = await _httpClient.GetAsync(WebUrl, cancellationToken);
                if ((int)response.StatusCode is >= 200 and < 400)
                {
                    SetState(HarnessState.Running);
                    LogLauncher("Harness is ready.");
                    if (openBrowserWhenReady) OpenWebInterface();
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { }

            try { await Task.Delay(700, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ProcessLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.Contains("npm install", StringComparison.OrdinalIgnoreCase) || line.Contains("reify", StringComparison.OrdinalIgnoreCase))
            UpdateStatusMessage("Updating packages...");
        else if (line.Contains(WebUrl, StringComparison.OrdinalIgnoreCase) || line.Contains("dsh web:", StringComparison.OrdinalIgnoreCase))
            UpdateStatusMessage("Waiting for web interface...");
    }

    private async Task<int> RunNpmAsync(string npm, IReadOnlyList<string> arguments)
    {
        var (exitCode, _) = await RunNpmCaptureAsync(npm, arguments, captureForReturn: false);
        return exitCode;
    }

    private async Task<(int ExitCode, string Stdout)> RunNpmCaptureAsync(string npm, IReadOnlyList<string> arguments, bool captureForReturn = true)
    {
        var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var command = QuoteCmd(npm) + " " + string.Join(' ', arguments.Select(QuoteCmd));
        var psi = new ProcessStartInfo
        {
            FileName = comspec,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi };
        var stdout = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { if (captureForReturn) stdout.AppendLine(e.Data); else AppendLine(_harnessLog, e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLine(_harnessErrorLog, e.Data); };
        _installProcess = process;
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout.ToString());
        }
        finally
        {
            _installProcess = null;
        }
    }

    private static string QuoteCmd(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static bool IsPortOccupied(int port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);

    private static async Task WaitForPortToBecomeFreeAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsPortOccupied(WebPort) && DateTime.UtcNow < deadline) await Task.Delay(100);
    }

    private void SetState(HarnessState state)
    {
        if (State == state) return;
        State = state;
        if (state == HarnessState.Running) UpdateStatusMessage("Running");
        else if (state == HarnessState.Stopped) UpdateStatusMessage("Stopped");
        else if (state == HarnessState.Failed) UpdateStatusMessage("Failed");
        StateChanged?.Invoke(this, state);
    }

    private void UpdateStatusMessage(string message)
    {
        if (StatusMessage == message) return;
        StatusMessage = message;
        StatusMessageChanged?.Invoke(this, message);
    }

    private void OpenFile(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void LogLauncher(string message) => AppendLine(_launcherLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    private void AppendLine(string path, string text) { lock (_logLock) File.AppendAllText(path, text + Environment.NewLine); }
    private static int SafeExitCode(Process? process) { try { return process?.ExitCode ?? -1; } catch { return -1; } }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        try { if (_installProcess is { HasExited: false }) _installProcess.Kill(entireProcessTree: true); } catch { }
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        try { _process?.Dispose(); } catch { }
        _httpClient.Dispose();
        _actionLock.Dispose();
        LogLauncher("WPF launcher stopped.");
    }
}
