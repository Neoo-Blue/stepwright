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

    /// <summary>Pictures travel inside the markup instead of sitting in a folder next to it.</summary>
    public bool EmbedImages { get; set; } = true;

    public bool UseJpeg { get; set; }
    public int MaxImageWidth { get; set; } = 1400;
    public long JpegQuality { get; set; } = 82;
    public string? ImageFolder { get; set; }
    public string ImageFolderName { get; set; } = "images";
}

public static class HtmlExporter
{
    public static string Build(Guide guide, AppSettings settings, HtmlOptions options)
    {
        var body = new StringBuilder();
        int number = 0;

        body.AppendLine("<div class=\"sw-doc\">");

        body.AppendLine("<header class=\"sw-head\">");
        body.AppendLine($"  <h1>{Escape(guide.Title)}</h1>");

        if (!string.IsNullOrWhiteSpace(guide.Summary))
        {
            body.AppendLine($"  <p class=\"sw-summary\">{Escape(guide.Summary)}</p>");
        }

        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(guide.Author))
        {
            facts.Add("By " + Escape(guide.Author));
        }

        facts.Add(guide.Updated.ToString("d MMMM yyyy"));
        int count = guide.Visible.Count(s => s.Kind != StepKind.Heading);
        facts.Add(count == 1 ? "1 step" : count + " steps");
        body.AppendLine($"  <p class=\"sw-meta\">{string.Join(" &nbsp;·&nbsp; ", facts)}</p>");
        body.AppendLine("</header>");

        body.AppendLine("<ol class=\"sw-steps\">");

        foreach (Step step in guide.Visible)
        {
            if (step.Kind == StepKind.Heading)
            {
                body.AppendLine("</ol>");
                body.AppendLine($"<h2 class=\"sw-section\">{Escape(step.Text)}</h2>");
                body.AppendLine("<ol class=\"sw-steps\">");
                continue;
            }

            number++;
            body.AppendLine("  <li class=\"sw-step\">");
            body.AppendLine("    <div class=\"sw-row\">");
            body.AppendLine($"      <div class=\"sw-num\">{number}</div>");
            body.AppendLine($"      <div class=\"sw-text\">{Escape(step.Text)}</div>");
            body.AppendLine("    </div>");

            if (!string.IsNullOrWhiteSpace(step.Notes))
            {
                body.AppendLine($"    <p class=\"sw-note\">{Escape(step.Notes)}</p>");
            }

            string? src = ImageSource(guide, step, settings, options, number);
            if (src is not null)
            {
                body.AppendLine($"    <img class=\"sw-shot\" src=\"{src}\" alt=\"{Escape(step.Text)}\" loading=\"lazy\" />");
            }

            body.AppendLine("  </li>");
        }

        body.AppendLine("</ol>");

        if (!options.Fragment)
        {
            body.AppendLine("<footer class=\"sw-foot\">Made with Stepwright</footer>");
        }

        body.AppendLine("</div>");

        if (options.Fragment)
        {
            // No page level rules here. A fragment that restyled the host page would
            // repaint whatever it was pasted into.
            return "<style>\n" + DocumentCss + "\n</style>\n" + body;
        }

        var page = new StringBuilder();
        page.AppendLine("<!doctype html>");
        page.AppendLine("<html lang=\"en\">");
        page.AppendLine("<head>");
        page.AppendLine("<meta charset=\"utf-8\" />");
        page.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        page.AppendLine($"<title>{Escape(guide.Title)}</title>");
        page.AppendLine("<style>");
        page.AppendLine(PageCss);
        page.AppendLine(DocumentCss);
        page.AppendLine("</style>");
        page.AppendLine("</head>");
        page.AppendLine("<body>");
        page.Append(body);
        page.AppendLine("</body>");
        page.AppendLine("</html>");
        return page.ToString();
    }

    private static string? ImageSource(Guide guide, Step step, AppSettings settings, HtmlOptions options, int number)
    {
        if (!step.HasImage)
        {
            return null;
        }

        byte[]? bytes = options.UseJpeg
            ? GuideRenderer.RenderJpeg(guide, step, settings, options.MaxImageWidth, options.JpegQuality)
            : GuideRenderer.RenderPng(guide, step, settings, options.MaxImageWidth);

        if (bytes is null)
        {
            return null;
        }

        string extension = options.UseJpeg ? ".jpg" : ".png";
        string mime = options.UseJpeg ? "image/jpeg" : "image/png";

        if (options.EmbedImages || string.IsNullOrEmpty(options.ImageFolder))
        {
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }

        Directory.CreateDirectory(options.ImageFolder);
        string name = $"step{number:D3}{extension}";
        File.WriteAllBytes(Path.Combine(options.ImageFolder, name), bytes);
        return options.ImageFolderName + "/" + name;
    }

    public static void Export(Guide guide, AppSettings settings, string path, HtmlOptions options)
    {
        if (!options.EmbedImages)
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

    /// <summary>Rules that touch the page itself, used only for a whole document export.</summary>
    private const string PageCss = """
    :root { color-scheme: light dark; }
    body { margin: 0; background: #f6f7f9; }
    @media (prefers-color-scheme: dark) { body { background: #14161a; } }
    """;

    /// <summary>Rules scoped to the guide itself, safe to paste into another page.</summary>
    private const string DocumentCss = """
    .sw-doc { max-width: 860px; margin: 0 auto; padding: 48px 24px 80px;
      font-family: "Segoe UI", system-ui, -apple-system, Roboto, Helvetica, Arial, sans-serif;
      color: #16181d; line-height: 1.55; }
    .sw-head { border-bottom: 1px solid #e3e6ea; padding-bottom: 20px; margin-bottom: 28px; }
    .sw-head h1 { font-size: 32px; line-height: 1.2; margin: 0 0 10px; letter-spacing: 0.2px; }
    .sw-summary { margin: 0 0 10px; font-size: 17px; color: #3c414a; }
    .sw-meta { margin: 0; font-size: 13px; color: #767d88; text-transform: uppercase; letter-spacing: 0.6px; }
    .sw-section { font-size: 20px; margin: 40px 0 8px; color: #16181d; }
    .sw-steps { list-style: none; margin: 0; padding: 0; }
    .sw-step { margin: 0 0 30px; padding: 0; }
    .sw-row { display: flex; align-items: flex-start; gap: 14px; }
    .sw-num { flex: 0 0 auto; width: 30px; height: 30px; border-radius: 50%;
      background: #2563eb; color: #fff; font-size: 15px; font-weight: 650;
      display: flex; align-items: center; justify-content: center; margin-top: 1px; }
    .sw-text { font-size: 17px; font-weight: 550; padding-top: 3px; }
    .sw-note { margin: 8px 0 0 44px; font-size: 15px; color: #4b515c; }
    .sw-shot { display: block; width: 100%; height: auto; margin: 14px 0 0 44px; max-width: calc(100% - 44px);
      border: 1px solid #dfe3e8; border-radius: 10px; box-shadow: 0 2px 10px rgba(15, 20, 30, 0.08); }
    .sw-foot { margin-top: 48px; padding-top: 16px; border-top: 1px solid #e3e6ea;
      font-size: 12px; color: #8b929c; text-align: center; }
    @media (prefers-color-scheme: dark) {
      .sw-doc { color: #e8eaee; }
      .sw-head, .sw-foot { border-color: #2a2e36; }
      .sw-summary { color: #b9bfc9; }
      .sw-note { color: #b0b6c0; }
      .sw-shot { border-color: #2a2e36; box-shadow: none; }
      .sw-section { color: #e8eaee; }
    }
    @media print {
      .sw-doc { max-width: none; padding: 0; background: #fff; }
      .sw-step { break-inside: avoid; page-break-inside: avoid; }
      .sw-shot { box-shadow: none; }
      .sw-foot { display: none; }
    }
    """;
}
