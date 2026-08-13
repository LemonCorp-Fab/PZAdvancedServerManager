using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Infrastructure;

public sealed class SteamWorkshopCachePruner(ApplicationPaths paths)
{
    public SteamWorkshopCacheCleanupResult RemoveItems(IEnumerable<ulong> workshopIds)
    {
        var ids = workshopIds.Where(id => id != 0).Distinct().ToArray();
        var directories = 0;
        long bytes = 0;
        foreach (var workshopRoot in paths.GetManagedSteamWorkshopRoots())
        {
            var contentRoot = Path.Combine(workshopRoot, "content", PzasmConstants.ProjectZomboidSteamAppId);
            if (!Directory.Exists(contentRoot)) continue;
            foreach (var workshopId in ids)
            {
                var itemRoot = Path.Combine(contentRoot, workshopId.ToString());
                if (!Directory.Exists(itemRoot)) continue;
                try
                {
                    var itemBytes = Directory.EnumerateFiles(itemRoot, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
                    SafeFileTree.DeleteScopedDirectory(contentRoot, itemRoot);
                    bytes += itemBytes;
                    directories++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return new SteamWorkshopCacheCleanupResult(directories, bytes);
    }
}

public sealed record SteamWorkshopCacheCleanupResult(int Directories, long Bytes);
