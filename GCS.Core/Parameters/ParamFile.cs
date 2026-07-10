using System.Globalization;

namespace GCS.Core.Parameters;

/// <summary>
/// Reads and writes Mission-Planner-compatible .param files:
/// one "NAME,VALUE" per line, '#' starts a comment. Whitespace and
/// tab-separated variants are accepted on load.
/// </summary>
public static class ParamFile
{
    public static void Save(string path, IEnumerable<KeyValuePair<string, float>> parameters, string? header = null)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine($"# GCS parameter backup {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(header))
            writer.WriteLine($"# {header}");
        foreach (var p in parameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            writer.WriteLine($"{p.Key},{p.Value.ToString("0.########", CultureInfo.InvariantCulture)}");
    }

    /// <summary>Parse a .param file. Unparseable lines are skipped and counted.</summary>
    public static (List<KeyValuePair<string, float>> Parameters, int SkippedLines) Load(string path)
    {
        var result = new List<KeyValuePair<string, float>>();
        int skipped = 0;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
                continue;

            // NAME,VALUE  |  NAME VALUE  |  NAME\tVALUE
            var parts = line.Split(new[] { ',', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                parts[0].Length is > 0 and <= 16 &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                result.Add(new KeyValuePair<string, float>(parts[0].ToUpperInvariant(), value));
            }
            else
            {
                skipped++;
            }
        }

        return (result, skipped);
    }
}
