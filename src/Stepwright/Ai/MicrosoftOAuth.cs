using System.Net.Http;
using System.Text.Json.Nodes;

namespace Stepwright.Ai;

/// <summary>What a finished Microsoft sign in leaves behind.</summary>
public sealed class MicrosoftSession
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required DateTimeOffset Expires { get; init; }
}

/// <summary>
/// Signing in to Microsoft the way a desktop application is meant to: the app asks for a code,
/// the person types that code into their browser on any machine, and the app waits. There is no
/// redirect address to register and no secret to keep, which matters because this app is handed
/// out as a file rather than hosted anywhere.
///
/// The application itself is registered once in your own tenant, so the permissions the person
/// grants are yours to see and to withdraw.
/// </summary>
public static class MicrosoftOAuth
{
    public const string PortalPage = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade";

    /// <summary>
    /// Everything the Copilot chat interface asks for. Microsoft requires all of them together,
    /// and offline access on top so the sign in can renew itself.
    /// </summary>
    public static readonly string[] CopilotScopes =
    {
        "https://graph.microsoft.com/Sites.Read.All",
        "https://graph.microsoft.com/Mail.Read",
        "https://graph.microsoft.com/People.Read.All",
        "https://graph.microsoft.com/OnlineMeetingTranscript.Read.All",
        "https://graph.microsoft.com/Chat.Read",
        "https://graph.microsoft.com/ChannelMessage.Read.All",
        "https://graph.microsoft.com/ExternalItem.Read.All",
        "offline_access",
    };

    /// <summary>What Azure AI Foundry asks for. One permission, plus offline access.</summary>
    public static readonly string[] FoundryScopes =
    {
        "https://cognitiveservices.azure.com/.default",
        "offline_access",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Work accounts only, because Copilot is not offered to personal ones.</summary>
    public static string Authority(string? tenant) =>
        "https://login.microsoftonline.com/"
        + (string.IsNullOrWhiteSpace(tenant) ? "organizations" : tenant.Trim())
        + "/oauth2/v2.0/";

    /// <summary>
    /// Asks Microsoft for a code, shows it to the person, and waits until they have typed it in.
    /// The waiting is the whole flow: nothing is polled faster than Microsoft asks for, because
    /// answering too eagerly is what gets a client throttled.
    /// </summary>
    public static async Task<MicrosoftSession> SignInAsync(
        string clientId,
        string? tenant,
        IReadOnlyList<string> scopes,
        Action<string, string> show,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "Signing in needs the identifier of your own Microsoft application.");
        }

        JsonNode start = await FormAsync(
            Authority(tenant) + "devicecode",
            new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["scope"] = string.Join(' ', scopes),
            },
            token).ConfigureAwait(false);

        string code = start["user_code"]?.GetValue<string>() ?? string.Empty;
        string where = start["verification_uri"]?.GetValue<string>() ?? "https://microsoft.com/devicelogin";
        string deviceCode = start["device_code"]?.GetValue<string>() ?? string.Empty;

        if (code.Length == 0 || deviceCode.Length == 0)
        {
            throw new InvalidOperationException("Microsoft did not hand back a code to sign in with.");
        }

        show(code, where);

        int wait = Math.Max(2, start["interval"]?.GetValue<int>() ?? 5);
        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(
            Math.Max(60, start["expires_in"]?.GetValue<int>() ?? 900));

        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait), token).ConfigureAwait(false);

            (JsonNode? granted, string error) = await TryFormAsync(
                Authority(tenant) + "token",
                new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId.Trim(),
                    ["device_code"] = deviceCode,
                },
                token).ConfigureAwait(false);

            if (granted is not null)
            {
                return Finish(granted, string.Empty);
            }

            switch (error)
            {
                case "authorization_pending":
                    continue;

                case "slow_down":
                    wait += 5;
                    continue;

                case "authorization_declined":
                    throw new InvalidOperationException("The sign in was declined.");

                case "expired_token":
                    throw new InvalidOperationException("The code ran out before it was used. Try again.");

                default:
                    throw new InvalidOperationException("Microsoft refused the sign in. " + error);
            }
        }

        throw new InvalidOperationException("The code ran out before it was used. Try again.");
    }

    /// <summary>Renews a sign in that has run out, without asking the person anything.</summary>
    public static async Task<MicrosoftSession> RefreshAsync(
        string clientId,
        string? tenant,
        IReadOnlyList<string> scopes,
        string refreshToken,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("There is no sign in to renew. Sign in to Microsoft again.");
        }

        JsonNode granted = await FormAsync(
            Authority(tenant) + "token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId.Trim(),
                ["refresh_token"] = refreshToken,
                ["scope"] = string.Join(' ', scopes),
            },
            token).ConfigureAwait(false);

        return Finish(granted, refreshToken);
    }

    private static MicrosoftSession Finish(JsonNode granted, string previousRefresh)
    {
        string access = granted["access_token"]?.GetValue<string>() ?? string.Empty;

        if (access.Length == 0)
        {
            throw new InvalidOperationException("Microsoft did not hand back a token.");
        }

        int seconds = granted["expires_in"]?.GetValue<int>() ?? 3600;

        return new MicrosoftSession
        {
            AccessToken = access,
            RefreshToken = granted["refresh_token"]?.GetValue<string>() ?? previousRefresh,

            // A minute is taken off so a request cannot start on a token that ends mid flight.
            Expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 60)),
        };
    }

    private static async Task<JsonNode> FormAsync(
        string url,
        Dictionary<string, string> fields,
        CancellationToken token)
    {
        (JsonNode? body, string error) = await TryFormAsync(url, fields, token).ConfigureAwait(false);

        return body ?? throw new InvalidOperationException("Microsoft refused the request. " + error);
    }

    /// <summary>
    /// Posts a form and hands back either the answer or the error name. The waiting half of the
    /// device flow is built entirely out of expected errors, so they are not exceptions here.
    /// </summary>
    private static async Task<(JsonNode? Body, string Error)> TryFormAsync(
        string url,
        Dictionary<string, string> fields,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };

        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        JsonNode? node = null;

        try
        {
            node = JsonNode.Parse(raw);
        }
        catch
        {
            // Not json, which only happens when something in front of Microsoft answered.
        }

        if (response.IsSuccessStatusCode && node is not null)
        {
            return (node, string.Empty);
        }

        string name = node?["error"]?.GetValue<string>() ?? string.Empty;
        string detail = node?["error_description"]?.GetValue<string>() ?? string.Empty;

        if (name.Length == 0)
        {
            name = $"{(int)response.StatusCode}. {Shorten(raw)}";
        }
        else if (detail.Length > 0 && name is not ("authorization_pending" or "slow_down"))
        {
            name = name + ". " + Shorten(detail);
        }

        return (null, name);
    }

    private static string Shorten(string text)
    {
        string clean = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 220 ? clean : clean[..220] + "...";
    }
}
