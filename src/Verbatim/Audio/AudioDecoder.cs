using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Verbatim.Core;

namespace Verbatim.Audio;

public record DecodedAudio(short[] Pcm16k, double DurationSec, double[] Peaks);

/// <summary>
/// Decodes any Media Foundation format (MP3, WAV, M4A, AAC, WMA, and FLAC on
/// Windows 10+) to 16 kHz mono PCM for the transcription engines, plus
/// waveform peaks for the player.
/// </summary>
public static class AudioDecoder
{
    public const int TargetRate = TranscriptionPipeline.SampleRate;
    private const int PeakBuckets = 1500;

    public static DecodedAudio Decode(string path, CancellationToken ct = default)
    {
        using var reader = OpenReader(path);
        var mono = reader.ToSampleProvider().ToMono();
        var resampled = new WdlResamplingSampleProvider(mono, TargetRate);

        var samples = new List<float>(1 << 20);
        var buffer = new float[TargetRate]; // 1s at a time
        int n;
        while ((n = resampled.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < n; i++) samples.Add(buffer[i]);
        }

        var pcm = new short[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var v = Math.Clamp(samples[i], -1f, 1f);
            pcm[i] = (short)(v < 0 ? v * 32768 : v * 32767);
        }
        return new DecodedAudio(pcm, (double)pcm.Length / TargetRate, ComputePeaks(samples));
    }

    private static WaveStream OpenReader(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".wav") return new WaveFileReader(path);
        try
        {
            return new MediaFoundationReader(path);
        }
        catch (Exception e)
        {
            throw new InvalidDataException(
                $"Windows could not decode this file ({ext}). MP3, WAV, M4A, AAC, WMA and FLAC are supported.", e);
        }
    }

    private static double[] ComputePeaks(List<float> samples)
    {
        int nBuckets = Math.Min(PeakBuckets, Math.Max(1, samples.Count));
        int per = Math.Max(1, samples.Count / nBuckets);
        var peaks = new double[nBuckets];
        for (int b = 0; b < nBuckets; b++)
        {
            float max = 0;
            int end = Math.Min(samples.Count, (b + 1) * per);
            for (int i = b * per; i < end; i += 4)
            {
                var a = Math.Abs(samples[i]);
                if (a > max) max = a;
            }
            peaks[b] = Math.Round(max, 3);
        }
        return peaks;
    }
}
