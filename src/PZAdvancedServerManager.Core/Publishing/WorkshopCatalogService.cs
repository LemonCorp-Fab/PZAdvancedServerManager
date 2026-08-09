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
    private readonly ConcurrentDictionary<string, BrowseCacheEntry> _browseCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ulong, RequiredItemsCacheEntry> _requiredItemsCache = new();
    private readonly HttpClient _httpClient;

    public WorkshopCatalogService() : this(SharedHttpClient) { }

    public WorkshopCatalogService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WorkshopCatalogPage> SearchAsync(WorkshopCatalogQuery query, CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize();
        var cacheKey = normalized.CacheKey;
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CreatedAt < TimeSpan.FromMinutes(5))
            return cached.Page;

        IReadOnlyList<ulong> ids;
        var sourcePagesScanned = 0;
        var hasNext = false;
        if (ulong.TryParse(normalized.SearchText, out var directId) && directId != 0)
        {
            ids = [directId];
        }
        else
        {
            var firstSourcePage = ((normalized.Page - 1) * normalized.ScanPages) + 1;
            var pageIds = await Task.WhenAll(Enumerable.Range(firstSourcePage, normalized.ScanPages)
                .Select(pageNumber => GetBrowseIdsAsync(normalized with { Page = pageNumber }, cancellationToken)));
            sourcePagesScanned = pageIds.Length;
            hasNext = pageIds.LastOrDefault()?.Count >= 20;
            ids = pageIds.SelectMany(page => page).Distinct().ToArray();
        }

        var details = ApplyFilters(await GetDetailsAsync(ids, cancellationToken), normalized);

        var page = new WorkshopCatalogPage(details, normalized.Page, normalized.Page > 1, hasNext, normalized, ids.Count, sourcePagesScanned);
        _cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow, page);
        return page;
    }

    private async Task<IReadOnlyList<ulong>> GetBrowseIdsAsync(WorkshopCatalogQuery query, CancellationToken cancellationToken)
    {
        var browseUrl = BuildBrowseUrl(query);
        if (_browseCache.TryGetValue(browseUrl, out var cached)
            && DateTimeOffset.UtcNow - cached.CreatedAt < TimeSpan.FromMinutes(5)) return cached.Ids;

        using var response = await _httpClient.GetAsync(browseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var ids = WorkshopIdRegex().Matches(html)
            .Select(match => ulong.TryParse(match.Groups[1].Value, out var id) ? id : 0)
            .Where(id => id != 0)
            .Distinct()
            .Take(30)
            .ToArray();
        _browseCache[browseUrl] = new BrowseCacheEntry(DateTimeOffset.UtcNow, ids);
        return ids;
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

    public async Task<IReadOnlyList<WorkshopRequiredItem>> GetRequiredItemsAsync(ulong workshopId, CancellationToken cancellationToken = default)
    {
        if (workshopId == 0) return [];
        if (_requiredItemsCache.TryGetValue(workshopId, out var cached)
            && DateTimeOffset.UtcNow - cached.CreatedAt < TimeSpan.FromMinutes(10)) return cached.Items;

        var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var start = html.IndexOf("id=\"RequiredItems\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            _requiredItemsCache[workshopId] = new RequiredItemsCacheEntry(DateTimeOffset.UtcNow, []);
            return [];
        }
        var end = html.IndexOf("<!-- created by -->", start, StringComparison.OrdinalIgnoreCase);
        var section = end > start ? html[start..end] : html[start..Math.Min(html.Length, start + 100_000)];
        var items = RequiredItemRegex().Matches(section)
            .Select(match => new WorkshopRequiredItem(
                ulong.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var id) ? id : 0,
                CleanRequiredItemTitle(match.Groups[2].Value)))
            .Where(item => item.WorkshopId != 0)
            .DistinctBy(item => item.WorkshopId)
            .ToArray();
        _requiredItemsCache[workshopId] = new RequiredItemsCacheEntry(DateTimeOffset.UtcNow, items);
        return items;
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
                FromUnixTime(ReadLong(detail, "time_created")),
                FromUnixTime(ReadLong(detail, "time_updated")),
                ReadString(detail, "creator"),
                ReadLong(detail, "file_size"),
                ReadLong(detail, "subscriptions"),
                ReadLong(detail, "lifetime_subscriptions"),
                ReadLong(detail, "favorited"),
                ReadLong(detail, "lifetime_favorited"),
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
            "updated" => "lastupdated",
            "subscribed" => "totaluniquesubscribers",
            "popular" => "totalvisitors",
            "rated" => "toprated",
            "relevance" when !string.IsNullOrWhiteSpace(query.SearchText) => "textsearch",
            _ => "trend"
        };
        var values = new List<string>
        {
            $"appid={PzasmConstants.ProjectZomboidSteamAppId}",
            "section=readytouseitems",
            $"browsesort={sort}",
            $"actualsort={sort}",
            $"p={query.Page}",
            $"searchtext={Uri.EscapeDataString(query.SearchText)}"
        };
        foreach (var tag in query.RequiredTagList)
            values.Add($"requiredtags%5B%5D={Uri.EscapeDataString(tag)}");
        foreach (var tag in query.ExcludedTagList)
            values.Add($"excludedtags%5B%5D={Uri.EscapeDataString(tag)}");
        if (query.Sort == "trend" && query.TrendDays != 0)
            values.Add($"days={query.TrendDays}");
        return "https://steamcommunity.com/workshop/browse/?" + string.Join('&', values);
    }

    private static IReadOnlyList<WorkshopCatalogItem> ApplyFilters(
        IReadOnlyList<WorkshopCatalogItem> items,
        WorkshopCatalogQuery query)
    {
        var now = DateTimeOffset.UtcNow;
        return items.Where(item =>
                (query.SearchFields == "all" || string.IsNullOrWhiteSpace(query.SearchText) ||
                    (query.SearchFields == "title" && item.Title.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)) ||
                    (query.SearchFields == "description" && item.Description.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase))) &&
                (query.RequiredTagList.Count == 0 || (query.MatchAllTags
                    ? query.RequiredTagList.All(tag => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    : query.RequiredTagList.Any(tag => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))) &&
                !query.ExcludedTagList.Any(tag => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) &&
                (query.CreatedWithinDays == 0 || item.CreatedAt >= now.AddDays(-query.CreatedWithinDays)) &&
                (query.UpdatedWithinDays == 0 || item.UpdatedAt >= now.AddDays(-query.UpdatedWithinDays)) &&
                (query.CreatorSteamId.Length == 0 || item.CreatorSteamId.Equals(query.CreatorSteamId, StringComparison.Ordinal)) &&
                item.Subscriptions >= query.MinSubscriptions &&
                item.LifetimeSubscriptions >= query.MinLifetimeSubscriptions &&
                item.Favorites >= query.MinFavorites &&
                item.Views >= query.MinViews &&
                item.FileSize >= query.MinFileSizeBytes &&
                (query.MaxFileSizeBytes == 0 || item.FileSize <= query.MaxFileSizeBytes) &&
                (query.Content == "any" ||
                    (query.Content == "preview" && item.PreviewUrl.Length > 0) ||
                    (query.Content == "description" && item.Description.Length > 0) ||
                    (query.Content == "complete" && item.PreviewUrl.Length > 0 && item.Description.Length > 0)))
            .ToArray();
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

    private static string CleanRequiredItemTitle(string value)
    {
        var decoded = WebUtility.HtmlDecode(HtmlTagRegex().Replace(value, " "));
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    [GeneratedRegex("<a\\s+href=\"https://steamcommunity\\.com/sharedfiles/filedetails/\\?id=(\\d+)\"[^>]*>\\s*<img\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkshopIdRegex();

    [GeneratedRegex(@"\[[^\]]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex BbCodeRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("<a\\s+href=\"https://steamcommunity\\.com/(?:sharedfiles|workshop)/filedetails/\\?id=(\\d+)[^\"]*\"[^>]*>[\\s\\S]*?<div\\s+class=\"requiredItem\"[^>]*>([\\s\\S]*?)</div>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequiredItemRegex();

    private sealed record CacheEntry(DateTimeOffset CreatedAt, WorkshopCatalogPage Page);
    private sealed record BrowseCacheEntry(DateTimeOffset CreatedAt, IReadOnlyList<ulong> Ids);
    private sealed record RequiredItemsCacheEntry(DateTimeOffset CreatedAt, IReadOnlyList<WorkshopRequiredItem> Items);
}

public sealed record WorkshopCatalogQuery(
    string SearchText = "",
    string Sort = "trend",
    int Page = 1,
    string RequiredTags = "",
    string ExcludedTags = "",
    bool MatchAllTags = true,
    string SearchFields = "all",
    int TrendDays = 7,
    int CreatedWithinDays = 0,
    int UpdatedWithinDays = 0,
    string CreatorSteamId = "",
    long MinSubscriptions = 0,
    long MinLifetimeSubscriptions = 0,
    long MinFavorites = 0,
    long MinViews = 0,
    long MinFileSizeBytes = 0,
    long MaxFileSizeBytes = 0,
    string Content = "any",
    int ScanPages = 1)
{
    public IReadOnlyList<string> RequiredTagList => SplitTags(RequiredTags);
    public IReadOnlyList<string> ExcludedTagList => SplitTags(ExcludedTags);
    public string CacheKey => string.Join('|', SearchText, Sort, Page, RequiredTags, ExcludedTags, MatchAllTags, SearchFields, TrendDays,
        CreatedWithinDays, UpdatedWithinDays, CreatorSteamId, MinSubscriptions, MinLifetimeSubscriptions, MinFavorites, MinViews,
        MinFileSizeBytes, MaxFileSizeBytes, Content, ScanPages);

    public WorkshopCatalogQuery Normalize()
    {
        var allowedSorts = new HashSet<string>(["trend", "recent", "updated", "subscribed", "popular", "rated", "relevance"], StringComparer.OrdinalIgnoreCase);
        var allowedSearchFields = new HashSet<string>(["all", "title", "description"], StringComparer.OrdinalIgnoreCase);
        var allowedContent = new HashSet<string>(["any", "preview", "description", "complete"], StringComparer.OrdinalIgnoreCase);
        var allowedPeriods = new HashSet<int>([0, 1, 7, 30, 90, 180, 365, 730]);
        var allowedTrendDays = new HashSet<int>([0, 1, 7, 30, 180, 365]);
        var allowedScanPages = new HashSet<int>([1, 3, 5]);
        var sort = Sort ?? string.Empty;
        var searchFields = SearchFields ?? string.Empty;
        var content = Content ?? string.Empty;
        var minFileSize = Math.Clamp(MinFileSizeBytes, 0, 100L * 1024 * 1024 * 1024);
        var maxFileSize = Math.Clamp(MaxFileSizeBytes, 0, 100L * 1024 * 1024 * 1024);
        if (maxFileSize > 0 && minFileSize > maxFileSize) (minFileSize, maxFileSize) = (maxFileSize, minFileSize);
        return new WorkshopCatalogQuery(
            (SearchText ?? string.Empty).Trim(),
            allowedSorts.Contains(sort) ? sort.ToLowerInvariant() : "trend",
            Math.Clamp(Page, 1, 1000),
            string.Join(';', SplitTags(RequiredTags)),
            string.Join(';', SplitTags(ExcludedTags)),
            MatchAllTags,
            allowedSearchFields.Contains(searchFields) ? searchFields.ToLowerInvariant() : "all",
            allowedTrendDays.Contains(TrendDays) ? TrendDays : 7,
            allowedPeriods.Contains(CreatedWithinDays) ? CreatedWithinDays : 0,
            allowedPeriods.Contains(UpdatedWithinDays) ? UpdatedWithinDays : 0,
            ulong.TryParse(CreatorSteamId, out var creatorId) && creatorId != 0 ? creatorId.ToString(CultureInfo.InvariantCulture) : string.Empty,
            Math.Max(0, MinSubscriptions),
            Math.Max(0, MinLifetimeSubscriptions),
            Math.Max(0, MinFavorites),
            Math.Max(0, MinViews),
            minFileSize,
            maxFileSize,
            allowedContent.Contains(content) ? content.ToLowerInvariant() : "any",
            allowedScanPages.Contains(ScanPages) ? ScanPages : 1);
    }

    private static IReadOnlyList<string> SplitTags(string? value) => (value ?? string.Empty)
        .Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(16)
        .ToArray();
}

public sealed record WorkshopCatalogPage(
    IReadOnlyList<WorkshopCatalogItem> Items,
    int Page,
    bool HasPrevious,
    bool HasNext,
    WorkshopCatalogQuery Query,
    int CandidatesInspected = 0,
    int SourcePagesScanned = 0);

public sealed record WorkshopCatalogItem(
    ulong WorkshopId,
    string Title,
    string Description,
    string PreviewUrl,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string CreatorSteamId,
    long FileSize,
    long Subscriptions,
    long LifetimeSubscriptions,
    long Favorites,
    long LifetimeFavorites,
    long Views,
    IReadOnlyList<string> Tags)
{
    public string WorkshopUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopId}";
}

public sealed record WorkshopRequiredItem(ulong WorkshopId, string Title)
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
