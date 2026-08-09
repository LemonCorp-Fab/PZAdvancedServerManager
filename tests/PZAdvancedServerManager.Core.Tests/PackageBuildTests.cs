using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class PackageBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Bundle_Preserves_ModRoots_AndAddsNoticeAndControlModules()
    {
        var source = CreateMod("FirstFolder", "first-id", "return 'first'");
        var project = ValidProject(PackageMode.Bundle,
            new PackageModReference { ModId = "first-id", Name = "First Mod", Author = "Example Author", Version = "1.2.3", SourceModRoot = source, SelectedVersionFolder = "common", WorkshopId = 123, SourceUrl = "https://example.test/123", Permission = new() { Status = PermissionStatus.AuthorOwned } });
        project.PublishedWorkshopId = 999;
        project.InjectConnectionNotice = true;
        project.InjectInGameControl = true;

        var service = new PackageBuildService(new ApplicationPaths(Path.Combine(_root, "data")), new PackageValidator());
        var result = service.Build(project);

        Assert.True(File.Exists(Path.Combine(result.WorkshopContentRoot, "mods", "FirstFolder", "common", "media", "lua", "client", "test.lua")));
        Assert.True(File.Exists(Path.Combine(result.WorkshopContentRoot, "mods", project.NoticeModId, "common", "media", "lua", "client", "PZASM_PackNotice.lua")));
        var controlClientPath = Path.Combine(result.WorkshopContentRoot, "mods", project.ControlModId, "common", "media", "lua", "client", "PZASM_ControlClient.lua");
        var controlServerPath = Path.Combine(result.WorkshopContentRoot, "mods", project.ControlModId, "common", "media", "lua", "server", "PZASM_ControlServer.lua");
        Assert.True(File.Exists(controlClientPath));
        Assert.True(File.Exists(controlServerPath));
        var controlClient = File.ReadAllText(controlClientPath);
        Assert.Contains("getActivatedMods()", controlClient);
        Assert.Contains("ISScrollingListBox", controlClient);
        Assert.Contains("author = \"Example Author\"", controlClient);
        Assert.Contains("version = \"1.2.3\"", controlClient);
        Assert.Contains("localSource = false", controlClient);
        Assert.Contains("PZASMHudButton", controlClient);
        Assert.Contains("getScreenHeight() - 54", controlClient);
        Assert.Contains("pzasmSaveHudPosition", controlClient);
        Assert.DoesNotContain("getModInfoByID", controlClient);
        Assert.Contains("Keyboard.KEY_F8", controlClient);
        Assert.Contains("pzasmIsAdmin", controlClient);
        Assert.Contains("Events.OnClientCommand", File.ReadAllText(controlServerPath));
        var notice = File.ReadAllText(Path.Combine(result.WorkshopContentRoot, "mods", project.NoticeModId, "common", "media", "lua", "client", "PZASM_PackNotice.lua"));
        Assert.Contains("Version: 1.2.3", notice);
        var publicManifest = File.ReadAllText(Path.Combine(result.WorkshopContentRoot, "pzasm-pack-manifest.json"));
        Assert.Contains("first-id", publicManifest);
        Assert.DoesNotContain("privateAttachmentPath", publicManifest, StringComparison.OrdinalIgnoreCase);
        var config = File.ReadAllText(result.ServerConfigSnippetPath);
        Assert.Contains("WorkshopItems=999", config);
        Assert.Contains($"Mods=first-id;{project.NoticeModId};{project.ControlModId}", config);
        Assert.DoesNotContain("WorkshopItems=123", config);
        Assert.True(File.Exists(result.WorkshopPreviewPath));
        Assert.Equal(".png", WorkshopPreviewFile.Validate(result.WorkshopPreviewPath));
        Assert.InRange(new FileInfo(result.WorkshopPreviewPath).Length, 1, WorkshopPreviewFile.MaximumBytes);
    }

    [Fact]
    public void WorkshopPreviewValidatorRejectsNonImageContent()
    {
        var path = Path.Combine(_root, "not-an-image.png");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "not an image");
        Assert.Throws<InvalidDataException>(() => WorkshopPreviewFile.Validate(path));
    }

    [Fact]
    public void FirstPublication_UsesZeroThenReadsSteamAssignedWorkshopId()
    {
        var source = CreateMod("NewWorkshopItem", "new-workshop-item", "return true");
        var project = ValidProject(PackageMode.Bundle, Ref("new-workshop-item", "New Workshop Item", source));
        Assert.Equal(0UL, project.PublishedWorkshopId);

        var service = new PackageBuildService(new ApplicationPaths(Path.Combine(_root, "new-workshop-data")), new PackageValidator());
        var result = service.Build(project);
        var vdf = File.ReadAllText(result.SteamCmdVdfPath);
        Assert.Contains("\"publishedfileid\"   \"0\"", vdf);
        Assert.Contains(result.WorkshopContentRoot.Replace("\\", "\\\\", StringComparison.Ordinal), vdf);
        Assert.Contains(Path.Combine(result.BuildRoot, "preview.png").Replace("\\", "\\\\", StringComparison.Ordinal), vdf);
        Assert.DoesNotContain(".next", vdf, StringComparison.OrdinalIgnoreCase);
        SteamCmdService.ValidatePublishPayload(result);

        File.WriteAllText(result.SteamCmdVdfPath, vdf.Replace("\"publishedfileid\"   \"0\"", "\"publishedfileid\"   \"9876543210\"", StringComparison.Ordinal));
        Assert.Equal(9876543210UL, SteamCmdService.ApplyPublishedFileId(project, result.SteamCmdVdfPath));
        Assert.Equal(9876543210UL, project.PublishedWorkshopId);
    }

    [Fact]
    public void FusionStrict_RejectsDifferentFilesAtSameMediaPath()
    {
        var first = CreateMod("One", "one", "return 1");
        var second = CreateMod("Two", "two", "return 2");
        var project = ValidProject(PackageMode.FusionStrict,
            Ref("one", "One", first), Ref("two", "Two", second));
        var service = new PackageBuildService(new ApplicationPaths(Path.Combine(_root, "fusion-data")), new PackageValidator());

        var exception = Assert.Throws<PackageBuildException>(() => service.Build(project));
        Assert.Contains(exception.Validation.Issues, x => x.Code == "FUSION_COLLISION");
    }

    [Fact]
    public void PermissionRecords_AreAdvisoryAndNeverBlockActions()
    {
        var source = CreateMod("Unknown", "unknown", "return true");
        var project = ValidProject(PackageMode.Bundle, new PackageModReference
        {
            ModId = "unknown",
            Name = "Unknown",
            SourceModRoot = source,
            SelectedVersionFolder = "common",
            Permission = new() { Status = PermissionStatus.Unknown }
        });
        var steamCmd = Path.Combine(_root, "steamcmd.exe");
        File.WriteAllText(steamCmd, string.Empty);
        project.Automation.SteamCmdPath = steamCmd;
        project.Automation.SteamUsername = "publisher";
        project.LegalWarningAccepted = false;
        var validation = new PackageValidator().Validate(project);
        Assert.True(validation.CanBuild);
        Assert.True(validation.CanPublish);
        Assert.True(validation.CanAutomate);
        Assert.Contains(validation.Issues, x => x.Code == "RIGHTS_UNKNOWN" && !x.IsError);
        Assert.Contains(validation.Issues, x => x.Code == "LEGAL_ACK" && !x.IsError);

        project.LegalWarningAccepted = true;
        project.Mods[0].Permission.Status = PermissionStatus.Denied;
        validation = new PackageValidator().Validate(project);
        Assert.True(validation.CanBuild);
        Assert.True(validation.CanPublish);
        Assert.True(validation.CanAutomate);
        Assert.Contains(validation.Issues, x => x.Code == "RIGHTS_DENIED" && !x.IsError);
    }

    [Fact]
    public void Snapshot_KeepsPinnedVersion_UntilExplicitUpdate()
    {
        var source = CreateMod("Pinned", "pinned-id", "return 'v1'");
        var project = ValidProject(PackageMode.Bundle, Ref("pinned-id", "Pinned", source));
        var paths = new ApplicationPaths(Path.Combine(_root, "snapshot-data"));
        var snapshots = new PackageSourceSnapshotService(paths);
        snapshots.UpdateAll(project);
        var originalHash = project.Mods[0].PinnedContentHash;

        File.WriteAllText(Path.Combine(source, "common", "media", "lua", "client", "test.lua"), "return 'v2'");
        var builder = new PackageBuildService(paths, new PackageValidator());
        var firstBuild = builder.Build(project);
        Assert.True(firstBuild.HardLinkedFiles > 0);
        Assert.True(firstBuild.HardLinkedBytes > 0);
        var firstBuildLua = Path.Combine(firstBuild.WorkshopContentRoot, "mods", "Pinned", "common", "media", "lua", "client", "test.lua");
        Assert.Equal("return 'v1'", File.ReadAllText(firstBuildLua));
        Assert.True(File.GetAttributes(firstBuildLua).HasFlag(FileAttributes.ReadOnly));
        snapshots.EnsurePinned(project);
        var lockFile = File.ReadAllText(firstBuild.LockFilePath);
        Assert.Contains("\"schemaVersion\": 2", lockFile);
        Assert.Contains("\"sourceModId\": \"pinned-id\"", lockFile);

        snapshots.UpdateAll(project);
        Assert.Equal("return 'v1'", File.ReadAllText(firstBuildLua));
        var secondBuild = builder.Build(project);
        Assert.Equal("return 'v2'", File.ReadAllText(Path.Combine(secondBuild.WorkshopContentRoot, "mods", "Pinned", "common", "media", "lua", "client", "test.lua")));
        Assert.NotEqual(originalHash, project.Mods[0].PinnedContentHash);
    }

    [Fact]
    public void HardLinkedBuildCanBeDeletedAndRebuiltWithoutLosingPinnedContent()
    {
        var source = CreateMod("DisposableBuild", "disposable-build", "return 'stable'");
        var project = ValidProject(PackageMode.Bundle, Ref("disposable-build", "Disposable Build", source));
        var paths = new ApplicationPaths(Path.Combine(_root, "disposable-build-data"));
        var snapshots = new PackageSourceSnapshotService(paths);
        snapshots.UpdateAll(project);
        var pinnedLua = Path.Combine(project.Mods[0].PinnedSourceRoot, "common", "media", "lua", "client", "test.lua");
        var pinnedHash = project.Mods[0].PinnedContentHash;
        var builder = new PackageBuildService(paths, new PackageValidator());
        Directory.Delete(source, true);

        var firstBuild = builder.Build(project);
        SafeFileTree.DeleteScopedDirectory(paths.BuildsRoot, firstBuild.BuildRoot);

        Assert.True(File.Exists(pinnedLua));
        Assert.Equal("return 'stable'", File.ReadAllText(pinnedLua));
        Assert.Equal(pinnedHash, SafeFileTree.ComputeDirectoryHash(project.Mods[0].PinnedSourceRoot));
        var secondBuild = builder.Build(project);
        Assert.True(secondBuild.HardLinkedFiles > 0);
        Assert.Equal("return 'stable'", File.ReadAllText(Path.Combine(secondBuild.WorkshopContentRoot, "mods", "DisposableBuild", "common", "media", "lua", "client", "test.lua")));
    }

    [Fact]
    public void UnpinnedAndFusionBuildsRemainIndependentCopies()
    {
        var bundleSource = CreateMod("Unpinned", "unpinned", "return 'bundle-v1'");
        var bundleProject = ValidProject(PackageMode.Bundle, Ref("unpinned", "Unpinned", bundleSource));
        var bundleResult = new PackageBuildService(new ApplicationPaths(Path.Combine(_root, "unpinned-data")), new PackageValidator()).Build(bundleProject);
        var bundleOutput = Path.Combine(bundleResult.WorkshopContentRoot, "mods", "Unpinned", "common", "media", "lua", "client", "test.lua");

        Assert.Equal(0, bundleResult.HardLinkedFiles);
        File.WriteAllText(Path.Combine(bundleSource, "common", "media", "lua", "client", "test.lua"), "return 'bundle-v2'");
        Assert.Equal("return 'bundle-v1'", File.ReadAllText(bundleOutput));

        var fusionSource = CreateMod("FusionCopy", "fusion-copy", "return 'fusion-v1'");
        var fusionProject = ValidProject(PackageMode.FusionStrict, Ref("fusion-copy", "Fusion Copy", fusionSource));
        var fusionPaths = new ApplicationPaths(Path.Combine(_root, "fusion-copy-data"));
        new PackageSourceSnapshotService(fusionPaths).UpdateAll(fusionProject);
        var fusionResult = new PackageBuildService(fusionPaths, new PackageValidator()).Build(fusionProject);

        Assert.Equal(0, fusionResult.HardLinkedFiles);
        Assert.True(File.Exists(Path.Combine(fusionResult.WorkshopContentRoot, "mods", fusionProject.FusionModId, "common", "media", "lua", "client", "test.lua")));
    }

    [Fact]
    public void HardLinkFailureFallsBackToIndependentFileCopies()
    {
        var source = Path.Combine(_root, "link-fallback-source");
        var destination = Path.Combine(_root, "link-fallback-destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var sourceFile = Path.Combine(source, "nested", "payload.bin");
        File.WriteAllText(sourceFile, "original");
        var materializations = new List<bool>();

        SafeFileTree.LinkOrCopyDirectory(
            source,
            destination,
            (_, _, linked) => materializations.Add(linked),
            (_, _) => false);

        var destinationFile = Path.Combine(destination, "nested", "payload.bin");
        Assert.Equal([false], materializations);
        Assert.Equal("original", File.ReadAllText(destinationFile));
        File.WriteAllText(sourceFile, "changed");
        Assert.Equal("original", File.ReadAllText(destinationFile));
    }

    [Fact]
    public void FailedRebuildKeepsThePreviouslyCompletedHardLinkedBuild()
    {
        var source = CreateMod("AtomicLinks", "atomic-links", "return 'v1'");
        var project = ValidProject(PackageMode.Bundle, Ref("atomic-links", "Atomic Links", source));
        var paths = new ApplicationPaths(Path.Combine(_root, "atomic-links-data"));
        var snapshots = new PackageSourceSnapshotService(paths);
        var builder = new PackageBuildService(paths, new PackageValidator());
        snapshots.UpdateAll(project);
        var firstBuild = builder.Build(project);
        var output = Path.Combine(firstBuild.WorkshopContentRoot, "mods", "AtomicLinks", "common", "media", "lua", "client", "test.lua");

        File.WriteAllText(Path.Combine(source, "common", "media", "lua", "client", "test.lua"), "return 'v2'");
        snapshots.UpdateAll(project);
        project.PreviewImagePath = Path.Combine(_root, "missing-preview.png");

        Assert.Throws<FileNotFoundException>(() => builder.Build(project));
        Assert.Equal("return 'v1'", File.ReadAllText(output));
        Assert.False(Directory.Exists(firstBuild.BuildRoot + ".next"));

        project.PreviewImagePath = null;
        var recoveredBuild = builder.Build(project);
        Assert.Equal("return 'v2'", File.ReadAllText(Path.Combine(recoveredBuild.WorkshopContentRoot, "mods", "AtomicLinks", "common", "media", "lua", "client", "test.lua")));
    }

    [Fact]
    public void Snapshot_TargetedUpdateLeavesExcludedModPinned()
    {
        var firstSource = CreateMod("TargetedFirst", "targeted-first", "return 'first-v1'");
        var secondSource = CreateMod("TargetedSecond", "targeted-second", "return 'second-v1'");
        var project = ValidProject(PackageMode.Bundle,
            Ref("targeted-first", "Targeted First", firstSource),
            Ref("targeted-second", "Targeted Second", secondSource));
        var snapshots = new PackageSourceSnapshotService(new ApplicationPaths(Path.Combine(_root, "targeted-data")));
        snapshots.UpdateAll(project);
        var secondHash = project.Mods[1].PinnedContentHash;

        File.WriteAllText(Path.Combine(firstSource, "common", "media", "lua", "client", "test.lua"), "return 'first-v2'");
        File.WriteAllText(Path.Combine(secondSource, "common", "media", "lua", "client", "test.lua"), "return 'second-v2'");
        snapshots.Update(project, [project.Mods[0]]);

        Assert.Equal("return 'first-v2'", File.ReadAllText(Path.Combine(project.Mods[0].PinnedSourceRoot, "common", "media", "lua", "client", "test.lua")));
        Assert.Equal("return 'second-v1'", File.ReadAllText(Path.Combine(project.Mods[1].PinnedSourceRoot, "common", "media", "lua", "client", "test.lua")));
        Assert.Equal(secondHash, project.Mods[1].PinnedContentHash);
    }

    [Fact]
    public void Snapshot_RejectsOutOfBandChanges()
    {
        var source = CreateMod("Protected", "protected-id", "return 'trusted'");
        var project = ValidProject(PackageMode.Bundle, Ref("protected-id", "Protected", source));
        var paths = new ApplicationPaths(Path.Combine(_root, "integrity-data"));
        var snapshots = new PackageSourceSnapshotService(paths);
        snapshots.UpdateAll(project);

        var pinnedLua = Path.Combine(project.Mods[0].PinnedSourceRoot, "common", "media", "lua", "client", "test.lua");
        File.WriteAllText(pinnedLua, "return 'tampered'");

        var exception = Assert.Throws<IOException>(() => snapshots.EnsurePinned(project));
        Assert.Contains("modifié hors de PZASM", exception.Message);
    }

    [Fact]
    public void SnapshotMetadataMigrationStillPerformsOneFullIntegrityAudit()
    {
        var source = CreateMod("MigratedIntegrity", "migrated-integrity", "return 'trusted'");
        var project = ValidProject(PackageMode.Bundle, Ref("migrated-integrity", "Migrated Integrity", source));
        var snapshots = new PackageSourceSnapshotService(new ApplicationPaths(Path.Combine(_root, "migrated-integrity-data")));
        snapshots.UpdateAll(project);
        project.Mods[0].PinnedMetadataStamp = string.Empty;
        File.WriteAllText(Path.Combine(project.Mods[0].PinnedSourceRoot, "common", "media", "lua", "client", "test.lua"), "return 'tampered'");

        Assert.Throws<IOException>(() => snapshots.EnsurePinned(project));
    }

    [Fact]
    public void Duplicate_GetsIndependentProjectAndSnapshotIds()
    {
        var source = CreateMod("Duplicate", "duplicate-id", "return true");
        var paths = new ApplicationPaths(Path.Combine(_root, "duplicate-data"));
        var store = new PackageProjectStore(paths);
        var snapshots = new PackageSourceSnapshotService(paths);
        var projects = new PackageProjectService(paths, store, snapshots);
        var original = ValidProject(PackageMode.Bundle, Ref("duplicate-id", "Duplicate", source));
        original.PublishedWorkshopId = 12345;
        snapshots.UpdateAll(original);
        store.Save(original);

        var clone = projects.Duplicate(original.Id);

        Assert.NotEqual(original.Id, clone.Id);
        Assert.NotEqual(original.StableSuffix, clone.StableSuffix);
        Assert.Equal(0UL, clone.PublishedWorkshopId);
        Assert.NotEqual(original.Mods[0].Id, clone.Mods[0].Id);
        Assert.NotEqual(original.Mods[0].PinnedSourceRoot, clone.Mods[0].PinnedSourceRoot);
        Assert.Equal(original.Mods[0].SourceModRoot, clone.Mods[0].SourceModRoot);
        Assert.True(Directory.Exists(clone.Mods[0].PinnedSourceRoot));
    }

    [Fact]
    public void AutomationErrors_DoNotBlockManualBuildOrPublish()
    {
        var source = CreateMod("Schedule", "schedule-id", "return true");
        var project = ValidProject(PackageMode.Bundle, Ref("schedule-id", "Schedule", source));
        var steamCmd = Path.Combine(_root, "steamcmd.exe");
        File.WriteAllText(steamCmd, string.Empty);
        project.Automation.SteamCmdPath = steamCmd;
        project.Automation.SteamUsername = "publisher";
        project.Automation.Enabled = true;
        project.Automation.DailyTimes = ["not-a-time"];
        project.Automation.CoordinatedServerName = string.Empty;

        var validation = new PackageValidator().Validate(project);

        Assert.True(validation.CanBuild);
        Assert.True(validation.CanPublish);
        Assert.False(validation.CanAutomate);
        Assert.Contains(validation.Issues, x => x.Code == "AUTOMATION_TIME");
    }

    [Fact]
    public void ScheduledPublishDoesNotRequireLocalServerCoordination()
    {
        var source = CreateMod("Remote", "remote-id", "return true");
        var project = ValidProject(PackageMode.Bundle, Ref("remote-id", "Remote", source));
        var steamCmd = Path.Combine(_root, "steamcmd.exe");
        File.WriteAllText(steamCmd, string.Empty);
        project.Automation.SteamCmdPath = steamCmd;
        project.Automation.SteamUsername = "publisher";
        project.Automation.Enabled = true;
        project.Automation.DailyTimes = ["04:00"];
        project.Automation.PublishAfterBuild = true;
        project.Automation.CoordinatedServerName = string.Empty;

        var validation = new PackageValidator().Validate(project);

        Assert.True(validation.CanAutomate);
        Assert.DoesNotContain(validation.Issues, issue => issue.Code == "AUTOMATION_SERVER");
    }

    private PackageProject ValidProject(PackageMode mode, params PackageModReference[] mods) => new()
    {
        Name = "Server Pack",
        Description = "A transparent server pack.",
        Mode = mode,
        LegalWarningAccepted = true,
        Mods = mods.Select((x, i) => { x.Order = i; return x; }).ToList()
    };

    private static PackageModReference Ref(string id, string name, string source) => new()
    {
        ModId = id,
        Name = name,
        SourceModRoot = source,
        SelectedVersionFolder = "common",
        Permission = new() { Status = PermissionStatus.AuthorOwned }
    };

    private string CreateMod(string folder, string id, string lua)
    {
        var root = Path.Combine(_root, "sources", folder);
        Directory.CreateDirectory(Path.Combine(root, "common", "media", "lua", "client"));
        File.WriteAllText(Path.Combine(root, "mod.info"), $"name={folder}\nid={id}\n");
        File.WriteAllText(Path.Combine(root, "common", "mod.info"), $"name={folder}\nid={id}\npzversion=42\n");
        File.WriteAllText(Path.Combine(root, "common", "media", "lua", "client", "test.lua"), lua);
        return root;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) SafeFileTree.DeleteScopedDirectory(Path.GetDirectoryName(_root)!, _root);
    }
}
