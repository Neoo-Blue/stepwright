using System.IO.Compression;
using System.Net;
using System.Text;
using Stepwright.Config;
using Stepwright.Model;
using Stepwright.Render;

namespace Stepwright.Export;

/// <summary>
/// Writes a Word document straight to the open packaging format, with no third party library.
/// Formatting is applied directly to each run so no style part is needed.
/// </summary>
public static class DocxExporter
{
    private const long EmuPerInch = 914400;
    private const long ContentWidthEmu = (long)(6.3 * EmuPerInch);

    public static void Export(Guide guide, AppSettings settings, string path, int maxImageWidth = 1400)
    {
        var images = new List<(string Name, byte[] Data, int Width, int Height)>();
        var body = new StringBuilder();

        body.Append(Paragraph(guide.Title, size: 40, bold: true, spaceAfter: 160));

        if (!string.IsNullOrWhiteSpace(guide.Summary))
        {
            body.Append(Paragraph(guide.Summary, size: 22, spaceAfter: 120));
        }

        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(guide.Author))
        {
            facts.Add("By " + guide.Author);
        }

        facts.Add(guide.Updated.ToString("d MMMM yyyy"));
        body.Append(Paragraph(string.Join("   ", facts), size: 18, color: "7A7F88", spaceAfter: 320));

        int number = 0;
        foreach (Step step in guide.Visible)
        {
            if (step.Kind == StepKind.Heading)
            {
                body.Append(Paragraph(step.Text, size: 28, bold: true, spaceBefore: 320, spaceAfter: 120));
                continue;
            }

            number++;
            body.Append(Paragraph($"{number}.  {step.Text}", size: 24, bold: true, spaceBefore: 240, spaceAfter: 80));

            if (!string.IsNullOrWhiteSpace(step.Notes))
            {
                body.Append(Paragraph(step.Notes, size: 20, color: "4B515C", spaceAfter: 80, indent: 360));
            }

            if (!step.HasImage)
            {
                continue;
            }

            using Bitmap? bitmap = GuideRenderer.Render(guide, step, settings, maxImageWidth);
            if (bitmap is null)
            {
                continue;
            }

            byte[] data;
            using (var buffer = new MemoryStream())
            {
                bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
                data = buffer.ToArray();
            }

            string name = $"image{images.Count + 1}.png";
            images.Add((name, data, bitmap.Width, bitmap.Height));

            long width = ContentWidthEmu;
            long height = (long)(width * (bitmap.Height / (double)Math.Max(1, bitmap.Width)));
            body.Append(ImageParagraph($"rIdImage{images.Count}", images.Count, width, height));
        }

        body.Append(
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>"
            + "<w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" "
            + "w:header=\"709\" w:footer=\"709\" w:gutter=\"0\"/></w:sectPr>");

        string document =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:document "
            + "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" "
            + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" "
            + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
            + "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">"
            + "<w:body>" + body + "</w:body></w:document>";

        var relationships = new StringBuilder();
        relationships.Append(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

        for (int i = 0; i < images.Count; i++)
        {
            relationships.Append(
                $"<Relationship Id=\"rIdImage{i + 1}\" "
                + "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" "
                + $"Target=\"media/{images[i].Name}\"/>");
        }

        relationships.Append("</Relationships>");

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(archive, "[Content_Types].xml", ContentTypes);
        WriteEntry(archive, "_rels/.rels", RootRelationships);
        WriteEntry(archive, "word/document.xml", document);
        WriteEntry(archive, "word/_rels/document.xml.rels", relationships.ToString());

        foreach ((string name, byte[] data, _, _) in images)
        {
            ZipArchiveEntry entry = archive.CreateEntry("word/media/" + name, CompressionLevel.NoCompression);
            using Stream target = entry.Open();
            target.Write(data, 0, data.Length);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream target = entry.Open();
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        target.Write(bytes, 0, bytes.Length);
    }

    private static string Paragraph(
        string text,
        int size = 22,
        bool bold = false,
        string? color = null,
        int spaceBefore = 0,
        int spaceAfter = 0,
        int indent = 0)
    {
        var properties = new StringBuilder("<w:pPr>");
        if (indent > 0)
        {
            properties.Append($"<w:ind w:left=\"{indent}\"/>");
        }

        properties.Append($"<w:spacing w:before=\"{spaceBefore}\" w:after=\"{spaceAfter}\" w:line=\"276\" w:lineRule=\"auto\"/>");
        properties.Append("</w:pPr>");

        var runProperties = new StringBuilder("<w:rPr>");
        runProperties.Append("<w:rFonts w:ascii=\"Segoe UI\" w:hAnsi=\"Segoe UI\" w:cs=\"Segoe UI\"/>");
        if (bold)
        {
            runProperties.Append("<w:b/>");
        }

        if (!string.IsNullOrEmpty(color))
        {
            runProperties.Append($"<w:color w:val=\"{color}\"/>");
        }

        runProperties.Append($"<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/>");
        runProperties.Append("</w:rPr>");

        var runs = new StringBuilder();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                runs.Append("<w:br/>");
            }

            runs.Append($"<w:t xml:space=\"preserve\">{Escape(lines[i])}</w:t>");
        }

        return $"<w:p>{properties}<w:r>{runProperties}{runs}</w:r></w:p>";
    }

    private static string ImageParagraph(string relationshipId, int index, long width, long height)
    {
        return "<w:p><w:pPr><w:spacing w:before=\"60\" w:after=\"200\"/></w:pPr><w:r><w:drawing>"
            + "<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">"
            + $"<wp:extent cx=\"{width}\" cy=\"{height}\"/>"
            + "<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>"
            + $"<wp:docPr id=\"{index + 100}\" name=\"Screenshot {index}\"/>"
            + "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>"
            + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">"
            + "<pic:pic>"
            + $"<pic:nvPicPr><pic:cNvPr id=\"{index + 100}\" name=\"Screenshot {index}\"/><pic:cNvPicPr/></pic:nvPicPr>"
            + $"<pic:blipFill><a:blip r:embed=\"{relationshipId}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
            + "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/>"
            + $"<a:ext cx=\"{width}\" cy=\"{height}\"/></a:xfrm>"
            + "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
            + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
        + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
        + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
        + "<Default Extension=\"png\" ContentType=\"image/png\"/>"
        + "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>"
        + "<Override PartName=\"/word/document.xml\" "
        + "ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
        + "</Types>";

    private const string RootRelationships =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
        + "<Relationship Id=\"rId1\" "
        + "Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" "
        + "Target=\"word/document.xml\"/>"
        + "</Relationships>";
}
