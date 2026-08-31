using System.Globalization;
using System.Text.RegularExpressions;

namespace DSHLauncher.Services;

public static class HarnessEndpoint
{
    public const int PreferredPort = 3080;
    public const string LoopbackHost = "127.0.0.1";
    public const string DefaultWebUrl = "http://127.0.0.1:3080";

    private static readonly Regex StartupUrlPattern = new(
        @"^\s*dsh web:\s+http://127\.0\.0\.1:(?<port>[0-9]{1,5})(?:[/\?#][^\s]*)?(?:\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string BuildWebUrl(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        return $"http://{LoopbackHost}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool TryParseStartupPort(string? line, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = StartupUrlPattern.Match(line);
        return match.Success &&
               int.TryParse(
                   match.Groups["port"].Value,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out port) &&
               port is >= 1 and <= 65535;
    }

    public static bool TryExtractPortOverride(
        IReadOnlyList<string> arguments,
        out int? requestedPort,
        out IReadOnlyList<string> remainingArguments,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        requestedPort = null;
        error = string.Empty;
        var remaining = new List<string>(arguments.Count);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (string.Equals(argument, "--port", StringComparison.OrdinalIgnoreCase))
            {
                if (requestedPort is not null)
                {
                    remainingArguments = Array.Empty<string>();
                    error = "Additional arguments contain more than one --port option.";
                    return false;
                }

                if (index + 1 >= arguments.Count ||
                    !TryParsePort(arguments[index + 1], out var parsedPort))
                {
                    remainingArguments = Array.Empty<string>();
                    error = "--port must be followed by a numeric value from 0 through 65535.";
                    return false;
                }

                requestedPort = parsedPort;
                index++;
                continue;
            }

            if (argument.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
            {
                if (requestedPort is not null)
                {
                    remainingArguments = Array.Empty<string>();
                    error = "Additional arguments contain more than one --port option.";
                    return false;
                }

                if (!TryParsePort(argument["--port=".Length..], out var parsedPort))
                {
                    remainingArguments = Array.Empty<string>();
                    error = "--port must be a numeric value from 0 through 65535.";
                    return false;
                }

                requestedPort = parsedPort;
                continue;
            }

            remaining.Add(argument);
        }

        remainingArguments = remaining;
        return true;
    }

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out port) &&
        port is >= 0 and <= 65535;
}
