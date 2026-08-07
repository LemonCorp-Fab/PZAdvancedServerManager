using System.Net;
using System.Net.Sockets;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class ServerManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-servers", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OccupiedPortWithoutRconAuthenticationIsOffline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
        });

        var online = await new ServerOrchestrationService().IsOnlineAsync("127.0.0.1", port, "not-a-real-password");

        Assert.False(online);
        await accept;
    }

    [Fact]
    public async Task MissingRconPasswordIsOffline()
    {
        var online = await new ServerOrchestrationService().IsOnlineAsync("127.0.0.1", 27015, string.Empty);

        Assert.False(online);
    }

    [Fact]
    public async Task ReachablePortCanBeDistinguishedFromAuthenticatedRcon()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
        });

        var reachable = await new ServerOrchestrationService().IsPortReachableAsync("127.0.0.1", port);

        Assert.True(reachable);
        await accept;
    }

    [Fact]
    public void RemoteProfilesPersistConnectionAndRconSettings()
    {
        var store = new RemoteServerConnectionStore(new ApplicationPaths(_root));
        store.Save(new RemoteServerConnection
        {
            Name = "vps-main",
            Host = "server.example.com",
            SshUser = "pz",
            RemoteIniPath = "/srv/pz/Zomboid/Server/main.ini",
            StartCommand = "systemctl start pzserver",
            RconHost = "rcon.example.com",
            RconPort = 28015,
            RconPassword = "secret"
        });

        var reopened = new RemoteServerConnectionStore(new ApplicationPaths(_root)).Get("VPS-MAIN");

        Assert.NotNull(reopened);
        Assert.Equal("server.example.com", reopened.Host);
        Assert.Equal("systemctl start pzserver", reopened.StartCommand);
        Assert.Equal(28015, reopened.RconPort);
        Assert.Equal("secret", reopened.RconPassword);
    }

    [Fact]
    public async Task RconOnlyProfileDoesNotRequireSshAndCanCoordinateRestart()
    {
        var paths = new ApplicationPaths(_root);
        var environment = new PzEnvironmentService(new PzDiscoveryService(paths));
        var store = new RemoteServerConnectionStore(paths);
        var service = new ServerProfileService(paths, environment, new ServerOrchestrationService(), store, new SshRemoteServerService());
        var profileName = $"rcon-{Guid.NewGuid():N}";

        var created = await service.CreateRemoteAsync(new RemoteServerConnection
        {
            Name = profileName,
            RconHost = "127.0.0.1",
            RconPort = 27015,
            RconPassword = "secret",
            AutoRestartAfterRconQuit = true
        }, createConfigIfMissing: false);

        Assert.False(created.Remote!.HasSshConnection);
        Assert.False(created.CanManageConfiguration);
        Assert.False(service.CanStart(created.Name));
        Assert.True(service.CanCoordinateRestart(created.Name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
