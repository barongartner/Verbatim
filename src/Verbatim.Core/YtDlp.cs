using System.Text.Json;
using System.Text.RegularExpressions;

namespace Verbatim.Core;

public record FetchedMedia(string FilePath, string Title, string Ext);

/// <summary>
/// Fetches audio from URLs (YouTube, podcasts, direct links, and everything
/// else yt-dlp understands). The binary is downloaded on first use and can
/// self-update via <see cref="UpdateAsync"/>.
/// </summary>
public class YtDlp(string toolsDir, string mediaDir)
{
    private const string ToolUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private const double MaxDurationSec = 4 * 3600;
    private const string MaxFilesize = "1400m";

    // The Windows app prefers m4a/mp4 so Media Foundation can always decode the
    // result (webm/opus has no MF decoder). On macOS (tests) order is harmless.
    private const string Format = "bestaudio[ext=m4a]/best[ext=mp4]/bestaudio/best";

    public string ToolPath => Path.Combine(toolsDir,
        OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    public async Task EnsureToolAsync(Action<long, long>? report, CancellationToken ct)
    {
        if (File.Exists(ToolPath)) return;
        Directory.CreateDirectory(toolsDir);
        var asset = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp_macos";
        await Downloader.DownloadWithRetryAsync(ToolUrl + asset, ToolPath, report, ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ToolPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ToolPath)) return null;
        try
        {
            var outp = await ProcessRunner.RunCheckedAsync(ToolPath, ["--version"], ct: ct).ConfigureAwait(false);
            return outp.Trim();
        }
        catch { return null; }
    }

    public async Task<string> UpdateAsync(Action<long, long>? report, CancellationToken ct)
    {
        await EnsureToolAsync(report, ct).ConfigureAwait(false);
        var outp = await ProcessRunner.RunCheckedAsync(ToolPath, ["-U"], ct: ct).ConfigureAwait(false);
        var lines = outp.Trim().Split('\n');
        return lines.Length > 0 ? lines[^1].Trim() : "Updated";
    }

    public async Task<FetchedMedia> FetchAsync(
        string url,
        IProgress<StageProgress>? progress,
        Action<long, long>? toolReport,
        CancellationToken ct)
    {
        url = url.Trim();
        if (!Regex.IsMatch(url, @"^https?://\S+$", RegexOptions.IgnoreCase))
            throw new ArgumentException("That does not look like a valid link");

        await EnsureToolAsync(toolReport, ct).ConfigureAwait(false);

        // Metadata pass first: refuse live streams and over-long audio before
        // downloading anything. %(title)j is JSON-encoded => guaranteed one line.
        progress?.Report(new("probe", 0, "Reading the link"));
        var probeOut = await ProcessRunner.RunCheckedAsync(ToolPath,
        [
            "--no-playlist", "--playlist-items", "1", "--skip-download",
            "--print", "%(title)j", "--print", "%(duration)s", "--print", "%(is_live)s",
            "--", url
        ], ct: ct).ConfigureAwait(false);

        var lines = probeOut.Trim().Split('\n');
        if (lines.Length < 3) throw new IOException("Could not read that link");
        var titleRaw = lines[^3].Trim();
        var durationRaw = lines[^2].Trim();
        var isLive = lines[^1].Trim();
        string title;
        try { title = JsonSerializer.Deserialize<string>(titleRaw) ?? "Transcript"; }
        catch { title = titleRaw; }
        if (string.Equals(isLive, "True", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live streams cannot be transcribed");
        if (double.TryParse(durationRaw, out var duration) && duration > MaxDurationSec)
            throw new InvalidOperationException("Audio longer than 4 hours is not supported yet");
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(mediaDir);
        var baseName = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        try
        {
            await ProcessRunner.RunCheckedAsync(ToolPath,
            [
                "--no-playlist", "--playlist-items", "1", "--newline",
                "-f", Format,
                "--max-filesize", MaxFilesize,
                "-o", Path.Combine(mediaDir, baseName + ".%(ext)s"),
                "--", url
            ],
            onStdoutLine: line =>
            {
                var m = Regex.Match(line, @"\[download\]\s+([\d.]+)%");
                if (m.Success && double.TryParse(m.Groups[1].Value, out var pct))
                    progress?.Report(new("download", pct, title));
            }, ct: ct).ConfigureAwait(false);
        }
        catch
        {
            DiscardPartials(baseName); // cancelled/failed: no stranded .part/.ytdl
            throw;
        }

        var file = Directory.EnumerateFiles(mediaDir, baseName + ".*")
            .FirstOrDefault(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl"));
        if (file is null)
        {
            DiscardPartials(baseName);
            throw new IOException("The audio was too large or could not be downloaded");
        }
        return new FetchedMedia(file, title.Trim().Length > 0 ? title.Trim() : "Transcript",
            Path.GetExtension(file).TrimStart('.'));
    }

    private void DiscardPartials(string baseName)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(mediaDir, baseName + ".*"))
                File.Delete(f);
        }
        catch { /* best effort */ }
    }
}
