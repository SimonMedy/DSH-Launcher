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
    public void PortOverride_Absent_PreservesArguments()
    {
        var input = new[] { "--no-open", "--foo", "bar" };

        Assert.True(HarnessEndpoint.TryExtractPortOverride(
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
    public void PortOverride_ExtractsAndNormalizesOnePort(
        string option,
        string? value,
        int expectedPort)
    {
        var input = value is null
            ? new[] { "--no-open", option, "--foo" }
            : new[] { "--no-open", option, value, "--foo" };

        Assert.True(HarnessEndpoint.TryExtractPortOverride(
            input,
            out var port,
            out var remaining,
            out var error), error);

        Assert.Equal(expectedPort, port);
        Assert.Equal(new[] { "--no-open", "--foo" }, remaining);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--port=abc")]
    [InlineData("--port=65536")]
    [InlineData("--port=-1")]
    public void PortOverride_RejectsInvalidValues(string option)
    {
        Assert.False(HarnessEndpoint.TryExtractPortOverride(
            new[] { option },
            out _,
            out _,
            out _));
    }

    [Fact]
    public void PortOverride_RejectsAmbiguousDuplicates()
    {
        Assert.False(HarnessEndpoint.TryExtractPortOverride(
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

            store.Publish(12345, 49152);

            Assert.True(store.TryRead(out var pid, out var port));
            Assert.Equal(12345, pid);
            Assert.Equal(49152, port);

            store.Clear();
            Assert.False(store.TryRead(out _, out _));
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
            File.WriteAllText(path, """{"launcherPid":12345,"port":70000}""");

            var store = new RuntimeEndpointStore(path);
            Assert.False(store.TryRead(out _, out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
