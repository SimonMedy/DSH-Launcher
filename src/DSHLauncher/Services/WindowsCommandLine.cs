using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DSHLauncher.Services;

public static class WindowsCommandLine
{
    private static readonly string[] ReservedPrefixes = ["--host", "--trusted-host", "--port"];

    public static IReadOnlyList<string> ParseAdditionalArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows command-line parsing is only supported on Windows.");
        }

        var pointer = CommandLineToArgvW("dsh " + commandLine, out var count);
        if (pointer == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var values = new List<string>(Math.Max(0, count - 1));
            for (var i = 1; i < count; i++)
            {
                var itemPointer = Marshal.ReadIntPtr(pointer, i * IntPtr.Size);
                values.Add(Marshal.PtrToStringUni(itemPointer) ?? string.Empty);
            }

            ValidateAdditionalArguments(values);
            return values;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public static void ValidateAdditionalArguments(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            var normalized = argument.Trim();
            foreach (var reserved in ReservedPrefixes)
            {
                if (normalized.Equals(reserved, StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(reserved + "=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{reserved} is managed by DSH Launcher and cannot be supplied through Additional CLI Arguments.");
                }
            }
        }
    }

    public static string RenderRedacted(IEnumerable<string> arguments)
    {
        var output = new List<string>();
        var redactNext = false;

        foreach (var argument in arguments)
        {
            if (redactNext)
            {
                output.Add("********");
                redactNext = false;
                continue;
            }

            var lower = argument.ToLowerInvariant();
            var sensitive = lower.Contains("token") || lower.Contains("api-key") || lower.Contains("apikey") ||
                            lower.Contains("password") || lower.Contains("secret") || lower.Contains("credential") || lower.Contains("auth");

            if (sensitive && argument.Contains('='))
            {
                output.Add(argument[..(argument.IndexOf('=') + 1)] + "********");
            }
            else
            {
                output.Add(argument);
                redactNext = sensitive && argument.StartsWith('-');
            }
        }

        return string.Join(' ', output.Select(QuoteForDisplay));
    }

    private static string QuoteForDisplay(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
