using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Transfer;

public sealed class TransferWorkspaceCleaner(ApplicationPaths paths)
{
    private static readonly string[] DirectoryPrefixes = ["pack-import-", "pack-backup-", "server-import-", "server-key-backup-"];
    private static readonly string[] FilePrefixes = ["pack-export-", "server-connections-"];

    public TransferCleanupResult CleanupStale(TimeSpan minimumAge)
    {
        Directory.CreateDirectory(paths.TransfersRoot);
        var threshold = DateTime.UtcNow - minimumAge;
        var directories = 0;
        var files = 0;
        long bytes = 0;

        foreach (var directory in Directory.EnumerateDirectories(paths.TransfersRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!DirectoryPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))) continue;
            if (Directory.GetLastWriteTimeUtc(directory) > threshold) continue;
            if (TransferWorkspaceLease.IsActive(directory)) continue;
            var measured = Measure(directory);
            if (!TryDeleteDirectory(directory)) continue;
            bytes += measured;
            directories++;
        }
        foreach (var file in Directory.EnumerateFiles(paths.TransfersRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (!FilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))) continue;
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc > threshold) continue;
            var measured = info.Length;
            if (!TryDeleteFile(info)) continue;
            bytes += measured;
            files++;
        }
        return new TransferCleanupResult(directories, files, bytes);
    }

    private static long Measure(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
        }
        return total;
    }

    private static bool TryDeleteDirectory(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool TryDeleteFile(FileInfo info)
    {
        try
        {
            using (new FileStream(info.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            info.Refresh();
            info.IsReadOnly = false;
            info.Delete();
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

internal static class TransferWorkspaceLease
{
    private const string LeaseName = ".active";

    public static FileStream Acquire(string root)
    {
        Directory.CreateDirectory(root);
        var stream = new FileStream(Path.Combine(root, LeaseName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write($"{Environment.ProcessId}|{DateTimeOffset.UtcNow:O}");
        writer.Flush();
        stream.Flush(true);
        return stream;
    }

    public static bool IsActive(string root)
    {
        var lease = Path.Combine(root, LeaseName);
        if (!File.Exists(lease)) return false;
        try
        {
            using var stream = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}

public sealed record TransferCleanupResult(int Directories, int Files, long Bytes);
