using System.IO.Compression;
using Verbatim.Core;
using Xunit;

namespace Verbatim.Core.Tests;

public class DocxExporterTests
{
    private static Project Sample() => new()
    {
        Title = "Q2 <Review> & Plan",
        CreatedAt = "2026-07-10T20:00:00Z",
        Speakers = new()
        {
            ["speaker_00"] = new SpeakerInfo { Name = "Sarah", Color = "#4cc2ff" },
            ["speaker_01"] = new SpeakerInfo { Name = "David", Color = "#ff9d6c" }
        },
        Segments =
        [
            new TranscriptSegment { Start = 0, End = 5, Speaker = "speaker_00", Text = "Revenue was up 12% & climbing." },
            new TranscriptSegment { Start = 65, End = 70, Speaker = "speaker_01", Text = "Better <than> expected." }
        ]
    };

    [Fact]
    public void Build_ProducesValidZipWithExpectedParts()
    {
        var bytes = DocxExporter.Build(Sample());
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("_rels/.rels"));
        Assert.NotNull(zip.GetEntry("word/document.xml"));
    }

    [Fact]
    public void Build_DocumentXml_EscapesAndContainsContent()
    {
        var bytes = DocxExporter.Build(Sample());
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = r.ReadToEnd();

        Assert.Contains("Q2 &lt;Review&gt; &amp; Plan", xml);
        Assert.Contains("Revenue was up 12% &amp; climbing.", xml);
        Assert.Contains("Better &lt;than&gt; expected.", xml);
        Assert.Contains(">Sarah:  <", xml);
        Assert.Contains("w:val=\"4CC2FF\"", xml);
        Assert.Contains("[1:05]", xml); // second segment timestamp
        // well-formed XML
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
    }
}

public class LibrarySearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"verbatim-search-{Guid.NewGuid():N}");
    private readonly ProjectStore _store;

    public LibrarySearchTests()
    {
        _store = new ProjectStore(_dir);
        _store.Save(new Project
        {
            Title = "Board meeting",
            Speakers = new() { ["speaker_00"] = new SpeakerInfo { Name = "Sarah" } },
            Segments = [new TranscriptSegment { Start = 0, End = 9, Speaker = "speaker_00", Text = "The Calgary office sent final figures late Tuesday." }]
        });
        _store.Save(new Project
        {
            Title = "Podcast notes",
            Speakers = new() { ["speaker_00"] = new SpeakerInfo { Name = "Host" } },
            Segments = [new TranscriptSegment { Start = 0, End = 9, Speaker = "speaker_00", Text = "Welcome back to the show." }]
        });
    }

    [Fact]
    public void Search_MatchesTranscriptText_WithSnippetAroundHit()
    {
        var hits = _store.Search("calgary");
        Assert.Single(hits);
        Assert.Equal("Board meeting", hits[0].Title);
        Assert.Contains("Calgary", hits[0].Snippet);
    }

    [Fact]
    public void Search_MatchesTitleAndSpeaker()
    {
        Assert.Single(_store.Search("podcast"));
        Assert.Single(_store.Search("sarah"));
        Assert.Empty(_store.Search("zebra"));
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsFullList()
    {
        Assert.Equal(2, _store.Search("  ").Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
