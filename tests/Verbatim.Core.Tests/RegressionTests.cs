using Verbatim.Core;
using Xunit;

namespace Verbatim.Core.Tests;

/// <summary>Guards for bugs fixed alongside the recording feature.</summary>
public class RegressionTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("[ BLANK_AUDIO ]")]
    [InlineData("(silence)")]
    [InlineData("[Music]")]
    public void IsNoiseMarker_FiltersWhisperBlankAndNoiseTokens(string text)
    {
        Assert.True(SegmentShaper.IsNoiseMarker(text));
    }

    [Fact]
    public void IsNoiseMarker_KeepsRealDialogue()
    {
        Assert.False(SegmentShaper.IsNoiseMarker("Let's talk about the blank form we sent."));
    }

    [Fact]
    public void Shape_ContainedSameSpeakerSpan_DoesNotTruncateAudio()
    {
        // A later span fully inside the previous one must not pull End backwards,
        // which would drop everything between the inner span's end and the real end.
        var raw = new List<SherpaOutput.DiarSegment>
        {
            new(0, 10, "speaker_00"),
            new(1, 2, "speaker_00"),
        };
        var shaped = SegmentShaper.Shape(raw, 10);

        Assert.Single(shaped);
        Assert.Equal(0, shaped[0].Start);
        Assert.Equal(10, shaped[0].End); // not 2
    }

    [Fact]
    public void ParseProgress_MalformedNumber_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(SherpaOutput.ParseProgress("progress 1.2.3%"));
        Assert.Equal(12.9, SherpaOutput.ParseProgress("progress 12.90%"));
    }

    [Fact]
    public void BuildSrt_TimestampNearWholeSecond_CarriesInsteadOfEmitting1000Ms()
    {
        var p = new Project
        {
            Title = "t",
            CreatedAt = "2026-07-12T00:00:00Z",
            Segments = [new TranscriptSegment { Start = 12.9997, End = 13.5, Speaker = "speaker_00", Text = "hi" }],
            Speakers = new() { ["speaker_00"] = new SpeakerInfo { Name = "A" } },
        };
        var srt = Exporters.BuildSrt(p);

        Assert.Contains("00:00:13,000 -->", srt); // carried from 12.9997, never "12,1000"
        Assert.DoesNotContain(",1000", srt);
    }
}
