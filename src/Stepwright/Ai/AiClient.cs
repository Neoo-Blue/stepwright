using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Stepwright.Config;
using Stepwright.Web;

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
        string auth = AiAuthKinds.Clean(settings.AiAuth);

        // The subscription route never touches this file's request shapes. The app that is
        // already signed in does the talking, and it is handed the same prompt.
        if (auth == AiAuthKinds.Cli)
        {
            return await AiAgents.CompleteAsync(settings, system, user, pictures, token).ConfigureAwait(false);
        }

        // A sign in made in this app and a token pasted from elsewhere reach the service the
        // same way. The difference is that the first one renews itself.
        // The page route never builds a request at all. The person's own browser does the asking,
        // so there is no address, no key and nothing to sign.
        if (auth == AiAuthKinds.Browser)
        {
            if (provider != AiProviders.Copilot)
            {
                throw new InvalidOperationException(
                    "Only Copilot can be reached through its own page. For the others use a key,"
                    + " or sign in.");
            }

            return await CopilotWeb
                .AskAsync(settings.AiCopilotWork, system + "\n\n" + user, token, pictures)
                .ConfigureAwait(false);
        }

        // A sign in made in this app. Claude reaches the ordinary address with a signed in token,
        // but ChatGPT and Gemini can only be reached through their own command line backends,
        // each with a shape of its own, so those two are handled apart before anything generic.
        if (auth == AiAuthKinds.Subscription)
        {
            switch (provider)
            {
                case AiProviders.OpenAi:
                    return await ChatGptSubscriptionAsync(settings, system, user, token).ConfigureAwait(false);
                case AiProviders.Gemini:
                    return await GeminiSubscriptionAsync(settings, system, user, token).ConfigureAwait(false);
            }
        }

        bool subscriptionToken = auth is AiAuthKinds.Token or AiAuthKinds.Subscription;

        string key = auth == AiAuthKinds.Subscription
            ? await ClaudeTokenAsync(settings, token).ConfigureAwait(false)
            : subscriptionToken ? settings.GetAiToken() : settings.GetAiKey();

        string baseUrl = (settings.AiBaseUrl ?? string.Empty).TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("The assistant has no address to call. Check Settings.");
        }

        // Signed in with a work account. Copilot only ever works this way; Foundry can also
        // take a key, in which case it falls through to the shape below.
        if (auth == AiAuthKinds.Microsoft)
        {
            string bearer = await MicrosoftTokenAsync(settings, token).ConfigureAwait(false);

            return provider == AiProviders.Copilot
                ? await CopilotAsync(baseUrl, bearer, system, user, token).ConfigureAwait(false)
                : await FoundryAsync(baseUrl, bearer, key: string.Empty, settings.AiModel, system, user, pictures, token)
                    .ConfigureAwait(false);
        }

        if (provider == AiProviders.Copilot)
        {
            throw new InvalidOperationException(
                "Microsoft 365 Copilot has no keys. Sign in with your work account in Settings.");
        }

        if (provider == AiProviders.Foundry)
        {
            return await FoundryAsync(baseUrl, bearer: string.Empty, key, settings.AiModel, system, user, pictures, token)
                .ConfigureAwait(false);
        }

        if (subscriptionToken && provider != AiProviders.Anthropic)
        {
            throw new InvalidOperationException(
                "Only Anthropic takes a subscription token. For the others use the app you signed in to, or a key.");
        }

        return provider switch
        {
            AiProviders.Anthropic => await AnthropicAsync(baseUrl, key, settings.AiModel, system, user, pictures, subscriptionToken, token).ConfigureAwait(false),
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

        JsonNode reply = await SendAsync(
            model,
            steady =>
            {
                var body = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = content },
                    },
                };

                if (steady)
                {
                    body["temperature"] = 0.2;
                }

                var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");

                if (!string.IsNullOrEmpty(key))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }

                request.Content = Json(body);
                return request;
            },
            token).ConfigureAwait(false);

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
        bool subscriptionToken,
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

        string url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl + "/messages"
            : baseUrl + "/v1/messages";

        JsonNode reply = await SendAsync(
            model,
            steady =>
            {
                var body = new JsonObject
                {
                    // Claude thinks before it answers unless told otherwise, and this ceiling
                    // covers the thinking as well as the sentence, so it is set well above
                    // what a rewritten step needs.
                    ["model"] = model,
                    ["max_tokens"] = 8192,
                    ["system"] = System(system, subscriptionToken),
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "user", ["content"] = content },
                    },
                };

                // Every current Claude model has dropped the temperature setting and rejects a
                // request that carries one. The older ones still take it, so it is offered and
                // withdrawn if it is refused.
                if (steady && !DropsTemperature(model))
                {
                    body["temperature"] = 0.2;
                }

                var request = new HttpRequestMessage(HttpMethod.Post, url);
                Sign(request, key, subscriptionToken);
                request.Content = Json(body);
                return request;
            },
            token).ConfigureAwait(false);


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

    /// <summary>
    /// A subscription token is granted to the Claude Code client, and the service only honours
    /// it when the request looks like one, so the first system block is that client's own line
    /// and the house style follows it. A bought key needs none of this.
    /// </summary>
    private static JsonNode System(string system, bool subscriptionToken) => subscriptionToken
        ? new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = "You are Claude Code, Anthropic's official CLI for Claude.",
            },
            new JsonObject { ["type"] = "text", ["text"] = system },
        }
        : JsonValue.Create(system)!;

    /// <summary>Puts the right kind of proof on an Anthropic request.</summary>
    private static void Sign(HttpRequestMessage request, string key, bool subscriptionToken)
    {
        if (!string.IsNullOrEmpty(key))
        {
            if (subscriptionToken)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            }
            else
            {
                request.Headers.Add("x-api-key", key);
            }
        }

        request.Headers.Add("anthropic-version", "2023-06-01");
    }

    // ------------------------------------------------------------------ Claude subscription

    /// <summary>
    /// A usable token for the signed in Claude subscription, renewing it when it has run out.
    /// The renewal is written back to settings, so this is the last time the person thinks about
    /// it: they sign in once and the app keeps itself signed in from then on.
    /// </summary>
    private static async Task<string> ClaudeTokenAsync(AppSettings settings, CancellationToken token)
    {
        if (!settings.HasClaudeSignIn)
        {
            throw new InvalidOperationException("Sign in to your Claude subscription in Settings first.");
        }

        string access = settings.GetAiAccess();

        if (!string.IsNullOrEmpty(access) && settings.AiAccessExpires > DateTimeOffset.UtcNow)
        {
            return access;
        }

        ClaudeSession renewed = await ClaudeOAuth
            .RenewAsync(settings.GetAiRefresh(), token)
            .ConfigureAwait(false);

        settings.RememberClaude(renewed);
        settings.Save();

        return renewed.AccessToken;
    }

    // ------------------------------------------------------------------ ChatGPT subscription

    /// <summary>
    /// One question to a signed in ChatGPT subscription. There is no ordinary API address for a
    /// subscription, so this speaks to the same backend the Codex app speaks to, in the shape it
    /// expects: a Responses request that must stream and must not be stored, carrying the
    /// workspace the sign in belongs to. The answer is streamed back and assembled here.
    /// </summary>
    private static async Task<string> ChatGptSubscriptionAsync(
        AppSettings settings,
        string system,
        string user,
        CancellationToken token)
    {
        ChatGptSession session = await ChatGptTokenAsync(settings, token).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["model"] = Model(settings.AiModel, "gpt-5.1"),
            ["instructions"] = "You are a ChatGPT agent.",
            ["stream"] = true,
            ["store"] = false,
            ["input"] = new JsonArray
            {
                Turn("developer", system),
                Turn("user", user),
            },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://chatgpt.com/backend-api/codex/responses")
        {
            Content = Json(body),
        };

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.AccessToken);
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", session.Workspace);
        request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        request.Headers.TryAddWithoutValidation("User-Agent", "codex-cli/0.144.1 (external, cli)");

        using HttpResponseMessage response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string failed = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            throw new InvalidOperationException(ChatGptTrouble(response.StatusCode, failed));
        }

        return await ReadResponsesStreamAsync(response, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the Responses stream and hands back the finished answer. The deltas are collected as
    /// they arrive, and the completed event is used as a fallback in case nothing was streamed.
    /// </summary>
    private static async Task<string> ReadResponsesStreamAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var answer = new StringBuilder();
        string fallback = string.Empty;

        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string data = line[5..].Trim();

            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            JsonNode? node;

            try
            {
                node = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            string type = node?["type"]?.GetValue<string>() ?? string.Empty;

            switch (type)
            {
                case "response.output_text.delta":
                    answer.Append(node?["delta"]?.GetValue<string>() ?? string.Empty);
                    break;

                case "response.completed":
                    fallback = OutputText(node?["response"]);
                    break;

                case "response.failed":
                    string why = node?["response"]?["error"]?["message"]?.GetValue<string>()
                        ?? "ChatGPT stopped before it finished.";
                    throw new InvalidOperationException(why);
            }
        }

        string text = answer.ToString().Trim();

        if (text.Length > 0)
        {
            return text;
        }

        return fallback.Trim().Length > 0
            ? fallback.Trim()
            : throw new InvalidOperationException("ChatGPT finished but said nothing. Try again.");
    }

    /// <summary>Pulls the assembled text out of a completed Responses object.</summary>
    private static string OutputText(JsonNode? response)
    {
        if (response?["output"] is not JsonArray output)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (JsonNode? item in output)
        {
            if (item?["content"] is not JsonArray content)
            {
                continue;
            }

            foreach (JsonNode? part in content)
            {
                if (part?["type"]?.GetValue<string>() is "output_text" or "text")
                {
                    text.Append(part?["text"]?.GetValue<string>() ?? string.Empty);
                }
            }
        }

        return text.ToString();
    }

    private static JsonObject Turn(string role, string text) => new()
    {
        ["type"] = "message",
        ["role"] = role,
        ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = text } },
    };

    private static string ChatGptTrouble(System.Net.HttpStatusCode code, string body)
    {
        string described = Describe(body);

        return (int)code switch
        {
            401 or 403 =>
                "ChatGPT would not accept this sign in. It may have expired, or this plan may not"
                + " cover the assistant. Sign in again in Settings. " + described,
            429 =>
                "ChatGPT is rate limiting this plan right now. Wait a little and try again. " + described,
            _ => "ChatGPT could not answer. " + described,
        };
    }

    private static async Task<ChatGptSession> LoadChatGptAsync(AppSettings settings)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        return new ChatGptSession
        {
            AccessToken = settings.GetAiAccess(),
            RefreshToken = settings.GetAiRefresh(),
            Expires = settings.AiAccessExpires,
            Workspace = settings.AiWorkspace,
            Plan = settings.AiPlan,
            Account = settings.AiAccount,
        };
    }

    private static async Task<ChatGptSession> ChatGptTokenAsync(AppSettings settings, CancellationToken token)
    {
        if (!settings.HasSubscriptionSignIn)
        {
            throw new InvalidOperationException("Sign in to your ChatGPT subscription in Settings first.");
        }

        ChatGptSession session = await LoadChatGptAsync(settings).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(session.AccessToken) && session.Expires > DateTimeOffset.UtcNow)
        {
            return session;
        }

        ChatGptSession renewed = await ChatGptOAuth.RenewAsync(session, token).ConfigureAwait(false);

        settings.RememberChatGpt(renewed);
        settings.Save();

        return renewed;
    }

    // ------------------------------------------------------------------ Gemini subscription

    /// <summary>
    /// One question to a signed in Gemini plan. Like ChatGPT there is no ordinary API address for
    /// a plan, so this speaks to the Code Assist service the Gemini app uses, naming the project
    /// the plan bills against. The whole answer comes back in one reply here rather than streamed.
    /// </summary>
    private static async Task<string> GeminiSubscriptionAsync(
        AppSettings settings,
        string system,
        string user,
        CancellationToken token)
    {
        GeminiSession session = await GeminiTokenAsync(settings, token).ConfigureAwait(false);
        string model = Model(settings.AiModel, "gemini-2.5-pro");

        var body = new JsonObject
        {
            ["model"] = model,
            ["project"] = session.Project,
            ["user_prompt_id"] = Guid.NewGuid().ToString("n"),
            ["request"] = new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = user } },
                    },
                },
                ["systemInstruction"] = new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = system } },
                },
            },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://cloudcode-pa.googleapis.com/v1internal:generateContent")
        {
            Content = Json(body),
        };

        GeminiOAuth.Sign(request, session.AccessToken);

        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GeminiTrouble(response.StatusCode, text));
        }

        JsonNode? node = JsonNode.Parse(text);
        string answer = GeminiText(node?["response"] ?? node);

        return answer.Trim().Length > 0
            ? answer.Trim()
            : throw new InvalidOperationException("Gemini finished but said nothing. Try again.");
    }

    /// <summary>
    /// The answer text out of a Code Assist reply. Every part that is a real answer is kept and
    /// joined, and the parts the model marks as its own thinking are left out, because a thought
    /// is not the answer and pasting the first part alone would sometimes hand back the thinking.
    /// </summary>
    private static string GeminiText(JsonNode? response)
    {
        if (response?["candidates"] is not JsonArray candidates || candidates.Count == 0)
        {
            return string.Empty;
        }

        if (candidates[0]?["content"]?["parts"] is not JsonArray parts)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        foreach (JsonNode? part in parts)
        {
            bool thought = part?["thought"]?.GetValue<bool>() ?? false;

            if (thought)
            {
                continue;
            }

            if (part?["text"]?.GetValue<string>() is string piece)
            {
                text.Append(piece);
            }
        }

        return text.ToString();
    }

    private static string GeminiTrouble(System.Net.HttpStatusCode code, string body)
    {
        string described = Describe(body);

        return (int)code switch
        {
            401 or 403 =>
                "Google would not accept this sign in. It may have expired, or this account may not"
                + " be entitled to Gemini Code Assist. Sign in again in Settings. " + described,
            429 =>
                "This Gemini plan is out of quota for now. Wait and try again. " + described,
            _ => "Gemini could not answer. " + described,
        };
    }

    private static async Task<GeminiSession> GeminiTokenAsync(AppSettings settings, CancellationToken token)
    {
        if (!settings.HasSubscriptionSignIn)
        {
            throw new InvalidOperationException("Sign in to your Gemini plan in Settings first.");
        }

        var session = new GeminiSession
        {
            AccessToken = settings.GetAiAccess(),
            RefreshToken = settings.GetAiRefresh(),
            Expires = settings.AiAccessExpires,
            Project = settings.AiProject,
            Plan = settings.AiPlan,
            Account = settings.AiAccount,
        };

        if (!string.IsNullOrEmpty(session.AccessToken) && session.Expires > DateTimeOffset.UtcNow)
        {
            return session;
        }

        GeminiSession renewed = await GeminiOAuth.RenewAsync(session, token).ConfigureAwait(false);

        settings.RememberGemini(renewed);
        settings.Save();

        return renewed;
    }

    private static string Model(string? chosen, string fallback)
    {
        string name = (chosen ?? string.Empty).Trim();
        return name.Length > 0 ? name : fallback;
    }

    // ------------------------------------------------------------------ Microsoft shapes

    /// <summary>
    /// A usable access token for the signed in work account, renewing it when it has run out.
    /// The renewal is written back to settings, so the next run of the app starts ready.
    /// </summary>
    private static async Task<string> MicrosoftTokenAsync(AppSettings settings, CancellationToken token)
    {
        if (!settings.HasMicrosoftSignIn)
        {
            throw new InvalidOperationException("Sign in with your Microsoft work account in Settings first.");
        }

        string access = settings.GetAiAccess();

        if (!string.IsNullOrEmpty(access) && settings.AiAccessExpires > DateTimeOffset.UtcNow)
        {
            return Usable(access);
        }

        string[] scopes = string.Equals(settings.AiProvider, AiProviders.Copilot, StringComparison.OrdinalIgnoreCase)
            ? MicrosoftOAuth.CopilotScopes
            : MicrosoftOAuth.FoundryScopes;

        MicrosoftSession renewed = await MicrosoftOAuth
            .RefreshAsync(settings.AiAppId, settings.AiTenant, scopes, settings.GetAiRefresh(), token)
            .ConfigureAwait(false);

        // A renewal that comes back for a different organisation means the account underneath
        // has changed. Carrying on would quietly send one customer's work to another customer's
        // Copilot, so it stops here and says so.
        if (!string.IsNullOrEmpty(settings.AiTenantId)
            && !string.IsNullOrEmpty(renewed.TenantId)
            && !string.Equals(settings.AiTenantId, renewed.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This sign in now belongs to a different organisation than the one it was set up"
                + " with. Sign out and sign in again in Settings, so it is clear which account"
                + " the assistant is using.");
        }

        settings.RememberMicrosoft(renewed);
        settings.Save();

        return Usable(renewed.AccessToken);
    }

    /// <summary>
    /// Makes sure the token is one worth sending. A work account token is a JWT, which is three
    /// parts joined by dots, and Microsoft rejects anything else with a message about dots that
    /// says nothing to the person. When the token held here is not that, the fault is on this
    /// side, so it is caught here and turned into an instruction the person can act on rather
    /// than sent off to come back as a riddle.
    /// </summary>
    private static string Usable(string token)
    {
        if (!string.IsNullOrWhiteSpace(token) && token.Count(c => c == '.') == 2)
        {
            return token;
        }

        throw new InvalidOperationException(
            "The Microsoft sign in on this machine did not leave a usable token. Open Settings,"
            + " sign out of the work account, and sign in again.");
    }

    /// <summary>
    /// Microsoft 365 Copilot. It is a conversation rather than a single question, so a fresh
    /// one is opened for every step: an assistant that remembers the last twenty steps starts
    /// answering about them instead of about the picture in front of it.
    ///
    /// It also has no separate place for the rules, so the rules and the question travel
    /// together, and no way to take a picture at all, which is why the screenshot switch is
    /// turned off for this service rather than quietly ignored.
    /// </summary>
    private static async Task<string> CopilotAsync(
        string baseUrl,
        string bearer,
        string system,
        string user,
        CancellationToken token)
    {
        string root = baseUrl.EndsWith("/beta", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/beta";

        using var opening = new HttpRequestMessage(HttpMethod.Post, root + "/copilot/conversations");
        opening.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        opening.Content = Json(new JsonObject());

        JsonNode opened = await SendAsync(opening, token).ConfigureAwait(false);
        string conversation = opened["id"]?.GetValue<string>() ?? string.Empty;

        if (conversation.Length == 0)
        {
            throw new InvalidOperationException("Copilot did not say which conversation it opened.");
        }

        var body = new JsonObject
        {
            ["message"] = new JsonObject { ["text"] = system + "\n\n" + user },
            ["locationHint"] = new JsonObject { ["timeZone"] = TimeZoneInfo.Local.Id },

            // The work in front of the person is the whole context. Searching their mail and
            // their sites for a step in a guide would be slow and beside the point.
            ["contextualResources"] = new JsonObject
            {
                ["webContext"] = new JsonObject { ["isWebEnabled"] = false },
            },
        };

        using var asking = new HttpRequestMessage(
            HttpMethod.Post,
            $"{root}/copilot/conversations/{conversation}/chat");

        asking.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        asking.Content = Json(body);

        JsonNode reply = await SendAsync(asking, token).ConfigureAwait(false);

        if (reply["messages"] is not JsonArray messages || messages.Count == 0)
        {
            return string.Empty;
        }

        // The first message is the question read back. The answer is the last one.
        string text = messages[^1]?["text"]?.GetValue<string>() ?? string.Empty;

        return Plain(text);
    }

    /// <summary>
    /// Copilot writes for a person: entity tags around names and files, and footnote markers
    /// pointing at where it looked. None of that survives a trip through a parser, so it is
    /// taken out before anything else reads the answer.
    /// </summary>
    private static string Plain(string text)
    {
        string result = Regex.Replace(
            text,
            "</?(Person|File|Event|Email|Message|Site|Citation)[^>]*>",
            string.Empty,
            RegexOptions.IgnoreCase);

        return Regex.Replace(result, @"\[\^\d+\^\]", string.Empty).Trim();
    }

    /// <summary>
    /// Azure AI Foundry speaks the OpenAI shape, with the deployment name in the address rather
    /// than the body, and either a signed in work account or a key of its own.
    /// </summary>
    private static async Task<string> FoundryAsync(
        string baseUrl,
        string bearer,
        string key,
        string model,
        string system,
        string user,
        IReadOnlyList<byte[]>? pictures,
        CancellationToken token)
    {
        string deployment = (model ?? string.Empty).Trim();

        if (deployment.Length == 0)
        {
            throw new InvalidOperationException(
                "Foundry needs the name of your deployment. Put it in the model box.");
        }

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

        JsonNode reply = await SendAsync(
            deployment,
            steady =>
            {
                var body = new JsonObject
                {
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = content },
                    },
                };

                if (steady)
                {
                    body["temperature"] = 0.2;
                }

                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl}/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21");

                if (bearer.Length > 0)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }
                else if (key.Length > 0)
                {
                    request.Headers.Add("api-key", key);
                }

                request.Content = Json(body);
                return request;
            },
            token).ConfigureAwait(false);

        return reply["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
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

        string root = baseUrl.Contains("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/v1beta";
        string url = $"{root}/models/{model}:generateContent";

        JsonNode reply = await SendAsync(
            model,
            steady =>
            {
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
                };

                if (steady)
                {
                    body["generationConfig"] = new JsonObject { ["temperature"] = 0.2 };
                }

                var request = new HttpRequestMessage(HttpMethod.Post, url);

                if (!string.IsNullOrEmpty(key))
                {
                    request.Headers.Add("x-goog-api-key", key);
                }

                request.Content = Json(body);
                return request;
            },
            token).ConfigureAwait(false);


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
        string auth = AiAuthKinds.Clean(settings.AiAuth);

        // There is nothing to ask when the app does the talking, so the names it knows are offered.
        if (auth == AiAuthKinds.Cli)
        {
            AiAgent? agent = AiAgents.Find(provider);
            return agent is null
                ? Array.Empty<string>()
                : agent.Models.Where(m => m.Length > 0).ToList();
        }

        // Copilot has no model to choose, whichever way it is reached. The page is whatever
        // Copilot is, and the work account route does not let one be named, so there is nothing
        // to ask and asking only earns a 404.
        if (provider == AiProviders.Copilot)
        {
            return Array.Empty<string>();
        }

        // The ChatGPT and Gemini subscription backends have no models list to ask, and asking
        // their own way would spend a real request, so the names they answer to are offered.
        if (auth == AiAuthKinds.Subscription && provider == AiProviders.OpenAi)
        {
            return new[] { "gpt-5.1", "gpt-5.1-codex", "gpt-5" };
        }

        if (auth == AiAuthKinds.Subscription && provider == AiProviders.Gemini)
        {
            return new[] { "gemini-2.5-pro", "gemini-2.5-flash" };
        }

        bool subscriptionToken = auth is AiAuthKinds.Token or AiAuthKinds.Subscription;

        string key = auth == AiAuthKinds.Subscription
            ? await ClaudeTokenAsync(settings, token).ConfigureAwait(false)
            : subscriptionToken ? settings.GetAiToken() : settings.GetAiKey();

        string baseUrl = (settings.AiBaseUrl ?? string.Empty).TrimEnd('/');

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("There is no address to ask. Fill in the address first.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl(provider, baseUrl));

        switch (provider)
        {
            case AiProviders.Anthropic:
                Sign(request, key, subscriptionToken);
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

    /// <summary>
    /// Models that answered that they will not be told what temperature to work at. Newer ones
    /// fix their own and reject the setting outright, so the second attempt is only ever made
    /// once for each of them.
    /// </summary>
    private static readonly HashSet<string> FixedTemperature = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True for the Claude models that removed the temperature setting rather than ignoring it:
    /// the 5 family, and 4.6 onwards. The plain names sonnet, opus and haiku are the current
    /// models, so they belong here too. Anything older is left alone.
    /// </summary>
    private static bool DropsTemperature(string? model)
    {
        string name = (model ?? string.Empty).Trim();

        if (name.Length == 0 || name is "sonnet" or "opus" or "haiku")
        {
            return true;
        }

        string[] recent = { "-5", "-4-6", "-4-7", "-4-8", "fable", "mythos" };

        return name.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            && recent.Any(mark => name.Contains(mark, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sends the request, and when the only complaint was the temperature, sends it again
    /// without one. A rewriting pass wants a steady temperature where it is allowed, and would
    /// rather have the answer than the setting where it is not.
    /// </summary>
    private static async Task<JsonNode> SendAsync(
        string model,
        Func<bool, HttpRequestMessage> build,
        CancellationToken token)
    {
        bool steady;

        lock (FixedTemperature)
        {
            steady = !FixedTemperature.Contains(model ?? string.Empty);
        }

        try
        {
            using HttpRequestMessage request = build(steady);
            return await SendAsync(request, token).ConfigureAwait(false);
        }
        catch (InvalidOperationException failure) when (steady && Blames(failure.Message, "temperature"))
        {
            lock (FixedTemperature)
            {
                FixedTemperature.Add(model ?? string.Empty);
            }

            using HttpRequestMessage retry = build(false);
            return await SendAsync(retry, token).ConfigureAwait(false);
        }
    }

    /// <summary>True when the service refused the request because of one named setting.</summary>
    private static bool Blames(string message, string setting) =>
        message.Contains(setting, StringComparison.OrdinalIgnoreCase)
        && (message.Contains("400", StringComparison.Ordinal)
            || message.Contains("deprecat", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not support", StringComparison.OrdinalIgnoreCase));

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
