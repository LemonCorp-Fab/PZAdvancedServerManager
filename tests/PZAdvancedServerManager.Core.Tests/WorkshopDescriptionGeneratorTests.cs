using System.Text;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Packaging;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class WorkshopDescriptionGeneratorTests
{
    [Fact]
    public void SmallPackKeepsDetailedCredits()
    {
        var project = CreateProject(2);

        var result = WorkshopDescriptionGenerator.GenerateResult(project);

        Assert.False(result.IsCompact);
        Assert.Contains("Author 1", result.Text);
        Assert.Contains("Workshop 100000001", result.Text);
        Assert.True(result.Utf8Bytes < PzasmConstants.SteamWorkshopDescriptionMaximumUtf8Bytes);
    }

    [Fact]
    public void LargePackUsesCompactExhaustiveReferencesWithinSteamLimit()
    {
        var project = CreateProject(183);

        var result = WorkshopDescriptionGenerator.GenerateResult(project);

        Assert.True(result.IsCompact);
        Assert.True(result.Utf8Bytes <= PzasmConstants.GeneratedWorkshopDescriptionMaximumUtf8Bytes);
        foreach (var mod in project.Mods)
        {
            Assert.Contains($"W:{mod.WorkshopId}", result.Text);
            Assert.Contains($"M:{mod.ModId}", result.Text);
        }
        Assert.Contains("pzasm-pack-manifest.json", result.Text);
    }

    [Fact]
    public void LongUnicodeDescriptionIsTruncatedOnRuneBoundary()
    {
        var project = CreateProject(183);
        project.Description = string.Concat(Enumerable.Repeat("Survie 🧟‍♀️ multijoueur — ", 2000));

        var result = WorkshopDescriptionGenerator.GenerateResult(project);

        Assert.True(result.IsCompact);
        Assert.True(result.Utf8Bytes <= PzasmConstants.GeneratedWorkshopDescriptionMaximumUtf8Bytes);
        Assert.Equal(result.Text, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result.Text)));
        Assert.Contains("…", result.Text);
    }

    [Fact]
    public void ImpossibleExhaustiveListFailsBeforePublishing()
    {
        var project = CreateProject(183);
        foreach (var mod in project.Mods) mod.ModId = new string('x', 100);

        var exception = Assert.Throws<InvalidOperationException>(() => WorkshopDescriptionGenerator.Generate(project));

        Assert.Contains("liste exhaustive", exception.Message);
        Assert.Contains("Scindez", exception.Message);
    }

    private static PackageProject CreateProject(int count)
    {
        var project = new PackageProject
        {
            Name = "Stable server pack",
            Description = "A server pack with pinned mod versions."
        };
        for (var index = 1; index <= count; index++)
        {
            project.Mods.Add(new PackageModReference
            {
                WorkshopId = (ulong)(100_000_000 + index),
                ModId = $"mod.id.{index:D3}",
                Name = $"A deliberately descriptive mod name number {index:D3}",
                Author = $"Author {index}",
                SourceUrl = $"https://steamcommunity.com/sharedfiles/filedetails/?id={100_000_000 + index}",
                Order = index,
                Permission = new PermissionEvidence { Status = PermissionStatus.ExplicitPermission }
            });
        }
        return project;
    }
}
