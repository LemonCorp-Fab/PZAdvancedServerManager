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
