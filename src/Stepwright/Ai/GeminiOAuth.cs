using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Stepwright.Web;

namespace Stepwright.Ai;

/// <summary>What a finished Gemini sign in leaves behind.</summary>
public sealed record GeminiSession
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required DateTimeOffset Expires { get; init; }

    /// <summary>
    /// The Cloud project the Code Assist route bills against. It is discovered once after sign in
    /// and then has to travel on every request, so it is kept here.
    /// </summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>Which tier is paying, said plainly for the settings page.</summary>
    public string Plan { get; init; } = string.Empty;

    public string Account { get; init; } = string.Empty;
}

/// <summary>
/// Signing in to a Gemini plan from the app, the way the Gemini command line app does.
///
/// Read the warning on the settings page first. Google issues these credentials to its own
/// command line app, and reaching the plan from anything else is outside its terms. The
/// sanctioned route is that app, already signed in, which this app can also drive. This is for
/// the person who has weighed that.
///
/// It is a plain Google installed application sign in: an authorization code against a client
/// that ships in the open, exchanged with a secret that is public on purpose because an installed
/// app cannot keep one. The door comes back to a free loopback port. Afterwards there is a short
/// handshake with the Code Assist service to learn which Cloud project the plan uses, because
/// every later request needs it. Every value here was read from a working implementation.
/// </summary>
public static class GeminiOAuth
{
    // The Gemini command line app's own installed application credentials. They are published in
    // the open by Google, but they live in Connect rather than here, so the public source carries
    // nothing a scanner could mistake for a leak. A build without them simply does not offer the
    // Gemini subscription route.
    private static string ClientId => Stepwright.Connect.GeminiClientId.Trim();

    private static string ClientSecret => Stepwright.Connect.GeminiClientSecret.Trim();

    private const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string UserInfoUrl = "https://www.googleapis.com/oauth2/v2/userinfo";

    private const string Scope =
        "https://www.googleapis.com/auth/cloud-platform"
        + " https://www.googleapis.com/auth/userinfo.email"
        + " https://www.googleapis.com/auth/userinfo.profile";

    private const string CodeAssist = "https://cloudcode-pa.googleapis.com/v1internal";

    public const string PlansPage = "https://one.google.com/about/google-ai-plans/";

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>True when this build carries the credentials the Gemini sign in needs.</summary>
    public static bool Available => Stepwright.Connect.HasGemini;

    public static async Task<GeminiSession> SignInAsync(Action<string> open, CancellationToken token)
    {
        if (!Available)
        {
            throw new InvalidOperationException(
                "This build of Stepwright was not given the Gemini application details, so it"
                + " cannot sign in to a Gemini plan. Use a key, or the Gemini command line app.");
        }

        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        // Google allows any loopback port for a desktop client, so a free one is taken rather
        // than a fixed one, and nothing has to be reserved.
        using var door = new Loopback(0, "/oauth2callback");
        string redirect = door.Address;

        var authorize = new StringBuilder(AuthorizeUrl);
        authorize.Append("?response_type=code");
        authorize.Append("&client_id=").Append(Uri.EscapeDataString(ClientId));
        authorize.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirect));
        authorize.Append("&access_type=offline");
        authorize.Append("&prompt=consent");
        authorize.Append("&scope=").Append(Uri.EscapeDataString(Scope));
        authorize.Append("&state=").Append(state);

        open(authorize.ToString());

        System.Collections.Specialized.NameValueCollection answer = await door
            .WaitAsync(state, "You are signed in to Google. You can close this tab.", token)
            .ConfigureAwait(false);

        string code = answer["code"]?.Trim() ?? string.Empty;

        if (code.Length == 0)
        {
            throw new InvalidOperationException("Google did not send a code back. Try again.");
        }

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirect,
        };

        JsonNode granted = await PostAsync(body, token).ConfigureAwait(false);
        GeminiSession session = Finish(granted, previousRefresh: string.Empty, previous: null);

        session = session with { Account = await WhoAsync(session.AccessToken, token).ConfigureAwait(false) };

        // The plan and the project it bills against are not in the token. They come from a short
        // handshake with the service, done once, here, so the first real request already knows
        // where to go.
        return await SettleAsync(session, token).ConfigureAwait(false);
    }

    public static async Task<GeminiSession> RenewAsync(GeminiSession previous, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(previous.RefreshToken))
        {
            throw new InvalidOperationException("There is no sign in to renew. Sign in to Google again.");
        }

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["refresh_token"] = previous.RefreshToken,
        };

        JsonNode granted = await PostAsync(body, token).ConfigureAwait(false);

        // The project was learned once and does not change between renewals, so a renewal keeps
        // it rather than paying for the handshake again.
        return Finish(granted, previous.RefreshToken, previous);
    }

    private static GeminiSession Finish(JsonNode granted, string previousRefresh, GeminiSession? previous)
    {
        string access = granted["access_token"]?.GetValue<string>() ?? string.Empty;

        if (access.Length == 0)
        {
            throw new InvalidOperationException("Google did not hand back a token.");
        }

        int seconds = granted["expires_in"]?.GetValue<int>() ?? 3600;

        return new GeminiSession
        {
            AccessToken = access,
            RefreshToken = granted["refresh_token"]?.GetValue<string>() ?? previousRefresh,
            Expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 60)),
            Project = previous?.Project ?? string.Empty,
            Plan = previous?.Plan ?? string.Empty,
            Account = previous?.Account ?? string.Empty,
        };
    }

    /// <summary>
    /// The Code Assist handshake. It asks the service which project and tier the account has, and
    /// if the account has never been set up it sets it up and waits for that to finish. Only after
    /// this does a request have a project to name.
    /// </summary>
    private static async Task<GeminiSession> SettleAsync(GeminiSession session, CancellationToken token)
    {
        var load = new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["ideType"] = "IDE_UNSPECIFIED",
                ["platform"] = "PLATFORM_UNSPECIFIED",
                ["pluginType"] = "GEMINI",
            },
        };

        JsonNode loaded = await CallAsync(session.AccessToken, "loadCodeAssist", load, token).ConfigureAwait(false);

        string project = loaded["cloudaicompanionProject"]?.GetValue<string>() ?? string.Empty;
        string plan = Tier(loaded);

        if (project.Length == 0)
        {
            // The account has not been onboarded. Pick the tier the service says is the default,
            // ask it to onboard, and wait for the operation it hands back to finish.
            string tier = DefaultTier(loaded);

            var onboard = new JsonObject
            {
                ["tierId"] = tier,
                ["metadata"] = new JsonObject
                {
                    ["ideType"] = "IDE_UNSPECIFIED",
                    ["platform"] = "PLATFORM_UNSPECIFIED",
                    ["pluginType"] = "GEMINI",
                },
            };

            project = await OnboardAsync(session.AccessToken, onboard, token).ConfigureAwait(false);

            if (plan.Length == 0)
            {
                plan = tier;
            }
        }

        if (project.Length == 0)
        {
            throw new InvalidOperationException(
                "Google signed you in but would not say which project your plan uses. This account"
                + " may not be eligible for Gemini Code Assist. Try a key instead.");
        }

        return session with { Project = project, Plan = plan };
    }

    private static async Task<string> OnboardAsync(string access, JsonObject onboard, CancellationToken token)
    {
        JsonNode result = await CallAsync(access, "onboardUser", onboard, token).ConfigureAwait(false);

        // Onboarding is a long running operation. It comes back either already done, or with a
        // name to poll until it is.
        for (int tries = 0; tries < 30; tries++)
        {
            bool done = result["done"]?.GetValue<bool>() ?? false;

            if (done)
            {
                return result["response"]?["cloudaicompanionProject"]?["id"]?.GetValue<string>()
                    ?? result["response"]?["cloudaicompanionProject"]?.GetValue<string>()
                    ?? string.Empty;
            }

            string? name = result["name"]?.GetValue<string>();

            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            result = await PollAsync(access, name, token).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Setting up your Gemini plan took too long. Try again.");
    }

    private static string Tier(JsonNode loaded)
    {
        string paid = loaded["paidTier"]?["id"]?.GetValue<string>() ?? string.Empty;

        if (paid.Length > 0)
        {
            return paid;
        }

        return loaded["currentTier"]?["id"]?.GetValue<string>() ?? string.Empty;
    }

    private static string DefaultTier(JsonNode loaded)
    {
        if (loaded["allowedTiers"] is JsonArray tiers)
        {
            foreach (JsonNode? tier in tiers)
            {
                if (tier?["isDefault"]?.GetValue<bool>() ?? false)
                {
                    return tier?["id"]?.GetValue<string>() ?? "free-tier";
                }
            }
        }

        return "free-tier";
    }

    private static async Task<string> WhoAsync(string access, CancellationToken token)
    {
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, UserInfoUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);

            using System.Net.Http.HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonNode.Parse(text)?["email"]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Signs a request to Code Assist. Everything the plan does goes through here.</summary>
    public static void Sign(System.Net.Http.HttpRequestMessage request, string access)
    {
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);
        request.Headers.TryAddWithoutValidation("User-Agent", "GeminiCLI/0.14.0 (stepwright)");
    }

    private static async Task<JsonNode> CallAsync(string access, string method, JsonObject body, CancellationToken token)
    {
        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Post,
            CodeAssist + ":" + method)
        {
            Content = new System.Net.Http.StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        Sign(request, access);
        return await SendAsync(request, token).ConfigureAwait(false);
    }

    private static async Task<JsonNode> PollAsync(string access, string operation, CancellationToken token)
    {
        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get,
            CodeAssist + "/" + operation);

        Sign(request, access);
        return await SendAsync(request, token).ConfigureAwait(false);
    }

    private static async Task<JsonNode> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
    {
        using System.Net.Http.HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        JsonNode? node = null;

        try
        {
            node = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || node is null)
        {
            throw new InvalidOperationException(Explain(node, text));
        }

        return node;
    }

    private static async Task<JsonNode> PostAsync(Dictionary<string, string> body, CancellationToken token)
    {
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, TokenUrl)
        {
            Content = new System.Net.Http.FormUrlEncodedContent(body),
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return await SendAsync(request, token).ConfigureAwait(false);
    }

    private static string Explain(JsonNode? node, string raw)
    {
        string name = node?["error"]?.GetValue<string>()
            ?? node?["error"]?["status"]?.GetValue<string>()
            ?? string.Empty;

        if (name is "invalid_grant")
        {
            return "That Google sign in has expired or was withdrawn. Sign in again.";
        }

        string detail = node?["error_description"]?.GetValue<string>()
            ?? node?["error"]?["message"]?.GetValue<string>()
            ?? string.Empty;

        if (detail.Length > 0)
        {
            return "Google refused the request. " + detail;
        }

        if (name.Length > 0)
        {
            return "Google refused the request. " + name;
        }

        string text = raw.Trim();
        return text.Length == 0
            ? "Google refused the request and gave no reason."
            : "Google refused the request. " + (text.Length <= 200 ? text : text[..200] + "...");
    }
}
