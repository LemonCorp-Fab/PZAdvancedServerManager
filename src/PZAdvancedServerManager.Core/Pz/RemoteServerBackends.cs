using PZAdvancedServerManager.Core.Domain;
using System.Net.Sockets;
using System.Net.WebSockets;

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
        var output = Array.Empty<ServerRuntimeLogLine>();
        var logSource = "RCON distant · journal de fichier non configuré";
        var logStatus = "RCON confirme l'état du serveur, mais ne diffuse pas spontanément le fichier de journal.";
        if (connection.HasSshManagement)
        {
            var consolePath = DeriveConsolePath(connection.RemoteIniPath);
            try
            {
                var content = await ssh.ReadTailAsync(connection, consolePath, cancellationToken: cancellationToken);
                output = content.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select((line, index) => new ServerRuntimeLogLine(index + 1, null, "SSH", line.TrimEnd()))
                    .ToArray();
                logSource = consolePath;
                logStatus = output.Length > 0
                    ? rconAvailable
                        ? $"Journal actif : {output.Length} lignes lues par SSH."
                        : $"RCON ne confirme aucun serveur actif : {output.Length} lignes archivées lues par SSH."
                    : $"Le fichier {consolePath} est absent ou vide.";
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logSource = consolePath;
                logStatus = $"Journal SSH temporairement indisponible : {exception.Message}";
            }
        }

        var overview = ServerRuntimeOverview.Empty;
        if (rconAvailable)
        {
            try
            {
                overview = await orchestration.ReadRconOverviewAsync(RconHost(connection), connection.RconPort, connection.RconPassword, null, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or UnauthorizedAccessException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }
        return new ServerRuntimeSnapshot(
            rconAvailable ? ServerRuntimeState.Online : ServerRuntimeState.Stopped,
            rconAvailable,
            rconAvailable,
            rconAvailable,
            false,
            false,
            null,
            null,
            output.Length > 0 ? DateTimeOffset.UtcNow : null,
            output)
        {
            Origin = ServerRuntimeOrigin.RemoteRcon,
            Overview = overview,
            LogSource = logSource,
            LogStatus = logStatus
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

    private static string DeriveConsolePath(string iniPath)
    {
        var normalized = iniPath.Replace('\\', '/');
        var serverSeparator = normalized.LastIndexOf('/');
        var serverDirectory = serverSeparator > 0 ? normalized[..serverSeparator] : normalized;
        var rootSeparator = serverDirectory.LastIndexOf('/');
        var root = rootSeparator <= 0 ? "/" : serverDirectory[..rootSeparator];
        return root.TrimEnd('/') + "/server-console.txt";
    }

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

public sealed class PineHostingRemoteBackend : IRemoteServerBackend
{
    private readonly PineHostingClient _pine;
    private readonly ServerOrchestrationService _orchestration;

    public PineHostingRemoteBackend(PineHostingClient pine, ServerOrchestrationService orchestration)
    {
        _pine = pine;
        _orchestration = orchestration;
    }

    public PineHostingRemoteBackend(PineHostingClient pine) : this(pine, new ServerOrchestrationService())
    {
    }

    public RemoteServerProvider Provider => RemoteServerProvider.PineHosting;

    public async Task TestAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
        => _ = await _pine.TestAsync(connection, cancellationToken);

    public Task<string> ReadFileAsync(RemoteServerConnection connection, string path, CancellationToken cancellationToken = default)
        => _pine.ReadFileAsync(connection, path, cancellationToken);

    public Task<string> WriteFileAsync(RemoteServerConnection connection, string path, string content, CancellationToken cancellationToken = default)
        => _pine.WriteFileAsync(connection, path, content, cancellationToken);

    public async Task<bool> IsOnlineAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
        => (await _pine.GetResourcesAsync(connection, cancellationToken)).IsRunning;

    public async Task<ServerRuntimeSnapshot> ReadRuntimeAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        var resourcesTask = _pine.GetResourcesAsync(connection, cancellationToken);
        var serverTask = _pine.GetServerAsync(connection, cancellationToken);
        await Task.WhenAll(resourcesTask, serverTask);
        var resources = await resourcesTask;
        var server = await serverTask;
        var state = resources.State switch
        {
            "running" => ServerRuntimeState.Online,
            "starting" => ServerRuntimeState.Starting,
            "stopping" => ServerRuntimeState.StartingSlow,
            _ => ServerRuntimeState.Stopped
        };
        PineConsoleSnapshot? console = null;
        string logStatus;
        if (resources.IsRunning)
        {
            try
            {
                console = await _pine.ReadConsoleTailAsync(connection, cancellationToken: cancellationToken);
                logStatus = console.Lines.Count > 0
                    ? $"{console.Lines.Count} lignes reçues depuis la console Pine Hosting."
                    : "La console Pine Hosting est connectée, mais aucune ligne récente n'a été renvoyée.";
            }
            catch (Exception exception) when (exception is IOException or WebSocketException or HttpRequestException or TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logStatus = $"Console Pine Hosting temporairement indisponible : {exception.Message}";
            }
        }
        else
        {
            logStatus = "Le serveur Pine Hosting est arrêté ; aucune console active n'est disponible.";
        }

        ServerRuntimeOverview rconOverview = ServerRuntimeOverview.Empty;
        var hasRcon = !string.IsNullOrWhiteSpace(connection.RconPassword)
            && !string.IsNullOrWhiteSpace(string.IsNullOrWhiteSpace(connection.RconHost) ? connection.Host : connection.RconHost);
        if (resources.State == "running" && hasRcon)
        {
            try
            {
                var host = string.IsNullOrWhiteSpace(connection.RconHost) ? connection.Host : connection.RconHost;
                rconOverview = await _orchestration.ReadRconOverviewAsync(host, connection.RconPort, connection.RconPassword, null, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or UnauthorizedAccessException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        if (rconOverview.PlayerSource != "rcon" && console is not null)
        {
            var consolePlayers = ParseLatestPlayers(console.Lines);
            if (consolePlayers is not null)
                rconOverview = rconOverview with
                {
                    PlayerCount = consolePlayers.Value.Count,
                    Players = consolePlayers.Value.Players,
                    CapturedAt = console.CapturedAt,
                    PlayerSource = "pine-console"
                };
        }

        var output = console?.Lines.Select((line, index) => new ServerRuntimeLogLine(index + 1, null, "PINE", line)).ToArray() ?? [];
        var now = DateTimeOffset.UtcNow;
        var overview = rconOverview with
        {
            CpuPercent = resources.CpuAbsolute,
            MemoryBytes = resources.MemoryBytes,
            MemoryLimitBytes = server.MemoryLimitMb > 0 ? server.MemoryLimitMb * 1024L * 1024L : null,
            DiskBytes = resources.DiskBytes,
            DiskLimitBytes = server.DiskLimitMb > 0 ? server.DiskLimitMb * 1024L * 1024L : null,
            NetworkRxBytes = resources.NetworkRxBytes,
            NetworkTxBytes = resources.NetworkTxBytes,
            UptimeMilliseconds = resources.UptimeMilliseconds,
            CapturedAt = now,
            PlayerSource = rconOverview.PlayerSource is "rcon" or "pine-console" ? rconOverview.PlayerSource : "rcon-required"
        };
        return new ServerRuntimeSnapshot(
            state,
            resources.IsRunning,
            resources.State == "running",
            rconOverview.PlayerSource == "rcon",
            false,
            false,
            null,
            null,
            output.Length > 0 ? console?.CapturedAt : null,
            output)
        {
            Origin = ServerRuntimeOrigin.PineHostingApi,
            Overview = overview,
            LogSource = console?.Source ?? "Pine Hosting · console indisponible",
            LogStatus = logStatus
        };
    }

    public async Task StartAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        await _pine.SetPowerAsync(connection, PinePowerSignal.Start, cancellationToken);
        await _pine.WaitForStateAsync(connection, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running" }, TimeSpan.FromMinutes(5), cancellationToken);
    }

    public async Task StopAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        var resources = await _pine.GetResourcesAsync(connection, cancellationToken);
        if (!resources.IsRunning) return;
        if (resources.State == "running")
        {
            await _pine.SendCommandAsync(connection, "save", cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await _pine.SendCommandAsync(connection, "quit", cancellationToken);
            try
            {
                await _pine.WaitForStateAsync(connection, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "offline" }, TimeSpan.FromMinutes(2), cancellationToken);
                return;
            }
            catch (TimeoutException) { }
        }
        await _pine.SetPowerAsync(connection, PinePowerSignal.Stop, cancellationToken);
        await _pine.WaitForStateAsync(connection, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "offline" }, TimeSpan.FromMinutes(2), cancellationToken);
    }

    public async Task RestartAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        var resources = await _pine.GetResourcesAsync(connection, cancellationToken);
        if (resources.State == "running")
        {
            await _pine.SendCommandAsync(connection, "save", cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await _pine.SetPowerAsync(connection, PinePowerSignal.Restart, cancellationToken);
        }
        else
        {
            await _pine.SetPowerAsync(connection, PinePowerSignal.Start, cancellationToken);
        }
        await _pine.WaitForStateAsync(connection, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running" }, TimeSpan.FromMinutes(5), cancellationToken);
    }

    public async Task<string> ExecuteCommandAsync(RemoteServerConnection connection, string command, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(connection.RconPassword))
        {
            var host = string.IsNullOrWhiteSpace(connection.RconHost) ? connection.Host : connection.RconHost;
            if (!string.IsNullOrWhiteSpace(host))
                return await _orchestration.ExecuteCommandAsync(host, connection.RconPort, connection.RconPassword, command, cancellationToken);
        }
        await _pine.SendCommandAsync(connection, command, cancellationToken);
        return $"Commande « {command.Trim()} » transmise à la console Pine Hosting.";
    }

    private static (int Count, IReadOnlyList<ServerPlayerSnapshot> Players)? ParseLatestPlayers(IReadOnlyList<string> lines)
    {
        var marker = -1;
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (!lines[index].Contains("Players connected", StringComparison.OrdinalIgnoreCase)) continue;
            marker = index;
            break;
        }
        if (marker < 0) return null;
        var response = string.Join('\n', lines.Skip(marker).Take(64));
        return ServerOrchestrationService.ParsePlayersResponse(response);
    }
}
