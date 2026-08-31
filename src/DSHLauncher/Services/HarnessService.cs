using System.Diagnostics;
using System.Globalization;
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
    public const string DefaultWebUrl = HarnessEndpoint.DefaultWebUrl;

    private const string PackageName = "@deepseek-ai/dsh";
    private const long MaximumLogBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan StartupAnnouncementTimeout = TimeSpan.FromSeconds(45);

    private static readonly Regex PackageVersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(1) };
    private readonly object _logLock = new();
    private readonly object _endpointLock = new();
    private readonly string _launcherLog;
    private readonly string _harnessLog;
    private readonly string _harnessErrorLog;
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly RuntimeEndpointStore _runtimeEndpointStore = new();
    private readonly long _launcherStartTimeUtcTicks;

    private Process? _process;
    private Process? _installProcess;
    private CancellationTokenSource? _monitorCts;
    private TaskCompletionSource<HarnessStartupAnnouncement>? _startupAnnouncementSource;
    private int? _activeWebPort;
    private string? _browserLaunchUrl;
    private int _requestedWebPort = HarnessEndpoint.PreferredPort;
    private bool _stopping;
    private bool _disposed;

    public HarnessState State { get; private set; } = HarnessState.Stopped;
    public string StatusMessage { get; private set; } = "Starting";
    public ConfigService Config { get; } = new();

    public int? ActiveWebPort
    {
        get
        {
            lock (_endpointLock)
            {
                return _activeWebPort;
            }
        }
    }

    public string? WebUrl
    {
        get
        {
            var port = ActiveWebPort;
            return port is null ? null : HarnessEndpoint.BuildWebUrl(port.Value);
        }
    }

    private string? BrowserLaunchUrl
    {
        get
        {
            lock (_endpointLock)
            {
                return _browserLaunchUrl;
            }
        }
    }

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

        using var currentProcess = Process.GetCurrentProcess();
        _launcherStartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks;

        _runtimeEndpointStore.Clear();
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
        _runtimeEndpointStore.Clear();
        SetActiveEndpoint(null, null);
        SetState(HarnessState.Starting);
        UpdateStatusMessage("Starting");

        var config = Config.Load();
        if (!string.IsNullOrWhiteSpace(Config.LastRecoveryWarning))
        {
            LogLauncher($"Configuration recovery warning: {Config.LastRecoveryWarning}");
        }

        var authorities = ValidateAuthorities(config.TrustedHosts);

        if (!CommandLineTokenizer.TryTokenize(
                config.CustomArgs,
                out var customArgs,
                out var customArgsError))
        {
            SetState(HarnessState.Failed);
            UpdateStatusMessage("Invalid additional arguments");
            throw new InvalidDataException(customArgsError);
        }

        if (!HarnessEndpoint.TryFilterLauncherOwnedArguments(
                customArgs,
                out var explicitPort,
                out var remainingCustomArgs,
                out var argumentError))
        {
            SetState(HarnessState.Failed);
            UpdateStatusMessage("Invalid additional arguments");
            throw new InvalidDataException(argumentError);
        }

        _requestedWebPort = ResolveRequestedWebPort(explicitPort);
        _startupAnnouncementSource = new TaskCompletionSource<HarnessStartupAnnouncement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var runtime = ResolveDshRuntime()
            ?? throw new InvalidOperationException(
                "DeepSeek Harness is installed but its Node.js entrypoint could not be resolved.");

        AppendLine(_harnessLog, string.Empty);
        AppendLine(_harnessLog, new string('=', 64));
        AppendLine(
            _harnessLog,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting DeepSeek Harness with " +
            $"{authorities.Count} trusted authorities, {remainingCustomArgs.Count} additional arguments, " +
            $"and port {(_requestedWebPort == 0 ? "assigned by Windows" : _requestedWebPort.ToString(CultureInfo.InvariantCulture))}.");

        LogLauncher(
            $"Starting Harness via node.exe on loopback with {authorities.Count} trusted authorities, " +
            $"{remainingCustomArgs.Count} additional arguments, and " +
            $"{(_requestedWebPort == 0 ? "an OS-assigned port" : $"port {_requestedWebPort}")}. " +
            "Argument values are intentionally not logged.");

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
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(HarnessEndpoint.LoopbackHost);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_requestedWebPort.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--no-open");

        foreach (var host in authorities)
        {
            startInfo.ArgumentList.Add("--trusted-host");
            startInfo.ArgumentList.Add(host);
        }

        foreach (var argument in remainingCustomArgs)
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
            _startupAnnouncementSource = null;
            SetActiveEndpoint(null, null);
            throw;
        }

        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = new CancellationTokenSource();

        try
        {
            await MonitorUntilReadyAsync(openBrowserWhenReady, _monitorCts.Token);
        }
        catch
        {
            await CleanupFailedStartupAsync();
            throw;
        }
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
        _startupAnnouncementSource?.TrySetCanceled();

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
            _startupAnnouncementSource = null;
            _stopping = false;
            SetActiveEndpoint(null, null);
            _runtimeEndpointStore.Clear();
            SetState(HarnessState.Stopped);
            LogLauncher("Harness stopped.");
        }
    }

    public void OpenWebInterface()
    {
        var browserUrl = BrowserLaunchUrl ?? WebUrl;
        if (State != HarnessState.Running || browserUrl is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = browserUrl,
            UseShellExecute = true
        });
    }

    public void OpenHarnessLogs() => OpenFile(_harnessLog);
    public void OpenLauncherLogs() => OpenFile(_launcherLog);

    public static bool TryGetPublishedWebUrl(out string webUrl)
    {
        webUrl = string.Empty;
        var store = new RuntimeEndpointStore();

        if (!store.TryRead(
                out var launcherPid,
                out var launcherStartTimeUtcTicks,
                out var port,
                out var requiresAuthenticatedHandoff) ||
            requiresAuthenticatedHandoff)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(launcherPid);
            if (process.HasExited ||
                process.StartTime.ToUniversalTime().Ticks != launcherStartTimeUtcTicks)
            {
                return false;
            }

            var currentExecutable = Environment.ProcessPath;
            var publishedExecutable = process.MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(currentExecutable) ||
                string.IsNullOrWhiteSpace(publishedExecutable) ||
                !string.Equals(
                    Path.GetFullPath(currentExecutable),
                    Path.GetFullPath(publishedExecutable),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            webUrl = HarnessEndpoint.BuildWebUrl(port);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private int ResolveRequestedWebPort(int? explicitPort)
    {
        if (explicitPort is not null)
        {
            if (explicitPort.Value != 0 && !IsPortAvailable(explicitPort.Value))
            {
                SetState(HarnessState.Failed);
                UpdateStatusMessage($"Port {explicitPort.Value} is already in use");
                throw new InvalidOperationException(
                    $"TCP port {explicitPort.Value} was explicitly requested through --port but is already in use. " +
                    "DSH Launcher will not terminate the process that owns it. Use --port 0 or another port.");
            }

            if (explicitPort.Value == 0)
            {
                UpdateStatusMessage("Choosing a free port...");
                LogLauncher("Custom arguments requested --port 0; Windows will assign a free loopback port.");
            }

            return explicitPort.Value;
        }

        if (IsPortAvailable(HarnessEndpoint.PreferredPort))
        {
            return HarnessEndpoint.PreferredPort;
        }

        UpdateStatusMessage($"Port {HarnessEndpoint.PreferredPort} busy; choosing a free port...");
        LogLauncher(
            $"Preferred TCP port {HarnessEndpoint.PreferredPort} is already in use. " +
            "The existing listener will be left untouched and Harness will be started with --port 0.");
        return 0;
    }

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
                throw new InvalidDataException(
                    $"Invalid trusted authority in config.json: {error}");
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

        var logLine = HarnessEndpoint.RedactSensitiveValues(line);
        if (HarnessEndpoint.TryParseStartupAnnouncement(line, out var announcement))
        {
            logLine = announcement.SanitizedLogLine;

            if (_requestedWebPort != 0 && announcement.Port != _requestedWebPort)
            {
                _startupAnnouncementSource?.TrySetException(
                    new InvalidOperationException(
                        $"Harness announced port {announcement.Port}, but port {_requestedWebPort} was requested."));
            }
            else
            {
                _startupAnnouncementSource?.TrySetResult(announcement);
            }
        }

        AppendLine(isError ? _harnessErrorLog : _harnessLog, logLine);
        ProcessLogLine(logLine);
    }

    private void HandleUnexpectedProcessExit()
    {
        if (_stopping)
        {
            return;
        }

        var exitCode = SafeExitCode(_process);
        var message = $"Harness process exited unexpectedly with code {exitCode}.";
        LogLauncher(message);
        _startupAnnouncementSource?.TrySetException(new InvalidOperationException(message));
        SetActiveEndpoint(null, null);
        _runtimeEndpointStore.Clear();
        SetState(exitCode == 0 ? HarnessState.Stopped : HarnessState.Failed);
    }

    private async Task CleanupFailedStartupAsync()
    {
        _stopping = true;

        try
        {
            await StopOwnedProcessAsync(_process);
        }
        finally
        {
            try { _process?.Dispose(); } catch { }
            _process = null;
            _startupAnnouncementSource = null;
            SetActiveEndpoint(null, null);
            _runtimeEndpointStore.Clear();
            _stopping = false;
            SetState(HarnessState.Failed);
        }
    }

    private async Task InstallOrUpdateHarnessAsync(bool isUpdate)
    {
        var actionName = isUpdate ? "Updating" : "Installing";
        LogLauncher($"{actionName} DeepSeek Harness globally via npm.");

        if (!isUpdate)
        {
            SetState(HarnessState.Starting);
        }

        UpdateStatusMessage(
            isUpdate ? "Checking latest version..." : "Resolving install version...");

        var exactVersion = await ResolveLatestVersionAsync();
        var installedRuntime = ResolveDshRuntime();
        var installedVersion = installedRuntime?.Version;

        LogLauncher(
            installedVersion is null
                ? $"Resolved {PackageName} target version: {exactVersion}."
                : $"Installed {PackageName} version: {installedVersion}; target version: {exactVersion}.");

        if (isUpdate && string.Equals(installedVersion, exactVersion, StringComparison.Ordinal))
        {
            LogLauncher($"DeepSeek Harness is already up to date at {exactVersion}; npm install skipped.");
            UpdateStatusMessage(State == HarnessState.Running ? "Running" : $"Already up to date ({exactVersion})");
            return;
        }

        if (_process is { HasExited: false })
        {
            await StopAsync();
        }

        SetState(HarnessState.Starting);
        UpdateStatusMessage(
            isUpdate ? $"Updating to {exactVersion}..." : $"Installing {exactVersion}...");

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

    public static string? ExtractVersionFromJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output.Trim());
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                var value = root.GetString();
                if (!string.IsNullOrWhiteSpace(value) && PackageVersionPattern.IsMatch(value))
                {
                    return value;
                }
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                string? singleMatch = null;

                foreach (var item in root.EnumerateArray())
                {
                    string? value = null;

                    if (item.ValueKind == JsonValueKind.String)
                    {
                        value = item.GetString();
                    }
                    else if (item.ValueKind == JsonValueKind.Object &&
                             item.TryGetProperty("latest", out var latestProperty) &&
                             latestProperty.ValueKind == JsonValueKind.String)
                    {
                        value = latestProperty.GetString();
                    }

                    if (string.IsNullOrWhiteSpace(value) || !PackageVersionPattern.IsMatch(value))
                    {
                        continue;
                    }

                    if (singleMatch is not null &&
                        !string.Equals(singleMatch, value, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    singleMatch = value;
                }

                return singleMatch;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("latest", out var objectLatest) &&
                objectLatest.ValueKind == JsonValueKind.String)
            {
                var value = objectLatest.GetString();
                if (!string.IsNullOrWhiteSpace(value) && PackageVersionPattern.IsMatch(value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to line-by-line text parsing for npm variants that emit plain text.
        }

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string? textMatch = null;

        foreach (var line in lines)
        {
            var cleaned = line.Trim().Trim('"', '\'', '[', ']', ',');

            if (string.IsNullOrWhiteSpace(cleaned) || !PackageVersionPattern.IsMatch(cleaned))
            {
                continue;
            }

            if (textMatch is not null && !string.Equals(textMatch, cleaned, StringComparison.Ordinal))
            {
                return null;
            }

            textMatch = cleaned;
        }

        return textMatch;
    }

    private async Task<string> ResolveLatestVersionAsync()
    {
        var output = await RunStaticCommandAsync(
            "npm view @deepseek-ai/dsh dist-tags.latest --json",
            TimeSpan.FromSeconds(30));

        var version = ExtractVersionFromJson(output);
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException(
                "npm returned an invalid package version. Update aborted before executing an install command.");
        }

        return version;
    }

    private static DshRuntime? ResolveDshRuntime()
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

        using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
        if (!document.RootElement.TryGetProperty("bin", out var bin))
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

        string? version = null;
        if (document.RootElement.TryGetProperty("version", out var versionElement) &&
            versionElement.ValueKind == JsonValueKind.String)
        {
            var candidate = versionElement.GetString();
            if (!string.IsNullOrWhiteSpace(candidate) && PackageVersionPattern.IsMatch(candidate))
            {
                version = candidate;
            }
        }

        return new DshRuntime(nodeExe, entrypoint, version);
    }

    private static string? FirstExistingPath(string output) => output
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .FirstOrDefault(File.Exists);

    private static string RunStaticCommand(string command, TimeSpan timeout)
    {
        using var process = CreateShellProcess(command, redirectOutput: true);
        process.Start();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Static command timed out: {command}");
        }

        if (process.ExitCode != 0)
        {
            return string.Empty;
        }

        return process.StandardOutput.ReadToEnd();
    }

    private static async Task<string> RunStaticCommandAsync(string command, TimeSpan timeout)
    {
        using var process = CreateShellProcess(command, redirectOutput: true);
        process.Start();

        using var timeoutCts = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Static command timed out: {command}");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"Static command failed with exit code {process.ExitCode}: {stderr.Trim()}");
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

    private async Task MonitorUntilReadyAsync(
        bool openBrowserWhenReady,
        CancellationToken cancellationToken)
    {
        var source = _startupAnnouncementSource
            ?? throw new InvalidOperationException("Harness startup announcement tracking was not initialized.");

        HarnessStartupAnnouncement announcement;
        try
        {
            announcement = await source.Task.WaitAsync(StartupAnnouncementTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            UpdateStatusMessage("Could not verify Harness startup");
            throw new TimeoutException(
                "DeepSeek Harness did not announce its validated loopback startup URL within 45 seconds. " +
                "Startup was aborted without touching unrelated processes.");
        }

        SetActiveEndpoint(announcement.Port, announcement.BrowserUrl);
        LogLauncher($"Harness announced validated loopback port {announcement.Port}.");

        var webUrl = HarnessEndpoint.BuildWebUrl(announcement.Port);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_process is null || _process.HasExited)
            {
                return;
            }

            try
            {
                using var response = await _httpClient.GetAsync(webUrl, cancellationToken);
                var statusCode = (int)response.StatusCode;

                // The process-specific startup announcement proves this listener is DSH. A 401/403 is
                // therefore a valid ready state for newer DSH builds that require browser authentication.
                if (statusCode is >= 200 and < 500)
                {
                    PublishRuntimeEndpoint(
                        announcement.Port,
                        announcement.RequiresAuthenticatedHandoff);
                    SetState(HarnessState.Running);

                    if (announcement.Port != HarnessEndpoint.PreferredPort)
                    {
                        UpdateStatusMessage($"Running on port {announcement.Port}");
                    }

                    LogLauncher(
                        $"Harness HTTP readiness confirmed on loopback port {announcement.Port} " +
                        $"with status {statusCode}.");

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
                // Harness is still starting or temporarily unavailable.
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

    private void PublishRuntimeEndpoint(int port, bool requiresAuthenticatedHandoff)
    {
        try
        {
            _runtimeEndpointStore.Publish(
                Environment.ProcessId,
                _launcherStartTimeUtcTicks,
                port,
                requiresAuthenticatedHandoff);
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            LogLauncher(
                $"Harness is running, but the runtime endpoint could not be published for second-instance discovery: {ex.Message}");
        }
    }

    private void SetActiveEndpoint(int? port, string? browserLaunchUrl)
    {
        lock (_endpointLock)
        {
            _activeWebPort = port;
            _browserLaunchUrl = browserLaunchUrl;
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
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cancellation.Token);
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

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void LogLauncher(string message) =>
        AppendLine(_launcherLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");

    private void AppendLine(string path, string text)
    {
        lock (_logLock)
        {
            try
            {
                RotateLogIfNeeded(path);
                File.AppendAllText(
                    path,
                    HarnessEndpoint.RedactSensitiveValues(text) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take down the supervised process or update workflow.
            }
        }
    }

    private static void RotateLogIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumLogBytes)
        {
            return;
        }

        File.Move(path, path + ".1", overwrite: true);
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
        _startupAnnouncementSource?.TrySetCanceled();
        _runtimeEndpointStore.Clear();

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

    private sealed record DshRuntime(
        string NodeExe,
        string Entrypoint,
        string? Version);
}
