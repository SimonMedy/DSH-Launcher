using System.Text;

namespace DSHLauncher.Services;

public static class CommandLineTokenizer
{
    public static bool TryTokenize(string? input, out IReadOnlyList<string> arguments, out string error)
    {
        var result = new List<string>();
        error = string.Empty;
        arguments = result;

        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        if (input.IndexOf('\0') >= 0 || input.IndexOf('\r') >= 0 || input.IndexOf('\n') >= 0)
        {
            error = "Additional arguments must be a single command line.";
            return false;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var escaping = false;

        foreach (var ch in input)
        {
            if (escaping)
            {
                if (ch is '"' or '\\')
                {
                    current.Append(ch);
                }
                else
                {
                    current.Append('\\').Append(ch);
                }
                escaping = false;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (escaping)
        {
            current.Append('\\');
        }

        if (inQuotes)
        {
            error = "Additional arguments contain an unterminated quote.";
            return false;
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return true;
    }
}
