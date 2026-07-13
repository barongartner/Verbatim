using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Verbatim.Core;

/// <summary>
/// Text exporters for a transcript project. Port of buildTxt/buildSrt/buildVtt
/// (+ fmtTime/srtTime/oneLine) from src/renderer/app.js. TXT and SRT use CRLF
/// (Windows app); VTT uses LF per the WebVTT spec.
/// </summary>
public static partial class Exporters
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Collapses internal whitespace/newlines to single spaces and trims.</summary>
    private static string OneLine(string s) => Whitespace().Replace(s, " ").Trim();

    /// <summary>M:SS, or H:MM:SS once the time reaches an hour.</summary>
    public static string FmtTime(double t)
    {
        int total = (int)Math.Floor(Math.Max(0, t));
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>HH:MM:SS{sep}mmm — comma separator for SRT, dot for VTT.</summary>
    private static string SrtTime(double t, char sep)
    {
        // Work in whole milliseconds so rounding carries into seconds instead of
        // emitting a 4-digit ",1000" field (breaks SRT/VTT parsers).
        long totalMs = (long)Math.Round(Math.Max(0, t) * 1000, MidpointRounding.AwayFromZero);
        long ms = totalMs % 1000, total = totalMs / 1000;
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return $"{h:00}:{m:00}:{s:00}{sep}{ms:000}";
    }

    private static string EscVtt(string s) =>
        OneLine(s).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string CreatedDate(Project p)
    {
        var date = DateTime.TryParse(p.CreatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.Now;
        return date.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture);
    }

    public static string BuildTxt(Project p)
    {
        var lines = new List<string>
        {
            p.Title,
            $"Transcribed by Verbatim — {CreatedDate(p)}",
            ""
        };
        foreach (var s in p.Segments)
            lines.Add($"[{FmtTime(s.Start)}] {p.SpeakerName(s.Speaker)}: {s.Text}");
        // The Electron app converted the whole document (including newlines
        // embedded in segment text) to CRLF on Windows; match that.
        return (string.Join("\n", lines) + "\n").Replace("\n", "\r\n");
    }

    public static string BuildSrt(Project p)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < p.Segments.Count; i++)
        {
            var s = p.Segments[i];
            if (i > 0) sb.Append("\r\n");
            sb.Append(i + 1).Append("\r\n")
              .Append(SrtTime(s.Start, ',')).Append(" --> ").Append(SrtTime(s.End, ',')).Append("\r\n")
              .Append(OneLine(p.SpeakerName(s.Speaker))).Append(": ").Append(OneLine(s.Text)).Append("\r\n");
        }
        return sb.ToString();
    }

    public static string BuildVtt(Project p)
    {
        var cues = p.Segments.Select(s =>
            $"{SrtTime(s.Start, '.')} --> {SrtTime(s.End, '.')}\n<v {EscVtt(p.SpeakerName(s.Speaker))}>{EscVtt(s.Text)}\n");
        return "WEBVTT\n\n" + string.Join("\n", cues);
    }
}
