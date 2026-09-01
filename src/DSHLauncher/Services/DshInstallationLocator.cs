using System.Text.Json;

namespace DSHLauncher.Services;

public sealed record DshInstallation(string NodeExecutable, string EntryPoint, string PackageDirectory);

public static class DshInstallationLocator
{
    public static bool TryResolve(out DshInstallation? installation)
    {
        installation = null;
        var dshShim = FindOnPath("dsh.cmd");
        var node = FindOnPath("node.exe");
        if (dshShim is null || node is null)
        {
            return false;
        }

        try
        {
            var npmBin = Path.GetDirectoryName(dshShim)!;
            var packageDirectory = Path.GetFullPath(Path.Combine(npmBin, "node_modules", "@deepseek-ai", "dsh"));
            var packageJson = Path.Combine(packageDirectory, "package.json");
            if (!File.Exists(packageJson))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (!document.RootElement.TryGetProperty("bin", out var bin))
            {
                return false;
            }

            string? relativeEntry = null;
            if (bin.ValueKind == JsonValueKind.String)
            {
                relativeEntry = bin.GetString();
            }
            else if (bin.ValueKind == JsonValueKind.Object && bin.TryGetProperty("dsh", out var dshBin))
            {
                relativeEntry = dshBin.GetString();
            }

            if (string.IsNullOrWhiteSpace(relativeEntry))
            {
                return false;
            }

            var entry = Path.GetFullPath(Path.Combine(packageDirectory, relativeEntry));
            var packagePrefix = packageDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!entry.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(entry))
            {
                return false;
            }

            installation = new DshInstallation(node, entry, packageDirectory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindNpmCommand() => FindOnPath("npm.cmd");

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var directory = rawDirectory.Trim().Trim('"');
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
