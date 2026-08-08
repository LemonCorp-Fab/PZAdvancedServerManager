using System.Text;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class ServerProfileService(
    ApplicationPaths paths,
    PzEnvironmentService environment,
    ServerOrchestrationService orchestration,
    RemoteServerConnectionStore remoteStore,
    SshRemoteServerService ssh)
{
    public PzInstallation Installation => environment.Installation;

    public IReadOnlyList<ServerConfigEntry> List()
    {
        var entries = new List<ServerConfigEntry>();
        if (Directory.Exists(ServerRoot))
            entries.AddRange(Directory.EnumerateFiles(ServerRoot, "*.ini").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => new ServerConfigEntry(Path.GetFileNameWithoutExtension(x), x, ServerConnectionKind.Local, null)));
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
        return new ServerConfigEntry(validated, path, ServerConnectionKind.Local, null);
    }

    public ServerConfigEntry Create(string name)
    {
        var validated = ValidateName(name);
        if (remoteStore.Get(validated) is not null) throw new IOException("Un profil distant utilise déjà ce nom.");
        Directory.CreateDirectory(ServerRoot);
        var path = Path.Combine(ServerRoot, validated + ".ini");
        if (File.Exists(path)) throw new IOException("Ce profil serveur existe déjà.");
        File.WriteAllText(path, Template(validated).Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        return new ServerConfigEntry(validated, path, ServerConnectionKind.Local, null);
    }

    public async Task<ServerConfigEntry> CreateRemoteAsync(RemoteServerConnection connection, bool createConfigIfMissing, CancellationToken cancellationToken = default)
    {
        NormalizeAndValidate(connection);
        if (remoteStore.Get(connection.Name) is not null || File.Exists(Path.Combine(ServerRoot, connection.Name + ".ini")))
            throw new IOException("Un profil local ou distant utilise déjà ce nom.");
        if (connection.HasSshConnection)
        {
            await ssh.TestAsync(connection, cancellationToken);
            if (connection.HasSshManagement)
            {
                try
                {
                    _ = await ssh.ReadFileAsync(connection, cancellationToken);
                }
                catch when (createConfigIfMissing)
                {
                    await ssh.WriteFileAsync(connection, Template(connection.Name), cancellationToken);
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
        NormalizeAndValidate(connection);
        if (connection.HasSshConnection) await ssh.TestAsync(connection, cancellationToken);
        remoteStore.Save(connection);
    }

    public async Task TestRemoteAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        PreserveStoredRconPassword(connection);
        NormalizeAndValidate(connection);
        if (!connection.HasSshConnection) throw new InvalidOperationException("Ajoutez l'hôte et l'utilisateur SSH pour tester la connexion facultative.");
        await ssh.TestAsync(connection, cancellationToken);
    }

    public async Task TestRconAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        PreserveStoredRconPassword(connection);
        NormalizeAndValidate(connection);
        if (!await orchestration.IsOnlineAsync(RconHost(connection), connection.RconPort, connection.RconPassword, cancellationToken))
            throw new IOException("Project Zomboid n'a pas accepté la connexion RCON. Vérifiez l'hôte, le port, le mot de passe et l'état du jeu.");
    }

    public bool RemoveRemote(string name) => remoteStore.Remove(ValidateName(name));

    public string ReadRaw(string name)
    {
        var profile = Get(name);
        if (profile.IsRemote) EnsureConfigurationAccess(profile);
        return profile.IsRemote
            ? ssh.ReadFileAsync(profile.Remote!).GetAwaiter().GetResult()
            : ServerConfigDocument.ReadText(profile.Path).Text;
    }

    public string SaveRaw(string name, string content)
    {
        var profile = Get(name);
        if (profile.IsRemote) EnsureConfigurationAccess(profile);
        string backup;
        if (profile.IsRemote)
        {
            backup = ssh.WriteFileAsync(profile.Remote!, content).GetAwaiter().GetResult();
        }
        else
        {
            backup = Backup(profile.Path);
            var original = ServerConfigDocument.ReadText(profile.Path);
            var newLine = original.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var temp = profile.Path + ".pzasm.tmp";
            File.WriteAllText(temp, content.Replace("\r\n", "\n").Replace("\n", newLine), original.Encoding);
            File.Move(temp, profile.Path, true);
        }
        var persisted = profile.IsRemote
            ? ssh.ReadFileAsync(profile.Remote!).GetAwaiter().GetResult()
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
            return orchestration.IsManagedProcessRunning(profile.Name) || await orchestration.IsOnlineAsync(profile.Path, cancellationToken);
        var remote = profile.Remote!;
        return await orchestration.IsOnlineAsync(RconHost(remote), remote.RconPort, remote.RconPassword, cancellationToken);
    }

    public async Task<bool> IsRconPortReachableAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (!profile.IsRemote) return await orchestration.IsPortReachableAsync(profile.Path, cancellationToken);
        var remote = profile.Remote!;
        return await orchestration.IsPortReachableAsync(RconHost(remote), remote.RconPort, cancellationToken);
    }

    public void Start(string name) => StartAsync(name).GetAwaiter().GetResult();
    public bool CanStart(string name)
    {
        var profile = Get(name);
        return !profile.IsRemote || profile.Remote!.HasSshConnection && !string.IsNullOrWhiteSpace(profile.Remote.StartCommand);
    }

    public bool CanCoordinateRestart(string name)
    {
        var profile = Get(name);
        return !profile.IsRemote || CanStart(name) || profile.Remote!.AutoRestartAfterRconQuit;
    }

    public async Task StartAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (profile.IsRemote)
        {
            await ssh.RunStartCommandAsync(profile.Remote!, cancellationToken);
            return;
        }
        var dedicatedRoot = environment.Installation.DedicatedServerRoot
            ?? throw new DirectoryNotFoundException("Installation Project Zomboid Dedicated Server introuvable.");
        orchestration.Start(profile.Name, dedicatedRoot);
    }

    public async Task StopAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (!profile.IsRemote)
        {
            await orchestration.StopGracefullyAsync(profile.Path, cancellationToken);
            return;
        }
        var remote = profile.Remote!;
        await orchestration.StopGracefullyAsync(RconHost(remote), remote.RconPort, remote.RconPassword, cancellationToken);
    }

    public async Task RestartViaRconAsync(string name, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        var (host, port, password) = RconEndpoint(profile);
        await orchestration.RequestRestartAsync(host, port, password, cancellationToken);
    }

    public async Task<string> ExecuteRconCommandAsync(string name, string command, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
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
            var sandboxConnection = WithRemotePath(profile.Remote!, SandboxPath(profile));
            return SandboxSettingsDocument.Parse(ssh.ReadFileAsync(sandboxConnection).GetAwaiter().GetResult());
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
            backup = ssh.WriteFileAsync(WithRemotePath(profile.Remote!, SandboxPath(profile)), document.Render()).GetAwaiter().GetResult();
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
        if (profile.IsRemote) return ssh.ReadFileAsync(WithRemotePath(profile.Remote!, path)).GetAwaiter().GetResult();
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
            backup = ssh.WriteFileAsync(WithRemotePath(profile.Remote!, path), content).GetAwaiter().GetResult();
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            backup = File.Exists(path) ? Backup(path) : string.Empty;
            var encoding = File.Exists(path) ? ServerConfigDocument.ReadText(path).Encoding : new UTF8Encoding(false);
            var temporary = path + ".pzasm.tmp";
            File.WriteAllText(temporary, content, encoding);
            File.Move(temporary, path, true);
        }
        var persisted = ReadLuaFile(name, kind);
        if (!NormalizeText(persisted).Equals(NormalizeText(content), StringComparison.Ordinal))
            throw new IOException($"Le fichier {Path.GetFileName(path)} a été écrit mais sa relecture diffère. Sauvegarde : {backup}");
        return backup;
    }

    private ServerConfigDocument ReadDocument(ServerConfigEntry profile) => profile.IsRemote
        ? ServerConfigDocument.Parse(ssh.ReadFileAsync(EnsureConfigurationAccess(profile)).GetAwaiter().GetResult())
        : ServerConfigDocument.Load(profile.Path);

    private string WriteDocument(ServerConfigEntry profile, ServerConfigDocument document)
    {
        var expected = NormalizeText(document.Render());
        string backup;
        if (profile.IsRemote)
            backup = ssh.WriteFileAsync(EnsureConfigurationAccess(profile), document.Render()).GetAwaiter().GetResult();
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

    private static RemoteServerConnection WithRemotePath(RemoteServerConnection source, string path) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Host = source.Host,
        SshPort = source.SshPort,
        SshUser = source.SshUser,
        SshPrivateKeyPath = source.SshPrivateKeyPath,
        RemoteIniPath = path,
        StartCommand = source.StartCommand,
        RconHost = source.RconHost,
        RconPort = source.RconPort,
        RconPassword = source.RconPassword,
        AutoRestartAfterRconQuit = source.AutoRestartAfterRconQuit,
        UpdatedAt = source.UpdatedAt
    };
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
        if (!profile.Remote!.HasSshManagement)
            throw new InvalidOperationException("Ce profil utilise RCON uniquement. Activez la gestion SSH facultative pour lire ou modifier l'INI distant.");
        return profile.Remote;
    }

    private void PreserveStoredRconPassword(RemoteServerConnection connection)
    {
        if (!string.IsNullOrEmpty(connection.RconPassword)) return;
        var existing = remoteStore.Get(connection.Name);
        if (existing is not null) connection.RconPassword = existing.RconPassword;
    }

    private static string Backup(string path)
    {
        var backup = path + $".pzasm.{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak";
        File.Copy(path, backup, false);
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

public sealed record ServerConfigEntry(string Name, string Path, ServerConnectionKind Kind, RemoteServerConnection? Remote)
{
    public bool IsRemote => Kind == ServerConnectionKind.Remote;
    public bool CanManageConfiguration => !IsRemote || Remote!.HasSshManagement;
    public string Location => IsRemote
        ? $"RCON {(string.IsNullOrWhiteSpace(Remote!.RconHost) ? Remote.Host : Remote.RconHost)}:{Remote.RconPort}" + (Remote.HasSshConnection ? $" · SSH {Remote.SshUser}@{Remote.Host}" : string.Empty)
        : Path;
}

public sealed record ServerConfigSummary(IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods, IReadOnlyList<string> Maps);
public sealed record ServerApplyResult(string BackupPath, IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods, IReadOnlyList<string> Maps);
public sealed record ServerContentUpdateResult(string BackupPath, int AddedWorkshopItems, int AddedMods, IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods);
public enum ServerLuaFileKind { SandboxVars, SpawnRegions, SpawnPoints }
