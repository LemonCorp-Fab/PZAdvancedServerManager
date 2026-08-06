using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class ProjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-store", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MultipleProjectsKeepIndependentStableIdsAndWorkshopIds()
    {
        var paths = new ApplicationPaths(_root);
        var store = new PackageProjectStore(paths);
        var first = store.Create("Premier pack");
        var second = store.Create("Deuxième pack");
        first.PublishedWorkshopId = 111;
        second.PublishedWorkshopId = 222;
        store.Save(first);
        store.Save(second);

        var reopenedFirst = store.Get(first.Id)!;
        var reopenedSecond = store.Get(second.Id)!;
        Assert.NotEqual(reopenedFirst.Id, reopenedSecond.Id);
        Assert.NotEqual(reopenedFirst.StableSuffix, reopenedSecond.StableSuffix);
        Assert.Equal(111UL, reopenedFirst.PublishedWorkshopId);
        Assert.Equal(222UL, reopenedSecond.PublishedWorkshopId);
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void OldProjectSchemaIsMigratedOnRead()
    {
        var paths = new ApplicationPaths(_root);
        var id = Guid.NewGuid();
        var source = Path.Combine(_root, "LegacyFolder");
        Directory.CreateDirectory(source);
        File.WriteAllText(paths.ProjectFile(id), $$"""
        {
          "id": "{{id}}",
          "schemaVersion": 1,
          "name": "Legacy",
          "mods": [{ "id": "{{Guid.NewGuid()}}", "modId": "legacy", "sourceModRoot": "{{source.Replace("\\", "\\\\")}}" }]
        }
        """);

        var project = new PackageProjectStore(paths).Get(id)!;

        Assert.Equal(PZAdvancedServerManager.Core.Domain.PzasmConstants.CurrentProjectSchemaVersion, project.SchemaVersion);
        Assert.Equal("LegacyFolder", project.Mods[0].SourceFolderName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
