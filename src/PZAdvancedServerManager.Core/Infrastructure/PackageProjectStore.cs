using System.Text.Json;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Infrastructure;

public sealed class PackageProjectStore(ApplicationPaths paths)
{
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public IReadOnlyList<PackageProject> GetAll()
    {
        lock (_sync)
        {
            return Directory.EnumerateFiles(paths.ProjectsRoot, "*.pzasm.json")
                .Select(ReadFile)
                .Where(x => x is not null)
                .Cast<PackageProject>()
                .OrderByDescending(x => x.UpdatedAt)
                .ToList();
        }
    }

    public PackageProject? Get(Guid id)
    {
        lock (_sync)
        {
            var file = paths.ProjectFile(id);
            return File.Exists(file) ? ReadFile(file) : null;
        }
    }

    public PackageProject Save(PackageProject project)
    {
        lock (_sync)
        {
            if (project.Id == Guid.Empty)
                project.Id = Guid.NewGuid();
            project.UpdatedAt = DateTimeOffset.UtcNow;
            var target = paths.ProjectFile(project.Id);
            var temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(project, JsonOptions));
            File.Move(temporary, target, true);
            return project;
        }
    }

    public PackageProject Create(string name)
    {
        var project = new PackageProject { Name = string.IsNullOrWhiteSpace(name) ? "Nouveau pack serveur" : name.Trim() };
        return Save(project);
    }

    private static PackageProject? ReadFile(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<PackageProject>(File.ReadAllText(file), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
