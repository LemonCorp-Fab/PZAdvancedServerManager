using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class PackageBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Bundle_Preserves_ModRoots_AndAddsOneNoticeMod()
    {
        var source = CreateMod("FirstFolder", "first-id", "return 'first'");
        var project = ValidProject(PackageMode.Bundle,
            new PackageModReference { ModId = "first-id", Name = "First Mod", Version = "1.2.3", SourceModRoot = source, SelectedVersionFolder = "common", WorkshopId = 123, SourceUrl = "https://example.test/123", Permission = new() { Status = PermissionStatus.AuthorOwned } });
        project.PublishedWorkshopId = 999;
        project.InjectConnectionNotice = true;

        var service = new PackageBuildService(new ApplicationPaths(Path.Combine(_root, "data")), new PackageValidator());
        var result = service.Build(project);

        Assert.True(File.Exists(Path.Combine(result.WorkshopContentRoot, "mods", "FirstFolder", "common", "media", "lua", "client", "test.lua")));
        Assert.True(File.Exists(Path.Combine(result.WorkshopContentRoot, "mods", project.NoticeModId, "common", "media", "lua", "client", "PZASM_PackNotice.lua")));
        var notice = File.ReadAllText(Path.Combine(result.WorkshopContentRoot, "mods", project.NoticeModId, "common", "media", "lua", "client", "PZASM_PackNotice.lua"));
        Assert.Contains("Version: 1.2.3", notice);
        var publicManifest = File.ReadAllText(Path.Combine(result.WorkshopContentRoot, "pzasm-pack-manifest.json"));
        Assert.Contains("first-id", publicManifest);
        Assert.DoesNotContain("privateAttachmentPath", publicManifest, StringComparison.OrdinalIgnoreCase);
        var config = File.ReadAllText(result.ServerConfigSnippetPath);
        Assert.Contains("WorkshopItems=999", config);
        Assert.Contains($"Mods=first-id;{project.NoticeModId}", config);
        Assert.DoesNotContain("WorkshopItems=123", config);
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
        Assert.Equal("return 'v1'", File.ReadAllText(Path.Combine(firstBuild.WorkshopContentRoot, "mods", "Pinned", "common", "media", "lua", "client", "test.lua")));

        snapshots.UpdateAll(project);
        var secondBuild = builder.Build(project);
        Assert.Equal("return 'v2'", File.ReadAllText(Path.Combine(secondBuild.WorkshopContentRoot, "mods", "Pinned", "common", "media", "lua", "client", "test.lua")));
        Assert.NotEqual(originalHash, project.Mods[0].PinnedContentHash);
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
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
