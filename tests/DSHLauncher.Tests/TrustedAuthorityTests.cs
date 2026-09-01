using DSHLauncher.Services;

namespace DSHLauncher.Tests;

public sealed class TrustedAuthorityTests
{
    [Theory]
    [InlineData("my-pc.tailnet.ts.net", "my-pc.tailnet.ts.net")]
    [InlineData("100.100.20.30", "100.100.20.30")]
    [InlineData("192.168.1.50:3080", "192.168.1.50:3080")]
    [InlineData("host.example:443", "host.example:443")]
    [InlineData("[fd00::1234]", "[fd00::1234]")]
    [InlineData("[fd00::1234]:443", "[fd00::1234]:443")]
    public void AcceptsExpectedAuthorities(string input, string expected)
    {
        Assert.True(TrustedAuthority.TryNormalize(input, out var value, out _));
        Assert.Equal(expected, value);
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
    [InlineData("host.example:0")]
    [InlineData("host.example:65536")]
    public void RejectsUnsafeOrMalformedAuthorities(string input)
    {
        Assert.False(TrustedAuthority.TryNormalize(input, out _, out _));
    }
}
