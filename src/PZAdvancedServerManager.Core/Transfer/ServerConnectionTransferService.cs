using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Transfer;

public sealed class ServerConnectionTransferService(ApplicationPaths paths, RemoteServerConnectionStore store)
{
    private const string EnvelopeFormat = "PZASM-SERVERS-ENCRYPTED";
    private const string PayloadFormat = "PZASM-SERVER-CONNECTIONS";
    private const string KeyPrefix = "pzasm-server-transfer://keys/";
    private const int FormatVersion = 1;
    private const int Pbkdf2Iterations = 600_000;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;
    private const int MaximumPrivateKeyBytes = 1024 * 1024;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("PZASM.ServerConnections.Transfer.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ServerConnectionExportResult Export(string password, IReadOnlyCollection<string>? names = null, bool includePrivateKeys = true, string? destinationPath = null)
    {
        ValidatePassword(password);
        var requested = names is null || names.Count == 0 ? null : names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = store.GetAll().Where(connection => requested is null || requested.Contains(connection.Name)).Select(Clone).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("No remote server connection was selected for export.");
        if (requested is not null)
        {
            var missing = requested.Except(selected.Select(connection => connection.Name), StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0) throw new KeyNotFoundException("Unknown remote server connection(s): " + string.Join(", ", missing));
        }

        var payload = new ServerConnectionPayload
        {
            Format = PayloadFormat,
            Version = FormatVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Connections = selected
        };
        if (includePrivateKeys)
        {
            var missingKeys = selected.Where(connection => !string.IsNullOrWhiteSpace(connection.SshPrivateKeyPath) && !File.Exists(connection.SshPrivateKeyPath)).ToArray();
            if (missingKeys.Length > 0)
                throw new FileNotFoundException("SSH private key file(s) are unavailable for: " + string.Join(", ", missingKeys.Select(connection => connection.Name)) + ". Repair the paths or explicitly export without SSH keys.");
            foreach (var connection in selected.Where(connection => !string.IsNullOrWhiteSpace(connection.SshPrivateKeyPath)))
            {
                var info = new FileInfo(connection.SshPrivateKeyPath);
                if (info.Length > MaximumPrivateKeyBytes) throw new InvalidDataException($"SSH private key '{info.Name}' exceeds the 1 MiB safety limit.");
                var bytes = File.ReadAllBytes(info.FullName);
                try
                {
                    var name = SafeFileName(info.Name);
                    payload.PrivateKeys.Add(new TransferredPrivateKey
                    {
                        ConnectionId = connection.Id,
                        FileName = name,
                        Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        Content = Convert.ToBase64String(bytes)
                    });
                    connection.SshPrivateKeyPath = KeyPrefix + connection.Id.ToString("N") + "/" + Uri.EscapeDataString(name);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (plaintext.Length > MaximumPayloadBytes) throw new InvalidDataException("The encrypted server transfer exceeds the 16 MiB safety limit.");
        var salt = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        var key = DeriveKey(password, salt);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var envelope = new ServerConnectionEnvelope
        {
            Format = EnvelopeFormat,
            Version = FormatVersion,
            Kdf = "PBKDF2-SHA256",
            Iterations = Pbkdf2Iterations,
            Salt = Convert.ToBase64String(salt),
            Cipher = "AES-256-GCM",
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Ciphertext = Convert.ToBase64String(ciphertext)
        };
        Directory.CreateDirectory(paths.TransfersRoot);
        var finalPath = string.IsNullOrWhiteSpace(destinationPath) ? null : Path.GetFullPath(destinationPath);
        var outputRoot = finalPath is null ? paths.TransfersRoot : Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(outputRoot);
        var exportPath = finalPath is null
            ? Path.Combine(paths.TransfersRoot, $"server-connections-{Guid.NewGuid():N}.pzasm-servers")
            : finalPath + $".pzasm-export-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(exportPath, JsonSerializer.Serialize(envelope, JsonOptions), new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exportPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (finalPath is not null) File.Move(exportPath, finalPath, true);
            return new ServerConnectionExportResult(finalPath ?? exportPath, "pzasm-server-connections.pzasm-servers", selected.Count, payload.PrivateKeys.Count);
        }
        catch
        {
            if (File.Exists(exportPath)) File.Delete(exportPath);
            throw;
        }
    }

    public ServerConnectionImportResult Import(Stream input, string password, bool replaceExisting)
    {
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        if (memory.Length > MaximumPayloadBytes * 2L) throw new InvalidDataException("The encrypted server transfer is too large.");
        return ImportBytes(memory.ToArray(), password, replaceExisting);
    }

    public ServerConnectionImportResult ImportFile(string file, string password, bool replaceExisting)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("Server transfer file not found.", file);
        if (info.Length > MaximumPayloadBytes * 2L) throw new InvalidDataException("The encrypted server transfer is too large.");
        return ImportBytes(File.ReadAllBytes(file), password, replaceExisting);
    }

    private ServerConnectionImportResult ImportBytes(byte[] envelopeBytes, string password, bool replaceExisting)
    {
        ValidatePassword(password);
        ServerConnectionEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ServerConnectionEnvelope>(envelopeBytes, JsonOptions) ?? throw new InvalidDataException("The server transfer envelope is invalid.");
        }
        catch (JsonException exception) { throw new InvalidDataException("The server transfer envelope is invalid.", exception); }
        if (!envelope.Format.Equals(EnvelopeFormat, StringComparison.Ordinal) || envelope.Version != FormatVersion || envelope.Iterations != Pbkdf2Iterations || envelope.Kdf != "PBKDF2-SHA256" || envelope.Cipher != "AES-256-GCM")
            throw new InvalidDataException("Unsupported server transfer format or cryptographic parameters.");

        byte[] salt;
        byte[] nonce;
        byte[] tag;
        byte[] ciphertext;
        try
        {
            salt = Convert.FromBase64String(envelope.Salt);
            nonce = Convert.FromBase64String(envelope.Nonce);
            tag = Convert.FromBase64String(envelope.Tag);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        }
        catch (FormatException exception) { throw new InvalidDataException("The encrypted server transfer is malformed.", exception); }
        if (salt.Length != 32 || nonce.Length != 12 || tag.Length != 16 || ciphertext.Length == 0 || ciphertext.Length > MaximumPayloadBytes)
            throw new InvalidDataException("The encrypted server transfer has invalid lengths.");

        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(password, salt);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The transfer password is incorrect or the server transfer was modified.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        ServerConnectionPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ServerConnectionPayload>(plaintext, JsonOptions) ?? throw new InvalidDataException("The decrypted server transfer is invalid.");
        }
        catch (JsonException exception) { throw new InvalidDataException("The decrypted server transfer is invalid.", exception); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        if (!payload.Format.Equals(PayloadFormat, StringComparison.Ordinal) || payload.Version != FormatVersion || payload.Connections.Count == 0)
            throw new InvalidDataException("Unsupported or empty server connection payload.");

        ValidateConnections(payload.Connections);
        var localStore = new LocalServerProfileStore(paths);
        var localNameConflicts = payload.Connections.Where(connection => localStore.Get(connection.Name) is not null).Select(connection => connection.Name).ToArray();
        if (localNameConflicts.Length > 0)
            throw new InvalidOperationException("Remote connections cannot replace local server profiles: " + string.Join(", ", localNameConflicts) + ".");
        var current = store.GetAll();
        var conflicts = payload.Connections.Where(incoming => current.Any(existing => existing.Id == incoming.Id || existing.Name.Equals(incoming.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (conflicts.Length > 0 && !replaceExisting)
            throw new InvalidOperationException("Existing server connection(s) would be replaced: " + string.Join(", ", conflicts.Select(connection => connection.Name)) + ". Enable explicit replacement to continue.");

        var stagedRoot = Path.Combine(paths.TransfersRoot, "server-import-" + Guid.NewGuid().ToString("N"));
        var keysStage = Path.Combine(stagedRoot, "keys");
        Directory.CreateDirectory(keysStage);
        FileStream? lease = TransferWorkspaceLease.Acquire(stagedRoot);
        var keyByConnection = payload.PrivateKeys.GroupBy(item => item.ConnectionId).ToDictionary(group => group.Key, group => group.Single());
        try
        {
            foreach (var connection in payload.Connections)
            {
                if (!connection.SshPrivateKeyPath.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
                if (!keyByConnection.TryGetValue(connection.Id, out var transferred)) throw new InvalidDataException($"The SSH private key for '{connection.Name}' is missing.");
                var expectedPrefix = KeyPrefix + connection.Id.ToString("N") + "/";
                if (!connection.SshPrivateKeyPath.StartsWith(expectedPrefix, StringComparison.Ordinal)) throw new InvalidDataException($"The SSH private key reference for '{connection.Name}' is invalid.");
                var fileName = SafeFileName(Uri.UnescapeDataString(connection.SshPrivateKeyPath[expectedPrefix.Length..]));
                if (!fileName.Equals(transferred.FileName, StringComparison.Ordinal)) throw new InvalidDataException($"The SSH private key name for '{connection.Name}' is invalid.");
                byte[] content;
                try { content = Convert.FromBase64String(transferred.Content); }
                catch (FormatException exception) { throw new InvalidDataException($"The SSH private key for '{connection.Name}' is malformed.", exception); }
                try
                {
                    if (content.Length > MaximumPrivateKeyBytes || !Convert.ToHexString(SHA256.HashData(content)).Equals(transferred.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"The SSH private key for '{connection.Name}' failed integrity verification.");
                    var staged = Path.Combine(keysStage, connection.Id.ToString("N"), fileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    File.WriteAllBytes(staged, content);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    connection.SshPrivateKeyPath = Path.Combine(paths.ImportedServerKeysRoot, connection.Id.ToString("N"), fileName);
                }
                finally { CryptographicOperations.ZeroMemory(content); }
            }

            CommitKeysAndConnections(keysStage, payload.Connections, replaceExisting);
            return new ServerConnectionImportResult(payload.Connections.Select(connection => connection.Name).ToArray(), payload.PrivateKeys.Count, conflicts.Length);
        }
        finally
        {
            lease.Dispose();
            TryDeleteDirectory(stagedRoot);
        }
    }

    private void CommitKeysAndConnections(string keysStage, IReadOnlyCollection<RemoteServerConnection> connections, bool replaceExisting)
    {
        Directory.CreateDirectory(paths.ImportedServerKeysRoot);
        var backupRoot = Path.Combine(paths.TransfersRoot, "server-key-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        var installed = new List<string>();
        var backups = new List<(string Backup, string Destination)>();
        try
        {
            foreach (var connection in connections.Where(connection => connection.SshPrivateKeyPath.StartsWith(paths.ImportedServerKeysRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                var source = Path.Combine(keysStage, connection.Id.ToString("N"));
                if (!Directory.Exists(source)) continue;
                var destination = Path.Combine(paths.ImportedServerKeysRoot, connection.Id.ToString("N"));
                if (Directory.Exists(destination))
                {
                    if (!replaceExisting) throw new InvalidOperationException($"Managed SSH key directory already exists for '{connection.Name}'.");
                    var backup = Path.Combine(backupRoot, connection.Id.ToString("N"));
                    Directory.Move(destination, backup);
                    backups.Add((backup, destination));
                }
                Directory.Move(source, destination);
                installed.Add(destination);
            }
            store.Import(connections, replaceExisting);
            TryDeleteDirectory(backupRoot);
        }
        catch
        {
            foreach (var directory in installed.AsEnumerable().Reverse()) TryDeleteDirectory(directory);
            foreach (var item in backups.AsEnumerable().Reverse())
                if (Directory.Exists(item.Backup)) Directory.Move(item.Backup, item.Destination);
            TryDeleteDirectory(backupRoot);
            throw;
        }
    }

    private static void ValidateConnections(IReadOnlyCollection<RemoteServerConnection> connections)
    {
        if (connections.Count > 1000) throw new InvalidDataException("A server transfer cannot contain more than 1,000 connections.");
        if (connections.Any(connection => connection.Id == Guid.Empty || string.IsNullOrWhiteSpace(connection.Name)))
            throw new InvalidDataException("A transferred server connection has no stable identity or name.");
        if (connections.GroupBy(connection => connection.Id).Any(group => group.Count() > 1) || connections.GroupBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("The server transfer contains duplicate identifiers or names.");
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        var material = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(material, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(material); }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new ArgumentException("The transfer password must contain at least 12 characters.", nameof(password));
    }

    private static RemoteServerConnection Clone(RemoteServerConnection connection) =>
        JsonSerializer.Deserialize<RemoteServerConnection>(JsonSerializer.Serialize(connection, JsonOptions), JsonOptions) ?? throw new InvalidDataException("A server connection could not be prepared for transfer.");

    private static string SafeFileName(string value)
    {
        var fileName = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("An SSH private key has an unsafe file name.");
        return fileName;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ServerConnectionEnvelope
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public string Kdf { get; set; } = string.Empty;
        public int Iterations { get; set; }
        public string Salt { get; set; } = string.Empty;
        public string Cipher { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
    }

    private sealed class ServerConnectionPayload
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public List<RemoteServerConnection> Connections { get; set; } = [];
        public List<TransferredPrivateKey> PrivateKeys { get; set; } = [];
    }

    private sealed class TransferredPrivateKey
    {
        public Guid ConnectionId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }
}

public sealed record ServerConnectionExportResult(string Path, string FileName, int Connections, int PrivateKeys);
public sealed record ServerConnectionImportResult(string[] ConnectionNames, int PrivateKeys, int ReplacedConnections)
{
    public int Connections => ConnectionNames.Length;
}
