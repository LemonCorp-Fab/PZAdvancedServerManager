using System.Net;
using System.Text;
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
}
