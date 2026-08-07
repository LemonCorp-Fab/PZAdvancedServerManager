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
            return Directory.EnumerateFiles(paths.ProjectsRoot, $"*{PzasmConstants.ProjectFileExtension}")
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
            var temporary = target + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(project, JsonOptions));
                File.Move(temporary, target, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return project;
        }
    }

    public PackageProject Create(string name)
    {
        var project = new PackageProject { Name = string.IsNullOrWhiteSpace(name) ? "Nouveau pack serveur" : name.Trim() };
        return Save(project);
    }

    public bool Delete(Guid id)
    {
        lock (_sync)
        {
            var file = paths.ProjectFile(id);
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
    }

    private static PackageProject? ReadFile(string file)
    {
        try
        {
            var project = JsonSerializer.Deserialize<PackageProject>(File.ReadAllText(file), JsonOptions);
            if (project is null) return null;
            Migrate(project);
            return project;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Migrate(PackageProject project)
    {
        if (project.SchemaVersion < 2)
        {
            foreach (var mod in project.Mods)
                if (string.IsNullOrWhiteSpace(mod.SourceFolderName) && !string.IsNullOrWhiteSpace(mod.SourceModRoot))
                    mod.SourceFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(mod.SourceModRoot));
        }
        foreach (var mod in project.Mods)
        {
            mod.Permission ??= new PermissionEvidence();
            if (string.IsNullOrWhiteSpace(mod.Permission.RightsHolder) && !string.IsNullOrWhiteSpace(mod.Author))
                mod.Permission.RightsHolder = mod.Author;
        }
        project.SchemaVersion = PzasmConstants.CurrentProjectSchemaVersion;
    }
}
