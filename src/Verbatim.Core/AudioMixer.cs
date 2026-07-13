namespace Verbatim.Core;

/// <summary>
/// Combines recorded audio sources into the 16 kHz mono 16-bit PCM the
/// transcription pipeline consumes. Mixing the microphone with the computer's
/// own output (for meetings) happens here, on plain float buffers, so it is
/// fully unit-testable without any audio hardware.
/// </summary>
public static class AudioMixer
{
    /// <summary>Clamp a float sample to [-1,1] and convert to 16-bit PCM, using
    /// the same asymmetric full-scale mapping as <see cref="Verbatim.Core"/>'s
    /// audio decode path so recorded and imported audio quantize identically.</summary>
    public static short ToPcm16(float v)
    {
        v = Math.Clamp(v, -1f, 1f);
        return (short)(v < 0 ? v * 32768 : v * 32767);
    }

    /// <summary>
    /// Sums any number of equally-sampled mono float sources (16 kHz) into one
    /// clamped 16-bit PCM buffer. Sources may differ in length — the result runs
    /// as long as the longest, treating shorter sources as silence past their end
    /// (so someone who unmutes their mic partway through a meeting still gets a
    /// full-length recording that stays aligned with the computer audio).
    /// Null sources are ignored.
    /// </summary>
    public static short[] Mix(params IReadOnlyList<float>?[] sources)
    {
        int n = 0;
        foreach (var s in sources)
            if (s is not null && s.Count > n) n = s.Count;

        var outp = new short[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0f;
            foreach (var s in sources)
                if (s is not null && i < s.Count) sum += s[i];
            outp[i] = ToPcm16(sum);
        }
        return outp;
    }
}
