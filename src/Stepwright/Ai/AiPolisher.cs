using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stepwright.Config;
using Stepwright.Model;

namespace Stepwright.Ai;

/// <summary>
/// The optional writing pass. It rewrites the sentence the recorder produced and, where it
/// genuinely helps, adds a short note underneath.
///
/// The recorder can only name a control as well as the application describes itself, which
/// in a browser is often not at all. When the person allows it, the assistant is shown the
/// screenshot for the step, marker and all, which is what lets it say what is really there.
/// </summary>
public static class AiPolisher
{
    private const string HouseStyle =
        "You write instructions for a step by step guide that someone will follow at a computer. "
        + "Write one short sentence in the imperative, starting with a verb. "
        + "Name the exact button, link, menu or field, in quotes, using the words that appear on screen. "
        + "Do not invent a step, do not merge steps, do not change the order. "
        + "A value that was typed is not always a value the reader should type. Keep it exactly when it belongs to the task and the reader must enter the same thing: an address, a command, a setting, a search, the name of something already in the system. When it was the person's own particulars, tell the reader to enter their own rather than copying someone else's: a name, a date of birth, an email address they are choosing, a phone number, a password. Name the field either way. "
        + "Never use a hyphen, an en dash or an em dash anywhere in your answer.";

    private const string NoteStyle =
        "A note is optional. Add one only when it genuinely helps the reader: a warning, "
        + "something to have ready first, or what they should expect to happen. "
        + "Leave the note empty when the step speaks for itself. Never restate the step. "
        + "Never write a note the next step contradicts: if you are told what comes next, a "
        + "note saying a field is optional is wrong when the next step fills it in.";

    /// <summary>
    /// Rewrites every step. Returns how many were changed.
    /// The picture callback is asked for a screenshot only when that setting is on.
    /// </summary>
    public static async Task<int> ImproveAsync(
        Guide guide,
        AppSettings settings,
        Func<Step, byte[]?> pictureFor,
        IProgress<string>? progress,
        CancellationToken token)
    {
        List<Step> targets = guide.Steps
            .Where(s => s.Kind != StepKind.Heading && !string.IsNullOrWhiteSpace(s.Text))
            .ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        // Not every service can be shown a picture. Copilot takes text and nothing else, so the
        // switch is honoured where it can be and the text pass is used where it cannot.
        return settings.AiSendScreenshots && AiProviders.Find(settings.AiProvider).SupportsPictures
            ? await WithPicturesAsync(guide, settings, targets, pictureFor, progress, token).ConfigureAwait(true)
            : await FromTextAsync(guide, settings, targets, progress, token).ConfigureAwait(true);
    }

    /// <summary>One request per step, each carrying the picture of that step.</summary>
    private static async Task<int> WithPicturesAsync(
        Guide guide,
        AppSettings settings,
        List<Step> targets,
        Func<Step, byte[]?> pictureFor,
        IProgress<string>? progress,
        CancellationToken token)
    {
        int changed = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            Step step = targets[i];
            progress?.Report($"Looking at step {i + 1} of {targets.Count}");

            byte[]? picture = pictureFor(step);
            var pictures = picture is null ? null : new[] { picture };

            string system = HouseStyle
                + " You are shown the screenshot taken at the moment of the action. "
                + "A red circle marks where the click landed, and a red outline marks the control that was used. "
                + "Trust what you can see in the picture over the description you are given. "
                + (settings.AiWriteNotes ? NoteStyle + " " : "Leave the note empty. ")
                + "Reply with JSON only in the form {\"text\":\"...\",\"note\":\"...\"}.";

            var context = new StringBuilder();
            context.AppendLine("Guide title: " + guide.Title);
            context.AppendLine("This is step " + (i + 1) + " of " + targets.Count + ".");
            AppendContext(context, step);

            // What happens next decides whether a note is true. Without it the assistant writes
            // things like "this field is optional" on the step before the one that fills it in.
            if (i + 1 < targets.Count)
            {
                context.AppendLine();
                context.AppendLine("The next step is: " + targets[i + 1].Text);
            }

            string reply;
            try
            {
                reply = await AiClient
                    .CompleteAsync(settings, system, context.ToString(), pictures, token)
                    .ConfigureAwait(true);
            }
            catch (Exception) when (i > 0)
            {
                // Keep whatever has already been improved rather than losing the whole run.
                progress?.Report($"Stopped after {i} steps. The assistant could not be reached.");
                return changed;
            }

            if (Apply(step, Parse(reply), settings))
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>Batched requests using only the text the recorder captured.</summary>
    private static async Task<int> FromTextAsync(
        Guide guide,
        AppSettings settings,
        List<Step> targets,
        IProgress<string>? progress,
        CancellationToken token)
    {
        int changed = 0;
        const int batchSize = 15;

        string system = HouseStyle + " "
            + (settings.AiWriteNotes ? NoteStyle + " " : "Leave every note empty. ")
            + "Reply with JSON only in the form {\"steps\":[{\"i\":1,\"text\":\"...\",\"note\":\"...\"}]}.";

        for (int offset = 0; offset < targets.Count; offset += batchSize)
        {
            token.ThrowIfCancellationRequested();
            List<Step> batch = targets.Skip(offset).Take(batchSize).ToList();
            progress?.Report($"Rewriting steps {offset + 1} to {offset + batch.Count} of {targets.Count}");

            var payload = new StringBuilder();
            payload.AppendLine("Guide title: " + guide.Title);
            payload.AppendLine("Rewrite each of these steps.");

            for (int i = 0; i < batch.Count; i++)
            {
                payload.AppendLine();
                payload.AppendLine($"Step {i + 1}");
                AppendContext(payload, batch[i]);
            }

            string reply = await AiClient
                .CompleteAsync(settings, system, payload.ToString(), null, token)
                .ConfigureAwait(true);

            JsonNode? parsed = ParseNode(reply);
            if (parsed?["steps"] is not JsonArray steps)
            {
                continue;
            }

            foreach (JsonNode? entry in steps)
            {
                int index = (entry?["i"]?.GetValue<int>() ?? 0) - 1;
                if (index < 0 || index >= batch.Count)
                {
                    continue;
                }

                if (Apply(batch[index], Read(entry), settings))
                {
                    changed++;
                }
            }
        }

        return changed;
    }

    private static void AppendContext(StringBuilder payload, Step step)
    {
        payload.AppendLine("  what the recorder wrote: " + step.Text);

        if (!string.IsNullOrWhiteSpace(step.AppName))
        {
            payload.AppendLine("  application: " + step.AppName);
        }

        if (!string.IsNullOrWhiteSpace(step.WindowTitle))
        {
            payload.AppendLine("  window: " + step.WindowTitle);
        }

        if (!string.IsNullOrWhiteSpace(step.ElementName))
        {
            payload.AppendLine($"  control the system reported: {step.ElementName} ({step.ElementType})");
        }

        if (!string.IsNullOrWhiteSpace(step.TypedText))
        {
            payload.AppendLine("  text typed: " + step.TypedText);
        }

        if (!string.IsNullOrWhiteSpace(step.Keys))
        {
            payload.AppendLine("  keys pressed: " + step.Keys);
        }

        if (step.Redacted)
        {
            payload.AppendLine("  this step is a secret value and must stay hidden");
        }
    }

    private static bool Apply(Step step, (string Text, string Note) result, AppSettings settings)
    {
        bool changed = false;

        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            string clean = Tidy(result.Text);
            if (!string.Equals(clean, step.Text, StringComparison.Ordinal))
            {
                step.Text = clean;
                changed = true;
            }
        }

        if (settings.AiWriteNotes && !string.IsNullOrWhiteSpace(result.Note))
        {
            string note = Tidy(result.Note);
            if (!string.Equals(note, step.Notes, StringComparison.Ordinal))
            {
                step.Notes = note;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Writes the wording for steps that have a picture and nothing else, which is what a
    /// person has after dropping a folder of screenshots in. There is no recorder text to
    /// improve here, so the picture is the whole of the evidence, and the step before it is
    /// given as context so the guide reads as one piece of writing rather than a list of
    /// captions.
    /// </summary>
    public static async Task<int> DraftAsync(
        Guide guide,
        AppSettings settings,
        Func<Step, byte[]?> pictureFor,
        IProgress<string>? progress,
        CancellationToken token)
    {
        if (!AiProviders.Find(settings.AiProvider).SupportsPictures)
        {
            throw new InvalidOperationException(
                AiProviders.Find(settings.AiProvider).Label
                + " cannot be shown a picture, so it cannot write the wording for one."
                + " Write these steps yourself, or choose a service that reads pictures.");
        }

        List<Step> targets = guide.Steps
            .Where(s => s.Kind != StepKind.Heading && s.HasImage && !s.Skip)
            .ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        int written = 0;
        string previous = string.Empty;

        for (int i = 0; i < targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            Step step = targets[i];
            progress?.Report($"Reading picture {i + 1} of {targets.Count}");

            byte[]? picture = pictureFor(step);

            if (picture is null)
            {
                continue;
            }

            string system = HouseStyle
                + " You are shown a screenshot from a procedure, and nothing else. Work out"
                + " what the person did on this screen and write that as the instruction."
                + " Say what is actually on the screen, using the words that appear in it."
                + " Where an obvious control is the point of the picture, name it. Where the"
                + " picture is a result rather than an action, say what the reader should see. "
                + (settings.AiWriteNotes ? NoteStyle + " " : "Leave the note empty. ")
                + "Reply with JSON only in the form {\"text\":\"...\",\"note\":\"...\"}.";

            var context = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(guide.Title) && guide.Title != "Untitled guide")
            {
                context.AppendLine("Guide title: " + guide.Title);
            }

            if (!string.IsNullOrWhiteSpace(guide.Summary))
            {
                context.AppendLine("What the guide is for: " + guide.Summary);
            }

            context.AppendLine($"This is picture {i + 1} of {targets.Count}.");

            if (previous.Length > 0)
            {
                context.AppendLine("The step before this one reads: " + previous);
            }

            if (!string.IsNullOrWhiteSpace(step.Text) && step.Text != Blank)
            {
                context.AppendLine("The person has already written: " + step.Text);
                context.AppendLine("Keep what they meant and tidy the wording.");
            }

            string reply;

            try
            {
                reply = await AiClient
                    .CompleteAsync(settings, system, context.ToString(), new[] { picture }, token)
                    .ConfigureAwait(true);
            }
            catch (Exception) when (i > 0)
            {
                progress?.Report($"Stopped after {i} pictures. The assistant could not be reached.");
                return written;
            }

            (string text, string note) = Parse(reply);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            step.Text = Tidy(text);
            step.OriginalText = step.Text;
            previous = step.Text;

            if (settings.AiWriteNotes && !string.IsNullOrWhiteSpace(note))
            {
                step.Notes = Tidy(note);
            }

            written++;
        }

        return written;
    }

    /// <summary>What a picture step says until somebody writes something better.</summary>
    public const string Blank = "Say what happens on this screen.";

    /// <summary>Suggests a title and a one line summary from the steps.</summary>
    public static async Task<(string Title, string Summary)> SuggestHeadingAsync(
        Guide guide,
        AppSettings settings,
        CancellationToken token)
    {
        var payload = new StringBuilder();
        payload.AppendLine("These are the steps of a guide:");

        int number = 0;
        foreach (Step step in guide.Visible.Take(40))
        {
            number++;
            payload.AppendLine($"{number}. {step.Text}");
        }

        const string rules =
            "Name the procedure these steps describe. "
            + "Reply with JSON only in the form {\"title\":\"...\",\"summary\":\"...\"}. "
            + "The title is at most eight words and starts with a verb ending in ing, "
            + "for example Resetting a password. The summary is one sentence saying who would "
            + "follow this and why. Never use a hyphen, an en dash or an em dash.";

        string reply = await AiClient
            .CompleteAsync(settings, rules, payload.ToString(), null, token)
            .ConfigureAwait(true);

        JsonNode? node = ParseNode(reply);
        return (
            Tidy(node?["title"]?.GetValue<string>() ?? string.Empty),
            Tidy(node?["summary"]?.GetValue<string>() ?? string.Empty));
    }

    /// <summary>Sends one tiny request so the settings screen can prove the endpoint works.</summary>
    public static async Task<string> TestAsync(AppSettings settings, CancellationToken token)
    {
        string reply = await AiClient
            .CompleteAsync(settings, "Reply with the single word ready.", "ping", null, token)
            .ConfigureAwait(true);

        return string.IsNullOrWhiteSpace(reply) ? "The endpoint answered but sent no text." : reply.Trim();
    }

    private static (string Text, string Note) Parse(string reply) => Read(ParseNode(reply));

    private static (string Text, string Note) Read(JsonNode? node) =>
        node is null
            ? (string.Empty, string.Empty)
            : (node["text"]?.GetValue<string>() ?? string.Empty, node["note"]?.GetValue<string>() ?? string.Empty);

    private static JsonNode? ParseNode(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        int start = reply.IndexOf('{');
        int end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(reply[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Tidy(string text)
    {
        string result = text.Trim();

        // The house style forbids dashes, so any that slip through are rewritten.
        result = result.Replace(" — ", ", ", StringComparison.Ordinal)
            .Replace(" – ", ", ", StringComparison.Ordinal)
            .Replace("—", " ", StringComparison.Ordinal)
            .Replace("–", " ", StringComparison.Ordinal);

        while (result.Contains("  ", StringComparison.Ordinal))
        {
            result = result.Replace("  ", " ", StringComparison.Ordinal);
        }

        return result;
    }
}
