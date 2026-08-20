using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class PineHostingClient
{
    public const string DefaultApiBaseUrl = "https://panel.pinehosting.com";
    public const string DefaultIniPath = "/.cache/Server/Zomboid.ini";
    public const string DefaultWorldPath = "/.cache/Saves/Multiplayer/Zomboid";
    public const string DefaultDatabasePath = "/.cache/db/Zomboid.db";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, PineConsoleCacheEntry> _consoleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _consoleGate = new(1, 1);

    public PineHostingClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public async Task<PineServerInfo> TestAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
        => await GetServerAsync(connection, cancellationToken);

    public async Task<IReadOnlyList<PineServerInfo>> ListServersAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(connection, HttpMethod.Get, "/api/client", cancellationToken: cancellationToken);
        var root = await ReadJsonAsync(response, cancellationToken);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(ParseServer).ToArray();
    }

    public async Task<PineServerInfo> GetServerAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(connection, HttpMethod.Get, ServerPath(connection), cancellationToken: cancellationToken);
        return ParseServer(await ReadJsonAsync(response, cancellationToken));
    }

    public async Task<PineServerResources> GetResourcesAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(connection, HttpMethod.Get, ServerPath(connection) + "/resources", cancellationToken: cancellationToken);
        var attributes = Attributes(await ReadJsonAsync(response, cancellationToken));
        var resources = attributes.TryGetProperty("resources", out var element) ? element : default;
        return new PineServerResources(
            String(attributes, "current_state"),
            Bool(attributes, "is_suspended"),
            Long(resources, "memory_bytes"),
            Double(resources, "cpu_absolute"),
            Long(resources, "disk_bytes"),
            Long(resources, "network_rx_bytes"),
            Long(resources, "network_tx_bytes"),
            Long(resources, "uptime"));
    }

    public async Task<string> ReadFileAsync(RemoteServerConnection connection, string path, CancellationToken cancellationToken = default)
    {
        ValidateRemotePath(path);
        using var response = await SendAsync(connection, HttpMethod.Get, ServerPath(connection) + "/files/contents?file=" + Uri.EscapeDataString(path), cancellationToken: cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<string> WriteFileAsync(RemoteServerConnection connection, string path, string content, CancellationToken cancellationToken = default)
    {
        ValidateRemotePath(path);
        var original = await ReadFileAsync(connection, path, cancellationToken);
        var backupPath = path + $".pzasm.{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.bak";
        await WriteFileUncheckedAsync(connection, backupPath, original, cancellationToken);
        await WriteFileUncheckedAsync(connection, path, content, cancellationToken);
        var persisted = await ReadFileAsync(connection, path, cancellationToken);
        if (!NormalizeText(persisted).Equals(NormalizeText(content), StringComparison.Ordinal))
            throw new IOException($"Pine Hosting a accepté l'écriture de « {path} », mais la relecture diffère. La sauvegarde reste disponible : {backupPath}");
        await RetainConfigurationBackupsAsync(connection, path, 20, cancellationToken);
        return backupPath;
    }

    public async Task SendCommandAsync(RemoteServerConnection connection, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(char.IsControl)) throw new ArgumentException("Commande de console invalide.", nameof(command));
        using var response = await SendJsonAsync(connection, HttpMethod.Post, ServerPath(connection) + "/command", new { command = command.Trim() }, cancellationToken);
    }

    public async Task<PineConsoleSnapshot> ReadConsoleTailAsync(RemoteServerConnection connection, int maximumLines = 240, CancellationToken cancellationToken = default)
    {
        if (maximumLines is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumLines));
        var key = connection.ApiServerIdentifier;
        if (_consoleCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CapturedAt < TimeSpan.FromSeconds(2))
            return cached.Snapshot;

        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
            if (_consoleCache.TryGetValue(key, out cached) && DateTimeOffset.UtcNow - cached.CapturedAt < TimeSpan.FromSeconds(2))
                return cached.Snapshot;
            var snapshot = await ReadConsoleTailCoreAsync(connection, maximumLines, cancellationToken);
            _consoleCache[key] = new PineConsoleCacheEntry(snapshot, DateTimeOffset.UtcNow);
            return snapshot;
        }
        finally
        {
            _consoleGate.Release();
        }
    }

    public async Task SetPowerAsync(RemoteServerConnection connection, PinePowerSignal signal, CancellationToken cancellationToken = default)
    {
        using var response = await SendJsonAsync(connection, HttpMethod.Post, ServerPath(connection) + "/power", new { signal = Signal(signal) }, cancellationToken);
    }

    public async Task<PineServerResources> WaitForStateAsync(RemoteServerConnection connection, IReadOnlySet<string> targetStates, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        PineServerResources? current = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            current = await GetResourcesAsync(connection, cancellationToken);
            if (targetStates.Contains(current.State)) return current;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException($"Pine Hosting n'a pas atteint l'état attendu dans le délai imparti. Dernier état : {current?.State ?? "inconnu"}.");
    }

    public async Task<IReadOnlyList<PineBackupInfo>> ListBackupsAsync(RemoteServerConnection connection, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(connection, HttpMethod.Get, ServerPath(connection) + "/backups", cancellationToken: cancellationToken);
        var root = await ReadJsonAsync(response, cancellationToken);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(ParseBackup).OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task<PineBackupInfo> CreateBackupAsync(RemoteServerConnection connection, string name, bool locked = false, CancellationToken cancellationToken = default)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? $"PZASM {DateTimeOffset.Now:yyyy-MM-dd HH:mm}" : name.Trim();
        if (safeName.Length > 191 || safeName.Any(char.IsControl)) throw new ArgumentException("Nom de sauvegarde Pine invalide.", nameof(name));
        using var response = await SendJsonAsync(connection, HttpMethod.Post, ServerPath(connection) + "/backups", new
        {
            name = safeName,
            ignored = string.Empty,
            include_dbs = true,
            is_locked = locked
        }, cancellationToken);
        return ParseBackup(await ReadJsonAsync(response, cancellationToken));
    }

    public async Task<PineBackupInfo> WaitForBackupAsync(RemoteServerConnection connection, string backupUuid, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(backupUuid, "identifiant de sauvegarde");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var backup = (await ListBackupsAsync(connection, cancellationToken)).FirstOrDefault(x => x.Uuid.Equals(backupUuid, StringComparison.OrdinalIgnoreCase));
            if (backup is not null && backup.CompletedAt is not null)
            {
                if (!backup.IsSuccessful) throw new IOException($"La sauvegarde Pine « {backup.Name} » s'est terminée en échec.");
                return backup;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        throw new TimeoutException("La sauvegarde Pine Hosting n'a pas été confirmée dans le délai imparti.");
    }

    public async Task RestoreBackupAsync(RemoteServerConnection connection, string backupUuid, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(backupUuid, "identifiant de sauvegarde");
        using var response = await SendJsonAsync(connection, HttpMethod.Post, BackupPath(connection, backupUuid) + "/restore", new { truncate = true }, cancellationToken);
    }

    public async Task SetBackupLockAsync(RemoteServerConnection connection, string backupUuid, bool locked, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(backupUuid, "identifiant de sauvegarde");
        using var response = await SendJsonAsync(connection, HttpMethod.Post, BackupPath(connection, backupUuid) + "/lock", new { locked }, cancellationToken);
    }

    public async Task DeleteBackupAsync(RemoteServerConnection connection, string backupUuid, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(backupUuid, "identifiant de sauvegarde");
        using var response = await SendAsync(connection, HttpMethod.Delete, BackupPath(connection, backupUuid), cancellationToken: cancellationToken);
    }

    public async Task<Uri> GetBackupDownloadUriAsync(RemoteServerConnection connection, string backupUuid, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(backupUuid, "identifiant de sauvegarde");
        using var response = await SendAsync(connection, HttpMethod.Get, BackupPath(connection, backupUuid) + "/download", cancellationToken: cancellationToken);
        var attributes = Attributes(await ReadJsonAsync(response, cancellationToken));
        var url = String(attributes, "url");
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : throw new InvalidDataException("Pine Hosting n'a pas renvoyé d'URL de téléchargement valide.");
    }

    public async Task DeleteFilesAsync(RemoteServerConnection connection, string root, IEnumerable<string> files, CancellationToken cancellationToken = default)
    {
        ValidateRemotePath(root);
        var validated = files.Select(file => file.Trim()).Where(file => file.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var file in validated) ValidateFileName(file);
        if (validated.Length == 0) return;
        using var response = await SendJsonAsync(connection, HttpMethod.Post, ServerPath(connection) + "/files/delete", new { root, files = validated }, cancellationToken);
    }

    private async Task RetainConfigurationBackupsAsync(RemoteServerConnection connection, string path, int keep, CancellationToken cancellationToken)
    {
        var separator = path.LastIndexOf('/');
        var directory = separator <= 0 ? "/" : path[..separator];
        var fileName = path[(separator + 1)..];
        try
        {
            using var response = await SendAsync(connection, HttpMethod.Get, ServerPath(connection) + "/files/list?directory=" + Uri.EscapeDataString(directory), cancellationToken: cancellationToken);
            var root = await ReadJsonAsync(response, cancellationToken);
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;
            var obsolete = data.EnumerateArray()
                .Select(Attributes)
                .Where(item => Bool(item, "is_file"))
                .Select(item => new { Name = String(item, "name"), ModifiedAt = Date(item, "modified_at") })
                .Where(item => item.Name.StartsWith(fileName + ".pzasm.", StringComparison.Ordinal) && item.Name.EndsWith(".bak", StringComparison.Ordinal))
                .OrderByDescending(item => item.ModifiedAt)
                .Skip(keep)
                .Select(item => item.Name)
                .ToArray();
            await DeleteFilesAsync(connection, directory, obsolete, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or PineHostingApiException or InvalidDataException or JsonException)
        {
        }
    }

    private async Task WriteFileUncheckedAsync(RemoteServerConnection connection, string path, string content, CancellationToken cancellationToken)
    {
        using var body = new StringContent(content, Encoding.UTF8, "text/plain");
        using var response = await SendAsync(connection, HttpMethod.Post, ServerPath(connection) + "/files/write?file=" + Uri.EscapeDataString(path), body, cancellationToken);
    }

    private async Task<PineConsoleSnapshot> ReadConsoleTailCoreAsync(RemoteServerConnection connection, int maximumLines, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(connection, HttpMethod.Get, ServerPath(connection) + "/websocket", cancellationToken: cancellationToken);
        var root = await ReadJsonAsync(response, cancellationToken);
        var data = root.TryGetProperty("data", out var dataElement) ? dataElement : root;
        var token = String(data, "token");
        var socketValue = String(data, "socket");
        if (string.IsNullOrWhiteSpace(token) || !Uri.TryCreate(socketValue, UriKind.Absolute, out var socketUri) || socketUri.Scheme != "wss")
            throw new InvalidDataException("Pine Hosting n'a pas fourni une WebSocket console sécurisée valide.");

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", connection.ApiBaseUrl.TrimEnd('/'));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        await socket.ConnectAsync(socketUri, timeout.Token);
        await SendWebSocketEventAsync(socket, "auth", [token], timeout.Token);

        var lines = new Queue<string>(maximumLines);
        var authenticated = false;
        var requestedLogs = false;
        var receivedConsole = false;
        while (socket.State == WebSocketState.Open && !timeout.IsCancellationRequested)
        {
            string? payload;
            try
            {
                var receiveTask = ReceiveWebSocketTextAsync(socket, timeout.Token);
                payload = receivedConsole
                    ? await receiveTask.WaitAsync(TimeSpan.FromMilliseconds(650), timeout.Token)
                    : await receiveTask;
            }
            catch (TimeoutException) when (receivedConsole)
            {
                break;
            }

            if (payload is null) break;
            if (!TryParseConsoleEvent(payload, out var eventName, out var arguments)) continue;
            if (eventName.Equals("auth success", StringComparison.OrdinalIgnoreCase))
            {
                authenticated = true;
                if (!requestedLogs)
                {
                    requestedLogs = true;
                    await SendWebSocketEventAsync(socket, "send logs", [null], timeout.Token);
                }
                continue;
            }
            if (eventName.Equals("console output", StringComparison.OrdinalIgnoreCase))
            {
                receivedConsole = true;
                foreach (var argument in arguments)
                    foreach (var rawLine in argument.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                    {
                        var line = StripAnsi(rawLine).TrimEnd();
                        if (line.Length == 0) continue;
                        if (lines.Count == maximumLines) lines.Dequeue();
                        lines.Enqueue(line);
                    }
            }
        }

        if (!authenticated) throw new IOException("La WebSocket Pine Hosting n'a pas confirmé son authentification.");
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "PZASM snapshot complete", CancellationToken.None);
        }
        catch (WebSocketException) { }

        return new PineConsoleSnapshot(lines.ToArray(), DateTimeOffset.UtcNow, "Pine Hosting · console Wings");
    }

    public static bool TryParseConsoleEvent(string payload, out string eventName, out IReadOnlyList<string> arguments)
    {
        eventName = string.Empty;
        arguments = [];
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("event", out var eventElement) || eventElement.ValueKind != JsonValueKind.String) return false;
            eventName = eventElement.GetString() ?? string.Empty;
            if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                arguments = args.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? string.Empty).ToArray();
            return eventName.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task SendWebSocketEventAsync(ClientWebSocket socket, string eventName, object?[] arguments, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { @event = eventName, args = arguments }, JsonOptions);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceiveWebSocketTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(rented, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                stream.Write(rented, 0, result.Count);
                if (stream.Length > 1024 * 1024) throw new IOException("Un message console Pine Hosting dépasse la limite de sécurité d'un mégaoctet.");
                if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string StripAnsi(string value) => Regex.Replace(value, "\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])", string.Empty);

    private async Task<HttpResponseMessage> SendJsonAsync(RemoteServerConnection connection, HttpMethod method, string relativePath, object body, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(body, options: JsonOptions);
        return await SendAsync(connection, method, relativePath, content, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(RemoteServerConnection connection, HttpMethod method, string relativePath, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        var baseUri = new Uri(connection.ApiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath.TrimStart('/')))
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        var detail = await ReadErrorAsync(response, cancellationToken);
        response.Dispose();
        throw new PineHostingApiException(response.StatusCode, detail);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var messages = errors.EnumerateArray().Select(error =>
                    error.TryGetProperty("detail", out var detail) ? detail.GetString() :
                    error.TryGetProperty("code", out var code) ? code.GetString() : null).Where(value => !string.IsNullOrWhiteSpace(value));
                var combined = string.Join(" · ", messages);
                if (!string.IsNullOrWhiteSpace(combined)) return combined;
            }
            return text.Length > 600 ? text[..600] : text;
        }
        catch { return fallback; }
    }

    private static PineServerInfo ParseServer(JsonElement value)
    {
        var attributes = Attributes(value);
        var limits = attributes.TryGetProperty("limits", out var limitElement) ? limitElement : default;
        var features = attributes.TryGetProperty("feature_limits", out var featureElement) ? featureElement : default;
        return new PineServerInfo(
            String(attributes, "identifier"),
            String(attributes, "uuid"),
            String(attributes, "name"),
            String(attributes, "description"),
            String(attributes, "node"),
            Int(limits, "memory"),
            Int(limits, "disk"),
            Int(features, "backups"),
            Int(features, "databases"));
    }

    private static PineBackupInfo ParseBackup(JsonElement value)
    {
        var attributes = Attributes(value);
        return new PineBackupInfo(
            String(attributes, "uuid"),
            String(attributes, "name"),
            Long(attributes, "bytes"),
            String(attributes, "sha256"),
            Bool(attributes, "is_successful"),
            Bool(attributes, "is_locked"),
            Date(attributes, "created_at"),
            Date(attributes, "completed_at"));
    }

    private static JsonElement Attributes(JsonElement value) => value.TryGetProperty("attributes", out var attributes) ? attributes : value;
    private static string String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.GetString() ?? string.Empty : string.Empty;
    private static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static int Int(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetInt32(out var parsed) ? parsed : 0;
    private static long Long(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetInt64(out var parsed) ? parsed : 0;
    private static double Double(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetDouble(out var parsed) ? parsed : 0;
    private static DateTimeOffset? Date(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && DateTimeOffset.TryParse(property.GetString(), out var parsed) ? parsed : null;
    private static string NormalizeText(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static string ServerPath(RemoteServerConnection connection) => "/api/client/servers/" + Uri.EscapeDataString(connection.ApiServerIdentifier);
    private static string BackupPath(RemoteServerConnection connection, string backupUuid) => ServerPath(connection) + "/backups/" + Uri.EscapeDataString(backupUuid);
    private static string Signal(PinePowerSignal signal) => signal switch { PinePowerSignal.Start => "start", PinePowerSignal.Stop => "stop", PinePowerSignal.Restart => "restart", PinePowerSignal.Kill => "kill", _ => throw new ArgumentOutOfRangeException(nameof(signal)) };

    public static void ValidateConnection(RemoteServerConnection connection)
    {
        if (!connection.IsPineHosting) throw new ArgumentException("Ce profil n'utilise pas le fournisseur Pine Hosting.");
        if (!Uri.TryCreate(connection.ApiBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps || !baseUri.Host.Equals("panel.pinehosting.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("L'URL API Pine doit être https://panel.pinehosting.com.");
        if (string.IsNullOrWhiteSpace(connection.ApiToken) || connection.ApiToken.Any(char.IsControl)) throw new ArgumentException("La clé API Pine Hosting est requise.");
        ValidateIdentifier(connection.ApiServerIdentifier, "identifiant serveur Pine");
    }

    private static void ValidateIdentifier(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException($"Le {label} est invalide.");
    }

    private static void ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Contains('\0') || path.Any(char.IsControl) || path.Contains("/../", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal))
            throw new ArgumentException("Chemin distant Pine invalide.", nameof(path));
    }

    private static void ValidateFileName(string file)
    {
        if (file.Contains('/') || file.Contains('\\') || file is "." or ".." || file.Any(char.IsControl)) throw new ArgumentException("Nom de fichier distant Pine invalide.");
    }
}

public enum PinePowerSignal { Start, Stop, Restart, Kill }

public sealed record PineServerInfo(string Identifier, string Uuid, string Name, string Description, string Node, int MemoryLimitMb, int DiskLimitMb, int BackupLimit, int DatabaseLimit);
public sealed record PineServerResources(string State, bool IsSuspended, long MemoryBytes, double CpuAbsolute, long DiskBytes, long NetworkRxBytes, long NetworkTxBytes, long UptimeMilliseconds)
{
    public bool IsRunning => State is "running" or "starting" or "stopping";
}
public sealed record PineConsoleSnapshot(IReadOnlyList<string> Lines, DateTimeOffset CapturedAt, string Source);
public sealed record PineBackupInfo(string Uuid, string Name, long Bytes, string Sha256, bool IsSuccessful, bool IsLocked, DateTimeOffset? CreatedAt, DateTimeOffset? CompletedAt);

internal sealed record PineConsoleCacheEntry(PineConsoleSnapshot Snapshot, DateTimeOffset CapturedAt);

public sealed class PineHostingApiException(HttpStatusCode statusCode, string message) : IOException($"Pine Hosting API ({(int)statusCode}) : {message}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
