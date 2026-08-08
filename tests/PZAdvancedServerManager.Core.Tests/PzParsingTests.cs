using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;
using PZAdvancedServerManager.Core.Packaging;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class PzParsingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-parse", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SelectsHighestCompatibleBuild42Manifest()
    {
        Write("mod.info", "id=legacy");
        Write("42.0/mod.info", "id=base42");
        Write("42.13/mod.info", "id=modern42\nversion=2.4.1");
        Write("43.0/mod.info", "id=future");
        var selected = PzVersionSelector.SelectManifest(_root, "42.20.2", out var folder);
        Assert.Equal("42.13", folder);
        var info = ModInfoParser.Parse(selected);
        Assert.Equal("modern42", info.Id);
        Assert.Equal("2.4.1", info.Version);
    }

    [Fact]
    public void ServerConfigPreservesCommentsAndUnknownKeys()
    {
        var path = Write("server.ini", "# comment\nUnknownOption=keep\nMods=one;two\n");
        var config = ServerConfigDocument.Load(path);
        config.SetList("Mods", ["pack-one", "notice"]);
        config.Set("WorkshopItems", "999");
        var rendered = config.Render();
        Assert.Contains("# comment", rendered);
        Assert.Contains("UnknownOption=keep", rendered);
        Assert.Contains("Mods=pack-one;notice", rendered);
        Assert.Contains("WorkshopItems=999", rendered);
    }

    [Fact]
    public void ReadsWorkshopIdWrittenBackBySteamCmd()
    {
        var path = Write("item.vdf", "\"workshopitem\" { \"publishedfileid\" \"1234567890\" }");
        Assert.Equal(1234567890UL, SteamCmdService.ReadPublishedFileId(path));
    }

    [Fact]
    public void ServerConfigPreservesLatin1AndCrLfWhenSaving()
    {
        var path = Path.Combine(_root, "latin1.ini");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes("PublicDescription=Serveur privé\r\nMods=one\r\n"));
        var config = ServerConfigDocument.Load(path);
        config.Set("Mods", "pack;notice");
        config.Save(path);

        var bytes = File.ReadAllBytes(path);
        Assert.Contains((byte)0xe9, bytes);
        Assert.Contains("Serveur privé\r\nMods=pack;notice\r\n", System.Text.Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void ServerConfigPersistsRconPasswordExactly()
    {
        var path = Write("rcon.ini", "RCONPort=27015\nRCONPassword=old-value\n");
        var config = ServerConfigDocument.Load(path);
        const string password = "Pzasm-RCON_42!exact";
        config.Set("RCONPassword", password);
        config.Save(path);

        var persisted = ServerConfigDocument.Load(path);
        Assert.Equal(password, persisted.Get("RCONPassword"));
        Assert.Contains($"RCONPassword={password}", File.ReadAllText(path));
    }

    [Fact]
    public void ModListImportReadsServerIniAndPreservesOrder()
    {
        var parsed = ModListImportParser.Parse("# server\nWorkshopItems=123;456;123\nMods=alpha;beta;ALPHA\n");

        Assert.Equal(ModListSourceKind.ServerIni, parsed.SourceKind);
        Assert.Equal([123UL, 456UL], parsed.WorkshopIds);
        Assert.Equal(["alpha", "beta"], parsed.ModIds);
        Assert.Empty(parsed.InvalidWorkshopIds);
    }

    [Fact]
    public void ModListImportReadsPlainSemicolonListAsModIds()
    {
        var parsed = ModListImportParser.Parse("alpha; beta ;\n gamma;alpha");

        Assert.Equal(ModListSourceKind.SemicolonList, parsed.SourceKind);
        Assert.Equal(["alpha", "beta", "gamma"], parsed.ModIds);
        Assert.Empty(parsed.WorkshopIds);
    }

    [Fact]
    public void ModListImportReportsInvalidWorkshopEntries()
    {
        var parsed = ModListImportParser.Parse("WorkshopItems=123;not-an-id\nMods=alpha");

        Assert.Equal([123UL], parsed.WorkshopIds);
        Assert.Equal(["not-an-id"], parsed.InvalidWorkshopIds);
    }

    [Fact]
    public void StructuredIniCatalogTypesRangesAndProtectsSecrets()
    {
        var content = "# Minimum=1 Maximum=100 Par défaut=32\nMaxPlayers=32\n# Mot de passe RCON\nRCONPassword=very-secret\nVoiceEnable=true\nUnknownFutureOption=value\n";
        var settings = StructuredServerSettings.ParseIni(content);

        var players = Assert.Single(settings, x => x.Key == "MaxPlayers");
        Assert.Equal(StructuredSettingKind.Integer, players.Kind);
        Assert.Equal(1, players.Minimum);
        Assert.Equal(100, players.Maximum);
        var password = Assert.Single(settings, x => x.Key == "RCONPassword");
        Assert.True(password.IsSecret);
        Assert.Equal("very-secret", StructuredServerSettings.ValidateAndFormat(password, string.Empty, password.Value));
        Assert.Contains(settings, x => x.Key == "UnknownFutureOption");
    }

    [Fact]
    public void SandboxEditorUpdatesNestedValuesWithoutFlatteningLua()
    {
        var path = Write("sandbox.lua", "SandboxVars = {\n    -- Minimum=0.00 Maximum=4.00\n    XpMultiplier = 1.0,\n    ZombieConfig = {\n        PopulationMultiplier = 1.0,\n    },\n    CustomMod = {\n        Enabled = true,\n        Label = \"original\",\n    },\n}\n");
        var document = SandboxSettingsDocument.Load(path);
        Assert.Contains(document.Settings, x => x.Key == "ZombieConfig.PopulationMultiplier");
        Assert.Contains(document.Settings, x => x.Key == "CustomMod.Enabled");

        document.Update(new Dictionary<string, string>
        {
            ["XpMultiplier"] = "2.5",
            ["ZombieConfig.PopulationMultiplier"] = "3.0",
            ["CustomMod.Enabled"] = "false",
            ["CustomMod.Label"] = "updated"
        });
        document.Save(path);

        var persisted = SandboxSettingsDocument.Load(path);
        Assert.Equal("2.5", persisted.Get("XpMultiplier"));
        Assert.Equal("3", persisted.Get("ZombieConfig.PopulationMultiplier"));
        Assert.Equal("false", persisted.Get("CustomMod.Enabled"));
        Assert.Equal("updated", persisted.Get("CustomMod.Label"));
        Assert.Contains("ZombieConfig = {", File.ReadAllText(path));
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\n", Environment.NewLine));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
