using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Verbatim.Core;

/// <summary>
/// Parsers for the stdout/stderr of the sherpa-onnx command line tools.
/// Port of parseDiarization / parseAsrLines and the stderr progress match
/// from src/main/pipeline.js.
/// </summary>
public static partial class SherpaOutput
{
    public record DiarSegment(double Start, double End, string Speaker);
    public record AsrResult(string Text, string Lang);

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*--\s*(\d+(?:\.\d+)?)\s+(speaker[_ ]?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DiarLine();

    [GeneratedRegex("\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex TextField();

    [GeneratedRegex(@"progress\s+([\d.]+)%", RegexOptions.IgnoreCase)]
    private static partial Regex Progress();

    /// <summary>Parses "start -- end speaker_NN" lines from the diarizer's stdout, sorted by start.</summary>
    public static List<DiarSegment> ParseDiarization(string stdout)
    {
        var segs = new List<DiarSegment>();
        foreach (var line in stdout.Split('\n'))
        {
            var m = DiarLine().Match(line);
            if (m.Success)
            {
                segs.Add(new DiarSegment(
                    double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    m.Groups[3].Value));
            }
        }
        return segs.OrderBy(s => s.Start).ToList();
    }

    /// <summary>Parses one JSON result object per line from sherpa-onnx-offline stdout.</summary>
    public static List<AsrResult> ParseAsrLines(string stdout)
    {
        var results = new List<AsrResult>();
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(t);
                if (doc.RootElement.TryGetProperty("text", out var textEl) &&
                    textEl.ValueKind == JsonValueKind.String)
                {
                    string lang = doc.RootElement.TryGetProperty("lang", out var langEl) &&
                                  langEl.ValueKind == JsonValueKind.String
                        ? langEl.GetString() ?? ""
                        : "";
                    results.Add(new AsrResult((textEl.GetString() ?? "").Trim(), lang));
                }
            }
            catch (JsonException)
            {
                var m = TextField().Match(t);
                if (m.Success)
                {
                    try
                    {
                        var text = JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"");
                        if (text is not null) results.Add(new AsrResult(text, ""));
                    }
                    catch (JsonException) { /* unrecoverable line; skip */ }
                }
            }
        }
        return results;
    }

    /// <summary>Extracts the percentage from a "progress 12.90%" stderr line, or null.</summary>
    public static double? ParseProgress(string stderrLine)
    {
        var m = Progress().Match(stderrLine);
        // TryParse: the [\d.]+ class can match a malformed "1.2.3"; a bad progress
        // line must never fault the stderr-reading callback (a threadpool thread).
        return m.Success && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var pct)
            ? pct : null;
    }
}
