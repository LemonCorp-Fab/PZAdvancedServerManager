using System.IO.Compression;
using System.Text;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Transfer;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class TransferBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-transfer-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CompletePackRoundTripPreservesIdentityContentAndPortablePaths()
    {
        var sourcePaths = new ApplicationPaths(Path.Combine(_root, "source"));
        var sourceStore = new PackageProjectStore(sourcePaths);
        var project = sourceStore.Create("Portable pack");
        var mod = new PackageModReference
        {
            Id = Guid.NewGuid(),
            WorkshopId = 123456789,
            ModId = "portable.mod",
            Name = "Portable Mod",
            Author = "Pack Author",
            Version = "4.2.1",
            Order = 7,
            RequiredModIds = ["base.library"],
            Permission = new PermissionEvidence { Status = PermissionStatus.ExplicitPermission, RightsHolder = "Pack Author" }
        };
        var sourceFolder = Path.Combine(sourcePaths.ModSourceRoot(project.Id, mod.Id), "PortableMod");
        Directory.CreateDirectory(Path.Combine(sourceFolder, "media", "lua", "client"));
        var sourceFile = Path.Combine(sourceFolder, "media", "lua", "client", "main.lua");
        File.WriteAllText(sourceFile, "return 'portable-content'", new UTF8Encoding(false));
        var timestamp = new DateTime(2025, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sourceFile, timestamp);
        mod.SourceFolderName = "PortableMod";
        mod.SourceModRoot = sourceFolder;
        mod.PinnedSourceRoot = sourceFolder;
        mod.PinnedContentHash = SafeFileTree.ComputeDirectoryHash(sourceFolder);
        mod.PinnedMetadataStamp = SafeFileTree.ComputeDirectoryMetadataStamp(sourceFolder);
        mod.SourceUpdateToken = "steam-workshop:123456789:7654321:1746428645";
        project.Mods.Add(mod);
        project.PublishedWorkshopId = 9988776655;
        project.MapOrder = ["PortableMap", "Muldraugh, KY"];
        project.ConflictWinners["lua:test"] = mod.ModId;
        project.AcknowledgedConflicts.Add("asset:expected");
        project.Automation.Enabled = true;
        project.Automation.DailyTimes = ["04:00", "16:00"];
        project.Automation.SteamUsername = "steam-owner";
        project.Automation.SteamCmdPath = sourcePaths.SteamCmdExecutable;
        project.Automation.SteamSessionVerifiedAt = new DateTimeOffset(2025, 5, 1, 2, 3, 4, TimeSpan.Zero);
        project.Automation.CoordinatedServerName = "production";
        project.Publication.ContentFingerprint = "content-state";
        project.Publication.RemoteContentHandle = "remote-handle";

        var preview = Path.Combine(_root, "custom-preview.png");
        File.WriteAllBytes(preview, [1, 2, 3, 4, 5]);
        project.PreviewImagePath = preview;
        var permission = Path.Combine(_root, "permission.txt");
        File.WriteAllText(permission, "permission evidence", new UTF8Encoding(false));
        mod.Permission.PrivateAttachmentPath = permission;

        var buildRoot = sourcePaths.BuildRoot(project.Id);
        var builtFile = Path.Combine(buildRoot, "Contents", "mods", "PortableMod", "media", "lua", "client", "main.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(builtFile)!);
        File.Copy(sourceFile, builtFile);
        File.SetLastWriteTimeUtc(builtFile, timestamp);
        File.WriteAllText(Path.Combine(buildRoot, "steamcmd-item.vdf"), $"\"contentfolder\" \"{Path.Combine(buildRoot, "Contents").Replace("\\", "\\\\")}\"\n\"previewfile\" \"{Path.Combine(buildRoot, "preview.png").Replace("\\", "\\\\")}\"");
        File.WriteAllBytes(Path.Combine(buildRoot, "preview.png"), [1, 2, 3, 4, 5]);
        File.WriteAllText(Path.Combine(buildRoot, "project.snapshot.json"), "old local snapshot");
        project.LastBuiltAt = new DateTimeOffset(2025, 5, 6, 7, 8, 9, TimeSpan.Zero);
        project.UpdatedAt = new DateTimeOffset(2025, 5, 7, 8, 9, 10, TimeSpan.Zero);
        sourceStore.SaveImported(project);
        var originalUpdatedAt = project.UpdatedAt;

        var exported = new PackTransferService(sourcePaths, sourceStore).Export(project.Id, PackTransferContentMode.Complete);
        Assert.True(exported.UniqueBlobs < exported.Files);

        var destinationPaths = new ApplicationPaths(Path.Combine(_root, "destination"));
        var destinationStore = new PackageProjectStore(destinationPaths);
        var imported = new PackTransferService(destinationPaths, destinationStore).ImportFile(exported.Path, replaceExisting: false);
        var reopened = destinationStore.Get(project.Id)!;

        Assert.Equal(project.Id, reopened.Id);
        Assert.Equal(project.StableSuffix, reopened.StableSuffix);
        Assert.Equal(9988776655UL, reopened.PublishedWorkshopId);
        Assert.Equal(originalUpdatedAt, reopened.UpdatedAt);
        Assert.Equal(project.LastBuiltAt, reopened.LastBuiltAt);
        Assert.Equal(project.MapOrder, reopened.MapOrder);
        Assert.Equal("content-state", reopened.Publication.ContentFingerprint);
        Assert.Equal("remote-handle", reopened.Publication.RemoteContentHandle);
        Assert.Equal(["04:00", "16:00"], reopened.Automation.DailyTimes);
        Assert.Equal("production", reopened.Automation.CoordinatedServerName);
        Assert.Equal(destinationPaths.SteamCmdExecutable, reopened.Automation.SteamCmdPath);
        Assert.Null(reopened.Automation.SteamSessionVerifiedAt);
        Assert.StartsWith(destinationPaths.ProjectSourcesRoot(project.Id), reopened.Mods[0].PinnedSourceRoot, PathComparison);
        Assert.Equal(reopened.Mods[0].PinnedSourceRoot, reopened.Mods[0].SourceModRoot);
        Assert.Equal("steam-workshop:123456789:7654321:1746428645", reopened.Mods[0].SourceUpdateToken);
        Assert.StartsWith(destinationPaths.ProjectAssetsRoot(project.Id), reopened.PreviewImagePath!, PathComparison);
        Assert.StartsWith(destinationPaths.ProjectAssetsRoot(project.Id), reopened.Mods[0].Permission.PrivateAttachmentPath, PathComparison);
        Assert.Equal("return 'portable-content'", File.ReadAllText(Path.Combine(reopened.Mods[0].PinnedSourceRoot, "media", "lua", "client", "main.lua")));
        Assert.Equal("permission evidence", File.ReadAllText(reopened.Mods[0].Permission.PrivateAttachmentPath));
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(reopened.PreviewImagePath!));
        Assert.Contains(Path.Combine(destinationPaths.BuildRoot(project.Id), "Contents").Replace("\\", "\\\\"), File.ReadAllText(Path.Combine(destinationPaths.BuildRoot(project.Id), "steamcmd-item.vdf")));
        Assert.Contains(destinationPaths.ProjectSourcesRoot(project.Id).Replace("\\", "\\\\"), File.ReadAllText(Path.Combine(destinationPaths.BuildRoot(project.Id), "project.snapshot.json")));
        Assert.False(imported.ReplacedExisting);
        Assert.Throws<InvalidOperationException>(() => new PackTransferService(destinationPaths, destinationStore).ImportFile(exported.Path, replaceExisting: false));
        Assert.True(new PackTransferService(destinationPaths, destinationStore).ImportFile(exported.Path, replaceExisting: true).ReplacedExisting);
        Assert.Empty(Directory.EnumerateDirectories(destinationPaths.TransfersRoot));
        File.Delete(exported.Path);
    }

    [Fact]
    public void CorruptPackBlobIsRejectedWithoutCreatingAProject()
    {
        var sourcePaths = new ApplicationPaths(Path.Combine(_root, "corrupt-source"));
        var store = new PackageProjectStore(sourcePaths);
        var project = store.Create("Corruption test");
        var mod = CreateMinimalMod(sourcePaths, project, "corrupt.mod", "original");
        project.Mods.Add(mod);
        store.SaveImported(project);
        var exported = new PackTransferService(sourcePaths, store).Export(project.Id, PackTransferContentMode.Complete);

        using (var archive = ZipFile.Open(exported.Path, ZipArchiveMode.Update))
        {
            var blob = archive.Entries.First(entry => entry.FullName.StartsWith("blobs/", StringComparison.Ordinal));
            var name = blob.FullName;
            blob.Delete();
            using var replacement = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
            replacement.Write("modified"u8);
        }

        var destinationPaths = new ApplicationPaths(Path.Combine(_root, "corrupt-destination"));
        var destinationStore = new PackageProjectStore(destinationPaths);
        Assert.Throws<InvalidDataException>(() => new PackTransferService(destinationPaths, destinationStore).ImportFile(exported.Path, replaceExisting: false));
        Assert.Null(destinationStore.Get(project.Id));
        Assert.False(Directory.Exists(destinationPaths.ProjectSourcesRoot(project.Id)));
        File.Delete(exported.Path);
    }

    [Fact]
    public void ConfigurationOnlyPackKeepsIdentityWithoutCarryingModOrBuildContent()
    {
        var sourcePaths = new ApplicationPaths(Path.Combine(_root, "light-source"));
        var sourceStore = new PackageProjectStore(sourcePaths);
        var project = sourceStore.Create("Light transfer");
        var mod = CreateMinimalMod(sourcePaths, project, "light.mod", "name=Light");
        project.Mods.Add(mod);
        project.PublishedWorkshopId = 123456;
        project.LastBuiltAt = DateTimeOffset.UtcNow;
        var buildFile = Path.Combine(sourcePaths.BuildRoot(project.Id), "Contents", "mods", "Mod", "large.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(buildFile)!);
        File.WriteAllBytes(buildFile, new byte[1024 * 1024]);
        sourceStore.SaveImported(project);

        var exported = new PackTransferService(sourcePaths, sourceStore).Export(project.Id, PackTransferContentMode.ConfigurationOnly);
        Assert.Equal(PackTransferContentMode.ConfigurationOnly, exported.ContentMode);
        Assert.True(new FileInfo(exported.Path).Length < 100_000);
        using (var archive = ZipFile.OpenRead(exported.Path))
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("blobs/", StringComparison.Ordinal));

        var destinationPaths = new ApplicationPaths(Path.Combine(_root, "light-destination"));
        var destinationStore = new PackageProjectStore(destinationPaths);
        var imported = new PackTransferService(destinationPaths, destinationStore).ImportFile(exported.Path, replaceExisting: false);
        var reopened = imported.Project;
        Assert.Equal(project.Id, reopened.Id);
        Assert.Equal(project.PublishedWorkshopId, reopened.PublishedWorkshopId);
        Assert.True(reopened.PortableSourcesRequired);
        Assert.Null(reopened.LastBuiltAt);
        Assert.Empty(reopened.Mods[0].SourceModRoot);
        Assert.Empty(reopened.Mods[0].PinnedSourceRoot);
        Assert.False(Directory.EnumerateFiles(destinationPaths.ProjectSourcesRoot(project.Id), "*", SearchOption.AllDirectories).Any());
        Assert.False(Directory.EnumerateFiles(destinationPaths.BuildRoot(project.Id), "*", SearchOption.AllDirectories).Any());
        File.Delete(exported.Path);
    }

    [Fact]
    public void ConfigurationOnlyPackCanBeDuplicatedBeforeWorkshopHydration()
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "light-duplicate"));
        var store = new PackageProjectStore(paths);
        var project = store.Create("Imported configuration");
        project.PortableSourcesRequired = true;
        project.Mods.Add(new PackageModReference
        {
            WorkshopId = 123456,
            ModId = "portable.mod",
            Name = "Portable Mod",
            SourceModRoot = string.Empty,
            PinnedSourceRoot = string.Empty
        });
        store.SaveImported(project);

        var duplicate = new PackageProjectService(paths, store, new PackageSourceSnapshotService(paths)).Duplicate(project.Id);

        Assert.NotEqual(project.Id, duplicate.Id);
        Assert.True(duplicate.PortableSourcesRequired);
        Assert.Empty(duplicate.Mods[0].SourceModRoot);
        Assert.Empty(duplicate.Mods[0].PinnedSourceRoot);
        Assert.Equal(123456UL, duplicate.Mods[0].WorkshopId);
    }

    [Fact]
    public void EncryptedServerConnectionsRoundTripAcrossDifferentLocalKeys()
    {
        var sourcePaths = new ApplicationPaths(Path.Combine(_root, "server-source"));
        var sourceStore = new RemoteServerConnectionStore(sourcePaths, new StoredSecretProtector(sourcePaths, "source-local-encryption-key-with-thirty-two-characters"));
        var privateKey = Path.Combine(_root, "id_ed25519");
        File.WriteAllText(privateKey, "PRIVATE-KEY-MATERIAL", new UTF8Encoding(false));
        var connection = new RemoteServerConnection
        {
            Id = Guid.NewGuid(),
            Name = "production-pine",
            Provider = RemoteServerProvider.PineHosting,
            ApiBaseUrl = "https://panel.example.test",
            ApiToken = "pine-api-secret-value",
            ApiServerIdentifier = "server123",
            ProviderServerName = "Production",
            RconHost = "pz.example.test",
            RconPort = 27015,
            RconPassword = "rcon-secret-value",
            SshPrivateKeyPath = privateKey,
            UpdatedAt = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero)
        };
        sourceStore.Import([connection], replaceExisting: false);
        var service = new ServerConnectionTransferService(sourcePaths, sourceStore);
        var exported = service.Export("portable-password-123", includePrivateKeys: true);
        var encryptedText = File.ReadAllText(exported.Path);
        Assert.DoesNotContain("pine-api-secret-value", encryptedText);
        Assert.DoesNotContain("rcon-secret-value", encryptedText);
        Assert.DoesNotContain("PRIVATE-KEY-MATERIAL", encryptedText);

        var destinationPaths = new ApplicationPaths(Path.Combine(_root, "server-destination"));
        var destinationStore = new RemoteServerConnectionStore(destinationPaths, new StoredSecretProtector(destinationPaths, "destination-local-encryption-key-with-thirty-two-characters"));
        var destinationService = new ServerConnectionTransferService(destinationPaths, destinationStore);
        Assert.Throws<InvalidDataException>(() => destinationService.ImportFile(exported.Path, "wrong-password-123", replaceExisting: false));
        var result = destinationService.ImportFile(exported.Path, "portable-password-123", replaceExisting: false);
        var reopened = destinationStore.Get("production-pine")!;
        Assert.Equal(connection.Id, reopened.Id);
        Assert.Equal(connection.UpdatedAt, reopened.UpdatedAt);
        Assert.Equal("pine-api-secret-value", reopened.ApiToken);
        Assert.Equal("rcon-secret-value", reopened.RconPassword);
        Assert.StartsWith(destinationPaths.ImportedServerKeysRoot, reopened.SshPrivateKeyPath, PathComparison);
        Assert.Equal("PRIVATE-KEY-MATERIAL", File.ReadAllText(reopened.SshPrivateKeyPath));
        Assert.Equal(1, result.Connections);
        Assert.Equal(1, result.PrivateKeys);
        var storedJson = File.ReadAllText(destinationPaths.RemoteServersFile);
        Assert.DoesNotContain("pine-api-secret-value", storedJson);
        Assert.DoesNotContain("rcon-secret-value", storedJson);
        Assert.Throws<InvalidOperationException>(() => destinationService.ImportFile(exported.Path, "portable-password-123", replaceExisting: false));
        Assert.Equal(1, destinationService.ImportFile(exported.Path, "portable-password-123", replaceExisting: true).ReplacedConnections);
        File.Delete(exported.Path);
    }

    [Fact]
    public void StaleTransferCleanupOnlyRemovesOwnedTemporaryArtifacts()
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "cleanup"));
        var staleDirectory = Path.Combine(paths.TransfersRoot, "pack-import-old");
        var unrelatedDirectory = Path.Combine(paths.TransfersRoot, "keep-me");
        Directory.CreateDirectory(staleDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        File.WriteAllBytes(Path.Combine(staleDirectory, "large.tmp"), new byte[4096]);
        File.WriteAllText(Path.Combine(unrelatedDirectory, "user.txt"), "keep");
        var staleFile = Path.Combine(paths.TransfersRoot, "pack-export-old.pzasm-pack");
        File.WriteAllBytes(staleFile, new byte[1024]);
        Directory.SetLastWriteTimeUtc(staleDirectory, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddDays(-2));

        var result = new TransferWorkspaceCleaner(paths).CleanupStale(TimeSpan.FromHours(6));

        Assert.False(Directory.Exists(staleDirectory));
        Assert.False(File.Exists(staleFile));
        Assert.True(File.Exists(Path.Combine(unrelatedDirectory, "user.txt")));
        Assert.Equal(1, result.Directories);
        Assert.Equal(1, result.Files);
        Assert.True(result.Bytes >= 5120);
    }

    [Fact]
    public void StorageMaintenanceRemovesOnlyStaleDisposableAndUnreferencedCacheData()
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "storage-maintenance"));
        var store = new PackageProjectStore(paths);
        var project = store.Create("Referenced cache");
        project.Mods.Add(new PackageModReference { WorkshopId = 42, ModId = "kept.mod", Name = "Kept" });
        store.SaveImported(project);
        var referenced = Path.Combine(paths.SteamCmdRoot, "steamapps", "workshop", "content", "108600", "42");
        var unreferenced = Path.Combine(paths.SteamCmdRoot, "steamapps", "workshop", "content", "108600", "99");
        Directory.CreateDirectory(referenced);
        Directory.CreateDirectory(unreferenced);
        File.WriteAllText(Path.Combine(referenced, "kept.bin"), "keep");
        File.WriteAllText(Path.Combine(unreferenced, "stale.bin"), "remove");
        Directory.SetLastWriteTimeUtc(referenced, DateTime.UtcNow.AddDays(-10));
        Directory.SetLastWriteTimeUtc(unreferenced, DateTime.UtcNow.AddDays(-10));
        var staleBuild = paths.BuildRoot(project.Id) + ".next";
        Directory.CreateDirectory(staleBuild);
        File.WriteAllText(Path.Combine(staleBuild, "partial.bin"), "remove");
        Directory.SetLastWriteTimeUtc(staleBuild, DateTime.UtcNow.AddDays(-1));
        var stableBuild = paths.BuildRoot(project.Id);
        Directory.CreateDirectory(stableBuild);
        File.WriteAllText(Path.Combine(stableBuild, "current.bin"), "keep");

        var result = new StorageMaintenanceService(paths, store).Run(DateTime.UtcNow);

        Assert.True(Directory.Exists(referenced));
        Assert.False(Directory.Exists(unreferenced));
        Assert.False(Directory.Exists(staleBuild));
        Assert.True(File.Exists(Path.Combine(stableBuild, "current.bin")));
        Assert.Equal(2, result.Directories);
    }

    [Fact]
    public void StorageMaintenanceRemovesRedundantManagedCacheWhenPinnedSnapshotsAreComplete()
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "redundant-workshop-cache"));
        var store = new PackageProjectStore(paths);
        var project = store.Create("Imported complete pack");
        var snapshot = Path.Combine(paths.ModSourceRoot(project.Id, Guid.NewGuid()), "PortableMod");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "mod.info"), "id=portable.mod");
        project.Mods.Add(new PackageModReference
        {
            WorkshopId = 42,
            ModId = "portable.mod",
            Name = "Portable Mod",
            SourceModRoot = Path.Combine(paths.RuntimeHomeRoot, "Steam", "steamapps", "workshop", "content", "108600", "42", "mods", "PortableMod"),
            PinnedSourceRoot = snapshot,
            PinnedContentHash = "verified-hash",
            SourceUpdateToken = "steam-workshop:42:123456:1700000000"
        });
        store.SaveImported(project);
        var cache = Path.Combine(paths.RuntimeHomeRoot, "Steam", "steamapps", "workshop", "content", "108600", "42");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "content.bin"), new byte[512]);

        var result = new StorageMaintenanceService(paths, store).Run(DateTime.UtcNow);
        var reopened = store.Get(project.Id)!;

        Assert.False(Directory.Exists(cache));
        Assert.Equal(snapshot, reopened.Mods[0].SourceModRoot);
        Assert.Equal(1, result.Directories);
        Assert.True(result.Bytes >= 512);
    }

    [Fact]
    public void StorageMaintenanceSkipsTransactionsForAnActiveProject()
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "active-maintenance"));
        var store = new PackageProjectStore(paths);
        var project = store.Create("Active project");
        var staleBuild = paths.BuildRoot(project.Id) + ".next";
        Directory.CreateDirectory(staleBuild);
        File.WriteAllText(Path.Combine(staleBuild, "partial.bin"), "keep while active");
        Directory.SetLastWriteTimeUtc(staleBuild, DateTime.UtcNow.AddDays(-1));

        using (new FileStream(paths.ProjectLockFile(project.Id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            new StorageMaintenanceService(paths, store).Run(DateTime.UtcNow);
            Assert.True(Directory.Exists(staleBuild));
        }

        new StorageMaintenanceService(paths, store).Run(DateTime.UtcNow);
        Assert.False(Directory.Exists(staleBuild));
    }

    private static PackageModReference CreateMinimalMod(ApplicationPaths paths, PackageProject project, string modId, string content)
    {
        var reference = new PackageModReference { Id = Guid.NewGuid(), WorkshopId = 42, ModId = modId, Name = modId, SourceFolderName = "Mod" };
        var source = Path.Combine(paths.ModSourceRoot(project.Id, reference.Id), "Mod");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "mod.info"), content, new UTF8Encoding(false));
        reference.SourceModRoot = source;
        reference.PinnedSourceRoot = source;
        reference.PinnedContentHash = SafeFileTree.ComputeDirectoryHash(source);
        reference.PinnedMetadataStamp = SafeFileTree.ComputeDirectoryMetadataStamp(source);
        return reference;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(_root, true);
    }
}
