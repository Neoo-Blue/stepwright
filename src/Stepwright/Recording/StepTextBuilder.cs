using Stepwright.Capture;
using Stepwright.Model;

namespace Stepwright.Recording;

/// <summary>
/// Turns a raw interaction into the sentence a reader will follow.
/// House style: plain imperative English, no dashes anywhere.
/// </summary>
public static class StepTextBuilder
{
    private static readonly HashSet<string> VagueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "pane", "custom", "group", "window", "document", "unknown", "client", "dialog", "form",
        "toolbar", "region", "section", "generic", "grouping", "landmark", "",
    };

    /// <summary>
    /// Endings a browser or a document application adds to its window title. They are noise
    /// in a step, because the application is named separately.
    /// </summary>
    private static readonly string[] TitleTails =
    {
        " and Google Chrome", " Google Chrome", " Mozilla Firefox", " Microsoft Edge",
        " Personal Microsoft Edge", " Work Microsoft Edge", " Brave", " Opera", " Microsoft Word",
        " Microsoft Excel", " Microsoft PowerPoint", " Visual Studio Code",
    };

    public static string Describe(StepKind kind, ElementInfo element, string appContext, string extra = "")
    {
        string target = DescribeTarget(element);
        string place = string.IsNullOrEmpty(appContext) ? string.Empty : " in " + appContext;

        return kind switch
        {
            StepKind.Click => $"Click {target}{place}.",
            StepKind.DoubleClick => $"Double click {target}{place}.",
            StepKind.RightClick => $"Right click {target}{place}.",
            StepKind.MiddleClick => $"Middle click {target}{place}.",
            StepKind.Drag => $"Drag {target} {extra}{place}.".Replace("  ", " ", StringComparison.Ordinal),
            StepKind.Scroll => $"Scroll {extra}{place}.",
            StepKind.Type => DescribeTyping(element, extra, place),
            StepKind.Hotkey => $"Press {extra}{place}.",
            StepKind.Screenshot => "Review this screen.",
            StepKind.Heading => appContext,
            _ => "Continue.",
        };
    }

    private static string DescribeTyping(ElementInfo element, string typed, string place)
    {
        string field = FieldName(element);
        if (string.IsNullOrEmpty(typed))
        {
            return $"Enter your details{field}{place}.";
        }

        return $"Type {Quote(typed)}{field}{place}.";
    }

    public static string DescribeRedactedTyping(ElementInfo element, string place)
    {
        string field = FieldName(element);
        return string.IsNullOrEmpty(field)
            ? $"Enter your password{place}."
            : $"Enter your password{field}{place}.";
    }

    private static string FieldName(ElementInfo element)
    {
        if (!element.HasName)
        {
            return string.Empty;
        }

        string name = Shorten(element.Name, 48);
        string type = string.IsNullOrWhiteSpace(element.ControlType) || VagueTypes.Contains(element.ControlType)
            ? "field"
            : element.ControlType.ToLowerInvariant();

        return $" in the {Quote(name)} {type}";
    }

    public static string DescribeTarget(ElementInfo element)
    {
        string type = element.ControlType?.ToLowerInvariant() ?? string.Empty;
        bool usefulType = !VagueTypes.Contains(type);

        if (element.HasName)
        {
            string name = Quote(Shorten(element.Name, 60));
            return usefulType ? $"{name} {type}" : name;
        }

        if (usefulType)
        {
            return $"the {type}";
        }

        return "the highlighted spot";
    }

    public static string DescribeScrollDirection(int delta, bool horizontal)
    {
        if (horizontal)
        {
            return delta < 0 ? "left" : "right";
        }

        return delta < 0 ? "down" : "up";
    }

    public static string AppContext(ElementInfo element)
    {
        if (!string.IsNullOrWhiteSpace(element.AppName))
        {
            return element.AppName;
        }

        if (!string.IsNullOrWhiteSpace(element.WindowTitle))
        {
            return Shorten(TrimTitle(element.WindowTitle), 40);
        }

        return string.Empty;
    }

    /// <summary>Removes the application name a window title carries on the end.</summary>
    public static string TrimTitle(string title)
    {
        string clean = title.Trim();

        // Titles arrive with several separators, so they are levelled first.
        string flat = clean.Replace(" — ", " ", StringComparison.Ordinal)
            .Replace(" – ", " ", StringComparison.Ordinal)
            .Replace(" - ", " ", StringComparison.Ordinal);

        foreach (string tail in TitleTails)
        {
            if (flat.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                flat = flat[..^tail.Length].TrimEnd();
                break;
            }
        }

        int more = flat.IndexOf(" and ", StringComparison.OrdinalIgnoreCase);
        if (more > 12 && flat.EndsWith("more pages", StringComparison.OrdinalIgnoreCase))
        {
            flat = flat[..more].TrimEnd();
        }

        return flat.Length == 0 ? clean : flat;
    }

    public static string Shorten(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string clean = value.Trim();
        if (clean.Length <= max)
        {
            return clean;
        }

        // Cutting between the halves of a surrogate pair would leave an invalid character
        // in the step text, which then travels into every export.
        int cut = max;
        if (char.IsHighSurrogate(clean[cut - 1]))
        {
            cut--;
        }

        return clean[..cut].TrimEnd() + "...";
    }

    private static string Quote(string value) => "“" + value + "”";
}
