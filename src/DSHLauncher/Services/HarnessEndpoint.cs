using System.Globalization;
using System.Text.RegularExpressions;

namespace DSHLauncher.Services;

public sealed record HarnessStartupAnnouncement(
    int Port,
    string BrowserUrl,
    bool RequiresAuthenticatedHandoff,
    string SanitizedLogLine);

public static class HarnessEndpoint
{
    public const int PreferredPort = 3080;
    public const string LoopbackHost = "127.0.0.1";
    public const string DefaultWebUrl = "http://127.0.0.1:3080";

    private static readonly Regex StartupUrlPattern = new(
        @"^\s*dsh web:\s+(?<url>http://127\.0\.0\.1:[0-9]{1,5}(?:[/\?#][^\s]*)?)(?:\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SensitiveValuePattern = new(
        @"(?i)(?<key>(?:token|access_token|api[_-]?key|secret)=)[^&\s)]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string BuildWebUrl(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        return $"http://{LoopbackHost}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool TryParseStartupAnnouncement(
        string? line,
        out HarnessStartupAnnouncement announcement)
    {
        announcement = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = StartupUrlPattern.Match(line);
        if (!match.Success ||
            !Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, LoopbackHost, StringComparison.Ordinal) ||
            uri.Port is < 1 or > 65535)
        {
            return false;
        }

        var cleanUrl = BuildWebUrl(uri.Port);
        var requiresAuthenticatedHandoff =
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/");

        var sanitized = requiresAuthenticatedHandoff
            ? $"dsh web: {cleanUrl} [authenticated startup URL redacted]"
            : $"dsh web: {cleanUrl}";

        announcement = new HarnessStartupAnnouncement(
            uri.Port,
            uri.AbsoluteUri,
            requiresAuthenticatedHandoff,
            sanitized);
        return true;
    }

    public static bool TryParseStartupPort(string? line, out int port)
    {
        port = 0;
        if (!TryParseStartupAnnouncement(line, out var announcement))
        {
            return false;
        }

        port = announcement.Port;
        return true;
    }

    public static string RedactSensitiveValues(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return SensitiveValuePattern.Replace(value, "${key}[redacted]");
    }

    public static bool TryFilterLauncherOwnedArguments(
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

            if (string.Equals(argument, "--host", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--host=", StringComparison.OrdinalIgnoreCase))
            {
                remainingArguments = Array.Empty<string>();
                error = "--host is managed by DSH Launcher and is fixed to 127.0.0.1 for safety.";
                return false;
            }

            if (string.Equals(argument, "--trusted-host", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--trusted-host=", StringComparison.OrdinalIgnoreCase))
            {
                remainingArguments = Array.Empty<string>();
                error = "--trusted-host is managed by DSH Launcher. Add trusted authorities in Settings instead.";
                return false;
            }

            if (string.Equals(argument, "--no-open", StringComparison.OrdinalIgnoreCase))
            {
                // The launcher always owns browser handoff, so an existing --no-open is redundant.
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
