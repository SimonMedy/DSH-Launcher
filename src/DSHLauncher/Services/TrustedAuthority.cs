using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DSHLauncher.Services;

public static class TrustedAuthority
{
    private static readonly Regex HostLabelPattern = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryNormalize(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Authority is empty.";
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Any(char.IsWhiteSpace) || candidate.IndexOfAny(new[] { '/', '\\', '?', '#', '@', '&', '|', '>', '<', '^', '(', ')', '%', '!', '"', '\'' }) >= 0)
        {
            error = $"'{candidate}' is not a valid host authority.";
            return false;
        }

        if (candidate.StartsWith('['))
        {
            var close = candidate.IndexOf(']');
            if (close <= 1)
            {
                error = $"'{candidate}' is not a valid bracketed IPv6 authority.";
                return false;
            }

            var addressText = candidate[1..close];
            if (!IPAddress.TryParse(addressText, out var ipv6) || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = $"'{candidate}' is not a valid IPv6 authority.";
                return false;
            }

            int? port = null;
            var suffix = candidate[(close + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || !TryParsePort(suffix[1..], out var parsedPort))
                {
                    error = $"'{candidate}' has an invalid port.";
                    return false;
                }
                port = parsedPort;
            }

            normalized = $"[{ipv6}]" + (port is null ? string.Empty : $":{port}");
            return true;
        }

        var colonCount = candidate.Count(c => c == ':');
        if (colonCount > 1)
        {
            error = "IPv6 authorities must use brackets, for example [fd00::1]:443.";
            return false;
        }

        var host = candidate;
        int? hostPort = null;
        if (colonCount == 1)
        {
            var separator = candidate.LastIndexOf(':');
            host = candidate[..separator];
            if (!TryParsePort(candidate[(separator + 1)..], out var parsedPort))
            {
                error = $"'{candidate}' has an invalid port.";
                return false;
            }
            hostPort = parsedPort;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "IPv6 authorities must use brackets.";
                return false;
            }
            normalized = ip + (hostPort is null ? string.Empty : $":{hostPort}");
            return true;
        }

        if (host.Length is < 1 or > 253 || host.StartsWith('.') || host.EndsWith('.'))
        {
            error = $"'{host}' is not a valid hostname.";
            return false;
        }

        var labels = host.Split('.');
        if (labels.Any(label => !HostLabelPattern.IsMatch(label)))
        {
            error = $"'{host}' is not a valid hostname.";
            return false;
        }

        normalized = host.ToLowerInvariant() + (hostPort is null ? string.Empty : $":{hostPort}");
        return true;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, out port) && port is >= 1 and <= 65535;
}
