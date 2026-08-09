using System.Buffers.Binary;
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
    public async Task NonRconListenerIsNotMistakenForAProjectZomboidServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
        });

        var detected = await new ServerOrchestrationService().IsRconServiceAsync("127.0.0.1", port, "probe-password");

        Assert.False(detected);
        await accept;
    }

    [Fact]
    public async Task RconAuthenticationRejectionStillIdentifiesTheServerProtocol()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var requestLengthBytes = new byte[4];
            await stream.ReadExactlyAsync(requestLengthBytes);
            var requestLength = BinaryPrimitives.ReadInt32LittleEndian(requestLengthBytes);
            var request = new byte[requestLength];
            await stream.ReadExactlyAsync(request);

            var response = new byte[14];
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(0, 4), 10);
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(4, 4), -1);
            BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(8, 4), 2);
            await stream.WriteAsync(response);
        });

        var detected = await new ServerOrchestrationService().IsRconServiceAsync("127.0.0.1", port, "wrong-password");

        Assert.True(detected);
        await server;
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
        var orchestration = new ServerOrchestrationService();
        var pine = new PineHostingClient();
        var backends = new RemoteServerBackendRouter([
            new SshRconRemoteBackend(new SshRemoteServerService(), orchestration),
            new PineHostingRemoteBackend(pine)
        ]);
        var service = new ServerProfileService(paths, environment, orchestration, store, new LocalServerProfileStore(paths), backends, pine);
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

    [Fact]
    public void LocalProfileModeIsPersistedIndependentlyFromTheSharedIni()
    {
        var paths = new ApplicationPaths(_root);
        var store = new LocalServerProfileStore(paths);

        store.Save("host-one", LocalServerMode.Hosted);
        store.Save("dedicated-one", LocalServerMode.Dedicated);

        var reopened = new LocalServerProfileStore(new ApplicationPaths(_root));
        Assert.Equal(LocalServerMode.Hosted, reopened.Get("HOST-ONE"));
        Assert.Equal(LocalServerMode.Dedicated, reopened.Get("dedicated-one"));
    }

    [Theory]
    [InlineData("java -cp projectzomboid.jar zombie.network.GameServer -servername \"servertest\"", "servertest")]
    [InlineData("java zombie.network.GameServer -servername=alpha-one", "alpha-one")]
    [InlineData("java zombie.network.GameServer -servername beta_2 -statistic 0", "beta_2")]
    public void ServerProfileNameIsParsedFromAProjectZomboidCommandLine(string commandLine, string expected)
    {
        Assert.Equal(expected, ServerOrchestrationService.ParseServerNameFromCommandLine(commandLine));
    }

    [Fact]
    public void UnrelatedJavaProcessIsNotTreatedAsAProjectZomboidServer()
    {
        Assert.Null(ServerOrchestrationService.ParseServerNameFromCommandLine("java -jar unrelated.jar -servername servertest"));
        Assert.Null(ServerOrchestrationService.ParseServerNameFromCommandLine("ProjectZomboid64.exe"));
    }

    [Theory]
    [InlineData("java zombie.network.GameServer -servername servertest", ServerRuntimeOrigin.LocalDedicated)]
    [InlineData("java zombie.network.GameServer -coop -servername servertest", ServerRuntimeOrigin.LocalHostedSession)]
    [InlineData("java zombie.network.GameServer -servername servertest -coop", ServerRuntimeOrigin.LocalHostedSession)]
    public void ServerRuntimeOriginDistinguishesDedicatedAndHostedSessions(string commandLine, ServerRuntimeOrigin expected)
    {
        Assert.Equal(expected, ServerOrchestrationService.ParseRuntimeOriginFromCommandLine(commandLine));
    }

    [Fact]
    public void HostedHelperWithFailedStartupIsNotAnActiveServer()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(ServerOrchestrationService.IsHostedSessionActive(
            now.AddSeconds(-20),
            now.AddSeconds(-1),
            gameReady: false,
            startupFailed: true,
            now));
    }

    [Fact]
    public void HostedSessionRequiresReadyStateOrRecentStartupProgress()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(ServerOrchestrationService.IsHostedSessionActive(
            now.AddMinutes(-10),
            now.AddMinutes(-8),
            gameReady: true,
            startupFailed: false,
            now));
        Assert.True(ServerOrchestrationService.IsHostedSessionActive(
            now.AddSeconds(-45),
            now.AddSeconds(-2),
            gameReady: false,
            startupFailed: false,
            now));
        Assert.False(ServerOrchestrationService.IsHostedSessionActive(
            now.AddMinutes(-10),
            now.AddSeconds(-2),
            gameReady: false,
            startupFailed: false,
            now));
    }

    [Fact]
    public async Task ForceStopRefusesAProfileWithoutAnExactDedicatedProcess()
    {
        var orchestration = new ServerOrchestrationService();
        var profile = $"missing-{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestration.ForceStopLocalDedicatedAsync(profile));

        Assert.Contains("Aucun processus serveur dédié actif", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LOG", "ERROR: General > failure", "error")]
    [InlineData("LOG", "WARN : Sprite > duplicate texture", "warning")]
    [InlineData("LOG", "*** SERVER STARTED ****", "success")]
    [InlineData("SYSTEM", "Launcher started", "system")]
    [InlineData("LOG", "\tjava.base/example", "stack")]
    public void RuntimeLogLinesExposeAReadableSeverity(string stream, string message, string expected)
    {
        Assert.Equal(expected, new ServerRuntimeLogLine(1, null, stream, message).Level);
    }

    [Fact]
    public async Task ManagedProcessOutputIdentifiesReadyGameWithRconBindFailureAndRedactsSecrets()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dedicatedRoot = Path.Combine(_root, "dedicated runtime server");
        Directory.CreateDirectory(dedicatedRoot);
        var script = Path.Combine(dedicatedRoot, "StartServer64.bat");
        var iniPath = Path.Combine(dedicatedRoot, "runtime.ini");
        var consolePath = Path.Combine(dedicatedRoot, "server-console.txt");
        File.WriteAllText(script, "@echo off\r\necho *** SERVER STARTED ****\r\necho RCON: error creating socket on port 27015\r\necho RCONPassword=top-secret\r\nping 127.0.0.1 -n 5 > NUL\r\n");
        File.WriteAllText(iniPath, "RCONPort=1\r\nRCONPassword=test-value\r\n");
        var orchestration = new ServerOrchestrationService();

        orchestration.Start("runtime-profile", dedicatedRoot, TimeSpan.FromMilliseconds(500));
        var runtime = await orchestration.InspectLocalRuntimeAsync("runtime-profile", iniPath, consolePath);

        Assert.Equal(ServerRuntimeState.OnlineWithoutRcon, runtime.State);
        Assert.True(runtime.IsRunning);
        Assert.True(runtime.IsGameReady);
        Assert.True(runtime.RconBindFailed);
        Assert.True(runtime.IsManagedByCurrentSession);
        Assert.Contains(runtime.Output, line => line.Message.Contains("SERVER STARTED", StringComparison.Ordinal));
        Assert.DoesNotContain(runtime.Output, line => line.Message.Contains("top-secret", StringComparison.Ordinal));
        Assert.Contains(runtime.Output, line => line.Message.Contains("<redacted>", StringComparison.Ordinal));
        Assert.True(SpinWait.SpinUntil(() => !orchestration.IsManagedProcessRunning("runtime-profile"), TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public async Task LiveServerConsoleCanBeReadWhileAnotherProcessKeepsItOpenForWriting()
    {
        var root = Path.Combine(_root, "shared live console");
        Directory.CreateDirectory(root);
        var iniPath = Path.Combine(root, "shared.ini");
        var consolePath = Path.Combine(root, "server-console.txt");
        File.WriteAllText(iniPath, "RCONPort=1\nRCONPassword=test-value\n");
        File.WriteAllText(consolePath, "LOG > *** SERVER STARTED ****\nLOG > live output\n");
        await using var writer = new FileStream(consolePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var runtime = await new ServerOrchestrationService().InspectLocalRuntimeAsync("no-such-profile", iniPath, consolePath);

        Assert.Equal(ServerRuntimeState.Stopped, runtime.State);
        Assert.False(runtime.IsGameReady);
        Assert.Contains(runtime.Output, line => line.Message.Contains("live output", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsDedicatedServerScriptReceivesTheSelectedProfileName()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dedicatedRoot = Path.Combine(_root, "dedicated server");
        Directory.CreateDirectory(dedicatedRoot);
        var script = Path.Combine(dedicatedRoot, "StartServer64.bat");
        var argumentsFile = Path.Combine(dedicatedRoot, "arguments.txt");
        var completedFile = Path.Combine(dedicatedRoot, "completed.txt");
        File.WriteAllText(script, "@echo off\r\n>\"%~dp0arguments.txt\" echo %*\r\nping 127.0.0.1 -n 3 > NUL\r\n>\"%~dp0completed.txt\" echo done\r\n");

        var orchestration = new ServerOrchestrationService();
        orchestration.Start("test-profile", dedicatedRoot, TimeSpan.FromMilliseconds(750));

        Assert.True(orchestration.IsManagedProcessRunning("test-profile"));
        Assert.True(SpinWait.SpinUntil(() => File.Exists(completedFile), TimeSpan.FromSeconds(5)));
        Assert.Equal("-servername \"test-profile\"", File.ReadAllText(argumentsFile).Trim());
        Assert.True(SpinWait.SpinUntil(() => !orchestration.IsManagedProcessRunning("test-profile"), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void WindowsFirstStartReceivesTheAdminPasswordThroughStandardInput()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dedicatedRoot = Path.Combine(_root, "dedicated admin server");
        Directory.CreateDirectory(dedicatedRoot);
        var script = Path.Combine(dedicatedRoot, "StartServer64.bat");
        var passwordFile = Path.Combine(dedicatedRoot, "admin-password.txt");
        File.WriteAllText(script, "@echo off\r\necho Enter new administrator password:\r\nset /p ADMIN_PASSWORD=\r\necho Confirm the password:\r\nset /p ADMIN_PASSWORD_CONFIRMATION=\r\n>\"%~dp0admin-password.txt\" echo %ADMIN_PASSWORD%\r\n>>\"%~dp0admin-password.txt\" echo %ADMIN_PASSWORD_CONFIRMATION%\r\nping 127.0.0.1 -n 3 > NUL\r\npause\r\n");

        var orchestration = new ServerOrchestrationService();
        orchestration.Start("new-world", dedicatedRoot, "transient-secret", TimeSpan.FromMilliseconds(750));

        Assert.True(SpinWait.SpinUntil(() => File.Exists(passwordFile), TimeSpan.FromSeconds(3)));
        Assert.Equal(["transient-secret", "transient-secret"], File.ReadAllLines(passwordFile));
        Assert.True(SpinWait.SpinUntil(() => !orchestration.IsManagedProcessRunning("new-world"), TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void WindowsFirstStartWithoutAnAdminPasswordStopsWithActionableError()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dedicatedRoot = Path.Combine(_root, "dedicated missing admin password");
        Directory.CreateDirectory(dedicatedRoot);
        var script = Path.Combine(dedicatedRoot, "StartServer64.bat");
        File.WriteAllText(script, "@echo off\r\necho Enter new administrator password:\r\nset /p ADMIN_PASSWORD=\r\n");
        var orchestration = new ServerOrchestrationService();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            orchestration.Start("new-world", dedicatedRoot, null, TimeSpan.FromSeconds(3)));

        Assert.Contains("mot de passe administrateur initial", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(orchestration.IsManagedProcessRunning("new-world"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
