using Stepwright.Config;
using Stepwright.Export.Pdf;
using Stepwright.Model;
using Stepwright.Render;

namespace Stepwright.Export;

/// <summary>
/// Turns a guide into a document. The pictures are placed as jpeg exactly as they were
/// compressed, and the text font is embedded, so the result looks the same everywhere and
/// stays selectable and searchable.
///
/// All the layout lives in <see cref="PdfGuideLayout"/>, which has no platform dependency.
/// This file only gathers the pictures and the font.
/// </summary>
public static class PdfExporter
{
    private static readonly (string Regular, string Bold, string Name)[] PreferredFonts =
    {
        ("segoeui.ttf", "segoeuib.ttf", "SegoeUI"),
        ("arial.ttf", "arialbd.ttf", "Arial"),
        ("tahoma.ttf", "tahomabd.ttf", "Tahoma"),
        ("verdana.ttf", "verdanab.ttf", "Verdana"),
        ("calibri.ttf", "calibrib.ttf", "Calibri"),
    };

    public static void Export(
        Guide guide,
        AppSettings settings,
        string path,
        int maxImageWidth = 1500,
        long imageQuality = 82)
    {
        var header = new PdfGuideHeader
        {
            Title = guide.Title,
            Summary = guide.Summary,
            Author = guide.Author,
            DateLine = guide.Updated.ToString("d MMMM yyyy"),
            StepCount = guide.Visible.Count(s => s.Kind != StepKind.Heading),
        };

        var items = new List<PdfGuideItem>();
        int number = 0;

        foreach (Step step in guide.Visible)
        {
            if (step.Kind == StepKind.Heading)
            {
                items.Add(new PdfGuideItem { IsHeading = true, Text = step.Text });
                continue;
            }

            number++;
            items.Add(new PdfGuideItem
            {
                Number = number,
                Text = step.Text,
                Note = step.Notes,
                Picture = step.HasImage
                    ? GuideRenderer.RenderJpeg(guide, step, settings, maxImageWidth, imageQuality)
                    : null,
            });
        }

        (byte[]? regular, byte[]? bold, string name) = LoadFonts();
        File.WriteAllBytes(path, PdfGuideLayout.Build(header, items, regular, bold, name));
    }

    /// <summary>
    /// Reads a face to embed. Anything that fails simply leaves the document using a
    /// standard font that every reader already carries.
    /// </summary>
    private static (byte[]? Regular, byte[]? Bold, string Name) LoadFonts()
    {
        string folder;
        try
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        }
        catch
        {
            return (null, null, "Fallback");
        }

        if (string.IsNullOrEmpty(folder))
        {
            return (null, null, "Fallback");
        }

        foreach ((string regularName, string boldName, string name) in PreferredFonts)
        {
            byte[]? regular = TryRead(Path.Combine(folder, regularName));
            if (regular is null)
            {
                continue;
            }

            byte[]? bold = TryRead(Path.Combine(folder, boldName));
            return (regular, bold, name);
        }

        return (null, null, "Fallback");
    }

    private static byte[]? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
