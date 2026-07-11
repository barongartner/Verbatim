using System.IO.Compression;
using System.Security;
using System.Text;

namespace Verbatim.Core;

/// <summary>
/// Minimal Word (.docx) writer — a docx is just a zip of XML, so no library
/// is needed. Word/LibreOffice/Google Docs all accept this minimal package.
/// Layout: title, attribution line, then one paragraph per segment with a
/// gray timestamp, the speaker name bolded in their transcript color, and
/// the text.
/// </summary>
public static class DocxExporter
{
    public static byte[] Build(Project p)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            Add(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            Add(zip, "word/document.xml", BuildDocumentXml(p));
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content.ReplaceLineEndings("\n"));
    }

    private static string BuildDocumentXml(Project p)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>""");

        // title + attribution
        sb.Append("<w:p><w:r><w:rPr><w:b/><w:sz w:val=\"40\"/></w:rPr><w:t xml:space=\"preserve\">")
          .Append(Esc(p.Title)).Append("</w:t></w:r></w:p>");
        sb.Append("<w:p><w:r><w:rPr><w:color w:val=\"8B95A5\"/></w:rPr><w:t xml:space=\"preserve\">")
          .Append(Esc($"Transcribed by Verbatim — {p.CreatedAt?.Split('T')[0] ?? ""}"))
          .Append("</w:t></w:r></w:p><w:p/>");

        foreach (var s in p.Segments)
        {
            var color = (p.Speakers.TryGetValue(s.Speaker, out var sp) ? sp.Color : "#8b95a5")
                .TrimStart('#').ToUpperInvariant();
            sb.Append("<w:p>");
            sb.Append("<w:r><w:rPr><w:color w:val=\"8B95A5\"/><w:sz w:val=\"18\"/></w:rPr>")
              .Append("<w:t xml:space=\"preserve\">[").Append(Exporters.FmtTime(s.Start)).Append("]  </w:t></w:r>");
            sb.Append("<w:r><w:rPr><w:b/><w:color w:val=\"").Append(Esc(color)).Append("\"/></w:rPr>")
              .Append("<w:t xml:space=\"preserve\">").Append(Esc(p.SpeakerName(s.Speaker))).Append(":  </w:t></w:r>");
            sb.Append("<w:r><w:t xml:space=\"preserve\">").Append(Esc(s.Text)).Append("</w:t></w:r>");
            sb.Append("</w:p>");
        }

        sb.Append("</w:body></w:document>");
        return sb.ToString();
    }

    private static string Esc(string s) => SecurityElement.Escape(s);
}
