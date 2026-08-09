using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Publishing;

public sealed partial class WorkshopCatalogService
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;

    public WorkshopCatalogService() : this(SharedHttpClient) { }

    public WorkshopCatalogService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WorkshopCatalogPage> SearchAsync(WorkshopCatalogQuery query, CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize();
        var cacheKey = $"{normalized.SearchText}|{normalized.Sort}|{normalized.Page}|{normalized.RequiredTag}";
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CreatedAt < TimeSpan.FromMinutes(5))
            return cached.Page;

        IReadOnlyList<ulong> ids;
        if (ulong.TryParse(normalized.SearchText, out var directId) && directId != 0)
        {
            ids = [directId];
        }
        else
        {
            var browseUrl = BuildBrowseUrl(normalized);
            using var response = await _httpClient.GetAsync(browseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            ids = WorkshopIdRegex().Matches(html)
                .Select(match => ulong.TryParse(match.Groups[1].Value, out var id) ? id : 0)
                .Where(id => id != 0)
                .Distinct()
                .Take(30)
                .ToArray();
        }

        var details = await GetDetailsAsync(ids, cancellationToken);
        if (!string.IsNullOrWhiteSpace(normalized.RequiredTag))
            details = details.Where(item => item.Tags.Contains(normalized.RequiredTag, StringComparer.OrdinalIgnoreCase)).ToArray();

        var page = new WorkshopCatalogPage(details, normalized.Page, normalized.Page > 1, ids.Count >= 20, normalized);
        _cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow, page);
        return page;
    }

    public async Task<IReadOnlyList<WorkshopCatalogItem>> GetDetailsAsync(IReadOnlyList<ulong> workshopIds, CancellationToken cancellationToken = default)
    {
        var ids = workshopIds.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length == 0) return [];

        var batches = await Task.WhenAll(ids
            .Chunk(50)
            .Select(batch => GetDetailsBatchAsync(batch, cancellationToken)));
        var byId = batches
            .SelectMany(batch => batch)
            .ToDictionary(item => item.WorkshopId);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
    }

    public async Task<WorkshopRemoteState?> GetRemoteStateAsync(ulong workshopId, CancellationToken cancellationToken = default)
    {
        if (workshopId == 0) return null;
        var values = new List<KeyValuePair<string, string>>
        {
            new("itemcount", "1"),
            new("publishedfileids[0]", workshopId.ToString(CultureInfo.InvariantCulture))
        };
        using var content = new FormUrlEncodedContent(values);
        using var response = await _httpClient.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("response", out var responseNode) ||
            !responseNode.TryGetProperty("publishedfiledetails", out var details) ||
            details.GetArrayLength() == 0) return null;

        var detail = details[0];
        if (ReadLong(detail, "result") != 1 || ReadUlong(detail, "publishedfileid") != workshopId) return null;
        return new WorkshopRemoteState(
            workshopId,
            ReadString(detail, "hcontent_file"),
            ReadString(detail, "hcontent_preview"),
            ReadLong(detail, "file_size"),
            FromUnixTime(ReadLong(detail, "time_updated")),
            ReadString(detail, "title"),
            ReadString(detail, "description"),
            (int)ReadLong(detail, "visibility"),
            ReadLong(detail, "consumer_app_id"),
            ReadLong(detail, "creator_app_id"),
            ReadLong(detail, "banned") != 0);
    }

    private async Task<IReadOnlyList<WorkshopCatalogItem>> GetDetailsBatchAsync(IReadOnlyList<ulong> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return [];

        var values = new List<KeyValuePair<string, string>>(ids.Count + 1)
        {
            new("itemcount", ids.Count.ToString(CultureInfo.InvariantCulture))
        };
        for (var index = 0; index < ids.Count; index++)
            values.Add(new($"publishedfileids[{index}]", ids[index].ToString(CultureInfo.InvariantCulture)));

        using var content = new FormUrlEncodedContent(values);
        using var response = await _httpClient.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("response", out var responseNode) ||
            !responseNode.TryGetProperty("publishedfiledetails", out var detailNodes)) return [];

        var byId = new Dictionary<ulong, WorkshopCatalogItem>();
        foreach (var detail in detailNodes.EnumerateArray())
        {
            var id = ReadUlong(detail, "publishedfileid");
            if (id == 0 || ReadLong(detail, "result") != 1) continue;
            var tags = detail.TryGetProperty("tags", out var tagNodes)
                ? tagNodes.EnumerateArray().Select(tag => ReadString(tag, "tag")).Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            byId[id] = new WorkshopCatalogItem(
                id,
                ReadString(detail, "title", $"Workshop item {id}"),
                CleanDescription(ReadString(detail, "file_description")),
                SafePreviewUrl(ReadString(detail, "preview_url")),
                FromUnixTime(ReadLong(detail, "time_updated")),
                ReadLong(detail, "file_size"),
                ReadLong(detail, "subscriptions"),
                ReadLong(detail, "favorited"),
                ReadLong(detail, "views"),
                tags);
        }

        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
    }

    private static string BuildBrowseUrl(WorkshopCatalogQuery query)
    {
        var sort = query.Sort switch
        {
            "recent" => "mostrecent",
            "subscribed" => "totaluniquesubscribers",
            "popular" => "totalvisitors",
            "relevance" when !string.IsNullOrWhiteSpace(query.SearchText) => "textsearch",
            _ => "trend"
        };
        var values = new List<string>
        {
            $"appid={PzasmConstants.ProjectZomboidSteamAppId}",
            "section=readytouseitems",
            $"browsesort={sort}",
            $"p={query.Page}",
            $"searchtext={Uri.EscapeDataString(query.SearchText)}"
        };
        if (!string.IsNullOrWhiteSpace(query.RequiredTag))
            values.Add($"requiredtags%5B%5D={Uri.EscapeDataString(query.RequiredTag)}");
        return "https://steamcommunity.com/workshop/browse/?" + string.Join('&', values);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PZAdvancedServerManager/1.0");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9,en;q=0.8");
        return client;
    }

    private static string ReadString(JsonElement node, string property, string fallback = "")
    {
        if (!node.TryGetProperty(property, out var value)) return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
    }

    private static long ReadLong(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.ToString(), CultureInfo.InvariantCulture, out number) ? number : 0;
    }

    private static ulong ReadUlong(JsonElement node, string property) =>
        ulong.TryParse(ReadString(node, property), CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static DateTimeOffset? FromUnixTime(long value) => value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : null;

    private static string SafePreviewUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return string.Empty;
        return uri.Host.EndsWith("steamusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("akamaihd.net", StringComparison.OrdinalIgnoreCase) ? uri.AbsoluteUri : string.Empty;
    }

    private static string CleanDescription(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        decoded = BbCodeRegex().Replace(decoded, " ");
        decoded = HtmlTagRegex().Replace(decoded, " ");
        decoded = WhitespaceRegex().Replace(decoded, " ").Trim();
        return decoded.Length <= 260 ? decoded : decoded[..257] + "…";
    }

    [GeneratedRegex("<a\\s+href=\"https://steamcommunity\\.com/sharedfiles/filedetails/\\?id=(\\d+)\"[^>]*>\\s*<img\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkshopIdRegex();

    [GeneratedRegex(@"\[[^\]]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex BbCodeRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record CacheEntry(DateTimeOffset CreatedAt, WorkshopCatalogPage Page);
}

public sealed record WorkshopCatalogQuery(string SearchText = "", string Sort = "trend", int Page = 1, string RequiredTag = "")
{
    public WorkshopCatalogQuery Normalize()
    {
        var allowedSorts = new HashSet<string>(["trend", "recent", "subscribed", "popular", "relevance"], StringComparer.OrdinalIgnoreCase);
        var sort = Sort ?? string.Empty;
        return new WorkshopCatalogQuery(
            (SearchText ?? string.Empty).Trim(),
            allowedSorts.Contains(sort) ? sort.ToLowerInvariant() : "trend",
            Math.Clamp(Page, 1, 1000),
            (RequiredTag ?? string.Empty).Trim());
    }
}

public sealed record WorkshopCatalogPage(IReadOnlyList<WorkshopCatalogItem> Items, int Page, bool HasPrevious, bool HasNext, WorkshopCatalogQuery Query);

public sealed record WorkshopCatalogItem(
    ulong WorkshopId,
    string Title,
    string Description,
    string PreviewUrl,
    DateTimeOffset? UpdatedAt,
    long FileSize,
    long Subscriptions,
    long Favorites,
    long Views,
    IReadOnlyList<string> Tags)
{
    public string WorkshopUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopId}";
}

public sealed record WorkshopRemoteState(
    ulong WorkshopId,
    string ContentHandle,
    string PreviewHandle,
    long FileSize,
    DateTimeOffset? UpdatedAt,
    string Title,
    string Description,
    int Visibility,
    long ConsumerAppId,
    long CreatorAppId,
    bool Banned);
