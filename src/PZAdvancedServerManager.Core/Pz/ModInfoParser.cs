using System.Collections.Concurrent;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Pz;

public static class ModInfoParser
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(PathComparer);

    public static ModInfo Parse(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("Le fichier mod.info est introuvable.", fullPath);
        var stamp = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
        if (Cache.TryGetValue(fullPath, out var cached) && cached.Stamp == stamp) return cached.Info;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(fullPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            values[key] = value;
        }

        values.TryGetValue("require", out var required);
        var info = new ModInfo
        {
            Name = Get(values, "name"),
            Id = Get(values, "id"),
            Author = Get(values, "author"),
            Description = Get(values, "description"),
            Poster = Get(values, "poster"),
            Version = First(values, "version", "modversion"),
            Required = SplitList(required),
            Properties = values
        };
        Cache[fullPath] = new CacheEntry(stamp, info);
        return info;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value.Trim('"') : string.Empty;

    private static string First(IReadOnlyDictionary<string, string> values, params string[] keys) =>
        keys.Select(key => Get(values, key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string[] SplitList(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks);
    private sealed record CacheEntry(FileStamp Stamp, ModInfo Info);
}

public static class PzVersionSelector
{
    public static string SelectManifest(string modRoot, string targetVersion, out string selectedVersionFolder)
    {
        selectedVersionFolder = string.Empty;
        var candidates = Directory.EnumerateDirectories(modRoot)
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(x => TryParseVersion(x.Name, out _))
            .Select(x => (x.Path, x.Name, Version: ParseVersion(x.Name)))
            .Where(x => x.Version <= ParseVersion(targetVersion))
            .Where(x => File.Exists(Path.Combine(x.Path, "mod.info")))
            .OrderByDescending(x => x.Version)
            .ToList();

        if (candidates.Count > 0)
        {
            selectedVersionFolder = candidates[0].Name;
            return Path.Combine(candidates[0].Path, "mod.info");
        }

        var common = Path.Combine(modRoot, "common", "mod.info");
        if (File.Exists(common))
        {
            selectedVersionFolder = "common";
            return common;
        }

        var legacy = Path.Combine(modRoot, "mod.info");
        return File.Exists(legacy) ? legacy : string.Empty;
    }

    public static IReadOnlyList<string> GetEffectiveMediaRoots(string modRoot, string selectedVersionFolder)
    {
        var result = new List<string>();
        var legacyMedia = Path.Combine(modRoot, "media");
        var commonMedia = Path.Combine(modRoot, "common", "media");
        if (Directory.Exists(legacyMedia)) result.Add(legacyMedia);
        if (Directory.Exists(commonMedia)) result.Add(commonMedia);
        if (!string.IsNullOrWhiteSpace(selectedVersionFolder) && !selectedVersionFolder.Equals("common", StringComparison.OrdinalIgnoreCase))
        {
            var versionMedia = Path.Combine(modRoot, selectedVersionFolder, "media");
            if (Directory.Exists(versionMedia)) result.Add(versionMedia);
        }
        return result;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Trim();
        if (normalized.Count(c => c == '.') == 0)
            normalized += ".0";
        return Version.TryParse(normalized, out version!);
    }

    private static Version ParseVersion(string value) => TryParseVersion(value, out var version) ? version : new Version(0, 0);
}
