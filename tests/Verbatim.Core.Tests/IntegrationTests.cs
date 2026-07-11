using Verbatim.Core;
using Xunit;

namespace Verbatim.Core.Tests;

/// <summary>
/// End-to-end tests that spawn the real sherpa-onnx binaries (vendor/mac on
/// macOS) against the real downloaded models. They no-op quietly when the
/// binaries or models are absent (e.g. on Windows CI, which has no models),
/// so the fast unit tests still gate every build.
/// </summary>
public class IntegrationTests
{
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    private static string? BinDir()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var dir = Path.Combine(RepoRoot, "vendor", "mac", "bin");
        return File.Exists(Path.Combine(dir, "sherpa-onnx-offline")) ? dir : null;
    }

    private static string? ModelsDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Verbatim", "models");
        return Directory.Exists(dir) ? dir : null;
    }

    [Fact]
    public async Task FullPipeline_TwoSpeakerMeeting_ProducesAttributedTranscript()
    {
        var binDir = BinDir();
        var modelsDir = ModelsDir();
        var wavPath = Path.Combine(
            "/private/tmp/claude-501/-Users-barongartner-Desktop/b5a1f52d-e951-4f94-a144-535202cf77de/scratchpad",
            "testaudio", "meeting.wav");
        if (binDir is null || modelsDir is null || !File.Exists(wavPath)) return; // environment not available

        var mm = new ModelManager(modelsDir);
        if (!mm.WhisperReady("whisper-base") || !mm.DiarizationReady()) return;

        var bytes = await File.ReadAllBytesAsync(wavPath);
        var pcm = new short[(bytes.Length - 44) / 2];
        Buffer.BlockCopy(bytes, 44, pcm, 0, pcm.Length * 2);

        var (enc, dec, tok) = mm.WhisperPaths("whisper-base");
        var tempDir = Path.Combine(Path.GetTempPath(), $"verbatim-inttest-{Guid.NewGuid():N}");
        var progressStages = new List<string>();
        try
        {
            var result = await TranscriptionPipeline.TranscribeAsync(pcm, new PipelineOptions
            {
                BinDir = binDir,
                SegmentationModel = mm.SegmentationPath,
                EmbeddingModel = mm.EmbeddingPath,
                WhisperEncoder = enc,
                WhisperDecoder = dec,
                WhisperTokens = tok,
                TempDir = tempDir
            }, new Progress<StageProgress>(p => { lock (progressStages) progressStages.Add(p.Stage); }));

            Assert.InRange(result.Segments.Count, 4, 8);
            Assert.Equal(2, result.Segments.Select(s => s.Speaker).Distinct().Count());
            Assert.Contains(result.Segments, s => s.Text.Contains("Calgary", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Segments, s => s.Text.Contains("quarterly", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("en", result.Language);
            Assert.InRange(result.DurationSec, 39, 42);
            Assert.All(result.Segments, s => Assert.True(s.End > s.Start));
            lock (progressStages)
            {
                Assert.Contains("diarize", progressStages);
                Assert.Contains("transcribe", progressStages);
                Assert.Contains("done", progressStages);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task YtDlp_DirectMp3Url_ProbesAndFetches()
    {
        // reuse a pre-downloaded yt-dlp binary if present; otherwise skip (no
        // 35 MB download in unit test runs)
        var toolsDir = "/private/tmp/claude-501/-Users-barongartner-Desktop/b5a1f52d-e951-4f94-a144-535202cf77de/scratchpad/ytdlp-test";
        if (!OperatingSystem.IsMacOS() || !File.Exists(Path.Combine(toolsDir, "yt-dlp"))) return;

        var mediaDir = Path.Combine(Path.GetTempPath(), $"verbatim-media-{Guid.NewGuid():N}");
        var ytdlp = new YtDlp(toolsDir, mediaDir);
        try
        {
            var fetched = await ytdlp.FetchAsync(
                "https://www.nasa.gov/wp-content/uploads/2015/01/591240main_JFKmoonspeech.mp3",
                null, null, CancellationToken.None);
            Assert.True(File.Exists(fetched.FilePath));
            Assert.Equal("mp3", fetched.Ext);
            Assert.True(new FileInfo(fetched.FilePath).Length > 100_000);
        }
        finally
        {
            try { Directory.Delete(mediaDir, true); } catch { /* best effort */ }
        }
    }
}
