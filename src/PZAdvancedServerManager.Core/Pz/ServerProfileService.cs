using System.Text;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class ServerProfileService(
    ApplicationPaths paths,
    PzEnvironmentService environment,
    ServerOrchestrationService orchestration)
{
    public IReadOnlyList<ServerConfigEntry> List()
    {
        var root = ServerRoot;
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.ini").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => new ServerConfigEntry(Path.GetFileNameWithoutExtension(x), x)).ToList()
            : [];
    }

    public ServerConfigEntry Get(string name)
    {
        var validated = ValidateName(name);
        var path = Path.Combine(ServerRoot, validated + ".ini");
        if (!File.Exists(path)) throw new FileNotFoundException("Profil serveur introuvable.", path);
        return new ServerConfigEntry(validated, path);
    }

    public ServerConfigEntry Create(string name)
    {
        var validated = ValidateName(name);
        Directory.CreateDirectory(ServerRoot);
        var path = Path.Combine(ServerRoot, validated + ".ini");
        if (File.Exists(path)) throw new IOException("Ce profil serveur existe déjà.");
        var template = $"# Created by PZ Advanced Server Manager\nPublicName={validated}\nPublicDescription=\nPassword=\nDefaultPort=16261\nRCONPort=27015\nRCONPassword=\nMaxPlayers=16\nPauseEmpty=true\nDoLuaChecksum=true\nWorkshopItems=\nMods=\nMap=Muldraugh, KY\n";
        File.WriteAllText(path, template.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        return new ServerConfigEntry(validated, path);
    }

    public string ReadRaw(string name) => ServerConfigDocument.ReadText(Get(name).Path).Text;

    public string SaveRaw(string name, string content)
    {
        var profile = Get(name);
        var backup = Backup(profile.Path);
        var original = ServerConfigDocument.ReadText(profile.Path);
        var encoding = original.Encoding;
        var newLine = original.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var temp = profile.Path + ".pzasm.tmp";
        var normalized = content.Replace("\r\n", "\n").Replace("\n", newLine);
        File.WriteAllText(temp, normalized, encoding);
        File.Move(temp, profile.Path, true);
        return backup;
    }

    public string Set(string name, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Any(char.IsControl))
            throw new ArgumentException("Clé de configuration invalide.", nameof(key));
        var profile = Get(name);
        var backup = Backup(profile.Path);
        var document = ServerConfigDocument.Load(profile.Path);
        document.Set(key.Trim(), value);
        document.Save(profile.Path);
        return backup;
    }

    public string Update(string name, IReadOnlyDictionary<string, string> values)
    {
        if (values.Keys.Any(key => string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Any(char.IsControl)))
            throw new ArgumentException("Une clé de configuration est invalide.", nameof(values));
        var profile = Get(name);
        var backup = Backup(profile.Path);
        var document = ServerConfigDocument.Load(profile.Path);
        foreach (var (key, value) in values) document.Set(key.Trim(), value);
        document.Save(profile.Path);
        return backup;
    }

    public ServerContentUpdateResult AddContent(string name, IEnumerable<ulong> workshopIds, IEnumerable<string> modIds)
    {
        var profile = Get(name);
        var document = ServerConfigDocument.Load(profile.Path);
        var workshop = document.GetList("WorkshopItems").ToList();
        var mods = document.GetList("Mods").ToList();
        var addedWorkshop = AppendDistinct(workshop, workshopIds.Where(id => id != 0).Select(id => id.ToString()));
        var addedMods = AppendDistinct(mods, modIds.Where(id => !string.IsNullOrWhiteSpace(id)));
        if (addedWorkshop == 0 && addedMods == 0)
            return new ServerContentUpdateResult(string.Empty, 0, 0, workshop, mods);

        var backup = Backup(profile.Path);
        document.Set("WorkshopItems", string.Join(';', workshop));
        document.Set("Mods", string.Join(';', mods));
        document.Save(profile.Path);
        return new ServerContentUpdateResult(backup, addedWorkshop, addedMods, workshop, mods);
    }

    public async Task<bool> IsOnlineAsync(string name, CancellationToken cancellationToken = default) =>
        await orchestration.IsOnlineAsync(Get(name).Path, cancellationToken);

    public void Start(string name)
    {
        var profile = Get(name);
        var dedicatedRoot = environment.Installation.DedicatedServerRoot
            ?? throw new DirectoryNotFoundException("Installation Project Zomboid Dedicated Server introuvable.");
        orchestration.Start(profile.Name, dedicatedRoot);
    }

    public async Task StopAsync(string name, CancellationToken cancellationToken = default) =>
        await orchestration.StopGracefullyAsync(Get(name).Path, cancellationToken);

    public async Task<ServerApplyResult> ApplyPackageAsync(string name, PackageProject project, CancellationToken cancellationToken = default)
    {
        var profile = Get(name);
        if (await orchestration.IsOnlineAsync(profile.Path, cancellationToken))
            throw new InvalidOperationException("Arrêtez d'abord le serveur : PZASM refuse d'appliquer un pack pendant qu'il est en ligne.");
        if (project.PublishedWorkshopId == 0)
            throw new InvalidOperationException("Le pack doit être publié avant son application au serveur.");
        var snippetPath = Path.Combine(paths.BuildRoot(project.Id), "server-config.txt");
        if (!File.Exists(snippetPath)) throw new FileNotFoundException("Construisez le pack avant de l'appliquer.", snippetPath);

        var backup = Backup(profile.Path);
        var source = ServerConfigDocument.Load(snippetPath);
        if (!source.Get("WorkshopItems").Equals(project.PublishedWorkshopId.ToString(), StringComparison.Ordinal))
            throw new InvalidOperationException("La configuration générée ne correspond pas au Workshop ID actuel. Reconstruisez le pack avant de l'appliquer.");
        var target = ServerConfigDocument.Load(profile.Path);
        target.Set("WorkshopItems", source.Get("WorkshopItems"));
        target.Set("Mods", source.Get("Mods"));
        target.Set("Map", source.Get("Map"));
        target.Save(profile.Path);
        return new ServerApplyResult(backup, source.GetList("WorkshopItems"), source.GetList("Mods"), source.GetList("Map"));
    }

    public ServerConfigSummary ReadSummary(string name)
    {
        var document = ServerConfigDocument.Load(Get(name).Path);
        return new ServerConfigSummary(document.GetList("WorkshopItems"), document.GetList("Mods"), document.GetList("Map"));
    }

    public string ResolveIniPath(string name) => Get(name).Path;

    private string ServerRoot => Path.Combine(environment.Installation.UserZomboidRoot, "Server");

    private static string ValidateName(string name)
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

public sealed record ServerConfigEntry(string Name, string Path);
public sealed record ServerConfigSummary(IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods, IReadOnlyList<string> Maps);
public sealed record ServerApplyResult(string BackupPath, IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods, IReadOnlyList<string> Maps);
public sealed record ServerContentUpdateResult(string BackupPath, int AddedWorkshopItems, int AddedMods, IReadOnlyList<string> WorkshopItems, IReadOnlyList<string> Mods);
