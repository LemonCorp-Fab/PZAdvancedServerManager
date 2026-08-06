namespace PZAdvancedServerManager.Core.Pz;

public sealed class ServerConfigDocument
{
    private readonly List<ConfigLine> _lines;

    private ServerConfigDocument(List<ConfigLine> lines) => _lines = lines;

    public static ServerConfigDocument Load(string path)
    {
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).Select(ParseLine).ToList()
            : [];
        return new ServerConfigDocument(lines);
    }

    public string Get(string key) => _lines.LastOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    public void Set(string key, string value)
    {
        var existing = _lines.LastOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _lines.Add(new ConfigLine(key, value, null));
        else existing.Value = value;
    }

    public IReadOnlyList<string> GetList(string key) => Get(key)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void SetList(string key, IEnumerable<string> values) => Set(key, string.Join(';', values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)));

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temp = path + ".pzasm.tmp";
        File.WriteAllLines(temp, _lines.Select(x => x.Raw is not null ? x.Raw : $"{x.Key}={x.Value}"));
        File.Move(temp, path, true);
    }

    public string Render() => string.Join(Environment.NewLine, _lines.Select(x => x.Raw is not null ? x.Raw : $"{x.Key}={x.Value}"));

    private static ConfigLine ParseLine(string line)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0 || line.TrimStart().StartsWith('#')) return new ConfigLine(string.Empty, string.Empty, line);
        return new ConfigLine(line[..separator].Trim(), line[(separator + 1)..], null);
    }

    private sealed class ConfigLine(string key, string value, string? raw)
    {
        public string Key { get; } = key;
        public string Value { get; set; } = value;
        public string? Raw { get; } = raw;
    }
}
