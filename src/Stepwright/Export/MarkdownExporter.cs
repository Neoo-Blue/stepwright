using System.Text;
using Stepwright.Config;
using Stepwright.Model;
using Stepwright.Render;

namespace Stepwright.Export;

public static class MarkdownExporter
{
    /// <summary>
    /// Writes the guide as Markdown. Screenshots land in a folder beside the file
    /// unless a folder is left out, in which case the text is written on its own.
    /// </summary>
    public static void Export(Guide guide, AppSettings settings, string path, bool writeImages = true, int maxImageWidth = 1400)
    {
        string root = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        string folderName = Path.GetFileNameWithoutExtension(path) + " images";
        string imageFolder = Path.Combine(root, folderName);

        var text = new StringBuilder();
        text.AppendLine("# " + guide.Title);
        text.AppendLine();

        if (!string.IsNullOrWhiteSpace(guide.Summary))
        {
            text.AppendLine(guide.Summary);
            text.AppendLine();
        }

        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(guide.Author))
        {
            facts.Add("By " + guide.Author);
        }

        facts.Add(guide.Updated.ToString("d MMMM yyyy"));
        text.AppendLine("*" + string.Join(" · ", facts) + "*");
        text.AppendLine();

        int number = 0;
        foreach (Step step in guide.Visible)
        {
            if (step.Kind == StepKind.Heading)
            {
                text.AppendLine();
                text.AppendLine("## " + step.Text);
                text.AppendLine();
                continue;
            }

            number++;
            text.AppendLine($"**Step {number}.** {step.Text}");
            text.AppendLine();

            if (!string.IsNullOrWhiteSpace(step.Notes))
            {
                text.AppendLine("> " + step.Notes.Replace("\n", "\n> ", StringComparison.Ordinal));
                text.AppendLine();
            }

            if (writeImages && step.HasImage)
            {
                byte[]? bytes = GuideRenderer.RenderPng(guide, step, settings, maxImageWidth);
                if (bytes is not null)
                {
                    Directory.CreateDirectory(imageFolder);
                    string name = $"step{number:D3}.png";
                    File.WriteAllBytes(Path.Combine(imageFolder, name), bytes);
                    string relative = Uri.EscapeDataString(folderName) + "/" + name;
                    text.AppendLine($"![Step {number}]({relative})");
                    text.AppendLine();
                }
            }
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Plain text version with no pictures, handy for a chat message or a ticket.</summary>
    public static string BuildPlain(Guide guide)
    {
        var text = new StringBuilder();
        text.AppendLine(guide.Title);
        text.AppendLine(new string('=', Math.Min(60, Math.Max(4, guide.Title.Length))));
        text.AppendLine();

        if (!string.IsNullOrWhiteSpace(guide.Summary))
        {
            text.AppendLine(guide.Summary);
            text.AppendLine();
        }

        int number = 0;
        foreach (Step step in guide.Visible)
        {
            if (step.Kind == StepKind.Heading)
            {
                text.AppendLine();
                text.AppendLine(step.Text.ToUpperInvariant());
                text.AppendLine();
                continue;
            }

            number++;
            text.AppendLine($"{number}. {step.Text}");
            if (!string.IsNullOrWhiteSpace(step.Notes))
            {
                text.AppendLine("   " + step.Notes);
            }
        }

        return text.ToString();
    }
}
