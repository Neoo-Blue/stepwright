using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stepwright.Config;
using Stepwright.Model;

namespace Stepwright.Ai;

/// <summary>
/// Optional writing pass. Talks to any endpoint that speaks the chat completions shape,
/// so it works with a hosted provider or a local model behind a gateway.
/// </summary>
public static class AiPolisher
{
    private const string StyleRules =
        "You rewrite instructions for a step by step guide. "
        + "Return one short imperative sentence per step, in plain English, starting with a verb. "
        + "Keep the exact button, menu and field names in quotes. Keep any typed values. "
        + "Never invent a step, never merge steps, never change the order, never add commentary. "
        + "Never use a hyphen, an en dash or an em dash anywhere in the output. "
        + "Reply with JSON only in the form {\"steps\":[{\"i\":1,\"text\":\"...\"}]}.";

    private sealed class PolishedStep
    {
        [JsonPropertyName("i")]
        public int Index { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    private sealed class PolishedBatch
    {
        [JsonPropertyName("steps")]
        public List<PolishedStep> Steps { get; set; } = new();
    }

    public static async Task<int> PolishAsync(
        Guide guide,
        AppSettings settings,
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

        int changed = 0;
        const int batchSize = 20;

        for (int offset = 0; offset < targets.Count; offset += batchSize)
        {
            token.ThrowIfCancellationRequested();
            List<Step> batch = targets.Skip(offset).Take(batchSize).ToList();
            progress?.Report($"Polishing steps {offset + 1} to {offset + batch.Count} of {targets.Count}");

            var payload = new StringBuilder();
            payload.AppendLine("Guide title: " + guide.Title);
            payload.AppendLine("Rewrite each step below.");
            for (int i = 0; i < batch.Count; i++)
            {
                Step step = batch[i];
                payload.AppendLine();
                payload.AppendLine($"Step {i + 1}");
                payload.AppendLine("  current text: " + step.Text);
                if (!string.IsNullOrWhiteSpace(step.AppName))
                {
                    payload.AppendLine("  application: " + step.AppName);
                }

                if (!string.IsNullOrWhiteSpace(step.ElementName))
                {
                    payload.AppendLine($"  control: {step.ElementName} ({step.ElementType})");
                }

                if (!string.IsNullOrWhiteSpace(step.TypedText))
                {
                    payload.AppendLine("  typed: " + step.TypedText);
                }
            }

            string reply = await CallAsync(settings, StyleRules, payload.ToString(), token).ConfigureAwait(false);
            PolishedBatch? parsed = ParseJson<PolishedBatch>(reply);
            if (parsed is null)
            {
                continue;
            }

            foreach (PolishedStep polished in parsed.Steps)
            {
                int index = polished.Index - 1;
                if (index < 0 || index >= batch.Count || string.IsNullOrWhiteSpace(polished.Text))
                {
                    continue;
                }

                string clean = Clean(polished.Text);
                if (!string.Equals(clean, batch[index].Text, StringComparison.Ordinal))
                {
                    batch[index].Text = clean;
                    changed++;
                }
            }
        }

        return changed;
    }

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
            "Name the procedure described by these steps. "
            + "Reply with JSON only in the form {\"title\":\"...\",\"summary\":\"...\"}. "
            + "The title is at most eight words and starts with a verb ending in ing, "
            + "for example Resetting a password. The summary is one sentence. "
            + "Never use a hyphen, an en dash or an em dash.";

        string reply = await CallAsync(settings, rules, payload.ToString(), token).ConfigureAwait(false);
        using JsonDocument? document = ParseDocument(reply);
        if (document is null)
        {
            return (string.Empty, string.Empty);
        }

        string title = document.RootElement.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
        string summary = document.RootElement.TryGetProperty("summary", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
        return (Clean(title), Clean(summary));
    }

    /// <summary>Sends one tiny request so the settings screen can prove the endpoint works.</summary>
    public static async Task<string> TestAsync(AppSettings settings, CancellationToken token)
    {
        string reply = await CallAsync(
            settings,
            "Reply with the single word ready.",
            "ping",
            token).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(reply) ? "The endpoint answered but sent no text." : reply.Trim();
    }

    private static async Task<string> CallAsync(AppSettings settings, string system, string user, CancellationToken token)
    {
        string key = settings.GetAiKey();
        string baseUrl = settings.AiBaseUrl.TrimEnd('/');

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120),
        };

        if (!string.IsNullOrEmpty(key))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        var request = new
        {
            model = settings.AiModel,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client
            .PostAsync(baseUrl + "/chat/completions", content, token)
            .ConfigureAwait(false);

        string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The writing assistant replied with {(int)response.StatusCode}. {Trim(raw, 300)}");
        }

        using JsonDocument document = JsonDocument.Parse(raw);
        if (document.RootElement.TryGetProperty("choices", out JsonElement choices)
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out JsonElement message)
            && message.TryGetProperty("content", out JsonElement text))
        {
            return text.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static T? ParseJson<T>(string reply)
        where T : class
    {
        string json = ExtractJson(reply);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }

    private static JsonDocument? ParseDocument(string reply)
    {
        string json = ExtractJson(reply);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractJson(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return string.Empty;
        }

        int start = reply.IndexOf('{');
        int end = reply.LastIndexOf('}');
        return start >= 0 && end > start ? reply[start..(end + 1)] : string.Empty;
    }

    private static string Clean(string text)
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

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
