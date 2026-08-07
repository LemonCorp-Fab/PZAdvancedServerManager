using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Packaging;

public static class PackageProjectComposer
{
    public static IReadOnlyList<PackageModReference> AddWithDependencies(PackageProject project, DiscoveredMod root, IReadOnlyList<DiscoveredMod> discovered)
    {
        var added = new List<PackageModReference>();
        var queue = new Queue<DiscoveredMod>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (project.Mods.Any(x => x.ModId.Equals(current.ModId, StringComparison.OrdinalIgnoreCase))) continue;
            var reference = new PackageModReference
            {
                WorkshopId = current.WorkshopId,
                ModId = current.ModId,
                Name = current.Name,
                Author = current.Author,
                SourceModRoot = current.ModRoot,
                SourceFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(current.ModRoot)),
                Version = current.Version,
                SelectedVersionFolder = current.SelectedVersionFolder,
                SourceUrl = current.WorkshopUrl,
                RequiredModIds = current.RequiredModIds,
                MapFolders = current.MapFolders,
                Order = project.Mods.Count
            };
            project.Mods.Add(reference);
            foreach (var map in current.MapFolders)
                if (!project.MapOrder.Contains(map, StringComparer.OrdinalIgnoreCase)) project.MapOrder.Add(map);
            added.Add(reference);
            foreach (var required in current.RequiredModIds)
            {
                var dependency = discovered.FirstOrDefault(x => x.ModId.Equals(required, StringComparison.OrdinalIgnoreCase));
                if (dependency is not null) queue.Enqueue(dependency);
            }
        }
        return added;
    }
}
