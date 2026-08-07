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
                    var result = await services.WorkshopImport.ImportAsync(current, workshopId);
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
                            ?? throw new InvalidOperationException("Mod absent du projet."))
                        : await services.Lifecycle.RefreshSourcesAsync(current);
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
                    WriteJson(new { result.BuildRoot, result.CopiedFiles, result.CopiedBytes, result.ServerConfigSnippetPath, canPublish = result.Validation.CanPublish });
                    return 0;
                }
            case "publish":
                {
                    if (!args.Has("yes")) return Fail("Publication non exécutée. Ajoutez --yes pour confirmer l'envoi vers Steam Workshop.", 3);
                    if (string.IsNullOrWhiteSpace(current.Automation.CoordinatedServerName))
                        Console.Error.WriteLine("ATTENTION: aucun serveur coordonné; --yes confirme que l'administrateur gère lui-même le redémarrage.");
                    var result = await services.Lifecycle.PublishAsync(current, refreshSources: false, requireCoordinatedServer: false);
                    current.Automation.LastResult = result.Output;
                    services.Store.Save(current);
                    Console.WriteLine(result.Output);
                    Console.WriteLine($"Workshop ID: {current.PublishedWorkshopId}");
                    return 0;
                }
            default:
                return Fail($"Sous-commande project inconnue : {action}");
        }
    }

    private static async Task<int> ServerAsync(string[] raw, CliArguments args, CliServices services)
    {
        if (raw.Length < 2) return Fail("Sous-commande requise : list, create, show, set, status, start, stop ou apply.");
        var action = raw[1].ToLowerInvariant();
        if (action == "list")
        {
            var configs = services.Servers.List();
            if (args.Has("json")) WriteJson(configs.Select(profile => new
            {
                profile.Name,
                profile.Kind,
                profile.Location,
                SshHost = profile.Remote?.Host,
                profile.Remote?.SshPort,
                profile.Remote?.SshUser,
                profile.Remote?.RemoteIniPath,
                RconHost = profile.Remote?.RconHost,
                profile.Remote?.RconPort
            }));
            else foreach (var profile in configs) Console.WriteLine($"{profile.Name,-30} {profile.Kind,-8} {profile.Location}");
            return 0;
        }

        var name = args.Require("name");
        if (action == "create")
        {
            var profile = services.Servers.Create(name);
            Console.WriteLine($"Profil créé : {profile.Path}");
            return 0;
        }
        if (action is "create-remote" or "configure-remote")
        {
            var connection = new RemoteServerConnection
            {
                Name = name,
                Host = args.Require("host"),
                SshPort = args.GetInt("ssh-port") ?? 22,
                SshUser = args.Require("ssh-user"),
                SshPrivateKeyPath = args.Get("ssh-key") ?? string.Empty,
                RemoteIniPath = args.Require("ini"),
                StartCommand = args.Get("start-command") ?? string.Empty,
                RconHost = args.Get("rcon-host") ?? string.Empty,
                RconPort = args.GetInt("rcon-port") ?? 27015,
                RconPassword = args.Get("rcon-password") ?? string.Empty
            };
            if (action == "create-remote")
            {
                var profile = await services.Servers.CreateRemoteAsync(connection, args.Has("create-config"));
                Console.WriteLine($"Remote profile created and SSH connection verified: {profile.Location}");
            }
            else
            {
                await services.Servers.UpdateRemoteAsync(connection);
                Console.WriteLine($"Remote profile updated and SSH connection verified: {name}");
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
                var online = await services.Servers.IsOnlineAsync(name);
                Console.WriteLine(online ? "online" : "offline");
                return online ? 0 : 4;
            case "start":
                await services.Servers.StartAsync(name);
                Console.WriteLine($"Démarrage demandé pour {name}.");
                return 0;
            case "stop":
                if (!args.Has("yes")) return Fail("Arrêt non exécuté. Ajoutez --yes pour confirmer save/quit par RCON.", 3);
                await services.Servers.StopAsync(name);
                Console.WriteLine($"{name} sauvegardé et arrêté proprement.");
                return 0;
            case "apply":
                {
                    if (!args.Has("yes")) return Fail("Application non exécutée. Ajoutez --yes pour remplacer WorkshopItems, Mods et Map avec sauvegarde.", 3);
                    var project = RequireProject(services.Store, args);
                    var result = await services.Servers.ApplyPackageAsync(name, project);
                    Console.WriteLine($"Pack appliqué. Sauvegarde: {result.BackupPath}");
                    return 0;
                }
            default:
                return Fail($"Sous-commande server inconnue : {action}");
        }
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
        if (raw.Length < 2) return Fail("Sous-commande requise : status ou install.");
        switch (raw[1].ToLowerInvariant())
        {
            case "status":
                var status = services.SteamCmdInstaller.GetStatus();
                if (args.Has("json")) WriteJson(status);
                else Console.WriteLine(status.Installed ? $"SteamCMD prêt : {status.ExecutablePath}" : $"SteamCMD non installé. Emplacement prévu : {status.ExecutablePath}");
                return status.Installed ? 0 : 4;
            case "install":
                var result = await services.SteamCmdInstaller.InstallAsync();
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
  pzasm project publish --id <guid> --yes
  pzasm server list [--json]
  pzasm server create --name <profil>
  pzasm server create-remote --name <profil> --host <host> --ssh-user <user> --ini <path> [--ssh-port 22] [--ssh-key <file>] [--start-command <command>] [--rcon-host <host>] [--rcon-port 27015] [--rcon-password <secret>] [--create-config]
  pzasm server configure-remote --name <profil> --host <host> --ssh-user <user> --ini <path> [--ssh-port 22] [--ssh-key <file>] [--start-command <command>] [--rcon-host <host>] [--rcon-port 27015] [--rcon-password <secret>]
  pzasm server delete-remote --name <profil> --yes
  pzasm server show --name <profil>
  pzasm server set --name <profil> --key <clé> [--value <valeur>] --yes
  pzasm server status --name <profil>
  pzasm server start --name <profil>
  pzasm server stop --name <profil> --yes
  pzasm server apply --name <profil> --id <guid> --yes
  pzasm workshop search [--query <texte-ou-id>] [--sort trend|recent|subscribed|popular|relevance] [--tag <tag>] [--page 1] [--json]
  pzasm steamcmd status [--json]
  pzasm steamcmd install [--id <guid>] [--json]
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
        var ssh = new SshRemoteServerService();
        Servers = new ServerProfileService(paths, Environment, orchestration, remoteStore, ssh);
        var builder = new PackageBuildService(paths, Validator);
        var steamCmd = new SteamCmdService(Validator);
        MapPriority = new MapPriorityService();
        Lifecycle = new PackageLifecycleService(paths, Store, snapshots, builder, steamCmd, Servers);
        Automation = new PackageAutomationService(paths, Store, Lifecycle);
        WorkshopImport = new WorkshopImportService(steamCmd, discovery, Environment, Projects);
        SteamCmdInstaller = new SteamCmdInstaller(paths);
        WorkshopCatalog = new WorkshopCatalogService();
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
    public WorkshopCatalogService WorkshopCatalog { get; }
    public MapPriorityService MapPriority { get; }
    public ServerProfileService Servers { get; }
}
