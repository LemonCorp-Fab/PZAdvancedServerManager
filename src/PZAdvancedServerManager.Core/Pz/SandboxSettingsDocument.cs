using System.Text;
using System.Text.RegularExpressions;

namespace PZAdvancedServerManager.Core.Pz;

public sealed partial class SandboxSettingsDocument
{
    private readonly List<string> _lines;
    private readonly Encoding _encoding;
    private readonly string _newLine;
    private readonly bool _endsWithNewLine;

    private SandboxSettingsDocument(List<string> lines, Encoding encoding, string newLine, bool endsWithNewLine)
    {
        _lines = lines;
        _encoding = encoding;
        _newLine = newLine;
        _endsWithNewLine = endsWithNewLine;
        Settings = ParseEntries();
    }

    public IReadOnlyList<StructuredServerSetting> Settings { get; private set; }

    public static SandboxSettingsDocument Load(string path)
    {
        var (text, encoding) = ServerConfigDocument.ReadText(path);
        return Parse(text, encoding);
    }

    public static SandboxSettingsDocument Parse(string text, Encoding? encoding = null)
    {
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewLine = text.EndsWith('\n');
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (endsWithNewLine && lines.Count > 0) lines.RemoveAt(lines.Count - 1);
        return new SandboxSettingsDocument(lines, encoding ?? new UTF8Encoding(false), newLine, endsWithNewLine);
    }

    public string Get(string path) => Settings.FirstOrDefault(x => x.Key.Equals(path, StringComparison.Ordinal))?.Value ?? string.Empty;

    public void Update(IReadOnlyDictionary<string, string> submitted)
    {
        var stack = new List<string>();
        for (var index = 0; index < _lines.Count; index++)
        {
            var trimmed = _lines[index].Trim();
            var open = TableOpenRegex().Match(trimmed);
            if (open.Success)
            {
                if (!open.Groups[1].Value.Equals("SandboxVars", StringComparison.Ordinal)) stack.Add(open.Groups[1].Value);
                continue;
            }
            if (trimmed.StartsWith('}'))
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            var assignment = AssignmentRegex().Match(trimmed);
            if (!assignment.Success) continue;
            var key = assignment.Groups[1].Value;
            var path = string.Join('.', stack.Append(key));
            if (!submitted.TryGetValue(path, out var value)) continue;
            var current = Settings.First(x => x.Key.Equals(path, StringComparison.Ordinal));
            var validated = StructuredServerSettings.ValidateAndFormat(current, value, current.Value);
            var indentation = _lines[index][..(_lines[index].Length - _lines[index].TrimStart().Length)];
            var rendered = current.Kind is StructuredSettingKind.Text or StructuredSettingKind.LongText or StructuredSettingKind.Secret
                ? '"' + EscapeLua(validated) + '"'
                : validated;
            _lines[index] = $"{indentation}{key} = {rendered},";
        }
        Settings = ParseEntries();
    }

    public string Render()
    {
        var text = string.Join(_newLine, _lines);
        return _endsWithNewLine ? text + _newLine : text;
    }

    public void Save(string path)
    {
        var temp = path + ".pzasm.tmp";
        File.WriteAllText(temp, Render(), _encoding);
        File.Move(temp, path, true);
    }

    private IReadOnlyList<StructuredServerSetting> ParseEntries()
    {
        var results = new List<StructuredServerSetting>();
        var comments = new List<string>();
        var stack = new List<string>();
        foreach (var raw in _lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                comments.Add(trimmed[2..].Trim());
                continue;
            }
            var open = TableOpenRegex().Match(trimmed);
            if (open.Success)
            {
                if (!open.Groups[1].Value.Equals("SandboxVars", StringComparison.Ordinal)) stack.Add(open.Groups[1].Value);
                comments.Clear();
                continue;
            }
            if (trimmed.StartsWith('}'))
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                comments.Clear();
                continue;
            }
            var assignment = AssignmentRegex().Match(trimmed);
            if (!assignment.Success)
            {
                if (trimmed.Length > 0) comments.Clear();
                continue;
            }
            var key = assignment.Groups[1].Value;
            var rawValue = assignment.Groups[2].Value.Trim();
            var value = Unquote(rawValue);
            var path = string.Join('.', stack.Append(key));
            var category = stack.Count == 0 ? "Monde général" : stack[0] switch
            {
                "ZombieConfig" => "Population & zombies",
                "ZombieLore" => "Comportement des zombies",
                "Map" => "Carte & exploration",
                _ => stack[0]
            };
            results.Add(StructuredServerSettings.Create(path, value, category, string.Join(" ", comments.Where(x => x.Length > 0))));
            comments.Clear();
        }
        return results;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        return value;
    }

    private static string EscapeLua(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\{")]
    private static partial Regex TableOpenRegex();

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+),\s*$")]
    private static partial Regex AssignmentRegex();
}
