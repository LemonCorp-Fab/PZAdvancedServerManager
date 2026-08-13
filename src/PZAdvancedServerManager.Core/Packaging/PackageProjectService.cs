using System.Text.Json;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Packaging;

public sealed class PackageProjectService(
    ApplicationPaths paths,
    PackageProjectStore store,
    PackageSourceSnapshotService snapshots)
{
    public PackageProject Create(string name) => store.Create(name);

    public int AddWithDependencies(
        PackageProject project,
        DiscoveredMod selected,
        IReadOnlyList<DiscoveredMod> discovered,
        bool includeDependencies = true)
    {
        var added = PackageProjectComposer.AddWithDependencies(project, selected, discovered, includeDependencies);
        try
        {
            foreach (var mod in added) snapshots.Pin(project, mod, replace: false);
            foreach (var mod in added) mod.SourceModRoot = mod.PinnedSourceRoot;
            NormalizeOrder(project);
            store.Save(project);
            return added.Count;
        }
        catch
        {
            foreach (var mod in added)
            {
                project.Mods.Remove(mod);
                snapshots.Delete(project, mod);
            }
            throw;
        }
    }

    public void SetWorkshopSourceToken(PackageProject project, ulong workshopId, string sourceUpdateToken)
    {
        if (workshopId == 0 || string.IsNullOrWhiteSpace(sourceUpdateToken)) return;
        foreach (var mod in project.Mods.Where(mod => mod.WorkshopId == workshopId && Directory.Exists(mod.PinnedSourceRoot)))
        {
            mod.SourceUpdateToken = sourceUpdateToken;
            mod.SourceModRoot = mod.PinnedSourceRoot;
        }
        store.Save(project);
    }

    public void Remove(PackageProject project, Guid modReferenceId)
    {
        var mod = project.Mods.FirstOrDefault(x => x.Id == modReferenceId)
            ?? throw new InvalidOperationException("Référence de mod introuvable.");
        project.Mods.Remove(mod);
        foreach (var map in mod.MapFolders.Where(map => project.Mods.All(x => !x.MapFolders.Contains(map, StringComparer.OrdinalIgnoreCase))))
            project.MapOrder.RemoveAll(x => x.Equals(map, StringComparison.OrdinalIgnoreCase));
        NormalizeOrder(project);
        store.Save(project);
        snapshots.Delete(project, mod);
    }

    public void Move(PackageProject project, Guid modReferenceId, int direction)
    {
        var ordered = project.Mods.OrderBy(x => x.Order).ToList();
        var index = ordered.FindIndex(x => x.Id == modReferenceId);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= ordered.Count) return;
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        project.Mods = ordered;
        store.Save(project);
    }

    public void Reorder(PackageProject project, Guid modReferenceId, ModPlacement placement, Guid? targetModReferenceId = null)
    {
        var ordered = project.Mods.OrderBy(x => x.Order).ToList();
        var mod = ordered.FirstOrDefault(x => x.Id == modReferenceId)
            ?? throw new InvalidOperationException("Référence de mod introuvable.");

        ordered.Remove(mod);
        var insertionIndex = placement switch
        {
            ModPlacement.First => 0,
            ModPlacement.Last => ordered.Count,
            ModPlacement.Before => ResolveTargetIndex(ordered, targetModReferenceId),
            ModPlacement.After => ResolveTargetIndex(ordered, targetModReferenceId) + 1,
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, null)
        };

        ordered.Insert(insertionIndex, mod);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        project.Mods = ordered;
        store.Save(project);
    }

    public PackageProject Duplicate(Guid id, string? name = null)
    {
        var source = store.Get(id) ?? throw new InvalidOperationException("Projet PZASM introuvable.");
        var clone = JsonSerializer.Deserialize<PackageProject>(JsonSerializer.Serialize(source))
            ?? throw new InvalidOperationException("Le projet n'a pas pu être dupliqué.");
        clone.Id = Guid.NewGuid();
        clone.Name = string.IsNullOrWhiteSpace(name) ? source.Name + " — copie" : name.Trim();
        clone.PublishedWorkshopId = 0;
        clone.Publication = new WorkshopPublicationState();
        clone.CreatedAt = clone.UpdatedAt = DateTimeOffset.UtcNow;
        clone.LastBuiltAt = clone.LastPublishedAt = null;
        clone.Automation.Enabled = false;
        clone.Automation.LastAttemptAt = clone.Automation.LastSuccessAt = null;
        clone.Automation.LastResult = string.Empty;
        var requiresPortableSources = source.PortableSourcesRequired;
        var refreshSources = new Dictionary<Guid, string>();
        foreach (var mod in clone.Mods)
        {
            mod.Id = Guid.NewGuid();
            refreshSources[mod.Id] = mod.SourceModRoot;
            if (!requiresPortableSources && Directory.Exists(mod.PinnedSourceRoot)) mod.SourceModRoot = mod.PinnedSourceRoot;
            else if (requiresPortableSources) mod.SourceModRoot = string.Empty;
            mod.PinnedSourceRoot = string.Empty;
            mod.PinnedAt = null;
            mod.PinnedContentHash = string.Empty;
            mod.PinnedMetadataStamp = string.Empty;
            mod.ValidatedContentHash = string.Empty;
            mod.SourceUpdateToken = string.Empty;
        }
        if (requiresPortableSources) return store.Save(clone);
        try
        {
            snapshots.UpdateAll(clone);
            foreach (var mod in clone.Mods) mod.SourceModRoot = refreshSources[mod.Id];
            return store.Save(clone);
        }
        catch
        {
            snapshots.DeleteProject(clone.Id);
            throw;
        }
    }

    public void Delete(Guid id)
    {
        if (store.Get(id) is null) return;
        store.Delete(id);
        snapshots.DeleteProject(id);
        SafeFileTree.DeleteScopedDirectory(paths.BuildsRoot, paths.BuildRoot(id));
        SafeFileTree.DeleteScopedDirectory(paths.AssetsRoot, paths.ProjectAssetsRoot(id));
        if (File.Exists(paths.ProjectLockFile(id))) File.Delete(paths.ProjectLockFile(id));
    }

    private static void NormalizeOrder(PackageProject project)
    {
        var ordered = project.Mods.OrderBy(x => x.Order).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        project.Mods = ordered;
    }

    private static int ResolveTargetIndex(IReadOnlyList<PackageModReference> ordered, Guid? targetModReferenceId)
    {
        if (targetModReferenceId is null)
            throw new InvalidOperationException("Choisissez un mod de référence.");
        var targetIndex = ordered.ToList().FindIndex(x => x.Id == targetModReferenceId.Value);
        return targetIndex >= 0
            ? targetIndex
            : throw new InvalidOperationException("Le mod de référence est introuvable.");
    }
}

public enum ModPlacement
{
    First,
    Last,
    Before,
    After
}
