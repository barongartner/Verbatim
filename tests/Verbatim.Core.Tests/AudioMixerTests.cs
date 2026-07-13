using Verbatim.Core;
using Xunit;

namespace Verbatim.Core.Tests;

public class AudioMixerTests
{
    [Fact]
    public void ToPcm16_MapsFullScaleAndClamps()
    {
        Assert.Equal(0, AudioMixer.ToPcm16(0f));
        Assert.Equal(32767, AudioMixer.ToPcm16(1f));
        Assert.Equal(-32768, AudioMixer.ToPcm16(-1f));
        // out-of-range sums (two loud sources) clamp instead of wrapping
        Assert.Equal(32767, AudioMixer.ToPcm16(1.9f));
        Assert.Equal(-32768, AudioMixer.ToPcm16(-1.9f));
    }

    [Fact]
    public void Mix_SumsTwoSourcesSampleForSample()
    {
        var a = new float[] { 0.25f, -0.25f, 0f };
        var b = new float[] { 0.25f, 0.25f, 0.5f };
        var pcm = AudioMixer.Mix(a, b);

        Assert.Equal(3, pcm.Length);
        Assert.Equal(AudioMixer.ToPcm16(0.5f), pcm[0]);
        Assert.Equal(0, pcm[1]);              // 0.25 + -0.25
        Assert.Equal(AudioMixer.ToPcm16(0.5f), pcm[2]);
    }

    [Fact]
    public void Mix_ResultIsAsLongAsLongestSource_ShorterTreatedAsSilence()
    {
        var mic = new float[] { 0.5f };                  // stopped talking early
        var system = new float[] { 0.1f, 0.2f, 0.3f };   // meeting kept going
        var pcm = AudioMixer.Mix(mic, system);

        Assert.Equal(3, pcm.Length);
        Assert.Equal(AudioMixer.ToPcm16(0.6f), pcm[0]);  // both present
        Assert.Equal(AudioMixer.ToPcm16(0.2f), pcm[1]);  // mic ran out -> +0
        Assert.Equal(AudioMixer.ToPcm16(0.3f), pcm[2]);
    }

    [Fact]
    public void Mix_SingleSource_PassesThrough()
    {
        var only = new float[] { 0f, 0.5f, -0.5f };
        var pcm = AudioMixer.Mix(only, null);

        Assert.Equal(3, pcm.Length);
        Assert.Equal(0, pcm[0]);
        Assert.Equal(AudioMixer.ToPcm16(0.5f), pcm[1]);
        Assert.Equal(AudioMixer.ToPcm16(-0.5f), pcm[2]);
    }

    [Fact]
    public void Mix_NoSamples_ReturnsEmpty()
    {
        Assert.Empty(AudioMixer.Mix(null, null));
        Assert.Empty(AudioMixer.Mix(Array.Empty<float>()));
    }
}
