using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Tests;

public sealed class PzParsingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pzasm-parse", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SelectsHighestCompatibleBuild42Manifest()
    {
        Write("mod.info", "id=legacy");
        Write("42.0/mod.info", "id=base42");
        Write("42.13/mod.info", "id=modern42");
        Write("43.0/mod.info", "id=future");
        var selected = PzVersionSelector.SelectManifest(_root, "42.20.2", out var folder);
        Assert.Equal("42.13", folder);
        Assert.Equal("modern42", ModInfoParser.Parse(selected).Id);
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
