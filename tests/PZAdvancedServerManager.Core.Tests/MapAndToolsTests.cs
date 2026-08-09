using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class MapAndToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-map-tools", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MapAnalysis_UsesDependenciesConflictsAndVanillaLast()
    {
        var modRoot = CreateMapMod();
        var project = new PackageProject
        {
            Mods =
            [
                new PackageModReference
                {
                    Name = "Map Pack",
                    ModId = "map-pack",
                    SourceModRoot = modRoot,
                    SelectedVersionFolder = "common",
                    MapFolders = ["Large Town", "Town Road Ext"]
                }
            ]
        };

        var analysis = new MapPriorityService().Analyze(project);

        Assert.Equal(["Town Road Ext", "Large Town", "Muldraugh, KY"], analysis.RecommendedOrder);
        Assert.Contains(analysis.Entries.Single(x => x.FolderName == "Town Road Ext").Conflicts, x => x.StartsWith("Large Town", StringComparison.Ordinal));
        Assert.True(analysis.Entries.Last().IsVanilla);
    }

    [Fact]
    public void MapAnalysis_PreservesManualOrderWhileOfferingRecommendation()
    {
        var project = new PackageProject { MapOrder = ["Muldraugh, KY", "Custom Manual Map"] };

        var analysis = new MapPriorityService().Analyze(project);

        Assert.Equal("Muldraugh, KY", analysis.Entries[0].FolderName);
        Assert.Equal("Custom Manual Map", analysis.Entries[1].FolderName);
        Assert.Equal("Muldraugh, KY", analysis.RecommendedOrder.Last());
    }

    [Fact]
    public void ModConflictAnalysis_DetectsCompatibilityDependenciesAndFileCollisions()
    {
        var appRoot = CreateConflictMod("app", "app", "return 'app'");
        var libraryRoot = CreateConflictMod("library", "library", "return 'library'");
        WriteConflictFile(appRoot, Path.Combine("lua", "shared", "Translate", "FR", "UI.json"), "{\"UI_Test\":\"Application\"}");
        WriteConflictFile(libraryRoot, Path.Combine("lua", "shared", "Translate", "FR", "UI.json"), "{\"UI_Test\":\"Library\"}");
        var legacyRoot = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(Path.Combine(legacyRoot, "media", "lua", "client"));
        File.WriteAllText(Path.Combine(legacyRoot, "mod.info"), "name=Legacy\nid=legacy\n");
        var app = new PackageModReference { ModId = "app", Name = "Application", SourceModRoot = appRoot, SelectedVersionFolder = "common", RequiredModIds = ["library"], Order = 0 };
        var library = new PackageModReference { ModId = "library", Name = "Library", SourceModRoot = libraryRoot, SelectedVersionFolder = "common", Order = 1 };
        var legacy = new PackageModReference { ModId = "legacy", Name = "Legacy", SourceModRoot = legacyRoot, Order = 2 };
        var project = new PackageProject { TargetPzVersion = "42.20.2", Mods = [app, library, legacy] };

        var analysis = new ModConflictAnalyzer(new MapPriorityService()).Analyze(project);

        Assert.Contains(analysis.Issues, issue => issue.Code == "B42_LEGACY" && issue.ModIds.Contains("legacy"));
        var gameplayConflict = Assert.Single(analysis.Issues, issue => issue.Code == "FILE_COLLISION" && issue.EffectiveTypeLabel == "Lua partagé / gameplay");
        Assert.Equal(ModConflictRisk.High, gameplayConflict.Risk);
        Assert.Equal(ModConflictSeverity.Warning, gameplayConflict.Severity);
        var translationConflict = Assert.Single(analysis.Issues, issue => issue.Code == "FILE_COLLISION" && issue.EffectiveTypeLabel == "Traductions");
        Assert.Equal(ModConflictRisk.Low, translationConflict.Risk);
        Assert.Equal(ModConflictSeverity.Information, translationConflict.Severity);
        Assert.Equal("lua/shared/translate/fr/ui.json", translationConflict.PrimaryEvidence);
        Assert.Equal(2, translationConflict.FileEvidence.Count);
        Assert.All(translationConflict.FileEvidence, evidence => Assert.True(File.Exists(evidence.PhysicalPath)));
        Assert.Contains(analysis.TypeSummaries, summary => summary.TypeLabel == "Traductions" && summary.Risk == ModConflictRisk.Low && summary.Information == 1);
        Assert.Equal(library.Id, analysis.RecommendedModOrder[0]);
        Assert.Contains(analysis.Issues, issue => issue.Code == "MOD_ORDER");
    }

    [Fact]
    public async Task SteamCmdInstaller_ExtractsPortableBootstrapIntoToolsRoot()
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "data"));
        using var client = new HttpClient(new ArchiveHandler(CreateSteamCmdArchive()));
        var installer = new SteamCmdInstaller(paths, client);

        var result = await installer.InstallAsync();

        Assert.Equal(paths.SteamCmdExecutable, result.ExecutablePath);
        Assert.True(File.Exists(result.ExecutablePath));
        Assert.True(installer.GetStatus().Installed);
    }

    private string CreateMapMod()
    {
        var root = Path.Combine(_root, "map-mod");
        var large = Path.Combine(root, "common", "media", "maps", "Large Town");
        var road = Path.Combine(root, "common", "media", "maps", "Town Road Ext");
        Directory.CreateDirectory(large);
        Directory.CreateDirectory(road);
        File.WriteAllText(Path.Combine(root, "common", "mod.info"), "name=Map Pack\nid=map-pack\n");
        File.WriteAllText(Path.Combine(large, "map.info"), "title=Large Town\nlots=Muldraugh, KY\n");
        File.WriteAllText(Path.Combine(road, "map.info"), "title=Town Road Ext\nlots=Large Town\n");
        File.WriteAllText(Path.Combine(large, "1_1.lotheader"), string.Empty);
        File.WriteAllText(Path.Combine(large, "1_2.lotheader"), string.Empty);
        File.WriteAllText(Path.Combine(road, "1_1.lotheader"), string.Empty);
        return root;
    }

    private string CreateConflictMod(string folder, string id, string content)
    {
        var root = Path.Combine(_root, folder);
        var luaRoot = Path.Combine(root, "common", "media", "lua", "shared");
        Directory.CreateDirectory(luaRoot);
        File.WriteAllText(Path.Combine(root, "common", "mod.info"), $"name={folder}\nid={id}\npzversion=42\n");
        File.WriteAllText(Path.Combine(luaRoot, "shared-path.lua"), content);
        return root;
    }

    private static void WriteConflictFile(string modRoot, string relativeMediaPath, string content)
    {
        var path = Path.Combine(modRoot, "common", "media", relativeMediaPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static byte[] CreateSteamCmdArchive()
    {
        using var memory = new MemoryStream();
        if (OperatingSystem.IsWindows())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                var entry = archive.CreateEntry("steamcmd.exe");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("not-an-executable");
            }
        }
        else
        {
            using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, true))
            using (var writer = new TarWriter(gzip, true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "steamcmd.sh")
                {
                    DataStream = new MemoryStream("#!/bin/sh\nexit 0\n"u8.ToArray())
                };
                writer.WriteEntry(entry);
            }
        }
        return memory.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ArchiveHandler(byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
    }
}
