using System.Net;

namespace DSHLauncher.Services;

public static class TrustedAuthority
{
    private static readonly char[] ForbiddenCharacters = ['/', '\\', '?', '#', '@', '"', '\'', '&', '|', '<', '>', '^', '%', '!', '`'];

    public static bool TryNormalize(string? input, out string authority, out string error)
    {
        authority = string.Empty;
        error = string.Empty;

        var value = input?.Trim() ?? string.Empty;
        if (value.Length is 0 or > 255)
        {
            error = "Authority must contain between 1 and 255 characters.";
            return false;
        }

        if (value.Any(char.IsWhiteSpace) || value.IndexOfAny(ForbiddenCharacters) >= 0 || value.Contains("://", StringComparison.Ordinal))
        {
            error = "Authority must be a hostname/IP with an optional port, not a URL, path, credential, or shell expression.";
            return false;
        }

        string host;
        string? portText = null;

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close <= 1)
            {
                error = "Invalid bracketed IPv6 authority.";
                return false;
            }

            host = value[1..close];
            var suffix = value[(close + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || suffix.Length == 1)
                {
                    error = "Invalid IPv6 port suffix.";
                    return false;
                }
                portText = suffix[1..];
            }

            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                error = "Bracket notation is only valid for IPv6 addresses.";
                return false;
            }

            authority = $"[{address}]";
        }
        else
        {
            if (value.Count(c => c == ':') > 1)
            {
                error = "IPv6 addresses must use bracket notation, for example [fd00::1234]:443.";
                return false;
            }

            var separator = value.LastIndexOf(':');
            if (separator > 0)
            {
                host = value[..separator];
                portText = value[(separator + 1)..];
            }
            else
            {
                host = value;
            }

            if (IPAddress.TryParse(host, out var address))
            {
                if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    error = "IPv6 addresses must use bracket notation.";
                    return false;
                }
                authority = address.ToString();
            }
            else
            {
                if (Uri.CheckHostName(host) != UriHostNameType.Dns)
                {
                    error = "Invalid hostname or IP address.";
                    return false;
                }
                authority = host.ToLowerInvariant();
            }
        }

        if (portText is not null)
        {
            if (!ushort.TryParse(portText, out var port) || port == 0)
            {
                error = "Port must be between 1 and 65535.";
                return false;
            }
            authority += $":{port}";
        }

        return true;
    }
}
