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
            new PackageModReference { ModId = "first-id", Name = "First Mod", SourceModRoot = source, SelectedVersionFolder = "common", WorkshopId = 123, SourceUrl = "https://example.test/123", Permission = new() { Status = PermissionStatus.AuthorOwned } });
        project.PublishedWorkshopId = 999;
        project.InjectConnectionNotice = true;

        var service = new PackageBuildService(new ApplicationPaths(Path.Combine(_root, "data")), new PackageValidator());
        var result = service.Build(project);

        Assert.True(File.Exists(Path.Combine(result.WorkshopContentRoot, "mods", "FirstFolder", "common", "media", "lua", "client", "test.lua")));
        Assert.True(File.Exists(Path.Combine(result.WorkshopContentRoot, "mods", project.NoticeModId, "common", "media", "lua", "client", "PZASM_PackNotice.lua")));
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
    public void UnknownRights_AllowsLocalBuild_ButBlocksPublication()
    {
        var source = CreateMod("Unknown", "unknown", "return true");
        var project = ValidProject(PackageMode.Bundle, new PackageModReference
        {
            ModId = "unknown", Name = "Unknown", SourceModRoot = source, SelectedVersionFolder = "common",
            Permission = new() { Status = PermissionStatus.Unknown }
        });
        var validation = new PackageValidator().Validate(project);
        Assert.True(validation.CanBuild);
        Assert.False(validation.CanPublish);
        Assert.Contains(validation.Issues, x => x.Code == "RIGHTS_UNKNOWN");
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
        ModId = id, Name = name, SourceModRoot = source, SelectedVersionFolder = "common",
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
