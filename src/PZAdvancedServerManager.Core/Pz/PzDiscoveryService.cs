using System.Text.RegularExpressions;
using Microsoft.Win32;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Pz;

public sealed partial class PzDiscoveryService(ApplicationPaths paths)
{
    public PzInstallation DiscoverInstallation()
    {
        var steamLibraries = DiscoverSteamLibraries().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var client = FindFirstExisting(steamLibraries.Select(x => Path.Combine(x, "steamapps", "common", "ProjectZomboid")));
        var dedicated = FindFirstExisting(steamLibraries.Select(x => Path.Combine(x, "steamapps", "common", "Project Zomboid Dedicated Server")));
        var workshop = FindFirstExisting(steamLibraries.Select(x => Path.Combine(x, "steamapps", "workshop", "content", PzasmConstants.ProjectZomboidSteamAppId)));
        var steamCmdCandidates = new[] { paths.SteamCmdExecutable }.Concat(steamLibraries.SelectMany(x => new[]
        {
            Path.Combine(x, "steamcmd", "steamcmd.exe"),
            Path.Combine(x, "steamapps", "common", "SteamCMD", "steamcmd.exe"),
            Path.Combine(x, "steamcmd.sh"),
            Path.Combine(x, "steamcmd", "steamcmd.sh"),
            Path.Combine(x, "steamapps", "common", "SteamCMD", "steamcmd.sh")
        }));
        var steamCmd = FindFirstExisting(steamCmdCandidates);

        return new PzInstallation
        {
            ClientRoot = client,
            DedicatedServerRoot = dedicated,
            WorkshopRoot = workshop,
            UserZomboidRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Zomboid"),
            SteamCmdPath = steamCmd
        };
    }

    public IReadOnlyList<DiscoveredMod> DiscoverMods(PzInstallation installation, string targetVersion = PzasmConstants.DefaultTargetVersion)
    {
        var roots = new List<(string ModsRoot, ulong WorkshopId)>();
        AddWorkshopMods(roots, installation.WorkshopRoot);
        AddWorkshopMods(roots, Path.Combine(paths.SteamCmdRoot, "steamapps", "workshop", "content", PzasmConstants.ProjectZomboidSteamAppId));
        if (installation.DedicatedServerRoot is not null)
            AddWorkshopMods(roots, Path.Combine(installation.DedicatedServerRoot, "steamapps", "workshop", "content", PzasmConstants.ProjectZomboidSteamAppId));

        AddLocalMods(roots, installation.UserZomboidRoot);
        if (installation.ClientRoot is not null) AddLocalMods(roots, installation.ClientRoot);
        if (installation.DedicatedServerRoot is not null) AddLocalMods(roots, installation.DedicatedServerRoot);

        return DiscoverRoots(roots, targetVersion);
    }

    public IReadOnlyList<DiscoveredMod> DiscoverWorkshopItem(string itemRoot, ulong workshopId, string targetVersion = PzasmConstants.DefaultTargetVersion)
    {
        var modsRoot = Path.Combine(itemRoot, "mods");
        if (!Directory.Exists(modsRoot)) modsRoot = Path.Combine(itemRoot, "Contents", "mods");
        return Directory.Exists(modsRoot) ? DiscoverRoots([(modsRoot, workshopId)], targetVersion) : [];
    }

    private static IReadOnlyList<DiscoveredMod> DiscoverRoots(IReadOnlyList<(string ModsRoot, ulong WorkshopId)> roots, string targetVersion)
    {
        var result = new List<DiscoveredMod>();
        foreach (var (modsRoot, workshopId) in roots)
        {
            foreach (var modRoot in Directory.EnumerateDirectories(modsRoot))
            {
                var manifestPath = PzVersionSelector.SelectManifest(modRoot, targetVersion, out var selectedFolder);
                if (string.IsNullOrWhiteSpace(manifestPath)) continue;
                ModInfo info;
                try { info = ModInfoParser.Parse(manifestPath); }
                catch (IOException) { continue; }
                if (string.IsNullOrWhiteSpace(info.Id)) continue;

                var mapFolders = PzVersionSelector.GetEffectiveMediaRoots(modRoot, selectedFolder)
                    .Select(x => Path.Combine(x, "maps"))
                    .Where(Directory.Exists)
                    .SelectMany(x => Directory.EnumerateDirectories(x))
                    .Select(Path.GetFileName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                result.Add(new DiscoveredMod
                {
                    WorkshopId = workshopId,
                    ModRoot = modRoot,
                    ModId = info.Id,
                    Name = string.IsNullOrWhiteSpace(info.Name) ? info.Id : info.Name,
                    Author = info.Author,
                    Description = info.Description,
                    Poster = info.Poster,
                    Version = info.Version,
                    EffectiveManifestPath = manifestPath,
                    SelectedVersionFolder = selectedFolder,
                    RequiredModIds = info.Required,
                    MapFolders = mapFolders,
                    SourceUpdatedAt = new DateTimeOffset(Directory.GetLastWriteTimeUtc(modRoot), TimeSpan.Zero)
                });
            }
        }

        return result
            .GroupBy(x => $"{x.WorkshopId}:{x.ModId}:{x.ModRoot}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void AddLocalMods(List<(string ModsRoot, ulong WorkshopId)> roots, string baseRoot)
    {
        var mods = Path.Combine(baseRoot, "mods");
        if (Directory.Exists(mods)) roots.Add((mods, 0));
    }

    private static void AddWorkshopMods(List<(string ModsRoot, ulong WorkshopId)> roots, string? workshopRoot)
    {
        if (!Directory.Exists(workshopRoot)) return;
        foreach (var item in Directory.EnumerateDirectories(workshopRoot))
        {
            if (!ulong.TryParse(Path.GetFileName(item), out var workshopId)) continue;
            var mods = Path.Combine(item, "mods");
            if (!Directory.Exists(mods)) mods = Path.Combine(item, "Contents", "mods");
            if (Directory.Exists(mods)) roots.Add((mods, workshopId));
        }
    }

    private static string? FindFirstExisting(IEnumerable<string> paths) => paths.FirstOrDefault(Directory.Exists) ?? paths.FirstOrDefault(File.Exists);

    private static IEnumerable<string> DiscoverSteamLibraries()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(userHome, ".steam", "steam"));
            candidates.Add(Path.Combine(userHome, ".steam", "root"));
            candidates.Add(Path.Combine(userHome, ".local", "share", "Steam"));
        }
        var registryPath = OperatingSystem.IsWindows()
            ? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            : null;
        if (!string.IsNullOrWhiteSpace(registryPath)) candidates.Add(registryPath.Replace('/', Path.DirectorySeparatorChar));
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady))
        {
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam"));
        }

        foreach (var steamRoot in candidates.ToArray())
        {
            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            foreach (Match match in LibraryPathRegex().Matches(File.ReadAllText(libraryFile)))
                candidates.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        }
        return candidates.Where(Directory.Exists);
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}
