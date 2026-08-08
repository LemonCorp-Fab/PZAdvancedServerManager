using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class ServerNetworkAndRconConsoleTests
{
    [Fact]
    public void NetworkInformationUsesConfiguredPortsAndLocalAddresses()
    {
        var profile = new ServerConfigEntry("main", "main.ini", ServerConnectionKind.Local, null);
        var document = ServerConfigDocument.Parse("DefaultPort=17261\nUDPPort=17262\nRCONPort=28015\nPublic=true\nOpen=false\n");

        var information = ServerNetworkInfo.Create(profile, document);

        Assert.Equal(17261, information.DefaultPort);
        Assert.Equal(17262, information.UdpPort);
        Assert.Equal(28015, information.RconPort);
        Assert.True(information.IsPublic);
        Assert.False(information.IsOpen);
        Assert.Contains("127.0.0.1", information.Addresses);
    }

    [Fact]
    public void RconOnlyRemoteProfileExposesOnlyItsKnownEndpoint()
    {
        var remote = new RemoteServerConnection
        {
            Name = "remote",
            RconHost = "rcon.example.com",
            RconPort = 29015,
            RconPassword = "secret"
        };
        var profile = new ServerConfigEntry("remote", string.Empty, ServerConnectionKind.Remote, remote);

        var information = ServerNetworkInfo.Create(profile, null);

        Assert.False(information.ConfigurationAvailable);
        Assert.Equal(["rcon.example.com"], information.Addresses);
        Assert.Equal(29015, information.RconPort);
        Assert.Null(information.DefaultPort);
    }

    [Fact]
    public void RconConsoleKeepsBoundedHistoryAndRedactsCredentialCommands()
    {
        var console = new RconConsoleStore(capacity: 10);
        for (var index = 0; index < 12; index++)
            console.Add("main", $"players {index}", $"response {index}", succeeded: true);
        console.Add("main", "changepwd admin super-secret", "ok", succeeded: true);

        var entries = console.List("main");

        Assert.Equal(10, entries.Count);
        Assert.Equal("changepwd <arguments redacted>", entries[^1].Command);
        Assert.DoesNotContain("super-secret", entries[^1].Command, StringComparison.Ordinal);
    }

    [Fact]
    public void RconConsoleHistoriesAreIsolatedAndCanBeCleared()
    {
        var console = new RconConsoleStore();
        console.Add("main", "players", "one player", succeeded: true);
        console.Add("secondary", "save", "saved", succeeded: true);

        console.Clear("main");

        Assert.Empty(console.List("main"));
        Assert.Single(console.List("secondary"));
    }
}
