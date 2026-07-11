namespace Verbatim.Core;

public record WhisperModel(string Id, string Label, string Detail, int DownloadMB, int DiskMB, string Url, string Dir, string Prefix);

public record ModelProgress(string Item, string Label, long Received, long Total, string Status);

/// <summary>
/// Downloads and installs the speech models from the sherpa-onnx GitHub
/// releases on first use. Downloads resume via Range requests and verify
/// their byte counts; archives extract into a staging directory and are
/// moved into place atomically so a crash can never leave a half-installed
/// model that looks installed.
/// </summary>
public class ModelManager(string modelsDir)
{
    private const string GH = "https://github.com/k2-fsa/sherpa-onnx/releases/download";

    public static readonly WhisperModel[] WhisperModels =
    [
        new("whisper-tiny", "Whisper Tiny", "Fastest, good for quick drafts", 111, 45,
            $"{GH}/asr-models/sherpa-onnx-whisper-tiny.tar.bz2", "sherpa-onnx-whisper-tiny", "tiny"),
        new("whisper-base", "Whisper Base", "Recommended balance of speed and accuracy", 207, 85,
            $"{GH}/asr-models/sherpa-onnx-whisper-base.tar.bz2", "sherpa-onnx-whisper-base", "base"),
        new("whisper-small", "Whisper Small", "Most accurate, slower and a bigger download", 610, 270,
            $"{GH}/asr-models/sherpa-onnx-whisper-small.tar.bz2", "sherpa-onnx-whisper-small", "small")
    ];

    private const string SegUrl = $"{GH}/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2";
    private const string SegDir = "sherpa-onnx-pyannote-segmentation-3-0";
    // NB: "recongition" typo is in the upstream release tag itself.
    private const string EmbUrl = $"{GH}/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";
    private const string EmbFile = "campplus_sv_zh_en_advanced.onnx";

    public string ModelsDir => modelsDir;
    public string SegmentationPath => Path.Combine(modelsDir, SegDir, "model.onnx");
    public string EmbeddingPath => Path.Combine(modelsDir, EmbFile);

    public (string Encoder, string Decoder, string Tokens) WhisperPaths(string modelId)
    {
        var m = WhisperModels.FirstOrDefault(w => w.Id == modelId)
            ?? throw new ArgumentException($"Unknown model: {modelId}");
        var dir = Path.Combine(modelsDir, m.Dir);
        return (Path.Combine(dir, $"{m.Prefix}-encoder.int8.onnx"),
                Path.Combine(dir, $"{m.Prefix}-decoder.int8.onnx"),
                Path.Combine(dir, $"{m.Prefix}-tokens.txt"));
    }

    public bool WhisperReady(string modelId)
    {
        var (e, d, t) = WhisperPaths(modelId);
        return File.Exists(e) && File.Exists(d) && File.Exists(t);
    }

    public bool DiarizationReady() => File.Exists(SegmentationPath) && File.Exists(EmbeddingPath);

    public long StorageBytes()
    {
        if (!Directory.Exists(modelsDir)) return 0;
        return new DirectoryInfo(modelsDir)
            .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
    }

    public async Task EnsureAsync(string modelId, IProgress<ModelProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(modelsDir);
        if (!WhisperReady(modelId))
        {
            var m = WhisperModels.First(w => w.Id == modelId);
            await InstallArchiveAsync(m.Id, m.Label, m.Url, m.Dir, progress, ct).ConfigureAwait(false);
            PruneWhisper(m);
        }
        if (!File.Exists(SegmentationPath))
        {
            await InstallArchiveAsync("segmentation", "Speaker segmentation", SegUrl, SegDir, progress, ct).ConfigureAwait(false);
        }
        if (!File.Exists(EmbeddingPath))
        {
            progress?.Report(new("embedding", "Speaker voiceprints", 0, 0, "downloading"));
            await Downloader.DownloadWithRetryAsync(EmbUrl, EmbeddingPath,
                (r, t) => progress?.Report(new("embedding", "Speaker voiceprints", r, t, "downloading")), ct).ConfigureAwait(false);
            progress?.Report(new("embedding", "Speaker voiceprints", 0, 0, "done"));
        }
    }

    private async Task InstallArchiveAsync(string item, string label, string url, string dir,
        IProgress<ModelProgress>? progress, CancellationToken ct)
    {
        var tarPath = Path.Combine(modelsDir, $"{item}.tar.bz2");
        var staging = Path.Combine(modelsDir, $".staging-{item}");
        try
        {
            progress?.Report(new(item, label, 0, 0, "downloading"));
            await Downloader.DownloadWithRetryAsync(url, tarPath,
                (r, t) => progress?.Report(new(item, label, r, t, "downloading")), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(item, label, 0, 0, "extracting"));

            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            await TarExtractor.ExtractTarBz2Async(tarPath, staging, ct).ConfigureAwait(false);

            var extracted = Path.Combine(staging, dir);
            if (!Directory.Exists(extracted)) throw new IOException($"Archive did not contain {dir}");
            var dest = Path.Combine(modelsDir, dir);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            await MoveWithRetryAsync(extracted, dest).ConfigureAwait(false);
            progress?.Report(new(item, label, 0, 0, "done"));
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* best effort */ }
            try { File.Delete(tarPath); } catch { /* Defender may hold it briefly */ }
        }
    }

    /// <summary>Windows can transiently lock freshly written files (Defender, indexer).</summary>
    private static async Task MoveWithRetryAsync(string src, string dest, int attempts = 5)
    {
        for (int i = 0; ; i++)
        {
            try { Directory.Move(src, dest); return; }
            catch (IOException) when (i < attempts - 1) { await Task.Delay(250 * (i + 1)); }
            catch (UnauthorizedAccessException) when (i < attempts - 1) { await Task.Delay(250 * (i + 1)); }
        }
    }

    /// <summary>The whisper archives ship fp32 + int8 copies; we only use int8.</summary>
    private void PruneWhisper(WhisperModel m)
    {
        var dir = Path.Combine(modelsDir, m.Dir);
        foreach (var junk in new[] { $"{m.Prefix}-encoder.onnx", $"{m.Prefix}-decoder.onnx" })
        {
            try { File.Delete(Path.Combine(dir, junk)); } catch { /* best effort */ }
        }
        try { Directory.Delete(Path.Combine(dir, "test_wavs"), true); } catch { /* best effort */ }
    }
}

public static class TarExtractor
{
    /// <summary>tar.exe (libarchive) ships with Windows 10 1803+; use the System32
    /// copy by absolute path so a GNU tar earlier on PATH can't shadow it.</summary>
    public static string TarBinary()
    {
        if (OperatingSystem.IsWindows())
        {
            var sys = Path.Combine(
                Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                "System32", "tar.exe");
            if (File.Exists(sys)) return sys;
        }
        return "tar";
    }

    public static Task ExtractTarBz2Async(string tarPath, string destDir, CancellationToken ct) =>
        ProcessRunner.RunCheckedAsync(TarBinary(), ["-xjf", tarPath, "-C", destDir], ct: ct);
}

public static class Downloader
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    static Downloader() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Verbatim");

    public static async Task DownloadWithRetryAsync(
        string url, string dest, Action<long, long>? report, CancellationToken ct, int attempts = 4)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadAsync(url, dest, report, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                last = e;
                await Task.Delay(1500 * (i + 1), ct).ConfigureAwait(false);
            }
        }
        throw last!;
    }

    /// <summary>Single attempt. Resumes an existing .part via Range and verifies
    /// the final byte count against Content-Length before moving into place.</summary>
    private static async Task DownloadAsync(string url, string dest, Action<long, long>? report, CancellationToken ct)
    {
        var part = dest + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        long start = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (start > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, null);

        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(part); // stale/oversized partial — restart cleanly next attempt
            throw new IOException("Stale partial download discarded");
        }
        res.EnsureSuccessStatusCode();
        bool resumed = res.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) start = 0; // server ignored the Range: restart the file

        long total = start + (res.Content.Headers.ContentLength ?? 0);
        long received = start;

        await using (var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                report?.Invoke(received, total);
            }
        }

        var size = new FileInfo(part).Length;
        if (total > 0 && size != total)
        {
            File.Delete(part); // clean end but wrong size — do not trust it
            throw new IOException($"Incomplete download ({size} of {total} bytes)");
        }
        for (int i = 0; ; i++)
        {
            try
            {
                File.Move(part, dest, overwrite: true);
                return;
            }
            catch (IOException) when (i < 4) { await Task.Delay(250 * (i + 1), ct).ConfigureAwait(false); }
        }
    }
}
