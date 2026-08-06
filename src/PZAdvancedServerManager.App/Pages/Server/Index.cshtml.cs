using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PZAdvancedServerManager.App.Services;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages.Server;

public class IndexModel(DiscoveryCache discovery, PackageProjectStore projectStore, ApplicationPaths applicationPaths) : PageModel
{
    public IReadOnlyList<ServerConfigEntry> Configs { get; private set; } = [];
    public ServerConfigEntry? Selected { get; private set; }
    public IReadOnlyList<PackageProject> Projects { get; private set; } = [];
    [BindProperty] public string RawContent { get; set; } = string.Empty;

    public void OnGet(string? key)
    {
        LoadConfigs();
        Projects = projectStore.GetAll();
        Selected = Configs.FirstOrDefault(x => x.Key == key) ?? Configs.FirstOrDefault();
        if (Selected is not null) RawContent = ReadPreservingEncoding(Selected.Path).Text;
    }

    public IActionResult OnPostSave(string key)
    {
        LoadConfigs();
        var selected = Configs.FirstOrDefault(x => x.Key == key);
        if (selected is null) return BadRequest("Configuration serveur non reconnue.");
        var backup = selected.Path + $".pzasm.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        System.IO.File.Copy(selected.Path, backup, false);
        var temp = selected.Path + ".pzasm.tmp";
        var encoding = ReadPreservingEncoding(selected.Path).Encoding;
        System.IO.File.WriteAllText(temp, RawContent.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), encoding);
        System.IO.File.Move(temp, selected.Path, true);
        TempData["Message"] = $"Configuration enregistrée. Sauvegarde : {backup}";
        return RedirectToPage(new { key });
    }

    public IActionResult OnPostCreate(string serverName)
    {
        var safeName = string.Concat((serverName ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            TempData["Error"] = "Choisissez un nom composé de lettres, chiffres, tirets ou underscores.";
            return RedirectToPage();
        }
        var serverRoot = GetServerRoot();
        Directory.CreateDirectory(serverRoot);
        var path = Path.Combine(serverRoot, safeName + ".ini");
        if (System.IO.File.Exists(path))
        {
            TempData["Error"] = "Cette configuration existe déjà.";
            return RedirectToPage();
        }
        var template = $"# Créé par PZ Advanced Server Manager\nPublicName={safeName}\nPublicDescription=\nPassword=\nDefaultPort=16261\nMaxPlayers=16\nPauseEmpty=true\nDoLuaChecksum=true\nWorkshopItems=\nMods=\nMap=Muldraugh, KY\n";
        System.IO.File.WriteAllText(path, template.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        return RedirectToPage(new { key = Encode(path) });
    }

    public IActionResult OnPostApplyPack(string key, Guid projectId)
    {
        LoadConfigs();
        var selected = Configs.FirstOrDefault(x => x.Key == key);
        var project = projectStore.Get(projectId);
        if (selected is null || project is null) return BadRequest("Serveur ou pack non reconnu.");
        var snippetPath = Path.Combine(applicationPaths.BuildRoot(project.Id), "server-config.txt");
        if (!System.IO.File.Exists(snippetPath))
        {
            TempData["Error"] = "Construisez d'abord ce pack afin de générer sa configuration serveur.";
            return RedirectToPage(new { key });
        }
        if (project.PublishedWorkshopId == 0)
        {
            TempData["Error"] = "Publiez d'abord le pack : le serveur a besoin de son Workshop ID réel.";
            return RedirectToPage(new { key });
        }

        var backup = selected.Path + $".pzasm.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        System.IO.File.Copy(selected.Path, backup, false);
        var source = ServerConfigDocument.Load(snippetPath);
        var target = ServerConfigDocument.Load(selected.Path);
        target.Set("WorkshopItems", source.Get("WorkshopItems"));
        target.Set("Mods", source.Get("Mods"));
        target.Set("Map", source.Get("Map"));
        target.Save(selected.Path);
        TempData["Message"] = $"Pack « {project.Name} » appliqué : un Workshop ID, {source.GetList("Mods").Count} Mod IDs. Sauvegarde : {backup}";
        return RedirectToPage(new { key });
    }

    public IReadOnlyList<string> Mods => Selected is null ? [] : ServerConfigDocument.Load(Selected.Path).GetList("Mods");
    public IReadOnlyList<string> WorkshopItems => Selected is null ? [] : ServerConfigDocument.Load(Selected.Path).GetList("WorkshopItems");
    public IReadOnlyList<string> Maps => Selected is null ? [] : ServerConfigDocument.Load(Selected.Path).GetList("Map");

    private void LoadConfigs()
    {
        var root = GetServerRoot();
        Configs = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.ini").OrderBy(x => x).Select(x => new ServerConfigEntry(Path.GetFileNameWithoutExtension(x), x, Encode(x))).ToList()
            : [];
    }

    private string GetServerRoot() => Path.Combine(discovery.Installation.UserZomboidRoot, "Server");
    private static string Encode(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
    private static (string Text, Encoding Encoding) ReadPreservingEncoding(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            return (Encoding.UTF8.GetString(bytes[3..]), new UTF8Encoding(true));
        try
        {
            var strictUtf8 = new UTF8Encoding(false, true);
            return (strictUtf8.GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
        }
    }
    public sealed record ServerConfigEntry(string Name, string Path, string Key);
}
