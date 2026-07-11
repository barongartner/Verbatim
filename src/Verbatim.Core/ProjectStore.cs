using System.Text.Json;
using System.Text.RegularExpressions;

namespace Verbatim.Core;

/// <summary>
/// Persistence for transcripts, settings, and fetched media. Uses the same
/// on-disk layout as Verbatim 1.x (%APPDATA%\Verbatim) so existing libraries
/// and downloaded models carry over untouched.
/// </summary>
public class ProjectStore(string userDir)
{
    public string UserDir => userDir;
    public string TranscriptsDir => Path.Combine(userDir, "transcripts");
    public string MediaDir => Path.Combine(userDir, "media");
    public string ModelsDir => Path.Combine(userDir, "models");
    public string ToolsDir => Path.Combine(userDir, "tools");
    private string SettingsPath => Path.Combine(userDir, "settings.json");

    public static bool IsValidId(string? id) =>
        id is not null && Regex.IsMatch(id, "^[a-f0-9]{16}$");

    private string ProjectFile(string id) =>
        IsValidId(id) ? Path.Combine(TranscriptsDir, id + ".json")
                      : throw new ArgumentException("Bad project id");

    // ---------------------------------------------------------- settings --
    public AppSettings LoadSettings()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath), Json.Options) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void SaveSettings(AppSettings s)
    {
        Directory.CreateDirectory(userDir);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json.Options));
        File.Move(tmp, SettingsPath, overwrite: true);
    }

    // ---------------------------------------------------------- projects --
    public string Save(Project p)
    {
        Directory.CreateDirectory(TranscriptsDir);
        if (!IsValidId(p.Id))
            p.Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        p.UpdatedAt = DateTime.UtcNow.ToString("o");
        p.CreatedAt ??= p.UpdatedAt;
        var tmp = ProjectFile(p.Id!) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(p, Json.Options));
        File.Move(tmp, ProjectFile(p.Id!), overwrite: true);
        return p.Id!;
    }

    public Project? Load(string id)
    {
        try
        {
            var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(ProjectFile(id)), Json.Options);
            return Normalize(p);
        }
        catch { return null; }
    }

    /// <summary>Load a project file from anywhere (import). The id is cleared so
    /// an imported copy can never overwrite an existing library entry.</summary>
    public Project? Import(string path)
    {
        try
        {
            var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(path), Json.Options);
            p = Normalize(p);
            if (p is not null) p.Id = null;
            return p;
        }
        catch { return null; }
    }

    /// <summary>Project JSON may be hand-edited or from another machine — trust nothing.</summary>
    public static Project? Normalize(Project? p)
    {
        if (p?.Segments is null) return null;
        return Clean(p);

        static Project Clean(Project p)
        {
            p.Segments = p.Segments.Where(s => s.Text is not null).ToList();
            foreach (var s in p.Segments)
            {
                s.Speaker = string.IsNullOrEmpty(s.Speaker) ? "speaker_00" : s.Speaker;
                if (s.End < s.Start) (s.Start, s.End) = (s.End, s.Start);
            }
            var keys = p.Segments.Select(s => s.Speaker).Distinct().Order().ToList();
            var speakers = new Dictionary<string, SpeakerInfo>();
            for (int i = 0; i < keys.Count; i++)
            {
                SpeakerInfo? given = null;
                p.Speakers?.TryGetValue(keys[i], out given);
                speakers[keys[i]] = new SpeakerInfo
                {
                    Name = string.IsNullOrWhiteSpace(given?.Name) ? $"Speaker {i + 1}" : given!.Name,
                    Color = given?.Color is { } c && Regex.IsMatch(c, "^#[0-9a-fA-F]{3,8}$")
                        ? c : SpeakerPalette.Color(i)
                };
            }
            p.Speakers = speakers;
            p.Title = string.IsNullOrWhiteSpace(p.Title) ? "Untitled" : p.Title;
            if (p.DurationSec <= 0 && p.Segments.Count > 0) p.DurationSec = p.Segments[^1].End;
            if (!IsValidId(p.Id)) p.Id = null;
            return p;
        }
    }

    public List<LibraryEntry> List()
    {
        var entries = new List<LibraryEntry>();
        if (!Directory.Exists(TranscriptsDir)) return entries;
        foreach (var f in Directory.EnumerateFiles(TranscriptsDir, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(f), Json.Options);
                if (p?.Segments is null) throw new JsonException("not a transcript");
                entries.Add(new LibraryEntry
                {
                    Id = Path.GetFileNameWithoutExtension(f),
                    Title = p.Title,
                    AudioName = p.AudioName,
                    AudioPath = p.AudioPath,
                    DurationSec = p.DurationSec,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt,
                    SpeakerNames = p.Speakers.Values.Select(s => s.Name).ToList(),
                    Snippet = p.Segments.Count > 0
                        ? p.Segments[0].Text[..Math.Min(160, p.Segments[0].Text.Length)] : ""
                });
            }
            catch
            {
                // Move corrupt files aside instead of silently hiding them forever.
                try { File.Move(f, f + ".corrupt", overwrite: true); } catch { /* locked */ }
            }
        }
        return entries.OrderByDescending(e => e.UpdatedAt ?? "").ToList();
    }

    /// <summary>Full-text search across the whole library: matches titles,
    /// speaker names, and transcript text; the snippet shows the first hit.</summary>
    public List<LibraryEntry> Search(string query)
    {
        query = query?.Trim() ?? "";
        if (query.Length == 0) return List();
        var results = new List<LibraryEntry>();
        if (!Directory.Exists(TranscriptsDir)) return results;
        foreach (var f in Directory.EnumerateFiles(TranscriptsDir, "*.json"))
        {
            try
            {
                var p = System.Text.Json.JsonSerializer.Deserialize<Project>(File.ReadAllText(f), Json.Options);
                if (p?.Segments is null) continue;
                var titleHit = p.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
                var speakerHit = p.Speakers.Values.Any(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
                var seg = p.Segments.FirstOrDefault(s => s.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
                if (!titleHit && !speakerHit && seg is null) continue;
                var snippet = seg is not null ? SnippetAround(seg.Text, query)
                    : p.Segments.Count > 0 ? p.Segments[0].Text[..Math.Min(160, p.Segments[0].Text.Length)] : "";
                results.Add(new LibraryEntry
                {
                    Id = Path.GetFileNameWithoutExtension(f),
                    Title = p.Title,
                    AudioName = p.AudioName,
                    AudioPath = p.AudioPath,
                    DurationSec = p.DurationSec,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt,
                    SpeakerNames = p.Speakers.Values.Select(s => s.Name).ToList(),
                    Snippet = snippet
                });
            }
            catch { /* unreadable file — List() handles quarantine */ }
        }
        return results.OrderByDescending(e => e.UpdatedAt ?? "").ToList();
    }

    private static string SnippetAround(string text, string query)
    {
        var i = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var from = Math.Max(0, i - 40);
        var len = Math.Min(text.Length - from, 160);
        var s = text.Substring(from, len);
        return (from > 0 ? "…" : "") + s + (from + len < text.Length ? "…" : "");
    }

    public void Delete(string id)
    {
        string? audioPath = null;
        try
        {
            var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(ProjectFile(id)), Json.Options);
            audioPath = p?.AudioPath;
        }
        catch { /* unreadable — still delete */ }
        File.Delete(ProjectFile(id));
        if (!string.IsNullOrEmpty(audioPath)) DiscardMediaIfUnreferenced(audioPath);
    }

    // ------------------------------------------------------------- media --
    public bool IsInMediaDir(string? p) =>
        !string.IsNullOrEmpty(p) &&
        Path.GetFullPath(p).StartsWith(MediaDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private HashSet<string> ReferencedAudioPaths(string? excludeId = null)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(TranscriptsDir)) return refs;
        foreach (var f in Directory.EnumerateFiles(TranscriptsDir, "*.json"))
        {
            if (excludeId is not null && Path.GetFileNameWithoutExtension(f) == excludeId) continue;
            try
            {
                var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(f), Json.Options);
                if (!string.IsNullOrEmpty(p?.AudioPath)) refs.Add(Path.GetFullPath(p.AudioPath));
            }
            catch { /* skip */ }
        }
        return refs;
    }

    /// <summary>Deletes a file in OUR media dir, but only when no saved transcript
    /// still points at it. User files elsewhere are never touched.</summary>
    public void DiscardMediaIfUnreferenced(string path, string? excludeId = null)
    {
        if (!IsInMediaDir(path)) return;
        if (ReferencedAudioPaths(excludeId).Contains(Path.GetFullPath(path))) return;
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Startup sweep: fetched audio that lost its transcript plus
    /// downloader journal files must not pile up invisibly.</summary>
    public void SweepMediaDir()
    {
        if (!Directory.Exists(MediaDir)) return;
        var refs = ReferencedAudioPaths();
        foreach (var f in Directory.EnumerateFiles(MediaDir))
        {
            if (f.EndsWith(".part") || f.EndsWith(".ytdl") || !refs.Contains(Path.GetFullPath(f)))
            {
                try { File.Delete(f); } catch { /* best effort */ }
            }
        }
    }
}

public static class SpeakerPalette
{
    public static readonly string[] Colors =
    [
        "#4cc2ff", "#ff9d6c", "#7ee2a8", "#e39bff", "#ffd54d",
        "#6c9fff", "#ff7fa5", "#5ee0d3", "#c9b458", "#9fb0c4"
    ];

    public static string Color(int i) => Colors[i % Colors.Length];
}
