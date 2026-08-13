using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Infrastructure;

public sealed class StorageMaintenanceService(ApplicationPaths paths, PackageProjectStore projects)
{
    private const long LogTrimThreshold = 64L * 1024 * 1024;
    private const long LogRetainedBytes = 8L * 1024 * 1024;

    public StorageMaintenanceResult Run(DateTime utcNow)
    {
        var result = new MutableResult();
        CleanupProjectTransactions(paths.BuildsRoot, utcNow - TimeSpan.FromHours(6), result);
        CleanupProjectTransactions(paths.SourcesRoot, utcNow - TimeSpan.FromHours(6), result);
        CleanupDirectories(paths.ToolsRoot, "steamcmd-stage-", utcNow - TimeSpan.FromHours(6), result);
        CleanupFiles(paths.ToolsRoot, "steamcmd-download-", utcNow - TimeSpan.FromHours(6), result);
        CleanupFiles(paths.ProjectsRoot, ".tmp", utcNow - TimeSpan.FromHours(6), result, suffixMatch: true);
        CleanupFiles(paths.ProfilesRoot, ".tmp", utcNow - TimeSpan.FromHours(6), result, suffixMatch: true);
        CleanupFiles(paths.ServerDataRoot, ".tmp", utcNow - TimeSpan.FromHours(6), result, suffixMatch: true, recursive: true);
        CleanupFiles(paths.AssetsRoot, "preview.upload.tmp", utcNow - TimeSpan.FromHours(6), result, suffixMatch: true, recursive: true);
        CleanupDirectoriesRecursive(paths.ServerDataRoot, ".restore-", utcNow - TimeSpan.FromHours(6), result);
        CleanupFiles(Path.Combine(paths.RuntimeHomeRoot, "launchers"), "pzasm-", utcNow - TimeSpan.FromHours(6), result);
        CleanupRedundantWorkshopCache(result);
        CleanupUnreferencedWorkshopCache(utcNow - TimeSpan.FromDays(7), result);
        TrimLogs(paths.LogsRoot, result);
        TrimLogs(Path.Combine(paths.SteamCmdRoot, "logs"), result);
        TrimLogs(Path.Combine(paths.RuntimeHomeRoot, "Steam", "logs"), result);
        return new StorageMaintenanceResult(result.Directories, result.Files, result.Bytes);
    }

    private void CleanupProjectTransactions(string root, DateTime threshold, MutableResult result)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            var separator = name.IndexOf('.');
            if (separator <= 0 || !(name.EndsWith(".next", StringComparison.Ordinal) || name.Contains(".previous-", StringComparison.Ordinal))) continue;
            if (!Guid.TryParseExact(name[..separator], "N", out var projectId) || IsProjectActive(projectId)) continue;
            if (Directory.GetLastWriteTimeUtc(directory) > threshold) continue;
            DeleteDirectory(directory, result);
        }
    }

    private bool IsProjectActive(Guid projectId)
    {
        var lockFile = paths.ProjectLockFile(projectId);
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void CleanupDirectories(string root, string prefix, DateTime threshold, MutableResult result)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, prefix + "*", SearchOption.TopDirectoryOnly))
        {
            if (Directory.GetLastWriteTimeUtc(directory) > threshold) continue;
            DeleteDirectory(directory, result);
        }
    }

    private static void CleanupDirectoriesRecursive(string root, string prefix, DateTime threshold, MutableResult result)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, prefix + "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.Exists(directory) || Directory.GetLastWriteTimeUtc(directory) > threshold) continue;
            DeleteDirectory(directory, result);
        }
    }

    private static void CleanupFiles(string root, string pattern, DateTime threshold, MutableResult result, bool suffixMatch = false, bool recursive = false)
    {
        if (!Directory.Exists(root)) return;
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(root, "*", option))
        {
            var name = Path.GetFileName(file);
            var matches = suffixMatch ? name.EndsWith(pattern, StringComparison.Ordinal) : name.StartsWith(pattern, StringComparison.Ordinal);
            if (!matches) continue;
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc > threshold) continue;
            DeleteFile(info, result);
        }
    }

    private void CleanupUnreferencedWorkshopCache(DateTime threshold, MutableResult result)
    {
        var referenced = projects.GetAll().SelectMany(project => project.Mods).Where(mod => mod.WorkshopId != 0).Select(mod => mod.WorkshopId).ToHashSet();
        foreach (var workshopRoot in paths.GetManagedSteamWorkshopRoots())
        {
            var contentRoot = Path.Combine(workshopRoot, "content", PzasmConstants.ProjectZomboidSteamAppId);
            if (!Directory.Exists(contentRoot)) continue;
            foreach (var directory in Directory.EnumerateDirectories(contentRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (!ulong.TryParse(Path.GetFileName(directory), out var workshopId) || referenced.Contains(workshopId)) continue;
                if (Directory.GetLastWriteTimeUtc(directory) > threshold) continue;
                DeleteDirectory(directory, result);
            }
        }
    }

    private void CleanupRedundantWorkshopCache(MutableResult result)
    {
        var allProjects = projects.GetAll();
        var references = allProjects
            .SelectMany(project => project.Mods
                .Where(mod => mod.WorkshopId != 0)
                .Select(mod => new { Project = project, Mod = mod }))
            .GroupBy(item => item.Mod.WorkshopId);
        var pruner = new SteamWorkshopCachePruner(paths);
        foreach (var group in references)
        {
            var projectsForItem = group.Select(item => item.Project).DistinctBy(project => project.Id).ToArray();
            if (projectsForItem.Any(project => IsProjectActive(project.Id))) continue;
            if (group.Any(item => !Directory.Exists(item.Mod.PinnedSourceRoot) ||
                                  string.IsNullOrWhiteSpace(item.Mod.PinnedContentHash) ||
                                  string.IsNullOrWhiteSpace(item.Mod.SourceUpdateToken))) continue;

            foreach (var project in projectsForItem)
            {
                var changed = false;
                foreach (var mod in project.Mods.Where(mod => mod.WorkshopId == group.Key && Directory.Exists(mod.PinnedSourceRoot)))
                {
                    if (mod.SourceModRoot.Equals(mod.PinnedSourceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                    mod.SourceModRoot = mod.PinnedSourceRoot;
                    changed = true;
                }
                if (changed) projects.SaveImported(project);
            }

            var cleanup = pruner.RemoveItems([group.Key]);
            result.Directories += cleanup.Directories;
            result.Bytes += cleanup.Bytes;
        }
    }

    private static void TrimLogs(string root, MutableResult result)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(file => Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase)
                                    || Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
                if (stream.Length <= LogTrimThreshold) continue;
                var originalLength = stream.Length;
                var sourceOffset = originalLength - LogRetainedBytes;
                var buffer = new byte[1024 * 1024];
                long destinationOffset = 0;
                while (sourceOffset < originalLength)
                {
                    stream.Position = sourceOffset;
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, originalLength - sourceOffset));
                    if (read == 0) break;
                    stream.Position = destinationOffset;
                    stream.Write(buffer, 0, read);
                    sourceOffset += read;
                    destinationOffset += read;
                }
                stream.SetLength(destinationOffset);
                stream.Flush(true);
                result.Files++;
                result.Bytes += originalLength - destinationOffset;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void DeleteDirectory(string directory, MutableResult result)
    {
        try
        {
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                bytes += info.Length;
                info.IsReadOnly = false;
            }
            Directory.Delete(directory, true);
            result.Directories++;
            result.Bytes += bytes;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteFile(FileInfo info, MutableResult result)
    {
        try
        {
            using (new FileStream(info.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            var bytes = info.Length;
            info.IsReadOnly = false;
            info.Delete();
            result.Files++;
            result.Bytes += bytes;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class MutableResult
    {
        public int Directories { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
    }
}

public sealed record StorageMaintenanceResult(int Directories, int Files, long Bytes);
