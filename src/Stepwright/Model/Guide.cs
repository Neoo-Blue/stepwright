using System.Text.Json.Serialization;

namespace Stepwright.Model;

public enum StepKind
{
    Click,
    DoubleClick,
    RightClick,
    MiddleClick,
    Drag,
    Type,
    Hotkey,
    Scroll,
    Screenshot,
    Note,
    Heading,
}

public enum AnnotationKind
{
    Rectangle,
    Arrow,
    Blur,
    Highlight,
    Text,
    Number,
}

/// <summary>A plain rectangle that survives a round trip through JSON without surprises.</summary>
public struct RectI
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    public RectI(int x, int y, int w, int h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }

    public static RectI From(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    [JsonIgnore]
    public Rectangle Rect => new(X, Y, W, H);

    [JsonIgnore]
    public bool IsEmpty => W <= 0 || H <= 0;
}

public struct PointI
{
    public int X { get; set; }
    public int Y { get; set; }

    public PointI(int x, int y)
    {
        X = x;
        Y = y;
    }

    public static PointI From(Point p) => new(p.X, p.Y);

    [JsonIgnore]
    public Point Point => new(X, Y);
}

public sealed class Annotation
{
    public AnnotationKind Kind { get; set; } = AnnotationKind.Rectangle;
    public RectI Area { get; set; }

    /// <summary>Hex colour in the form RRGGBB.</summary>
    public string Color { get; set; } = "FF3B30";

    public string Text { get; set; } = string.Empty;
    public int Thickness { get; set; } = 4;
    public int Number { get; set; }
}

public sealed class Step
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public StepKind Kind { get; set; } = StepKind.Click;

    /// <summary>The sentence shown to the reader. Editable.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>What the recorder originally wrote, kept so an edit can be undone.</summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>Optional extra paragraph under the step.</summary>
    public string Notes { get; set; } = string.Empty;

    public DateTime Moment { get; set; } = DateTime.Now;

    /// <summary>File name of the screenshot inside the guide folder. Empty for text only steps.</summary>
    public string Image { get; set; } = string.Empty;

    public PointI? ClickPoint { get; set; }
    public RectI? ElementArea { get; set; }

    /// <summary>The window that was in use, so the picture can be cropped back to it.</summary>
    public RectI? WindowArea { get; set; }

    public RectI? Crop { get; set; }

    public bool ShowClickMarker { get; set; } = true;
    public bool ShowElementOutline { get; set; } = true;
    public bool AutoZoom { get; set; } = true;
    public bool Skip { get; set; }

    public List<Annotation> Annotations { get; set; } = new();

    public string AppName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string ElementName { get; set; } = string.Empty;
    public string ElementType { get; set; } = string.Empty;
    public string TypedText { get; set; } = string.Empty;
    public string Keys { get; set; } = string.Empty;
    public bool Redacted { get; set; }

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrEmpty(Image);

    public Step Clone()
    {
        return new Step
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = Kind,
            Text = Text,
            OriginalText = OriginalText,
            Notes = Notes,
            Moment = Moment,
            Image = Image,
            ClickPoint = ClickPoint,
            ElementArea = ElementArea,
            WindowArea = WindowArea,
            Crop = Crop,
            ShowClickMarker = ShowClickMarker,
            ShowElementOutline = ShowElementOutline,
            AutoZoom = AutoZoom,
            Skip = Skip,
            Annotations = Annotations.Select(a => new Annotation
            {
                Kind = a.Kind,
                Area = a.Area,
                Color = a.Color,
                Text = a.Text,
                Thickness = a.Thickness,
                Number = a.Number,
            }).ToList(),
            AppName = AppName,
            WindowTitle = WindowTitle,
            ElementName = ElementName,
            ElementType = ElementType,
            TypedText = TypedText,
            Keys = Keys,
            Redacted = Redacted,
        };
    }
}

public sealed class Guide
{
    public string FormatVersion { get; set; } = "1";
    public string Title { get; set; } = "Untitled guide";
    public string Summary { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public List<Step> Steps { get; set; } = new();

    /// <summary>Folder holding the screenshots referenced by the steps.</summary>
    [JsonIgnore]
    public string MediaFolder { get; set; } = string.Empty;

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public bool Dirty { get; set; }

    public string ImagePath(Step step) =>
        step.HasImage && !string.IsNullOrEmpty(MediaFolder)
            ? Path.Combine(MediaFolder, step.Image)
            : string.Empty;

    /// <summary>Ignored when saving, otherwise every step would be written to the file twice.</summary>
    [JsonIgnore]
    public IEnumerable<Step> Visible => Steps.Where(s => !s.Skip);
}
