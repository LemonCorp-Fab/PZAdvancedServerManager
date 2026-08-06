using System.Text;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class ServerConfigDocument
{
    private readonly List<ConfigLine> _lines;
    private readonly Encoding _encoding;
    private readonly string _newLine;
    private readonly bool _endsWithNewLine;

    private ServerConfigDocument(List<ConfigLine> lines, Encoding encoding, string newLine, bool endsWithNewLine)
    {
        _lines = lines;
        _encoding = encoding;
        _newLine = newLine;
        _endsWithNewLine = endsWithNewLine;
    }

    public static ServerConfigDocument Load(string path)
    {
        if (!File.Exists(path)) return new ServerConfigDocument([], new UTF8Encoding(false), Environment.NewLine, false);
        var (text, encoding) = ReadText(path);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewLine = text.EndsWith("\n", StringComparison.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (endsWithNewLine && lines.Count > 0) lines.RemoveAt(lines.Count - 1);
        return new ServerConfigDocument(lines.Select(ParseLine).ToList(), encoding, newLine, endsWithNewLine);
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
        File.WriteAllText(temp, Render(), _encoding);
        File.Move(temp, path, true);
    }

    public string Render()
    {
        var text = string.Join(_newLine, _lines.Select(x => x.Raw is not null ? x.Raw : $"{x.Key}={x.Value}"));
        return _endsWithNewLine || _lines.Count == 0 ? text + _newLine : text;
    }

    public Encoding Encoding => _encoding;

    public static (string Text, Encoding Encoding) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            return (Encoding.UTF8.GetString(bytes[3..]), new UTF8Encoding(true));
        try
        {
            var strictUtf8 = new UTF8Encoding(false, true);
            return (strictUtf8.GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
        }
    }

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
