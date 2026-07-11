namespace Verbatim.Core;

/// <summary>
/// Minimal 16-bit PCM mono WAV writer used to hand audio to the sherpa-onnx CLIs.
/// Port of src/main/wav.js.
/// </summary>
public static class WavWriter
{
    /// <summary>Writes samples [startSample, endSample) of <paramref name="pcm"/> to a WAV file.</summary>
    public static void WriteSlice(string filePath, short[] pcm, int sampleRate, int startSample, int endSample)
    {
        int s = Math.Max(0, Math.Min(startSample, pcm.Length));
        int e = Math.Max(s, Math.Min(endSample, pcm.Length));
        int numSamples = e - s;
        int dataBytes = numSamples * 2;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        // 44-byte RIFF header (little-endian; BinaryWriter is little-endian).
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                        // fmt chunk size
        w.Write((short)1);                  // PCM
        w.Write((short)1);                  // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);            // byte rate
        w.Write((short)2);                  // block align
        w.Write((short)16);                 // bits per sample
        w.Write("data"u8);
        w.Write(dataBytes);

        for (int i = s; i < e; i++)
            w.Write(pcm[i]);
    }
}
