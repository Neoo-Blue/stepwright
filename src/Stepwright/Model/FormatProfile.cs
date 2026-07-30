using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stepwright.Model;

/// <summary>
/// How a guide is turned into markup. Every system that receives a document wants something
/// slightly different, so the rules live in a file that can be edited, shared and swapped
/// rather than being buried in the exporter.
/// </summary>
public sealed class FormatProfile
{
    /// <summary>Name shown in the list. Also the file name when exported.</summary>
    public string Name { get; set; } = "Untitled format";

    public string Description { get; set; } = string.Empty;

    // ---------------------------------------------------------------- shape

    /// <summary>Style rules travel on each element rather than in a stylesheet.</summary>
    public bool InlineStyles { get; set; }

    /// <summary>Everything sits inside one element, which some systems insist on.</summary>
    public bool SingleContainer { get; set; }

    /// <summary>Numbered steps use a list rather than a numbered heading per step.</summary>
    public bool UseOrderedList { get; set; } = true;

    /// <summary>Heading levels are written as h2 and h3 rather than styled text.</summary>
    public bool UseHeadingTags { get; set; } = true;

    /// <summary>Colour is left to the receiving system, for light and dark mode.</summary>
    public bool AllowColor { get; set; } = true;

    public bool IncludeTitle { get; set; } = true;
    public bool IncludeSummary { get; set; } = true;
    public bool IncludeMeta { get; set; } = true;
    public bool IncludeFooter { get; set; } = true;

    // ---------------------------------------------------------------- type

    public string FontFamily { get; set; } = "Segoe UI, system-ui, Helvetica, Arial, sans-serif";
    public int TitleSize { get; set; } = 32;
    public int HeadingSize { get; set; } = 20;
    public int BodySize { get; set; } = 17;
    public int NoteSize { get; set; } = 15;
    public int MetaSize { get; set; } = 13;
    public int BlockSpacing { get; set; } = 12;

    // ---------------------------------------------------------------- steps

    /// <summary>Wording placed before the number, for example "Step ".</summary>
    public string StepPrefix { get; set; } = string.Empty;

    /// <summary>What follows the number when steps are not written as a list.</summary>
    public string StepSuffix { get; set; } = ".";

    public bool BoldStepText { get; set; }

    /// <summary>Wording placed before a note, for example "Note: ".</summary>
    public string NotePrefix { get; set; } = string.Empty;

    // ---------------------------------------------------------------- pictures

    public int ImageWidth { get; set; } = 1400;

    /// <summary>Pictures are compressed rather than kept lossless, which keeps a page small.</summary>
    public bool UseJpeg { get; set; }

    public long JpegQuality { get; set; } = 82;

    /// <summary>Animated steps are written as animations where the format allows it.</summary>
    public bool AllowAnimation { get; set; } = true;

    /// <summary>Pictures travel inside the markup rather than beside it.</summary>
    public bool EmbedImages { get; set; } = true;

    /// <summary>Widest a picture is drawn in the finished page. Zero leaves it alone.</summary>
    public int ImageDisplayWidth { get; set; }

    public bool RoundImageCorners { get; set; } = true;

    /// <summary>
    /// Written in place of each picture instead of the picture itself. The number of the step
    /// replaces {n}. Used by systems that attach pictures separately.
    /// </summary>
    public string ImagePlaceholder { get; set; } = string.Empty;

    // ---------------------------------------------------------------- extras

    /// <summary>Added at the end when a footer is wanted. The date replaces {date}.</summary>
    public string FooterText { get; set; } = "Made with Stepwright";

    /// <summary>Written before everything else, exactly as given.</summary>
    public string Preamble { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    public FormatProfile Copy()
    {
        string json = JsonSerializer.Serialize(this);
        FormatProfile clone = JsonSerializer.Deserialize<FormatProfile>(json) ?? new FormatProfile();
        clone.IsBuiltIn = false;
        return clone;
    }
}

/// <summary>The formats that ship with the app, and the folder holding any others.</summary>
public static class FormatProfiles
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public const string FileExtension = ".swformat";
    public const string FileFilter = "Stepwright format (*.swformat)|*.swformat|JSON file (*.json)|*.json";

    public static string Folder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Stepwright",
            "formats");

    /// <summary>The look the app uses unless told otherwise.</summary>
    public static FormatProfile Standard() => new()
    {
        Name = "Stepwright",
        Description = "The look the app uses by default: a styled page with rounded pictures.",
        IsBuiltIn = true,
        InlineStyles = false,
        SingleContainer = true,
        UseOrderedList = true,
        UseHeadingTags = true,
        AllowColor = true,
        BoldStepText = true,
        RoundImageCorners = true,
    };

    /// <summary>
    /// What Hudu accepts: one container, inline styles, Arial, sixteen point bold headings,
    /// fourteen point body, no tables and no colour so the site controls light and dark mode.
    /// </summary>
    public static FormatProfile Hudu() => new()
    {
        Name = "Hudu",
        Description = "One container, inline styles, Arial, no colour so Hudu controls light and dark mode.",
        IsBuiltIn = true,
        InlineStyles = true,
        SingleContainer = true,
        UseOrderedList = true,
        UseHeadingTags = false,
        AllowColor = false,
        IncludeMeta = false,
        FontFamily = "Arial, sans-serif",
        TitleSize = 16,
        HeadingSize = 16,
        BodySize = 14,
        NoteSize = 14,
        MetaSize = 12,
        BlockSpacing = 12,
        BoldStepText = false,
        NotePrefix = "Note: ",
        UseJpeg = true,
        JpegQuality = 78,
        ImageWidth = 1100,
        ImageDisplayWidth = 700,
        RoundImageCorners = false,
        AllowAnimation = false,
        FooterText = "Published from Stepwright on {date}",
    };

    /// <summary>
    /// Confluence keeps pictures as attachments and refers to them by name, so the markup
    /// carries a reference rather than the picture itself.
    /// </summary>
    public static FormatProfile Confluence() => new()
    {
        Name = "Confluence",
        Description = "Storage format, with pictures attached to the page and referred to by name.",
        IsBuiltIn = true,
        InlineStyles = false,
        SingleContainer = false,
        UseOrderedList = false,
        UseHeadingTags = true,
        AllowColor = false,
        IncludeMeta = true,
        FontFamily = string.Empty,
        BoldStepText = true,
        StepPrefix = string.Empty,
        StepSuffix = ".",
        UseJpeg = true,
        JpegQuality = 80,
        ImageWidth = 1100,
        ImageDisplayWidth = 700,
        AllowAnimation = false,
        RoundImageCorners = false,
        ImagePlaceholder = "<ac:image ac:width=\"700\"><ri:attachment ri:filename=\"step{n}.jpg\" /></ac:image>",
        FooterText = "Published from Stepwright on {date}",
    };

    /// <summary>Markup with nothing added, for pasting into an editor that styles it itself.</summary>
    public static FormatProfile Plain() => new()
    {
        Name = "Plain",
        Description = "Headings, paragraphs and pictures with no styling at all.",
        IsBuiltIn = true,
        InlineStyles = false,
        SingleContainer = false,
        UseOrderedList = true,
        UseHeadingTags = true,
        AllowColor = false,
        IncludeMeta = false,
        IncludeFooter = false,
        FontFamily = string.Empty,
        BoldStepText = true,
        RoundImageCorners = false,
    };

    public static List<FormatProfile> BuiltIn() => new() { Standard(), Hudu(), Confluence(), Plain() };

    /// <summary>Everything that ships with the app plus everything saved beside it.</summary>
    public static List<FormatProfile> All()
    {
        List<FormatProfile> profiles = BuiltIn();

        try
        {
            if (!Directory.Exists(Folder))
            {
                return profiles;
            }

            foreach (string file in Directory.GetFiles(Folder, "*" + FileExtension))
            {
                FormatProfile? loaded = Load(file);
                if (loaded is null)
                {
                    continue;
                }

                // A saved format with the same name replaces the one that ships with the app.
                profiles.RemoveAll(p => p.Name.Equals(loaded.Name, StringComparison.OrdinalIgnoreCase));
                profiles.Add(loaded);
            }
        }
        catch
        {
            // A damaged folder should never stop the app from exporting.
        }

        return profiles;
    }

    public static FormatProfile Find(string? name)
    {
        List<FormatProfile> profiles = All();
        return profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? profiles[0];
    }

    public static FormatProfile? Load(string path)
    {
        try
        {
            FormatProfile? profile = JsonSerializer.Deserialize<FormatProfile>(
                File.ReadAllText(path),
                JsonOptions);

            if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
            {
                return null;
            }

            return profile;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(FormatProfile profile)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, SafeName(profile.Name) + FileExtension), Write(profile));
    }

    public static void Export(FormatProfile profile, string path) => File.WriteAllText(path, Write(profile));

    public static string Write(FormatProfile profile) => JsonSerializer.Serialize(profile, JsonOptions);

    public static void Delete(string name)
    {
        try
        {
            string path = Path.Combine(Folder, SafeName(name) + FileExtension);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Nothing useful to do if the file is locked.
        }
    }

    private static string SafeName(string name)
    {
        string clean = name;
        foreach (char bad in Path.GetInvalidFileNameChars())
        {
            clean = clean.Replace(bad, ' ');
        }

        clean = clean.Trim();
        return clean.Length == 0 ? "format" : clean;
    }
}
