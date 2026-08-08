using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class ServerWorldDataStore(ApplicationPaths paths)
{
    private const string ArchiveSuffix = ".pzasm-world.zip";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ServerWorldDataStatus Inspect(ServerWorldDataLocation location)
    {
        ValidateLocation(location);
        var hasWorld = Directory.Exists(location.WorldPath);
        var databaseFiles = location.DatabaseFiles.Where(File.Exists).ToArray();
        var hasDatabase = databaseFiles.Length > 0;
        var timestamps = databaseFiles.Select(file => new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)).ToList();
        if (hasWorld) timestamps.Add(new DateTimeOffset(Directory.GetLastWriteTimeUtc(location.WorldPath), TimeSpan.Zero));
        var lastModified = timestamps.DefaultIfEmpty().Max();
        return new ServerWorldDataStatus(
            hasWorld,
            hasDatabase,
            lastModified == default ? null : lastModified,
            location.WorldPath,
            location.DatabasePath);
    }

    public InitialAdminAccountStatus InspectInitialAdminAccount(ServerWorldDataLocation location)
    {
        ValidateLocation(location);
        if (!File.Exists(location.DatabasePath))
            return new InitialAdminAccountStatus(InitialAdminAccountState.Required, "La base joueurs n’existe pas encore.");

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = location.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 2
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'whitelist')";
            if (Convert.ToInt32(tableCommand.ExecuteScalar()) != 1)
                return new InitialAdminAccountStatus(InitialAdminAccountState.Required, "La table des comptes n’a pas encore été initialisée.");

            using var accountCommand = connection.CreateCommand();
            accountCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM whitelist WHERE LOWER(username) = LOWER($username) LIMIT 1)";
            accountCommand.Parameters.AddWithValue("$username", "admin");
            return Convert.ToInt32(accountCommand.ExecuteScalar()) == 1
                ? new InitialAdminAccountStatus(InitialAdminAccountState.Configured, "Le compte « admin » existe dans la base joueurs.")
                : new InitialAdminAccountStatus(InitialAdminAccountState.Required, "Aucun compte « admin » n’existe encore dans la base joueurs.");
        }
        catch (SqliteException exception)
        {
            return new InitialAdminAccountStatus(InitialAdminAccountState.Unknown, $"La base joueurs existe, mais son état admin n’a pas pu être lu (SQLite {exception.SqliteErrorCode}).");
        }
    }

    public IReadOnlyList<ServerWorldBackupInfo> List(string profileName)
    {
        var root = BackupRoot(profileName);
        if (!Directory.Exists(root)) return [];
        var backups = new List<ServerWorldBackupInfo>();
        foreach (var metadataPath in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<ServerWorldBackupInfo>(File.ReadAllText(metadataPath), JsonOptions);
                if (metadata is null || !metadata.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(ArchivePath(root, metadata.Id))) continue;
                backups.Add(metadata);
            }
            catch (JsonException) { }
        }
        return backups.OrderByDescending(backup => backup.CreatedAt).ToArray();
    }

    public async Task<ServerWorldBackupInfo> CreateBackupAsync(
        ServerWorldDataLocation location,
        string reason = "manual",
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateLocation(location);
        var status = Inspect(location);
        if (!status.HasData)
            throw new InvalidOperationException("Aucune donnée de monde n'existe encore pour ce profil. Démarrez le serveur une première fois avant de créer une sauvegarde.");

        var root = BackupRoot(location.ProfileName);
        Directory.CreateDirectory(root);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        var archivePath = ArchivePath(root, id);
        var temporaryArchive = archivePath + ".tmp";
        var sources = EnumerateSources(location).ToArray();
        var sourceBytes = sources.Sum(source => source.Length);
        var createdAt = DateTimeOffset.UtcNow;
        progress?.Report(new OperationProgress("inventory", $"{sources.Length:N0} fichier(s) prêts, {FormatBytes(sourceBytes)} à archiver.", 0, Math.Max(sources.Length, 1)));

        try
        {
            await using (var archiveStream = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                if (status.HasWorld) archive.CreateEntry("world/");
                for (var index = 0; index < sources.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sources[index];
                    progress?.Report(new OperationProgress("archive", $"Archivage de {source.DisplayName}", index, sources.Length));
                    var entry = archive.CreateEntry(source.EntryName, CompressionLevel.Fastest);
                    await using var destination = entry.Open();
                    await using var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(destination, 131072, cancellationToken);
                }

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, new ServerWorldBackupManifest
                {
                    FormatVersion = 1,
                    Id = id,
                    ProfileName = location.ProfileName,
                    CreatedAt = createdAt,
                    Reason = NormalizeReason(reason),
                    HasWorld = status.HasWorld,
                    HasDatabase = status.HasDatabase,
                    HasConfiguration = sources.Any(source => source.EntryName.StartsWith("configuration/", StringComparison.Ordinal)),
                    SourceBytes = sourceBytes,
                    FileCount = sources.Length
                }, JsonOptions, cancellationToken);
            }

            File.Move(temporaryArchive, archivePath, false);
            var hash = await ComputeHashAsync(archivePath, cancellationToken);
            var info = new ServerWorldBackupInfo(
                id,
                location.ProfileName,
                createdAt,
                NormalizeReason(reason),
                new FileInfo(archivePath).Length,
                sourceBytes,
                sources.Length,
                status.HasWorld,
                status.HasDatabase,
                sources.Any(source => source.EntryName.StartsWith("configuration/", StringComparison.Ordinal)),
                hash);
            await WriteMetadataAsync(root, info, cancellationToken);
            progress?.Report(new OperationProgress("verify", $"Sauvegarde vérifiée : {FormatBytes(info.ArchiveBytes)}, SHA-256 {hash[..12]}…", sources.Length, sources.Length));
            return info;
        }
        catch
        {
            TryDeleteFile(temporaryArchive);
            if (!File.Exists(MetadataPath(root, id))) TryDeleteFile(archivePath);
            throw;
        }
    }

    public async Task<ServerWorldRestoreResult> RestoreAsync(
        ServerWorldDataLocation location,
        string backupId,
        bool restoreConfiguration,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateLocation(location);
        var backup = GetBackup(location.ProfileName, backupId);
        var root = BackupRoot(location.ProfileName);
        var archivePath = ArchivePath(root, backup.Id);
        progress?.Report(new OperationProgress("integrity", "Vérification de l'intégrité SHA-256 de l'archive."));
        var actualHash = await ComputeHashAsync(archivePath, cancellationToken);
        if (!actualHash.Equals(backup.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("L'archive ne correspond plus à son empreinte SHA-256. La restauration est refusée.");

        ServerWorldBackupInfo? safetyBackup = null;
        if (Inspect(location).HasData)
            safetyBackup = await CreateBackupAsync(location, "pre-restore", progress, cancellationToken);

        var stagingRoot = Path.Combine(Path.GetDirectoryName(root)!, $".restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            await ExtractValidatedAsync(location, archivePath, stagingRoot, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress("replace", "Remplacement transactionnel des données du monde."));
            ApplyStaged(location, stagingRoot, backup, restoreConfiguration);
            progress?.Report(new OperationProgress("verify", "Monde restauré et chemins de données vérifiés."));
            return new ServerWorldRestoreResult(backup, safetyBackup, restoreConfiguration && backup.HasConfiguration);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    public async Task<ServerWorldResetResult> ResetAsync(
        ServerWorldDataLocation location,
        bool createSafetyBackup = true,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateLocation(location);
        if (!Inspect(location).HasData)
            throw new InvalidOperationException("Ce profil est déjà vierge : aucun monde ni base de joueurs n'a été détecté.");
        ServerWorldBackupInfo? backup = null;
        if (createSafetyBackup)
            backup = await CreateBackupAsync(location, "pre-reset", progress, cancellationToken);
        else
            progress?.Report(new OperationProgress("prepare", "Sauvegarde préalable désactivée conformément au choix confirmé par l'administrateur."));
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new OperationProgress("reset", "Retrait transactionnel du monde et de la base de joueurs."));

        var token = Guid.NewGuid().ToString("N");
        var worldQuarantine = location.WorldPath + $".pzasm-reset-{token}";
        var worldMoved = false;
        var databaseQuarantines = new List<(string Target, string Quarantine)>();
        try
        {
            if (Directory.Exists(location.WorldPath))
            {
                Directory.Move(location.WorldPath, worldQuarantine);
                worldMoved = true;
            }
            foreach (var target in location.DatabaseFiles.Where(File.Exists))
            {
                var quarantine = target + $".pzasm-reset-{token}";
                File.Move(target, quarantine, false);
                databaseQuarantines.Add((target, quarantine));
            }
        }
        catch
        {
            foreach (var (target, quarantine) in databaseQuarantines.AsEnumerable().Reverse())
                if (File.Exists(quarantine)) File.Move(quarantine, target, false);
            if (worldMoved && Directory.Exists(worldQuarantine)) Directory.Move(worldQuarantine, location.WorldPath);
            throw;
        }

        TryDeleteDirectory(worldQuarantine);
        foreach (var (_, quarantine) in databaseQuarantines) TryDeleteFile(quarantine);
        progress?.Report(new OperationProgress("verify", "Fresh start prêt. La configuration, les mods et les SandboxVars sont conservés."));
        return new ServerWorldResetResult(backup);
    }

    public void Delete(string profileName, string backupId)
    {
        var backup = GetBackup(profileName, backupId);
        var root = BackupRoot(profileName);
        File.Delete(ArchivePath(root, backup.Id));
        File.Delete(MetadataPath(root, backup.Id));
    }

    public string EnsureBackupRoot(string profileName)
    {
        var root = BackupRoot(profileName);
        Directory.CreateDirectory(root);
        return root;
    }

    public string GetBackupRoot(string profileName) => BackupRoot(profileName);

    private IEnumerable<BackupSourceFile> EnumerateSources(ServerWorldDataLocation location)
    {
        if (Directory.Exists(location.WorldPath))
        {
            RejectReparsePoint(location.WorldPath);
            foreach (var directory in Directory.EnumerateDirectories(location.WorldPath, "*", SearchOption.AllDirectories)) RejectReparsePoint(directory);
            foreach (var file in Directory.EnumerateFiles(location.WorldPath, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                RejectReparsePoint(file);
                var relative = Path.GetRelativePath(location.WorldPath, file).Replace('\\', '/');
                yield return new BackupSourceFile(file, $"world/{relative}", relative, new FileInfo(file).Length);
            }
        }
        foreach (var databaseFile in location.DatabaseFiles.Where(File.Exists))
        {
            RejectReparsePoint(databaseFile);
            yield return new BackupSourceFile(databaseFile, $"database/{Path.GetFileName(databaseFile)}", Path.GetFileName(databaseFile), new FileInfo(databaseFile).Length);
        }
        foreach (var file in location.ConfigurationFiles.Where(File.Exists).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            RejectReparsePoint(file);
            yield return new BackupSourceFile(file, $"configuration/{Path.GetFileName(file)}", Path.GetFileName(file), new FileInfo(file).Length);
        }
    }

    private async Task ExtractValidatedAsync(ServerWorldDataLocation location, string archivePath, string stagingRoot, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(entry => !entry.FullName.Equals("manifest.json", StringComparison.Ordinal)).ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var normalized = ValidateArchiveEntry(location, entry.FullName);
            progress?.Report(new OperationProgress("extract", $"Extraction de {normalized}", index, entries.Length));
            var target = ResolveChild(stagingRoot, normalized);
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, 131072, cancellationToken);
        }
    }

    private static void ApplyStaged(ServerWorldDataLocation location, string stagingRoot, ServerWorldBackupInfo backup, bool restoreConfiguration)
    {
        var token = Guid.NewGuid().ToString("N");
        var stagedWorld = Path.Combine(stagingRoot, "world");
        var worldRollback = location.WorldPath + $".pzasm-rollback-{token}";
        var databaseRollbacks = new List<(string Target, string Rollback)>();
        var configRollbacks = new List<(string Target, string Rollback)>();
        var worldMoved = false;
        try
        {
            if (Directory.Exists(location.WorldPath))
            {
                Directory.Move(location.WorldPath, worldRollback);
                worldMoved = true;
            }
            foreach (var target in location.DatabaseFiles.Where(File.Exists))
            {
                var rollback = target + $".pzasm-rollback-{token}";
                File.Move(target, rollback, false);
                databaseRollbacks.Add((target, rollback));
            }
            if (restoreConfiguration && backup.HasConfiguration)
            {
                foreach (var target in location.ConfigurationFiles)
                {
                    if (!File.Exists(target)) continue;
                    var rollback = target + $".pzasm-rollback-{token}";
                    File.Move(target, rollback, false);
                    configRollbacks.Add((target, rollback));
                }
            }

            if (backup.HasWorld)
            {
                if (!Directory.Exists(stagedWorld)) throw new InvalidDataException("L'archive déclare un monde mais ne contient pas le dossier world.");
                SafeFileTree.CopyDirectory(stagedWorld, location.WorldPath);
            }
            if (backup.HasDatabase)
            {
                var stagedDatabaseRoot = Path.Combine(stagingRoot, "database");
                var restoredDatabaseFiles = 0;
                foreach (var target in location.DatabaseFiles)
                {
                    var source = Path.Combine(stagedDatabaseRoot, Path.GetFileName(target));
                    if (!File.Exists(source)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, false);
                    restoredDatabaseFiles++;
                }
                if (restoredDatabaseFiles == 0) throw new InvalidDataException("L'archive déclare une base de joueurs mais ses fichiers sont absents.");
            }
            if (restoreConfiguration && backup.HasConfiguration)
            {
                foreach (var target in location.ConfigurationFiles)
                {
                    var source = Path.Combine(stagingRoot, "configuration", Path.GetFileName(target));
                    if (!File.Exists(source)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, false);
                }
            }
        }
        catch
        {
            TryDeleteDirectory(location.WorldPath);
            foreach (var target in location.DatabaseFiles) TryDeleteFile(target);
            if (restoreConfiguration && backup.HasConfiguration)
                foreach (var target in location.ConfigurationFiles) TryDeleteFile(target);
            foreach (var (target, rollback) in configRollbacks.AsEnumerable().Reverse())
                if (File.Exists(rollback)) File.Move(rollback, target, false);
            foreach (var (target, rollback) in databaseRollbacks.AsEnumerable().Reverse())
                if (File.Exists(rollback)) File.Move(rollback, target, false);
            if (worldMoved && Directory.Exists(worldRollback)) Directory.Move(worldRollback, location.WorldPath);
            throw;
        }

        TryDeleteDirectory(worldRollback);
        foreach (var (_, rollback) in databaseRollbacks) TryDeleteFile(rollback);
        foreach (var (_, rollback) in configRollbacks) TryDeleteFile(rollback);
    }

    private ServerWorldBackupInfo GetBackup(string profileName, string backupId)
    {
        ValidateProfileName(profileName);
        ValidateBackupId(backupId);
        var root = BackupRoot(profileName);
        var metadataPath = MetadataPath(root, backupId);
        var archivePath = ArchivePath(root, backupId);
        if (!File.Exists(metadataPath) || !File.Exists(archivePath)) throw new FileNotFoundException("Sauvegarde de monde introuvable.", archivePath);
        var backup = JsonSerializer.Deserialize<ServerWorldBackupInfo>(File.ReadAllText(metadataPath), JsonOptions)
            ?? throw new InvalidDataException("Métadonnées de sauvegarde invalides.");
        if (!backup.Id.Equals(backupId, StringComparison.Ordinal) || !backup.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Les métadonnées de la sauvegarde ne correspondent pas au profil demandé.");
        return backup;
    }

    private async Task WriteMetadataAsync(string root, ServerWorldBackupInfo info, CancellationToken cancellationToken)
    {
        var target = MetadataPath(root, info.Id);
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(info, JsonOptions), cancellationToken);
        File.Move(temporary, target, false);
    }

    private static string ValidateArchiveEntry(ServerWorldDataLocation location, string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Split('/').Any(part => part is ".." or "."))
            throw new InvalidDataException($"Chemin interdit dans l'archive : {entryName}");
        var allowedConfiguration = location.ConfigurationFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalized.Equals("world/", StringComparison.Ordinal) || normalized.StartsWith("world/", StringComparison.Ordinal)) return normalized;
        var allowedDatabase = location.DatabaseFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalized.StartsWith("database/", StringComparison.Ordinal) && allowedDatabase.Contains(Path.GetFileName(normalized))) return normalized;
        if (normalized.StartsWith("configuration/", StringComparison.Ordinal) && allowedConfiguration.Contains(Path.GetFileName(normalized))) return normalized;
        throw new InvalidDataException($"Entrée inattendue dans l'archive : {entryName}");
    }

    private string BackupRoot(string profileName)
    {
        ValidateProfileName(profileName);
        var root = Path.GetFullPath(paths.ServerBackupsRoot(profileName));
        var allowed = Path.GetFullPath(paths.ServerDataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowed, PathComparison)) throw new InvalidOperationException("Dossier de sauvegarde hors de l'espace autorisé.");
        return root;
    }

    private static string ArchivePath(string root, string backupId) => Path.Combine(root, backupId + ArchiveSuffix);
    private static string MetadataPath(string root, string backupId) => Path.Combine(root, backupId + ".json");
    private static string NormalizeReason(string reason) => reason is "pre-reset" or "pre-restore" ? reason : "manual";
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void ValidateLocation(ServerWorldDataLocation location)
    {
        ValidateProfileName(location.ProfileName);
        var userRoot = Path.GetFullPath(location.UserRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var path in new[] { location.WorldPath, location.DatabasePath }.Concat(location.ConfigurationFiles))
        {
            var resolved = Path.GetFullPath(path);
            if (!resolved.StartsWith(userRoot, PathComparison)) throw new InvalidOperationException($"Chemin de données refusé hors du dossier Zomboid : {resolved}");
        }
    }

    private static void ValidateProfileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Nom de profil invalide.", nameof(value));
    }

    private static void ValidateBackupId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Identifiant de sauvegarde invalide.", nameof(value));
    }

    private static string ResolveChild(string root, string relative)
    {
        var allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(allowed, PathComparison)) throw new InvalidDataException($"Extraction refusée hors du dossier temporaire : {relative}");
        return resolved;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Lien symbolique ou point de jonction refusé dans les données du monde : {path}");
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["o", "Kio", "Mio", "Gio", "Tio"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
    }

    private sealed record BackupSourceFile(string SourcePath, string EntryName, string DisplayName, long Length);
    private sealed class ServerWorldBackupManifest
    {
        public int FormatVersion { get; init; }
        public string Id { get; init; } = string.Empty;
        public string ProfileName { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public string Reason { get; init; } = string.Empty;
        public long SourceBytes { get; init; }
        public int FileCount { get; init; }
        public bool HasWorld { get; init; }
        public bool HasDatabase { get; init; }
        public bool HasConfiguration { get; init; }
    }
}

public sealed record ServerWorldDataLocation(
    string ProfileName,
    string UserRoot,
    string WorldPath,
    string DatabasePath,
    IReadOnlyList<string> ConfigurationFiles)
{
    public IReadOnlyList<string> DatabaseFiles =>
    [
        DatabasePath,
        DatabasePath + "-wal",
        DatabasePath + "-shm",
        DatabasePath + "-journal"
    ];
}

public sealed record ServerWorldDataStatus(
    bool HasWorld,
    bool HasDatabase,
    DateTimeOffset? LastModifiedAt,
    string WorldPath,
    string DatabasePath)
{
    public bool HasData => HasWorld || HasDatabase;
}

public sealed record InitialAdminAccountStatus(InitialAdminAccountState State, string Detail)
{
    public bool IsRequired => State == InitialAdminAccountState.Required;
    public bool IsConfigured => State == InitialAdminAccountState.Configured;
}

public enum InitialAdminAccountState
{
    Required,
    Configured,
    Unknown
}

public sealed record ServerWorldBackupInfo(
    string Id,
    string ProfileName,
    DateTimeOffset CreatedAt,
    string Reason,
    long ArchiveBytes,
    long SourceBytes,
    int FileCount,
    bool HasWorld,
    bool HasDatabase,
    bool HasConfiguration,
    string Sha256);

public sealed record ServerWorldRestoreResult(
    ServerWorldBackupInfo RestoredBackup,
    ServerWorldBackupInfo? SafetyBackup,
    bool ConfigurationRestored);

public sealed record ServerWorldResetResult(ServerWorldBackupInfo? SafetyBackup);
