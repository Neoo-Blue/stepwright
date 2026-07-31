using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Stepwright.Web;

namespace Stepwright.Ai;

/// <summary>What a finished ChatGPT sign in leaves behind.</summary>
public sealed class ChatGptSession
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required DateTimeOffset Expires { get; init; }

    /// <summary>
    /// The workspace this sign in belongs to. Every later request has to carry it, or it reaches
    /// the wrong account.
    /// </summary>
    public string Workspace { get; init; } = string.Empty;

    /// <summary>Which plan is paying, said plainly for the settings page.</summary>
    public string Plan { get; init; } = string.Empty;

    public string Account { get; init; } = string.Empty;
}

/// <summary>
/// Signing in to a ChatGPT subscription from the app, the way the Codex command line app does.
///
/// Read the warning on the settings page before reaching for this. OpenAI issues a subscription
/// to its own apps, and a subscription reached from anything else is outside the terms of a
/// consumer plan. The sanctioned route is the Codex app already signed in on the machine, which
/// this app can also drive. This exists for the person who has weighed that and wants it anyway.
///
/// The flow is a plain authorization code with a proof key. The one thing that is not free choice
/// is the address the sign in comes back to: OpenAI registered http://localhost:1455/auth/callback
/// for this client, so the door has to open on exactly that port. Everything here was read from a
/// working implementation rather than recalled.
/// </summary>
public static class ChatGptOAuth
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";

    private const int Port = 1455;
    private const string CallbackPath = "/auth/callback";
    private const string Redirect = "http://localhost:1455/auth/callback";

    private const string Scope = "openid profile email offline_access";
    private const string AuthClaim = "https://api.openai.com/auth";

    public const string PlansPage = "https://chatgpt.com/#pricing";

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Signs in. The whole thing happens here rather than being split across a begin and a finish,
    /// because the door has to stay open for the browser to come back through, and closing it
    /// early is the one way to break the flow.
    /// </summary>
    public static async Task<ChatGptSession> SignInAsync(Action<string> open, CancellationToken token)
    {
        string verifier = Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Url(RandomNumberGenerator.GetBytes(32));

        Loopback door;

        try
        {
            door = new Loopback(Port, CallbackPath);
        }
        catch (SocketException)
        {
            throw new InvalidOperationException(
                "Port 1455 is busy, and this sign in has to use it because that is the address"
                + " OpenAI registered. Close whatever is using it, most likely the Codex app"
                + " mid sign in, and try again.");
        }

        try
        {
            open(Authorize(challenge, state));

            System.Collections.Specialized.NameValueCollection answer = await door
                .WaitAsync(state, "You are signed in to ChatGPT. You can close this tab.", token)
                .ConfigureAwait(false);

            string code = answer["code"]?.Trim() ?? string.Empty;

            if (code.Length == 0)
            {
                throw new InvalidOperationException("ChatGPT did not send a code back. Try again.");
            }

            var body = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["redirect_uri"] = Redirect,
                ["code_verifier"] = verifier,
            };

            JsonNode granted = await PostAsync(body, token).ConfigureAwait(false);
            return Finish(granted, previousRefresh: string.Empty, previous: null);
        }
        finally
        {
            door.Dispose();
        }
    }

    /// <summary>Renews a sign in that has run out, without asking the person anything.</summary>
    public static async Task<ChatGptSession> RenewAsync(ChatGptSession previous, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(previous.RefreshToken))
        {
            throw new InvalidOperationException("There is no sign in to renew. Sign in to ChatGPT again.");
        }

        // OpenAI rotates the refresh token on every use, and sending a scope on a renewal makes
        // it treat the request as a re-scope, which can sign the account out everywhere. So the
        // renewal carries exactly three fields and no more.
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = previous.RefreshToken,
            ["client_id"] = ClientId,
        };

        JsonNode granted = await PostAsync(body, token).ConfigureAwait(false);
        return Finish(granted, previous.RefreshToken, previous);
    }

    private static string Authorize(string challenge, string state)
    {
        // Only the values are escaped, and the parameters are sent in the order the Codex client
        // sends them. A stricter encoder that turns the spaces in the scope into plus signs
        // produces a different string than the one OpenAI expects.
        (string Key, string Value)[] pairs =
        {
            ("response_type", "code"),
            ("client_id", ClientId),
            ("redirect_uri", Redirect),
            ("scope", Scope),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("id_token_add_organizations", "true"),
            ("codex_cli_simplified_flow", "true"),
            ("originator", "codex_cli_rs"),
            ("prompt", "login"),
            ("state", state),
        };

        var query = new StringBuilder(AuthorizeUrl);
        query.Append('?');

        for (int i = 0; i < pairs.Length; i++)
        {
            if (i > 0)
            {
                query.Append('&');
            }

            query.Append(pairs[i].Key).Append('=').Append(Uri.EscapeDataString(pairs[i].Value));
        }

        return query.ToString();
    }

    private static ChatGptSession Finish(JsonNode granted, string previousRefresh, ChatGptSession? previous)
    {
        string access = granted["access_token"]?.GetValue<string>() ?? string.Empty;

        if (access.Length == 0)
        {
            throw new InvalidOperationException("ChatGPT did not hand back a token.");
        }

        int seconds = granted["expires_in"]?.GetValue<int>() ?? 3600;

        // A renewal does not always carry a fresh id_token, so what was learned at sign in about
        // the workspace and the plan is kept rather than lost.
        Identity who = Read(granted["id_token"]?.GetValue<string>());

        return new ChatGptSession
        {
            AccessToken = access,
            RefreshToken = granted["refresh_token"]?.GetValue<string>() ?? previousRefresh,
            Expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 60)),
            Workspace = who.Workspace.Length > 0 ? who.Workspace : previous?.Workspace ?? string.Empty,
            Plan = who.Plan.Length > 0 ? who.Plan : previous?.Plan ?? string.Empty,
            Account = who.Account.Length > 0 ? who.Account : previous?.Account ?? string.Empty,
        };
    }

    private readonly record struct Identity(string Workspace, string Plan, string Account);

    /// <summary>
    /// Pulls the workspace, the plan and the email out of the id_token. The token came straight
    /// from the token endpoint over a secure connection, so its signature is not checked here.
    /// </summary>
    private static Identity Read(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return default;
        }

        try
        {
            string[] parts = idToken.Split('.');

            if (parts.Length < 2)
            {
                return default;
            }

            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload,
            };

            JsonNode? node = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));

            string account = node?["email"]?.GetValue<string>() ?? string.Empty;
            JsonNode? auth = node?[AuthClaim];

            string workspace = auth?["chatgpt_account_id"]?.GetValue<string>() ?? string.Empty;
            string plan = auth?["chatgpt_plan_type"]?.GetValue<string>() ?? string.Empty;

            // A person on a free personal plan who also belongs to a team keeps working under the
            // team, which is the workspace their subscription actually lives in.
            if (auth?["organizations"] is JsonArray orgs
                && (plan.Length == 0 || plan.Equals("free", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (JsonNode? org in orgs)
                {
                    bool isDefault = org?["is_default"]?.GetValue<bool>() ?? false;
                    string title = org?["title"]?.GetValue<string>()?.ToLowerInvariant() ?? string.Empty;

                    if (!isDefault
                        && (title.Contains("team") || title.Contains("business") || title.Contains("workspace")))
                    {
                        workspace = org?["id"]?.GetValue<string>() ?? workspace;
                        plan = "team";
                        break;
                    }
                }
            }

            return new Identity(workspace, plan, account);
        }
        catch
        {
            // Identity is a nicety. A token whose payload will not read still signs the person in.
            return default;
        }
    }

    private static async Task<JsonNode> PostAsync(Dictionary<string, string> body, CancellationToken token)
    {
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, TokenUrl)
        {
            Content = new System.Net.Http.FormUrlEncodedContent(body),
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using System.Net.Http.HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        JsonNode? node = null;

        try
        {
            node = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            // A body that is not json is reported by what it says instead.
        }

        if (!response.IsSuccessStatusCode || node is null)
        {
            throw new InvalidOperationException(Explain(node, text));
        }

        return node;
    }

    private static string Explain(JsonNode? node, string raw)
    {
        string name = node?["error"]?.GetValue<string>() ?? string.Empty;

        if (name is "invalid_grant" or "refresh_token_reused")
        {
            return "That sign in has expired or was already used. Sign in to ChatGPT again.";
        }

        string detail = node?["error_description"]?.GetValue<string>() ?? string.Empty;

        if (detail.Length > 0)
        {
            return "ChatGPT refused the sign in. " + detail;
        }

        if (name.Length > 0)
        {
            return "ChatGPT refused the sign in. " + name;
        }

        string text = raw.Trim();
        return text.Length == 0
            ? "ChatGPT refused the sign in and gave no reason."
            : "ChatGPT refused the sign in. " + (text.Length <= 200 ? text : text[..200] + "...");
    }

    private static string Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
