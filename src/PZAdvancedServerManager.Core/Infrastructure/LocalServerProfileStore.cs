using System.Text.Json;
using System.Text.Json.Serialization;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Infrastructure;

public sealed class LocalServerProfileStore(ApplicationPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _gate = new();

    public LocalServerMode? Get(string name)
    {
        lock (_gate)
            return Read().FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Mode;
    }

    public void Save(string name, LocalServerMode mode)
    {
        lock (_gate)
        {
            var entries = Read();
            var existing = entries.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) entries.Add(new LocalServerProfilePreference { Name = name, Mode = mode });
            else existing.Mode = mode;
            Write(entries);
        }
    }

    private List<LocalServerProfilePreference> Read()
    {
        if (!File.Exists(paths.LocalServerProfilesFile)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<LocalServerProfilePreference>>(
                File.ReadAllText(paths.LocalServerProfilesFile),
                JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write(IReadOnlyCollection<LocalServerProfilePreference> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LocalServerProfilesFile)!);
        var temporary = paths.LocalServerProfilesFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries.OrderBy(entry => entry.Name), JsonOptions));
        File.Move(temporary, paths.LocalServerProfilesFile, true);
    }

    private sealed class LocalServerProfilePreference
    {
        public string Name { get; set; } = string.Empty;
        public LocalServerMode Mode { get; set; }
    }
}
