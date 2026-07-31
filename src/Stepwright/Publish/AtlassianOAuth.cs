using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Stepwright.Publish;

/// <summary>One Confluence site the sign in covers.</summary>
public sealed class AtlassianSite
{
    public required string CloudId { get; init; }

    /// <summary>The address a person recognises, for example https://yourcompany.atlassian.net.</summary>
    public required string Url { get; init; }

    public required string Name { get; init; }

    public override string ToString() => string.IsNullOrEmpty(Url) ? Name : $"{Name}   {Url}";
}

/// <summary>What a finished sign in leaves behind.</summary>
public sealed class AtlassianSession
{
    public required string AccessToken { get; init; }

    public required string RefreshToken { get; init; }

    public required DateTimeOffset Expires { get; init; }

    /// <summary>Identifies the site the token may be used against.</summary>
    public required string CloudId { get; init; }

    /// <summary>The address a person recognises, for example https://yourcompany.atlassian.net.</summary>
    public required string SiteUrl { get; init; }

    public required string SiteName { get; init; }

    /// <summary>
    /// Every site this sign in covers. A person who works for one company sees one. A person
    /// who supports several customers sees several, and which one they meant is not something
    /// this app is entitled to guess.
    /// </summary>
    public IReadOnlyList<AtlassianSite> Sites { get; init; } = Array.Empty<AtlassianSite>();
}

/// <summary>
/// Signing in to Atlassian the way their own documentation describes: the browser asks the
/// person, the answer comes back to a listener on this machine, and that answer is traded for
/// a token that can be renewed without asking again.
///
/// Atlassian issues these tokens to an application you register once, so the identifier and
/// the secret belong to your own company rather than to Stepwright.
/// </summary>
public static class AtlassianOAuth
{
    /// <summary>The address the browser is sent back to. It has to match the one registered.</summary>
    public const int CallbackPort = 53682;

    public static string CallbackUrl => $"http://localhost:{CallbackPort}/callback";

    public const string ConsolePage = "https://developer.atlassian.com/console/myapps/";

    /// <summary>
    /// The older style of permission, which covers both interfaces this app uses. Attachments
    /// still go through the first one, and an application may not mix the two styles.
    /// </summary>
    private static readonly string[] Scopes =
    {
        "read:confluence-space.summary",
        "read:confluence-content.all",
        "write:confluence-content",
        "write:confluence-file",
        "offline_access",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Opens the browser, waits for the person to agree, and comes back with a usable session.
    /// </summary>
    public static async Task<AtlassianSession> SignInAsync(
        string clientId,
        string clientSecret,
        Action<string>? progress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Signing in needs the identifier and the secret of your own Atlassian application.");
        }

        string state = Guid.NewGuid().ToString("n");

        var listener = new TcpListener(IPAddress.Loopback, CallbackPort);

        try
        {
            listener.Start();
        }
        catch (SocketException error)
        {
            throw new InvalidOperationException(
                $"Nothing could listen on port {CallbackPort}. Close whatever is using it and try again. {error.Message}");
        }

        try
        {
            var address = new StringBuilder("https://auth.atlassian.com/authorize?audience=api.atlassian.com");
            address.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
            address.Append("&scope=").Append(Uri.EscapeDataString(string.Join(' ', Scopes)));
            address.Append("&redirect_uri=").Append(Uri.EscapeDataString(CallbackUrl));
            address.Append("&state=").Append(state);
            address.Append("&response_type=code&prompt=consent");

            progress?.Invoke("Waiting for the browser...");
            Open(address.ToString());

            string code = await WaitForCodeAsync(listener, state, token).ConfigureAwait(false);

            progress?.Invoke("Trading the answer for a token...");

            JsonNode granted = await PostAsync(
                new JsonObject
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["code"] = code,
                    ["redirect_uri"] = CallbackUrl,
                },
                token).ConfigureAwait(false);

            return await FinishAsync(granted, string.Empty, chosen: null, token).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Signing in through the published broker. The person presses one button: everything they
    /// would otherwise have registered themselves already exists on the other side of it.
    ///
    /// The tokens come back by ticket rather than in the address, so they never touch browser
    /// history, and the ticket is good once.
    /// </summary>
    public static async Task<AtlassianSession> SignInThroughBrokerAsync(
        Action<string>? progress,
        CancellationToken token)
    {
        if (!Connect.HasBroker)
        {
            throw new InvalidOperationException("This copy of Stepwright has no sign in broker.");
        }

        string state = Guid.NewGuid().ToString("n") + Guid.NewGuid().ToString("n");
        var listener = new TcpListener(IPAddress.Loopback, CallbackPort);

        try
        {
            listener.Start();
        }
        catch (SocketException error)
        {
            throw new InvalidOperationException(
                $"Nothing could listen on port {CallbackPort}. Close whatever is using it and try again. {error.Message}");
        }

        try
        {
            progress?.Invoke("Waiting for the browser...");
            Open($"{Connect.Broker}/start?port={CallbackPort}&state={state}");

            string ticket = await WaitForAsync(listener, "ticket", state: null, token).ConfigureAwait(false);

            progress?.Invoke("Collecting the sign in...");

            using var claim = new HttpRequestMessage(HttpMethod.Post, Connect.Broker + "/claim")
            {
                Content = new StringContent(
                    new JsonObject { ["ticket"] = ticket }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"),
            };

            using HttpResponseMessage response = await Http.SendAsync(claim, token).ConfigureAwait(false);
            string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || JsonNode.Parse(raw) is not JsonNode granted)
            {
                throw new InvalidOperationException("The sign in could not be collected. Try again.");
            }

            return await FinishAsync(granted, string.Empty, chosen: null, token).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Renews a sign in that was made through the broker, which holds the secret.</summary>
    public static async Task<AtlassianSession> RefreshThroughBrokerAsync(
        string refreshToken,
        string keepCloudId,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Connect.Broker + "/refresh")
        {
            Content = new StringContent(
                new JsonObject { ["refresh_token"] = refreshToken }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode || JsonNode.Parse(raw) is not JsonNode granted)
        {
            throw new InvalidOperationException("That sign in could not be renewed. Sign in again in Settings.");
        }

        return await FinishAsync(granted, refreshToken, keepCloudId, token).ConfigureAwait(false);
    }

    /// <summary>Renews a session that has run out, without asking the person anything.</summary>
    public static async Task<AtlassianSession> RefreshAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        string keepCloudId,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("There is no sign in to renew. Sign in to Atlassian again.");
        }

        JsonNode granted = await PostAsync(
            new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
            },
            token).ConfigureAwait(false);

        // A renewal does not always hand back a new one, so the old one is kept. The site is
        // kept as well: renewing a sign in is not an invitation to move a person's work to a
        // different customer.
        return await FinishAsync(granted, refreshToken, keepCloudId, token).ConfigureAwait(false);
    }

    private static async Task<AtlassianSession> FinishAsync(
        JsonNode granted,
        string previousRefresh,
        string? chosen,
        CancellationToken token)
    {
        string access = granted["access_token"]?.GetValue<string>() ?? string.Empty;

        if (string.IsNullOrEmpty(access))
        {
            throw new InvalidOperationException("Atlassian did not hand back a token.");
        }

        int seconds = granted["expires_in"]?.GetValue<int>() ?? 3600;
        string refresh = granted["refresh_token"]?.GetValue<string>() ?? previousRefresh;

        IReadOnlyList<AtlassianSite> sites = await SitesAsync(access, token).ConfigureAwait(false);

        // When a site was already chosen it stays chosen. Otherwise the first is only a
        // starting point: the caller asks the person when there is more than one.
        AtlassianSite site = sites.FirstOrDefault(s => s.CloudId == chosen) ?? sites[0];

        return new AtlassianSession
        {
            AccessToken = access,
            RefreshToken = refresh,

            // A minute is taken off so a request cannot start on a token that ends mid flight.
            Expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 60)),
            CloudId = site.CloudId,
            SiteUrl = site.Url,
            SiteName = site.Name,
            Sites = sites,
        };
    }

    /// <summary>Every site the token was granted for, in the order Atlassian returned them.</summary>
    public static async Task<IReadOnlyList<AtlassianSite>> SitesAsync(
        string access,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.atlassian.com/oauth/token/accessible-resources");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Atlassian would not say which sites this sign in covers. It replied with {(int)response.StatusCode}.");
        }

        if (JsonNode.Parse(raw) is not JsonArray sites || sites.Count == 0)
        {
            throw new InvalidOperationException(
                "This sign in covers no Confluence site. Check that the application has the Confluence permissions.");
        }

        var found = new List<AtlassianSite>();

        foreach (JsonNode? entry in sites)
        {
            string id = entry?["id"]?.GetValue<string>() ?? string.Empty;

            if (id.Length == 0)
            {
                continue;
            }

            found.Add(new AtlassianSite
            {
                CloudId = id,
                Url = (entry?["url"]?.GetValue<string>() ?? string.Empty).TrimEnd('/'),
                Name = entry?["name"]?.GetValue<string>() ?? "your site",
            });
        }

        if (found.Count == 0)
        {
            throw new InvalidOperationException(
                "This sign in covers no Confluence site. Check that the application has the Confluence permissions.");
        }

        return found;
    }

    private static async Task<JsonNode> PostAsync(JsonObject body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.atlassian.com/oauth/token")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string detail = string.Empty;

            try
            {
                JsonNode? node = JsonNode.Parse(raw);
                detail = node?["error_description"]?.GetValue<string>()
                    ?? node?["error"]?.GetValue<string>()
                    ?? string.Empty;
            }
            catch
            {
                // Not json, and the status alone still says enough.
            }

            throw new InvalidOperationException(
                $"Atlassian refused the sign in with {(int)response.StatusCode}. {detail}".TrimEnd());
        }

        return JsonNode.Parse(raw) ?? throw new InvalidOperationException("Atlassian sent nothing back.");
    }

    /// <summary>
    /// Reads the one request the browser makes when it comes back. Only the first line matters,
    /// which is why this is a socket rather than anything larger.
    /// </summary>
    private static Task<string> WaitForCodeAsync(TcpListener listener, string state, CancellationToken token) =>
        WaitForAsync(listener, "code", state, token);

    private static async Task<string> WaitForAsync(
        TcpListener listener,
        string wanted,
        string? state,
        CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();

            var buffer = new byte[4096];
            int read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
            string head = Encoding.UTF8.GetString(buffer, 0, read);

            int start = head.IndexOf(' ') + 1;
            int end = head.IndexOf(' ', Math.Max(start, 1));

            if (start <= 0 || end <= start)
            {
                await ReplyAsync(stream, "Stepwright could not read that.", stop.Token).ConfigureAwait(false);
                continue;
            }

            string target = head[start..end];

            // The browser asks for the icon as well, and that is not the answer being waited for.
            if (!target.StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
            {
                await ReplyAsync(stream, "Nothing to see here.", stop.Token).ConfigureAwait(false);
                continue;
            }

            System.Collections.Specialized.NameValueCollection query =
                System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + target).Query);

            string? error = query["error_description"] ?? query["error"];

            if (!string.IsNullOrEmpty(error))
            {
                await ReplyAsync(stream, "Atlassian said no. You can close this tab.", stop.Token).ConfigureAwait(false);
                throw new InvalidOperationException("Atlassian refused the sign in. " + error);
            }

            if (state is not null && !string.Equals(query["state"], state, StringComparison.Ordinal))
            {
                await ReplyAsync(stream, "That answer was not the one asked for.", stop.Token).ConfigureAwait(false);
                throw new InvalidOperationException("The answer from the browser did not match the request.");
            }

            string code = query[wanted] ?? string.Empty;

            if (string.IsNullOrEmpty(code))
            {
                await ReplyAsync(stream, "That answer carried nothing.", stop.Token).ConfigureAwait(false);
                continue;
            }

            await ReplyAsync(stream, "Stepwright is signed in. You can close this tab.", stop.Token)
                .ConfigureAwait(false);

            return code;
        }
    }

    private static async Task ReplyAsync(NetworkStream stream, string message, CancellationToken token)
    {
        string page =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Stepwright</title></head>"
            + "<body style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;padding:48px;\">"
            + "<h2>Stepwright</h2><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";

        byte[] body = Encoding.UTF8.GetBytes(page);

        var head = new StringBuilder();
        head.Append("HTTP/1.1 200 OK\r\n");
        head.Append("Content-Type: text/html; charset=utf-8\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");

        byte[] header = Encoding.ASCII.GetBytes(head.ToString());

        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static void Open(string address)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = address;
            process.StartInfo.UseShellExecute = true;
            process.Start();
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "The browser could not be opened. Sign in there and come back. " + error.Message);
        }
    }
}
