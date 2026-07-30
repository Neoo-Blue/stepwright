using System.Net;
using System.Text;
using Stepwright.Config;
using Stepwright.Model;
using Stepwright.Render;

namespace Stepwright.Export;

public sealed class HtmlOptions
{
    /// <summary>A fragment drops the page shell so the markup can be pasted into another system.</summary>
    public bool Fragment { get; set; }

    /// <summary>The rules to write by. Without one the standard look is used.</summary>
    public FormatProfile? Format { get; set; }

    /// <summary>Overrides the format when set, for an export that writes pictures beside the file.</summary>
    public bool? EmbedImages { get; set; }

    public string? ImageFolder { get; set; }
    public string ImageFolderName { get; set; } = "images";

    /// <summary>
    /// Filled in as the document is written: the picture for each step, by step number. Used
    /// by a system that wants the pictures attached separately rather than carried inline.
    /// </summary>
    public Dictionary<int, byte[]> CollectedImages { get; } = new();

    /// <summary>Collect the pictures rather than writing them into the markup.</summary>
    public bool CollectImagesOnly { get; set; }
}

public static class HtmlExporter
{
    public static string Build(Guide guide, AppSettings settings, HtmlOptions options)
    {
        FormatProfile format = options.Format ?? FormatProfiles.Standard();
        var body = new StringBuilder();
        int number = 0;

        if (!string.IsNullOrWhiteSpace(format.Preamble))
        {
            body.AppendLine(format.Preamble);
        }

        if (format.SingleContainer)
        {
            body.AppendLine($"<div{Style(format, Container(format))}>");
        }

        if (format.IncludeTitle && !string.IsNullOrWhiteSpace(guide.Title))
        {
            body.AppendLine(format.UseHeadingTags
                ? $"<h1{Style(format, Heading(format, format.TitleSize))}>{Escape(guide.Title)}</h1>"
                : $"<div{Style(format, Heading(format, format.TitleSize))}><b>{Escape(guide.Title)}</b></div>");
        }

        if (format.IncludeSummary && !string.IsNullOrWhiteSpace(guide.Summary))
        {
            body.AppendLine($"<div{Style(format, Body(format))}>{Escape(guide.Summary)}</div>");
        }

        if (format.IncludeMeta)
        {
            var facts = new List<string>();
            if (!string.IsNullOrWhiteSpace(guide.Author))
            {
                facts.Add("By " + Escape(guide.Author));
            }

            facts.Add(guide.Updated.ToString("d MMMM yyyy"));
            int count = guide.Visible.Count(s => s.Kind != StepKind.Heading);
            facts.Add(count == 1 ? "1 step" : count + " steps");

            // A numeric entity rather than a named one, because a system that treats the
            // markup as strict xml will reject anything outside the five it knows.
            body.AppendLine($"<div{Style(format, Meta(format))}>{string.Join(" &#160;·&#160; ", facts)}</div>");
        }

        bool listOpen = false;

        void OpenList()
        {
            if (format.UseOrderedList && !listOpen)
            {
                body.AppendLine($"<ol{Style(format, List(format))}>");
                listOpen = true;
            }
        }

        void CloseList()
        {
            if (listOpen)
            {
                body.AppendLine("</ol>");
                listOpen = false;
            }
        }

        OpenList();

        foreach (Step step in guide.Visible)
        {
            if (step.Kind == StepKind.Heading)
            {
                CloseList();

                body.AppendLine(format.UseHeadingTags
                    ? $"<h2{Style(format, Heading(format, format.HeadingSize))}>{Escape(step.Text)}</h2>"
                    : $"<div{Style(format, Heading(format, format.HeadingSize))}><b>{Escape(step.Text)}</b></div>");

                OpenList();
                continue;
            }

            number++;
            string text = Escape(step.Text);
            if (format.BoldStepText)
            {
                text = "<b>" + text + "</b>";
            }

            if (format.UseOrderedList)
            {
                body.AppendLine($"  <li{Style(format, Item(format))}>{text}");
            }
            else
            {
                string label = format.StepPrefix + number + format.StepSuffix;
                body.AppendLine($"<div{Style(format, Body(format))}>{Escape(label)} {text}</div>");
            }

            if (!string.IsNullOrWhiteSpace(step.Notes))
            {
                body.AppendLine($"    <div{Style(format, Note(format))}>{Escape(format.NotePrefix)}{Escape(step.Notes)}</div>");
            }

            string? picture = Picture(guide, step, settings, format, options, number);
            if (picture is not null)
            {
                body.AppendLine("    " + picture);
            }

            if (format.UseOrderedList)
            {
                body.AppendLine("  </li>");
            }
        }

        CloseList();

        if (format.IncludeFooter && !options.Fragment && !string.IsNullOrWhiteSpace(format.FooterText))
        {
            string footer = format.FooterText.Replace(
                "{date}",
                DateTime.Now.ToString("d MMMM yyyy"),
                StringComparison.Ordinal);

            body.AppendLine($"<div{Style(format, Footer(format))}>{Escape(footer)}</div>");
        }

        if (format.SingleContainer)
        {
            body.AppendLine("</div>");
        }

        // Styles written on each element carry themselves, so no stylesheet is needed.
        if (format.InlineStyles)
        {
            return options.Fragment ? body.ToString() : Page(guide, body.ToString(), string.Empty);
        }

        string sheet = Stylesheet(format);
        return options.Fragment
            ? "<style>\n" + sheet + "\n</style>\n" + body
            : Page(guide, body.ToString(), PageCss + "\n" + sheet);
    }

    private static string Page(Guide guide, string body, string css)
    {
        var page = new StringBuilder();
        page.AppendLine("<!doctype html>");
        page.AppendLine("<html lang=\"en\">");
        page.AppendLine("<head>");
        page.AppendLine("<meta charset=\"utf-8\" />");
        page.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        page.AppendLine($"<title>{Escape(guide.Title)}</title>");

        if (!string.IsNullOrWhiteSpace(css))
        {
            page.AppendLine("<style>");
            page.AppendLine(css);
            page.AppendLine("</style>");
        }

        page.AppendLine("</head>");
        page.AppendLine("<body>");
        page.Append(body);
        page.AppendLine("</body>");
        page.AppendLine("</html>");
        return page.ToString();
    }

    // ------------------------------------------------------------------ pictures

    private static string? Picture(
        Guide guide,
        Step step,
        AppSettings settings,
        FormatProfile format,
        HtmlOptions options,
        int number)
    {
        if (!step.HasImage)
        {
            return null;
        }

        // An animated step is written as an animation where the format allows one.
        byte[]? animation = step.Animate && format.AllowAnimation
            ? GuideRenderer.RenderAnimation(guide, step, settings)
            : null;

        byte[]? bytes = animation ?? (format.UseJpeg
            ? GuideRenderer.RenderJpeg(guide, step, settings, format.ImageWidth, format.JpegQuality)
            : GuideRenderer.RenderPng(guide, step, settings, format.ImageWidth));

        if (bytes is null)
        {
            return null;
        }

        options.CollectedImages[number] = bytes;

        // Some systems keep pictures beside the page and refer to them by name.
        if (!string.IsNullOrWhiteSpace(format.ImagePlaceholder))
        {
            return format.ImagePlaceholder.Replace("{n}", number.ToString("D3"), StringComparison.Ordinal);
        }

        if (options.CollectImagesOnly)
        {
            return null;
        }

        string extension = animation is not null ? ".gif" : format.UseJpeg ? ".jpg" : ".png";
        string mime = animation is not null ? "image/gif" : format.UseJpeg ? "image/jpeg" : "image/png";

        string source;
        bool embed = options.EmbedImages ?? format.EmbedImages;

        if (embed || string.IsNullOrEmpty(options.ImageFolder))
        {
            source = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        else
        {
            Directory.CreateDirectory(options.ImageFolder);
            string name = $"step{number:D3}{extension}";
            File.WriteAllBytes(Path.Combine(options.ImageFolder, name), bytes);
            source = options.ImageFolderName + "/" + name;
        }

        string style = Style(format, Image(format));
        string alt = Escape(step.Text);
        return $"<img{(format.InlineStyles ? string.Empty : " class=\"sw-shot\"")}{style} src=\"{source}\" alt=\"{alt}\" />";
    }

    // ------------------------------------------------------------------ styling

    /// <summary>
    /// Writes the style on the element when the format asks for it, and the class name
    /// otherwise, so the same builder serves both kinds of document.
    /// </summary>
    private static string Style(FormatProfile format, (string Class, string Inline) rules) =>
        format.InlineStyles
            ? (string.IsNullOrEmpty(rules.Inline) ? string.Empty : $" style=\"{rules.Inline}\"")
            : (string.IsNullOrEmpty(rules.Class) ? string.Empty : $" class=\"{rules.Class}\"");

    private static string Font(FormatProfile format) =>
        string.IsNullOrWhiteSpace(format.FontFamily) ? string.Empty : $"font-family:{format.FontFamily};";

    private static (string, string) Container(FormatProfile format) =>
        ("sw-doc", Font(format) + $"font-size:{format.BodySize}px;");

    private static (string, string) Heading(FormatProfile format, int size) =>
        ("sw-section", Font(format) + $"font-size:{size}px;font-weight:bold;margin-bottom:{format.BlockSpacing}px;");

    private static (string, string) Body(FormatProfile format) =>
        ("sw-text", Font(format) + $"font-size:{format.BodySize}px;margin-bottom:{format.BlockSpacing}px;");

    private static (string, string) Note(FormatProfile format) =>
        ("sw-note", Font(format) + $"font-size:{format.NoteSize}px;margin-bottom:{format.BlockSpacing}px;" + Quiet(format));

    private static (string, string) Meta(FormatProfile format) =>
        ("sw-meta", Font(format) + $"font-size:{format.MetaSize}px;margin-bottom:{format.BlockSpacing}px;" + Quiet(format));

    private static (string, string) Footer(FormatProfile format) =>
        ("sw-foot", Font(format) + $"font-size:{format.MetaSize}px;margin-top:18px;padding-top:8px;border-top:1px solid;" + Quiet(format));

    /// <summary>
    /// How the less important text is held back. A format that is not allowed to state a
    /// colour fades it instead, so the receiving system keeps control of light and dark mode.
    /// </summary>
    private static string Quiet(FormatProfile format) =>
        format.AllowColor ? "color:#6c7480;" : "opacity:.7;";

    private static (string, string) List(FormatProfile format) =>
        ("sw-steps", Font(format) + $"font-size:{format.BodySize}px;margin-top:0px;margin-bottom:{format.BlockSpacing}px;");

    private static (string, string) Item(FormatProfile format) =>
        ("sw-step", $"margin-bottom:{format.BlockSpacing}px;");

    private static (string, string) Image(FormatProfile format)
    {
        var inline = new StringBuilder("display:block;height:auto;max-width:100%;");

        if (format.ImageDisplayWidth > 0)
        {
            inline.Append($"width:{format.ImageDisplayWidth}px;");
        }

        inline.Append($"margin-top:8px;margin-bottom:{format.BlockSpacing}px;");

        if (format.RoundImageCorners)
        {
            inline.Append("border-radius:10px;");
        }

        return ("sw-shot", inline.ToString());
    }

    /// <summary>The stylesheet used when the rules are not written onto each element.</summary>
    private static string Stylesheet(FormatProfile format)
    {
        var css = new StringBuilder();
        string font = string.IsNullOrWhiteSpace(format.FontFamily)
            ? "system-ui, sans-serif"
            : format.FontFamily;

        css.AppendLine($".sw-doc {{ max-width: 860px; margin: 0 auto; padding: 40px 24px 72px; font-family: {font}; font-size: {format.BodySize}px; line-height: 1.55; }}");
        css.AppendLine($".sw-doc h1 {{ font-size: {format.TitleSize}px; line-height: 1.2; margin: 0 0 12px; }}");
        css.AppendLine($".sw-section {{ font-size: {format.HeadingSize}px; margin: 32px 0 10px; }}");
        css.AppendLine($".sw-meta {{ font-size: {format.MetaSize}px; {Faded(format, 0.72)} margin-bottom: 24px; }}");
        css.AppendLine($".sw-steps {{ margin-top: 0; margin-bottom: {format.BlockSpacing}px; padding-left: 22px; }}");
        css.AppendLine($".sw-step {{ margin-bottom: {format.BlockSpacing + 12}px; }}");
        css.AppendLine($".sw-note {{ font-size: {format.NoteSize}px; {Faded(format, 0.8)} margin: 6px 0 {format.BlockSpacing}px; }}");

        var image = new StringBuilder(".sw-shot { display: block; height: auto; max-width: 100%;");
        image.Append(format.ImageDisplayWidth > 0 ? $" width: {format.ImageDisplayWidth}px;" : string.Empty);
        image.Append($" margin: 8px 0 {format.BlockSpacing}px;");
        image.Append(format.RoundImageCorners ? " border: 1px solid rgba(128,128,128,0.3); border-radius: 10px;" : string.Empty);
        image.AppendLine(" }");
        css.Append(image);

        css.AppendLine($".sw-foot {{ font-size: {format.MetaSize}px; {Faded(format, 0.6)} margin-top: 40px; padding-top: 12px; border-top: 1px solid rgba(128,128,128,0.3); }}");

        if (format.AllowColor)
        {
            css.AppendLine(".sw-num { background: #2563eb; color: #fff; }");
        }
        css.AppendLine("@media print { .sw-step { break-inside: avoid; page-break-inside: avoid; } .sw-foot { display: none; } }");

        return css.ToString();
    }

    private static string Faded(FormatProfile format, double amount) =>
        format.AllowColor ? "color: #6c7480;" : $"opacity: {amount};";

    private const string PageCss = """
    :root { color-scheme: light dark; }
    body { margin: 0; background: #f6f7f9; }
    @media (prefers-color-scheme: dark) { body { background: #14161a; } }
    """;

    public static void Export(Guide guide, AppSettings settings, string path, HtmlOptions options)
    {
        bool embed = options.EmbedImages ?? (options.Format ?? FormatProfiles.Standard()).EmbedImages;

        if (!embed)
        {
            // The folder carries the document name, so two guides exported side by side
            // cannot overwrite each other's pictures.
            string root = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            options.ImageFolderName = Path.GetFileNameWithoutExtension(path) + " images";
            options.ImageFolder = Path.Combine(root, options.ImageFolderName);
        }

        File.WriteAllText(path, Build(guide, settings, options), new UTF8Encoding(false));
    }

    public static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
