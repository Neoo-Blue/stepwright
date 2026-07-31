using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stepwright.Publish;

/// <summary>
/// Talks to Confluence. Unlike Hudu it will not take a picture carried inside the markup, so
/// a page goes across in two moves: the page is created, then each picture is attached to it
/// and referred to by name from the text that was already written.
/// </summary>
public sealed class ConfluenceClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly string _base;

    /// <summary>Where a person opens the page, which is not always where the requests go.</summary>
    private readonly string _browse = string.Empty;

    public ConfluenceClient(string siteUrl, string email, string apiToken)
    {
        string site = (siteUrl ?? string.Empty).Trim().TrimEnd('/');

        if (string.IsNullOrEmpty(site))
        {
            throw new InvalidOperationException("Confluence needs the address of your site.");
        }

        if (!site.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            site = "https://" + site;
        }

        // The address is usually given as the site, while the api sits under wiki.
        _base = site.EndsWith("/wiki", StringComparison.OrdinalIgnoreCase) ? site : site + "/wiki";
        _browse = _base;

        string pair = $"{(email ?? string.Empty).Trim()}:{(apiToken ?? string.Empty).Trim()}";
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));

        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// The same site reached through a browser sign in. Atlassian routes those requests through
    /// its own gateway, where the site is named by its identifier rather than by its address,
    /// so only the front of the address differs and every path below stays the same.
    /// </summary>
    private ConfluenceClient(AtlassianSession session)
    {
        if (string.IsNullOrWhiteSpace(session.CloudId))
        {
            throw new InvalidOperationException("This sign in does not say which Confluence site it covers.");
        }

        _base = $"https://api.atlassian.com/ex/confluence/{session.CloudId.Trim()}/wiki";

        string site = (session.SiteUrl ?? string.Empty).TrimEnd('/');
        _browse = site.Length == 0
            ? _base
            : site.EndsWith("/wiki", StringComparison.OrdinalIgnoreCase) ? site : site + "/wiki";

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// Builds a client for whichever route the settings say, renewing a sign in that has run
    /// out. The settings are saved again when that happens, so the next run starts ready.
    /// </summary>
    public static async Task<ConfluenceClient> CreateAsync(Config.AppSettings settings, CancellationToken token)
    {
        if (!settings.ConfluenceUsesOAuth)
        {
            return new ConfluenceClient(
                settings.ConfluenceSite,
                settings.ConfluenceEmail,
                settings.GetConfluenceToken());
        }

        if (!settings.HasConfluenceSignIn)
        {
            throw new InvalidOperationException("Sign in to Atlassian in Settings first.");
        }

        string access = settings.GetConfluenceAccess();

        if (string.IsNullOrEmpty(access) || settings.ConfluenceAccessExpires <= DateTimeOffset.UtcNow)
        {
            AtlassianSession renewed = await AtlassianOAuth.RefreshAsync(
                settings.ConfluenceClientId,
                settings.GetConfluenceSecret(),
                settings.GetConfluenceRefresh(),
                settings.ConfluenceCloudId,
                token).ConfigureAwait(false);

            settings.RememberConfluence(renewed);
            settings.Save();
            return new ConfluenceClient(renewed);
        }

        return new ConfluenceClient(new AtlassianSession
        {
            AccessToken = access,
            RefreshToken = string.Empty,
            Expires = settings.ConfluenceAccessExpires,
            CloudId = settings.ConfluenceCloudId,
            SiteUrl = settings.ConfluenceSite,
            SiteName = settings.ConfluenceSiteName,
        });
    }

    public async Task<string> CheckAsync(CancellationToken token)
    {
        JsonNode? reply = await SendAsync(HttpMethod.Get, "/api/v2/spaces?limit=1", null, token)
            .ConfigureAwait(false);

        return reply is null ? "Connected." : "Connected to " + _browse + ".";
    }

    public async Task<List<PublishTarget>> SpacesAsync(CancellationToken token)
    {
        var targets = new List<PublishTarget>();
        string? cursor = null;

        for (int page = 0; page < 20; page++)
        {
            string path = "/api/v2/spaces?limit=100" + (cursor is null ? string.Empty : "&cursor=" + cursor);
            JsonNode? reply = await SendAsync(HttpMethod.Get, path, null, token).ConfigureAwait(false);

            if (reply?["results"] is not JsonArray results)
            {
                break;
            }

            foreach (JsonNode? space in results)
            {
                string id = space?["id"]?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                targets.Add(new PublishTarget
                {
                    Id = id,
                    Name = space?["name"]?.GetValue<string>() ?? "Space " + id,
                    Detail = space?["key"]?.GetValue<string>() ?? string.Empty,
                });
            }

            cursor = Cursor(reply);
            if (cursor is null)
            {
                break;
            }
        }

        return targets.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Pages already in a space, so a guide can be filed under one of them.</summary>
    public async Task<List<PublishTarget>> PagesAsync(string spaceId, CancellationToken token)
    {
        var targets = new List<PublishTarget>
        {
            new() { Id = string.Empty, Name = "At the top of the space" },
        };

        if (string.IsNullOrEmpty(spaceId))
        {
            return targets;
        }

        string? cursor = null;

        for (int page = 0; page < 10; page++)
        {
            string path = $"/api/v2/spaces/{spaceId}/pages?limit=100"
                + (cursor is null ? string.Empty : "&cursor=" + cursor);

            JsonNode? reply = await SendAsync(HttpMethod.Get, path, null, token).ConfigureAwait(false);

            if (reply?["results"] is not JsonArray results)
            {
                break;
            }

            foreach (JsonNode? item in results)
            {
                string id = item?["id"]?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                targets.Add(new PublishTarget
                {
                    Id = id,
                    Name = item?["title"]?.GetValue<string>() ?? "Page " + id,
                    Detail = "under this",
                });
            }

            cursor = Cursor(reply);
            if (cursor is null)
            {
                break;
            }
        }

        return targets;
    }

    /// <summary>
    /// Creates the page, then attaches each picture. The markup written earlier already
    /// refers to them by the names used here.
    /// </summary>
    public async Task<string> PublishAsync(
        string title,
        string storageHtml,
        string spaceId,
        string parentId,
        IReadOnlyDictionary<int, byte[]> pictures,
        bool jpeg,
        IProgress<string>? progress,
        CancellationToken token)
    {
        var body = new JsonObject
        {
            ["spaceId"] = spaceId,
            ["status"] = "current",
            ["title"] = title,
            ["body"] = new JsonObject
            {
                ["representation"] = "storage",
                ["value"] = storageHtml,
            },
        };

        if (!string.IsNullOrEmpty(parentId))
        {
            body["parentId"] = parentId;
        }

        progress?.Report("Creating the page...");
        JsonNode? reply = await SendAsync(HttpMethod.Post, "/api/v2/pages", body, token).ConfigureAwait(false);

        string pageId = reply?["id"]?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(pageId))
        {
            throw new InvalidOperationException("Confluence created the page but did not say which one.");
        }

        int done = 0;

        foreach ((int number, byte[] picture) in pictures.OrderBy(p => p.Key))
        {
            done++;
            progress?.Report($"Attaching picture {done} of {pictures.Count}...");
            await AttachAsync(pageId, $"step{number:D3}.{(jpeg ? "jpg" : "png")}", picture, jpeg, token)
                .ConfigureAwait(false);
        }

        string? link = reply?["_links"]?["webui"]?.GetValue<string>();
        return string.IsNullOrEmpty(link) ? $"{_browse}/pages/{pageId}" : _browse + link;
    }

    /// <summary>
    /// Attachments still go through the older interface, which is the only one that takes a
    /// file, and it insists on a header saying the request is deliberate.
    /// </summary>
    private async Task AttachAsync(string pageId, string name, byte[] data, bool jpeg, CancellationToken token)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(jpeg ? "image/jpeg" : "image/png");
        content.Add(file, "file", name);
        content.Add(new StringContent("true"), "minorEdit");

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{_base}/rest/api/content/{pageId}/child/attachment");

        request.Headers.Add("X-Atlassian-Token", "no-check");
        request.Content = content;

        using HttpResponseMessage response = await _http.SendAsync(request, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"The picture {name} could not be attached. Confluence replied with {(int)response.StatusCode}. {Describe(raw)}");
        }
    }

    private static string? Cursor(JsonNode? reply)
    {
        string? next = reply?["_links"]?["next"]?.GetValue<string>();
        if (string.IsNullOrEmpty(next))
        {
            return null;
        }

        int mark = next.IndexOf("cursor=", StringComparison.OrdinalIgnoreCase);
        if (mark < 0)
        {
            return null;
        }

        string value = next[(mark + 7)..];
        int end = value.IndexOf('&');
        return end > 0 ? value[..end] : value;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, _base + path);

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _http.SendAsync(request, token).ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Confluence replied with {(int)response.StatusCode}. {Describe(raw)}");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Describe(string raw)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(raw);

            if (node?["errors"] is JsonArray errors && errors.Count > 0)
            {
                string? title = errors[0]?["title"]?.GetValue<string>();
                string? detail = errors[0]?["detail"]?.GetValue<string>();
                string joined = string.Join(" ", new[] { title, detail }.Where(t => !string.IsNullOrEmpty(t)));

                if (!string.IsNullOrWhiteSpace(joined))
                {
                    return joined;
                }
            }

            string? message = node?["message"]?.GetValue<string>();
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
