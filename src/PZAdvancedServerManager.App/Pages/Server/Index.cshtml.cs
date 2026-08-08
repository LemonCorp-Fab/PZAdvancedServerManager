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
    public bool SelectedServerCanStart => Selected is not null && (!Selected.IsRemote || Selected.Remote!.HasSshConnection && !string.IsNullOrWhiteSpace(Selected.Remote.StartCommand));
    public string ConnectionError { get; private set; } = string.Empty;
    [BindProperty] public string RawContent { get; set; } = string.Empty;
    [BindProperty] public GuidedServerForm Guided { get; set; } = new();
    [BindProperty] public RemoteServerForm Remote { get; set; } = new();
    [BindProperty] public RemoteServerForm NewRemote { get; set; } = new();

    public async Task OnGetAsync(string? name, CancellationToken cancellationToken)
    {
        Configs = servers.List();
        Projects = projectStore.GetAll();
        Selected = Configs.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Configs.FirstOrDefault();
        if (Selected is null) return;
        Remote = RemoteServerForm.From(Selected.Remote);
        if (Selected.CanManageConfiguration) try
            {
                var document = servers.ReadDocument(Selected.Name);
                RawContent = document.Render();
                Summary = new ServerConfigSummary(document.GetList("WorkshopItems"), document.GetList("Mods"), document.GetList("Map"));
                Guided = GuidedServerForm.From(document);
            }
            catch (Exception exception) { ConnectionError = exception.Message; }
        try { SelectedServerOnline = await servers.IsOnlineAsync(Selected.Name, cancellationToken); }
        catch (Exception exception) { ConnectionError = string.IsNullOrWhiteSpace(ConnectionError) ? exception.Message : ConnectionError + " " + exception.Message; }
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
            if (!TryValidateHandlerModel(Guided, nameof(Guided))) throw new ValidationException("Vérifiez les ports, le nombre de joueurs et les valeurs numériques.");
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

    public async Task<IActionResult> OnPostCreateRemoteAsync(bool createConfigIfMissing, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryValidateHandlerModel(NewRemote, nameof(NewRemote))) throw new ValidationException("Vérifiez le nom et les paramètres RCON du profil distant.");
            var profile = await servers.CreateRemoteAsync(NewRemote.ToConnection(), createConfigIfMissing, cancellationToken);
            TempData["Message"] = $"Profil distant RCON « {profile.Name} » ajouté." + (profile.Remote!.HasSshConnection ? " Connexion SSH facultative vérifiée." : string.Empty);
            return RedirectToPage(new { name = profile.Name });
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostSaveRemoteAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryValidateHandlerModel(Remote, nameof(Remote))) throw new ValidationException("Vérifiez les paramètres SSH et RCON.");
            var connection = Remote.ToConnection();
            connection.Name = name;
            await servers.UpdateRemoteAsync(connection, cancellationToken);
            TempData["Message"] = $"Connexion distante « {name} » enregistrée et testée.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostTestRemoteAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryValidateHandlerModel(Remote, nameof(Remote))) throw new ValidationException("Vérifiez les paramètres SSH et RCON.");
            var connection = Remote.ToConnection();
            connection.Name = name;
            await servers.TestRemoteAsync(connection, cancellationToken);
            TempData["Message"] = $"Connexion SSH vers « {name} » opérationnelle.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostTestRconAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var connection = Remote.ToConnection();
            connection.Name = name;
            await servers.TestRconAsync(connection, cancellationToken);
            TempData["Message"] = $"Project Zomboid a accepté l'authentification RCON du profil « {name} ».";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public IActionResult OnPostDeleteRemote(string name)
    {
        try
        {
            if (!servers.RemoveRemote(name)) throw new KeyNotFoundException("Profil serveur distant introuvable.");
            TempData["Message"] = $"Profil distant « {name} » supprimé. Aucun fichier ni processus distant n'a été modifié.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStartAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await servers.StartAsync(name, cancellationToken);
            TempData["Message"] = $"Démarrage du jeu « {name} » demandé. Le statut RCON apparaîtra après son initialisation.";
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

    public async Task<IActionResult> OnPostRestartRconAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await servers.RestartViaRconAsync(name, cancellationToken);
            TempData["Message"] = $"Serveur « {name} » sauvegardé puis commande quit envoyée par RCON. Le superviseur configuré doit relancer le processus Project Zomboid.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostRconCommandAsync(string name, string command, CancellationToken cancellationToken)
    {
        try
        {
            var output = await servers.ExecuteRconCommandAsync(name, command, cancellationToken);
            TempData["Message"] = $"Commande RCON « {command.Trim()} » exécutée.";
            TempData["RconOutput"] = string.IsNullOrWhiteSpace(output)
                ? "Commande acceptée sans réponse textuelle."
                : output.Length <= 4000 ? output : output[..4000] + Environment.NewLine + "… réponse tronquée à 4 000 caractères";
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

    private bool TryValidateHandlerModel<TModel>(TModel model, string prefix) where TModel : notnull
    {
        var unrelatedKeys = ModelState.Keys
            .Where(key => !key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var key in unrelatedKeys) ModelState.Remove(key);
        ModelState.ClearValidationState(prefix);
        return TryValidateModel(model, prefix);
    }

    public sealed class GuidedServerForm
    {
        [StringLength(64)] public string? PublicName { get; set; }
        [StringLength(256)] public string? PublicDescription { get; set; }
        public bool Public { get; set; }
        public bool Open { get; set; } = true;
        public string? Password { get; set; }
        [Range(1, 1000)] public int MaxPlayers { get; set; } = 16;
        [Range(1, 65535)] public int DefaultPort { get; set; } = 16261;
        [Range(1, 65535)] public int RconPort { get; set; } = 27015;
        public string? RconPassword { get; set; }
        public bool PauseEmpty { get; set; } = true;
        public bool DoLuaChecksum { get; set; } = true;
        public bool Pvp { get; set; } = true;
        public bool SafetySystem { get; set; } = true;
        public bool SleepAllowed { get; set; } = true;
        public bool SleepNeeded { get; set; }
        [Range(0, 1440)] public int SaveWorldEveryMinutes { get; set; }
        [Range(0, 100)] public int BackupsCount { get; set; } = 5;
        public string? WorkshopItems { get; set; }
        public string? Mods { get; set; }
        public string? Map { get; set; } = "Muldraugh, KY";

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
        private static string NormalizeList(string? value) => string.Join(';', (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public sealed class RemoteServerForm
    {
        [Required, StringLength(64)] public string Name { get; set; } = string.Empty;
        [StringLength(255)] public string? Host { get; set; }
        [Range(1, 65535)] public int SshPort { get; set; } = 22;
        [StringLength(128)] public string? SshUser { get; set; }
        [StringLength(1024)] public string? SshPrivateKeyPath { get; set; }
        [StringLength(2048)] public string? RemoteIniPath { get; set; }
        [StringLength(2048)] public string? StartCommand { get; set; }
        [Required, StringLength(255)] public string RconHost { get; set; } = string.Empty;
        [Range(1, 65535)] public int RconPort { get; set; } = 27015;
        [StringLength(512)] public string? RconPassword { get; set; }
        public bool AutoRestartAfterRconQuit { get; set; } = true;

        public RemoteServerConnection ToConnection() => new()
        {
            Name = Name,
            Host = Host ?? string.Empty,
            SshPort = SshPort,
            SshUser = SshUser ?? string.Empty,
            SshPrivateKeyPath = SshPrivateKeyPath ?? string.Empty,
            RemoteIniPath = RemoteIniPath ?? string.Empty,
            StartCommand = StartCommand ?? string.Empty,
            RconHost = RconHost,
            RconPort = RconPort,
            RconPassword = RconPassword ?? string.Empty,
            AutoRestartAfterRconQuit = AutoRestartAfterRconQuit
        };

        public static RemoteServerForm From(RemoteServerConnection? connection) => connection is null ? new() : new()
        {
            Name = connection.Name,
            Host = connection.Host,
            SshPort = connection.SshPort,
            SshUser = connection.SshUser,
            SshPrivateKeyPath = connection.SshPrivateKeyPath,
            RemoteIniPath = connection.RemoteIniPath,
            StartCommand = connection.StartCommand,
            RconHost = connection.RconHost,
            RconPort = connection.RconPort,
            RconPassword = string.Empty,
            AutoRestartAfterRconQuit = connection.AutoRestartAfterRconQuit
        };
    }
}
