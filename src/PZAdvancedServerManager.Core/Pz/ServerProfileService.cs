using System.Text;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class ServerProfileService(
    ApplicationPaths paths,
    PzEnvironmentService environment,
    ServerOrchestrationService orchestration,
    RemoteServerConnectionStore remoteStore,
    LocalServerProfileStore localStore,
    RemoteServerBackendRouter remoteBackends,
    PineHostingClient pine)
{
    public PzInstallation Installation => environment.Installation;

    public IReadOnlyList<ServerConfigEntry> List()
    {
        var entries = new List<ServerConfigEntry>();
        if (Directory.Exists(ServerRoot))
            entries.AddRange(Directory.EnumerateFiles(ServerRoot, "*.ini").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => LocalEntry(Path.GetFileNameWithoutExtension(x), x)));
        entries.AddRange(remoteStore.GetAll().Select(remote => new ServerConfigEntry(remote.Name, remote.RemoteIniPath, ServerConnectionKind.Remote, remote)));
        return entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Kind).ToList();
    }

    public ServerConfigEntry Get(string name)
    {
        var validated = ValidateName(name);
        var remote = remoteStore.Get(validated);
        if (remote is not null) return new ServerConfigEntry(remote.Name, remote.RemoteIniPath, ServerConnectionKind.Remote, remote);
        var path = Path.Combine(ServerRoot, validated + ".ini");
        if (!File.Exists(path)) throw new FileNotFoundException("Profil serveur introuvable.", path);
        return LocalEntry(validated, path);
    }

    public ServerConfigEntry Create(string name, LocalServerMode mode = LocalServerMode.Dedicated)
    {
        var validated = ValidateName(name);
        if (remoteStore.Get(validated) is not null) throw new IOException("Un profil distant utilise déjà ce nom.");
        Directory.CreateDirectory(ServerRoot);
        var path = Path.Combine(ServerRoot, validated + ".ini");
        if (File.Exists(path)) throw new IOException("Ce profil serveur existe déjà.");
        File.WriteAllText(path, Template(validated).Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        localStore.Save(validated, mode);
        return LocalEntry(validated, path);
    }

    public void SetLocalMode(string name, LocalServerMode mode)
    {
        var profile = Get(name);
        if (profile.IsRemote) throw new InvalidOperationException("Le type d'exécution local ne s'applique pas aux serveurs distants.");
        localStore.Save(profile.Name, mode);
    }

    public async Task<ServerConfigEntry> CreateRemoteAsync(RemoteServerConnection connection, bool createConfigIfMissing, CancellationToken cancellationToken = default)
    {
        NormalizeAndValidate(connection);
        if (remoteStore.Get(connection.Name) is not null || File.Exists(Path.Combine(ServerRoot, connection.Name + ".ini")))
            throw new IOException("Un profil local ou distant utilise déjà ce nom.");
        if (connection.IsPineHosting)
        {
            var info = await pine.TestAsync(connection, cancellationToken);
            connection.ProviderServerName = info.Name;
            try
            {
                _ = await remoteBackends.Resolve(connection).ReadFileAsync(connection, connection.RemoteIniPath, cancellationToken);
            }
            catch when (createConfigIfMissing)
            {
                throw new InvalidOperationException("La création automatique de l'INI n'est pas proposée sur Pine Hosting. Démarrez une première fois le serveur depuis le panel, puis reconnectez le profil.");
            }
        }
        else if (connection.HasSshConnection)
        {
            await remoteBackends.Resolve(connection).TestAsync(connection, cancellationToken);
            if (connection.HasSshManagement)
            {
                try
                {
                    _ = await remoteBackends.Resolve(connection).ReadFileAsync(connection, connection.RemoteIniPath, cancellationToken);
                }
                catch when (createConfigIfMissing)
                {
                    await remoteBackends.Resolve(connection).WriteFileAsync(connection, connection.RemoteIniPath, Template(connection.Name), cancellationToken);
                }
            }
        }
        remoteStore.Save(connection);
        return new ServerConfigEntry(connection.Name, connection.RemoteIniPath, ServerConnectionKind.Remote, connection);
    }

    public async Task UpdateRemoteAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        var existing = remoteStore.Get(connection.Name) ?? throw new KeyNotFoundException("Profil serveur distant introuvable.");
        connection.Id = existing.Id;
        if (string.IsNullOrEmpty(connection.RconPassword)) connection.RconPassword = existing.RconPassword;
        if (string.IsNullOrEmpty(connection.ApiToken)) connection.ApiToken = existing.ApiToken;
        NormalizeAndValidate(connection);
        if (connection.IsPineHosting)
            connection.ProviderServerName = (await pine.GetServerAsync(connection, cancellationToken)).Name;
        else if (connection.HasSshConnection)
            await remoteBackends.Resolve(connection).TestAsync(connection, cancellationToken);
        remoteStore.Save(connection);
    }

    public async Task TestRemoteAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        PreserveStoredSecrets(connection);
        NormalizeAndValidate(connection);
        await remoteBackends.Resolve(connection).TestAsync(connection, cancellationToken);
    }

    public async Task TestRconAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        PreserveStoredSecrets(connection);
        NormalizeAndValidate(connection);
        if (connection.IsPineHosting && (string.IsNullOrWhiteSpace(connection.RconHost) || string.IsNullOrWhiteSpace(connection.RconPassword)))
            throw new InvalidOperationException("RCON est facultatif avec Pine Hosting. Ajoutez son hôte et son mot de passe uniquement si vous souhaitez aussi tester RCON.");
        if (!await orchestration.IsOnlineAsync(RconHost(connection), connection.RconPort, connection.RconPassword, cancellationToken))
            throw new IOException("Project Zomboid n'a pas accepté la connexion RCON. Vérifiez l'hôte, le port, le mot de passe et l'état du jeu.");
    }

    public bool RemoveRemote(string name) => remoteStore.Remove(ValidateName(name));

    public string ReadRaw(string name)
    {
        var profile = Get(name);
        if (profile.IsRemote) EnsureConfigurationAccess(profile);
        return profile.IsRemote
            ? remoteBackends.Resolve(profile.Remote!).ReadFileAsync(profile.Remote!, profile.Path).GetAwaiter().GetResult()
            : ServerConfigDocument.ReadText(profile.Path).Text;
    }

    public string SaveRaw(string name, string content)
    {
        var profile = Get(name);
        if (profile.IsRemote) EnsureConfigurationAccess(profile);
        string backup;
        if (profile.IsRemote)
        {
            backup = remoteBackends.Resolve(profile.Remote!).WriteFileAsync(profile.Remote!, profile.Path, content).GetAwaiter().GetResult();
        }
        else
        {
            backup = Backup(profile.Path);
            var original = ServerConfigDocument.ReadText(profile.Path);
            var newLine = original.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var temp = profile.Path + ".pzasm.tmp";
            try
            {
                File.WriteAllText(temp, content.Replace("\r\n", "\n").Replace("\n", newLine), original.Encoding);
                File.Move(temp, profile.Path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        var persisted = profile.IsRemote
            ? remoteBackends.Resolve(profile.Remote!).ReadFileAsync(profile.Remote!, profile.Path).GetAwaiter().GetResult()
            : ServerConfigDocument.ReadText(profile.Path).Text;
        if (!NormalizeText(persisted).Equals(NormalizeText(content), StringComparison.Ordinal))
            throw new IOException($"La configuration « {profile.Name} » a été écrite mais sa relecture diffère. La sauvegarde reste disponible : {backup}");
        return backup;
    }

    public string Set(string name, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Any(char.IsControl))
            throw new ArgumentException("Clé de configuration invalide.", nameof(key));
        return Update(name, new Dictionary<string, string> { [key.Trim()] = value });
    }

    public string Update(string name, IReadOnlyDictionary<string, string> values)
    {
        if (values.Keys.Any(key => string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Any(char.IsControl)))
            throw new ArgumentException("Une clé de configuration est invalide.", nameof(values));
        var profile = Get(name);
        var document = ReadDocument(profile);
        foreach (var (key, value) in values) document.Set(key.Trim(), value);
        var backup = WriteDocument(profile, document);
        if (profile.IsRemote)
        {
            var remote = profile.Remote!;
            if (values.TryGetValue("RCONPort", out var port) && int.TryParse(port, out var parsed)) remote.RconPort = parsed;
            if (values.TryGetValue("RCONPassword", out var password)) remote.RconPassword = password;
            remoteStore.Save(remote);
        }
        return backup;
    }

    public ServerContentUpdateResult AddContent(string name, IEnumerable<ulong> workshopIds, IEnumerable<string> modIds)
    {
        var profile = Get(name);
        var document = ReadDocument(profile);
        var workshop = document.GetList("WorkshopItems").ToList();
        var mods = document.GetList("Mods").ToList();
        var addedWorkshop = AppendDistinct(workshop, workshopIds.Where(id => id != 0).Select(id => id.ToString()));
        var addedMods = AppendDistinct(mods, modIds.Where(id => !string.IsNullOrWhiteSpace(id)));
        if (addedWorkshop == 0 && addedMods == 0)
            return new ServerContentUpdateResult(string.Empty, 0, 0, workshop, mods);
        document.Set("WorkshopItems", string.Join(';', workshop));
        document.Set("Mods", string.Join(';', mods));
        var backup = WriteDocument(profile, document);
        return new ServerContentUpdateResult(backup, addedWorkshop, addedMods, workshop, mods);
    }

    public async Task<bool> IsOnlineAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (!profile.IsRemote)
            return (await ReadRuntimeAsync(profile.Name, cancellationToken)).IsRunning;
        return await remoteBackends.Resolve(profile.Remote!).IsOnlineAsync(profile.Remote!, cancellationToken);
    }

    public async Task<bool> IsRconAuthenticatedAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (profile.Remote?.IsPineHosting == true && (string.IsNullOrWhiteSpace(profile.Remote.RconHost) || string.IsNullOrWhiteSpace(profile.Remote.RconPassword))) return false;
        var (host, port, password) = RconEndpoint(profile);
        return await orchestration.IsOnlineAsync(host, port, password, cancellationToken);
    }

    public bool IsManagerProcessRunning(string name)
    {
        var profile = Get(name);
        return !profile.IsRemote && orchestration.IsManagedProcessRunning(profile.Name);
    }

    public bool IsLocalProcessRunning(string name)
    {
        var profile = Get(name);
        return !profile.IsRemote && orchestration.IsLocalServerProcessRunning(profile.Name);
    }

    public async Task<ServerRuntimeSnapshot> ReadRuntimeAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (!profile.IsRemote)
        {
            var consolePath = Path.Combine(environment.Installation.UserZomboidRoot, "server-console.txt");
            var coopConsolePath = Path.Combine(environment.Installation.UserZomboidRoot, "coop-console.txt");
            return await orchestration.InspectLocalRuntimeAsync(profile.Name, profile.Path, consolePath, coopConsolePath, cancellationToken);
        }

        return await remoteBackends.Resolve(profile.Remote!).ReadRuntimeAsync(profile.Remote!, cancellationToken);
    }

    public ServerNetworkInfo ReadNetworkInfo(string name)
    {
        var profile = Get(name);
        ServerConfigDocument? document = null;
        if (profile.CanManageConfiguration)
        {
            try { document = ReadDocument(profile); }
            catch when (profile.IsRemote) { }
        }
        return ServerNetworkInfo.Create(profile, document);
    }

    public async Task<bool> IsRconPortReachableAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (!profile.IsRemote) return await orchestration.IsPortReachableAsync(profile.Path, cancellationToken);
        var remote = profile.Remote!;
        if (remote.IsPineHosting && (string.IsNullOrWhiteSpace(remote.RconHost) || string.IsNullOrWhiteSpace(remote.RconPassword))) return false;
        return await orchestration.IsPortReachableAsync(RconHost(remote), remote.RconPort, cancellationToken);
    }

    public async Task<bool> IsRconServiceAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (!profile.IsRemote)
            return (await ReadRuntimeAsync(profile.Name, cancellationToken)).IsRunning
                || await orchestration.IsRconServiceAsync(profile.Path, cancellationToken);
        var remote = profile.Remote!;
        if (remote.IsPineHosting && (string.IsNullOrWhiteSpace(remote.RconHost) || string.IsNullOrWhiteSpace(remote.RconPassword))) return false;
        return await orchestration.IsRconServiceAsync(RconHost(remote), remote.RconPort, remote.RconPassword, cancellationToken);
    }

    public void Start(string name) => StartAsync(name).GetAwaiter().GetResult();
    public bool CanStart(string name)
    {
        var profile = Get(name);
        return !profile.IsRemote || profile.Remote!.IsPineHosting || profile.Remote.HasSshConnection && !string.IsNullOrWhiteSpace(profile.Remote.StartCommand);
    }

    public bool CanCoordinateRestart(string name)
    {
        var profile = Get(name);
        return !profile.IsRemote || profile.Remote!.IsPineHosting || CanStart(name) || profile.Remote.AutoRestartAfterRconQuit;
    }

    public async Task StartAsync(string name, CancellationToken cancellationToken = default)
        => await StartAsync(name, null, cancellationToken);

    public async Task StartAsync(string name, string? initialAdminPassword, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (profile.IsRemote)
        {
            if (!string.IsNullOrEmpty(initialAdminPassword))
                throw new InvalidOperationException("Le mot de passe administrateur initial ne peut être transmis qu'à un serveur local lancé par le manager.");
            await remoteBackends.Resolve(profile.Remote!).StartAsync(profile.Remote!, cancellationToken);
            return;
        }
        var dedicatedRoot = environment.Installation.DedicatedServerRoot
            ?? throw new DirectoryNotFoundException("Installation Project Zomboid Dedicated Server introuvable.");
        if (profile.LocalMode != LocalServerMode.Dedicated)
            throw new InvalidOperationException("Ce profil est configuré comme Host local. Lancez-le depuis le menu Host de Project Zomboid ou changez son mode en serveur dédié local.");
        var runtime = await ReadRuntimeAsync(profile.Name, cancellationToken);
        if (runtime.IsRunning)
            throw new InvalidOperationException($"Un serveur Project Zomboid actif utilise déjà le profil « {profile.Name} ».");
        orchestration.Start(profile.Name, dedicatedRoot, initialAdminPassword);
    }

    public async Task StopAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (!profile.IsRemote)
        {
            await orchestration.StopGracefullyAsync(profile.Path, cancellationToken);
            return;
        }
        await remoteBackends.Resolve(profile.Remote!).StopAsync(profile.Remote!, cancellationToken);
    }

    public async Task<ForcedServerStopResult> ForceStopLocalDedicatedAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (profile.IsRemote)
            throw new InvalidOperationException("Un processus distant ne peut pas être terminé depuis le gestionnaire local.");
        var runtime = await ReadRuntimeAsync(profile.Name, cancellationToken);
        if (runtime.IsRconAuthenticated)
            throw new InvalidOperationException("RCON est disponible. Utilisez l'arrêt propre save/quit afin de protéger le monde.");
        if (!runtime.Instances.Any(instance => instance.Origin == ServerRuntimeOrigin.LocalDedicated))
            throw new InvalidOperationException("Aucun serveur dédié local actif ne correspond à ce profil.");
        return await orchestration.ForceStopLocalDedicatedAsync(profile.Name, cancellationToken);
    }

    public async Task RestartViaRconAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (profile.IsRemote)
        {
            await remoteBackends.Resolve(profile.Remote!).RestartAsync(profile.Remote!, cancellationToken);
            return;
        }
        var (host, port, password) = RconEndpoint(profile);
        await orchestration.RequestRestartAsync(host, port, password, cancellationToken);
    }

    public async Task<string> ExecuteRconCommandAsync(string name, string command, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (profile.IsRemote)
            return await remoteBackends.Resolve(profile.Remote!).ExecuteCommandAsync(profile.Remote!, command, cancellationToken);
        var (host, port, password) = RconEndpoint(profile);
        return await orchestration.ExecuteCommandAsync(host, port, password, command, cancellationToken);
    }

    public async Task<ServerApplyResult> ApplyPackageAsync(string name, PackageProject project, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (await IsOnlineAsync(name, cancellationToken))
            throw new InvalidOperationException("Arrêtez d'abord le serveur : PZASM refuse d'appliquer un pack pendant qu'il est en ligne.");
        if (project.PublishedWorkshopId == 0)
            throw new InvalidOperationException("Le pack doit être publié avant son application au serveur.");
        var snippetPath = Path.Combine(paths.BuildRoot(project.Id), "server-config.txt");
        if (!File.Exists(snippetPath)) throw new FileNotFoundException("Construisez le pack avant de l'appliquer.", snippetPath);
        var source = ServerConfigDocument.Load(snippetPath);
        if (!source.Get("WorkshopItems").Equals(project.PublishedWorkshopId.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("La configuration générée ne correspond pas au Workshop ID actuel. Reconstruisez le pack avant de l'appliquer.");
        var target = ReadDocument(profile);
        target.Set("WorkshopItems", source.Get("WorkshopItems"));
        target.Set("Mods", source.Get("Mods"));
        target.Set("Map", source.Get("Map"));
        var backup = WriteDocument(profile, target);
        return new ServerApplyResult(backup, source.GetList("WorkshopItems"), source.GetList("Mods"), source.GetList("Map"));
    }

    public ServerConfigSummary ReadSummary(string name)
    {
        var document = ReadDocument(Get(name));
        return new ServerConfigSummary(document.GetList("WorkshopItems"), document.GetList("Mods"), document.GetList("Map"));
    }

    public string ResolveIniPath(string name) => Get(name).Path;
    public ServerConfigDocument ReadDocument(string name) => ReadDocument(Get(name));

    public async Task<PineServerInfo> ReadPineServerAsync(string name, CancellationToken cancellationToken = default)
        => await pine.GetServerAsync(RequirePine(name), cancellationToken);

    public async Task<IReadOnlyList<PineBackupInfo>> ListPineBackupsAsync(string name, CancellationToken cancellationToken = default)
        => await pine.ListBackupsAsync(RequirePine(name), cancellationToken);

    public async Task<PineBackupInfo> CreatePineBackupAsync(string name, string? backupName = null, bool locked = false, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var connection = RequirePine(name);
        if (await remoteBackends.Resolve(connection).IsOnlineAsync(connection, cancellationToken))
            throw new InvalidOperationException("Arrêtez le serveur Pine avant de créer une sauvegarde cohérente du monde et de sa base joueurs.");
        progress?.Report(new OperationProgress("pine-backup", "Création de la sauvegarde complète chez Pine Hosting…", 1, 2));
        var created = await pine.CreateBackupAsync(connection, backupName ?? $"PZASM {DateTimeOffset.Now:yyyy-MM-dd HH:mm}", locked, cancellationToken);
        progress?.Report(new OperationProgress("pine-backup", "Vérification de l'achèvement et de l'empreinte de la sauvegarde…", 2, 2));
        return await pine.WaitForBackupAsync(connection, created.Uuid, TimeSpan.FromMinutes(30), cancellationToken);
    }

    public async Task RestorePineBackupAsync(string name, string backupUuid, bool createSafetyBackup, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var connection = RequirePine(name);
        if (await remoteBackends.Resolve(connection).IsOnlineAsync(connection, cancellationToken))
            throw new InvalidOperationException("Arrêtez le serveur Pine avant de restaurer une sauvegarde.");
        if (createSafetyBackup)
        {
            progress?.Report(new OperationProgress("pine-restore", "Création de la sauvegarde de sécurité préalable…", 1, 3));
            await CreatePineBackupAsync(name, $"PZASM avant restauration {DateTimeOffset.Now:yyyy-MM-dd HH:mm}", true, null, cancellationToken);
        }
        progress?.Report(new OperationProgress("pine-restore", "Demande de restauration transactionnelle à Pine Hosting…", 2, 3));
        await pine.RestoreBackupAsync(connection, backupUuid, cancellationToken);
        progress?.Report(new OperationProgress("pine-restore", "Restauration acceptée par Pine Hosting. Le panel finalise les fichiers…", 3, 3));
    }

    public async Task SetPineBackupLockAsync(string name, string backupUuid, bool locked, CancellationToken cancellationToken = default)
        => await pine.SetBackupLockAsync(RequirePine(name), backupUuid, locked, cancellationToken);

    public async Task DeletePineBackupAsync(string name, string backupUuid, CancellationToken cancellationToken = default)
        => await pine.DeleteBackupAsync(RequirePine(name), backupUuid, cancellationToken);

    public async Task<Uri> GetPineBackupDownloadUriAsync(string name, string backupUuid, CancellationToken cancellationToken = default)
        => await pine.GetBackupDownloadUriAsync(RequirePine(name), backupUuid, cancellationToken);

    public async Task<PineWorldResetResult> ResetPineWorldAsync(string name, bool createSafetyBackup, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var connection = RequirePine(name);
        if (await remoteBackends.Resolve(connection).IsOnlineAsync(connection, cancellationToken))
            throw new InvalidOperationException("Arrêtez le serveur Pine avant de réinitialiser le monde.");
        PineBackupInfo? backup = null;
        if (createSafetyBackup)
        {
            progress?.Report(new OperationProgress("pine-reset", "Création d'une sauvegarde Pine verrouillée avant le fresh start…", 1, 3));
            backup = await CreatePineBackupAsync(name, $"PZASM avant fresh start {DateTimeOffset.Now:yyyy-MM-dd HH:mm}", true, null, cancellationToken);
        }
        progress?.Report(new OperationProgress("pine-reset", "Retrait du monde multijoueur…", 2, 3));
        await pine.DeleteFilesAsync(connection, "/.cache/Saves/Multiplayer", ["Zomboid"], cancellationToken);
        progress?.Report(new OperationProgress("pine-reset", "Retrait de la base joueurs…", 3, 3));
        await pine.DeleteFilesAsync(connection, "/.cache/db", ["Zomboid.db"], cancellationToken);
        return new PineWorldResetResult(backup, DateTimeOffset.UtcNow);
    }

    public ServerWorldDataLocation ResolveWorldDataLocation(string name)
    {
        var profile = Get(name);
        if (profile.IsRemote)
            throw new NotSupportedException("La gestion des données du monde nécessite actuellement un accès local au dossier Zomboid. Les profils distants restent contrôlables par RCON et SSH pour le processus et la configuration.");
        var userRoot = Path.GetFullPath(environment.Installation.UserZomboidRoot);
        var serverRoot = Path.Combine(userRoot, "Server");
        return new ServerWorldDataLocation(
            profile.Name,
            userRoot,
            Path.Combine(userRoot, "Saves", "Multiplayer", profile.Name),
            Path.Combine(userRoot, "db", profile.Name + ".db"),
            [
                profile.Path,
                Path.Combine(serverRoot, profile.Name + "_SandboxVars.lua"),
                Path.Combine(serverRoot, profile.Name + "_spawnregions.lua"),
                Path.Combine(serverRoot, profile.Name + "_spawnpoints.lua")
            ]);
    }

    public IReadOnlyList<StructuredServerSetting> ReadStructuredSettings(string name) => StructuredServerSettings.ParseIni(ReadRaw(name));

    public SandboxSettingsDocument ReadSandboxDocument(string name)
    {
        var profile = Get(name);
        if (profile.IsRemote)
        {
            return SandboxSettingsDocument.Parse(remoteBackends.Resolve(profile.Remote!).ReadFileAsync(profile.Remote!, SandboxPath(profile)).GetAwaiter().GetResult());
        }
        var path = SandboxPath(profile);
        if (!File.Exists(path)) throw new FileNotFoundException("Le fichier SandboxVars de ce profil n'existe pas encore. Démarrez une première fois le serveur ou créez-le avec l'éditeur officiel.", path);
        return SandboxSettingsDocument.Load(path);
    }

    public string UpdateSandbox(string name, IReadOnlyDictionary<string, string> values)
    {
        var profile = Get(name);
        var document = ReadSandboxDocument(name);
        document.Update(values);
        var expected = values.Keys.ToDictionary(key => key, document.Get, StringComparer.Ordinal);
        string backup;
        if (profile.IsRemote)
            backup = remoteBackends.Resolve(profile.Remote!).WriteFileAsync(profile.Remote!, SandboxPath(profile), document.Render()).GetAwaiter().GetResult();
        else
        {
            var path = SandboxPath(profile);
            backup = Backup(path);
            document.Save(path);
        }
        var persisted = ReadSandboxDocument(name);
        var mismatches = expected.Where(pair => !persisted.Get(pair.Key).Equals(pair.Value, StringComparison.Ordinal)).Select(pair => pair.Key).Take(8).ToArray();
        if (mismatches.Length > 0)
            throw new IOException($"Le fichier SandboxVars a été écrit mais la relecture diffère pour : {string.Join(", ", mismatches)}. Sauvegarde : {backup}");
        return backup;
    }

    public string ReadLuaFile(string name, ServerLuaFileKind kind)
    {
        var profile = Get(name);
        var path = LuaFilePath(profile, kind);
        if (profile.IsRemote) return remoteBackends.Resolve(profile.Remote!).ReadFileAsync(profile.Remote!, path).GetAwaiter().GetResult();
        if (!File.Exists(path)) return string.Empty;
        return ServerConfigDocument.ReadText(path).Text;
    }

    public string SaveLuaFile(string name, ServerLuaFileKind kind, string content)
    {
        ValidateLuaFile(kind, content);
        var profile = Get(name);
        var path = LuaFilePath(profile, kind);
        var current = ReadLuaFile(name, kind);
        if (NormalizeText(current).Equals(NormalizeText(content), StringComparison.Ordinal)) return string.Empty;
        string backup;
        if (profile.IsRemote)
            backup = remoteBackends.Resolve(profile.Remote!).WriteFileAsync(profile.Remote!, path, content).GetAwaiter().GetResult();
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            backup = File.Exists(path) ? Backup(path) : string.Empty;
            var encoding = File.Exists(path) ? ServerConfigDocument.ReadText(path).Encoding : new UTF8Encoding(false);
            var temporary = path + ".pzasm.tmp";
            try
            {
                File.WriteAllText(temporary, content, encoding);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var persisted = ReadLuaFile(name, kind);
        if (!NormalizeText(persisted).Equals(NormalizeText(content), StringComparison.Ordinal))
            throw new IOException($"Le fichier {Path.GetFileName(path)} a été écrit mais sa relecture diffère. Sauvegarde : {backup}");
        return backup;
    }

    private ServerConfigDocument ReadDocument(ServerConfigEntry profile) => profile.IsRemote
        ? ServerConfigDocument.Parse(remoteBackends.Resolve(profile.Remote!).ReadFileAsync(EnsureConfigurationAccess(profile), profile.Path).GetAwaiter().GetResult())
        : ServerConfigDocument.Load(profile.Path);

    private string WriteDocument(ServerConfigEntry profile, ServerConfigDocument document)
    {
        var expected = NormalizeText(document.Render());
        string backup;
        if (profile.IsRemote)
            backup = remoteBackends.Resolve(profile.Remote!).WriteFileAsync(EnsureConfigurationAccess(profile), profile.Path, document.Render()).GetAwaiter().GetResult();
        else
        {
            backup = Backup(profile.Path);
            document.Save(profile.Path);
        }
        var persisted = NormalizeText(ReadDocument(profile).Render());
        if (!persisted.Equals(expected, StringComparison.Ordinal))
            throw new IOException($"La configuration « {profile.Name} » a été écrite mais la vérification de relecture a échoué. La sauvegarde reste disponible : {backup}");
        return backup;
    }

    private static string NormalizeText(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string ServerRoot => Path.Combine(environment.Installation.UserZomboidRoot, "Server");
    private ServerConfigEntry LocalEntry(string name, string path)
    {
        var storedMode = localStore.Get(name);
        var mode = storedMode
            ?? (orchestration.HasLocalDedicatedProcess(name) ? LocalServerMode.Dedicated : LocalServerMode.Hosted);
        if (mode == LocalServerMode.Dedicated && storedMode is null) localStore.Save(name, mode);
        return new ServerConfigEntry(name, path, ServerConnectionKind.Local, null, mode);
    }

    private static string SandboxPath(ServerConfigEntry profile) => LuaFilePath(profile, ServerLuaFileKind.SandboxVars);

    private static string LuaFilePath(ServerConfigEntry profile, ServerLuaFileKind kind)
    {
        var suffix = kind switch
        {
            ServerLuaFileKind.SandboxVars => "_SandboxVars.lua",
            ServerLuaFileKind.SpawnRegions => "_spawnregions.lua",
            ServerLuaFileKind.SpawnPoints => "_spawnpoints.lua",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var iniPath = profile.IsRemote ? profile.Remote!.RemoteIniPath : profile.Path;
        var path = Path.Combine(Path.GetDirectoryName(iniPath) ?? string.Empty, Path.GetFileNameWithoutExtension(iniPath) + suffix);
        return profile.IsRemote ? path.Replace('\\', '/') : path;
    }

    private static void ValidateLuaFile(ServerLuaFileKind kind, string content)
    {
        if (content.Any(c => c == '\0')) throw new InvalidDataException("Le fichier Lua contient un caractère nul invalide.");
        if (kind == ServerLuaFileKind.SandboxVars && !content.Contains("SandboxVars", StringComparison.Ordinal))
            throw new InvalidDataException("Le fichier SandboxVars doit conserver la table SandboxVars.");
        if (kind is ServerLuaFileKind.SpawnRegions or ServerLuaFileKind.SpawnPoints && content.Length > 0 && !content.Contains("return", StringComparison.Ordinal))
            throw new InvalidDataException("Le fichier de spawn doit retourner une table Lua.");
    }

    private static string RconHost(RemoteServerConnection remote) => string.IsNullOrWhiteSpace(remote.RconHost) ? remote.Host : remote.RconHost;
    private static (string Host, int Port, string Password) RconEndpoint(ServerConfigEntry profile)
    {
        if (profile.IsRemote)
        {
            var remote = profile.Remote!;
            return (RconHost(remote), remote.RconPort, remote.RconPassword);
        }
        var document = ServerConfigDocument.Load(profile.Path);
        var port = int.TryParse(document.Get("RCONPort"), out var parsed) ? parsed : 27015;
        return ("127.0.0.1", port, document.Get("RCONPassword"));
    }
    private static string Template(string name) => $"# Created by PZ Advanced Server Manager\nPublicName={name}\nPublicDescription=\nPassword=\nDefaultPort=16261\nRCONPort=27015\nRCONPassword=\nMaxPlayers=16\nPauseEmpty=true\nDoLuaChecksum=true\nWorkshopItems=\nMods=\nMap=Muldraugh, KY\n";

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Le nom du profil serveur ne peut contenir que lettres, chiffres, tirets et underscores.");
        return name;
    }

    private static void NormalizeAndValidate(RemoteServerConnection connection)
    {
        connection.Name = ValidateName(connection.Name.Trim());
        connection.Host = connection.Host.Trim();
        connection.SshUser = connection.SshUser.Trim();
        connection.SshPrivateKeyPath = connection.SshPrivateKeyPath.Trim();
        connection.RemoteIniPath = connection.RemoteIniPath.Trim();
        connection.StartCommand = connection.StartCommand.Trim();
        connection.RconHost = connection.RconHost.Trim();
        connection.ApiBaseUrl = string.IsNullOrWhiteSpace(connection.ApiBaseUrl) ? PineHostingClient.DefaultApiBaseUrl : connection.ApiBaseUrl.Trim().TrimEnd('/');
        connection.ApiToken = connection.ApiToken.Trim();
        connection.ApiServerIdentifier = connection.ApiServerIdentifier.Trim();
        connection.ProviderServerName = connection.ProviderServerName.Trim();
        if (connection.IsPineHosting)
        {
            if (string.IsNullOrWhiteSpace(connection.RemoteIniPath)) connection.RemoteIniPath = PineHostingClient.DefaultIniPath;
            PineHostingClient.ValidateConnection(connection);
            if (connection.RconPort is < 1 or > 65535) throw new ArgumentException("Le port RCON doit être compris entre 1 et 65535.");
            return;
        }
        if (string.IsNullOrWhiteSpace(connection.RconHost)) connection.RconHost = connection.Host;
        if (string.IsNullOrWhiteSpace(connection.RconHost) || string.IsNullOrWhiteSpace(connection.RconPassword))
            throw new ArgumentException("L'hôte et le mot de passe RCON sont requis pour un profil distant.");
        var hasAnySshSetting = !string.IsNullOrWhiteSpace(connection.Host) || !string.IsNullOrWhiteSpace(connection.SshUser) || !string.IsNullOrWhiteSpace(connection.RemoteIniPath) || !string.IsNullOrWhiteSpace(connection.StartCommand) || !string.IsNullOrWhiteSpace(connection.SshPrivateKeyPath);
        if (hasAnySshSetting && !connection.HasSshConnection)
            throw new ArgumentException("Pour activer SSH, renseignez ensemble l'hôte et l'utilisateur. Le chemin INI reste facultatif.");
        if (connection.SshPort is < 1 or > 65535 || connection.RconPort is < 1 or > 65535)
            throw new ArgumentException("Les ports SSH et RCON doivent être compris entre 1 et 65535.");
        var forbiddenHostCommands = new[] { "reboot", "shutdown", "poweroff", "systemctl reboot", "systemctl poweroff", "systemctl halt", "init 6" };
        if (forbiddenHostCommands.Any(command => connection.StartCommand.Contains(command, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("La commande SSH doit démarrer uniquement Project Zomboid. Les commandes d'arrêt ou de redémarrage de l'hôte sont refusées.");
    }

    private static RemoteServerConnection EnsureConfigurationAccess(ServerConfigEntry profile)
    {
        if (!profile.IsRemote) throw new InvalidOperationException("Ce profil est local.");
        if (!profile.Remote!.HasConfigurationManagement)
            throw new InvalidOperationException("Ce profil utilise RCON uniquement. Activez SSH ou sélectionnez le fournisseur Pine Hosting pour lire et modifier les fichiers distants.");
        return profile.Remote;
    }

    private void PreserveStoredSecrets(RemoteServerConnection connection)
    {
        var existing = remoteStore.Get(connection.Name);
        if (existing is null) return;
        if (string.IsNullOrEmpty(connection.RconPassword)) connection.RconPassword = existing.RconPassword;
        if (string.IsNullOrEmpty(connection.ApiToken)) connection.ApiToken = existing.ApiToken;
    }

    private RemoteServerConnection RequirePine(string name)
    {
        var profile = Get(name);
        if (profile.Remote?.IsPineHosting != true) throw new InvalidOperationException("Cette opération nécessite un profil Pine Hosting.");
        return profile.Remote;
    }

    private static string Backup(string path)
    {
        var backup = path + $".pzasm.{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak";
        File.Copy(path, backup, false);
        foreach (var obsolete in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".pzasm.*.bak", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(20))
        {
            try { File.Delete(obsolete); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return backup;
    }

    private static int AppendDistinct(List<string> target, IEnumerable<string> values)
    {
        var known = target.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var value in values.Select(value => value.Trim()).Where(value => value.Length > 0))
        {
            if (!known.Add(value)) continue;
            target.Add(value);
            added++;
        }
        return added;
    }
}

public enum LocalServerMode
{
    Hosted,
    Dedicated
}

public sealed record ServerConfigEntry(
    string Name,
    string Path,
    ServerConnectionKind Kind,
    RemoteServerConnection? Remote,
    LocalServerMode LocalMode = LocalServerMode.Hosted)
{
    public bool IsRemote => Kind == ServerConnectionKind.Remote;
    public bool IsHostedLocal => !IsRemote && LocalMode == LocalServerMode.Hosted;
    public bool IsDedicatedLocal => !IsRemote && LocalMode == LocalServerMode.Dedicated;
    public bool CanManageConfiguration => !IsRemote || Remote!.HasConfigurationManagement;
    public bool IsPineHosting => Remote?.IsPineHosting == true;
    public bool CanUseProviderConsole => IsPineHosting || !IsRemote;
    public string Location => IsRemote
        ? IsPineHosting
            ? $"Pine Hosting API · {(string.IsNullOrWhiteSpace(Remote!.ProviderServerName) ? Remote.ApiServerIdentifier : Remote.ProviderServerName)}"
            : $"RCON {(string.IsNullOrWhiteSpace(Remote!.RconHost) ? Remote.Host : Remote.RconHost)}:{Remote.RconPort}" + (Remote.HasSshConnection ? $" · SSH {Remote.SshUser}@{Remote.Host}" : string.Empty)
        : Path;
}

public sealed record ServerConfigSummary(IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods, IReadOnlyList<string> Maps);
public sealed record ServerApplyResult(string BackupPath, IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods, IReadOnlyList<string> Maps);
public sealed record ServerContentUpdateResult(string BackupPath, int AddedWorkshopItems, int AddedMods, IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods);
public enum ServerLuaFileKind { SandboxVars, SpawnRegions, SpawnPoints }
public sealed record PineWorldResetResult(PineBackupInfo? SafetyBackup, DateTimeOffset CompletedAt);
