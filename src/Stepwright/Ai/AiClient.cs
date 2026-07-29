using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stepwright.Config;

namespace Stepwright.Ai;

/// <summary>
/// One request shape per service. The three hosted services each want something different,
/// so the differences are handled here and nothing above this file has to care which one is
/// in use. Pictures are optional and only ever sent when the person turned that on.
/// </summary>
public static class AiClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(180),
    };

    public static async Task<string> CompleteAsync(
        AppSettings settings,
        string system,
        string user,
        IReadOnlyList<byte[]>? pictures,
        CancellationToken token)
    {
        string provider = settings.AiProvider?.ToLowerInvariant() ?? AiProviders.OpenAi;
        string key = settings.GetAiKey();
        string baseUrl = (settings.AiBaseUrl ?? string.Empty).TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("The assistant has no address to call. Check Settings.");
        }

        return provider switch
        {
            AiProviders.Anthropic => await AnthropicAsync(baseUrl, key, settings.AiModel, system, user, pictures, token).ConfigureAwait(false),
            AiProviders.Gemini => await GeminiAsync(baseUrl, key, settings.AiModel, system, user, pictures, token).ConfigureAwait(false),
            _ => await OpenAiAsync(baseUrl, key, settings.AiModel, system, user, pictures, token).ConfigureAwait(false),
        };
    }

    // ------------------------------------------------------------------ OpenAI shape

    private static async Task<string> OpenAiAsync(
        string baseUrl,
        string key,
        string model,
        string system,
        string user,
        IReadOnlyList<byte[]>? pictures,
        CancellationToken token)
    {
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = user },
        };

        foreach (byte[] picture in pictures ?? Array.Empty<byte[]>())
        {
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(picture),
                },
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = content },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        request.Content = Json(body);

        JsonNode reply = await SendAsync(request, token).ConfigureAwait(false);
        return reply["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
    }

    // ------------------------------------------------------------------ Anthropic shape

    private static async Task<string> AnthropicAsync(
        string baseUrl,
        string key,
        string model,
        string system,
        string user,
        IReadOnlyList<byte[]>? pictures,
        CancellationToken token)
    {
        var content = new JsonArray();

        // Anthropic reads better when the picture comes before the question about it.
        foreach (byte[] picture in pictures ?? Array.Empty<byte[]>())
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"] = Convert.ToBase64String(picture),
                },
            });
        }

        content.Add(new JsonObject { ["type"] = "text", ["text"] = user });

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["temperature"] = 0.2,
            ["system"] = system,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = content },
            },
        };

        string url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl + "/messages"
            : baseUrl + "/v1/messages";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Add("x-api-key", key);
        }

        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = Json(body);

        JsonNode reply = await SendAsync(request, token).ConfigureAwait(false);

        // The answer arrives as a list of blocks, and the text ones are joined.
        if (reply["content"] is JsonArray blocks)
        {
            var text = new StringBuilder();
            foreach (JsonNode? block in blocks)
            {
                if (block?["type"]?.GetValue<string>() == "text")
                {
                    text.Append(block["text"]?.GetValue<string>());
                }
            }

            return text.ToString();
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------ Gemini shape

    private static async Task<string> GeminiAsync(
        string baseUrl,
        string key,
        string model,
        string system,
        string user,
        IReadOnlyList<byte[]>? pictures,
        CancellationToken token)
    {
        var parts = new JsonArray
        {
            new JsonObject { ["text"] = user },
        };

        foreach (byte[] picture in pictures ?? Array.Empty<byte[]>())
        {
            parts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = "image/jpeg",
                    ["data"] = Convert.ToBase64String(picture),
                },
            });
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = system } },
            },
            ["contents"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["parts"] = parts },
            },
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.2,
            },
        };

        string root = baseUrl.Contains("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/v1beta";
        string url = $"{root}/models/{model}:generateContent";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Add("x-goog-api-key", key);
        }

        request.Content = Json(body);

        JsonNode reply = await SendAsync(request, token).ConfigureAwait(false);

        if (reply["candidates"]?[0]?["content"]?["parts"] is JsonArray parts2)
        {
            var text = new StringBuilder();
            foreach (JsonNode? part in parts2)
            {
                text.Append(part?["text"]?.GetValue<string>());
            }

            return text.ToString();
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------ model list

    /// <summary>
    /// Asks the service which models the key can actually use, so the person can pick one
    /// from a list instead of typing a name and hoping.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(AppSettings settings, CancellationToken token)
    {
        string provider = settings.AiProvider?.ToLowerInvariant() ?? AiProviders.OpenAi;
        string key = settings.GetAiKey();
        string baseUrl = (settings.AiBaseUrl ?? string.Empty).TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("There is no address to ask. Fill in the address first.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl(provider, baseUrl));

        switch (provider)
        {
            case AiProviders.Anthropic:
                if (!string.IsNullOrEmpty(key))
                {
                    request.Headers.Add("x-api-key", key);
                }

                request.Headers.Add("anthropic-version", "2023-06-01");
                break;

            case AiProviders.Gemini:
                if (!string.IsNullOrEmpty(key))
                {
                    request.Headers.Add("x-goog-api-key", key);
                }

                break;

            default:
                if (!string.IsNullOrEmpty(key))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }

                break;
        }

        JsonNode reply = await SendAsync(request, token).ConfigureAwait(false);
        return ReadModels(provider, reply);
    }

    private static string ModelsUrl(string provider, string baseUrl) => provider switch
    {
        AiProviders.Anthropic => baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl + "/models?limit=100"
            : baseUrl + "/v1/models?limit=100",
        AiProviders.Gemini => (baseUrl.Contains("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/v1beta") + "/models?pageSize=200",
        _ => baseUrl + "/models",
    };

    private static IReadOnlyList<string> ReadModels(string provider, JsonNode reply)
    {
        var names = new List<string>();

        if (provider == AiProviders.Gemini)
        {
            foreach (JsonNode? model in reply["models"] as JsonArray ?? new JsonArray())
            {
                string name = model?["name"]?.GetValue<string>() ?? string.Empty;

                // Only the ones that can answer a prompt are of any use here.
                bool canGenerate = model?["supportedGenerationMethods"] is not JsonArray methods
                    || methods.Any(m => m?.GetValue<string>() == "generateContent");

                if (canGenerate && name.StartsWith("models/", StringComparison.Ordinal))
                {
                    names.Add(name["models/".Length..]);
                }
            }
        }
        else
        {
            foreach (JsonNode? model in reply["data"] as JsonArray ?? new JsonArray())
            {
                string id = model?["id"]?.GetValue<string>() ?? string.Empty;
                if (!string.IsNullOrEmpty(id))
                {
                    names.Add(id);
                }
            }
        }

        // Anything that cannot hold a conversation is noise in a list you pick from.
        string[] unrelated = { "whisper", "tts", "dall-e", "embedding", "moderation", "audio", "realtime", "image" };

        return names
            .Where(n => !unrelated.Any(bad => n.Contains(bad, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ shared

    private static StringContent Json(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonNode> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The assistant replied with {(int)response.StatusCode}. {Describe(raw)}");
        }

        return JsonNode.Parse(raw) ?? throw new InvalidOperationException("The assistant sent nothing back.");
    }

    /// <summary>Pulls the human readable part out of an error body where there is one.</summary>
    private static string Describe(string raw)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(raw);
            string? message = node?["error"]?["message"]?.GetValue<string>()
                ?? node?["error"]?["status"]?.GetValue<string>()
                ?? node?["message"]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch
        {
            // Not json, so the raw text is the best that can be offered.
        }

        return raw.Length <= 300 ? raw : raw[..300];
    }
}
