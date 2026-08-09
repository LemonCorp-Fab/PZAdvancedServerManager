using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Packaging;

public static class PackageProjectComposer
{
    public static PackageDependencyPlan PlanDependencies(PackageProject project, IEnumerable<DiscoveredMod> roots, IReadOnlyList<DiscoveredMod> discovered)
    {
        var available = discovered
            .GroupBy(mod => Normalize(mod.ModId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(mod => mod.SourceUpdatedAt).First(), StringComparer.OrdinalIgnoreCase);
        var included = project.Mods.Select(mod => Normalize(mod.ModId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rootIds = roots.Select(mod => Normalize(mod.ModId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<string, DiscoveredMod>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(DiscoveredMod mod)
        {
            if (!visited.Add(Normalize(mod.ModId))) return;
            foreach (var requiredValue in mod.RequiredModIds)
            {
                var required = Normalize(requiredValue);
                if (required.Length == 0 || included.Contains(required)) continue;
                if (!available.TryGetValue(required, out var dependency))
                {
                    unresolved.Add(required);
                    continue;
                }
                if (!rootIds.Contains(required)) dependencies.TryAdd(required, dependency);
                Visit(dependency);
            }
        }

        foreach (var root in roots) Visit(root);
        return new PackageDependencyPlan(dependencies.Values.ToArray(), unresolved.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static IReadOnlyList<PackageModReference> AddWithDependencies(
        PackageProject project,
        DiscoveredMod root,
        IReadOnlyList<DiscoveredMod> discovered,
        bool includeDependencies = true)
    {
        var added = new List<PackageModReference>();
        var available = discovered
            .GroupBy(mod => Normalize(mod.ModId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(mod => mod.SourceUpdatedAt).First(), StringComparer.OrdinalIgnoreCase);
        var included = project.Mods.Select(mod => Normalize(mod.ModId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(DiscoveredMod current)
        {
            var currentId = Normalize(current.ModId);
            if (included.Contains(currentId) || !visiting.Add(currentId)) return;
            if (includeDependencies)
            {
                foreach (var requiredValue in current.RequiredModIds)
                {
                    var required = Normalize(requiredValue);
                    if (available.TryGetValue(required, out var dependency)) Visit(dependency);
                }
            }

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
                LoadAfterModIds = current.LoadAfterModIds,
                LoadBeforeModIds = current.LoadBeforeModIds,
                IncompatibleModIds = current.IncompatibleModIds,
                MapFolders = current.MapFolders,
                Permission = new PermissionEvidence { RightsHolder = current.Author },
                Order = project.Mods.Count
            };
            project.Mods.Add(reference);
            included.Add(currentId);
            visiting.Remove(currentId);
            foreach (var map in current.MapFolders)
                if (!project.MapOrder.Contains(map, StringComparer.OrdinalIgnoreCase)) project.MapOrder.Add(map);
            added.Add(reference);
        }

        Visit(root);
        return added;
    }

    private static string Normalize(string value) => ModInfoParser.NormalizeDependencyId(value).ToLowerInvariant();
}

public sealed record PackageDependencyPlan(
    IReadOnlyList<DiscoveredMod> AvailableDependencies,
    IReadOnlyList<string> UnresolvedModIds);
