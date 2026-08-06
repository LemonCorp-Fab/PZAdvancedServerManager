using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;

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

    public static void DeleteScopedDirectory(string root, string target)
    {
        if (!Directory.Exists(target)) return;
        var resolved = ResolveScopedDirectory(root, target);
        Directory.Delete(resolved, true);
    }

    public static void ReplaceDirectory(string root, string staged, string destination)
    {
        var stagedPath = ResolveScopedDirectory(root, staged);
        var destinationPath = ResolveScopedDirectory(root, destination);
        if (!Directory.Exists(stagedPath)) throw new DirectoryNotFoundException($"Dossier préparé introuvable : {stagedPath}");

        var previousPath = ResolveScopedDirectory(root, destinationPath + ".previous");
        DeleteScopedDirectory(root, previousPath);
        var previousMoved = false;
        try
        {
            if (Directory.Exists(destinationPath))
            {
                Directory.Move(destinationPath, previousPath);
                previousMoved = true;
            }
            Directory.Move(stagedPath, destinationPath);
        }
        catch
        {
            if (!Directory.Exists(destinationPath) && previousMoved && Directory.Exists(previousPath))
                Directory.Move(previousPath, destinationPath);
            throw;
        }

        DeleteScopedDirectory(root, previousPath);
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
}
