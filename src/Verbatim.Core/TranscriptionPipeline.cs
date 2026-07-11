namespace Verbatim.Core;

public class PipelineOptions
{
    /// <summary>Directory containing the sherpa-onnx executables.</summary>
    public required string BinDir { get; init; }
    public required string SegmentationModel { get; init; }
    public required string EmbeddingModel { get; init; }
    public required string WhisperEncoder { get; init; }
    public required string WhisperDecoder { get; init; }
    public required string WhisperTokens { get; init; }
    public required string TempDir { get; init; }
    public string Language { get; init; } = "";
    public int NumSpeakers { get; init; }
    public int Threads { get; init; } = DefaultThreads();

    public static int DefaultThreads() =>
        Math.Min(6, Math.Max(2, Environment.ProcessorCount - 2));
}

public class PipelineResult
{
    public List<TranscriptSegment> Segments { get; init; } = new();
    public string Language { get; init; } = "";
    public double DurationSec { get; init; }
}

/// <summary>
/// Diarize-then-transcribe over the sherpa-onnx CLIs: who spoke when
/// (pyannote + speaker embeddings), then whisper per speaker chunk, batched so
/// the model loads once per spawn.
/// </summary>
public static class TranscriptionPipeline
{
    public const int SampleRate = 16000;
    private const int AsrBatch = 8;

    private static string Exe(string binDir, string name) =>
        Path.Combine(binDir, OperatingSystem.IsWindows() ? name + ".exe" : name);

    public static async Task<PipelineResult> TranscribeAsync(
        short[] pcm,
        PipelineOptions opt,
        IProgress<StageProgress>? progress = null,
        CancellationToken ct = default)
    {
        double totalSec = (double)pcm.Length / SampleRate;
        Directory.CreateDirectory(opt.TempDir);

        progress?.Report(new("prepare", 0, "Preparing audio"));
        var masterWav = Path.Combine(opt.TempDir, "audio.wav");
        WavWriter.WriteSlice(masterWav, pcm, SampleRate, 0, pcm.Length);
        ct.ThrowIfCancellationRequested();

        // --- who spoke when -------------------------------------------------
        progress?.Report(new("diarize", 0, "Identifying speakers"));
        var diarArgs = new List<string>
        {
            $"--segmentation.pyannote-model={opt.SegmentationModel}",
            $"--embedding.model={opt.EmbeddingModel}",
            $"--segmentation.num-threads={opt.Threads}",
            $"--embedding.num-threads={opt.Threads}",
            opt.NumSpeakers > 0
                ? $"--clustering.num-clusters={opt.NumSpeakers}"
                : "--clustering.cluster-threshold=0.5",
            masterWav
        };
        var diarOut = await ProcessRunner.RunCheckedAsync(
            Exe(opt.BinDir, "sherpa-onnx-offline-speaker-diarization"), diarArgs,
            onStderrLine: line =>
            {
                var pct = SherpaOutput.ParseProgress(line);
                if (pct is not null) progress?.Report(new("diarize", pct.Value, "Identifying speakers"));
            },
            ct: ct).ConfigureAwait(false);

        var shaped = SegmentShaper.Shape(SherpaOutput.ParseDiarization(diarOut), totalSec);

        // --- what was said --------------------------------------------------
        progress?.Report(new("transcribe", 0, "Transcribing"));
        var chunkFiles = new List<string>(shaped.Count);
        for (int i = 0; i < shaped.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(opt.TempDir, $"chunk_{i:D4}.wav");
            WavWriter.WriteSlice(file, pcm, SampleRate,
                (int)Math.Floor(shaped[i].Start * SampleRate),
                (int)Math.Ceiling(shaped[i].End * SampleRate));
            chunkFiles.Add(file);
        }

        var rows = new List<TranscriptSegment>();
        var langCounts = new Dictionary<string, int>();
        for (int i = 0; i < shaped.Count; i += AsrBatch)
        {
            ct.ThrowIfCancellationRequested();
            var batch = shaped.Skip(i).Take(AsrBatch).ToList();
            var asrArgs = new List<string>
            {
                $"--whisper-encoder={opt.WhisperEncoder}",
                $"--whisper-decoder={opt.WhisperDecoder}",
                $"--tokens={opt.WhisperTokens}",
                $"--num-threads={opt.Threads}"
            };
            if (!string.IsNullOrEmpty(opt.Language)) asrArgs.Add($"--whisper-language={opt.Language}");
            asrArgs.AddRange(chunkFiles.Skip(i).Take(AsrBatch));

            var asrOut = await ProcessRunner.RunCheckedAsync(
                Exe(opt.BinDir, "sherpa-onnx-offline"), asrArgs, ct: ct).ConfigureAwait(false);
            var results = SherpaOutput.ParseAsrLines(asrOut);
            if (results.Count != batch.Count)
            {
                throw new EngineException(
                    $"Transcriber returned {results.Count} results for {batch.Count} clips", asrOut);
            }
            for (int j = 0; j < batch.Count; j++)
            {
                var text = results[j].Text;
                if (SegmentShaper.IsNoiseMarker(text)) continue;
                rows.Add(new TranscriptSegment
                {
                    Start = Math.Round(batch[j].Start, 2),
                    End = Math.Round(batch[j].End, 2),
                    Speaker = batch[j].Speaker,
                    Text = text
                });
                if (!string.IsNullOrEmpty(results[j].Lang))
                {
                    langCounts[results[j].Lang] = langCounts.GetValueOrDefault(results[j].Lang) + 1;
                }
            }
            progress?.Report(new("transcribe",
                Math.Min(100.0, (i + batch.Count) * 100.0 / shaped.Count),
                $"Transcribing {Math.Min(i + batch.Count, shaped.Count)} of {shaped.Count} sections"));
        }

        var language = langCounts.Count > 0
            ? langCounts.OrderByDescending(kv => kv.Value).First().Key
            : opt.Language;
        progress?.Report(new("done", 100, "Done"));
        return new PipelineResult
        {
            Segments = rows,
            Language = language,
            DurationSec = Math.Round(totalSec, 2)
        };
    }
}
