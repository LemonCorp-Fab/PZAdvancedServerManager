using System.Net;
using System.Text;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class PineHostingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-pine-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ApiUsesBearerAndTheSelectedServerIdentifier()
    {
        HttpRequestMessage? observed = null;
        var client = Client(request =>
        {
            observed = Clone(request);
            return Task.FromResult(Json("""
                {"object":"server","attributes":{"identifier":"a1b2c3d4","uuid":"u","name":"Pine PZ","description":"","node":"EU","limits":{"memory":8192,"disk":25000},"feature_limits":{"backups":5,"databases":1}}}
                """));
        });

        var result = await client.TestAsync(Connection());

        Assert.Equal("Pine PZ", result.Name);
        Assert.NotNull(observed);
        Assert.Equal("Bearer", observed!.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", observed.Headers.Authorization?.Parameter);
        Assert.Equal("https://panel.pinehosting.com/api/client/servers/a1b2c3d4", observed.RequestUri?.ToString());
    }

    [Fact]
    public async Task ConfigurationWriteCreatesProviderBackupAndVerifiesTheResult()
    {
        var requests = new List<(HttpMethod Method, string Url, string Body)>();
        var targetReads = 0;
        var client = Client(async request =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method, request.RequestUri!.ToString(), body));
            if (request.RequestUri.AbsolutePath.EndsWith("/files/list", StringComparison.Ordinal))
                return Json("{\"data\":[]}");
            if (request.Method == HttpMethod.Get)
            {
                targetReads++;
                return Text(targetReads == 1 ? "PublicName=Old\n" : "PublicName=New\n");
            }
            return Empty();
        });

        var backup = await client.WriteFileAsync(Connection(), PineHostingClient.DefaultIniPath, "PublicName=New\n");

        Assert.Contains(".pzasm.", backup);
        Assert.EndsWith(".bak", backup);
        Assert.Equal(5, requests.Count);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Contains(Uri.EscapeDataString(PineHostingClient.DefaultIniPath + ".pzasm."), requests[1].Url);
        Assert.Equal("PublicName=Old\n", requests[1].Body);
        Assert.Contains(Uri.EscapeDataString(PineHostingClient.DefaultIniPath), requests[2].Url);
        Assert.Equal("PublicName=New\n", requests[2].Body);
        Assert.Equal(HttpMethod.Get, requests[3].Method);
        Assert.Contains("/files/list", requests[4].Url);
    }

    [Fact]
    public async Task ConfigurationWriteRetainsOnlyTwentyManagerBackups()
    {
        var deleteBody = string.Empty;
        var targetReads = 0;
        var entries = string.Join(',', Enumerable.Range(0, 22).Select(index =>
            $"{{\"attributes\":{{\"name\":\"Zomboid.ini.pzasm.{index:00}.bak\",\"is_file\":true,\"modified_at\":\"2025-01-01T00:{index:00}:00Z\"}}}}"));
        var client = Client(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/files/list", StringComparison.Ordinal))
                return Json("{\"data\":[" + entries + "]}");
            if (request.RequestUri.AbsolutePath.EndsWith("/files/delete", StringComparison.Ordinal))
            {
                deleteBody = await request.Content!.ReadAsStringAsync();
                return Empty();
            }
            if (request.Method == HttpMethod.Get)
            {
                targetReads++;
                return Text(targetReads == 1 ? "PublicName=Old\n" : "PublicName=New\n");
            }
            return Empty();
        });

        await client.WriteFileAsync(Connection(), PineHostingClient.DefaultIniPath, "PublicName=New\n");

        Assert.Contains("Zomboid.ini.pzasm.00.bak", deleteBody);
        Assert.Contains("Zomboid.ini.pzasm.01.bak", deleteBody);
        Assert.DoesNotContain("Zomboid.ini.pzasm.21.bak", deleteBody);
    }

    [Fact]
    public async Task PineProfileNeedsOnlyApiKeyAndServerId()
    {
        var handler = new DelegateHandler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/files/contents", StringComparison.Ordinal)
                ? Text("PublicName=Pine\nWorkshopItems=\nMods=\nMap=Muldraugh, KY\n")
                : Json("""{"object":"server","attributes":{"identifier":"a1b2c3d4","uuid":"u","name":"Pine PZ","description":"","node":"EU","limits":{},"feature_limits":{"backups":5}}}""")));
        var pine = new PineHostingClient(new HttpClient(handler));
        var paths = new ApplicationPaths(_root);
        var environment = new PzEnvironmentService(new PzDiscoveryService(paths));
        var orchestration = new ServerOrchestrationService();
        var router = new RemoteServerBackendRouter([
            new SshRconRemoteBackend(new SshRemoteServerService(), orchestration),
            new PineHostingRemoteBackend(pine)
        ]);
        var service = new ServerProfileService(paths, environment, orchestration, new RemoteServerConnectionStore(paths), new LocalServerProfileStore(paths), router, pine);

        var profile = await service.CreateRemoteAsync(WithName(Connection(), "pine-main"), false);

        Assert.True(profile.IsPineHosting);
        Assert.True(profile.CanManageConfiguration);
        Assert.True(service.CanStart(profile.Name));
        Assert.Equal("Pine PZ", profile.Remote!.ProviderServerName);
        Assert.Equal("Pine", service.ReadDocument(profile.Name).Get("PublicName"));
        Assert.True(string.IsNullOrEmpty(profile.Remote.RconHost));
        Assert.True(string.IsNullOrEmpty(profile.Remote.RconPassword));
    }

    [Fact]
    public void RejectsNonPineApiHosts()
    {
        var connection = Connection();
        connection.ApiBaseUrl = "https://example.com";

        var exception = Assert.Throws<ArgumentException>(() => PineHostingClient.ValidateConnection(connection));

        Assert.Contains("panel.pinehosting.com", exception.Message);
    }

    [Fact]
    public void ConsoleWebSocketEventParserReadsConsoleOutput()
    {
        var parsed = PineHostingClient.TryParseConsoleEvent(
            "{\"event\":\"console output\",\"args\":[\"LOG  : General > *** SERVER STARTED ****\"]}",
            out var eventName,
            out var arguments);

        Assert.True(parsed);
        Assert.Equal("console output", eventName);
        Assert.Equal(["LOG  : General > *** SERVER STARTED ****"], arguments);
    }

    [Fact]
    public void ConsoleWebSocketEventParserRejectsInvalidPayload()
    {
        Assert.False(PineHostingClient.TryParseConsoleEvent("not-json", out _, out _));
    }

    private static PineHostingClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        => new(new HttpClient(new DelegateHandler(handler)));

    private static RemoteServerConnection Connection() => new()
    {
        Name = "pine-test",
        Provider = RemoteServerProvider.PineHosting,
        ApiBaseUrl = PineHostingClient.DefaultApiBaseUrl,
        ApiToken = "test-api-key",
        ApiServerIdentifier = "a1b2c3d4",
        RemoteIniPath = PineHostingClient.DefaultIniPath
    };

    private static RemoteServerConnection WithName(RemoteServerConnection connection, string name)
    {
        connection.Name = name;
        return connection;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    private static HttpResponseMessage Empty() => new(HttpStatusCode.NoContent);

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;
        return clone;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
