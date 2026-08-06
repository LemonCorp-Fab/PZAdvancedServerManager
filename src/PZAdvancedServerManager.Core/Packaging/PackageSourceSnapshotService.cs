using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Packaging;

public sealed class PackageSourceSnapshotService(ApplicationPaths paths)
{
    public void EnsurePinned(PackageProject project)
    {
        foreach (var mod in project.Mods.Where(x => x.Enabled))
        {
            if (!Directory.Exists(mod.PinnedSourceRoot)) Pin(project, mod, replace: false);
            var currentHash = SafeFileTree.ComputeDirectoryHash(mod.PinnedSourceRoot);
            if (string.IsNullOrWhiteSpace(mod.PinnedContentHash)) mod.PinnedContentHash = currentHash;
            else if (!currentHash.Equals(mod.PinnedContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Le snapshot figé de « {mod.Name} » a été modifié hors de PZASM. Actualisez explicitement les sources pour créer une nouvelle version contrôlée.");
        }
    }

    public void UpdateAll(PackageProject project)
    {
        foreach (var mod in project.Mods.Where(x => x.Enabled)) Pin(project, mod, replace: true);
    }

    public void Pin(PackageProject project, PackageModReference mod, bool replace)
    {
        if (!Directory.Exists(mod.SourceModRoot))
            throw new DirectoryNotFoundException($"Source introuvable pour « {mod.Name} » : {mod.SourceModRoot}");
        if (!replace && Directory.Exists(mod.PinnedSourceRoot)) return;

        mod.SourceFolderName = SanitizeFolderName(string.IsNullOrWhiteSpace(mod.SourceFolderName)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(mod.SourceModRoot))
            : mod.SourceFolderName);
        var container = paths.ModSourceRoot(project.Id, mod.Id);
        var final = Path.Combine(container, mod.SourceFolderName);
        var next = container + ".next";
        SafeFileTree.DeleteScopedDirectory(paths.SourcesRoot, next);
        Directory.CreateDirectory(next);
        var staged = Path.Combine(next, mod.SourceFolderName);
        try
        {
            SafeFileTree.CopyDirectory(mod.SourceModRoot, staged);
            var hash = SafeFileTree.ComputeDirectoryHash(staged);
            SafeFileTree.ReplaceDirectory(paths.SourcesRoot, next, container);
            mod.PinnedSourceRoot = final;
            mod.PinnedAt = DateTimeOffset.UtcNow;
            mod.PinnedContentHash = hash;
        }
        catch
        {
            SafeFileTree.DeleteScopedDirectory(paths.SourcesRoot, next);
            throw;
        }
    }

    public void Delete(PackageProject project, PackageModReference mod) =>
        SafeFileTree.DeleteScopedDirectory(paths.SourcesRoot, paths.ModSourceRoot(project.Id, mod.Id));

    public void DeleteProject(Guid projectId) =>
        SafeFileTree.DeleteScopedDirectory(paths.SourcesRoot, paths.ProjectSourcesRoot(projectId));

    private static string SanitizeFolderName(string value)
    {
        var cleaned = string.Concat(value.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c is not '/' and not '\\')).Trim();
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..") throw new InvalidOperationException("Nom de dossier source invalide.");
        return cleaned;
    }
}
