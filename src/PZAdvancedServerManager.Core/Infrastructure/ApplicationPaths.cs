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

        Directory.CreateDirectory(ProjectsRoot);
        Directory.CreateDirectory(BuildsRoot);
        Directory.CreateDirectory(SourcesRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(LocksRoot);
        Directory.CreateDirectory(ProfilesRoot);
    }

    public string DataRoot { get; }
    public string ProjectsRoot { get; }
    public string BuildsRoot { get; }
    public string SourcesRoot { get; }
    public string LogsRoot { get; }
    public string LocksRoot { get; }
    public string ProfilesRoot { get; }

    public string ProjectFile(Guid id) => Path.Combine(ProjectsRoot, $"{id:N}{PzasmConstants.ProjectFileExtension}");
    public string BuildRoot(Guid id) => Path.Combine(BuildsRoot, id.ToString("N"));
    public string ProjectSourcesRoot(Guid id) => Path.Combine(SourcesRoot, id.ToString("N"));
    public string ModSourceRoot(Guid projectId, Guid modReferenceId) => Path.Combine(ProjectSourcesRoot(projectId), modReferenceId.ToString("N"));
    public string ProjectLockFile(Guid id) => Path.Combine(LocksRoot, $"{id:N}.lock");
    public string AutomationLockFile => Path.Combine(LocksRoot, "automation.lock");
}
