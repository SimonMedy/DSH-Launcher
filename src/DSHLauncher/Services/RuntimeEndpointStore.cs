using System.Text.Json;

namespace DSHLauncher.Services;

public sealed class RuntimeEndpointStore
{
    private const long MaximumStateFileBytes = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _statePath;

    public RuntimeEndpointStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarness",
            "runtime.json"))
    {
    }

    public RuntimeEndpointStore(string statePath)
    {
        _statePath = statePath ?? throw new ArgumentNullException(nameof(statePath));
    }

    public void Publish(int launcherPid, int port)
    {
        if (launcherPid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(launcherPid));
        }

        _ = HarnessEndpoint.BuildWebUrl(port);

        var directory = Path.GetDirectoryName(_statePath)
            ?? throw new ArgumentException("Runtime state path must have a parent directory.", nameof(_statePath));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(new RuntimeEndpointState(launcherPid, port), JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _statePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup. Publishing errors are handled by the caller.
            }
        }
    }

    public bool TryRead(out int launcherPid, out int port)
    {
        launcherPid = 0;
        port = 0;

        try
        {
            if (!File.Exists(_statePath))
            {
                return false;
            }

            var fileInfo = new FileInfo(_statePath);
            if (fileInfo.Length is <= 0 or > MaximumStateFileBytes)
            {
                return false;
            }

            var state = JsonSerializer.Deserialize<RuntimeEndpointState>(
                File.ReadAllText(_statePath),
                JsonOptions);

            if (state is null ||
                state.LauncherPid <= 0 ||
                state.Port is < 1 or > 65535)
            {
                return false;
            }

            launcherPid = state.LauncherPid;
            port = state.Port;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Runtime discovery is convenience state. A stale file is validated before use.
        }
    }

    private sealed record RuntimeEndpointState(int LauncherPid, int Port);
}
