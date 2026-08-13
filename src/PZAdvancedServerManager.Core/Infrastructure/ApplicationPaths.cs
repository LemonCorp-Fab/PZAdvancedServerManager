using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Infrastructure;

public sealed class ApplicationPaths
{
    public ApplicationPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            PzasmConstants.DataVendor,
            PzasmConstants.DataFolder);
        ProjectsRoot = Path.Combine(DataRoot, "projects");
        BuildsRoot = Path.Combine(DataRoot, "builds");
        SourcesRoot = Path.Combine(DataRoot, "sources");
        LogsRoot = Path.Combine(DataRoot, "logs");
        LocksRoot = Path.Combine(DataRoot, "locks");
        ProfilesRoot = Path.Combine(DataRoot, "profiles");
        ServerDataRoot = Path.Combine(DataRoot, "server-data");
        AssetsRoot = Path.Combine(DataRoot, "assets");
        ToolsRoot = Path.Combine(DataRoot, "tools");
        RuntimeHomeRoot = Path.Combine(DataRoot, "home");

        Directory.CreateDirectory(ProjectsRoot);
        Directory.CreateDirectory(BuildsRoot);
        Directory.CreateDirectory(SourcesRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(LocksRoot);
        Directory.CreateDirectory(ProfilesRoot);
        Directory.CreateDirectory(ServerDataRoot);
        Directory.CreateDirectory(AssetsRoot);
        Directory.CreateDirectory(ToolsRoot);
        Directory.CreateDirectory(RuntimeHomeRoot);
        Directory.CreateDirectory(TransfersRoot);
    }

    public string DataRoot { get; }
    public string ProjectsRoot { get; }
    public string BuildsRoot { get; }
    public string SourcesRoot { get; }
    public string LogsRoot { get; }
    public string LocksRoot { get; }
    public string ProfilesRoot { get; }
    public string ServerDataRoot { get; }
    public string AssetsRoot { get; }
    public string ToolsRoot { get; }
    public string RuntimeHomeRoot { get; }
    public string SteamCmdRoot => Path.Combine(ToolsRoot, "steamcmd");
    public string SteamCmdExecutable => Path.Combine(SteamCmdRoot, OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");

    public IReadOnlyList<string> GetSteamWorkshopRoots(string? steamCmdExecutable = null)
    {
        var executableRoot = string.IsNullOrWhiteSpace(steamCmdExecutable)
            ? SteamCmdRoot
            : Path.GetDirectoryName(Path.GetFullPath(steamCmdExecutable)) ?? SteamCmdRoot;
        var executableWorkshopRoot = Path.Combine(executableRoot, "steamapps", "workshop");
        var runtimeWorkshopRoot = Path.Combine(RuntimeHomeRoot, "Steam", "steamapps", "workshop");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profileWorkshopRoot = Path.Combine(userProfile, "Steam", "steamapps", "workshop");
        var candidates = OperatingSystem.IsWindows()
            ? new[] { executableWorkshopRoot, runtimeWorkshopRoot, profileWorkshopRoot }
            : new[] { runtimeWorkshopRoot, profileWorkshopRoot, executableWorkshopRoot };
        return candidates
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    public string ResolveSteamWorkshopRoot(string? steamCmdExecutable = null, IEnumerable<ulong>? workshopIds = null)
    {
        var ids = workshopIds?.Where(id => id != 0).Distinct().ToArray() ?? [];
        var roots = GetSteamWorkshopRoots(steamCmdExecutable);
        return roots
            .Select((root, index) => new
            {
                Root = root,
                Index = index,
                Score = ids.Count(id => Directory.Exists(Path.Combine(root, "content", PzasmConstants.ProjectZomboidSteamAppId, id.ToString()))) * 100
                        + (File.Exists(Path.Combine(root, $"appworkshop_{PzasmConstants.ProjectZomboidSteamAppId}.acf")) ? 10 : 0)
                        + (Directory.Exists(root) ? 1 : 0)
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .First().Root;
    }

    public string ResolveSteamWorkshopItemRoot(string? steamCmdExecutable, ulong workshopId)
    {
        var workshopRoot = ResolveSteamWorkshopRoot(steamCmdExecutable, [workshopId]);
        return Path.Combine(workshopRoot, "content", PzasmConstants.ProjectZomboidSteamAppId, workshopId.ToString());
    }

    public string ProjectFile(Guid id) => Path.Combine(ProjectsRoot, $"{id:N}{PzasmConstants.ProjectFileExtension}");
    public string BuildRoot(Guid id) => Path.Combine(BuildsRoot, id.ToString("N"));
    public string ProjectSourcesRoot(Guid id) => Path.Combine(SourcesRoot, id.ToString("N"));
    public string ModSourceRoot(Guid projectId, Guid modReferenceId) => Path.Combine(ProjectSourcesRoot(projectId), modReferenceId.ToString("N"));
    public string ProjectAssetsRoot(Guid id) => Path.Combine(AssetsRoot, id.ToString("N"));
    public string ProjectLockFile(Guid id) => Path.Combine(LocksRoot, $"{id:N}.lock");
    public string AutomationLockFile => Path.Combine(LocksRoot, "automation.lock");
    public string RemoteServersFile => Path.Combine(ProfilesRoot, "remote-servers.json");
    public string ImportedServerKeysRoot => Path.Combine(ProfilesRoot, "imported-keys");
    public string TransfersRoot => Path.Combine(DataRoot, "transfers");
    public string LocalServerProfilesFile => Path.Combine(ProfilesRoot, "local-server-profiles.json");
    public string ServerBackupsRoot(string profileName) => Path.Combine(ServerDataRoot, profileName, "backups");
}
