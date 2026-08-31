using DSHLauncher.Services;

namespace DSHLauncher.Tests;

public sealed class SecurityTests
{
    [Theory]
    [InlineData("my-pc.tailnet.ts.net", "my-pc.tailnet.ts.net")]
    [InlineData("100.100.20.30", "100.100.20.30")]
    [InlineData("192.168.1.50:3080", "192.168.1.50:3080")]
    [InlineData("host.example:443", "host.example:443")]
    [InlineData("[fd00::1234]", "[fd00::1234]")]
    [InlineData("[fd00::1234]:443", "[fd00::1234]:443")]
    public void TrustedAuthority_AcceptsExpectedAuthorities(string input, string expected)
    {
        Assert.True(TrustedAuthority.TryNormalize(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("foo&&calc")]
    [InlineData("foo|bar")]
    [InlineData("foo>file")]
    [InlineData("http://example.com")]
    [InlineData("user@example.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com?x=1")]
    [InlineData("example.com#fragment")]
    [InlineData("\"example.com\"")]
    [InlineData("fd00::1234")]
    [InlineData("example.com:99999")]
    public void TrustedAuthority_RejectsUnsafeOrMalformedValues(string input)
    {
        Assert.False(TrustedAuthority.TryNormalize(input, out _, out _));
    }

    [Fact]
    public void CommandLineTokenizer_PreservesMetacharactersAsLiteralArguments()
    {
        Assert.True(CommandLineTokenizer.TryTokenize("--name \"hello world\" --literal foo&&bar", out var args, out var error), error);
        Assert.Equal(new[] { "--name", "hello world", "--literal", "foo&&bar" }, args);
    }

    [Fact]
    public void CommandLineTokenizer_RejectsUnterminatedQuotes()
    {
        Assert.False(CommandLineTokenizer.TryTokenize("--name \"broken", out _, out _));
    }

    [Fact]
    public void ConfigService_SaveAndLoad_RoundTripsAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DSHLauncher.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ConfigService(Path.Combine(directory, "config.json"));
            service.Save(new LauncherConfig { TrustedHosts = new List<string> { "example.com" }, CustomArgs = "--foo bar" });
            var loaded = service.Load();
            Assert.Equal(new[] { "example.com" }, loaded.TrustedHosts);
            Assert.Equal("--foo bar", loaded.CustomArgs);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConfigService_CorruptJson_IsPreservedAndFailsClosedToDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DSHLauncher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{not json");
            var service = new ConfigService(path);
            var loaded = service.Load();
            Assert.Empty(loaded.TrustedHosts);
            Assert.NotNull(service.LastRecoveryWarning);
            Assert.Single(Directory.GetFiles(directory, "config.json.corrupt.*.json"));
            Assert.Equal("{not json", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
