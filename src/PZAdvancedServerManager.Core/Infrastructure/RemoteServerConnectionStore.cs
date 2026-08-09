using System.Text.Json;
using System.Text.Json.Serialization;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Infrastructure;

public sealed class RemoteServerConnectionStore(ApplicationPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _sync = new();

    public IReadOnlyList<RemoteServerConnection> GetAll()
    {
        lock (_sync)
        {
            if (!File.Exists(paths.RemoteServersFile)) return [];
            return JsonSerializer.Deserialize<List<RemoteServerConnection>>(File.ReadAllText(paths.RemoteServersFile), JsonOptions) ?? [];
        }
    }

    public RemoteServerConnection? Get(string name) => GetAll().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Save(RemoteServerConnection connection)
    {
        lock (_sync)
        {
            var connections = File.Exists(paths.RemoteServersFile)
                ? JsonSerializer.Deserialize<List<RemoteServerConnection>>(File.ReadAllText(paths.RemoteServersFile), JsonOptions) ?? []
                : [];
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
            if (!File.Exists(paths.RemoteServersFile)) return false;
            var connections = JsonSerializer.Deserialize<List<RemoteServerConnection>>(File.ReadAllText(paths.RemoteServersFile), JsonOptions) ?? [];
            var removed = connections.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Write(connections);
            return removed;
        }
    }

    private void Write(IReadOnlyCollection<RemoteServerConnection> connections)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RemoteServersFile)!);
        var temporary = paths.RemoteServersFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(connections, JsonOptions));
        File.Move(temporary, paths.RemoteServersFile, true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(paths.RemoteServersFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
