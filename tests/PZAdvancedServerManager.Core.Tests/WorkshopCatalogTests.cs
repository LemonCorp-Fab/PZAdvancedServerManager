using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PZAdvancedServerManager.Core.Publishing;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class WorkshopCatalogTests
{
    [Fact]
    public async Task SearchUsesCommunityIdsAndPublicSteamDetails()
    {
        var handler = new CatalogHandler();
        var service = new WorkshopCatalogService(new HttpClient(handler));

        var page = await service.SearchAsync(new WorkshopCatalogQuery("vehicles", "relevance", 1, "Build 42"));

        var item = Assert.Single(page.Items);
        Assert.Equal(2335368829UL, item.WorkshopId);
        Assert.Equal("Authentic Z", item.Title);
        Assert.Equal("Public description with markup.", item.Description);
        Assert.Contains("Build 42", item.Tags);
        Assert.Equal(2, handler.Requests);
        Assert.Contains("searchtext=vehicles", handler.LastBrowseUrl);
    }

    [Fact]
    public async Task NumericSearchFetchesDetailsWithoutScrapingBrowsePage()
    {
        var handler = new CatalogHandler();
        var service = new WorkshopCatalogService(new HttpClient(handler));

        var page = await service.SearchAsync(new WorkshopCatalogQuery("2335368829"));

        Assert.Single(page.Items);
        Assert.Equal(1, handler.Requests);
        Assert.Null(handler.LastBrowseUrl);
    }

    [Fact]
    public async Task DetailLookupBatchesLargePackWithoutDroppingWorkshopIds()
    {
        var handler = new BatchCatalogHandler();
        var service = new WorkshopCatalogService(new HttpClient(handler));
        var ids = Enumerable.Range(1, 121).Select(value => (ulong)value).ToArray();

        var details = await service.GetDetailsAsync(ids);

        Assert.Equal(121, details.Count);
        Assert.Equal(ids, details.Select(item => item.WorkshopId));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task RemoteStateIncludesManifestAndPreviewHandles()
    {
        var handler = new RemoteStateHandler();
        var service = new WorkshopCatalogService(new HttpClient(handler));

        var state = await service.GetRemoteStateAsync(2335368829);

        Assert.NotNull(state);
        Assert.Equal("987654321", state.ContentHandle);
        Assert.Equal("123456789", state.PreviewHandle);
        Assert.Equal(10_585_073_778, state.FileSize);
        Assert.Equal(3, state.Visibility);
        Assert.False(state.Banned);
    }

    [Fact]
    public async Task RequiredItemsAreReadFromTheDedicatedWorkshopSection()
    {
        var handler = new RequiredItemsHandler();
        var service = new WorkshopCatalogService(new HttpClient(handler));

        var items = await service.GetRequiredItemsAsync(3778856579);
        var cached = await service.GetRequiredItemsAsync(3778856579);

        var dependency = Assert.Single(items);
        Assert.Equal(3396446795UL, dependency.WorkshopId);
        Assert.Equal("Moodle Framework", dependency.Title);
        Assert.Same(items, cached);
        Assert.Equal(1, handler.Requests);
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? LastBrowseUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.Method == HttpMethod.Get)
            {
                LastBrowseUrl = request.RequestUri?.AbsoluteUri;
                return Task.FromResult(Response("<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=2335368829\" class=\"item\"><img src=\"preview.jpg\" alt=\"Authentic Z\"></a>"));
            }

            const string json = """
                {"response":{"publishedfiledetails":[{
                  "publishedfileid":"2335368829","result":1,"title":"Authentic Z",
                  "file_description":"[h1]Public[/h1] description <b>with</b> markup.",
                  "preview_url":"https://images.steamusercontent.com/ugc/example/preview/",
                  "time_updated":1700000000,"file_size":"877568832","subscriptions":2526727,
                  "favorited":90554,"views":100,"tags":[{"tag":"Build 42"},{"tag":"Map"}]
                }]}}
                """;
            return Task.FromResult(Response(json, "application/json"));
        }

        private static HttpResponseMessage Response(string body, string contentType = "text/html") => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };
    }

    private sealed class BatchCatalogHandler : HttpMessageHandler
    {
        private int _requests;
        public int Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var ids = Regex.Matches(body, @"publishedfileids%5B\d+%5D=(\d+)", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value)
                .ToArray();
            var items = string.Join(',', ids.Select(id => $"{{\"publishedfileid\":\"{id}\",\"result\":1,\"title\":\"Item {id}\",\"time_updated\":1700000000}}"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"response\":{{\"publishedfiledetails\":[{items}]}}}}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RemoteStateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string json = """
                {"response":{"publishedfiledetails":[{
                  "publishedfileid":"2335368829","result":1,"consumer_app_id":108600,"creator_app_id":108600,
                  "hcontent_file":"987654321","hcontent_preview":"123456789","file_size":"10585073778",
                  "time_updated":1780000000,"title":"Pack","description":"Description","visibility":3,"banned":0
                }]}}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RequiredItemsHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            const string html = """
                <a href="https://steamcommunity.com/sharedfiles/filedetails/?id=111">Unrelated recommendation</a>
                <div class="requiredItemsContainer" id="RequiredItems">
                  <a href="https://steamcommunity.com/workshop/filedetails/?id=3396446795" target="_blank">
                    <div class="requiredItem"> Moodle <b>Framework</b> </div>
                  </a>
                </div>
                <!-- created by -->
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            });
        }
    }
}
