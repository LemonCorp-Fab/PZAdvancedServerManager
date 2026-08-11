using System.Text.Json;
using System.Text.Json.Serialization;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Infrastructure;

public sealed class RemoteServerConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ApplicationPaths _paths;
    private readonly StoredSecretProtector _protector;
    private readonly object _sync = new();

    public RemoteServerConnectionStore(ApplicationPaths paths)
        : this(paths, new StoredSecretProtector(paths)) { }

    public RemoteServerConnectionStore(ApplicationPaths paths, StoredSecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    public IReadOnlyList<RemoteServerConnection> GetAll()
    {
        lock (_sync)
        {
            var connections = Read(out var requiresMigration);
            if (requiresMigration) Write(connections);
            return connections;
        }
    }

    public RemoteServerConnection? Get(string name) => GetAll().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Save(RemoteServerConnection connection)
    {
        lock (_sync)
        {
            var connections = Read(out _);
            var index = connections.FindIndex(x => x.Name.Equals(connection.Name, StringComparison.OrdinalIgnoreCase));
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            if (index >= 0) connections[index] = connection;
            else connections.Add(connection);
            Write(connections);
        }
    }

    public bool Remove(string name)
    {
        lock (_sync)
        {
            if (!File.Exists(_paths.RemoteServersFile)) return false;
            var connections = Read(out _);
            var removed = connections.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Write(connections);
            return removed;
        }
    }

    public void Import(IReadOnlyCollection<RemoteServerConnection> imported, bool replaceExisting)
    {
        lock (_sync)
        {
            var duplicateId = imported.GroupBy(connection => connection.Id).FirstOrDefault(group => group.Count() > 1);
            if (duplicateId is not null) throw new InvalidDataException($"Duplicate server identifier in transfer: {duplicateId.Key}.");
            var duplicateName = imported.GroupBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateName is not null) throw new InvalidDataException($"Duplicate server name in transfer: {duplicateName.Key}.");

            var connections = Read(out _);
            foreach (var connection in imported)
            {
                var conflicts = connections
                    .Where(existing => existing.Id == connection.Id || existing.Name.Equals(connection.Name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (conflicts.Length > 0 && !replaceExisting)
                    throw new InvalidOperationException($"A server connection named '{connection.Name}' or using identifier {connection.Id} already exists.");
                foreach (var conflict in conflicts) connections.Remove(conflict);
                connections.Add(connection);
            }
            Write(connections);
        }
    }

    private void Write(IReadOnlyCollection<RemoteServerConnection> connections)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.RemoteServersFile)!);
        var stored = connections.Select(CloneForStorage).ToList();
        var temporary = _paths.RemoteServersFile + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, JsonOptions));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _paths.RemoteServersFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private List<RemoteServerConnection> Read(out bool requiresMigration)
    {
        requiresMigration = false;
        if (!File.Exists(_paths.RemoteServersFile)) return [];
        var connections = JsonSerializer.Deserialize<List<RemoteServerConnection>>(File.ReadAllText(_paths.RemoteServersFile), JsonOptions) ?? [];
        foreach (var connection in connections)
        {
            requiresMigration |= NeedsProtection(connection.RconPassword) || NeedsProtection(connection.ApiToken);
            connection.RconPassword = _protector.Unprotect(connection.RconPassword);
            connection.ApiToken = _protector.Unprotect(connection.ApiToken);
        }
        return connections;
    }

    private bool NeedsProtection(string value) => !string.IsNullOrEmpty(value) && !_protector.IsProtected(value);

    private RemoteServerConnection CloneForStorage(RemoteServerConnection source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Host = source.Host,
        SshPort = source.SshPort,
        SshUser = source.SshUser,
        SshPrivateKeyPath = source.SshPrivateKeyPath,
        RemoteIniPath = source.RemoteIniPath,
        StartCommand = source.StartCommand,
        RconHost = source.RconHost,
        RconPort = source.RconPort,
        RconPassword = _protector.Protect(source.RconPassword),
        AutoRestartAfterRconQuit = source.AutoRestartAfterRconQuit,
        Provider = source.Provider,
        ApiBaseUrl = source.ApiBaseUrl,
        ApiToken = _protector.Protect(source.ApiToken),
        ApiServerIdentifier = source.ApiServerIdentifier,
        ProviderServerName = source.ProviderServerName,
        UpdatedAt = source.UpdatedAt
    };
}
