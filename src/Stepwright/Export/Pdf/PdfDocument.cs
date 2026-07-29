using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Stepwright.Export.Pdf;

public sealed class PdfTextStyle
{
    public double Size { get; init; } = 11;

    public bool Bold { get; init; }

    /// <summary>Zero is black and one is white.</summary>
    public double Gray { get; init; }

    public double SpaceBefore { get; init; }

    public double SpaceAfter { get; init; }

    public double Indent { get; init; }

    public double Leading { get; init; } = 1.38;
}

/// <summary>
/// Writes a document by hand: object table, cross reference table, embedded font and
/// jpeg pictures placed as they are. There is no dependency on the platform or on any
/// other library, so the whole thing can be produced and checked anywhere.
/// </summary>
public sealed class PdfDocument
{
    private const double DefaultPageWidth = 595.28;   // A4 in points
    private const double DefaultPageHeight = 841.89;

    private readonly double _pageWidth;
    private readonly double _pageHeight;
    private readonly double _margin;

    private readonly List<byte[]?> _objects = new();
    private readonly List<Page> _pages = new();
    private readonly PdfFont _regular = new("F1");
    private readonly PdfFont _bold = new("F2");

    private Page _page;
    private double _y;
    private int _imageCounter;

    public PdfDocument(double pageWidth = DefaultPageWidth, double pageHeight = DefaultPageHeight, double margin = 54)
    {
        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        _margin = margin;

        _page = new Page();
        _pages.Add(_page);
        _y = _pageHeight - _margin;
    }

    public double ContentWidth => _pageWidth - (_margin * 2);

    public int PageCount => _pages.Count;

    /// <summary>
    /// Supplies the font files to embed. Either may be null, in which case the document
    /// falls back to a standard font that every reader already has.
    /// </summary>
    public void UseFonts(byte[]? regular, byte[]? bold, string name)
    {
        _regular.Font = regular is null ? null : TrueTypeFont.Load(regular, name);
        _bold.Font = bold is null ? null : TrueTypeFont.Load(bold, name + "Bold");
    }

    public void Space(double points) => _y -= points;

    /// <summary>Starts a new page when the next block would not fit on this one.</summary>
    public void EnsureSpace(double height)
    {
        if (_y - height < _margin && _y < _pageHeight - _margin)
        {
            NewPage();
        }
    }

    public void NewPage()
    {
        _page = new Page();
        _pages.Add(_page);
        _y = _pageHeight - _margin;
    }

    public double MeasureText(string text, PdfTextStyle style)
    {
        PdfFont font = style.Bold ? Bold : _regular;
        List<string> lines = Wrap(text, font, style.Size, ContentWidth - style.Indent);
        return style.SpaceBefore + (lines.Count * style.Size * style.Leading) + style.SpaceAfter;
    }

    public void Text(string text, PdfTextStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        PdfFont font = style.Bold ? Bold : _regular;
        bool fauxBold = style.Bold && _bold.Font is null && _regular.Font is not null;

        List<string> lines = Wrap(text, font, style.Size, ContentWidth - style.Indent);
        double lineHeight = style.Size * style.Leading;

        _y -= style.SpaceBefore;

        foreach (string line in lines)
        {
            EnsureSpace(lineHeight);
            _y -= lineHeight;

            double x = _margin + style.Indent;
            double baseline = _y + (lineHeight * 0.22);

            _page.Content.Append("BT\n");
            _page.Content.Append(Number(style.Gray)).Append(' ').Append(Number(style.Gray)).Append(' ')
                .Append(Number(style.Gray)).Append(" rg\n");
            _page.Content.Append('/').Append(font.Resource).Append(' ').Append(Number(style.Size)).Append(" Tf\n");

            if (fauxBold)
            {
                // No bold face available, so the glyphs are outlined as well as filled.
                _page.Content.Append("2 Tr ").Append(Number(style.Size * 0.03)).Append(" w\n");
                _page.Content.Append(Number(style.Gray)).Append(' ').Append(Number(style.Gray)).Append(' ')
                    .Append(Number(style.Gray)).Append(" RG\n");
            }

            _page.Content.Append(Number(x)).Append(' ').Append(Number(baseline)).Append(" Td\n");
            _page.Content.Append('<').Append(Encode(line, font)).Append("> Tj\n");

            if (fauxBold)
            {
                _page.Content.Append("0 Tr\n");
            }

            _page.Content.Append("ET\n");
        }

        _y -= style.SpaceAfter;
    }

    public void HorizontalRule(double gray = 0.85, double spaceBefore = 6, double spaceAfter = 10)
    {
        _y -= spaceBefore;
        EnsureSpace(2);

        _page.Content.Append(Number(gray)).Append(' ').Append(Number(gray)).Append(' ')
            .Append(Number(gray)).Append(" RG\n0.7 w\n");
        _page.Content.Append(Number(_margin)).Append(' ').Append(Number(_y)).Append(" m\n");
        _page.Content.Append(Number(_pageWidth - _margin)).Append(' ').Append(Number(_y)).Append(" l\nS\n");

        _y -= spaceAfter;
    }

    public double MeasureImage(byte[] jpeg, double indent = 0)
    {
        JpegInfo? info = JpegInfo.Read(jpeg);
        if (info is null)
        {
            return 0;
        }

        double width = ContentWidth - indent;
        double height = width * info.Height / info.Width;
        double maxHeight = _pageHeight - (_margin * 2);

        return Math.Min(height, maxHeight);
    }

    /// <summary>Places a jpeg at the cursor, scaled to the width of the text column.</summary>
    public void Image(byte[] jpeg, double indent = 0)
    {
        JpegInfo? info = JpegInfo.Read(jpeg);
        if (info is null)
        {
            return;
        }

        double width = ContentWidth - indent;
        double height = width * info.Height / info.Width;
        double maxHeight = _pageHeight - (_margin * 2);

        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * info.Width / info.Height;
        }

        EnsureSpace(height);
        _y -= height;

        string name = "Im" + (++_imageCounter);
        int id = Reserve();
        Set(id, StreamObject(
            id,
            "/Type /XObject /Subtype /Image "
            + $"/Width {info.Width} /Height {info.Height} "
            + $"/ColorSpace {info.ColorSpace} /BitsPerComponent 8 /Filter /DCTDecode",
            jpeg));

        _page.Images[name] = id;

        _page.Content.Append("q\n");
        _page.Content.Append(Number(width)).Append(" 0 0 ").Append(Number(height)).Append(' ')
            .Append(Number(_margin + indent)).Append(' ').Append(Number(_y)).Append(" cm\n");
        _page.Content.Append('/').Append(name).Append(" Do\nQ\n");
    }

    // ------------------------------------------------------------------ text measurement

    private PdfFont Bold => _bold.Font is not null ? _bold : _regular;

    private static double GlyphWidth(PdfFont font, int codePoint, double size)
    {
        if (font.Font is null)
        {
            // A standard font is only used when no file could be read. Half an em is a fair
            // average for the wrapping to stay inside the column.
            return size * 0.5;
        }

        ushort glyph = font.Font.GlyphFor(codePoint);
        if (glyph == 0)
        {
            glyph = font.Font.GlyphFor('?');
        }

        return font.Font.ScaledAdvance(glyph) / 1000.0 * size;
    }

    private static double MeasureLine(string text, PdfFont font, double size)
    {
        double total = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int codePoint = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i]))
            {
                i++;
            }

            total += GlyphWidth(font, codePoint, size);
        }

        return total;
    }

    private static List<string> Wrap(string text, PdfFont font, double size, double maxWidth)
    {
        var lines = new List<string>();
        if (maxWidth <= 0)
        {
            maxWidth = 1;
        }

        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var line = new StringBuilder();
            foreach (string word in paragraph.Split(' ', StringSplitOptions.None))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (MeasureLine(candidate, font, size) <= maxWidth || line.Length == 0)
                {
                    // A single word wider than the column is broken by character below.
                    if (line.Length == 0 && MeasureLine(word, font, size) > maxWidth)
                    {
                        foreach (string piece in BreakWord(word, font, size, maxWidth))
                        {
                            lines.Add(piece);
                        }

                        continue;
                    }

                    line.Clear();
                    line.Append(candidate);
                }
                else
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    line.Append(word);
                }
            }

            if (line.Length > 0)
            {
                lines.Add(line.ToString());
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        return lines;
    }

    private static IEnumerable<string> BreakWord(string word, PdfFont font, double size, double maxWidth)
    {
        var piece = new StringBuilder();
        foreach (char c in word)
        {
            if (piece.Length > 0 && MeasureLine(piece.ToString() + c, font, size) > maxWidth)
            {
                yield return piece.ToString();
                piece.Clear();
            }

            piece.Append(c);
        }

        if (piece.Length > 0)
        {
            yield return piece.ToString();
        }
    }

    private static string Encode(string text, PdfFont font)
    {
        var hex = new StringBuilder(text.Length * 4);

        for (int i = 0; i < text.Length; i++)
        {
            int codePoint = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i]))
            {
                i++;
            }

            if (font.Font is null)
            {
                // Standard font, one byte per character in the Windows Latin encoding.
                int code = codePoint is > 0 and < 256 ? codePoint : '?';
                hex.Append(code.ToString("X2", CultureInfo.InvariantCulture));
                continue;
            }

            ushort glyph = font.Font.GlyphFor(codePoint);
            if (glyph == 0)
            {
                glyph = font.Font.GlyphFor('?');
                codePoint = '?';
            }

            font.Used[glyph] = codePoint;
            hex.Append(glyph.ToString("X4", CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    // ------------------------------------------------------------------ file assembly

    public byte[] Build(string title, string author)
    {
        int regularId = BuildFont(_regular);
        int boldId = _bold.Font is not null ? BuildFont(_bold) : regularId;

        int pagesId = Reserve();
        var pageIds = new List<int>();

        foreach (Page page in _pages)
        {
            byte[] content = Latin1(page.Content.ToString());
            int contentId = Reserve();
            Set(contentId, StreamObject(contentId, "/Filter /FlateDecode", Deflate(content), alreadyEncoded: true));

            var resources = new StringBuilder();
            resources.Append("<< /Font << /").Append(_regular.Resource).Append(' ').Append(regularId).Append(" 0 R");
            if (_bold.Font is not null)
            {
                resources.Append(" /").Append(_bold.Resource).Append(' ').Append(boldId).Append(" 0 R");
            }

            resources.Append(" >>");

            if (page.Images.Count > 0)
            {
                resources.Append(" /XObject << ");
                foreach ((string name, int id) in page.Images)
                {
                    resources.Append('/').Append(name).Append(' ').Append(id).Append(" 0 R ");
                }

                resources.Append(">>");
            }

            resources.Append(" /ProcSet [/PDF /Text /ImageC] >>");

            int pageId = Reserve();
            Set(pageId, DictionaryObject(
                pageId,
                $"<< /Type /Page /Parent {pagesId} 0 R "
                + $"/MediaBox [0 0 {Number(_pageWidth)} {Number(_pageHeight)}] "
                + $"/Resources {resources} /Contents {contentId} 0 R >>"));

            pageIds.Add(pageId);
        }

        var kids = new StringBuilder("[");
        foreach (int id in pageIds)
        {
            kids.Append(id).Append(" 0 R ");
        }

        kids.Append(']');

        Set(pagesId, DictionaryObject(
            pagesId,
            $"<< /Type /Pages /Kids {kids} /Count {pageIds.Count} >>"));

        int infoId = Reserve();
        Set(infoId, DictionaryObject(
            infoId,
            "<< /Title " + TextString(title)
            + " /Author " + TextString(author)
            + " /Producer " + TextString("Stepwright")
            + " /Creator " + TextString("Stepwright") + " >>"));

        int catalogId = Reserve();
        Set(catalogId, DictionaryObject(catalogId, $"<< /Type /Catalog /Pages {pagesId} 0 R >>"));

        return Assemble(catalogId, infoId);
    }

    private int BuildFont(PdfFont font)
    {
        if (font.Font is null)
        {
            int standardId = Reserve();
            Set(standardId, DictionaryObject(
                standardId,
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
            return standardId;
        }

        TrueTypeFont face = font.Font;
        double scale = 1000.0 / face.UnitsPerEm;

        int fileId = Reserve();
        Set(fileId, StreamObject(
            fileId,
            $"/Length1 {face.Data.Length} /Filter /FlateDecode",
            Deflate(face.Data),
            alreadyEncoded: true));

        int descriptorId = Reserve();
        Set(descriptorId, DictionaryObject(
            descriptorId,
            $"<< /Type /FontDescriptor /FontName /{face.Name} /Flags 32 "
            + $"/FontBBox [{(int)(face.XMin * scale)} {(int)(face.YMin * scale)} "
            + $"{(int)(face.XMax * scale)} {(int)(face.YMax * scale)}] "
            + $"/ItalicAngle 0 /Ascent {(int)(face.Ascender * scale)} /Descent {(int)(face.Descender * scale)} "
            + $"/CapHeight {(int)(face.Ascender * scale * 0.72)} /StemV 80 /FontFile2 {fileId} 0 R >>"));

        var widths = new StringBuilder("[");
        foreach (ushort glyph in font.Used.Keys.OrderBy(g => g))
        {
            widths.Append(glyph).Append(" [").Append(face.ScaledAdvance(glyph)).Append("] ");
        }

        widths.Append(']');

        int descendantId = Reserve();
        Set(descendantId, DictionaryObject(
            descendantId,
            $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{face.Name} "
            + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + $"/FontDescriptor {descriptorId} 0 R /DW 600 /W {widths} /CIDToGIDMap /Identity >>"));

        int toUnicodeId = Reserve();
        Set(toUnicodeId, StreamObject(toUnicodeId, "/Filter /FlateDecode", Deflate(Latin1(ToUnicodeCMap(font))), alreadyEncoded: true));

        int fontId = Reserve();
        Set(fontId, DictionaryObject(
            fontId,
            $"<< /Type /Font /Subtype /Type0 /BaseFont /{face.Name} /Encoding /Identity-H "
            + $"/DescendantFonts [{descendantId} 0 R] /ToUnicode {toUnicodeId} 0 R >>"));

        return fontId;
    }

    /// <summary>Lets a reader turn the glyphs back into text for search and copy.</summary>
    private static string ToUnicodeCMap(PdfFont font)
    {
        var map = new StringBuilder();
        map.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        map.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        map.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        map.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        List<KeyValuePair<ushort, int>> entries = font.Used.OrderBy(pair => pair.Key).ToList();

        for (int offset = 0; offset < entries.Count; offset += 100)
        {
            List<KeyValuePair<ushort, int>> chunk = entries.Skip(offset).Take(100).ToList();
            map.Append(chunk.Count).Append(" beginbfchar\n");

            foreach ((ushort glyph, int codePoint) in chunk)
            {
                map.Append('<').Append(glyph.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");

                if (codePoint > 0xFFFF)
                {
                    // Outside the basic plane the value is written as a surrogate pair.
                    int value = codePoint - 0x10000;
                    int high = 0xD800 + (value >> 10);
                    int low = 0xDC00 + (value & 0x3FF);
                    map.Append(high.ToString("X4", CultureInfo.InvariantCulture));
                    map.Append(low.ToString("X4", CultureInfo.InvariantCulture));
                }
                else
                {
                    map.Append(codePoint.ToString("X4", CultureInfo.InvariantCulture));
                }

                map.Append(">\n");
            }

            map.Append("endbfchar\n");
        }

        map.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return map.ToString();
    }

    private byte[] Assemble(int catalogId, int infoId)
    {
        using var output = new MemoryStream();
        Write(output, "%PDF-1.7\n");

        // A comment with high bytes marks the file as binary for anything that copies it.
        output.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });

        var offsets = new long[_objects.Count + 1];

        for (int i = 0; i < _objects.Count; i++)
        {
            byte[]? body = _objects[i];
            if (body is null)
            {
                // A reserved slot that was never filled still needs to exist.
                body = DictionaryObject(i + 1, "<< >>");
            }

            offsets[i + 1] = output.Position;
            output.Write(body);
        }

        long xref = output.Position;
        Write(output, $"xref\n0 {_objects.Count + 1}\n");
        Write(output, "0000000000 65535 f \n");

        for (int i = 1; i <= _objects.Count; i++)
        {
            Write(output, offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }

        Write(output, $"trailer\n<< /Size {_objects.Count + 1} /Root {catalogId} 0 R /Info {infoId} 0 R >>\n");
        Write(output, $"startxref\n{xref}\n%%EOF\n");

        return output.ToArray();
    }

    private int Reserve()
    {
        _objects.Add(null);
        return _objects.Count;
    }

    private void Set(int id, byte[] body) => _objects[id - 1] = body;

    private static byte[] DictionaryObject(int id, string dictionary) =>
        Latin1($"{id} 0 obj\n{dictionary}\nendobj\n");

    private static byte[] StreamObject(int id, string dictionary, byte[] data, bool alreadyEncoded = false)
    {
        using var buffer = new MemoryStream();
        Write(buffer, $"{id} 0 obj\n<< {dictionary} /Length {data.Length} >>\nstream\n");
        buffer.Write(data);
        Write(buffer, "\nendstream\nendobj\n");
        return buffer.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    /// <summary>A string in the file, written as unicode so any language survives.</summary>
    private static string TextString(string value)
    {
        var hex = new StringBuilder("<FEFF");
        foreach (char c in value ?? string.Empty)
        {
            hex.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        hex.Append('>');
        return hex.ToString();
    }

    private static string Number(double value) =>
        Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static void Write(Stream stream, string text)
    {
        byte[] bytes = Latin1(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private sealed class Page
    {
        public StringBuilder Content { get; } = new();

        public Dictionary<string, int> Images { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PdfFont
    {
        public PdfFont(string resource) => Resource = resource;

        public string Resource { get; }

        public TrueTypeFont? Font { get; set; }

        /// <summary>Glyphs actually used, and the character each one came from.</summary>
        public Dictionary<ushort, int> Used { get; } = new();
    }
}
