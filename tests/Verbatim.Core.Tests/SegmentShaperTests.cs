using Verbatim.Core;

namespace Verbatim.Core.Tests;

public class SegmentShaperTests
{
    private static SherpaOutput.DiarSegment Seg(double start, double end, string speaker = "speaker_00") =>
        new(start, end, speaker);

    // --- Shape ---------------------------------------------------------------

    [Fact]
    public void DropsSegmentsShorterThanMinimum()
    {
        var shaped = SegmentShaper.Shape(
            [Seg(0, 0.1), Seg(1, 1.14), Seg(2, 5)], totalSec: 10);

        var s = Assert.Single(shaped);
        Assert.Equal(2, s.Start);
        Assert.Equal(5, s.End);
    }

    [Fact]
    public void EmptyInput_ProducesSingleFullSpanSegment()
    {
        var shaped = SegmentShaper.Shape([], totalSec: 12.5);

        var s = Assert.Single(shaped);
        Assert.Equal(0, s.Start);
        Assert.Equal(12.5, s.End);
        Assert.Equal("speaker_00", s.Speaker);
        Assert.Equal("", s.Text);
    }

    [Fact]
    public void AllTooShort_AlsoProducesFullSpanFallback()
    {
        var shaped = SegmentShaper.Shape([Seg(1, 1.05), Seg(2, 2.1)], totalSec: 20);

        var s = Assert.Single(shaped);
        Assert.Equal(0, s.Start);
        Assert.Equal(20, s.End);
    }

    [Fact]
    public void MergesSameSpeakerAcrossShortGap()
    {
        var shaped = SegmentShaper.Shape(
            [Seg(0, 5), Seg(5.5, 10)], totalSec: 10); // gap 0.5 <= 0.75

        var s = Assert.Single(shaped);
        Assert.Equal(0, s.Start);
        Assert.Equal(10, s.End);
    }

    [Fact]
    public void DoesNotMergeAcrossLongGap()
    {
        var shaped = SegmentShaper.Shape(
            [Seg(0, 5), Seg(6, 10)], totalSec: 10); // gap 1.0 > 0.75

        Assert.Equal(2, shaped.Count);
    }

    [Fact]
    public void DoesNotMergeDifferentSpeakers()
    {
        var shaped = SegmentShaper.Shape(
            [Seg(0, 5, "speaker_00"), Seg(5.2, 10, "speaker_01")], totalSec: 10);

        Assert.Equal(2, shaped.Count);
        Assert.Equal("speaker_00", shaped[0].Speaker);
        Assert.Equal("speaker_01", shaped[1].Speaker);
    }

    [Fact]
    public void DoesNotMergeWhenResultWouldExceedMaxChunk()
    {
        // merged span would be 30.5s > 28s
        var shaped = SegmentShaper.Shape(
            [Seg(0, 15), Seg(15.5, 30.5)], totalSec: 31);

        Assert.Equal(2, shaped.Count);
        Assert.Equal(15, shaped[0].End);
    }

    [Fact]
    public void MergesChainOfShortSameSpeakerRuns()
    {
        var shaped = SegmentShaper.Shape(
            [Seg(0, 3), Seg(3.5, 7), Seg(7.5, 11)], totalSec: 11);

        var s = Assert.Single(shaped);
        Assert.Equal(0, s.Start);
        Assert.Equal(11, s.End);
    }

    [Fact]
    public void Splits90SecondSegmentIntoFourEqualParts()
    {
        var shaped = SegmentShaper.Shape([Seg(10, 100)], totalSec: 100); // 90s

        Assert.Equal(4, shaped.Count); // ceil(90 / 28) = 4
        double step = 90.0 / 4;        // 22.5s each, <= 28
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(10 + i * step, shaped[i].Start, precision: 9);
            Assert.Equal(10 + (i + 1) * step, shaped[i].End, precision: 9);
            Assert.True(shaped[i].End - shaped[i].Start <= SegmentShaper.MaxChunkSec);
            Assert.Equal("speaker_00", shaped[i].Speaker);
        }
        // parts are contiguous
        for (int i = 1; i < 4; i++)
            Assert.Equal(shaped[i - 1].End, shaped[i].Start, precision: 9);
    }

    [Fact]
    public void SegmentAtExactlyMaxChunkIsNotSplit()
    {
        var shaped = SegmentShaper.Shape([Seg(0, 28)], totalSec: 28);
        Assert.Single(shaped);
    }

    [Fact]
    public void EmptyInputFallbackLongerThanMaxChunk_IsSplit()
    {
        var shaped = SegmentShaper.Shape([], totalSec: 60);

        Assert.Equal(3, shaped.Count); // ceil(60 / 28) = 3, 20s each
        Assert.Equal(0, shaped[0].Start);
        Assert.Equal(60, shaped[^1].End, precision: 9);
        Assert.All(shaped, s => Assert.True(s.End - s.Start <= SegmentShaper.MaxChunkSec));
    }

    // --- IsNoiseMarker ---------------------------------------------------------

    [Theory]
    [InlineData("[Music]")]
    [InlineData("(applause)")]
    [InlineData("[ Silence ]")]
    [InlineData("(laughter)")]
    [InlineData("[inaudible]")]
    [InlineData("[BACKGROUND NOISE]")]
    [InlineData("♪♪")]
    [InlineData("♪ ♫ ♬")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" .,- .")]
    public void IsNoiseMarker_True(string text)
    {
        Assert.True(SegmentShaper.IsNoiseMarker(text));
    }

    [Theory]
    [InlineData("[Music] hello")]
    [InlineData("He said [music] rocks")]
    [InlineData("Normal.")]
    [InlineData("(unrelated bracket text)")]
    [InlineData("♪ lyrics with words ♪")]
    public void IsNoiseMarker_False(string text)
    {
        Assert.False(SegmentShaper.IsNoiseMarker(text));
    }
}
