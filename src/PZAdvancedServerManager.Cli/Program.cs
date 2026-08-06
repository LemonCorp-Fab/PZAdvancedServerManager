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
    private readonly PzDiscoveryService _discovery = new();
    private readonly PackageValidator _validator = new();
    private readonly ServerOrchestrationService _servers = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") return Help();
            var parsed = CliArguments.Parse(args);
            var paths = new ApplicationPaths(parsed.Get("data-root"));
            var store = new PackageProjectStore(paths);
            return args[0].ToLowerInvariant() switch
            {
                "scan" => Scan(parsed),
                "projects" => ListProjects(store, parsed),
                "project" => await ProjectAsync(args, parsed, paths, store),
                "server" => await ServerAsync(args, parsed, paths, store),
                _ => Fail($"Commande inconnue : {args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ERREUR: " + exception.Message);
            return 1;
        }
    }

    private int Scan(CliArguments args)
    {
        var installation = _discovery.DiscoverInstallation();
        var mods = _discovery.DiscoverMods(installation, args.Get("target") ?? "42.20.2");
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

    private async Task<int> ProjectAsync(string[] raw, CliArguments args, ApplicationPaths paths, PackageProjectStore store)
    {
        if (raw.Length < 2) return Fail("Sous-commande requise : create, show, add, remove, rights, configure, refresh, validate, build ou publish.");
        var action = raw[1].ToLowerInvariant();
        if (action == "create")
        {
            var project = store.Create(args.Require("name"));
            if (args.Get("mode") is { } mode) project.Mode = ParseMode(mode);
            store.Save(project);
            WriteJson(new { project.Id, project.Name, project.Mode, project.StableSuffix, projectFile = paths.ProjectFile(project.Id) });
            return 0;
        }

        var current = RequireProject(store, args);
        switch (action)
        {
            case "show":
                WriteJson(current);
                return 0;
            case "add":
            {
                var installation = _discovery.DiscoverInstallation();
                var discovered = _discovery.DiscoverMods(installation, current.TargetPzVersion);
                var modId = args.Require("mod-id");
                var workshopId = args.GetUlong("workshop-id");
                var selected = discovered.FirstOrDefault(x => x.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase) && (workshopId is null || x.WorkshopId == workshopId))
                    ?? throw new InvalidOperationException("Mod source introuvable. Exécutez scan et vérifiez --mod-id/--workshop-id.");
                var count = PackageProjectComposer.AddWithDependencies(current, selected, discovered);
                store.Save(current);
                Console.WriteLine($"{count} mod(s) ajouté(s), dépendances comprises. Total: {current.Mods.Count}.");
                return 0;
            }
            case "remove":
            {
                var mod = current.Mods.FirstOrDefault(x => x.ModId.Equals(args.Require("mod-id"), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Mod absent du projet.");
                current.Mods.Remove(mod);
                foreach (var map in mod.MapFolders.Where(map => current.Mods.All(x => !x.MapFolders.Contains(map, StringComparer.OrdinalIgnoreCase))))
                    current.MapOrder.RemoveAll(x => x.Equals(map, StringComparison.OrdinalIgnoreCase));
                store.Save(current);
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
                store.Save(current);
                Console.WriteLine($"Droits mis à jour pour {mod.ModId}: {mod.Permission.Status}.");
                return 0;
            }
            case "configure":
                Configure(current, args);
                store.Save(current);
                Console.WriteLine("Projet configuré.");
                return 0;
            case "refresh":
            {
                var refresh = await new SteamCmdService(_validator).RefreshSourcesAsync(current);
                current.Automation.LastResult = refresh.CombinedOutput;
                store.Save(current);
                Console.WriteLine(refresh.CombinedOutput);
                return refresh.Success ? 0 : refresh.ExitCode;
            }
            case "validate":
            {
                var validation = _validator.Validate(current);
                WriteValidation(validation);
                return validation.CanPublish ? 0 : 2;
            }
            case "build":
            {
                var result = new PackageBuildService(paths, _validator).Build(current);
                store.Save(current);
                WriteJson(new { result.BuildRoot, result.CopiedFiles, result.CopiedBytes, result.ServerConfigSnippetPath, canPublish = result.Validation.CanPublish });
                return 0;
            }
            case "publish":
            {
                if (!args.Has("yes")) return Fail("Publication non exécutée. Ajoutez --yes pour confirmer l'envoi vers Steam Workshop.", 3);
                var result = new PackageBuildService(paths, _validator).Build(current);
                var steam = new SteamCmdService(_validator);
                var restart = false;
                try
                {
                    if (!string.IsNullOrWhiteSpace(current.Automation.CoordinatedServerName))
                    {
                        var installation = _discovery.DiscoverInstallation();
                        var ini = Path.Combine(installation.UserZomboidRoot, "Server", current.Automation.CoordinatedServerName + ".ini");
                        if (await _servers.IsOnlineAsync(ini))
                        {
                            await _servers.StopGracefullyAsync(ini);
                            restart = true;
                        }
                    }
                    else Console.Error.WriteLine("ATTENTION: aucun serveur coordonné; l'administrateur confirme avec --yes que le serveur concerné est déjà arrêté ou sera redémarré.");

                    var publish = await steam.PublishAsync(current, result);
                    current.Automation.LastResult = publish.CombinedOutput;
                    store.Save(current);
                    Console.WriteLine(publish.CombinedOutput);
                    Console.WriteLine($"Workshop ID: {current.PublishedWorkshopId}");
                    return publish.Success ? 0 : publish.ExitCode;
                }
                finally
                {
                    if (restart)
                    {
                        var installation = _discovery.DiscoverInstallation();
                        _servers.Start(current.Automation.CoordinatedServerName, installation.DedicatedServerRoot ?? throw new DirectoryNotFoundException("Installation du serveur dédié introuvable pour le redémarrage."));
                    }
                }
            }
            default:
                return Fail($"Sous-commande project inconnue : {action}");
        }
    }

    private async Task<int> ServerAsync(string[] raw, CliArguments args, ApplicationPaths paths, PackageProjectStore store)
    {
        if (raw.Length < 2) return Fail("Sous-commande requise : list, create, show, set, status, start, stop ou apply.");
        var installation = _discovery.DiscoverInstallation();
        var serverRoot = Path.Combine(installation.UserZomboidRoot, "Server");
        var action = raw[1].ToLowerInvariant();
        if (action == "list")
        {
            var configs = Directory.Exists(serverRoot) ? Directory.EnumerateFiles(serverRoot, "*.ini").OrderBy(x => x).ToArray() : [];
            if (args.Has("json")) WriteJson(configs.Select(x => new { name = Path.GetFileNameWithoutExtension(x), path = x }));
            else foreach (var file in configs) Console.WriteLine($"{Path.GetFileNameWithoutExtension(file),-30} {file}");
            return 0;
        }

        var name = ValidateServerName(args.Require("name"));
        var ini = Path.Combine(serverRoot, name + ".ini");
        if (action == "create")
        {
            if (File.Exists(ini)) throw new IOException("Ce profil serveur existe déjà.");
            Directory.CreateDirectory(serverRoot);
            File.WriteAllText(ini, $"# Créé par PZ Advanced Server Manager{Environment.NewLine}PublicName={name}{Environment.NewLine}PublicDescription={Environment.NewLine}Password={Environment.NewLine}DefaultPort=16261{Environment.NewLine}MaxPlayers=16{Environment.NewLine}PauseEmpty=true{Environment.NewLine}DoLuaChecksum=true{Environment.NewLine}WorkshopItems={Environment.NewLine}Mods={Environment.NewLine}Map=Muldraugh, KY{Environment.NewLine}", new UTF8Encoding(false));
            Console.WriteLine($"Profil créé : {ini}");
            return 0;
        }
        if (!File.Exists(ini)) throw new FileNotFoundException("Profil serveur introuvable.", ini);
        switch (action)
        {
            case "show":
                Console.WriteLine(File.ReadAllText(ini));
                return 0;
            case "set":
            {
                if (!args.Has("yes")) return Fail("Modification non exécutée. Ajoutez --yes pour confirmer la sauvegarde et l'écriture.", 3);
                var key = args.Require("key").Trim();
                if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Any(char.IsControl)) throw new ArgumentException("Clé de configuration invalide.");
                var backup = Backup(ini);
                var config = ServerConfigDocument.Load(ini);
                config.Set(key, args.Get("value") ?? string.Empty);
                config.Save(ini);
                Console.WriteLine($"{key} mis à jour. Sauvegarde: {backup}");
                return 0;
            }
            case "status":
                var online = await _servers.IsOnlineAsync(ini);
                Console.WriteLine(online ? "online" : "offline");
                return online ? 0 : 4;
            case "start":
                _servers.Start(name, installation.DedicatedServerRoot ?? throw new DirectoryNotFoundException("Installation du serveur dédié introuvable."));
                Console.WriteLine($"Démarrage demandé pour {name}.");
                return 0;
            case "stop":
                if (!args.Has("yes")) return Fail("Arrêt non exécuté. Ajoutez --yes pour confirmer save/quit par RCON.", 3);
                await _servers.StopGracefullyAsync(ini);
                Console.WriteLine($"{name} sauvegardé et arrêté proprement.");
                return 0;
            case "apply":
            {
                if (!args.Has("yes")) return Fail("Application non exécutée. Ajoutez --yes pour remplacer WorkshopItems, Mods et Map avec sauvegarde.", 3);
                if (await _servers.IsOnlineAsync(ini)) throw new InvalidOperationException("Arrêtez d'abord le serveur : PZASM refuse d'appliquer un pack pendant qu'il est en ligne.");
                var project = RequireProject(store, args);
                if (project.PublishedWorkshopId == 0) throw new InvalidOperationException("Le pack doit être publié avant son application au serveur.");
                var snippetPath = Path.Combine(paths.BuildRoot(project.Id), "server-config.txt");
                if (!File.Exists(snippetPath)) throw new FileNotFoundException("Construisez le pack avant de l'appliquer.", snippetPath);
                var backup = Backup(ini);
                var source = ServerConfigDocument.Load(snippetPath);
                var target = ServerConfigDocument.Load(ini);
                target.Set("WorkshopItems", source.Get("WorkshopItems"));
                target.Set("Mods", source.Get("Mods"));
                target.Set("Map", source.Get("Map"));
                target.Save(ini);
                Console.WriteLine($"Pack appliqué. Sauvegarde: {backup}");
                return 0;
            }
            default:
                return Fail($"Sous-commande server inconnue : {action}");
        }
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

    private static string ValidateServerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Le nom du profil serveur ne peut contenir que lettres, chiffres, tirets et underscores.");
        return name;
    }

    private static string Backup(string path)
    {
        var backup = path + $".pzasm.{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak";
        File.Copy(path, backup, false);
        return backup;
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
  pzasm project add --id <guid> --mod-id <id> [--workshop-id <id>]
  pzasm project remove --id <guid> --mod-id <id>
  pzasm project rights --id <guid> --mod-id <id> --status <Unknown|AuthorOwned|ExplicitPermission|CompatibleLicense|Denied> [--evidence-url <url>] [--private-proof <path>] [--notes <texte>]
  pzasm project configure --id <guid> [--description <texte>] [--workshop-id <id>] [--steamcmd <path>] [--steam-user <nom>] [--server <profil>] [--automation true] [--schedule 04:00,16:00] [--accept-legal]
  pzasm project refresh --id <guid>
  pzasm project validate --id <guid>
  pzasm project build --id <guid>
  pzasm project publish --id <guid> --yes
  pzasm server list [--json]
  pzasm server create --name <profil>
  pzasm server show --name <profil>
  pzasm server set --name <profil> --key <clé> [--value <valeur>] --yes
  pzasm server status --name <profil>
  pzasm server start --name <profil>
  pzasm server stop --name <profil> --yes
  pzasm server apply --name <profil> --id <guid> --yes

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
}
