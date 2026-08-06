namespace PZAdvancedServerManager.Core.Infrastructure;

public sealed class ApplicationPaths
{
    public ApplicationPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LemonCorp",
            "PZAdvancedServerManager");
        ProjectsRoot = Path.Combine(DataRoot, "projects");
        BuildsRoot = Path.Combine(DataRoot, "builds");
        LogsRoot = Path.Combine(DataRoot, "logs");
        ProfilesRoot = Path.Combine(DataRoot, "profiles");

        Directory.CreateDirectory(ProjectsRoot);
        Directory.CreateDirectory(BuildsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(ProfilesRoot);
    }

    public string DataRoot { get; }
    public string ProjectsRoot { get; }
    public string BuildsRoot { get; }
    public string LogsRoot { get; }
    public string ProfilesRoot { get; }

    public string ProjectFile(Guid id) => Path.Combine(ProjectsRoot, $"{id:N}.pzasm.json");
    public string BuildRoot(Guid id) => Path.Combine(BuildsRoot, id.ToString("N"));
}
