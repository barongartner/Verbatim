using Verbatim.Core;

namespace Verbatim.Core.Tests;

public class SherpaOutputTests
{
    // --- ParseDiarization ---------------------------------------------------

    [Fact]
    public void ParseDiarization_RealFixture_IgnoresNoiseLines()
    {
        var stdout = string.Join('\n',
            "OfflineSpeakerDiarizationConfig(segmentation=OfflineSpeakerSegmentationModelConfig(...))",
            "Started",
            "0.031 -- 7.810 speaker_00",
            "8.152 -- 12.400 speaker_01",
            "");

        var segs = SherpaOutput.ParseDiarization(stdout);

        Assert.Equal(2, segs.Count);
        Assert.Equal(0.031, segs[0].Start);
        Assert.Equal(7.810, segs[0].End);
        Assert.Equal("speaker_00", segs[0].Speaker);
        Assert.Equal("speaker_01", segs[1].Speaker);
    }

    [Fact]
    public void ParseDiarization_SortsByStart()
    {
        var stdout = "10.5 -- 12.0 speaker_01\n0.5 -- 3.0 speaker_00\n4.0 -- 9.0 speaker_00\n";
        var segs = SherpaOutput.ParseDiarization(stdout);

        Assert.Equal(3, segs.Count);
        Assert.Equal(new[] { 0.5, 4.0, 10.5 }, segs.Select(s => s.Start));
    }

    [Fact]
    public void ParseDiarization_AcceptsSpeakerVariantsAndLeadingWhitespace()
    {
        var stdout = "  1.0 -- 2.0 SPEAKER_03\n3.0--4.0 speaker 12\n5 -- 6 speaker7\n";
        var segs = SherpaOutput.ParseDiarization(stdout);

        Assert.Equal(3, segs.Count);
        Assert.Equal("SPEAKER_03", segs[0].Speaker);
        Assert.Equal("speaker 12", segs[1].Speaker);
        Assert.Equal("speaker7", segs[2].Speaker);
        Assert.Equal(5.0, segs[2].Start);
    }

    [Fact]
    public void ParseDiarization_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(SherpaOutput.ParseDiarization(""));
        Assert.Empty(SherpaOutput.ParseDiarization("Started\nDone\n"));
    }

    // --- ParseAsrLines --------------------------------------------------------

    [Fact]
    public void ParseAsrLines_RealFixtureLine()
    {
        var stdout =
            """{"lang": "en", "emotion": "", "event": "", "text": "Good morning everyone, thanks for joining the call.", "timestamps": [], "durations": [], "tokens":[" Good"], "ys_log_probs": [], "words": []}""" +
            "\n";

        var results = SherpaOutput.ParseAsrLines(stdout);

        var r = Assert.Single(results);
        Assert.Equal("Good morning everyone, thanks for joining the call.", r.Text);
        Assert.Equal("en", r.Lang);
    }

    [Fact]
    public void ParseAsrLines_IgnoresNonJsonLines()
    {
        var stdout = string.Join('\n',
            "sherpa-onnx-offline --whisper-encoder=...",
            "Started",
            """{"text": " hello ", "lang": "en"}""",
            "Done! elapsed seconds: 1.2");

        var results = SherpaOutput.ParseAsrLines(stdout);

        var r = Assert.Single(results);
        Assert.Equal("hello", r.Text); // trimmed
    }

    [Fact]
    public void ParseAsrLines_MissingLang_YieldsEmptyLang()
    {
        var results = SherpaOutput.ParseAsrLines("""{"text": "no language here"}""");
        var r = Assert.Single(results);
        Assert.Equal("no language here", r.Text);
        Assert.Equal("", r.Lang);
    }

    [Fact]
    public void ParseAsrLines_MalformedJson_FallsBackToRegex()
    {
        // Trailing garbage makes JSON parsing fail; the "text" field is still recoverable.
        var stdout = """{"lang": "en", "text": "She said \"hi\" twice", "tokens": [,]}""";

        var results = SherpaOutput.ParseAsrLines(stdout);

        var r = Assert.Single(results);
        Assert.Equal("She said \"hi\" twice", r.Text); // JSON-unescaped
        Assert.Equal("", r.Lang);                      // regex fallback loses lang
    }

    [Fact]
    public void ParseAsrLines_MultipleLines_PreserveOrder()
    {
        var stdout = string.Join('\n',
            """{"text": "first", "lang": "en"}""",
            """{"text": "second", "lang": "de"}""");

        var results = SherpaOutput.ParseAsrLines(stdout);

        Assert.Equal(2, results.Count);
        Assert.Equal("first", results[0].Text);
        Assert.Equal("de", results[1].Lang);
    }

    [Fact]
    public void ParseAsrLines_UnrecoverableLine_IsSkipped()
    {
        Assert.Empty(SherpaOutput.ParseAsrLines("{not json at all"));
    }

    // --- ParseProgress --------------------------------------------------------

    [Theory]
    [InlineData("progress 12.90%", 12.90)]
    [InlineData("  num_speakers: 2, progress 100%", 100.0)]
    [InlineData("PROGRESS 5.5% done", 5.5)]
    public void ParseProgress_MatchesPercentage(string line, double expected)
    {
        Assert.Equal(expected, SherpaOutput.ParseProgress(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Started")]
    [InlineData("progress unknown")]
    public void ParseProgress_NoMatch_ReturnsNull(string line)
    {
        Assert.Null(SherpaOutput.ParseProgress(line));
    }
}
