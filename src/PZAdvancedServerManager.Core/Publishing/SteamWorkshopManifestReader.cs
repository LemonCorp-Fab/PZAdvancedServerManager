using System.Globalization;
using System.Text.RegularExpressions;

namespace PZAdvancedServerManager.Core.Publishing;

public static partial class SteamWorkshopManifestReader
{
    public static IReadOnlyDictionary<ulong, SteamWorkshopItemState> Read(string path)
    {
        if (!File.Exists(path)) return new Dictionary<ulong, SteamWorkshopItemState>();
        return Parse(File.ReadAllText(path));
    }

    public static IReadOnlyDictionary<ulong, SteamWorkshopItemState> Parse(string value)
    {
        var installedSection = ReadObject(value, "WorkshopItemsInstalled");
        if (installedSection.Length == 0) return new Dictionary<ulong, SteamWorkshopItemState>();

        var result = new Dictionary<ulong, SteamWorkshopItemState>();
        foreach (Match match in ItemBlockRegex().Matches(installedSection))
        {
            if (!ulong.TryParse(match.Groups["id"].Value, CultureInfo.InvariantCulture, out var id)) continue;
            var body = match.Groups["body"].Value;
            result[id] = new SteamWorkshopItemState(
                id,
                ReadString(body, "manifest"),
                ReadLong(body, "timeupdated"),
                ReadLong(body, "size"));
        }

        return result;
    }

    private static string ReadObject(string value, string key)
    {
        var keyIndex = value.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0) return string.Empty;
        var openBrace = value.IndexOf('{', keyIndex + key.Length + 2);
        if (openBrace < 0) return string.Empty;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = openBrace; index < value.Length; index++)
        {
            var character = value[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }
            if (character == '{') depth++;
            else if (character == '}' && --depth == 0)
                return value[(openBrace + 1)..index];
        }

        return string.Empty;
    }

    private static string ReadString(string body, string key)
    {
        var match = Regex.Match(body, $"\"{Regex.Escape(key)}\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }

    private static long ReadLong(string body, string key) =>
        long.TryParse(ReadString(body, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    [GeneratedRegex("\\\"(?<id>\\d+)\\\"\\s*\\{(?<body>[^{}]*)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex ItemBlockRegex();
}

public sealed record SteamWorkshopItemState(ulong WorkshopId, string ManifestId, long TimeUpdated, long Size);
