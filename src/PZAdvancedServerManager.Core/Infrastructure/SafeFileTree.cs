using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PZAdvancedServerManager.Core.Infrastructure;

public static class SafeFileTree
{
    public static void CopyDirectory(string source, string destination, Action<string, string>? onFile = null)
    {
        var sourceRoot = Path.GetFullPath(source);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException($"Source introuvable : {sourceRoot}");
        RejectReparsePoint(sourceRoot);
        Directory.CreateDirectory(destination);

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceRoot, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(entry);

            var relative = Path.GetRelativePath(sourceRoot, entry);
            var target = ResolveChild(destination, relative);
            if (Directory.Exists(entry)) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry, target, true);
                onFile?.Invoke(entry, target);
            }
        }
    }

    public static void LinkOrCopyDirectory(
        string source,
        string destination,
        Action<string, string, bool>? onFile = null,
        Func<string, string, bool>? linkFactory = null)
    {
        var sourceRoot = Path.GetFullPath(source);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException($"Source introuvable : {sourceRoot}");
        RejectReparsePoint(sourceRoot);
        Directory.CreateDirectory(destination);

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceRoot, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(entry);
            var relative = Path.GetRelativePath(sourceRoot, entry);
            var target = ResolveChild(destination, relative);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var linked = linkFactory?.Invoke(target, entry) ?? TryCreateHardLink(target, entry);
            if (!linked) File.Copy(entry, target, true);
            onFile?.Invoke(entry, target, linked);
        }
    }

    public static string ComputeDirectoryHash(string root)
    {
        var resolvedRoot = Path.GetFullPath(root);
        if (!Directory.Exists(resolvedRoot)) throw new DirectoryNotFoundException($"Source introuvable : {resolvedRoot}");
        RejectReparsePoint(resolvedRoot);
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = new byte[8];
        foreach (var entry in Directory.EnumerateFileSystemEntries(resolvedRoot, "*", SearchOption.AllDirectories))
            RejectReparsePoint(entry);
        foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(x => Path.GetRelativePath(resolvedRoot, x), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(resolvedRoot, file).Replace('\\', '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(length, new FileInfo(file).Length);
            aggregate.AppendData(length);
            using var stream = File.OpenRead(file);
            aggregate.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeDirectoryMetadataStamp(string root)
    {
        var resolvedRoot = Path.GetFullPath(root);
        if (!Directory.Exists(resolvedRoot)) throw new DirectoryNotFoundException($"Source introuvable : {resolvedRoot}");
        RejectReparsePoint(resolvedRoot);
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var number = new byte[8];
        foreach (var entry in Directory.EnumerateFileSystemEntries(resolvedRoot, "*", SearchOption.AllDirectories))
            RejectReparsePoint(entry);
        foreach (var file in Directory.EnumerateFiles(resolvedRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(resolvedRoot, path), StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(resolvedRoot, file).Replace('\\', '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(number, info.Length);
            aggregate.AppendData(number);
            BinaryPrimitives.WriteInt64LittleEndian(number, info.LastWriteTimeUtc.Ticks);
            aggregate.AppendData(number);
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    public static void DeleteScopedDirectory(string root, string target)
    {
        if (!Directory.Exists(target)) return;
        var resolved = ResolveScopedDirectory(root, target);
        ClearReadOnlyAttributes(resolved);
        Directory.Delete(resolved, true);
    }

    public static void ReplaceDirectory(string root, string staged, string destination)
    {
        var stagedPath = ResolveScopedDirectory(root, staged);
        var destinationPath = ResolveScopedDirectory(root, destination);
        if (!Directory.Exists(stagedPath)) throw new DirectoryNotFoundException($"Dossier préparé introuvable : {stagedPath}");

        if (!Directory.Exists(destinationPath))
        {
            Directory.Move(stagedPath, destinationPath);
            return;
        }

        // Keep the stable build directory in place because Explorer and file pickers can hold it open on Windows.
        var previousPath = ResolveScopedDirectory(root, destinationPath + $".previous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(previousPath);
        var previousEntries = new List<(string Current, string Backup)>();
        var stagedEntries = new List<(string Current, string Destination)>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(destinationPath).ToArray())
            {
                var backup = Path.Combine(previousPath, Path.GetFileName(entry));
                MoveEntry(entry, backup);
                previousEntries.Add((entry, backup));
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(stagedPath).ToArray())
            {
                var destinationEntry = Path.Combine(destinationPath, Path.GetFileName(entry));
                MoveEntry(entry, destinationEntry);
                stagedEntries.Add((entry, destinationEntry));
            }
            Directory.Delete(stagedPath, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestoreMovedEntries(stagedPath, stagedEntries, previousEntries);
            TryDeleteScopedDirectory(root, previousPath);
            SynchronizeDirectory(stagedPath, destinationPath);
            return;
        }
        catch
        {
            RestoreMovedEntries(stagedPath, stagedEntries, previousEntries);
            TryDeleteScopedDirectory(root, previousPath);
            throw;
        }

        TryDeleteScopedDirectory(root, previousPath);
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static void RestoreMovedEntries(
        string stagedPath,
        IEnumerable<(string Current, string Destination)> stagedEntries,
        IEnumerable<(string Current, string Backup)> previousEntries)
    {
        Directory.CreateDirectory(stagedPath);
        foreach (var entry in stagedEntries.Reverse())
            if (File.Exists(entry.Destination) || Directory.Exists(entry.Destination)) MoveEntry(entry.Destination, entry.Current);
        foreach (var entry in previousEntries.Reverse())
            if (File.Exists(entry.Backup) || Directory.Exists(entry.Backup)) MoveEntry(entry.Backup, entry.Current);
    }

    private static void SynchronizeDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var desiredFiles = new HashSet<string>(comparison);
        var desiredDirectories = new HashSet<string>(comparison) { string.Empty };

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            desiredDirectories.Add(relative);
            Directory.CreateDirectory(ResolveChild(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            desiredFiles.Add(relative);
            var target = ResolveChild(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                var attributes = File.GetAttributes(target);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                File.Delete(target);
            }
            if (!TryCreateHardLink(target, file)) File.Copy(file, target, false);
        }

        foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).ToArray())
        {
            var relative = Path.GetRelativePath(destination, file);
            if (desiredFiles.Contains(relative)) continue;
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(destination, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length).ToArray())
        {
            var relative = Path.GetRelativePath(destination, directory);
            if (desiredDirectories.Contains(relative)) continue;
            try { Directory.Delete(directory, true); }
            catch (IOException) when (!Directory.EnumerateFileSystemEntries(directory).Any()) { }
            catch (UnauthorizedAccessException) when (!Directory.EnumerateFileSystemEntries(directory).Any()) { }
        }

        DeleteScopedDirectory(Path.GetDirectoryName(source)!, source);
    }

    private static void TryDeleteScopedDirectory(string root, string target)
    {
        try { DeleteScopedDirectory(root, target); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static string ResolveChild(string root, string relative)
    {
        var allowedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(allowedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException($"Chemin de source refusé : {relative}");
        return resolved;
    }

    private static string ResolveScopedDirectory(string root, string target)
    {
        var allowedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(target);
        if (!resolved.StartsWith(allowedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException($"Opération refusée hors du dossier autorisé : {resolved}");
        return resolved;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Lien symbolique ou point de jonction refusé dans une source de mod : {path}");
    }

    private static bool TryCreateHardLink(string destination, string source)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return CreateHardLinkWindows(destination, source, IntPtr.Zero);
            if (OperatingSystem.IsLinux()) return CreateHardLinkUnix(source, destination) == 0;
            return false;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingFileName, string fileName);
}
