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
        await ssh.TestAsync(connection, cancellationToken);
        try
        {
            _ = await ssh.ReadFileAsync(connection, cancellationToken);
        }
        catch when (createConfigIfMissing)
        {
            await ssh.WriteFileAsync(connection, Template(connection.Name), cancellationToken);
        }
        remoteStore.Save(connection);
        return new ServerConfigEntry(connection.Name, connection.RemoteIniPath, ServerConnectionKind.Remote, connection);
    }

    public async Task UpdateRemoteAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        NormalizeAndValidate(connection);
        var existing = remoteStore.Get(connection.Name) ?? throw new KeyNotFoundException("Profil serveur distant introuvable.");
        connection.Id = existing.Id;
        if (string.IsNullOrEmpty(connection.RconPassword)) connection.RconPassword = existing.RconPassword;
        await ssh.TestAsync(connection, cancellationToken);
        remoteStore.Save(connection);
    }

    public async Task TestRemoteAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        NormalizeAndValidate(connection);
        await ssh.TestAsync(connection, cancellationToken);
    }

    public bool RemoveRemote(string name) => remoteStore.Remove(ValidateName(name));

    public string ReadRaw(string name)
    {
        var profile = Get(name);
        return profile.IsRemote
            ? ssh.ReadFileAsync(profile.Remote!).GetAwaiter().GetResult()
            : ServerConfigDocument.ReadText(profile.Path).Text;
    }

    public string SaveRaw(string name, string content)
    {
        var profile = Get(name);
        if (profile.IsRemote) return ssh.WriteFileAsync(profile.Remote!, content).GetAwaiter().GetResult();
        var backup = Backup(profile.Path);
        var original = ServerConfigDocument.ReadText(profile.Path);
        var newLine = original.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var temp = profile.Path + ".pzasm.tmp";
        File.WriteAllText(temp, content.Replace("\r\n", "\n").Replace("\n", newLine), original.Encoding);
        File.Move(temp, profile.Path, true);
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
        if (!profile.IsRemote) return await orchestration.IsOnlineAsync(profile.Path, cancellationToken);
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
        return !profile.IsRemote || !string.IsNullOrWhiteSpace(profile.Remote!.StartCommand);
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

    private ServerConfigDocument ReadDocument(ServerConfigEntry profile) => profile.IsRemote
        ? ServerConfigDocument.Parse(ssh.ReadFileAsync(profile.Remote!).GetAwaiter().GetResult())
        : ServerConfigDocument.Load(profile.Path);

    private string WriteDocument(ServerConfigEntry profile, ServerConfigDocument document)
    {
        if (profile.IsRemote) return ssh.WriteFileAsync(profile.Remote!, document.Render()).GetAwaiter().GetResult();
        var backup = Backup(profile.Path);
        document.Save(profile.Path);
        return backup;
    }

    private string ServerRoot => Path.Combine(environment.Installation.UserZomboidRoot, "Server");
    private static string RconHost(RemoteServerConnection remote) => string.IsNullOrWhiteSpace(remote.RconHost) ? remote.Host : remote.RconHost;
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
        if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.SshUser) || string.IsNullOrWhiteSpace(connection.RemoteIniPath))
            throw new ArgumentException("Hôte, utilisateur SSH et chemin INI distant sont requis.");
        if (connection.SshPort is < 1 or > 65535 || connection.RconPort is < 1 or > 65535)
            throw new ArgumentException("Les ports SSH et RCON doivent être compris entre 1 et 65535.");
        var forbiddenHostCommands = new[] { "reboot", "shutdown", "poweroff", "systemctl reboot", "systemctl poweroff", "systemctl halt", "init 6" };
        if (forbiddenHostCommands.Any(command => connection.StartCommand.Contains(command, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("La commande SSH doit démarrer uniquement Project Zomboid. Les commandes d'arrêt ou de redémarrage de l'hôte sont refusées.");
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
    public string Location => IsRemote ? $"{Remote!.SshUser}@{Remote.Host}:{Path}" : Path;
}

public sealed record ServerConfigSummary(IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods, IReadOnlyList<string> Maps);
public sealed record ServerApplyResult(string BackupPath, IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods, IReadOnlyList<string> Maps);
public sealed record ServerContentUpdateResult(string BackupPath, int AddedWorkshopItems, int AddedMods, IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods);
