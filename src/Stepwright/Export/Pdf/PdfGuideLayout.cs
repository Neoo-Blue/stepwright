namespace Stepwright.Export.Pdf;

/// <summary>One entry in a document: either a section heading or a numbered step.</summary>
public sealed class PdfGuideItem
{
    public bool IsHeading { get; init; }

    public int Number { get; init; }

    public string Text { get; init; } = string.Empty;

    public string? Note { get; init; }

    /// <summary>The picture for this step, already compressed as a jpeg.</summary>
    public byte[]? Picture { get; init; }
}

public sealed class PdfGuideHeader
{
    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string DateLine { get; init; } = string.Empty;

    public int StepCount { get; init; }
}

/// <summary>
/// Lays a guide out on the page. This holds every decision about spacing and page breaks
/// and touches nothing platform specific, so the same code that ships is the code that gets
/// exercised by the probe.
/// </summary>
public static class PdfGuideLayout
{
    private static PdfTextStyle TitleStyle => new() { Size = 21, Bold = true, SpaceAfter = 6 };

    private static PdfTextStyle SummaryStyle => new() { Size = 11.5, Gray = 0.28, SpaceAfter = 4 };

    private static PdfTextStyle MetaStyle => new() { Size = 8.6, Gray = 0.52 };

    private static PdfTextStyle HeadingStyle => new() { Size = 14, Bold = true, SpaceBefore = 10, SpaceAfter = 6 };

    private static PdfTextStyle StepStyle => new() { Size = 11.5, Bold = true, SpaceBefore = 10, SpaceAfter = 6 };

    private static PdfTextStyle NoteStyle => new() { Size = 10, Gray = 0.35, Indent = 16, SpaceAfter = 4 };

    private static PdfTextStyle FooterStyle => new() { Size = 8, Gray = 0.6 };

    public static byte[] Build(
        PdfGuideHeader header,
        IEnumerable<PdfGuideItem> items,
        byte[]? regularFont,
        byte[]? boldFont,
        string fontName)
    {
        var document = new PdfDocument();
        document.UseFonts(regularFont, boldFont, fontName);

        document.Text(header.Title, TitleStyle);

        if (!string.IsNullOrWhiteSpace(header.Summary))
        {
            document.Text(header.Summary, SummaryStyle);
        }

        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(header.Author))
        {
            facts.Add("By " + header.Author);
        }

        if (!string.IsNullOrWhiteSpace(header.DateLine))
        {
            facts.Add(header.DateLine);
        }

        facts.Add(header.StepCount == 1 ? "1 step" : header.StepCount + " steps");

        document.Text(string.Join("   ", facts), MetaStyle);
        document.HorizontalRule();

        foreach (PdfGuideItem item in items)
        {
            if (item.IsHeading)
            {
                // A heading on its own at the foot of a page reads as an orphan.
                document.EnsureSpace(90);
                document.Text(item.Text, HeadingStyle);
                continue;
            }

            string text = $"{item.Number}.  {item.Text}";

            // A step and its picture belong on the same page, so the room for both is
            // claimed before either one is written.
            double needed = document.MeasureText(text, StepStyle);

            if (!string.IsNullOrWhiteSpace(item.Note))
            {
                needed += document.MeasureText(item.Note, NoteStyle);
            }

            if (item.Picture is not null)
            {
                needed += document.MeasureImage(item.Picture) + 12;
            }

            document.EnsureSpace(needed);
            document.Text(text, StepStyle);

            if (!string.IsNullOrWhiteSpace(item.Note))
            {
                document.Text(item.Note, NoteStyle);
            }

            if (item.Picture is not null)
            {
                document.Image(item.Picture);
                document.Space(12);
            }
        }

        document.Space(6);
        document.HorizontalRule(0.88, spaceBefore: 10, spaceAfter: 8);
        document.Text("Made with Stepwright", FooterStyle);

        return document.Build(header.Title, header.Author);
    }
}
