using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Packaging;

public static class PackageProjectComposer
{
    public static int AddWithDependencies(PackageProject project, DiscoveredMod root, IReadOnlyList<DiscoveredMod> discovered)
    {
        var added = 0;
        var queue = new Queue<DiscoveredMod>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (project.Mods.Any(x => x.ModId.Equals(current.ModId, StringComparison.OrdinalIgnoreCase))) continue;
            project.Mods.Add(new PackageModReference
            {
                WorkshopId = current.WorkshopId,
                ModId = current.ModId,
                Name = current.Name,
                Author = current.Author,
                SourceModRoot = current.ModRoot,
                SelectedVersionFolder = current.SelectedVersionFolder,
                SourceUrl = current.WorkshopUrl,
                RequiredModIds = current.RequiredModIds,
                MapFolders = current.MapFolders,
                Order = project.Mods.Count
            });
            foreach (var map in current.MapFolders)
                if (!project.MapOrder.Contains(map, StringComparer.OrdinalIgnoreCase)) project.MapOrder.Add(map);
            added++;
            foreach (var required in current.RequiredModIds)
            {
                var dependency = discovered.FirstOrDefault(x => x.ModId.Equals(required, StringComparison.OrdinalIgnoreCase));
                if (dependency is not null) queue.Enqueue(dependency);
            }
        }
        return added;
    }
}
