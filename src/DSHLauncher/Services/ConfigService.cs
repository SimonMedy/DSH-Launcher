using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSHLauncher.Services;

public sealed class LauncherConfig
{
    [JsonPropertyName("trustedHosts")]
    public List<string> TrustedHosts { get; set; } = new();

    [JsonPropertyName("customArgs")]
    public string? CustomArgs { get; set; } = string.Empty;
}

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configPath;
    public string ConfigPath => _configPath;
    public string? LastLoadWarning { get; private set; }

    public ConfigService(string? configPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            _configPath = Path.GetFullPath(configPath);
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            return;
        }

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepSeekHarness");
        Directory.CreateDirectory(appData);
        _configPath = Path.Combine(appData, "config.json");
    }

    public LauncherConfig Load()
    {
        LastLoadWarning = null;
        if (!File.Exists(_configPath))
        {
            return new LauncherConfig();
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions)
                         ?? throw new InvalidDataException("Configuration deserialized to null.");
            config.TrustedHosts ??= new List<string>();
            return config;
        }
        catch (Exception ex)
        {
            var corruptPath = $"{_configPath}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";
            try
            {
                File.Move(_configPath, corruptPath, overwrite: false);
                LastLoadWarning = $"The configuration was invalid and has been preserved as {Path.GetFileName(corruptPath)}. Defaults are being used. ({ex.Message})";
            }
            catch (Exception preserveException)
            {
                LastLoadWarning = $"The configuration could not be read and could not be preserved. Defaults are being used. ({ex.Message}; preserve failed: {preserveException.Message})";
            }

            return new LauncherConfig();
        }
    }

    public void Save(LauncherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.TrustedHosts ??= new List<string>();

        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_configPath)}.{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(config, JsonOptions);

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            _ = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(tempPath), JsonOptions)
                ?? throw new InvalidDataException("Saved configuration failed validation.");
            File.Move(tempPath, _configPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // Best-effort temporary-file cleanup only.
            }
        }
    }

    public void OpenConfigFile()
    {
        if (!File.Exists(_configPath))
        {
            Save(new LauncherConfig());
        }

        Process.Start(new ProcessStartInfo { FileName = _configPath, UseShellExecute = true });
    }
}
