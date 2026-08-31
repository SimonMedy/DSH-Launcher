using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    private const int WebPort = 3080;
    private const string PackageName = "@deepseek-ai/dsh";

    private static readonly Regex PackageVersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarness",
            "logs");

        Directory.CreateDirectory(logDirectory);
        _launcherLog = Path.Combine(logDirectory, "launcher.log");
        _harnessLog = Path.Combine(logDirectory, "harness.log");
        _harnessErrorLog = Path.Combine(logDirectory, "harness-error.log");
        LogLauncher("WPF launcher starting.");
    }

    public bool IsDshInstalled()
    {
        try
        {
            return ResolveDshRuntime() is not null;
        }
        catch
        {
            return false;
        }
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

    public async Task StartAsync(bool openBrowserWhenReady = false)
    {
        ThrowIfDisposed();
        if (_process is { HasExited: false })
        {
            return;
        }

        if (!IsDshInstalled())
        {
            LogLauncher("DeepSeek Harness not found globally. Running initial install.");
            await InstallOrUpdateHarnessAsync(isUpdate: false);
            return;
        }

        _stopping = false;
        SetState(HarnessState.Starting);
        UpdateStatusMessage("Starting");

        if (!IsPortAvailable(WebPort))
        {
            SetState(HarnessState.Failed);
            UpdateStatusMessage("Port 3080 is already in use");
            throw new InvalidOperationException(
                "TCP port 3080 is already in use. DSH Launcher will not terminate the process that owns it. " +
                "Stop the conflicting application or reconfigure it, then try again.");
        }

        var config = Config.Load();
        var authorities = ValidateAuthorities(config.TrustedHosts);
        if (!CommandLineTokenizer.TryTokenize(config.CustomArgs, out var customArgs, out var customArgsError))
        {
            SetState(HarnessState.Failed);
            UpdateStatusMessage("Invalid additional arguments");
            throw new InvalidDataException(customArgsError);
        }

        var runtime = ResolveDshRuntime()
            ?? throw new InvalidOperationException(
                "DeepSeek Harness is installed but its Node.js entrypoint could not be resolved.");

        AppendLine(_harnessLog, string.Empty);
        AppendLine(_harnessLog, new string('=', 64));
        AppendLine(
            _harnessLog,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting DeepSeek Harness with " +
            $"{authorities.Count} trusted authorities and {customArgs.Count} additional arguments.");
        LogLauncher(
            $"Starting Harness via node.exe with {authorities.Count} trusted authorities and " +
            $"{customArgs.Count} additional arguments. Argument values are intentionally not logged.");

        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.NodeExe,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(runtime.Entrypoint);
        startInfo.ArgumentList.Add("web");

        foreach (var host in authorities)
        {
            startInfo.ArgumentList.Add("--trusted-host");
            startInfo.ArgumentList.Add(host);
        }

        foreach (var argument in customArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) => HandleProcessOutput(args.Data, isError: false);
        _process.ErrorDataReceived += (_, args) => HandleProcessOutput(args.Data, isError: true);
        _process.Exited += (_, _) => HandleUnexpectedProcessExit();

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
        _stopping = true;
        _monitorCts?.Cancel();

        try
        {
            await StopOwnedProcessAsync(_installProcess);
            _installProcess = null;

            if (_process is null || _process.HasExited)
            {
                SetState(HarnessState.Stopped);
                return;
            }

            var pid = _process.Id;
            LogLauncher($"Stopping owned Harness process tree (PID {pid}).");
            try
            {
                _process.CancelOutputRead();
                _process.CancelErrorRead();
            }
            catch
            {
                // Stream reads may already have completed.
            }

            _process.Kill(entireProcessTree: true);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                LogLauncher("Timed out while waiting for the owned Harness process tree to exit.");
            }
        }
        catch (Exception ex)
        {
            LogLauncher($"Failed to stop Harness cleanly: {ex.Message}");
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

    public void OpenWebInterface() => Process.Start(new ProcessStartInfo
    {
        FileName = WebUrl,
        UseShellExecute = true
    });

    public void OpenHarnessLogs() => OpenFile(_harnessLog);
    public void OpenLauncherLogs() => OpenFile(_launcherLog);

    private List<string> ValidateAuthorities(IEnumerable<string>? configuredAuthorities)
    {
        var authorities = new List<string>();
        foreach (var host in configuredAuthorities ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            if (!TrustedAuthority.TryNormalize(host, out var normalized, out var error))
            {
                SetState(HarnessState.Failed);
                UpdateStatusMessage("Invalid trusted authority");
                throw new InvalidDataException($"Invalid trusted authority in config.json: {error}");
            }

            authorities.Add(normalized);
        }

        return authorities;
    }

    private void HandleProcessOutput(string? line, bool isError)
    {
        if (line is null)
        {
            return;
        }

        AppendLine(isError ? _harnessErrorLog : _harnessLog, line);
        ProcessLogLine(line);
    }

    private void HandleUnexpectedProcessExit()
    {
        if (_stopping)
        {
            return;
        }

        var exitCode = SafeExitCode(_process);
        LogLauncher($"Harness process exited unexpectedly with code {exitCode}.");
        SetState(exitCode == 0 ? HarnessState.Stopped : HarnessState.Failed);
    }

    private async Task InstallOrUpdateHarnessAsync(bool isUpdate)
    {
        var actionName = isUpdate ? "Updating" : "Installing";
        LogLauncher($"{actionName} DeepSeek Harness globally via npm.");

        if (_process is { HasExited: false })
        {
            await StopAsync();
        }

        SetState(HarnessState.Starting);
        UpdateStatusMessage(isUpdate ? "Checking latest version..." : "Resolving install version...");

        var exactVersion = await ResolveLatestVersionAsync();
        LogLauncher($"Resolved {PackageName} target version: {exactVersion}.");
        UpdateStatusMessage(isUpdate ? $"Updating to {exactVersion}..." : $"Installing {exactVersion}...");

        AppendLine(_harnessLog, string.Empty);
        AppendLine(_harnessLog, new string('=', 64));
        AppendLine(
            _harnessLog,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {actionName} DeepSeek Harness: " +
            $"npm install -g {PackageName}@{exactVersion}");

        var command = $"npm install -g {PackageName}@{exactVersion}";
        using var proc = CreateShellProcess(command, redirectOutput: true);
        proc.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null) AppendLine(_harnessLog, args.Data);
        };
        proc.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) AppendLine(_harnessErrorLog, args.Data);
        };
        _installProcess = proc;

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
        }
        finally
        {
            _installProcess = null;
        }

        if (proc.ExitCode != 0)
        {
            SetState(HarnessState.Failed);
            UpdateStatusMessage("Install failed");
            throw new InvalidOperationException(
                $"npm install failed with exit code {proc.ExitCode}. Check harness-error.log for details.");
        }

        LogLauncher(
            $"DeepSeek Harness {(isUpdate ? "updated" : "installed")} successfully at exact version {exactVersion}.");
        await StartAsync(openBrowserWhenReady: !isUpdate);
    }

    private async Task<string> ResolveLatestVersionAsync()
    {
        var output = await RunStaticCommandAsync(
            "npm view @deepseek-ai/dsh version --json",
            TimeSpan.FromSeconds(30));

        string? version;
        try
        {
            version = JsonSerializer.Deserialize<string>(output.Trim());
        }
        catch (JsonException)
        {
            version = output.Trim().Trim('"');
        }

        if (string.IsNullOrWhiteSpace(version) || !PackageVersionPattern.IsMatch(version))
        {
            throw new InvalidDataException(
                "npm returned an invalid package version. Update aborted before executing an install command.");
        }

        return version;
    }

    private static (string NodeExe, string Entrypoint)? ResolveDshRuntime()
    {
        var nodeExe = FirstExistingPath(RunStaticCommand("where.exe node.exe", TimeSpan.FromSeconds(3)));
        if (nodeExe is null)
        {
            return null;
        }

        var npmRoot = RunStaticCommand("npm root -g", TimeSpan.FromSeconds(5))
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(Directory.Exists);
        if (npmRoot is null)
        {
            return null;
        }

        var packageDirectory = Path.GetFullPath(Path.Combine(npmRoot, "@deepseek-ai", "dsh"));
        var packageJsonPath = Path.Combine(packageDirectory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
        if (!doc.RootElement.TryGetProperty("bin", out var bin))
        {
            return null;
        }

        string? relativeEntrypoint = null;
        if (bin.ValueKind == JsonValueKind.String)
        {
            relativeEntrypoint = bin.GetString();
        }
        else if (bin.ValueKind == JsonValueKind.Object &&
                 bin.TryGetProperty("dsh", out var dshBin) &&
                 dshBin.ValueKind == JsonValueKind.String)
        {
            relativeEntrypoint = dshBin.GetString();
        }

        if (string.IsNullOrWhiteSpace(relativeEntrypoint))
        {
            return null;
        }

        var entrypoint = Path.GetFullPath(Path.Combine(packageDirectory, relativeEntrypoint));
        var packagePrefix = packageDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!entrypoint.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(entrypoint))
        {
            return null;
        }

        return (nodeExe, entrypoint);
    }

    private static string? FirstExistingPath(string output) => output
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .FirstOrDefault(File.Exists);

    private static string RunStaticCommand(string command, TimeSpan timeout)
    {
        using var proc = CreateShellProcess(command, redirectOutput: true);
        proc.Start();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Static command timed out: {command}");
        }

        if (proc.ExitCode != 0)
        {
            return string.Empty;
        }

        return proc.StandardOutput.ReadToEnd();
    }

    private static async Task<string> RunStaticCommandAsync(string command, TimeSpan timeout)
    {
        using var proc = CreateShellProcess(command, redirectOutput: true);
        proc.Start();

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Static command timed out: {command}");
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"Static command failed with exit code {proc.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }

    private static Process CreateShellProcess(string command, bool redirectOutput)
    {
        var shell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = $"/d /s /c \"{command}\"",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = redirectOutput,
                RedirectStandardError = redirectOutput
            }
        };
    }

    private static bool IsPortAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

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
                if ((int)response.StatusCode is >= 200 and < 400)
                {
                    SetState(HarnessState.Running);
                    LogLauncher($"Harness HTTP readiness confirmed with status {(int)response.StatusCode}.");
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

    private void ProcessLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Contains("Need to install", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("npm install", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("reify", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("npm notice", StringComparison.OrdinalIgnoreCase))
        {
            UpdateStatusMessage("Updating packages...");
        }
    }

    private static async Task StopOwnedProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cts.Token);
            }
        }
        catch
        {
            // Best effort for a process that is already exiting.
        }
    }

    private void SetState(HarnessState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        UpdateStatusMessage(state switch
        {
            HarnessState.Running => "Running",
            HarnessState.Stopped => "Stopped",
            HarnessState.Failed => "Failed",
            _ => StatusMessage
        });
        StateChanged?.Invoke(this, state);
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

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void LogLauncher(string message) =>
        AppendLine(_launcherLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");

    private void AppendLine(string path, string text)
    {
        lock (_logLock)
        {
            File.AppendAllText(path, text + Environment.NewLine);
        }
    }

    private static int SafeExitCode(Process? process)
    {
        try { return process?.ExitCode ?? -1; }
        catch { return -1; }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();

        try
        {
            if (_installProcess is { HasExited: false })
            {
                _installProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }

        try { _process?.Dispose(); } catch { }
        _httpClient.Dispose();
        _actionLock.Dispose();
        LogLauncher("WPF launcher stopped.");
    }
}
