using System.Diagnostics;
using System.IO;
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

    public ConfigService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepSeekHarness");

        Directory.CreateDirectory(appData);
        _configPath = Path.Combine(appData, "config.json");
    }

    public LauncherConfig Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions);
                if (config is not null)
                {
                    config.TrustedHosts ??= new List<string>();
                    return config;
                }
            }
        }
        catch
        {
            // Fallback to default on error
        }

        var defaultConfig = new LauncherConfig();
        Save(defaultConfig);
        return defaultConfig;
    }

    public void Save(LauncherConfig config)
    {
        try
        {
            config.TrustedHosts ??= new List<string>();
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }

    public void OpenConfigFile()
    {
        if (!File.Exists(_configPath))
        {
            Save(new LauncherConfig());
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _configPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore open error
        }
    }
}
