using DSHLauncher.Services;

namespace DSHLauncher.Tests;

public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DSHLauncher.Tests", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "config.json");

    public ConfigServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var service = new ConfigService(ConfigPath);
        service.Save(new LauncherConfig { TrustedHosts = ["host.example:443"], CustomArgs = "--foo bar" });
        var loaded = service.Load();
        Assert.Equal(new[] { "host.example:443" }, loaded.TrustedHosts);
        Assert.Equal("--foo bar", loaded.CustomArgs);
    }

    [Fact]
    public void InvalidJsonIsPreservedAndDefaultsAreReturned()
    {
        File.WriteAllText(ConfigPath, "{ definitely invalid json");
        var service = new ConfigService(ConfigPath);
        var loaded = service.Load();
        Assert.Empty(loaded.TrustedHosts);
        Assert.NotNull(service.LastLoadWarning);
        Assert.False(File.Exists(ConfigPath));
        Assert.Single(Directory.GetFiles(_directory, "config.json.corrupt.*.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
