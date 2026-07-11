using System.Text.Json;
using System.Text.Json.Serialization;

namespace Verbatim.Core;

/// <summary>One diarized, transcribed span of speech.</summary>
public class TranscriptSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Speaker { get; set; } = "speaker_00";
    public string Text { get; set; } = "";
}

public class SpeakerInfo
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#8b95a5";
}

/// <summary>
/// A saved transcript. Serialized camelCase and shaped identically to the
/// v1.x (Electron) project JSON so existing libraries load unchanged.
/// </summary>
public class Project
{
    public string? Id { get; set; }
    public string App { get; set; } = "verbatim";
    public string AppVersion { get; set; } = "";
    public string Title { get; set; } = "Untitled";
    public string AudioName { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public double DurationSec { get; set; }
    public string Language { get; set; } = "";
    public string Model { get; set; } = "";
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public Dictionary<string, SpeakerInfo> Speakers { get; set; } = new();
    public List<TranscriptSegment> Segments { get; set; } = new();
    public List<double>? Peaks { get; set; }

    public string SpeakerName(string key) =>
        Speakers.TryGetValue(key, out var s) ? s.Name : key;
}

public class LibraryEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string AudioName { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public double DurationSec { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    public List<string> SpeakerNames { get; set; } = new();
    public string Snippet { get; set; } = "";
}

public class AppSettings
{
    public string Model { get; set; } = "whisper-base";
    public string Language { get; set; } = "";
    public int NumSpeakers { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public record StageProgress(string Stage, double Pct, string Detail);
