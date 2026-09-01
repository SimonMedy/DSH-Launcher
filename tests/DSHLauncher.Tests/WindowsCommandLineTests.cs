using DSHLauncher.Services;

namespace DSHLauncher.Tests;

public sealed class WindowsCommandLineTests
{
    [Fact]
    public void TokenizesQuotedArgumentsWithoutShellInterpretation()
    {
        var args = WindowsCommandLine.ParseAdditionalArguments("--foo \"hello world\" value&&still-one-token");
        Assert.Equal(new[] { "--foo", "hello world", "value&&still-one-token" }, args);
    }

    [Theory]
    [InlineData("--host 0.0.0.0")]
    [InlineData("--host=0.0.0.0")]
    [InlineData("--trusted-host evil.example")]
    [InlineData("--port 9999")]
    public void RejectsLauncherManagedSecurityArguments(string input)
    {
        Assert.Throws<InvalidOperationException>(() => WindowsCommandLine.ParseAdditionalArguments(input));
    }

    [Fact]
    public void RedactsLikelySecretValues()
    {
        var rendered = WindowsCommandLine.RenderRedacted(new[] { "--api-key", "super-secret", "--token=abc", "--foo", "bar" });
        Assert.DoesNotContain("super-secret", rendered);
        Assert.DoesNotContain("abc", rendered);
        Assert.Contains("********", rendered);
    }
}
