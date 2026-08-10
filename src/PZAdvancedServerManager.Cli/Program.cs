using System.Text;
using System.Text.Json;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

Console.OutputEncoding = Encoding.UTF8;
return await new PzasmCli().RunAsync(args);

internal sealed class PzasmCli
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return Help();
            var parsed = CliArguments.Parse(args);
            var paths = new ApplicationPaths(parsed.Get("data-root"));
            var services = new CliServices(paths);
            return args[0].ToLowerInvariant() switch
            {
                "scan" => Scan(services, parsed),
                "projects" => ListProjects(services.Store, parsed),
                "project" => await ProjectAsync(args, parsed, services),
                "server" => await ServerAsync(args, parsed, services),
                "workshop" => await WorkshopAsync(args, parsed, services),
                "steamcmd" => await SteamCmdAsync(args, parsed, services),
                "automation" => await AutomationAsync(args, parsed, services),
                _ => Fail($"Commande inconnue : {args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ERREUR: " + exception.Message);
            return 1;
        }
    }

    private static int Scan(CliServices services, CliArguments args)
    {
        var installation = services.Environment.Installation;
        var mods = services.Environment.GetMods(args.Get("target") ?? "42.20.2");
        if (args.Has("json"))
        {
            WriteJson(new { installation, count = mods.Count, mods });
            return 0;
        }
        Console.WriteLine($"Client       : {installation.ClientRoot ?? "non détecté"}");
        Console.WriteLine($"Serveur      : {installation.DedicatedServerRoot ?? "non détecté"}");
        Console.WriteLine($"Workshop     : {installation.WorkshopRoot ?? "non détecté"}");
        Console.WriteLine($"SteamCMD     : {installation.SteamCmdPath ?? "non détecté"}");
        Console.WriteLine($"Mods logiques: {mods.Count}");
        foreach (var mod in mods) Console.WriteLine($"{mod.WorkshopId,12}  {mod.ModId,-38}  {mod.Name}");
        return 0;
    }

    private static int ListProjects(PackageProjectStore store, CliArguments args)
    {
        var projects = store.GetAll();
        if (args.Has("json")) WriteJson(projects);
        else
        {
            Console.WriteLine($"{projects.Count} projet(s) PZASM");
            foreach (var p in projects)
                Console.WriteLine($"{p.Id}  {p.Mode,-12}  mods={p.Mods.Count(x => x.Enabled),3}  workshop={(p.PublishedWorkshopId == 0 ? "nouveau" : p.PublishedWorkshopId)}  {p.Name}");
        }
        return 0;
    }

    private static async Task<int> ProjectAsync(string[] raw, CliArguments args, CliServices services)
    {
        if (raw.Length < 2) return Fail("Sous-commande requise : create, show, duplicate, delete, add, import-workshop, remove, rights, configure, maps, refresh, validate, build ou publish.");
        var action = raw[1].ToLowerInvariant();
        if (action == "create")
        {
            var project = services.Projects.Create(args.Require("name"));
            if (args.Get("mode") is { } mode) project.Mode = ParseMode(mode);
            services.Store.Save(project);
            WriteJson(new { project.Id, project.Name, project.Mode, project.StableSuffix, projectFile = services.Paths.ProjectFile(project.Id) });
            return 0;
        }

        var current = RequireProject(services.Store, args);
        switch (action)
        {
            case "show":
                WriteJson(current);
                return 0;
            case "duplicate":
                {
                    var clone = services.Projects.Duplicate(current.Id, args.Get("name"));
                    WriteJson(new { clone.Id, clone.Name, clone.StableSuffix, workshopId = clone.PublishedWorkshopId });
                    return 0;
                }
            case "delete":
                if (!args.Has("yes")) return Fail("Suppression non exécutée. Ajoutez --yes pour supprimer le projet, ses snapshots et ses builds locaux.", 3);
                services.Projects.Delete(current.Id);
                Console.WriteLine("Projet PZASM supprimé. Les sources d'origine et le Workshop n'ont pas été touchés.");
                return 0;
            case "add":
                {
                    var discovered = services.Environment.GetMods(current.TargetPzVersion);
                    var modId = args.Require("mod-id");
                    var workshopId = args.GetUlong("workshop-id");
                    var selected = discovered.FirstOrDefault(x => x.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase) && (workshopId is null || x.WorkshopId == workshopId))
                        ?? throw new InvalidOperationException("Mod source introuvable. Exécutez scan et vérifiez --mod-id/--workshop-id.");
                    var count = services.Projects.AddWithDependencies(current, selected, discovered);
                    Console.WriteLine($"{count} mod(s) ajouté(s), dépendances comprises. Total: {current.Mods.Count}.");
                    return 0;
                }
            case "import-workshop":
                {
                    var workshopId = args.GetUlong("workshop-id") ?? throw new ArgumentException("Option --workshop-id requise.");
                    var result = await services.WorkshopImport.ImportAsync(current, workshopId, progress: new CliOperationProgress());
                    WriteJson(result);
                    return 0;
                }
            case "remove":
                {
                    var mod = current.Mods.FirstOrDefault(x => x.ModId.Equals(args.Require("mod-id"), StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Mod absent du projet.");
                    services.Projects.Remove(current, mod.Id);
                    Console.WriteLine($"Mod {mod.ModId} retiré du projet.");
                    return 0;
                }
            case "rights":
                {
                    var mod = current.Mods.FirstOrDefault(x => x.ModId.Equals(args.Require("mod-id"), StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Mod absent du projet.");
                    mod.Permission.Status = Enum.Parse<PermissionStatus>(args.Require("status"), true);
                    mod.Permission.RightsHolder = args.Get("holder") ?? mod.Permission.RightsHolder;
                    mod.Permission.PublicEvidenceUrl = args.Get("evidence-url") ?? mod.Permission.PublicEvidenceUrl;
                    mod.Permission.PrivateAttachmentPath = args.Get("private-proof") ?? mod.Permission.PrivateAttachmentPath;
                    mod.Permission.Notes = args.Get("notes") ?? mod.Permission.Notes;
                    services.Store.Save(current);
                    Console.WriteLine($"Droits mis à jour pour {mod.ModId}: {mod.Permission.Status}.");
                    return 0;
                }
            case "configure":
                Configure(current, args);
                services.Store.Save(current);
                Console.WriteLine("Projet configuré.");
                return 0;
            case "maps":
                {
                    var analysis = services.MapPriority.Analyze(current);
                    if (args.Has("apply-recommended"))
                    {
                        if (!args.Has("yes")) return Fail("Ordre non modifié. Ajoutez --yes avec --apply-recommended pour enregistrer la recommandation.", 3);
                        current.MapOrder = analysis.RecommendedOrder.ToList();
                        services.Store.Save(current);
                    }
                    WriteJson(analysis);
                    return 0;
                }
            case "update-policy":
                {
                    var mod = current.Mods.FirstOrDefault(x => x.ModId.Equals(args.Require("mod-id"), StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Mod absent du projet.");
                    mod.IncludeInGlobalUpdates = bool.Parse(args.Require("enabled"));
                    services.Store.Save(current);
                    Console.WriteLine($"Mise à jour globale pour {mod.ModId}: {(mod.IncludeInGlobalUpdates ? "activée" : "désactivée")}.");
                    return 0;
                }
            case "refresh":
                {
                    var refresh = args.Get("mod-id") is { } modId
                        ? await services.Lifecycle.RefreshModAsync(current, current.Mods.FirstOrDefault(x => x.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))?.Id
                            ?? throw new InvalidOperationException("Mod absent du projet."), progress: new CliOperationProgress())
                        : await services.Lifecycle.RefreshSourcesAsync(current, progress: new CliOperationProgress());
                    current.Automation.LastResult = refresh.CombinedOutput;
                    services.Store.Save(current);
                    Console.WriteLine(refresh.CombinedOutput);
                    return refresh.Success ? 0 : refresh.ExitCode;
                }
            case "validate":
                {
                    var validation = services.Validator.Validate(current);
                    WriteValidation(validation);
                    return validation.CanPublish ? 0 : 2;
                }
            case "build":
                {
                    var result = services.Lifecycle.Build(current);
                    WriteJson(new { result.BuildRoot, result.CopiedFiles, result.CopiedBytes, result.HardLinkedFiles, result.HardLinkedBytes, result.ReusedFiles, result.ReusedBytes, result.RebuiltComponents, result.ReusedComponents, result.RemovedComponents, result.IsIncremental, result.IsNoOp, result.ServerConfigSnippetPath, canPublish = result.Validation.CanPublish });
                    return 0;
                }
            case "publish":
                {
                    if (!args.Has("yes")) return Fail("Publication non exécutée. Ajoutez --yes pour confirmer l'envoi vers Steam Workshop.", 3);
                    if (string.IsNullOrWhiteSpace(current.Automation.CoordinatedServerName))
                        Console.Error.WriteLine("ATTENTION: aucun serveur coordonné; --yes confirme que l'administrateur gère lui-même le redémarrage.");
                    var result = await services.Lifecycle.PublishAsync(current, refreshSources: false, requireCoordinatedServer: false, progress: new CliOperationProgress(), force: args.Has("force"));
                    current.Automation.LastResult = result.Output;
                    services.Store.Save(current);
                    Console.WriteLine(result.Output);
                    Console.WriteLine($"Workshop ID: {current.PublishedWorkshopId}");
                    Console.WriteLine(result.PublicationSkipped
                        ? "Publication: aucun changement local ou distant, SteamCMD non lancé."
                        : $"Publication: {result.PublicationMode}.");
                    return 0;
                }
            default:
                return Fail($"Sous-commande project inconnue : {action}");
        }
    }

    private static async Task<int> ServerAsync(string[] raw, CliArguments args, CliServices services)
    {
        if (raw.Length < 2) return Fail("Sous-commande requise : list, create, show, set, status, start, stop, apply, data-status, backup, backups, restore, reset-world ou delete-backup.");
        var action = raw[1].ToLowerInvariant();
        if (action == "list")
        {
            var configs = services.Servers.List();
            if (args.Has("json")) WriteJson(configs.Select(profile => new
            {
                profile.Name,
                profile.Kind,
                profile.LocalMode,
                profile.Location,
                Provider = profile.Remote?.Provider,
                PineServerId = profile.Remote?.ApiServerIdentifier,
                SshHost = profile.Remote?.Host,
                profile.Remote?.SshPort,
                profile.Remote?.SshUser,
                profile.Remote?.RemoteIniPath,
                RconHost = profile.Remote?.RconHost,
                profile.Remote?.RconPort
            }));
            else foreach (var profile in configs) Console.WriteLine($"{profile.Name,-30} {(profile.IsRemote ? "Remote" : profile.LocalMode),-10} {profile.Location}");
            return 0;
        }

        var name = args.Require("name");
        if (action == "create")
        {
            var mode = Enum.Parse<LocalServerMode>(args.Get("local-mode") ?? "Dedicated", true);
            var profile = services.Servers.Create(name, mode);
            Console.WriteLine($"Profil {profile.LocalMode} créé : {profile.Path}");
            return 0;
        }
        if (action is "create-remote" or "configure-remote")
        {
            var existing = action == "configure-remote"
                ? services.Servers.Get(name).Remote ?? throw new InvalidOperationException("configure-remote requires a remote server profile.")
                : null;
            var providerText = args.Get("provider") ?? existing?.Provider.ToString() ?? "RconSsh";
            var provider = providerText.Equals("pine", StringComparison.OrdinalIgnoreCase)
                ? RemoteServerProvider.PineHosting
                : Enum.Parse<RemoteServerProvider>(providerText, true);
            var connection = new RemoteServerConnection
            {
                Name = name,
                Provider = provider,
                ApiBaseUrl = PineHostingClient.DefaultApiBaseUrl,
                ApiToken = ReadPineApiToken(args, existing?.ApiToken),
                ApiServerIdentifier = args.Get("server-id") ?? existing?.ApiServerIdentifier ?? string.Empty,
                Host = args.Get("ssh-host") ?? existing?.Host ?? string.Empty,
                SshPort = args.GetInt("ssh-port") ?? existing?.SshPort ?? 22,
                SshUser = args.Get("ssh-user") ?? existing?.SshUser ?? string.Empty,
                SshPrivateKeyPath = args.Get("ssh-key") ?? existing?.SshPrivateKeyPath ?? string.Empty,
                RemoteIniPath = args.Get("ini") ?? existing?.RemoteIniPath ?? (provider == RemoteServerProvider.PineHosting ? PineHostingClient.DefaultIniPath : string.Empty),
                StartCommand = args.Get("start-command") ?? existing?.StartCommand ?? string.Empty,
                RconHost = args.Get("rcon-host") ?? existing?.RconHost ?? string.Empty,
                RconPort = args.GetInt("rcon-port") ?? existing?.RconPort ?? 27015,
                RconPassword = args.Get("rcon-password") ?? existing?.RconPassword ?? string.Empty,
                AutoRestartAfterRconQuit = args.Has("no-auto-restart") ? false : args.Has("auto-restart") ? true : existing?.AutoRestartAfterRconQuit ?? true
            };
            if (action == "create-remote")
            {
                var profile = await services.Servers.CreateRemoteAsync(connection, args.Has("create-config"));
                Console.WriteLine($"Remote {profile.Remote!.Provider} profile created: {profile.Location}");
            }
            else
            {
                await services.Servers.UpdateRemoteAsync(connection);
                Console.WriteLine($"Remote {connection.Provider} profile updated: {name}");
            }
            return 0;
        }
        if (action == "delete-remote")
        {
            if (!args.Has("yes")) return Fail("Remote profile deletion was not performed. Add --yes to confirm.", 3);
            if (!services.Servers.RemoveRemote(name)) return Fail("Remote server profile not found.", 4);
            Console.WriteLine($"Remote profile removed without changing the remote host: {name}");
            return 0;
        }
        switch (action)
        {
            case "set-local-mode":
                var localMode = Enum.Parse<LocalServerMode>(args.Require("local-mode"), true);
                services.Servers.SetLocalMode(name, localMode);
                Console.WriteLine($"{name} est maintenant classé comme {localMode} local.");
                return 0;
            case "show":
                Console.WriteLine(services.Servers.ReadRaw(name));
                return 0;
            case "set":
                {
                    if (!args.Has("yes")) return Fail("Modification non exécutée. Ajoutez --yes pour confirmer la sauvegarde et l'écriture.", 3);
                    var key = args.Require("key").Trim();
                    if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Any(char.IsControl)) throw new ArgumentException("Clé de configuration invalide.");
                    var backup = services.Servers.Set(name, key, args.Get("value") ?? string.Empty);
                    Console.WriteLine($"{key} mis à jour. Sauvegarde: {backup}");
                    return 0;
                }
            case "status":
                var runtime = await services.Servers.ReadRuntimeAsync(name);
                if (args.Has("json")) WriteJson(runtime);
                else
                {
                    var process = runtime.ProcessId is int processId ? $" pid={processId}" : string.Empty;
                    var state = runtime.State switch
                    {
                        ServerRuntimeState.MultipleInstances => "multiple-instances",
                        ServerRuntimeState.OnlineWithoutRcon => "online-without-rcon",
                        ServerRuntimeState.StartingSlow => "starting-slow",
                        _ => runtime.State.ToString().ToLowerInvariant()
                    };
                    var origin = runtime.Origin.ToString().ToLowerInvariant();
                    Console.WriteLine($"{state}{process} origin={origin} instances={runtime.Instances.Count} rcon={(runtime.IsRconAuthenticated ? "authenticated" : "unavailable")}");
                }
                return runtime.IsRunning ? 0 : 4;
            case "test-rcon":
                var remote = services.Servers.Get(name).Remote ?? throw new InvalidOperationException("test-rcon is intended for a remote server profile.");
                await services.Servers.TestRconAsync(remote);
                Console.WriteLine($"RCON authentication accepted for {name}.");
                return 0;
            case "rcon":
                var output = await services.Servers.ExecuteRconCommandAsync(name, args.Require("command"));
                Console.WriteLine(string.IsNullOrWhiteSpace(output) ? "Command accepted without textual output." : output);
                return 0;
            case "restart-rcon":
                if (!args.Has("yes")) return Fail("Restart not requested. Add --yes to confirm save/quit through RCON.", 3);
                await services.Servers.RestartViaRconAsync(name);
                Console.WriteLine($"{name} saved and quit through RCON. Its configured supervisor must restart Project Zomboid.");
                return 0;
            case "start":
                await services.Servers.StartAsync(name, ReadInitialAdminPassword(args));
                Console.WriteLine($"Démarrage demandé pour {name}.");
                return 0;
            case "stop":
                if (!args.Has("yes")) return Fail("Arrêt non exécuté. Ajoutez --yes pour confirmer save/quit par RCON.", 3);
                await services.Servers.StopAsync(name);
                Console.WriteLine($"{name} sauvegardé et arrêté proprement.");
                return 0;
            case "force-stop-local":
                if (!args.Has("yes")) return Fail("Arrêt forcé non exécuté. Ajoutez --yes pour confirmer la terminaison sans sauvegarde RCON.", 3);
                var forced = await services.Servers.ForceStopLocalDedicatedAsync(name);
                Console.WriteLine($"Processus dédié {name} terminé de force. PID : {string.Join(", ", forced.ProcessIds)}.");
                return 0;
            case "apply":
                {
                    if (!args.Has("yes")) return Fail("Application non exécutée. Ajoutez --yes pour remplacer WorkshopItems, Mods et Map avec sauvegarde.", 3);
                    var project = RequireProject(services.Store, args);
                    var result = await services.Servers.ApplyPackageAsync(name, project);
                    Console.WriteLine($"Pack appliqué. Sauvegarde: {result.BackupPath}");
                    return 0;
                }
            case "data-status":
                {
                    if (services.Servers.Get(name).IsPineHosting)
                    {
                        var server = await services.Servers.ReadPineServerAsync(name);
                        var pineRuntime = await services.Servers.ReadRuntimeAsync(name);
                        var providerBackups = await services.Servers.ListPineBackupsAsync(name);
                        var pineResult = new { profile = name, provider = "PineHosting", server, pineRuntime.State, pineRuntime.IsRunning, backupCount = providerBackups.Count, backups = providerBackups };
                        if (args.Has("json")) WriteJson(pineResult);
                        else Console.WriteLine($"Pine: {server.Name} ({server.Identifier}) — {pineRuntime.State} — {providerBackups.Count}/{server.BackupLimit} sauvegarde(s)");
                        return 0;
                    }
                    var location = services.Servers.ResolveWorldDataLocation(name);
                    var status = services.WorldData.Inspect(location);
                    var adminAccount = services.WorldData.InspectInitialAdminAccount(location);
                    var backups = services.WorldData.List(name);
                    var result = new
                    {
                        profile = name,
                        status.HasData,
                        status.HasWorld,
                        status.HasDatabase,
                        status.LastModifiedAt,
                        status.WorldPath,
                        status.DatabasePath,
                        adminAccountState = adminAccount.State.ToString(),
                        adminAccount.Detail,
                        backupCount = backups.Count,
                        backupRoot = services.WorldData.GetBackupRoot(name)
                    };
                    if (args.Has("json")) WriteJson(result);
                    else
                    {
                        Console.WriteLine($"Profil: {name}");
                        Console.WriteLine($"Monde: {(status.HasWorld ? "présent" : "absent")} — {status.WorldPath}");
                        Console.WriteLine($"Base joueurs: {(status.HasDatabase ? "présente" : "absente")} — {status.DatabasePath}");
                        Console.WriteLine($"Compte admin: {adminAccount.State} — {adminAccount.Detail}");
                        Console.WriteLine($"Sauvegardes PZASM: {backups.Count}");
                    }
                    return 0;
                }
            case "backups":
                {
                    if (services.Servers.Get(name).IsPineHosting)
                    {
                        var pineBackups = await services.Servers.ListPineBackupsAsync(name);
                        if (args.Has("json")) WriteJson(pineBackups);
                        else if (pineBackups.Count == 0) Console.WriteLine("Aucune sauvegarde Pine Hosting.");
                        else foreach (var backup in pineBackups)
                                Console.WriteLine($"{backup.Uuid}  {backup.CreatedAt:O}  {(backup.IsLocked ? "LOCKED" : ""),-8}  {ServerWorldDataStore.FormatBytes(backup.Bytes),10}  {backup.Name}");
                        return 0;
                    }
                    services.Servers.ResolveWorldDataLocation(name);
                    var backups = services.WorldData.List(name);
                    if (args.Has("json")) WriteJson(backups);
                    else if (backups.Count == 0) Console.WriteLine("Aucune sauvegarde PZASM.");
                    else foreach (var backup in backups)
                            Console.WriteLine($"{backup.Id}  {backup.CreatedAt:O}  {backup.Reason,-12}  {ServerWorldDataStore.FormatBytes(backup.ArchiveBytes),10}  SHA-256 {backup.Sha256[..12]}…");
                    return 0;
                }
            case "backup":
                {
                    if (services.Servers.Get(name).IsPineHosting)
                    {
                        var pineBackup = await services.Servers.CreatePineBackupAsync(name, args.Get("backup-name"), args.Has("lock"), args.Has("json") ? null : new CliOperationProgress());
                        if (args.Has("json")) WriteJson(pineBackup);
                        else Console.WriteLine($"Sauvegarde Pine créée : {pineBackup.Uuid} ({ServerWorldDataStore.FormatBytes(pineBackup.Bytes)})");
                        return 0;
                    }
                    await RequireServerOfflineAsync(services, name);
                    var location = services.Servers.ResolveWorldDataLocation(name);
                    var progress = args.Has("json") ? null : new CliOperationProgress();
                    var backup = await services.WorldData.CreateBackupAsync(location, "manual", progress);
                    if (args.Has("json")) WriteJson(backup);
                    else Console.WriteLine($"Sauvegarde créée : {backup.Id} ({ServerWorldDataStore.FormatBytes(backup.ArchiveBytes)}, SHA-256 {backup.Sha256})");
                    return 0;
                }
            case "restore":
                {
                    if (!args.Has("yes")) return Fail("Restauration non exécutée. Ajoutez --yes pour confirmer le remplacement du monde et de la base de joueurs.", 3);
                    if (services.Servers.Get(name).IsPineHosting)
                    {
                        await services.Servers.RestorePineBackupAsync(name, args.Require("backup"), !args.Has("no-backup"), args.Has("json") ? null : new CliOperationProgress());
                        Console.WriteLine("Restauration Pine acceptée. Attendez sa finalisation avant de redémarrer le serveur.");
                        return 0;
                    }
                    await RequireServerOfflineAsync(services, name);
                    var location = services.Servers.ResolveWorldDataLocation(name);
                    var progress = args.Has("json") ? null : new CliOperationProgress();
                    var result = await services.WorldData.RestoreAsync(location, args.Require("backup"), args.Has("restore-config"), progress);
                    if (args.Has("json")) WriteJson(result);
                    else
                    {
                        Console.WriteLine($"Sauvegarde restaurée : {result.RestoredBackup.Id}");
                        if (result.SafetyBackup is not null) Console.WriteLine($"Sauvegarde de sécurité préalable : {result.SafetyBackup.Id}");
                        Console.WriteLine(result.ConfigurationRestored ? "Configuration restaurée sur demande." : "Configuration actuelle conservée.");
                    }
                    return 0;
                }
            case "reset-world":
                {
                    if (!args.Has("yes")) return Fail("Remise à zéro non exécutée. Ajoutez --yes pour confirmer le fresh start du monde et des joueurs.", 3);
                    if (services.Servers.Get(name).IsPineHosting)
                    {
                        var pineReset = await services.Servers.ResetPineWorldAsync(name, !args.Has("no-backup"), args.Has("json") ? null : new CliOperationProgress());
                        if (args.Has("json")) WriteJson(pineReset);
                        else Console.WriteLine(pineReset.SafetyBackup is null ? "Fresh start Pine terminé sans sauvegarde préalable." : $"Fresh start Pine terminé. Sauvegarde : {pineReset.SafetyBackup.Uuid}");
                        return 0;
                    }
                    await RequireServerOfflineAsync(services, name);
                    var location = services.Servers.ResolveWorldDataLocation(name);
                    var progress = args.Has("json") ? null : new CliOperationProgress();
                    var result = await services.WorldData.ResetAsync(location, !args.Has("no-backup"), progress);
                    if (args.Has("json")) WriteJson(result);
                    else Console.WriteLine(result.SafetyBackup is not null
                        ? $"Fresh start prêt. Sauvegarde de sécurité : {result.SafetyBackup.Id}"
                        : "Fresh start prêt sans sauvegarde préalable, conformément à l'option --no-backup.");
                    return 0;
                }
            case "delete-backup":
                {
                    if (!args.Has("yes")) return Fail("Suppression non exécutée. Ajoutez --yes pour confirmer la suppression définitive de l'archive.", 3);
                    if (services.Servers.Get(name).IsPineHosting)
                    {
                        await services.Servers.DeletePineBackupAsync(name, args.Require("backup"));
                        Console.WriteLine("Sauvegarde Pine supprimée.");
                        return 0;
                    }
                    services.Servers.ResolveWorldDataLocation(name);
                    var backupId = args.Require("backup");
                    services.WorldData.Delete(name, backupId);
                    Console.WriteLine($"Sauvegarde supprimée : {backupId}");
                    return 0;
                }
            case "lock-backup":
                if (!services.Servers.Get(name).IsPineHosting) return Fail("Le verrou fournisseur est disponible uniquement pour Pine Hosting.", 4);
                await services.Servers.SetPineBackupLockAsync(name, args.Require("backup"), !args.Has("unlock"));
                Console.WriteLine(args.Has("unlock") ? "Sauvegarde Pine déverrouillée." : "Sauvegarde Pine verrouillée.");
                return 0;
            case "download-backup":
                if (!services.Servers.Get(name).IsPineHosting) return Fail("L'URL fournisseur est disponible uniquement pour Pine Hosting.", 4);
                Console.WriteLine(await services.Servers.GetPineBackupDownloadUriAsync(name, args.Require("backup")));
                return 0;
            default:
                return Fail($"Sous-commande server inconnue : {action}");
        }
    }

    private static async Task RequireServerOfflineAsync(CliServices services, string name)
    {
        if (await services.Servers.IsRconServiceAsync(name))
            throw new InvalidOperationException("Le serveur doit être arrêté avant toute opération sur le monde et la base de joueurs. Un service RCON Project Zomboid répond encore pour ce profil.");
    }

    private static string? ReadInitialAdminPassword(CliArguments args)
    {
        var selected = new[] { "admin-password", "admin-password-file", "admin-password-env" }.Where(args.Has).ToArray();
        if (selected.Length > 1)
            throw new ArgumentException("Utilisez une seule source pour le mot de passe administrateur initial.");
        if (selected.Length == 0) return null;
        if (selected[0] == "admin-password") return args.Require("admin-password");
        if (selected[0] == "admin-password-env")
        {
            var variable = args.Require("admin-password-env");
            return Environment.GetEnvironmentVariable(variable)
                ?? throw new InvalidOperationException($"La variable d'environnement {variable} n'est pas définie.");
        }
        var path = Path.GetFullPath(args.Require("admin-password-file"));
        if (!File.Exists(path)) throw new FileNotFoundException("Fichier de mot de passe administrateur introuvable.", path);
        return File.ReadAllText(path).TrimEnd('\r', '\n');
    }

    private static string ReadPineApiToken(CliArguments args, string? existing)
    {
        var selected = new[] { "api-key", "api-key-file", "api-key-env" }.Where(args.Has).ToArray();
        if (selected.Length > 1) throw new ArgumentException("Utilisez une seule source pour la clé API Pine.");
        if (selected.Length == 0) return existing ?? string.Empty;
        if (selected[0] == "api-key") return args.Require("api-key");
        if (selected[0] == "api-key-env")
        {
            var variable = args.Require("api-key-env");
            return Environment.GetEnvironmentVariable(variable)
                ?? throw new InvalidOperationException($"La variable d'environnement {variable} n'est pas définie.");
        }
        var path = Path.GetFullPath(args.Require("api-key-file"));
        if (!File.Exists(path)) throw new FileNotFoundException("Fichier de clé API Pine introuvable.", path);
        return File.ReadAllText(path).TrimEnd('\r', '\n');
    }

    private static async Task<int> AutomationAsync(string[] raw, CliArguments args, CliServices services)
    {
        if (raw.Length < 2) return Fail("Sous-commande requise : once, execute ou run.");
        var action = raw[1].ToLowerInvariant();
        if (action == "execute")
        {
            var project = RequireProject(services.Store, args);
            var result = await services.Automation.RunProjectAsync(project);
            WriteJson(result);
            return result.Success ? 0 : 2;
        }
        if (action == "once")
        {
            var results = await services.Automation.RunDueAsync(DateTimeOffset.Now);
            WriteJson(results);
            return results.All(x => x.Success) ? 0 : 2;
        }
        if (action != "run") return Fail($"Sous-commande automation inconnue : {action}");

        var interval = Math.Clamp(args.GetInt("interval") ?? 30, 10, 3600);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.WriteLine($"Planificateur PZASM actif (intervalle {interval}s). Ctrl+C pour arrêter.");
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                foreach (var result in await services.Automation.RunDueAsync(DateTimeOffset.Now, cancellation.Token))
                    Console.WriteLine($"{DateTimeOffset.Now:O} {(result.Success ? "OK" : "ERROR")} {result.ProjectName}: {result.Message}");
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        return 0;
    }

    private static async Task<int> SteamCmdAsync(string[] raw, CliArguments args, CliServices services)
    {
        if (raw.Length < 2) return Fail("Sous-commande requise : status, install, verify ou login.");
        switch (raw[1].ToLowerInvariant())
        {
            case "status":
                var status = services.SteamCmdInstaller.GetStatus();
                if (args.Has("json")) WriteJson(status);
                else Console.WriteLine(status.Installed ? $"SteamCMD prêt : {status.ExecutablePath}" : $"SteamCMD non installé. Emplacement prévu : {status.ExecutablePath}");
                return status.Installed ? 0 : 4;
            case "install":
                var result = await services.SteamCmdInstaller.InstallAsync(progress: new CliOperationProgress());
                if (args.Get("id") is { } projectId)
                {
                    if (!Guid.TryParse(projectId, out var parsedId)) throw new ArgumentException("--id doit être un GUID de projet.");
                    var project = services.Store.Get(parsedId) ?? throw new InvalidOperationException("Projet PZASM introuvable.");
                    project.Automation.SteamCmdPath = result.ExecutablePath;
                    services.Store.Save(project);
                }
                if (args.Has("json")) WriteJson(result);
                else
                {
                    Console.WriteLine($"SteamCMD installé : {result.ExecutablePath}");
                    Console.WriteLine(result.Bootstrapped ? "Initialisation terminée." : "L'extraction a réussi, mais l'initialisation doit être vérifiée.");
                    if (!string.IsNullOrWhiteSpace(result.Output)) Console.WriteLine(result.Output);
                }
                return result.Bootstrapped ? 0 : 2;
            case "login":
                {
                    var project = RequireProject(services.Store, args);
                    if (args.Get("steam-user") is { } username) project.Automation.SteamUsername = username.Trim();
                    if (args.Get("steamcmd") is { } executable) project.Automation.SteamCmdPath = executable.Trim();
                    if (Console.IsInputRedirected) throw new InvalidOperationException("Steam login requires an interactive terminal so secrets are never passed on the command line.");
                    Console.WriteLine("Use a dedicated publishing account that owns Project Zomboid. Reusing the account active in the desktop Steam client can disrupt that session.");
                    var password = ReadSecret("Steam password: ");
                    var progress = new Progress<OperationProgress>(value => Console.WriteLine($"[{value.Phase}] {value.Message}"));
                    Console.WriteLine("If Steam Guard is enabled, approve the Steam Mobile notification first. SteamCMD continues automatically; a current code is requested only as a fallback. SteamCMD does not expose a QR login for this publishing session.");
                    var guardCode = string.Empty;
                    SteamCmdResult login;
                    for (var attempt = 0; ; attempt++)
                    {
                        login = await services.SteamCmd.AuthenticateAsync(project, new SteamCredentials(password, guardCode), progress: progress);
                        if (login.Interaction is not (SteamCmdInteraction.SteamGuardCode or SteamCmdInteraction.SteamGuardMobileApprovalExpired)) break;
                        if (attempt >= 4) return Fail("Steam Guard authentication was not completed after five attempts. Wait for a fresh request or code and run the login command again.", 2);
                        Console.WriteLine(login.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault());
                        guardCode = ReadSecret("Current Steam Guard code, or Enter to retry mobile approval: ");
                    }
                    if (!login.Success) return Fail("SteamCMD login failed: " + login.CombinedOutput, 2);
                    project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
                    services.Store.Save(project);
                    Console.WriteLine("Portable SteamCMD session verified. The scheduler can now reuse it without storing the password or Steam Guard code.");
                    return 0;
                }
            case "verify":
                {
                    var project = RequireProject(services.Store, args);
                    if (args.Get("steam-user") is { } username) project.Automation.SteamUsername = username.Trim();
                    if (args.Get("steamcmd") is { } executable) project.Automation.SteamCmdPath = executable.Trim();
                    var progress = args.Has("json") ? null : new Progress<OperationProgress>(value => Console.WriteLine($"[{value.Phase}] {value.Message}"));
                    var verification = await services.SteamCmd.VerifyCachedSessionAsync(project, progress: progress);
                    if (verification.Interaction != SteamCmdInteraction.None)
                        return Fail(SteamCmdInteractionRequiredException.FromResult(verification).Message, 2);
                    if (!verification.Success) return Fail("SteamCMD session verification failed: " + verification.CombinedOutput, 2);
                    project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
                    services.Store.Save(project);
                    if (args.Has("json")) WriteJson(new { success = true, verifiedAt = project.Automation.SteamSessionVerifiedAt });
                    else Console.WriteLine("Existing SteamCMD session verified without a password or a new token.");
                    return 0;
                }
            default:
                return Fail($"Sous-commande steamcmd inconnue : {raw[1]}");
        }
    }

    private static async Task<int> WorkshopAsync(string[] raw, CliArguments args, CliServices services)
    {
        if (raw.Length < 2 || !raw[1].Equals("search", StringComparison.OrdinalIgnoreCase))
            return Fail("Sous-commande requise : workshop search.");
        var query = new WorkshopCatalogQuery(
            args.Get("query") ?? string.Empty,
            args.Get("sort") ?? "trend",
            args.GetInt("page") ?? 1,
            args.Get("tag") ?? string.Empty);
        var page = await services.WorkshopCatalog.SearchAsync(query);
        if (args.Has("json")) WriteJson(page);
        else
        {
            Console.WriteLine($"{page.Items.Count} item(s) Workshop · page {page.Page}");
            foreach (var item in page.Items)
                Console.WriteLine($"{item.WorkshopId,12}  {item.Subscriptions,10:N0} abonnés  {item.Title}");
        }
        return 0;
    }

    private static void Configure(PackageProject project, CliArguments args)
    {
        if (args.Get("name") is { } name) project.Name = name;
        if (args.Get("description") is { } description) project.Description = description;
        if (args.Get("mode") is { } mode) project.Mode = ParseMode(mode);
        if (args.Get("target") is { } target) project.TargetPzVersion = target;
        if (args.GetUlong("workshop-id") is { } workshopId) project.PublishedWorkshopId = workshopId;
        if (args.Get("steamcmd") is { } steamCmd) project.Automation.SteamCmdPath = steamCmd;
        if (args.Get("steam-user") is { } user) project.Automation.SteamUsername = user;
        if (args.Get("anonymous-downloads") is { } anonymousDownloads) project.Automation.AnonymousWorkshopDownloads = bool.Parse(anonymousDownloads);
        if (args.Get("server") is { } server) project.Automation.CoordinatedServerName = server;
        if (args.GetInt("restart-delay-minutes") is { } restartDelay) project.Automation.PostPublishRestartDelayMinutes = Math.Clamp(restartDelay, 5, 60);
        if (args.Get("automation") is { } automation) project.Automation.Enabled = bool.Parse(automation);
        if (args.Get("schedule") is { } schedule) project.Automation.DailyTimes = schedule.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Get("refresh-sources") is { } refreshSources) project.Automation.RefreshWorkshopSourcesBeforeBuild = bool.Parse(refreshSources);
        if (args.Get("publish-after-build") is { } publishAfterBuild) project.Automation.PublishAfterBuild = bool.Parse(publishAfterBuild);
        if (args.Get("visibility") is { } visibility) project.Visibility = Enum.Parse<WorkshopVisibility>(visibility, true);
        if (args.Get("tags") is { } tags) project.Tags = tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Get("map-order") is { } maps) project.MapOrder = maps.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (args.Get("inject-notice") is { } notice) project.InjectConnectionNotice = bool.Parse(notice);
        if (args.Has("accept-legal"))
        {
            project.LegalWarningAccepted = true;
            project.LegalWarningAcceptedAt ??= DateTimeOffset.UtcNow;
        }
    }

    private static PackageProject RequireProject(PackageProjectStore store, CliArguments args)
    {
        if (!Guid.TryParse(args.Require("id"), out var id)) throw new ArgumentException("--id doit être un GUID de projet.");
        return store.Get(id) ?? throw new InvalidOperationException("Projet PZASM introuvable.");
    }

    private static PackageMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "bundle" => PackageMode.Bundle,
        "fusion" or "fusionstrict" or "fusion-strict" => PackageMode.FusionStrict,
        _ => throw new ArgumentException("Mode attendu : bundle ou fusion-strict.")
    };

    private static void WriteValidation(PackageValidationResult result)
    {
        Console.WriteLine($"Build: {(result.CanBuild ? "OK" : "BLOQUÉ")} · Publication: {(result.CanPublish ? "OK" : "BLOQUÉ")}");
        foreach (var issue in result.Issues) Console.WriteLine($"{(issue.IsError ? "ERROR" : "WARN ")} {issue.Code}: {issue.Message}");
    }

    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    private static int Fail(string message, int code = 1) { Console.Error.WriteLine(message); return code; }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.WriteLine();
        return value.ToString();
    }

    private static int Help()
    {
        Console.WriteLine("""
PZ Advanced Server Manager CLI — Windows et Linux

Chaque projet représente un pack global indépendant avec son propre Workshop ID.

  pzasm scan [--target 42.20.2] [--json]
  pzasm projects [--json]
  pzasm project create --name "Mon pack" [--mode bundle]
  pzasm project show --id <guid>
  pzasm project duplicate --id <guid> [--name "Copie"]
  pzasm project delete --id <guid> --yes
  pzasm project add --id <guid> --mod-id <id> [--workshop-id <id>]
  pzasm project import-workshop --id <guid> --workshop-id <id>
  pzasm project remove --id <guid> --mod-id <id>
  pzasm project rights --id <guid> --mod-id <id> --status <Unknown|AuthorOwned|ExplicitPermission|CompatibleLicense|Denied> [--evidence-url <url>] [--private-proof <path>] [--notes <texte>]
  pzasm project update-policy --id <guid> --mod-id <id> --enabled <true|false>
  pzasm project configure --id <guid> [--description <texte>] [--workshop-id <id>] [--steamcmd <path>] [--steam-user <nom>] [--server <profil>] [--automation true] [--schedule 04:00,16:00] [--accept-legal]
  pzasm project maps --id <guid> [--apply-recommended --yes]
  pzasm project refresh --id <guid> [--mod-id <id>]
  pzasm project validate --id <guid>
  pzasm project build --id <guid>
  pzasm project publish --id <guid> --yes [--force]
  pzasm server list [--json]
  pzasm server create --name <profil> [--local-mode dedicated|hosted]
  pzasm server set-local-mode --name <profil> --local-mode dedicated|hosted
  pzasm server create-remote --provider pine --name <profil> (--api-key <secret> | --api-key-file <fichier> | --api-key-env <variable>) --server-id <id> [--ini /.cache/Server/Zomboid.ini]
  pzasm server create-remote --name <profil> --rcon-host <host> --rcon-password <secret> [--rcon-port 27015] [--no-auto-restart] [--ssh-host <host> --ssh-user <user>] [--ini <path>] [--ssh-port 22] [--ssh-key <file>] [--start-command <command>] [--create-config]
  pzasm server configure-remote --name <profil> [--provider pine --api-key <secret> --server-id <id>] [--rcon-host <host>] [--rcon-password <secret>] [--rcon-port 27015] [--auto-restart|--no-auto-restart] [--ssh-host <host> --ssh-user <user>] [--ini <path>] [--ssh-port 22] [--ssh-key <file>] [--start-command <command>]
  pzasm server delete-remote --name <profil> --yes
  pzasm server test-rcon --name <profil>
  pzasm server rcon --name <profil> --command <commande>
  pzasm server restart-rcon --name <profil> --yes
  pzasm server show --name <profil>
  pzasm server set --name <profil> --key <clé> [--value <valeur>] --yes
  pzasm server status --name <profil> [--json]
  pzasm server start --name <profil> [--admin-password <secret> | --admin-password-file <fichier> | --admin-password-env <variable>]
  pzasm server stop --name <profil> --yes
  pzasm server force-stop-local --name <profil> --yes
  pzasm server apply --name <profil> --id <guid> --yes
  pzasm server data-status --name <profil> [--json]
  pzasm server backup --name <profil> [--json]
  pzasm server backups --name <profil> [--json]
  pzasm server restore --name <profil> --backup <id> [--restore-config] [--no-backup] --yes [--json]
  pzasm server reset-world --name <profil> --yes [--no-backup] [--json]
  pzasm server delete-backup --name <profil> --backup <id> --yes
  pzasm server lock-backup --name <profil-pine> --backup <uuid> [--unlock]
  pzasm server download-backup --name <profil-pine> --backup <uuid>
  pzasm workshop search [--query <texte-ou-id>] [--sort trend|recent|subscribed|popular|relevance] [--tag <tag>] [--page 1] [--json]
  pzasm steamcmd status [--json]
  pzasm steamcmd install [--id <guid>] [--json]
  pzasm steamcmd verify --id <guid> [--steam-user <nom>] [--steamcmd <path>] [--json]
  pzasm steamcmd login --id <guid> [--steam-user <nom>] [--steamcmd <path>]
  pzasm automation once
  pzasm automation execute --id <guid>
  pzasm automation run [--interval 30]

Option globale : --data-root <dossier>. Sans cette option, PZASM utilise le dossier applicatif local de l'OS.
La construction est locale. La publication et les opérations destructrices exigent toujours --yes.
""");
        return 0;
    }
}

internal sealed class CliArguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public static CliArguments Parse(string[] args)
    {
        var result = new CliArguments();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) result._options[key] = args[++i];
            else result._options[key] = null;
        }
        return result;
    }

    public bool Has(string key) => _options.ContainsKey(key);
    public string? Get(string key) => _options.TryGetValue(key, out var value) ? value : null;
    public string Require(string key) => Get(key) ?? throw new ArgumentException($"Option --{key} requise.");
    public ulong? GetUlong(string key) => Get(key) is { } value ? ulong.Parse(value) : null;
    public int? GetInt(string key) => Get(key) is { } value ? int.Parse(value) : null;
}

internal sealed class CliServices
{
    public CliServices(ApplicationPaths paths)
    {
        Paths = paths;
        Store = new PackageProjectStore(paths);
        var discovery = new PzDiscoveryService(paths);
        Environment = new PzEnvironmentService(discovery);
        Validator = new PackageValidator();
        var snapshots = new PackageSourceSnapshotService(paths);
        Projects = new PackageProjectService(paths, Store, snapshots);
        var orchestration = new ServerOrchestrationService();
        var remoteStore = new RemoteServerConnectionStore(paths);
        var localStore = new LocalServerProfileStore(paths);
        var ssh = new SshRemoteServerService();
        var pine = new PineHostingClient();
        var remoteBackends = new RemoteServerBackendRouter([
            new SshRconRemoteBackend(ssh, orchestration),
            new PineHostingRemoteBackend(pine)
        ]);
        Servers = new ServerProfileService(paths, Environment, orchestration, remoteStore, localStore, remoteBackends, pine);
        WorldData = new ServerWorldDataStore(paths);
        var builder = new PackageBuildService(paths, Validator);
        WorkshopCatalog = new WorkshopCatalogService();
        SteamCmdInstaller = new SteamCmdInstaller(paths);
        SteamCmd = new SteamCmdService(Validator, WorkshopCatalog, SteamCmdInstaller);
        MapPriority = new MapPriorityService();
        Lifecycle = new PackageLifecycleService(paths, Store, snapshots, builder, SteamCmd, Servers);
        Automation = new PackageAutomationService(paths, Store, Lifecycle);
        WorkshopImport = new WorkshopImportService(SteamCmd, discovery, Environment, Projects);
    }

    public ApplicationPaths Paths { get; }
    public PackageProjectStore Store { get; }
    public PzEnvironmentService Environment { get; }
    public PackageValidator Validator { get; }
    public PackageProjectService Projects { get; }
    public PackageLifecycleService Lifecycle { get; }
    public PackageAutomationService Automation { get; }
    public WorkshopImportService WorkshopImport { get; }
    public SteamCmdInstaller SteamCmdInstaller { get; }
    public SteamCmdService SteamCmd { get; }
    public WorkshopCatalogService WorkshopCatalog { get; }
    public MapPriorityService MapPriority { get; }
    public ServerProfileService Servers { get; }
    public ServerWorldDataStore WorldData { get; }
}

internal sealed class CliOperationProgress : IProgress<OperationProgress>
{
    public void Report(OperationProgress value)
    {
        var count = value.Current is not null && value.Total is not null
            ? $" ({value.Current}/{value.Total})"
            : string.Empty;
        Console.Error.WriteLine($"[{value.Phase}]{count} {value.Message}");
    }
}
