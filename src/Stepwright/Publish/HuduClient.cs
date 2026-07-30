using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stepwright.Publish;

/// <summary>Something a guide can be filed under, with the name a person recognises.</summary>
public sealed class PublishTarget
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = string.Empty;

    public override string ToString() => string.IsNullOrEmpty(Detail) ? Name : $"{Name}   {Detail}";
}

/// <summary>
/// Talks to Hudu. The site keeps articles as HTML with the pictures carried inside them, so a
/// guide goes across in one request with nothing to attach afterwards.
/// </summary>
public sealed class HuduClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly string _base;
    private readonly string _key;

    public HuduClient(string baseUrl, string apiKey)
    {
        _base = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        _key = (apiKey ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(_base))
        {
            throw new InvalidOperationException("Hudu needs the address of your site.");
        }

        if (!_base.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _base = "https://" + _base;
        }
    }

    public async Task<string> CheckAsync(CancellationToken token)
    {
        JsonNode? reply = await SendAsync(HttpMethod.Get, "/companies?page=1&page_size=1", null, token)
            .ConfigureAwait(false);

        return reply is null ? "Connected." : "Connected to " + _base + ".";
    }

    /// <summary>Every company, plus the shared library that belongs to no company.</summary>
    public async Task<List<PublishTarget>> CompaniesAsync(CancellationToken token)
    {
        var targets = new List<PublishTarget>
        {
            new() { Id = string.Empty, Name = "Global knowledge base", Detail = "no company" },
        };

        foreach (JsonNode? company in await PagesAsync("/companies", "companies", token).ConfigureAwait(false))
        {
            if (company is null)
            {
                continue;
            }

            if (company["archived"]?.GetValue<bool>() == true)
            {
                continue;
            }

            int id = company["id"]?.GetValue<int>() ?? 0;
            if (id <= 0)
            {
                continue;
            }

            targets.Add(new PublishTarget
            {
                Id = id.ToString(),
                Name = company["name"]?.GetValue<string>() ?? "Company " + id,
            });
        }

        return targets;
    }

    public async Task<List<PublishTarget>> FoldersAsync(string companyId, CancellationToken token)
    {
        var targets = new List<PublishTarget>
        {
            new() { Id = string.Empty, Name = "No folder", Detail = "the top of the knowledge base" },
        };

        string query = string.IsNullOrEmpty(companyId) ? string.Empty : "&company_id=" + companyId;

        foreach (JsonNode? folder in await PagesAsync("/folders" + (query.Length > 0 ? "?" + query.TrimStart('&') : string.Empty), "folders", token).ConfigureAwait(false))
        {
            if (folder is null)
            {
                continue;
            }

            int id = folder["id"]?.GetValue<int>() ?? 0;
            if (id <= 0)
            {
                continue;
            }

            // A folder belonging to a company must not appear under a different one.
            string owner = folder["company_id"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(companyId) && !string.IsNullOrEmpty(owner) && owner != companyId)
            {
                continue;
            }

            if (string.IsNullOrEmpty(companyId) && !string.IsNullOrEmpty(owner) && owner != "0")
            {
                continue;
            }

            targets.Add(new PublishTarget
            {
                Id = id.ToString(),
                Name = folder["name"]?.GetValue<string>() ?? "Folder " + id,
            });
        }

        return targets;
    }

    /// <summary>Articles already there, so an existing one can be replaced rather than doubled.</summary>
    public async Task<List<PublishTarget>> ArticlesAsync(string companyId, CancellationToken token)
    {
        var targets = new List<PublishTarget>
        {
            new() { Id = string.Empty, Name = "Create a new article" },
        };

        string path = string.IsNullOrEmpty(companyId) ? "/articles" : "/articles?company_id=" + companyId;

        foreach (JsonNode? article in await PagesAsync(path, "articles", token).ConfigureAwait(false))
        {
            if (article is null)
            {
                continue;
            }

            int id = article["id"]?.GetValue<int>() ?? 0;
            if (id <= 0)
            {
                continue;
            }

            targets.Add(new PublishTarget
            {
                Id = id.ToString(),
                Name = article["name"]?.GetValue<string>() ?? "Article " + id,
                Detail = "replace",
            });
        }

        return targets;
    }

    /// <summary>Creates the article, or replaces one when an identifier is given.</summary>
    public async Task<string> PublishAsync(
        string title,
        string html,
        string companyId,
        string folderId,
        string articleId,
        CancellationToken token)
    {
        var article = new JsonObject
        {
            ["name"] = title,
            ["content"] = html,
        };

        if (int.TryParse(companyId, out int company) && company > 0)
        {
            article["company_id"] = company;
        }

        if (int.TryParse(folderId, out int folder) && folder > 0)
        {
            article["folder_id"] = folder;
        }

        var body = new JsonObject { ["article"] = article };
        bool replacing = int.TryParse(articleId, out int existing) && existing > 0;

        JsonNode? reply = await SendAsync(
            replacing ? HttpMethod.Put : HttpMethod.Post,
            replacing ? "/articles/" + existing : "/articles",
            body,
            token).ConfigureAwait(false);

        JsonNode? created = reply?["article"] ?? reply;
        string? url = created?["url"]?.GetValue<string>();

        if (!string.IsNullOrEmpty(url))
        {
            return url;
        }

        int id = created?["id"]?.GetValue<int>() ?? existing;
        return id > 0 ? $"{_base}/a/{id}" : _base;
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<List<JsonNode?>> PagesAsync(string path, string key, CancellationToken token)
    {
        var all = new List<JsonNode?>();
        const int pageSize = 100;

        for (int page = 1; page <= 25; page++)
        {
            string separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            string query = $"{path}{separator}page={page}&page_size={pageSize}";

            JsonNode? reply = await SendAsync(HttpMethod.Get, query, null, token).ConfigureAwait(false);
            JsonArray? batch = reply?[key] as JsonArray ?? reply as JsonArray;

            if (batch is null || batch.Count == 0)
            {
                break;
            }

            all.AddRange(batch);

            if (batch.Count < pageSize)
            {
                break;
            }
        }

        return all;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, _base + "/api/v1" + path);
        request.Headers.Add("x-api-key", _key);
        request.Headers.Add("accept", "application/json");

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await _http.SendAsync(request, token).ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Hudu replied with {(int)response.StatusCode}. {Describe(raw)}");
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
            string? message = node?["error"]?.ToString() ?? node?["message"]?.ToString();

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
