using PZAdvancedServerManager.Core.Packaging;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class PackageIntegrityServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pzasm-integrity-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RepairCaseReferences_CorrectsOnlyUniquePortablePath()
    {
        Write("mods/Test/42/media/textures/UI/Icon.png", "png");
        Write("mods/Test/42/media/lua/client/test.lua", "local icon = 'media/textures/ui/icon.png'\n");

        var report = PackageIntegrityService.RepairCaseReferences(Path.Combine(root, "mods/Test"));

        Assert.Equal(1, report.ReferencesCorrected);
        Assert.Contains("media/textures/UI/Icon.png", File.ReadAllText(Path.Combine(root, "mods/Test/42/media/lua/client/test.lua")));
    }

    [Fact]
    public void RepairCaseReferences_CorrectsPzModelAlias()
    {
        Write("42/media/models_X/MyMod/Burger.fbx", "model");
        Write("42/media/scripts/items.txt", "model Burger { mesh = mymod/burger, }\n");

        var report = PackageIntegrityService.RepairCaseReferences(root);

        Assert.Equal(1, report.ReferencesCorrected);
        Assert.Contains("mesh = MyMod/Burger", File.ReadAllText(Path.Combine(root, "42/media/scripts/items.txt")));
    }

    [Fact]
    public void RepairCaseReferences_LeavesAmbiguousReferenceUntouched()
    {
        Write("media/textures/A/Icon.png", "a");
        Write("media/textures/B/Icon.png", "b");
        Write("media/lua/client/test.lua", "local icon = 'icon.png'\n");

        var report = PackageIntegrityService.RepairCaseReferences(root);

        Assert.Equal(0, report.ReferencesCorrected);
        Assert.NotEmpty(report.Warnings);
        Assert.Contains("icon.png", File.ReadAllText(Path.Combine(root, "media/lua/client/test.lua")));
    }

    [Fact]
    public void ManifestVerification_DetectsTamperingAndUnexpectedFiles()
    {
        Write("mods/Test/mod.info", "id=Test");
        var manifest = PackageIntegrityService.CreateManifest(root);
        var verified = PackageIntegrityService.VerifyManifest(root, manifest.PayloadFingerprint);
        Assert.True(verified.Success);

        Write("mods/Test/mod.info", "id=Changed");
        Assert.Throws<PackageIntegrityException>(() => PackageIntegrityService.VerifyManifest(root, manifest.PayloadFingerprint));

        Write("mods/Test/mod.info", "id=Test");
        Write("unexpected.txt", "extra");
        Assert.Throws<PackageIntegrityException>(() => PackageIntegrityService.VerifyManifest(root, manifest.PayloadFingerprint));
    }

    [Fact]
    public void ValidatePortableTree_RejectsCaseCollision_OnCaseSensitiveFileSystems()
    {
        if (OperatingSystem.IsWindows()) return;
        Write("mods/Test/Icon.png", "a");
        Write("mods/Test/icon.png", "b");

        Assert.Throws<PackageIntegrityException>(() => PackageIntegrityService.ValidatePortableTree(root));
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
