using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages.Server;

public class IndexModel(
    PackageProjectStore projectStore,
    ServerProfileService servers,
    SteamCmdInstaller steamCmdInstaller,
    SteamCmdService steamCmd,
    ServerWorldDataStore worldData,
    RconConsoleStore rconConsole,
    ModConflictAnalyzer conflicts) : PageModel
{
    public IReadOnlyList<ServerConfigEntry> Configs { get; private set; } = [];
    public IEnumerable<ServerConfigEntry> HostedConfigs => Configs.Where(config => config.IsHostedLocal);
    public IEnumerable<ServerConfigEntry> DedicatedConfigs => Configs.Where(config => config.IsDedicatedLocal);
    public IEnumerable<ServerConfigEntry> RemoteConfigs => Configs.Where(config => config.IsRemote);
    public ServerConfigEntry? Selected { get; private set; }
    public ServerConfigSummary Summary { get; private set; } = new([], [], []);
    public IReadOnlyList<PackageProject> Projects { get; private set; } = [];
    public ServerModAudit? ModAudit { get; private set; }
    public bool SelectedServerOnline { get; private set; }
    public bool SelectedRconAvailable { get; private set; }
    public ServerRuntimeSnapshot SelectedRuntime { get; private set; } = StoppedRuntime();
    public ServerNetworkInfo? NetworkInfo { get; private set; }
    public IReadOnlyList<RconConsoleEntry> RconHistory { get; private set; } = [];
    public bool PlayerPasswordConfigured { get; private set; }
    public bool RconPasswordConfigured { get; private set; }
    public ServerWorldDataStatus? WorldDataStatus { get; private set; }
    public InitialAdminAccountStatus AdminAccountStatus { get; private set; } = new(InitialAdminAccountState.Unknown, "État non vérifié.");
    public IReadOnlyList<ServerWorldBackupInfo> WorldBackups { get; private set; } = [];
    public PineServerInfo? PineServer { get; private set; }
    public IReadOnlyList<PineBackupInfo> PineBackups { get; private set; } = [];
    public string WorldDataError { get; private set; } = string.Empty;
    public bool SelectedServerCanStart => Selected is not null
        && (Selected.IsDedicatedLocal
            || Selected.IsPineHosting
            || Selected.IsRemote && Selected.Remote!.HasSshConnection && !string.IsNullOrWhiteSpace(Selected.Remote.StartCommand));
    public bool SelectedControlAvailable => SelectedRconAvailable || Selected?.IsPineHosting == true && SelectedServerOnline;
    public bool SelectedServerCanForceStop => Selected is { IsRemote: false }
        && SelectedRuntime.IsRunning
        && !SelectedRuntime.IsRconAuthenticated
        && SelectedRuntime.Instances.Any(instance => instance.Origin == ServerRuntimeOrigin.LocalDedicated);
    public bool InitialAdminPasswordRequired => Selected is { IsRemote: false } && AdminAccountStatus.IsRequired;
    public string ConnectionError { get; private set; } = string.Empty;
    public string SandboxError { get; private set; } = string.Empty;
    public IReadOnlyList<StructuredServerSetting> AllSettings { get; private set; } = [];
    public IReadOnlyList<StructuredServerSetting> SandboxSettings { get; private set; } = [];
    public string SandboxRaw { get; private set; } = string.Empty;
    public string SpawnRegionsRaw { get; private set; } = string.Empty;
    public string SpawnPointsRaw { get; private set; } = string.Empty;
    public StructuredSettingsEditorModel IniEditor => new(AllSettings, "ini-settings-catalog", "RCON, anti-cheat, safehouse, voix…", "Aucune clé INI n'a pu être lue.");
    public StructuredSettingsEditorModel SandboxEditor => new(SandboxSettings, "sandbox-settings-catalog", "population, loot, érosion, véhicule, mod…", SandboxError);
    public string RuntimeStatusText => RuntimeStatus(SelectedRuntime);
    public string RuntimeStatusDetail => RuntimeDetail(SelectedRuntime);
    public string RuntimeStatusCss => RuntimeCss(SelectedRuntime.State);
    public string RuntimeDetectionSource => Selected?.IsPineHosting == true
        ? "Supervision directe par l'API Pine Hosting"
        : Selected?.IsRemote == true
        ? "Supervision distante par RCON"
        : SelectedRuntime.Instances.Count > 1
            ? "Plusieurs instances locales détectées"
        : SelectedRuntime.IsManagedByCurrentSession
            ? "Serveur dédié lancé par le manager"
            : SelectedRuntime.Origin == ServerRuntimeOrigin.LocalDedicated
                ? "Serveur dédié local redécouvert"
                : SelectedRuntime.Origin == ServerRuntimeOrigin.LocalHostedSession
                    ? "Session hébergée par le client PZ"
                    : SelectedRuntime.IsRunning
                        ? "Processus serveur redécouvert sur la machine"
                        : "Aucun processus associé au profil";
    public string RuntimeLogSource => SelectedRuntime.Origin == ServerRuntimeOrigin.LocalHostedSession
        ? "coop-console.txt"
        : Selected?.IsPineHosting == true
            ? "État Pine Hosting API"
            : Selected?.IsRemote == true
            ? "RCON distant"
            : "server-console.txt";
    [BindProperty] public string RawContent { get; set; } = string.Empty;
    [BindProperty] public GuidedServerForm Guided { get; set; } = new();
    [BindProperty] public RemoteServerForm Remote { get; set; } = new();
    [BindProperty] public RemoteServerForm NewRemote { get; set; } = new() { Provider = RemoteServerProvider.PineHosting, RemoteIniPath = PineHostingClient.DefaultIniPath };

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
                ModAudit = BuildModAudit(Projects, Summary);
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
            SelectedRuntime = await servers.ReadRuntimeAsync(Selected.Name, cancellationToken);
            SelectedRconAvailable = SelectedRuntime.IsRconAuthenticated;
            SelectedServerOnline = SelectedRuntime.IsRunning;
            if (ModAudit is not null) ModAudit = ModAudit with { RuntimeFindings = AnalyzeRuntimeModFindings(SelectedRuntime) };
        }
        catch (Exception exception) { ConnectionError = string.IsNullOrWhiteSpace(ConnectionError) ? exception.Message : ConnectionError + " " + exception.Message; }
        if (!Selected.IsRemote)
        {
            try
            {
                var location = servers.ResolveWorldDataLocation(Selected.Name);
                WorldDataStatus = worldData.Inspect(location);
                AdminAccountStatus = worldData.InspectInitialAdminAccount(location);
                WorldBackups = worldData.List(Selected.Name);
            }
            catch (Exception exception) { WorldDataError = exception.Message; }
        }
        else if (Selected.IsPineHosting)
        {
            try
            {
                PineServer = await servers.ReadPineServerAsync(Selected.Name, cancellationToken);
                PineBackups = await servers.ListPineBackupsAsync(Selected.Name, cancellationToken);
            }
            catch (Exception exception) { WorldDataError = exception.Message; }
        }
    }

    public async Task<IActionResult> OnGetRuntimeAsync(string name, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, no-cache";
        try
        {
            var profile = servers.Get(name);
            var runtime = await servers.ReadRuntimeAsync(profile.Name, cancellationToken);
            return new JsonResult(new
            {
                state = runtime.State.ToString(),
                status = RuntimeStatus(runtime),
                detail = RuntimeDetail(runtime),
                cssClass = RuntimeCss(runtime.State),
                runtime.IsRunning,
                runtime.IsGameReady,
                runtime.IsRconAuthenticated,
                runtime.RconBindFailed,
                runtime.IsManagedByCurrentSession,
                runtime.ProcessId,
                runtime.InactiveHostedHelperCount,
                origin = runtime.Origin.ToString(),
                instances = runtime.Instances.Select(instance => new
                {
                    instance.ProcessId,
                    instance.ParentProcessId,
                    origin = instance.Origin.ToString(),
                    label = RuntimeOriginLabel(instance.Origin),
                    startedAt = instance.StartedAt?.ToString("O"),
                    instance.ExecutablePath
                }),
                startedAt = runtime.StartedAt?.ToString("O"),
                lastOutputAt = runtime.LastOutputAt?.ToString("O"),
                source = profile.IsPineHosting
                    ? "Supervision directe par l'API Pine Hosting"
                    : profile.IsRemote
                    ? "Supervision distante par RCON"
                    : runtime.Instances.Count > 1
                        ? "Plusieurs instances locales détectées"
                    : runtime.IsManagedByCurrentSession
                        ? "Serveur dédié lancé par le manager"
                        : runtime.Origin == ServerRuntimeOrigin.LocalDedicated
                            ? "Serveur dédié local redécouvert"
                            : runtime.Origin == ServerRuntimeOrigin.LocalHostedSession
                                ? "Session hébergée par le client PZ"
                                : runtime.IsRunning
                                    ? "Processus serveur redécouvert sur la machine"
                                    : "Aucun processus associé au profil",
                output = runtime.Output.Select(line => new
                {
                    line.Sequence,
                    timestamp = line.Timestamp?.ToString("O"),
                    line.Stream,
                    line.Message,
                    line.Level
                })
            });
        }
        catch (Exception exception)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return new JsonResult(new { error = exception.Message });
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

    public IActionResult OnPostCreate(string serverName, LocalServerMode localMode = LocalServerMode.Dedicated)
    {
        try
        {
            var profile = servers.Create(serverName, localMode);
            return RedirectToPage(new { name = profile.Name });
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToPage();
        }
    }

    public IActionResult OnPostSetLocalMode(string name, LocalServerMode localMode)
    {
        try
        {
            servers.SetLocalMode(name, localMode);
            TempData["Message"] = localMode == LocalServerMode.Dedicated
                ? $"« {name} » est maintenant géré comme serveur dédié local (AppID 380870)."
                : $"« {name} » est maintenant géré comme profil Host local du jeu.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostCreateRemoteAsync(bool createConfigIfMissing, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryValidateHandlerModel(NewRemote, nameof(NewRemote))) throw new ValidationException("Vérifiez le nom et les paramètres du fournisseur distant.");
            var profile = await servers.CreateRemoteAsync(NewRemote.ToConnection(), createConfigIfMissing, cancellationToken);
            TempData["Message"] = profile.IsPineHosting
                ? $"Serveur Pine Hosting « {profile.Remote!.ProviderServerName} » ajouté avec accès complet à la configuration, aux contrôles et aux sauvegardes."
                : $"Profil distant RCON « {profile.Name} » ajouté." + (profile.Remote!.HasSshConnection ? " Connexion SSH facultative vérifiée." : string.Empty);
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
            if (!TryValidateHandlerModel(Remote, nameof(Remote))) throw new ValidationException("Vérifiez les paramètres du fournisseur distant.");
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
            if (!TryValidateHandlerModel(Remote, nameof(Remote))) throw new ValidationException("Vérifiez les paramètres du fournisseur distant.");
            var connection = Remote.ToConnection();
            connection.Name = name;
            await servers.TestRemoteAsync(connection, cancellationToken);
            TempData["Message"] = connection.IsPineHosting
                ? $"API Pine Hosting opérationnelle pour le serveur « {connection.ApiServerIdentifier} »."
                : $"Connexion SSH vers « {name} » opérationnelle.";
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
                var adminStatus = worldData.InspectInitialAdminAccount(servers.ResolveWorldDataLocation(name));
                if (adminStatus.IsConfigured)
                {
                    initialAdminPassword = null;
                    initialAdminPasswordConfirmation = null;
                }
                else
                {
                    if (!string.Equals(initialAdminPassword, initialAdminPasswordConfirmation, StringComparison.Ordinal))
                        throw new InvalidOperationException("Les deux saisies du mot de passe administrateur initial ne correspondent pas.");
                    if (adminStatus.IsRequired && string.IsNullOrEmpty(initialAdminPassword))
                        throw new InvalidOperationException("Aucun compte « admin » n’existe dans la base joueurs. Saisissez et confirmez son mot de passe pour permettre la première initialisation non interactive.");
                }
                var workshopItems = servers.ReadSummary(name).WorkshopItems;
                if (workshopItems.Count > 0)
                {
                    var downloadContext = new PackageProject();
                    downloadContext.Automation.AnonymousWorkshopDownloads = true;
                    foreach (var value in workshopItems)
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
            TempData["Message"] = servers.Get(name).IsPineHosting
                ? $"Serveur Pine « {name} » sauvegardé puis arrêté proprement via la console et l'API du fournisseur."
                : $"Serveur « {name} » sauvegardé puis arrêté proprement par RCON.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostForceStopLocalAsync(string name, bool confirmationAcknowledged, CancellationToken cancellationToken)
    {
        try
        {
            if (!confirmationAcknowledged)
                throw new InvalidOperationException("Confirmez explicitement l'arrêt forcé dans le dialogue du manager.");
            var result = await servers.ForceStopLocalDedicatedAsync(name, cancellationToken);
            TempData["Message"] = $"Processus dédié « {name} » terminé de force. PID : {string.Join(", ", result.ProcessIds)}. Vérifiez l'intégrité du monde avant le prochain démarrage.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostRestartRconAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await servers.RestartViaRconAsync(name, cancellationToken);
            TempData["Message"] = servers.Get(name).IsPineHosting
                ? $"Serveur Pine « {name} » sauvegardé puis redémarré via l'API du fournisseur."
                : $"Serveur « {name} » sauvegardé puis commande quit envoyée par RCON. Le superviseur configuré doit relancer le processus Project Zomboid.";
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

    public async Task<IActionResult> OnPostCreatePineBackupAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var backup = await servers.CreatePineBackupAsync(name, cancellationToken: cancellationToken);
            TempData["Message"] = $"Sauvegarde Pine « {backup.Name} » terminée et vérifiée ({FormatBytes(backup.Bytes)}).";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostRestorePineBackupAsync(string name, string backupUuid, bool createSafetyBackup, bool confirmationAcknowledged, CancellationToken cancellationToken)
    {
        try
        {
            if (!confirmationAcknowledged) throw new InvalidOperationException("Confirmez explicitement la restauration dans le dialogue du manager.");
            await servers.RestorePineBackupAsync(name, backupUuid, createSafetyBackup, cancellationToken: cancellationToken);
            TempData["Message"] = "Restauration transmise à Pine Hosting. Attendez sa finalisation dans le panel avant de redémarrer le serveur.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostLockPineBackupAsync(string name, string backupUuid, bool locked, CancellationToken cancellationToken)
    {
        try
        {
            await servers.SetPineBackupLockAsync(name, backupUuid, locked, cancellationToken);
            TempData["Message"] = locked ? "Sauvegarde Pine protégée contre la suppression." : "Verrou de la sauvegarde Pine retiré.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostDeletePineBackupAsync(string name, string backupUuid, bool confirmationAcknowledged, CancellationToken cancellationToken)
    {
        try
        {
            if (!confirmationAcknowledged) throw new InvalidOperationException("Confirmez explicitement la suppression dans le dialogue du manager.");
            await servers.DeletePineBackupAsync(name, backupUuid, cancellationToken);
            TempData["Message"] = "Sauvegarde Pine supprimée.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnGetDownloadPineBackupAsync(string name, string backupUuid, CancellationToken cancellationToken)
    {
        try { return Redirect((await servers.GetPineBackupDownloadUriAsync(name, backupUuid, cancellationToken)).ToString()); }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToPage(new { name });
        }
    }

    public async Task<IActionResult> OnPostResetPineWorldAsync(string name, bool createSafetyBackup, bool confirmationAcknowledged, CancellationToken cancellationToken)
    {
        try
        {
            if (!confirmationAcknowledged) throw new InvalidOperationException("Confirmez explicitement le fresh start dans le dialogue du manager.");
            var result = await servers.ResetPineWorldAsync(name, createSafetyBackup, cancellationToken: cancellationToken);
            TempData["Message"] = result.SafetyBackup is null
                ? "Monde et base joueurs Pine retirés sans sauvegarde préalable."
                : $"Fresh start Pine terminé après vérification de la sauvegarde « {result.SafetyBackup.Name} ».";
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
            if (project.PublishedWorkshopId == 0 || project.LastBuiltAt is null)
                throw new InvalidOperationException("Construisez et publiez le pack avant de l'appliquer. Project Zomboid ne transmet pas les mods locaux aux clients.");
            var result = await servers.ApplyPackageAsync(name, project, cancellationToken);
            TempData["Message"] = $"Pack « {project.Name} » appliqué : {result.WorkshopItems.Count} Workshop ID, {result.Mods.Count} Mod IDs. Sauvegarde : {result.BackupPath}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostOptimizeAndApplyPackAsync(string name, Guid projectId, CancellationToken cancellationToken)
    {
        try
        {
            var project = projectStore.Get(projectId) ?? throw new InvalidOperationException("Pack introuvable.");
            await EnsureConfigurationCanBeWrittenAsync(name, cancellationToken);
            var analysis = conflicts.Analyze(project, refresh: true);
            var rank = analysis.RecommendedModOrder.Select((modId, index) => (modId, index)).ToDictionary(item => item.modId, item => item.index);
            var disabledRank = rank.Count;
            foreach (var mod in project.Mods) mod.Order = rank.TryGetValue(mod.Id, out var position) ? position : disabledRank++;
            project.Mods = project.Mods.OrderBy(mod => mod.Order).ToList();
            project.MapOrder = analysis.RecommendedMapOrder.ToList();
            projectStore.Save(project);
            var result = await servers.ApplyPackageAsync(name, project, cancellationToken);
            TempData["Message"] = $"Ordre recommand\u00e9 calcul\u00e9 puis appliqu\u00e9 au pack et au serveur. Sauvegarde INI : {result.BackupPath}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { name, view = "deployment" });
    }

    public IReadOnlyList<string> Mods => Summary.Mods;
    public IReadOnlyList<string> WorkshopItems => Summary.WorkshopItems;
    public IReadOnlyList<string> Maps => Summary.Maps;

    private ServerModAudit? BuildModAudit(IReadOnlyList<PackageProject> projects, ServerConfigSummary summary)
    {
        var workshopIds = summary.WorkshopItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var project = projects
            .Where(candidate => candidate.PublishedWorkshopId != 0 && workshopIds.Contains(candidate.PublishedWorkshopId.ToString()))
            .OrderByDescending(candidate => candidate.Mods.Count(mod => mod.Enabled && summary.Mods.Contains(mod.ModId, StringComparer.OrdinalIgnoreCase)))
            .FirstOrDefault();
        if (project is null) return null;

        var expectedMods = project.Mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Order).Select(mod => mod.ModId).ToList();
        if (project.InjectConnectionNotice) expectedMods.Add(project.NoticeModId);
        if (project.InjectInGameControl) expectedMods.Add(project.ControlModId);
        var actualMods = summary.Mods.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        var missing = expectedMods.Where(modId => !actualMods.Contains(modId, StringComparer.OrdinalIgnoreCase)).ToArray();
        var extra = actualMods.Where(modId => !expectedMods.Contains(modId, StringComparer.OrdinalIgnoreCase)).ToArray();
        var commonActual = actualMods.Where(modId => expectedMods.Contains(modId, StringComparer.OrdinalIgnoreCase)).ToArray();
        var commonExpected = expectedMods.Where(modId => actualMods.Contains(modId, StringComparer.OrdinalIgnoreCase)).ToArray();
        var orderMatches = commonActual.SequenceEqual(commonExpected, StringComparer.OrdinalIgnoreCase);
        var expectedMaps = project.MapOrder.Count > 0 ? project.MapOrder : conflicts.Analyze(project).RecommendedMapOrder.ToList();
        var mapsMatch = summary.Maps.SequenceEqual(expectedMaps, StringComparer.OrdinalIgnoreCase);
        return new ServerModAudit(project, conflicts.Analyze(project), expectedMods, missing, extra, orderMatches, mapsMatch, []);
    }

    private static IReadOnlyList<string> AnalyzeRuntimeModFindings(ServerRuntimeSnapshot runtime) => runtime.Output
        .Select(line => line.Message.Trim())
        .Where(message =>
            message.Contains("loadModAndRequired", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("required mod", StringComparison.OrdinalIgnoreCase) && message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Lua((MOD:", StringComparison.OrdinalIgnoreCase) && message.Contains("require(", StringComparison.OrdinalIgnoreCase) && message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("mod.info", StringComparison.OrdinalIgnoreCase) && (message.Contains("error", StringComparison.OrdinalIgnoreCase) || message.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .TakeLast(80)
        .ToArray();

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

    private static ServerRuntimeSnapshot StoppedRuntime()
        => new(ServerRuntimeState.Stopped, false, false, false, false, false, null, null, null, []);

    private static string RuntimeStatus(ServerRuntimeSnapshot runtime) => runtime.State switch
    {
        ServerRuntimeState.MultipleInstances => "CONFLIT · PLUSIEURS SERVEURS ACTIFS",
        ServerRuntimeState.Online when runtime.Origin == ServerRuntimeOrigin.RemoteRcon => "SERVEUR DISTANT EN LIGNE · RCON OK",
        ServerRuntimeState.Online when runtime.Origin == ServerRuntimeOrigin.LocalHostedSession => "SESSION HÉBERGÉE ACTIVE · RCON OK",
        ServerRuntimeState.Online => "SERVEUR DÉDIÉ EN LIGNE · RCON OK",
        ServerRuntimeState.OnlineWithoutRcon when runtime.Origin == ServerRuntimeOrigin.LocalHostedSession => "SESSION HÉBERGÉE ACTIVE",
        ServerRuntimeState.OnlineWithoutRcon => "SERVEUR DÉDIÉ ACTIF · RCON INDISPONIBLE",
        ServerRuntimeState.StartingSlow => "DÉMARRAGE LENT · À VÉRIFIER",
        ServerRuntimeState.Starting when runtime.Origin == ServerRuntimeOrigin.LocalHostedSession => "SESSION HÉBERGÉE · INITIALISATION",
        ServerRuntimeState.Starting => "SERVEUR DÉDIÉ · INITIALISATION",
        _ => "ARRÊTÉ · AUCUN SERVEUR PZ"
    };

    private static string RuntimeDetail(ServerRuntimeSnapshot runtime) => runtime.State switch
    {
        ServerRuntimeState.MultipleInstances => "Plusieurs processus serveur utilisent le même profil. Le serveur dédié et la session hébergée sont affichés séparément ci-dessous; leurs ports peuvent entrer en conflit.",
        ServerRuntimeState.Online when runtime.Origin == ServerRuntimeOrigin.RemoteRcon => "Le serveur distant répond et l'authentification RCON fonctionne.",
        ServerRuntimeState.Online when runtime.Origin == ServerRuntimeOrigin.LocalHostedSession => "La session multijoueur hébergée par le client Project Zomboid est prête.",
        ServerRuntimeState.Online => "Le serveur dédié local est prêt et l'authentification RCON fonctionne.",
        ServerRuntimeState.OnlineWithoutRcon when runtime.RconBindFailed => "Le serveur dédié est prêt, mais Project Zomboid n'a pas pu ouvrir le port RCON car il est déjà utilisé par un autre processus.",
        ServerRuntimeState.OnlineWithoutRcon when runtime.Origin == ServerRuntimeOrigin.LocalHostedSession => "La session multijoueur hébergée par le client Project Zomboid est active.",
        ServerRuntimeState.OnlineWithoutRcon => "Le serveur dédié est prêt sur ses ports de jeu, mais RCON n'est pas authentifié.",
        ServerRuntimeState.StartingSlow => "Le processus existe, mais aucune progression récente ni confirmation de démarrage n'a été détectée. Consultez le journal ci-dessous.",
        ServerRuntimeState.Starting when runtime.Origin == ServerRuntimeOrigin.LocalHostedSession => "La session hébergée initialise les mods, les cartes et le monde depuis le client Project Zomboid.",
        ServerRuntimeState.Starting => "Le serveur dédié initialise actuellement les mods, les cartes et le monde.",
        _ => "Aucun processus zombie.network.GameServer correspondant à ce profil n'est actuellement détecté."
    };

    private static string RuntimeCss(ServerRuntimeState state) => state switch
    {
        ServerRuntimeState.Online => "online",
        ServerRuntimeState.OnlineWithoutRcon => "degraded",
        ServerRuntimeState.Starting or ServerRuntimeState.StartingSlow => "starting",
        ServerRuntimeState.MultipleInstances => "conflict",
        _ => "offline"
    };

    public static string RuntimeOriginLabel(ServerRuntimeOrigin origin) => origin switch
    {
        ServerRuntimeOrigin.LocalDedicated => "Serveur dédié local",
        ServerRuntimeOrigin.LocalHostedSession => "Session hébergée par le jeu",
        ServerRuntimeOrigin.RemoteRcon => "Serveur distant par RCON",
        _ => "Origine indéterminée"
    };

    private static string Limit(string value, int length) => value.Length <= length ? value : value[^length..];

    public sealed record ServerModAudit(
        PackageProject Project,
        ModConflictAnalysis Analysis,
        IReadOnlyList<string> ExpectedMods,
        IReadOnlyList<string> MissingMods,
        IReadOnlyList<string> ExtraMods,
        bool OrderMatches,
        bool MapsMatch,
        IReadOnlyList<string> RuntimeFindings)
    {
        public bool IsAligned => MissingMods.Count == 0 && ExtraMods.Count == 0 && OrderMatches && MapsMatch;
    }

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
        public RemoteServerProvider Provider { get; set; } = RemoteServerProvider.RconSsh;
        [StringLength(255)] public string? ApiBaseUrl { get; set; } = PineHostingClient.DefaultApiBaseUrl;
        [StringLength(512)] public string? ApiToken { get; set; }
        [StringLength(128)] public string? ApiServerIdentifier { get; set; }
        [StringLength(255)] public string? Host { get; set; }
        [Range(1, 65535)] public int SshPort { get; set; } = 22;
        [StringLength(128)] public string? SshUser { get; set; }
        [StringLength(1024)] public string? SshPrivateKeyPath { get; set; }
        [StringLength(2048)] public string? RemoteIniPath { get; set; }
        [StringLength(2048)] public string? StartCommand { get; set; }
        [StringLength(255)] public string? RconHost { get; set; }
        [Range(1, 65535)] public int RconPort { get; set; } = 27015;
        [StringLength(512)] public string? RconPassword { get; set; }
        public bool AutoRestartAfterRconQuit { get; set; } = true;

        public RemoteServerConnection ToConnection() => new()
        {
            Name = Name,
            Provider = Provider,
            ApiBaseUrl = ApiBaseUrl ?? PineHostingClient.DefaultApiBaseUrl,
            ApiToken = ApiToken ?? string.Empty,
            ApiServerIdentifier = ApiServerIdentifier ?? string.Empty,
            Host = Host ?? string.Empty,
            SshPort = SshPort,
            SshUser = SshUser ?? string.Empty,
            SshPrivateKeyPath = SshPrivateKeyPath ?? string.Empty,
            RemoteIniPath = RemoteIniPath ?? string.Empty,
            StartCommand = StartCommand ?? string.Empty,
            RconHost = RconHost ?? string.Empty,
            RconPort = RconPort,
            RconPassword = RconPassword ?? string.Empty,
            AutoRestartAfterRconQuit = AutoRestartAfterRconQuit
        };

        public static RemoteServerForm From(RemoteServerConnection? connection) => connection is null ? new() : new()
        {
            Name = connection.Name,
            Provider = connection.Provider,
            ApiBaseUrl = connection.ApiBaseUrl,
            ApiToken = string.Empty,
            ApiServerIdentifier = connection.ApiServerIdentifier,
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

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.00} Gio",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.0} Mio",
        >= 1024 => $"{bytes / 1024d:0.0} Kio",
        _ => $"{bytes} o"
    };
}
