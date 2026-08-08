using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages.Server;

public class IndexModel(
    PackageProjectStore projectStore,
    ServerProfileService servers,
    SteamCmdInstaller steamCmdInstaller,
    SteamCmdService steamCmd,
    ServerWorldDataStore worldData,
    RconConsoleStore rconConsole) : PageModel
{
    public IReadOnlyList<ServerConfigEntry> Configs { get; private set; } = [];
    public ServerConfigEntry? Selected { get; private set; }
    public ServerConfigSummary Summary { get; private set; } = new([], [], []);
    public IReadOnlyList<PackageProject> Projects { get; private set; } = [];
    public bool SelectedServerOnline { get; private set; }
    public bool SelectedRconAvailable { get; private set; }
    public ServerNetworkInfo? NetworkInfo { get; private set; }
    public IReadOnlyList<RconConsoleEntry> RconHistory { get; private set; } = [];
    public bool PlayerPasswordConfigured { get; private set; }
    public bool RconPasswordConfigured { get; private set; }
    public ServerWorldDataStatus? WorldDataStatus { get; private set; }
    public IReadOnlyList<ServerWorldBackupInfo> WorldBackups { get; private set; } = [];
    public string WorldDataError { get; private set; } = string.Empty;
    public bool SelectedServerCanStart => Selected is not null && (!Selected.IsRemote || Selected.Remote!.HasSshConnection && !string.IsNullOrWhiteSpace(Selected.Remote.StartCommand));
    public bool InitialAdminPasswordRecommended => Selected is { IsRemote: false } && WorldDataStatus?.HasWorld != true;
    public string ConnectionError { get; private set; } = string.Empty;
    public string SandboxError { get; private set; } = string.Empty;
    public IReadOnlyList<StructuredServerSetting> AllSettings { get; private set; } = [];
    public IReadOnlyList<StructuredServerSetting> SandboxSettings { get; private set; } = [];
    public string SandboxRaw { get; private set; } = string.Empty;
    public string SpawnRegionsRaw { get; private set; } = string.Empty;
    public string SpawnPointsRaw { get; private set; } = string.Empty;
    public StructuredSettingsEditorModel IniEditor => new(AllSettings, "ini-settings-catalog", "RCON, anti-cheat, safehouse, voix…", "Aucune clé INI n'a pu être lue.");
    public StructuredSettingsEditorModel SandboxEditor => new(SandboxSettings, "sandbox-settings-catalog", "population, loot, érosion, véhicule, mod…", SandboxError);
    [BindProperty] public string RawContent { get; set; } = string.Empty;
    [BindProperty] public GuidedServerForm Guided { get; set; } = new();
    [BindProperty] public RemoteServerForm Remote { get; set; } = new();
    [BindProperty] public RemoteServerForm NewRemote { get; set; } = new();

    public async Task OnGetAsync(string? name, CancellationToken cancellationToken)
    {
        Configs = servers.List();
        Projects = projectStore.GetAll().Where(x => x.PublishedWorkshopId != 0 && x.LastBuiltAt is not null).ToArray();
        Selected = Configs.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Configs.FirstOrDefault();
        if (Selected is null) return;
        NetworkInfo = servers.ReadNetworkInfo(Selected.Name);
        RconHistory = rconConsole.List(Selected.Name);
        Remote = RemoteServerForm.From(Selected.Remote);
        if (Selected.CanManageConfiguration)
        {
            try
            {
                var document = servers.ReadDocument(Selected.Name);
                RawContent = document.Render();
                AllSettings = StructuredServerSettings.ParseIni(RawContent);
                Summary = new ServerConfigSummary(document.GetList("WorkshopItems"), document.GetList("Mods"), document.GetList("Map"));
                PlayerPasswordConfigured = !string.IsNullOrEmpty(document.Get("Password"));
                RconPasswordConfigured = !string.IsNullOrEmpty(document.Get("RCONPassword"));
                Guided = GuidedServerForm.From(document);
            }
            catch (Exception exception) { ConnectionError = exception.Message; }
        }
        if (Selected.CanManageConfiguration)
        {
            try
            {
                SandboxSettings = servers.ReadSandboxDocument(Selected.Name).Settings;
                SandboxRaw = servers.ReadLuaFile(Selected.Name, ServerLuaFileKind.SandboxVars);
                SpawnRegionsRaw = servers.ReadLuaFile(Selected.Name, ServerLuaFileKind.SpawnRegions);
                SpawnPointsRaw = servers.ReadLuaFile(Selected.Name, ServerLuaFileKind.SpawnPoints);
            }
            catch (Exception exception) { SandboxError = exception.Message; }
        }
        try
        {
            SelectedRconAvailable = await servers.IsRconAuthenticatedAsync(Selected.Name, cancellationToken);
            SelectedServerOnline = SelectedRconAvailable || servers.IsManagerProcessRunning(Selected.Name);
        }
        catch (Exception exception) { ConnectionError = string.IsNullOrWhiteSpace(ConnectionError) ? exception.Message : ConnectionError + " " + exception.Message; }
        if (!Selected.IsRemote)
        {
            try
            {
                var location = servers.ResolveWorldDataLocation(Selected.Name);
                WorldDataStatus = worldData.Inspect(location);
                WorldBackups = worldData.List(Selected.Name);
            }
            catch (Exception exception) { WorldDataError = exception.Message; }
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConfigurationCanBeWrittenAsync(name, cancellationToken);
            var backup = servers.SaveRaw(name, RawContent);
            TempData["Message"] = $"Configuration enregistrée puis relue avec succès. Sauvegarde : {backup}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostSaveGuidedAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryValidateHandlerModel(Guided, nameof(Guided))) throw new ValidationException("Vérifiez les ports, le nombre de joueurs et les valeurs numériques.");
            await EnsureConfigurationCanBeWrittenAsync(name, cancellationToken);
            var current = servers.ReadDocument(name);
            var backup = servers.Update(name, Guided.ToValues(current));
            TempData["Message"] = $"Configuration guidée enregistrée puis relue avec succès, mot de passe RCON inclus. Sauvegarde : {backup}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostSaveAllAsync(string name, List<string> settingKeys, List<string> settingValues, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConfigurationCanBeWrittenAsync(name, cancellationToken);
            if (settingKeys.Count == 0 || settingKeys.Count != settingValues.Count) throw new InvalidDataException("Le formulaire complet de l’INI est incomplet.");
            var current = servers.ReadDocument(name);
            var catalog = servers.ReadStructuredSettings(name).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < settingKeys.Count; index++)
            {
                var key = settingKeys[index];
                if (!catalog.TryGetValue(key, out var setting)) throw new InvalidDataException($"La clé INI « {key} » n’existe plus; rechargez la page.");
                values[key] = StructuredServerSettings.ValidateAndFormat(setting, settingValues[index], current.Get(key));
            }
            var backup = servers.Update(name, values);
            TempData["Message"] = $"{values.Count} réglage(s) INI validé(s), écrits atomiquement puis relus. Sauvegarde : {backup}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name, tab = "all-settings" });
    }

    public async Task<IActionResult> OnPostSaveSandboxAsync(string name, List<string> settingKeys, List<string> settingValues, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConfigurationCanBeWrittenAsync(name, cancellationToken);
            if (settingKeys.Count == 0 || settingKeys.Count != settingValues.Count) throw new InvalidDataException("Le formulaire SandboxVars est incomplet.");
            var values = settingKeys.Select((key, index) => new KeyValuePair<string, string>(key, settingValues[index])).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            var backup = servers.UpdateSandbox(name, values);
            TempData["Message"] = $"{values.Count} SandboxVars validée(s), écrite(s) puis relue(s). Sauvegarde : {backup}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name, tab = "sandbox" });
    }

    public async Task<IActionResult> OnPostSaveLuaFilesAsync(string name, string sandboxContent, string spawnRegionsContent, string spawnPointsContent, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConfigurationCanBeWrittenAsync(name, cancellationToken);
            var backups = new[]
            {
                servers.SaveLuaFile(name, ServerLuaFileKind.SandboxVars, sandboxContent),
                servers.SaveLuaFile(name, ServerLuaFileKind.SpawnRegions, spawnRegionsContent),
                servers.SaveLuaFile(name, ServerLuaFileKind.SpawnPoints, spawnPointsContent)
            }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            TempData["Message"] = backups.Length == 0
                ? "Aucun fichier Lua n’avait changé."
                : $"Fichiers Lua écrits puis relus avec succès. {backups.Length} sauvegarde(s) créée(s).";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name, tab = "lua-files" });
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

    public async Task<IActionResult> OnPostStartAsync(string name, string? initialAdminPassword, string? initialAdminPasswordConfirmation, CancellationToken cancellationToken)
    {
        try
        {
            var profile = servers.Get(name);
            if (!profile.IsRemote)
            {
                if (!string.Equals(initialAdminPassword, initialAdminPasswordConfirmation, StringComparison.Ordinal))
                    throw new InvalidOperationException("Les deux saisies du mot de passe administrateur initial ne correspondent pas.");
                var worldStatus = worldData.Inspect(servers.ResolveWorldDataLocation(name));
                if (!worldStatus.HasWorld && string.IsNullOrEmpty(initialAdminPassword))
                    throw new InvalidOperationException("Ce profil n'a pas encore de monde actif. Saisissez et confirmez le mot de passe du compte « admin » pour permettre son initialisation non interactive.");
                var steamStatus = steamCmdInstaller.GetStatus();
                if (steamStatus.Installed)
                {
                    var downloadContext = new PackageProject();
                    downloadContext.Automation.SteamCmdPath = steamStatus.ExecutablePath;
                    downloadContext.Automation.AnonymousWorkshopDownloads = true;
                    foreach (var value in servers.ReadSummary(name).WorkshopItems)
                    {
                        if (!ulong.TryParse(value, out var workshopId) || workshopId == 0) continue;
                        var availability = await steamCmd.VerifyWorkshopItemAvailableAsync(downloadContext, workshopId, 1, cancellationToken);
                        if (!availability.SteamCmd.Success || !Directory.Exists(availability.ContentRoot) || !Directory.EnumerateFiles(availability.ContentRoot, "*", SearchOption.AllDirectories).Any())
                            throw new InvalidOperationException($"Le serveur n’a pas été lancé : l’item Workshop {workshopId} n’est pas encore téléchargeable anonymement. Une publication peut nécessiter quelques instants de propagation. Vérifiez l’item puis réessayez. Détail : {Limit(availability.SteamCmd.CombinedOutput, 900)}");
                    }
                }
            }
            await servers.StartAsync(name, profile.IsRemote ? null : initialAdminPassword, cancellationToken);
            TempData["Message"] = $"Démarrage du jeu « {name} » demandé. Le statut RCON apparaîtra après son initialisation.";
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message.Contains("onItemNotDownloaded", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("result=2", StringComparison.OrdinalIgnoreCase)
                ? exception.Message + " Le droit de téléchargement anonyme de l'installation dédiée peut être obsolète : utilisez « Mettre à jour et réparer » puis relancez le jeu."
                : exception.Message;
        }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostRepairDedicatedAsync(string name, bool confirmationAcknowledged, CancellationToken cancellationToken)
    {
        try
        {
            TempData["Message"] = await RepairDedicatedAsync(name, confirmationAcknowledged, null, cancellationToken);
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostRepairDedicatedStreamAsync(string name, bool confirmationAcknowledged, CancellationToken cancellationToken)
        => await StreamOperationAsync(
            progress => RepairDedicatedAsync(name, confirmationAcknowledged, progress, cancellationToken),
            Url.Page("/Server/Index", values: new { name }) ?? $"/Server?name={Uri.EscapeDataString(name)}",
            cancellationToken);

    public async Task<IActionResult> OnPostCreateWorldBackupAsync(string name, CancellationToken cancellationToken)
    {
        try { TempData["Message"] = await CreateWorldBackupAsync(name, null, cancellationToken); }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostCreateWorldBackupStreamAsync(string name, CancellationToken cancellationToken)
        => await StreamOperationAsync(
            progress => CreateWorldBackupAsync(name, progress, cancellationToken),
            Url.Page("/Server/Index", values: new { name }) ?? $"/Server?name={Uri.EscapeDataString(name)}",
            cancellationToken);

    public async Task<IActionResult> OnPostRestoreWorldBackupAsync(string name, string backupId, bool restoreConfiguration, bool confirmationAcknowledged, CancellationToken cancellationToken)
    {
        try { TempData["Message"] = await RestoreWorldBackupAsync(name, backupId, restoreConfiguration, confirmationAcknowledged, null, cancellationToken); }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostRestoreWorldBackupStreamAsync(string name, string backupId, bool restoreConfiguration, bool confirmationAcknowledged, CancellationToken cancellationToken)
        => await StreamOperationAsync(
            progress => RestoreWorldBackupAsync(name, backupId, restoreConfiguration, confirmationAcknowledged, progress, cancellationToken),
            Url.Page("/Server/Index", values: new { name }) ?? $"/Server?name={Uri.EscapeDataString(name)}",
            cancellationToken);

    public async Task<IActionResult> OnPostResetWorldAsync(string name, bool createSafetyBackup, bool confirmationAcknowledged, CancellationToken cancellationToken)
    {
        try { TempData["Message"] = await ResetWorldAsync(name, createSafetyBackup, confirmationAcknowledged, null, cancellationToken); }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostResetWorldStreamAsync(string name, bool createSafetyBackup, bool confirmationAcknowledged, CancellationToken cancellationToken)
        => await StreamOperationAsync(
            progress => ResetWorldAsync(name, createSafetyBackup, confirmationAcknowledged, progress, cancellationToken),
            Url.Page("/Server/Index", values: new { name }) ?? $"/Server?name={Uri.EscapeDataString(name)}",
            cancellationToken);

    public IActionResult OnPostDeleteWorldBackup(string name, string backupId, bool confirmationAcknowledged)
    {
        try
        {
            if (!confirmationAcknowledged) throw new InvalidOperationException("Confirmez explicitement la suppression de cette archive dans le dialogue du manager.");
            worldData.Delete(name, backupId);
            TempData["Message"] = $"Sauvegarde {backupId} supprimée du stockage du manager.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public IActionResult OnPostOpenWorldBackupFolder(string name)
    {
        try
        {
            _ = servers.ResolveWorldDataLocation(name);
            var root = worldData.EnsureBackupRoot(name);
            var start = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(root);
            Process.Start(start)?.Dispose();
            TempData["Message"] = $"Dossier des sauvegardes ouvert : {root}";
        }
        catch (Exception exception) { TempData["Error"] = $"Impossible d'ouvrir le dossier des sauvegardes : {exception.Message}"; }
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
        var submittedCommand = command?.Trim() ?? string.Empty;
        try
        {
            if (submittedCommand.Length == 0) throw new ValidationException("Saisissez une commande RCON.");
            var output = await servers.ExecuteRconCommandAsync(name, submittedCommand, cancellationToken);
            var response = string.IsNullOrWhiteSpace(output)
                ? "Commande acceptée sans réponse textuelle."
                : output.Length <= 4000 ? output : output[..4000] + Environment.NewLine + "… réponse tronquée à 4 000 caractères";
            rconConsole.Add(name, submittedCommand, response, succeeded: true);
            TempData["Message"] = "Commande RCON exécutée.";
        }
        catch (Exception exception)
        {
            rconConsole.Add(name, submittedCommand, exception.Message, succeeded: false);
            TempData["Error"] = exception.Message;
        }
        return RedirectToPage(new { name });
    }

    public IActionResult OnPostClearRconConsole(string name)
    {
        rconConsole.Clear(name);
        TempData["Message"] = "Historique de la console RCON effacé.";
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostApplyPackAsync(string name, Guid projectId, CancellationToken cancellationToken)
    {
        var project = projectStore.Get(projectId);
        if (project is null) return BadRequest("Pack non reconnu.");
        try
        {
            if (project.PublishedWorkshopId == 0 || project.LastBuiltAt is null)
                throw new InvalidOperationException("Construisez et publiez le pack avant de l'appliquer. Project Zomboid ne transmet pas les mods locaux aux clients.");
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

    private async Task EnsureConfigurationCanBeWrittenAsync(string name, CancellationToken cancellationToken)
    {
        if (await servers.IsOnlineAsync(name, cancellationToken))
            throw new InvalidOperationException("Arrêtez le processus Project Zomboid avant de modifier son INI. Le jeu peut réécrire le fichier pendant son arrêt et annuler les changements enregistrés à chaud.");
    }

    private async Task<ServerWorldDataLocation> GetStoppedWorldDataLocationAsync(string name, CancellationToken cancellationToken)
    {
        var location = servers.ResolveWorldDataLocation(name);
        if (await servers.IsRconServiceAsync(name, cancellationToken))
            throw new InvalidOperationException("Arrêtez proprement Project Zomboid avant de sauvegarder, restaurer ou réinitialiser les données du monde. Un service RCON Project Zomboid répond encore pour ce profil.");
        return location;
    }

    private async Task<string> CreateWorldBackupAsync(string name, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var location = await GetStoppedWorldDataLocationAsync(name, cancellationToken);
        var backup = await worldData.CreateBackupAsync(location, "manual", progress, cancellationToken);
        return $"Sauvegarde du monde créée : {backup.Id} · {backup.FileCount:N0} fichiers · {ServerWorldDataStore.FormatBytes(backup.ArchiveBytes)}.";
    }

    private async Task<string> RestoreWorldBackupAsync(string name, string backupId, bool restoreConfiguration, bool confirmationAcknowledged, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!confirmationAcknowledged)
            throw new InvalidOperationException("Confirmez explicitement la restauration dans le dialogue du manager avant de remplacer le monde actuel.");
        var location = await GetStoppedWorldDataLocationAsync(name, cancellationToken);
        var result = await worldData.RestoreAsync(location, backupId, restoreConfiguration, progress, cancellationToken);
        var safety = result.SafetyBackup is null ? "Aucune donnée précédente n'existait." : $"Sauvegarde de sécurité créée : {result.SafetyBackup.Id}.";
        return $"Monde restauré depuis {result.RestoredBackup.Id}. {safety}" + (result.ConfigurationRestored ? " La configuration serveur archivée a également été restaurée." : string.Empty);
    }

    private async Task<string> ResetWorldAsync(string name, bool createSafetyBackup, bool confirmationAcknowledged, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!confirmationAcknowledged)
            throw new InvalidOperationException("Confirmez explicitement le fresh start dans le dialogue du manager avant de retirer le monde actuel.");
        var location = await GetStoppedWorldDataLocationAsync(name, cancellationToken);
        var result = await worldData.ResetAsync(location, createSafetyBackup, progress, cancellationToken);
        return result.SafetyBackup is not null
            ? $"Fresh start prêt. Le monde et la base des joueurs seront recréés au prochain démarrage. Sauvegarde de récupération : {result.SafetyBackup.Id}."
            : "Fresh start prêt sans sauvegarde préalable, conformément au choix confirmé. Le monde et la base des joueurs seront recréés au prochain démarrage.";
    }

    private async Task<string> RepairDedicatedAsync(string name, bool confirmationAcknowledged, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!confirmationAcknowledged)
            throw new InvalidOperationException("Confirmez explicitement la maintenance du serveur dédié dans le dialogue du manager avant de lancer SteamCMD.");
        progress?.Report(new OperationProgress("profile", "Vérification du profil local et de l'arrêt complet de Project Zomboid."));
        var profile = servers.Get(name);
        if (profile.IsRemote)
            throw new InvalidOperationException("La maintenance SteamCMD intégrée s'applique uniquement à l'installation dédiée locale.");
        if (await servers.IsOnlineAsync(name, cancellationToken))
            throw new InvalidOperationException("Arrêtez proprement Project Zomboid avant de mettre à jour son installation dédiée.");

        var installation = servers.Installation;
        if (string.IsNullOrWhiteSpace(installation.DedicatedServerRoot))
            throw new DirectoryNotFoundException("Aucune installation locale de Project Zomboid Dedicated Server n'a été détectée.");
        var steamStatus = steamCmdInstaller.GetStatus();
        if (!steamStatus.Installed)
            throw new FileNotFoundException("Installez d'abord SteamCMD depuis le tableau de bord.", steamStatus.ExecutablePath);

        var result = await steamCmd.UpdateDedicatedServerAsync(steamStatus.ExecutablePath, installation.DedicatedServerRoot, cancellationToken, progress);
        if (!result.Success)
            throw new InvalidOperationException($"SteamCMD n'a pas pu mettre à jour le serveur dédié : {Limit(result.CombinedOutput, 1800)}");
        return "Installation dédiée mise à jour et vérifiée par SteamCMD anonyme. Les droits Workshop du serveur ont été rafraîchis.";
    }

    private async Task<IActionResult> StreamOperationAsync(
        Func<IProgress<OperationProgress>, Task<string>> operation,
        string redirectUrl,
        CancellationToken cancellationToken)
    {
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Append("X-Accel-Buffering", "no");
        var channel = Channel.CreateUnbounded<OperationProgress>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var progress = new CallbackProgress<OperationProgress>(value => channel.Writer.TryWrite(value));
        var operationTask = operation(progress);
        _ = operationTask.ContinueWith(_ => channel.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
                await WriteProgressAsync(new { type = "progress", phase = update.Phase, message = update.Message, current = update.Current, total = update.Total }, cancellationToken);
            var message = await operationTask;
            await WriteProgressAsync(new { type = "done", message, redirectUrl }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await WriteProgressAsync(new { type = "error", message = exception.Message }, CancellationToken.None);
        }
        return new EmptyResult();
    }

    private async Task WriteProgressAsync(object value, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(value) + "\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[^length..];

    public sealed class GuidedServerForm
    {
        [StringLength(64)] public string? PublicName { get; set; }
        [StringLength(256)] public string? PublicDescription { get; set; }
        public bool Public { get; set; }
        public bool Open { get; set; } = true;
        public string? Password { get; set; }
        public bool ClearPassword { get; set; }
        [Range(1, 1000)] public int MaxPlayers { get; set; } = 16;
        [Range(1, 65535)] public int DefaultPort { get; set; } = 16261;
        [Range(1, 65535)] public int RconPort { get; set; } = 27015;
        public string? RconPassword { get; set; }
        public bool ClearRconPassword { get; set; }
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
            Password = null,
            MaxPlayers = ParseInt(document.Get("MaxPlayers"), 16),
            DefaultPort = ParseInt(document.Get("DefaultPort"), 16261),
            RconPort = ParseInt(document.Get("RCONPort"), 27015),
            RconPassword = null,
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

        public IReadOnlyDictionary<string, string> ToValues(ServerConfigDocument current) => new Dictionary<string, string>
        {
            ["PublicName"] = PublicName?.Trim() ?? string.Empty,
            ["PublicDescription"] = PublicDescription?.Trim() ?? string.Empty,
            ["Public"] = Public.ToString().ToLowerInvariant(),
            ["Open"] = Open.ToString().ToLowerInvariant(),
            ["Password"] = ClearPassword ? string.Empty : string.IsNullOrEmpty(Password) ? current.Get("Password") : Password,
            ["MaxPlayers"] = MaxPlayers.ToString(),
            ["DefaultPort"] = DefaultPort.ToString(),
            ["RCONPort"] = RconPort.ToString(),
            ["RCONPassword"] = ClearRconPassword ? string.Empty : string.IsNullOrEmpty(RconPassword) ? current.Get("RCONPassword") : RconPassword,
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
