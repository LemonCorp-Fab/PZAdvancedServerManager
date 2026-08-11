using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Transfer;

public enum PackTransferContentMode
{
    Complete,
    ConfigurationOnly
}

public sealed class PackTransferService(ApplicationPaths paths, PackageProjectStore store)
{
    public const long MaximumUniqueArchiveBytes = 64L * 1024 * 1024 * 1024;
    public const long MaximumRestoredBytes = 128L * 1024 * 1024 * 1024;
    public const int MaximumFiles = 500_000;
    private const string FormatName = "PZASM-PACK";
    private const int FormatVersion = 2;
    private const string PortablePrefix = "pzasm-transfer://";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public PackTransferExportResult Export(Guid projectId, PackTransferContentMode contentMode, string? destinationPath = null)
    {
        var sourceProject = store.Get(projectId) ?? throw new KeyNotFoundException("Pack not found.");
        var project = Clone(sourceProject);
        Directory.CreateDirectory(paths.TransfersRoot);
        var finalPath = string.IsNullOrWhiteSpace(destinationPath) ? null : Path.GetFullPath(destinationPath);
        var outputRoot = finalPath is null ? paths.TransfersRoot : Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(outputRoot);
        var archivePath = finalPath is null
            ? Path.Combine(paths.TransfersRoot, $"pack-export-{project.Id:N}-{Guid.NewGuid():N}.pzasm-pack")
            : finalPath + $".pzasm-export-{Guid.NewGuid():N}.tmp";
        var manifest = new PackTransferManifest
        {
            Format = FormatName,
            Version = FormatVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            ProjectId = project.Id,
            ContentMode = contentMode,
            Project = project
        };
        var logicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blobSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long uniqueBytes = 0;
        long restoredBytes = 0;

        try
        {
            using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                void AddFile(string file, string logicalPath)
                {
                    logicalPath = NormalizeLogicalPath(logicalPath);
                    if (!logicalPaths.Add(logicalPath)) throw new InvalidDataException($"Duplicate portable path: {logicalPath}.");
                    if (manifest.Files.Count >= MaximumFiles) throw new InvalidDataException($"A pack transfer cannot contain more than {MaximumFiles:N0} files.");
                    RejectLink(file);
                    var info = new FileInfo(file);
                    if (info.Length < 0 || restoredBytes + info.Length > MaximumRestoredBytes)
                        throw new InvalidDataException("The restored pack would exceed the 128 GiB safety limit.");
                    restoredBytes += info.Length;
                    var hash = ComputeHash(file);
                    manifest.Files.Add(new PortableFileRecord
                    {
                        Path = logicalPath,
                        Blob = hash,
                        Length = info.Length,
                        LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                        ReadOnly = info.IsReadOnly,
                        UnixMode = OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(file)
                    });
                    if (blobSources.ContainsKey(hash)) return;
                    if (uniqueBytes + info.Length > MaximumUniqueArchiveBytes)
                        throw new InvalidDataException("The transfer contains more than 64 GiB of unique file data.");
                    EnsureFreeSpace(outputRoot, info.Length + 256L * 1024 * 1024, "export the next unique pack file");
                    uniqueBytes += info.Length;
                    blobSources.Add(hash, file);
                    var entry = archive.CreateEntry($"blobs/{hash}", CompressionLevel.Fastest);
                    entry.LastWriteTime = ClampZipTime(info.LastWriteTimeUtc);
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                    using var output = entry.Open();
                    input.CopyTo(output, 1024 * 1024);
                }

                void AddTree(string root, string area)
                {
                    if (!Directory.Exists(root)) return;
                    RejectLink(root);
                    manifest.Directories.Add(NormalizeLogicalPath(area));
                    foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                    {
                        RejectLink(directory);
                        manifest.Directories.Add(NormalizeLogicalPath(Path.Combine(area, Path.GetRelativePath(root, directory))));
                    }
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        AddFile(file, Path.Combine(area, Path.GetRelativePath(root, file)));
                }

                if (contentMode == PackTransferContentMode.Complete)
                {
                    var projectSources = paths.ProjectSourcesRoot(project.Id);
                    AddTree(projectSources, "sources");
                    foreach (var mod in project.Mods)
                    {
                        var source = Directory.Exists(mod.PinnedSourceRoot)
                            ? mod.PinnedSourceRoot
                            : Directory.Exists(mod.SourceModRoot) ? mod.SourceModRoot : string.Empty;
                        if (string.IsNullOrWhiteSpace(source))
                            throw new DirectoryNotFoundException($"The frozen source for '{mod.Name}' ({mod.ModId}) is unavailable. Refresh or repair this mod before exporting a complete pack.");
                        if (TryRelative(projectSources, source, out var relative))
                        {
                            mod.PinnedSourceRoot = Portable("sources", relative);
                        }
                        else
                        {
                            var folder = SafeSegment(string.IsNullOrWhiteSpace(mod.SourceFolderName) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(source)) : mod.SourceFolderName);
                            var area = $"sources-external/{mod.Id:N}/{folder}";
                            AddTree(source, area);
                            mod.PinnedSourceRoot = Portable(area);
                        }
                        mod.SourceModRoot = mod.PinnedSourceRoot;
                    }
                    if (project.LastBuiltAt is not null && !Directory.Exists(paths.BuildRoot(project.Id)))
                        throw new DirectoryNotFoundException("The project records a completed build, but its build directory is unavailable. Rebuild the pack before exporting a complete transfer.");
                    AddTree(paths.BuildRoot(project.Id), "build");
                    project.PortableSourcesRequired = false;
                }
                else
                {
                    var localMods = project.Mods.Where(mod => mod.WorkshopId == 0).Select(mod => mod.Name).ToArray();
                    if (localMods.Length > 0)
                        throw new InvalidOperationException("A configuration-only transfer cannot recreate local mods without Workshop IDs. Use a complete transfer for: " + string.Join(", ", localMods) + ".");
                    foreach (var mod in project.Mods)
                    {
                        mod.SourceModRoot = string.Empty;
                        mod.PinnedSourceRoot = string.Empty;
                        mod.PinnedAt = null;
                        mod.PinnedContentHash = string.Empty;
                        mod.PinnedMetadataStamp = string.Empty;
                        mod.ValidatedContentHash = string.Empty;
                        mod.SourceUpdateToken = string.Empty;
                    }
                    project.LastBuiltAt = null;
                    project.PortableSourcesRequired = project.Mods.Any(mod => mod.Enabled);
                }

                AddTree(paths.ProjectAssetsRoot(project.Id), "assets");

                if (!string.IsNullOrWhiteSpace(project.PreviewImagePath) && !File.Exists(project.PreviewImagePath))
                    throw new FileNotFoundException("The custom Workshop preview is unavailable and cannot be included in a complete transfer.", project.PreviewImagePath);
                if (!string.IsNullOrWhiteSpace(project.PreviewImagePath))
                {
                    var relative = $"preview/{SafeSegment(Path.GetFileName(project.PreviewImagePath))}";
                    AddFile(project.PreviewImagePath, relative);
                    project.PreviewImagePath = Portable(relative);
                }
                else project.PreviewImagePath = null;

                foreach (var mod in project.Mods)
                {
                    var attachment = mod.Permission.PrivateAttachmentPath;
                    if (string.IsNullOrWhiteSpace(attachment))
                    {
                        mod.Permission.PrivateAttachmentPath = string.Empty;
                        continue;
                    }
                    if (!File.Exists(attachment)) throw new FileNotFoundException($"The private permission attachment for '{mod.Name}' is unavailable.", attachment);
                    var relative = $"attachments/{mod.Id:N}/{SafeSegment(Path.GetFileName(attachment))}";
                    AddFile(attachment, relative);
                    mod.Permission.PrivateAttachmentPath = Portable(relative);
                }

                if (!string.IsNullOrWhiteSpace(project.Automation.SteamCmdPath))
                    project.Automation.SteamCmdPath = Portable("manager-tools", "steamcmd");
                project.Automation.SteamSessionVerifiedAt = null;

                manifest.Directories = manifest.Directories.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
                manifest.Files = manifest.Files.OrderBy(item => item.Path, StringComparer.Ordinal).ToList();
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                using var manifestOutput = manifestEntry.Open();
                JsonSerializer.Serialize(manifestOutput, manifest, JsonOptions);
            }
            if (finalPath is not null) File.Move(archivePath, finalPath, true);
            return new PackTransferExportResult(
                finalPath ?? archivePath,
                SafeSegment(project.Name) + ".pzasm-pack",
                manifest.Files.Count,
                restoredBytes,
                blobSources.Count,
                uniqueBytes,
                contentMode);
        }
        catch
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            throw;
        }
    }

    public PackTransferImportResult Import(Stream input, bool replaceExisting)
    {
        Directory.CreateDirectory(paths.TransfersRoot);
        var transactionRoot = Path.Combine(paths.TransfersRoot, "pack-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionRoot);
        FileStream? lease = TransferWorkspaceLease.Acquire(transactionRoot);
        try
        {
            if (input.CanSeek)
            {
                input.Position = 0;
                return ImportArchive(input, transactionRoot, replaceExisting);
            }
            var archivePath = Path.Combine(transactionRoot, "incoming.pzasm-pack");
            using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan)) input.CopyTo(output, 1024 * 1024);
            using var buffered = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            return ImportArchive(buffered, transactionRoot, replaceExisting);
        }
        finally
        {
            lease.Dispose();
            TryDeleteDirectory(transactionRoot);
        }
    }

    public PackTransferImportResult ImportFile(string archivePath, bool replaceExisting)
    {
        Directory.CreateDirectory(paths.TransfersRoot);
        var transactionRoot = Path.Combine(paths.TransfersRoot, "pack-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionRoot);
        FileStream? lease = TransferWorkspaceLease.Acquire(transactionRoot);
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            return ImportArchive(stream, transactionRoot, replaceExisting);
        }
        finally
        {
            lease.Dispose();
            TryDeleteDirectory(transactionRoot);
        }
    }

    private PackTransferImportResult ImportArchive(Stream archiveStream, string transactionRoot, bool replaceExisting)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
        if (archive.Entries.Count > MaximumFiles + 1) throw new InvalidDataException("The transfer contains too many archive entries.");
        var manifestEntries = archive.Entries.Where(entry => entry.FullName.Equals("manifest.json", StringComparison.Ordinal)).ToArray();
        if (manifestEntries.Length != 1) throw new InvalidDataException("The pack transfer manifest is missing or duplicated.");
        if (manifestEntries[0].Length > 256L * 1024 * 1024) throw new InvalidDataException("The pack transfer manifest exceeds the 256 MiB safety limit.");
        PackTransferManifest manifest;
        using (var manifestStream = manifestEntries[0].Open())
            manifest = JsonSerializer.Deserialize<PackTransferManifest>(manifestStream, JsonOptions) ?? throw new InvalidDataException("The pack transfer manifest is invalid.");
        ValidateManifest(manifest);
        var project = manifest.Project;
        var existed = store.Get(project.Id) is not null;
        if (existed && !replaceExisting)
            throw new InvalidOperationException($"Pack '{project.Name}' ({project.Id}) already exists. Enable explicit replacement to import it.");

        var entries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("blobs/", StringComparison.Ordinal))
            .ToDictionary(entry => entry.FullName[6..], StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Any(entry => !entry.FullName.Equals("manifest.json", StringComparison.Ordinal) && !entry.FullName.StartsWith("blobs/", StringComparison.Ordinal)))
            throw new InvalidDataException("The transfer contains an unexpected archive entry.");

        var blobLengths = manifest.Files.GroupBy(file => file.Blob, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Length).Distinct().Single(), StringComparer.OrdinalIgnoreCase);
        if (blobLengths.Values.Sum() > MaximumUniqueArchiveBytes) throw new InvalidDataException("The transfer exceeds the 64 GiB unique-data limit.");
        if (entries.Count != blobLengths.Count || entries.Keys.Except(blobLengths.Keys, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("The transfer contains missing or unreferenced file blobs.");
        var hardLinksAvailable = SupportsHardLinks(transactionRoot);
        var materializationBytes = hardLinksAvailable
            ? manifest.Files
                .GroupBy(file => new { file.Blob, file.LastWriteUtcTicks, file.ReadOnly, file.UnixMode })
                .GroupBy(group => group.Key.Blob, StringComparer.OrdinalIgnoreCase)
                .Sum(groups => groups.Skip(1).Sum(group => group.First().Length))
            : manifest.Files.Sum(file => file.Length);
        EnsureFreeSpace(paths.TransfersRoot, blobLengths.Values.Sum() + materializationBytes + 256L * 1024 * 1024, "verify and reconstruct this pack");
        var blobsRoot = Path.Combine(transactionRoot, "blobs");
        var payloadRoot = Path.Combine(transactionRoot, "payload");
        Directory.CreateDirectory(blobsRoot);
        Directory.CreateDirectory(payloadRoot);

        foreach (var pair in blobLengths)
        {
            if (!entries.TryGetValue(pair.Key, out var entry)) throw new InvalidDataException($"Missing file blob {pair.Key}.");
            if (entry.Length != pair.Value) throw new InvalidDataException($"Invalid length for file blob {pair.Key}.");
            var destination = Path.Combine(blobsRoot, pair.Key);
            using (var source = entry.Open())
            using (var destinationStream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                source.CopyTo(destinationStream, 1024 * 1024);
            if (!ComputeHash(destination).Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum verification failed for file blob {pair.Key}.");
        }

        foreach (var directory in manifest.Directories)
            Directory.CreateDirectory(ResolvePayloadPath(payloadRoot, directory));
        foreach (var area in new[] { "sources", "sources-external", "assets", "build", "attachments", "preview" })
            Directory.CreateDirectory(Path.Combine(payloadRoot, area));

        var materialized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var primaryMetadataByBlob = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var target = ResolvePayloadPath(payloadRoot, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var metadataKey = $"{file.Blob}:{file.LastWriteUtcTicks}:{file.ReadOnly}:{file.UnixMode}";
            if (!materialized.TryGetValue(metadataKey, out var first))
            {
                var blob = Path.Combine(blobsRoot, file.Blob);
                var isPrimaryMetadata = !primaryMetadataByBlob.ContainsKey(file.Blob);
                if (isPrimaryMetadata) primaryMetadataByBlob.Add(file.Blob, metadataKey);
                if (!hardLinksAvailable || !isPrimaryMetadata || !SafeFileTree.TryCreateHardLink(target, blob)) File.Copy(blob, target, false);
                materialized.Add(metadataKey, target);
            }
            else if (!SafeFileTree.TryCreateHardLink(target, first)) File.Copy(first, target, false);
            File.SetLastWriteTimeUtc(target, new DateTime(file.LastWriteUtcTicks, DateTimeKind.Utc));
            if (!OperatingSystem.IsWindows() && file.UnixMode is int unixMode)
                File.SetUnixFileMode(target, (UnixFileMode)unixMode);
        }
        foreach (var file in manifest.Files.Where(item => item.ReadOnly))
        {
            var target = ResolvePayloadPath(payloadRoot, file.Path);
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        }

        RebaseProject(project, manifest.ContentMode);
        RewriteBuild(payloadRoot, project);
        CommitProject(payloadRoot, project, replaceExisting);
        return new PackTransferImportResult(project, manifest.Files.Count, manifest.Files.Sum(item => item.Length), manifest.Files.Select(item => item.Blob).Distinct(StringComparer.OrdinalIgnoreCase).Count(), existed, manifest.ContentMode);
    }

    private void RebaseProject(PackageProject project, PackTransferContentMode contentMode)
    {
        foreach (var mod in project.Mods)
        {
            mod.PinnedSourceRoot = ResolvePortableProjectPath(project.Id, mod.PinnedSourceRoot);
            mod.SourceModRoot = mod.PinnedSourceRoot;
            mod.Permission.PrivateAttachmentPath = ResolvePortableProjectPath(project.Id, mod.Permission.PrivateAttachmentPath);
        }
        project.PortableSourcesRequired = contentMode == PackTransferContentMode.ConfigurationOnly && project.Mods.Any(mod => mod.Enabled);
        project.PreviewImagePath = string.IsNullOrWhiteSpace(project.PreviewImagePath) ? null : ResolvePortableProjectPath(project.Id, project.PreviewImagePath);
        if (!string.IsNullOrWhiteSpace(project.Automation.SteamCmdPath))
        {
            if (!project.Automation.SteamCmdPath.Equals(Portable("manager-tools", "steamcmd"), StringComparison.Ordinal))
                throw new InvalidDataException("The imported project contains an unsupported SteamCMD path.");
            project.Automation.SteamCmdPath = paths.SteamCmdExecutable;
        }
    }

    private string ResolvePortableProjectPath(Guid projectId, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (!value.StartsWith(PortablePrefix, StringComparison.Ordinal)) throw new InvalidDataException("The imported project contains a non-portable local path.");
        var relative = NormalizeLogicalPath(Uri.UnescapeDataString(value[PortablePrefix.Length..]));
        if (relative.Equals("sources", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("sources/", StringComparison.OrdinalIgnoreCase))
            return ResolveFinalPath(paths.ProjectSourcesRoot(projectId), relative["sources".Length..].TrimStart('/'));
        if (relative.Equals("sources-external", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("sources-external/", StringComparison.OrdinalIgnoreCase))
            return ResolveFinalPath(paths.ProjectSourcesRoot(projectId), relative);
        if (relative.Equals("assets", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            return ResolveFinalPath(paths.ProjectAssetsRoot(projectId), relative["assets".Length..].TrimStart('/'));
        if (relative.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("preview/", StringComparison.OrdinalIgnoreCase))
            return ResolveFinalPath(paths.ProjectAssetsRoot(projectId), "transfer/" + relative);
        throw new InvalidDataException($"Unsupported portable project path: {relative}.");
    }

    private void RewriteBuild(string payloadRoot, PackageProject project)
    {
        var stagedBuild = Path.Combine(payloadRoot, "build");
        if (!Directory.Exists(stagedBuild) || !Directory.EnumerateFileSystemEntries(stagedBuild).Any()) return;
        var finalBuild = paths.BuildRoot(project.Id);
        var previewName = Directory.EnumerateFiles(stagedBuild, "preview.*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).FirstOrDefault();
        foreach (var name in new[] { "steamcmd-item.vdf", "steamcmd-publish.vdf" })
        {
            var file = Path.Combine(stagedBuild, name);
            if (!File.Exists(file)) continue;
            var content = File.ReadAllText(file);
            content = ReplaceVdfPath(content, "contentfolder", Path.Combine(finalBuild, "Contents"));
            if (!string.IsNullOrWhiteSpace(previewName)) content = ReplaceVdfPath(content, "previewfile", Path.Combine(finalBuild, previewName));
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            File.WriteAllText(file, content, new UTF8Encoding(false));
        }
        var snapshot = Path.Combine(stagedBuild, "project.snapshot.json");
        if (File.Exists(snapshot)) File.SetAttributes(snapshot, File.GetAttributes(snapshot) & ~FileAttributes.ReadOnly);
        File.WriteAllText(snapshot, JsonSerializer.Serialize(new
        {
            warning = "Local project copy. It may contain private local evidence paths and is never published by the Workshop VDF.",
            project
        }, JsonOptions), new UTF8Encoding(false));
    }

    private void CommitProject(string payloadRoot, PackageProject project, bool replaceExisting)
    {
        var backupRoot = Path.Combine(paths.TransfersRoot, "pack-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        var directories = new[]
        {
            (Staged: Path.Combine(payloadRoot, "sources"), Destination: paths.ProjectSourcesRoot(project.Id), Name: "sources"),
            (Staged: Path.Combine(payloadRoot, "assets"), Destination: paths.ProjectAssetsRoot(project.Id), Name: "assets"),
            (Staged: Path.Combine(payloadRoot, "build"), Destination: paths.BuildRoot(project.Id), Name: "build")
        };
        MergeTree(Path.Combine(payloadRoot, "sources-external"), Path.Combine(payloadRoot, "sources", "sources-external"));
        MergeTree(Path.Combine(payloadRoot, "attachments"), Path.Combine(payloadRoot, "assets", "transfer", "attachments"));
        MergeTree(Path.Combine(payloadRoot, "preview"), Path.Combine(payloadRoot, "assets", "transfer", "preview"));
        var movedNew = new List<string>();
        var movedOld = new List<(string Backup, string Destination)>();
        var projectFile = paths.ProjectFile(project.Id);
        var projectBackup = Path.Combine(backupRoot, "project.json");
        try
        {
            foreach (var item in directories)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                if (Directory.Exists(item.Destination))
                {
                    if (!replaceExisting) throw new InvalidOperationException($"Destination directory already exists: {item.Destination}.");
                    var backup = Path.Combine(backupRoot, item.Name);
                    Directory.Move(item.Destination, backup);
                    movedOld.Add((backup, item.Destination));
                }
                Directory.Move(item.Staged, item.Destination);
                movedNew.Add(item.Destination);
            }
            if (File.Exists(projectFile)) File.Move(projectFile, projectBackup);
            store.SaveImported(project);
            TryDeleteDirectory(backupRoot);
        }
        catch
        {
            if (File.Exists(projectFile)) File.Delete(projectFile);
            if (File.Exists(projectBackup)) File.Move(projectBackup, projectFile, true);
            foreach (var directory in movedNew.AsEnumerable().Reverse()) TryDeleteDirectory(directory);
            foreach (var item in movedOld.AsEnumerable().Reverse())
                if (Directory.Exists(item.Backup)) Directory.Move(item.Backup, item.Destination);
            TryDeleteDirectory(backupRoot);
            throw;
        }
    }

    private static void MergeTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target);
        }
        TryDeleteDirectory(source);
    }

    private static void ValidateManifest(PackTransferManifest manifest)
    {
        if (!manifest.Format.Equals(FormatName, StringComparison.Ordinal) || manifest.Version != FormatVersion)
            throw new InvalidDataException("Unsupported pack transfer format or version.");
        if (!Enum.IsDefined(manifest.ContentMode)) throw new InvalidDataException("Unsupported pack transfer content mode.");
        if (manifest.Project is null || manifest.ProjectId == Guid.Empty || manifest.Project.Id != manifest.ProjectId)
            throw new InvalidDataException("The transferred project identity is invalid.");
        if (manifest.Files.Count > MaximumFiles) throw new InvalidDataException("The transfer contains too many files.");
        if (manifest.Files.Sum(item => item.Length) > MaximumRestoredBytes) throw new InvalidDataException("The restored pack exceeds 128 GiB.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var normalized = NormalizeLogicalPath(file.Path);
            if (!paths.Add(normalized)) throw new InvalidDataException($"Duplicate path in transfer: {normalized}.");
            if (file.Length < 0 || file.LastWriteUtcTicks < DateTime.MinValue.Ticks || file.LastWriteUtcTicks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException($"Invalid metadata for transferred file: {normalized}.");
            if (!Regex.IsMatch(file.Blob, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException($"Invalid blob identifier for transferred file: {normalized}.");
        }
        foreach (var group in manifest.Files.GroupBy(file => file.Blob, StringComparer.OrdinalIgnoreCase))
            if (group.Select(file => file.Length).Distinct().Count() != 1) throw new InvalidDataException($"Conflicting lengths for blob {group.Key}.");
        foreach (var directory in manifest.Directories) NormalizeLogicalPath(directory);
        if (manifest.ContentMode == PackTransferContentMode.ConfigurationOnly)
        {
            if (manifest.Files.Any(file => file.Path.Equals("sources", StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith("sources/", StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith("sources-external/", StringComparison.OrdinalIgnoreCase) || file.Path.Equals("build", StringComparison.OrdinalIgnoreCase) || file.Path.StartsWith("build/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A configuration-only transfer contains forbidden mod or build payloads.");
            if (manifest.Project.Mods.Any(mod => mod.WorkshopId == 0 || !string.IsNullOrWhiteSpace(mod.SourceModRoot) || !string.IsNullOrWhiteSpace(mod.PinnedSourceRoot)) || manifest.Project.LastBuiltAt is not null)
                throw new InvalidDataException("A configuration-only transfer contains non-portable source state.");
        }
    }

    private static string ReplaceVdfPath(string content, string key, string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return Regex.Replace(content, $"(\"{Regex.Escape(key)}\"\\s+\")[^\"]*(\")", match => match.Groups[1].Value + escaped + match.Groups[2].Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ComputeHash(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool SupportsHardLinks(string root)
    {
        var source = Path.Combine(root, ".hardlink-probe-source");
        var link = Path.Combine(root, ".hardlink-probe-link");
        try
        {
            File.WriteAllBytes(source, [0]);
            return SafeFileTree.TryCreateHardLink(link, source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (File.Exists(source)) File.Delete(source);
        }
    }

    private static void EnsureFreeSpace(string root, long requiredBytes, string operation)
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(driveRoot)) throw new IOException("The transfer storage volume could not be resolved.");
        var available = new DriveInfo(driveRoot).AvailableFreeSpace;
        if (available < requiredBytes)
            throw new IOException($"Insufficient free disk space to {operation}. Required safety estimate: {FormatBytes(requiredBytes)}; available: {FormatBytes(available)}.");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
        _ => $"{bytes:N0} bytes"
    };

    private static PackageProject Clone(PackageProject project) =>
        JsonSerializer.Deserialize<PackageProject>(JsonSerializer.Serialize(project, JsonOptions), JsonOptions) ?? throw new InvalidDataException("The project could not be prepared for transfer.");

    private static string Portable(params string[] parts) => PortablePrefix + string.Join('/', parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => Uri.EscapeDataString(part.Replace('\\', '/')).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)));

    private static bool TryRelative(string root, string candidate, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidatePath = Path.GetFullPath(candidate);
        if (!candidatePath.StartsWith(rootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        relative = Path.GetRelativePath(root, candidate);
        return true;
    }

    private static string NormalizeLogicalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("A portable path is empty.");
        var normalized = value.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"Unsafe portable path: {value}.");
        return normalized;
    }

    private static string ResolvePayloadPath(string payloadRoot, string logicalPath)
    {
        var normalized = NormalizeLogicalPath(logicalPath);
        var root = Path.GetFullPath(payloadRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(payloadRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe portable path: {logicalPath}.");
        return resolved;
    }

    private static string ResolveFinalPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return Path.GetFullPath(root);
        var normalized = NormalizeLogicalPath(relative);
        var allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(allowed, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe destination path: {relative}.");
        return resolved;
    }

    private static string SafeSegment(string value)
    {
        var result = string.Concat(value.Where(character => !Path.GetInvalidFileNameChars().Contains(character) && character is not '/' and not '\\')).Trim();
        return string.IsNullOrWhiteSpace(result) || result is "." or ".." ? "pzasm-pack" : result;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Symbolic links and reparse points cannot be exported: {path}.");
    }

    private static DateTimeOffset ClampZipTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        if (utc.Year < 1980) utc = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (utc.Year > 2107) utc = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class PackTransferManifest
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public Guid ProjectId { get; set; }
        public PackTransferContentMode ContentMode { get; set; }
        public PackageProject Project { get; set; } = new();
        public List<string> Directories { get; set; } = [];
        public List<PortableFileRecord> Files { get; set; } = [];
    }

    private sealed class PortableFileRecord
    {
        public string Path { get; set; } = string.Empty;
        public string Blob { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public bool ReadOnly { get; set; }
        public int? UnixMode { get; set; }
    }
}

public sealed record PackTransferExportResult(string Path, string FileName, int Files, long RestoredBytes, int UniqueBlobs, long UniqueBytes, PackTransferContentMode ContentMode);
public sealed record PackTransferImportResult(PackageProject Project, int Files, long RestoredBytes, int UniqueBlobs, bool ReplacedExisting, PackTransferContentMode ContentMode);
