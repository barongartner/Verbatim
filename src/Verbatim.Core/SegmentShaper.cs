using System.Text.RegularExpressions;

namespace Verbatim.Core;

/// <summary>
/// Turns raw diarization spans into whisper-sized transcript segments.
/// Port of shapeSegments and the noise-marker filter from src/main/pipeline.js.
/// </summary>
public static partial class SegmentShaper
{
    /// <summary>Whisper decodes a 30s window; keep headroom.</summary>
    public const double MaxChunkSec = 28;

    /// <summary>Join same-speaker segments separated by pauses up to this long.</summary>
    public const double MergeGapSec = 0.75;

    public const double MinSegSec = 0.15;

    [GeneratedRegex(@"^[\s.\-,]*$")]
    private static partial Regex EmptyText();

    [GeneratedRegex(@"^[[(][^\])]{0,40}[\])]$")]
    private static partial Regex BracketGroup();

    [GeneratedRegex("music|applause|noise|silence|laughter|inaudible", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseWord();

    [GeneratedRegex(@"^[♪♩♫♬\s]+$")]
    private static partial Regex MusicNotes();

    /// <summary>
    /// Merge short same-speaker runs, then split anything longer than the
    /// whisper window into equal parts. Returned segments carry no text.
    /// </summary>
    public static List<TranscriptSegment> Shape(IReadOnlyList<SherpaOutput.DiarSegment> raw, double totalSec)
    {
        var segs = raw.Where(s => s.End - s.Start >= MinSegSec).ToList();
        if (segs.Count == 0)
            segs = [new SherpaOutput.DiarSegment(0, totalSec, "speaker_00")];

        var merged = new List<TranscriptSegment>();
        foreach (var s in segs)
        {
            var last = merged.Count > 0 ? merged[^1] : null;
            if (last is not null &&
                last.Speaker == s.Speaker &&
                s.Start - last.End <= MergeGapSec &&
                s.End - last.Start <= MaxChunkSec)
            {
                last.End = s.End;
            }
            else
            {
                merged.Add(new TranscriptSegment { Start = s.Start, End = s.End, Speaker = s.Speaker, Text = "" });
            }
        }

        var shaped = new List<TranscriptSegment>();
        foreach (var s in merged)
        {
            double len = s.End - s.Start;
            if (len <= MaxChunkSec)
            {
                shaped.Add(s);
                continue;
            }
            int parts = (int)Math.Ceiling(len / MaxChunkSec);
            double step = len / parts;
            for (int i = 0; i < parts; i++)
            {
                shaped.Add(new TranscriptSegment
                {
                    Start = s.Start + i * step,
                    End = s.Start + (i + 1) * step,
                    Speaker = s.Speaker,
                    Text = ""
                });
            }
        }
        return shaped;
    }

    /// <summary>
    /// True when the text is one of whisper's non-speech markers ("[Music]",
    /// "(applause)", "♪♪") or is effectively empty — such output isn't dialogue.
    /// </summary>
    public static bool IsNoiseMarker(string text)
    {
        if (EmptyText().IsMatch(text)) return true;
        var trimmed = text.Trim();
        if (BracketGroup().IsMatch(trimmed) && NoiseWord().IsMatch(trimmed)) return true;
        return MusicNotes().IsMatch(trimmed);
    }
}
