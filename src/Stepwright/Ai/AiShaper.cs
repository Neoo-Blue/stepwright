using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stepwright.Config;
using Stepwright.Model;

namespace Stepwright.Ai;

/// <summary>What the shaping pass did, so a person can be told and can put it back.</summary>
public sealed class ShapeResult
{
    /// <summary>Steps that were folded into the step before them.</summary>
    public int Folded { get; set; }

    /// <summary>Steps that turned into two, because they held two actions.</summary>
    public int Split { get; set; }

    /// <summary>Steps set aside as carrying nothing a reader needs.</summary>
    public int Hidden { get; set; }

    public bool Changed => Folded > 0 || Split > 0 || Hidden > 0;

    public string Describe()
    {
        var parts = new List<string>();

        if (Folded > 0)
        {
            parts.Add($"folded {Folded} steps into the ones before them");
        }

        if (Split > 0)
        {
            parts.Add($"split {Split} steps that held two actions");
        }

        if (Hidden > 0)
        {
            parts.Add($"set {Hidden} steps aside");
        }

        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }
}

/// <summary>
/// The second half of the assistant. The rewriting pass fixes the words of a step; this one
/// fixes how many steps there are.
///
/// A recorder writes one step per action, which is finer than any reader wants. Opening a tab,
/// typing an address and pressing Enter are three recorded actions and one instruction. This
/// pass decides which runs of steps are really one, which single step is really two, and which
/// carry nothing at all. Nothing is deleted: a step that is folded away or left out is set
/// aside, so it stays in the editor and can be brought back.
/// </summary>
public static class AiShaper
{
    /// <summary>How many steps are considered at once. Folding only happens inside a batch.</summary>
    private const int BatchSize = 30;

    private const string Rules =
        "You are editing a step by step guide so that someone can follow it quickly. "
        + "A recorder wrote one step for every action, which is finer than a reader wants. "
        + "Your job is the shape of the guide, not only the words.\n"
        + "Fold a run of steps into one when a reader would do them as a single action. "
        + "Opening a tab, typing an address and pressing Enter is one step: go to that address. "
        + "Clicking a menu and then the item inside it is one step. Typing into a field and "
        + "pressing Enter is one step.\n"
        + "Split a step into two when it holds two actions the reader has to do separately.\n"
        + "Leave a step out when it carries nothing a reader has to do. That includes a stray "
        + "click, a repeat of the step before it, and opening something the next step opens "
        + "anyway. It also includes anything a later step undoes or replaces: a value that was "
        + "typed and then typed again differently, a search that was abandoned, a page that was "
        + "opened and then left without doing anything there. The person was working, not "
        + "performing, and their false starts are not instructions.\n"
        + "The title and the summary say what the guide is for. A step that does not carry the "
        + "reader towards that is noise, however carefully it was recorded.\n"
        + "Keep the order. Only fold steps that sit next to each other.\n"
        + "Every value the person typed and every keyboard shortcut must survive somewhere, "
        + "unless the step it came from is left out as noise. Aim for the shortest guide that "
        + "still tells the reader everything they have to do.\n"
        + "Write one short sentence per step, in the imperative, starting with a verb, naming "
        + "the exact button, link, menu or field in quotes. "
        + "Never use a hyphen, an en dash or an em dash anywhere in your answer.\n"
        + "Reply with JSON only, in this form:\n"
        + "{\"steps\":[{\"from\":[1,2,3],\"picture\":3,\"text\":\"...\",\"note\":\"...\"}],\"leaveOut\":[7]}\n"
        + "from lists the numbers of the original steps this one is made of, in order. "
        + "picture says which of them to show, normally the last, or the one where the action "
        + "actually landed. To split a step, give two entries with the same single from. "
        + "leaveOut lists steps that carry nothing. Every number must appear once, either in a "
        + "from list or in leaveOut.";

    /// <summary>
    /// Reshapes the guide in place. Headings are left where they are, because they are the
    /// person's own structure rather than something the recorder guessed at.
    /// </summary>
    public static async Task<ShapeResult> ShapeAsync(
        Guide guide,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken token)
    {
        var result = new ShapeResult();

        List<Step> targets = guide.Steps
            .Where(s => s.Kind != StepKind.Heading && !s.Skip && !string.IsNullOrWhiteSpace(s.Text))
            .ToList();

        if (targets.Count < 2)
        {
            return result;
        }

        for (int offset = 0; offset < targets.Count; offset += BatchSize)
        {
            token.ThrowIfCancellationRequested();

            List<Step> batch = targets.Skip(offset).Take(BatchSize).ToList();
            progress?.Report($"Deciding the shape of steps {offset + 1} to {offset + batch.Count}...");

            var payload = new StringBuilder();
            payload.AppendLine("Guide title: " + guide.Title);
            payload.AppendLine($"These are steps {offset + 1} to {offset + batch.Count} of {targets.Count}.");
            payload.AppendLine("Number them from 1 as they appear below.");

            for (int i = 0; i < batch.Count; i++)
            {
                payload.AppendLine();
                payload.AppendLine($"Step {i + 1}");
                Describe(payload, batch[i]);
            }

            string reply = await AiClient
                .CompleteAsync(settings, Rules, payload.ToString(), null, token)
                .ConfigureAwait(true);

            Apply(guide, batch, reply, result);
        }

        return result;
    }

    private static void Describe(StringBuilder payload, Step step)
    {
        payload.AppendLine("  " + step.Text);

        if (!string.IsNullOrWhiteSpace(step.AppName))
        {
            payload.AppendLine("  application: " + step.AppName);
        }

        if (!string.IsNullOrWhiteSpace(step.WindowTitle))
        {
            payload.AppendLine("  window: " + step.WindowTitle);
        }

        if (!string.IsNullOrWhiteSpace(step.TypedText))
        {
            payload.AppendLine("  text typed: " + step.TypedText);
        }

        if (!string.IsNullOrWhiteSpace(step.Keys))
        {
            payload.AppendLine("  keys pressed: " + step.Keys);
        }

        if (!string.IsNullOrWhiteSpace(step.Notes))
        {
            payload.AppendLine("  note underneath: " + step.Notes);
        }

        if (step.Redacted)
        {
            payload.AppendLine("  this step is a secret value and must stay hidden");
        }
    }

    /// <summary>
    /// Carries out the plan against the real guide. Everything here is deliberately cautious:
    /// a plan that does not add up is ignored rather than half applied, and no step is ever
    /// removed from the list, only set aside.
    /// </summary>
    private static void Apply(Guide guide, List<Step> batch, string reply, ShapeResult result)
    {
        JsonNode? plan = Parse(reply);

        if (plan?["steps"] is not JsonArray entries)
        {
            return;
        }

        // Numbers the plan spoke about. Anything it forgot is left exactly as it was.
        var spoken = new HashSet<int>();
        var work = new List<(Step Primary, List<Step> Folded, string Text, string Note, bool Clone)>();

        // Read first, because a fold is allowed to reach over steps that are being left out.
        var leaving = new HashSet<int>();

        if (plan["leaveOut"] is JsonArray listed)
        {
            foreach (JsonNode? number in listed)
            {
                int value = number?.GetValue<int>() ?? 0;

                if (value >= 1 && value <= batch.Count)
                {
                    leaving.Add(value);
                }
            }
        }

        foreach (JsonNode? entry in entries)
        {
            if (entry?["from"] is not JsonArray from || from.Count == 0)
            {
                continue;
            }

            var numbers = new List<int>();
            foreach (JsonNode? number in from)
            {
                if (number?.GetValue<int>() is int value && value >= 1 && value <= batch.Count)
                {
                    numbers.Add(value);
                }
            }

            if (numbers.Count != from.Count || numbers.Count == 0)
            {
                continue;
            }

            // A fold has to read forwards, and may only reach over steps that are being left
            // out anyway. Anything else would quietly reorder the guide, and a guide in the
            // wrong order is worse than a long one.
            bool forwards = numbers.Zip(numbers.Skip(1), (a, b) => b > a).All(next => next);

            bool overNothing = numbers.Count < 2
                || Enumerable.Range(numbers[0], numbers[^1] - numbers[0] + 1)
                    .All(n => numbers.Contains(n) || leaving.Contains(n));

            if (!forwards || !overNothing)
            {
                continue;
            }

            int chosen = entry["picture"]?.GetValue<int>() ?? numbers[^1];
            if (!numbers.Contains(chosen))
            {
                chosen = numbers[^1];
            }

            bool split = numbers.Count == 1 && spoken.Contains(numbers[0]);

            work.Add((
                batch[chosen - 1],
                numbers.Where(n => n != chosen).Select(n => batch[n - 1]).ToList(),
                Tidy(entry["text"]?.GetValue<string>() ?? string.Empty),
                Tidy(entry["note"]?.GetValue<string>() ?? string.Empty),
                split));

            foreach (int number in numbers)
            {
                spoken.Add(number);
            }
        }

        foreach (var (primary, folded, text, note, clone) in work)
        {
            if (clone)
            {
                // A step that became two: the second half is a copy, so both halves keep the
                // same picture and marker.
                Step copy = primary.Clone();
                copy.Text = text;
                copy.Notes = note;

                int at = guide.Steps.IndexOf(primary);
                guide.Steps.Insert(at < 0 ? guide.Steps.Count : at + 1, copy);
                result.Split++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                primary.Text = text;
            }

            primary.Notes = note;
            primary.Skip = false;

            foreach (Step gone in folded)
            {
                gone.Skip = true;
                result.Folded++;
            }
        }

        foreach (int value in leaving.Where(v => !spoken.Contains(v)))
        {
            batch[value - 1].Skip = true;
            result.Hidden++;
        }
    }

    private static JsonNode? Parse(string reply)
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
        string result = text.Trim()
            .Replace(" — ", ", ", StringComparison.Ordinal)
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
