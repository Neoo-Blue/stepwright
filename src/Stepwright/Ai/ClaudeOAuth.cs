using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Stepwright.Ai;

/// <summary>What a finished Claude sign in leaves behind.</summary>
public sealed class ClaudeSession
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required DateTimeOffset Expires { get; init; }

    /// <summary>Who signed in, so the settings page can say whose plan is paying.</summary>
    public string Account { get; init; } = string.Empty;
}

/// <summary>
/// Signing in to a Claude subscription from the app itself, with no command line app to install
/// and nothing to register anywhere.
///
/// This is the flow the Claude command line app uses: an authorization code with a proof key,
/// which is the pattern written for exactly this situation, an application handed out as a file
/// that cannot keep a secret. The proof key takes the place of the secret, so there is nothing
/// in this file that would be worth anything to somebody who read it.
///
/// Anthropic shows the code on a page rather than sending it to an address, which is why there
/// is no listener here and no port to be blocked: the person signs in, copies one line, and
/// pastes it back. That also means it works from a machine with no browser, and through a remote
/// session, where a loopback address would not survive the trip.
/// </summary>
public static class ClaudeOAuth
{
    /// <summary>
    /// The published identifier of the Claude command line client. An identifier is a name
    /// rather than a secret, which is what makes a proof key flow safe to ship.
    /// </summary>
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private const string AuthorizePage = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://api.anthropic.com/v1/oauth/token";
    private const string CodePage = "https://platform.claude.com/oauth/code/callback";
    private const string BootstrapUrl = "https://api.anthropic.com/api/claude_cli/bootstrap";

    /// <summary>
    /// What the sign in asks for. It is the set the command line client asks for, kept whole
    /// because a shorter list is refused rather than narrowed.
    /// </summary>
    private static readonly string Scopes = string.Join(
        ' ',
        "org:create_api_key",
        "user:profile",
        "user:inference",
        "user:sessions:claude_code",
        "user:mcp_servers");

    public const string PlansPage = "https://claude.ai/upgrade";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// One sign in in progress. The proof key never leaves this object, so a code copied out of
    /// somebody's browser history is worth nothing without the app that started the sign in.
    /// </summary>
    public sealed class Attempt
    {
        public required string Address { get; init; }

        internal string Verifier { get; init; } = string.Empty;

        internal string State { get; init; } = string.Empty;
    }

    /// <summary>Builds the page to open. Nothing is sent anywhere until the code comes back.</summary>
    public static Attempt Begin()
    {
        string verifier = Random(32);
        string state = Random(32);

        var address = new StringBuilder(AuthorizePage);
        address.Append("?code=true");
        address.Append("&client_id=").Append(Uri.EscapeDataString(ClientId));
        address.Append("&response_type=code");
        address.Append("&redirect_uri=").Append(Uri.EscapeDataString(CodePage));
        address.Append("&scope=").Append(Uri.EscapeDataString(Scopes));
        address.Append("&code_challenge=").Append(Challenge(verifier));
        address.Append("&code_challenge_method=S256");
        address.Append("&state=").Append(Uri.EscapeDataString(state));

        return new Attempt { Address = address.ToString(), Verifier = verifier, State = state };
    }

    /// <summary>
    /// Turns what the person pasted into a signed in session. They may paste the code, the code
    /// and its state joined by a hash, or the whole address of the page they landed on, and all
    /// three are the same thing wearing different clothes.
    /// </summary>
    public static async Task<ClaudeSession> FinishAsync(
        Attempt attempt,
        string pasted,
        CancellationToken token)
    {
        (string code, string state) = Read(pasted);

        if (code.Length == 0)
        {
            throw new InvalidOperationException(
                "That does not look like the code from the page. Copy the whole line it showed you.");
        }

        var body = new JsonObject
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["state"] = state.Length > 0 ? state : attempt.State,
            ["client_id"] = ClientId,
            ["redirect_uri"] = CodePage,
            ["code_verifier"] = attempt.Verifier,
        };

        JsonNode granted = await PostAsync(body, token).ConfigureAwait(false);
        return await FinishAsync(granted, previousRefresh: string.Empty, token).ConfigureAwait(false);
    }

    /// <summary>Renews a sign in that has run out, without asking the person anything.</summary>
    public static async Task<ClaudeSession> RenewAsync(string refreshToken, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("There is no sign in to renew. Sign in to Claude again.");
        }

        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken.Trim(),
            ["client_id"] = ClientId,
        };

        JsonNode granted = await PostAsync(body, token).ConfigureAwait(false);
        return await FinishAsync(granted, refreshToken.Trim(), token).ConfigureAwait(false);
    }

    private static async Task<ClaudeSession> FinishAsync(
        JsonNode granted,
        string previousRefresh,
        CancellationToken token)
    {
        string access = granted["access_token"]?.GetValue<string>() ?? string.Empty;

        if (access.Length == 0)
        {
            throw new InvalidOperationException("Claude did not hand back a token.");
        }

        int seconds = granted["expires_in"]?.GetValue<int>() ?? 3600;

        return new ClaudeSession
        {
            AccessToken = access,

            // A sign in that hands back no new refresh token keeps the one it was renewed with.
            RefreshToken = granted["refresh_token"]?.GetValue<string>() ?? previousRefresh,

            // A minute is taken off so a request cannot start on a token that ends mid flight.
            Expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 60)),

            Account = await WhoAsync(access, token).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Asks Anthropic whose plan this is. Failing here is not a failed sign in: the token is
    /// good either way, and this only decides whether the settings page can name the account.
    /// </summary>
    private static async Task<string> WhoAsync(string access, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BootstrapUrl);
            Sign(request, access);

            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            JsonNode? node = JsonNode.Parse(text);

            return node?["oauth_account"]?["account_email"]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The headers a request made with one of these tokens has to carry. A subscription token is
    /// granted to the command line client, so a request that does not look like it is refused.
    /// </summary>
    public static void Sign(HttpRequestMessage request, string access)
    {
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.0.0 (external, cli)");
        request.Headers.TryAddWithoutValidation("X-App", "cli");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
    }

    private static async Task<JsonNode> PostAsync(JsonObject body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        JsonNode? node = null;

        try
        {
            node = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            // An answer that is not json is reported by what it says instead.
        }

        if (!response.IsSuccessStatusCode || node is null)
        {
            throw new InvalidOperationException(Explain(node, text));
        }

        return node;
    }

    /// <summary>Turns a refusal into something a person can act on.</summary>
    private static string Explain(JsonNode? node, string raw)
    {
        string name = node?["error"]?.GetValue<string>() ?? string.Empty;
        string detail = node?["error_description"]?.GetValue<string>() ?? string.Empty;

        if (name == "invalid_grant")
        {
            return "That code has already been used, or it ran out. Start the sign in again.";
        }

        if (detail.Length > 0)
        {
            return "Claude refused the sign in. " + detail;
        }

        if (name.Length > 0)
        {
            return "Claude refused the sign in. " + name;
        }

        string text = raw.Trim();
        return text.Length == 0
            ? "Claude refused the sign in and gave no reason."
            : "Claude refused the sign in. " + (text.Length <= 200 ? text : text[..200] + "...");
    }

    /// <summary>
    /// Pulls the code and its state out of whatever was pasted. Anthropic joins the two with a
    /// hash on the page it shows, and a person who copies the address bar instead brings the
    /// same pair along as query values.
    /// </summary>
    private static (string Code, string State) Read(string pasted)
    {
        string text = (pasted ?? string.Empty).Trim().Trim('"');

        if (text.Contains("code=", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(text, UriKind.Absolute, out Uri? address))
        {
            var values = System.Web.HttpUtility.ParseQueryString(address.Query);
            return (values["code"]?.Trim() ?? string.Empty, values["state"]?.Trim() ?? string.Empty);
        }

        int hash = text.IndexOf('#', StringComparison.Ordinal);

        return hash < 0
            ? (text, string.Empty)
            : (text[..hash].Trim(), text[(hash + 1)..].Trim());
    }

    private static string Random(int bytes)
    {
        byte[] buffer = RandomNumberGenerator.GetBytes(bytes);
        return Url(buffer);
    }

    private static string Challenge(string verifier) =>
        Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Base 64 the way addresses want it: no padding and no characters needing escaping.</summary>
    private static string Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
