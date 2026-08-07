using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Packaging;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class PackageComposerTests
{
    [Fact]
    public void AddingModPrefillsRightsHolderAndGlobalUpdatePolicy()
    {
        var project = new PackageProject();
        var discovered = new DiscoveredMod
        {
            WorkshopId = 123,
            ModRoot = Path.Combine(Path.GetTempPath(), "example-mod"),
            ModId = "example-id",
            Name = "Example Mod",
            Author = "Example Author"
        };

        var added = PackageProjectComposer.AddWithDependencies(project, discovered, [discovered]);

        var reference = Assert.Single(added);
        Assert.Equal("Example Author", reference.Author);
        Assert.Equal("Example Author", reference.Permission.RightsHolder);
        Assert.True(reference.IncludeInGlobalUpdates);
    }
}
