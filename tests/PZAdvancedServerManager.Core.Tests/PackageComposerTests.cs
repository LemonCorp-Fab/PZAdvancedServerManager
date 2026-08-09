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

    [Fact]
    public void DependencyPlanNormalizesIdsAndAddsDependenciesBeforeRequester()
    {
        var project = new PackageProject();
        var library = Mod("library", "Library");
        var feature = Mod("feature", "Feature", ["\\library"]);

        var plan = PackageProjectComposer.PlanDependencies(project, [feature], [feature, library]);
        var added = PackageProjectComposer.AddWithDependencies(project, feature, [feature, library]);

        Assert.Equal("library", Assert.Single(plan.AvailableDependencies).ModId);
        Assert.Empty(plan.UnresolvedModIds);
        Assert.Equal(["library", "feature"], added.Select(mod => mod.ModId));
        Assert.Equal([0, 1], project.Mods.Select(mod => mod.Order));
    }

    [Fact]
    public void DependencyPlanReportsUnavailableModIdsWithoutInventingSources()
    {
        var project = new PackageProject();
        var feature = Mod("feature", "Feature", ["missing-library"]);

        var plan = PackageProjectComposer.PlanDependencies(project, [feature], [feature]);
        var added = PackageProjectComposer.AddWithDependencies(project, feature, [feature], includeDependencies: false);

        Assert.Empty(plan.AvailableDependencies);
        Assert.Equal("missing-library", Assert.Single(plan.UnresolvedModIds));
        Assert.Equal("feature", Assert.Single(added).ModId);
    }

    private static DiscoveredMod Mod(string id, string name, string[]? required = null) => new()
    {
        ModRoot = Path.Combine(Path.GetTempPath(), id),
        ModId = id,
        Name = name,
        RequiredModIds = required ?? []
    };
}
