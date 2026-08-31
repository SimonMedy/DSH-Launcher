using System.Diagnostics;
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
    public string? LastRecoveryWarning { get; private set; }

    public ConfigService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarness",
            "config.json"))
    {
    }

    public ConfigService(string configPath)
    {
        _configPath = configPath;
        var directory = Path.GetDirectoryName(_configPath)
            ?? throw new ArgumentException("Config path must have a parent directory.", nameof(configPath));
        Directory.CreateDirectory(directory);
    }

    public LauncherConfig Load()
    {
        LastRecoveryWarning = null;
        if (!File.Exists(_configPath))
        {
            return new LauncherConfig();
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions)
                ?? throw new InvalidDataException("Configuration file is empty or invalid.");
            config.TrustedHosts ??= new List<string>();
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var backupPath = $"{_configPath}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";
            try
            {
                File.Copy(_configPath, backupPath, overwrite: false);
                LastRecoveryWarning = $"The configuration could not be read and was backed up to {backupPath}. Safe defaults are being used. Error: {ex.Message}";
            }
            catch (Exception backupEx)
            {
                LastRecoveryWarning = $"The configuration could not be read. Safe defaults are being used. Original file was preserved. Error: {ex.Message}. Backup failed: {backupEx.Message}";
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

        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(tempPath, json);

            var verifyJson = File.ReadAllText(tempPath);
            _ = JsonSerializer.Deserialize<LauncherConfig>(verifyJson, JsonOptions)
                ?? throw new InvalidDataException("Configuration validation failed after writing the temporary file.");

            File.Move(tempPath, _configPath, overwrite: true);
        }
        catch
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
                // Preserve the original exception.
            }
            throw;
        }
    }

    public void OpenConfigFile()
    {
        if (!File.Exists(_configPath))
        {
            Save(new LauncherConfig());
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _configPath,
            UseShellExecute = true
        });
    }
}
