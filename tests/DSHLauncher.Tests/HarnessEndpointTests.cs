using DSHLauncher.Services;
using Xunit;

namespace DSHLauncher.Tests;

public sealed class HarnessEndpointTests
{
    [Theory]
    [InlineData("dsh web: http://127.0.0.1:3080", 3080)]
    [InlineData("dsh web: http://127.0.0.1:49152/api/auth?token=secret", 49152)]
    [InlineData("dsh web: http://127.0.0.1:60000/bootstrap?t=abc (LAN: http://192.168.1.4:60000)", 60000)]
    public void StartupPortParser_AcceptsStrictLoopbackAnnouncements(string line, int expectedPort)
    {
        Assert.True(HarnessEndpoint.TryParseStartupPort(line, out var port));
        Assert.Equal(expectedPort, port);
    }

    [Fact]
    public void StartupAnnouncement_PreservesAuthenticatedUrlOnlyInMemoryAndRedactsLogs()
    {
        const string line =
            "dsh web: http://127.0.0.1:49152/api/auth?token=super-secret " +
            "(LAN: http://192.168.1.8:49152/api/auth?token=super-secret)";

        Assert.True(HarnessEndpoint.TryParseStartupAnnouncement(line, out var announcement));
        Assert.Equal(49152, announcement.Port);
        Assert.Equal("http://127.0.0.1:49152/api/auth?token=super-secret", announcement.BrowserUrl);
        Assert.True(announcement.RequiresAuthenticatedHandoff);
        Assert.DoesNotContain("super-secret", announcement.SanitizedLogLine, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", announcement.SanitizedLogLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SensitiveValueRedaction_RemovesTokenLikeQueryValues()
    {
        var redacted = HarnessEndpoint.RedactSensitiveValues(
            "url=http://127.0.0.1:3080/auth?token=abc123&x=1 api_key=xyz");

        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz", redacted, StringComparison.Ordinal);
        Assert.Contains("token=[redacted]", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api_key=[redacted]", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("dsh web: https://127.0.0.1:3080")]
    [InlineData("dsh web: http://localhost:3080")]
    [InlineData("dsh web: http://127.0.0.1.evil.example:3080")]
    [InlineData("dsh web: http://127.0.0.1:0")]
    [InlineData("dsh web: http://127.0.0.1:65536")]
    [InlineData("prefix dsh web: http://127.0.0.1:3080")]
    [InlineData("http://127.0.0.1:3080")]
    public void StartupPortParser_RejectsUnexpectedOrUnsafeAnnouncements(string line)
    {
        Assert.False(HarnessEndpoint.TryParseStartupPort(line, out _));
    }

    [Fact]
    public void LauncherOwnedArguments_Absent_PreservesArguments()
    {
        var input = new[] { "--foo", "bar" };

        Assert.True(HarnessEndpoint.TryFilterLauncherOwnedArguments(
            input,
            out var port,
            out var remaining,
            out var error), error);

        Assert.Null(port);
        Assert.Equal(input, remaining);
    }

    [Theory]
    [InlineData("--port", "8080", 8080)]
    [InlineData("--port", "0", 0)]
    [InlineData("--port=443", null, 443)]
    [InlineData("--PORT=0", null, 0)]
    public void LauncherOwnedArguments_ExtractsAndNormalizesOnePort(
        string option,
        string? value,
        int expectedPort)
    {
        var input = value is null
            ? new[] { "--foo", option }
            : new[] { "--foo", option, value };

        Assert.True(HarnessEndpoint.TryFilterLauncherOwnedArguments(
            input,
            out var port,
            out var remaining,
            out var error), error);

        Assert.Equal(expectedPort, port);
        Assert.Equal(new[] { "--foo" }, remaining);
    }

    [Theory]
    [InlineData("--host", "127.0.0.1")]
    [InlineData("--host=192.168.1.10", null)]
    [InlineData("--trusted-host", "example.com")]
    [InlineData("--trusted-host=example.com", null)]
    public void LauncherOwnedArguments_RejectsSecurityBoundaryOverrides(string option, string? value)
    {
        var input = value is null ? new[] { option } : new[] { option, value };

        Assert.False(HarnessEndpoint.TryFilterLauncherOwnedArguments(
            input,
            out _,
            out _,
            out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void LauncherOwnedArguments_RemovesRedundantNoOpen()
    {
        Assert.True(HarnessEndpoint.TryFilterLauncherOwnedArguments(
            new[] { "--no-open", "--foo" },
            out _,
            out var remaining,
            out var error), error);

        Assert.Equal(new[] { "--foo" }, remaining);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--port=abc")]
    [InlineData("--port=65536")]
    [InlineData("--port=-1")]
    public void LauncherOwnedArguments_RejectsInvalidPortValues(string option)
    {
        Assert.False(HarnessEndpoint.TryFilterLauncherOwnedArguments(
            new[] { option },
            out _,
            out _,
            out _));
    }

    [Fact]
    public void LauncherOwnedArguments_RejectsAmbiguousPortDuplicates()
    {
        Assert.False(HarnessEndpoint.TryFilterLauncherOwnedArguments(
            new[] { "--port", "3080", "--port=8080" },
            out _,
            out _,
            out var error));

        Assert.Contains("more than one", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeEndpointStore_RoundTripsAndClearsValidatedState()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DSHLauncher.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var path = Path.Combine(directory, "runtime.json");
            var store = new RuntimeEndpointStore(path);

            store.Publish(12345, 638922240000000000, 49152, false);

            Assert.True(store.TryRead(out var pid, out var startTicks, out var port, out var requiresAuth));
            Assert.Equal(12345, pid);
            Assert.Equal(638922240000000000, startTicks);
            Assert.Equal(49152, port);
            Assert.False(requiresAuth);

            store.Clear();
            Assert.False(store.TryRead(out _, out _, out _, out _));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RuntimeEndpointStore_PreservesOnlyAuthenticationRequirementNotToken()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DSHLauncher.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var path = Path.Combine(directory, "runtime.json");
            var store = new RuntimeEndpointStore(path);
            store.Publish(12345, 638922240000000000, 49152, true);

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.True(store.TryRead(out _, out _, out _, out var requiresAuth));
            Assert.True(requiresAuth);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RuntimeEndpointStore_RejectsTamperedPort()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DSHLauncher.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "runtime.json");
            File.WriteAllText(
                path,
                """{"launcherPid":12345,"launcherStartTimeUtcTicks":638922240000000000,"port":70000,"requiresAuthenticatedHandoff":false}""");

            var store = new RuntimeEndpointStore(path);
            Assert.False(store.TryRead(out _, out _, out _, out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
