using Verbatim.Core;

namespace Verbatim.Core.Tests;

public class ExportersTests
{
    private static Project MakeProject() => new()
    {
        Title = "Weekly Call",
        CreatedAt = "2026-07-10T16:38:00",
        Speakers = new()
        {
            ["speaker_00"] = new SpeakerInfo { Name = "Alice" },
            ["speaker_01"] = new SpeakerInfo { Name = "Bob" },
        },
        Segments =
        [
            new TranscriptSegment { Start = 3.12, End = 7.81, Speaker = "speaker_00", Text = "Good morning everyone, thanks for joining the call." },
            new TranscriptSegment { Start = 8.15, End = 12.4, Speaker = "speaker_01", Text = "Happy to be here." },
            new TranscriptSegment { Start = 3661.5, End = 3665.0, Speaker = "speaker_99", Text = "Line one.\nLine   two." },
        ],
    };

    // --- TXT -------------------------------------------------------------------

    [Fact]
    public void Txt_HeaderTitleAndAttributionLine()
    {
        var txt = Exporters.BuildTxt(MakeProject());
        var lines = txt.Split("\r\n");

        Assert.Equal("Weekly Call", lines[0]);
        Assert.Equal("Transcribed by Verbatim — Jul 10, 2026, 4:38 PM", lines[1]);
        Assert.Equal("", lines[2]);
    }

    [Fact]
    public void Txt_SegmentLinesWithTimestampsAndSpeakerNames()
    {
        var txt = Exporters.BuildTxt(MakeProject());
        var lines = txt.Split("\r\n");

        Assert.Equal("[0:03] Alice: Good morning everyone, thanks for joining the call.", lines[3]);
        Assert.Equal("[0:08] Bob: Happy to be here.", lines[4]);
        // hour-long timestamp uses H:MM:SS; unknown speaker key falls back to the raw key
        Assert.StartsWith("[1:01:01] speaker_99:", lines[5]);
    }

    [Fact]
    public void Txt_UsesCrlfLineEndingsAndTrailingNewline()
    {
        var txt = Exporters.BuildTxt(MakeProject());
        Assert.Contains("\r\n", txt);
        Assert.DoesNotContain("\n", txt.Replace("\r\n", "")); // no bare LFs outside CRLF pairs
        Assert.EndsWith("\r\n", txt);
    }

    [Fact]
    public void Txt_UnparseableCreatedAt_FallsBackWithoutThrowing()
    {
        var p = MakeProject();
        p.CreatedAt = "not a date";
        var txt = Exporters.BuildTxt(p);
        Assert.Contains("Transcribed by Verbatim — ", txt);
    }

    // --- SRT -------------------------------------------------------------------

    [Fact]
    public void Srt_NumberedCuesWithCommaTimestamps()
    {
        var srt = Exporters.BuildSrt(MakeProject());
        var blocks = srt.Split("\r\n\r\n");

        Assert.Equal(3, blocks.Length);
        Assert.StartsWith("1\r\n00:00:03,120 --> 00:00:07,810\r\nAlice: Good morning", blocks[0]);
        Assert.StartsWith("2\r\n00:00:08,150 --> 00:00:12,400\r\nBob: Happy to be here.", blocks[1]);
        Assert.StartsWith("3\r\n01:01:01,500 --> 01:01:05,000\r\nspeaker_99:", blocks[2]);
    }

    [Fact]
    public void Srt_CollapsesTextToOneLine()
    {
        var srt = Exporters.BuildSrt(MakeProject());
        Assert.Contains("speaker_99: Line one. Line two.", srt);
    }

    [Fact]
    public void Srt_UsesCrlfLineEndings()
    {
        var srt = Exporters.BuildSrt(MakeProject());
        Assert.Contains("\r\n", srt);
        Assert.DoesNotContain("\n", srt.Replace("\r\n", ""));
    }

    // --- VTT -------------------------------------------------------------------

    [Fact]
    public void Vtt_HeaderAndDotTimestamps()
    {
        var vtt = Exporters.BuildVtt(MakeProject());

        Assert.StartsWith("WEBVTT\n\n", vtt);
        Assert.Contains("00:00:03.120 --> 00:00:07.810\n<v Alice>Good morning", vtt);
        Assert.Contains("01:01:01.500 --> 01:01:05.000\n<v speaker_99>Line one. Line two.", vtt);
    }

    [Fact]
    public void Vtt_UsesLfOnly()
    {
        var vtt = Exporters.BuildVtt(MakeProject());
        Assert.DoesNotContain("\r", vtt);
        Assert.Contains("\n", vtt);
    }

    [Fact]
    public void Vtt_EscapesMarkupInSpeakerAndText()
    {
        var p = MakeProject();
        p.Speakers["speaker_00"] = new SpeakerInfo { Name = "Q&A <review>" };
        p.Segments = [new TranscriptSegment { Start = 0, End = 1, Speaker = "speaker_00", Text = "Q&A <review>  session" }];

        var vtt = Exporters.BuildVtt(p);

        Assert.Contains("<v Q&amp;A &lt;review&gt;>Q&amp;A &lt;review&gt; session", vtt);
        Assert.DoesNotContain("<review>", vtt);
    }

    [Fact]
    public void EmptyProject_ProducesHeadersOnly()
    {
        var p = MakeProject();
        p.Segments = [];

        Assert.Equal("WEBVTT\n\n", Exporters.BuildVtt(p));
        Assert.Equal("", Exporters.BuildSrt(p));
        var txt = Exporters.BuildTxt(p);
        Assert.Equal("Weekly Call\r\nTranscribed by Verbatim — Jul 10, 2026, 4:38 PM\r\n\r\n", txt);
    }
}
