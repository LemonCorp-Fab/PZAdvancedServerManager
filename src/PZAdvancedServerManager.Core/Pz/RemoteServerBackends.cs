using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Pz;

public interface IRemoteServerBackend
{
    RemoteServerProvider Provider { get; }
    Task TestAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default);
    Task<string> ReadFileAsync(RemoteServerConnection connection, string path, CancellationToken cancellationToken = default);
    Task<string> WriteFileAsync(RemoteServerConnection connection, string path, string content, CancellationToken cancellationToken = default);
    Task<bool> IsOnlineAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default);
    Task<ServerRuntimeSnapshot> ReadRuntimeAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default);
    Task StartAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default);
    Task StopAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default);
    Task RestartAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default);
    Task<string> ExecuteCommandAsync(RemoteServerConnection connection, string command, CancellationToken cancellationToken = default);
}

public sealed class RemoteServerBackendRouter(IEnumerable<IRemoteServerBackend> backends)
{
    private readonly IReadOnlyDictionary<RemoteServerProvider, IRemoteServerBackend> _backends = backends.ToDictionary(x => x.Provider);

    public IRemoteServerBackend Resolve(RemoteServerConnection connection) => _backends.TryGetValue(connection.Provider, out var backend)
        ? backend
        : throw new NotSupportedException($"Le fournisseur distant « {connection.Provider} » n'est pas disponible.");
}

public sealed class SshRconRemoteBackend(SshRemoteServerService ssh, ServerOrchestrationService orchestration) : IRemoteServerBackend
{
    public RemoteServerProvider Provider => RemoteServerProvider.RconSsh;

    public async Task TestAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        if (!connection.HasSshConnection) throw new InvalidOperationException("Ajoutez l'hôte et l'utilisateur SSH pour tester la connexion facultative.");
        await ssh.TestAsync(connection, cancellationToken);
    }

    public Task<string> ReadFileAsync(RemoteServerConnection connection, string path, CancellationToken cancellationToken = default)
        => ssh.ReadFileAsync(WithPath(connection, path), cancellationToken);

    public Task<string> WriteFileAsync(RemoteServerConnection connection, string path, string content, CancellationToken cancellationToken = default)
        => ssh.WriteFileAsync(WithPath(connection, path), content, cancellationToken);

    public Task<bool> IsOnlineAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
        => orchestration.IsOnlineAsync(RconHost(connection), connection.RconPort, connection.RconPassword, cancellationToken);

    public async Task<ServerRuntimeSnapshot> ReadRuntimeAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        var rconAvailable = await IsOnlineAsync(connection, cancellationToken);
        return new ServerRuntimeSnapshot(
            rconAvailable ? ServerRuntimeState.Online : ServerRuntimeState.Stopped,
            rconAvailable,
            rconAvailable,
            rconAvailable,
            false,
            false,
            null,
            null,
            null,
            [])
        {
            Origin = ServerRuntimeOrigin.RemoteRcon
        };
    }

    public Task StartAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
        => ssh.RunStartCommandAsync(connection, cancellationToken);

    public Task StopAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
        => orchestration.StopGracefullyAsync(RconHost(connection), connection.RconPort, connection.RconPassword, cancellationToken);

    public Task RestartAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
        => orchestration.RequestRestartAsync(RconHost(connection), connection.RconPort, connection.RconPassword, cancellationToken);

    public Task<string> ExecuteCommandAsync(RemoteServerConnection connection, string command, CancellationToken cancellationToken = default)
        => orchestration.ExecuteCommandAsync(RconHost(connection), connection.RconPort, connection.RconPassword, command, cancellationToken);

    private static string RconHost(RemoteServerConnection connection) => string.IsNullOrWhiteSpace(connection.RconHost) ? connection.Host : connection.RconHost;

    private static RemoteServerConnection WithPath(RemoteServerConnection source, string path) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Provider = source.Provider,
        Host = source.Host,
        SshPort = source.SshPort,
        SshUser = source.SshUser,
        SshPrivateKeyPath = source.SshPrivateKeyPath,
        RemoteIniPath = path,
        StartCommand = source.StartCommand,
        RconHost = source.RconHost,
        RconPort = source.RconPort,
        RconPassword = source.RconPassword,
        AutoRestartAfterRconQuit = source.AutoRestartAfterRconQuit,
        UpdatedAt = source.UpdatedAt
    };
}

public sealed class PineHostingRemoteBackend(PineHostingClient pine) : IRemoteServerBackend
{
    public RemoteServerProvider Provider => RemoteServerProvider.PineHosting;

    public async Task TestAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
        => _ = await pine.TestAsync(connection, cancellationToken);

    public Task<string> ReadFileAsync(RemoteServerConnection connection, string path, CancellationToken cancellationToken = default)
        => pine.ReadFileAsync(connection, path, cancellationToken);

    public Task<string> WriteFileAsync(RemoteServerConnection connection, string path, string content, CancellationToken cancellationToken = default)
        => pine.WriteFileAsync(connection, path, content, cancellationToken);

    public async Task<bool> IsOnlineAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
        => (await pine.GetResourcesAsync(connection, cancellationToken)).IsRunning;

    public async Task<ServerRuntimeSnapshot> ReadRuntimeAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        var resources = await pine.GetResourcesAsync(connection, cancellationToken);
        var state = resources.State switch
        {
            "running" => ServerRuntimeState.Online,
            "starting" => ServerRuntimeState.Starting,
            "stopping" => ServerRuntimeState.StartingSlow,
            _ => ServerRuntimeState.Stopped
        };
        var now = DateTimeOffset.UtcNow;
        var output = new[]
        {
            new ServerRuntimeLogLine(1, now, "PINE", $"État API : {resources.State} · CPU {resources.CpuAbsolute:0.0}% · mémoire {resources.MemoryBytes / 1024d / 1024d:0} Mio · disque {resources.DiskBytes / 1024d / 1024d:0} Mio")
        };
        return new ServerRuntimeSnapshot(
            state,
            resources.IsRunning,
            resources.State == "running",
            false,
            false,
            false,
            null,
            null,
            now,
            output)
        {
            Origin = ServerRuntimeOrigin.PineHostingApi
        };
    }

    public async Task StartAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        await pine.SetPowerAsync(connection, PinePowerSignal.Start, cancellationToken);
        await pine.WaitForStateAsync(connection, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running" }, TimeSpan.FromMinutes(5), cancellationToken);
    }

    public async Task StopAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        var resources = await pine.GetResourcesAsync(connection, cancellationToken);
        if (!resources.IsRunning) return;
        if (resources.State == "running")
        {
            await pine.SendCommandAsync(connection, "save", cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await pine.SendCommandAsync(connection, "quit", cancellationToken);
            try
            {
                await pine.WaitForStateAsync(connection, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "offline" }, TimeSpan.FromMinutes(2), cancellationToken);
                return;
            }
            catch (TimeoutException) { }
        }
        await pine.SetPowerAsync(connection, PinePowerSignal.Stop, cancellationToken);
        await pine.WaitForStateAsync(connection, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "offline" }, TimeSpan.FromMinutes(2), cancellationToken);
    }

    public async Task RestartAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        var resources = await pine.GetResourcesAsync(connection, cancellationToken);
        if (resources.State == "running")
        {
            await pine.SendCommandAsync(connection, "save", cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await pine.SetPowerAsync(connection, PinePowerSignal.Restart, cancellationToken);
        }
        else
        {
            await pine.SetPowerAsync(connection, PinePowerSignal.Start, cancellationToken);
        }
        await pine.WaitForStateAsync(connection, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running" }, TimeSpan.FromMinutes(5), cancellationToken);
    }

    public async Task<string> ExecuteCommandAsync(RemoteServerConnection connection, string command, CancellationToken cancellationToken = default)
    {
        await pine.SendCommandAsync(connection, command, cancellationToken);
        return $"Commande « {command.Trim()} » transmise à la console Pine Hosting.";
    }
}
