using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages.Server;

public class IndexModel(
    PackageProjectStore projectStore,
    ServerProfileService servers) : PageModel
{
    public IReadOnlyList<ServerConfigEntry> Configs { get; private set; } = [];
    public ServerConfigEntry? Selected { get; private set; }
    public ServerConfigSummary Summary { get; private set; } = new([], [], []);
    public IReadOnlyList<PackageProject> Projects { get; private set; } = [];
    public bool SelectedServerOnline { get; private set; }
    [BindProperty] public string RawContent { get; set; } = string.Empty;
    [BindProperty] public GuidedServerForm Guided { get; set; } = new();

    public async Task OnGetAsync(string? name, CancellationToken cancellationToken)
    {
        Configs = servers.List();
        Projects = projectStore.GetAll();
        Selected = Configs.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Configs.FirstOrDefault();
        if (Selected is null) return;
        RawContent = servers.ReadRaw(Selected.Name);
        Summary = servers.ReadSummary(Selected.Name);
        Guided = GuidedServerForm.From(ServerConfigDocument.Load(Selected.Path));
        SelectedServerOnline = await servers.IsOnlineAsync(Selected.Name, cancellationToken);
    }

    public IActionResult OnPostSave(string name)
    {
        try
        {
            var backup = servers.SaveRaw(name, RawContent);
            TempData["Message"] = $"Configuration enregistrée. Sauvegarde : {backup}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public IActionResult OnPostSaveGuided(string name)
    {
        try
        {
            if (!TryValidateModel(Guided)) throw new ValidationException("Vérifiez les ports, le nombre de joueurs et les valeurs numériques.");
            var backup = servers.Update(name, Guided.ToValues());
            TempData["Message"] = $"Configuration guidée enregistrée. Sauvegarde : {backup}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public IActionResult OnPostCreate(string serverName)
    {
        try
        {
            var profile = servers.Create(serverName);
            return RedirectToPage(new { name = profile.Name });
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToPage();
        }
    }

    public IActionResult OnPostStart(string name)
    {
        try
        {
            servers.Start(name);
            TempData["Message"] = $"Démarrage de « {name} » demandé. Le statut RCON apparaîtra après l'initialisation.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostStopAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await servers.StopAsync(name, cancellationToken);
            TempData["Message"] = $"Serveur « {name} » sauvegardé puis arrêté proprement par RCON.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostApplyPackAsync(string name, Guid projectId, CancellationToken cancellationToken)
    {
        var project = projectStore.Get(projectId);
        if (project is null) return BadRequest("Pack non reconnu.");
        try
        {
            var result = await servers.ApplyPackageAsync(name, project, cancellationToken);
            TempData["Message"] = $"Pack « {project.Name} » appliqué : {result.WorkshopItems.Count} Workshop ID, {result.Mods.Count} Mod IDs. Sauvegarde : {result.BackupPath}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public IReadOnlyList<string> Mods => Summary.Mods;
    public IReadOnlyList<string> WorkshopItems => Summary.WorkshopItems;
    public IReadOnlyList<string> Maps => Summary.Maps;

    public sealed class GuidedServerForm
    {
        [StringLength(64)] public string PublicName { get; set; } = string.Empty;
        [StringLength(256)] public string PublicDescription { get; set; } = string.Empty;
        public bool Public { get; set; }
        public bool Open { get; set; } = true;
        public string Password { get; set; } = string.Empty;
        [Range(1, 1000)] public int MaxPlayers { get; set; } = 16;
        [Range(1, 65535)] public int DefaultPort { get; set; } = 16261;
        [Range(1, 65535)] public int RconPort { get; set; } = 27015;
        public string RconPassword { get; set; } = string.Empty;
        public bool PauseEmpty { get; set; } = true;
        public bool DoLuaChecksum { get; set; } = true;
        public bool Pvp { get; set; } = true;
        public bool SafetySystem { get; set; } = true;
        public bool SleepAllowed { get; set; } = true;
        public bool SleepNeeded { get; set; }
        [Range(0, 1440)] public int SaveWorldEveryMinutes { get; set; }
        [Range(0, 100)] public int BackupsCount { get; set; } = 5;
        public string WorkshopItems { get; set; } = string.Empty;
        public string Mods { get; set; } = string.Empty;
        public string Map { get; set; } = "Muldraugh, KY";

        public static GuidedServerForm From(ServerConfigDocument document) => new()
        {
            PublicName = document.Get("PublicName"),
            PublicDescription = document.Get("PublicDescription"),
            Public = ParseBool(document.Get("Public")),
            Open = ParseBool(document.Get("Open"), true),
            Password = document.Get("Password"),
            MaxPlayers = ParseInt(document.Get("MaxPlayers"), 16),
            DefaultPort = ParseInt(document.Get("DefaultPort"), 16261),
            RconPort = ParseInt(document.Get("RCONPort"), 27015),
            RconPassword = document.Get("RCONPassword"),
            PauseEmpty = ParseBool(document.Get("PauseEmpty"), true),
            DoLuaChecksum = ParseBool(document.Get("DoLuaChecksum"), true),
            Pvp = ParseBool(document.Get("PVP"), true),
            SafetySystem = ParseBool(document.Get("SafetySystem"), true),
            SleepAllowed = ParseBool(document.Get("SleepAllowed"), true),
            SleepNeeded = ParseBool(document.Get("SleepNeeded")),
            SaveWorldEveryMinutes = ParseInt(document.Get("SaveWorldEveryMinutes"), 0),
            BackupsCount = ParseInt(document.Get("BackupsCount"), 5),
            WorkshopItems = document.Get("WorkshopItems"),
            Mods = document.Get("Mods"),
            Map = document.Get("Map")
        };

        public IReadOnlyDictionary<string, string> ToValues() => new Dictionary<string, string>
        {
            ["PublicName"] = PublicName?.Trim() ?? string.Empty,
            ["PublicDescription"] = PublicDescription?.Trim() ?? string.Empty,
            ["Public"] = Public.ToString().ToLowerInvariant(),
            ["Open"] = Open.ToString().ToLowerInvariant(),
            ["Password"] = Password ?? string.Empty,
            ["MaxPlayers"] = MaxPlayers.ToString(),
            ["DefaultPort"] = DefaultPort.ToString(),
            ["RCONPort"] = RconPort.ToString(),
            ["RCONPassword"] = RconPassword ?? string.Empty,
            ["PauseEmpty"] = PauseEmpty.ToString().ToLowerInvariant(),
            ["DoLuaChecksum"] = DoLuaChecksum.ToString().ToLowerInvariant(),
            ["PVP"] = Pvp.ToString().ToLowerInvariant(),
            ["SafetySystem"] = SafetySystem.ToString().ToLowerInvariant(),
            ["SleepAllowed"] = SleepAllowed.ToString().ToLowerInvariant(),
            ["SleepNeeded"] = SleepNeeded.ToString().ToLowerInvariant(),
            ["SaveWorldEveryMinutes"] = SaveWorldEveryMinutes.ToString(),
            ["BackupsCount"] = BackupsCount.ToString(),
            ["WorkshopItems"] = NormalizeList(WorkshopItems),
            ["Mods"] = NormalizeList(Mods),
            ["Map"] = NormalizeList(Map)
        };

        private static bool ParseBool(string value, bool fallback = false) => bool.TryParse(value, out var parsed) ? parsed : fallback;
        private static int ParseInt(string value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
        private static string NormalizeList(string value) => string.Join(';', value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
