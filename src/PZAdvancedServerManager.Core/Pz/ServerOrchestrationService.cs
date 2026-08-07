using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class ServerOrchestrationService
{
    public async Task<bool> IsOnlineAsync(string iniPath, CancellationToken cancellationToken = default)
    {
        var settings = ReadRconSettings(iniPath, requirePassword: false);
        return await IsOnlineAsync("127.0.0.1", settings.Port, settings.Password, cancellationToken);
    }

    public async Task<bool> IsOnlineAsync(string host, int port, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password)) return false;
        try
        {
            await using var rcon = await PzRconClient.ConnectAsync(host, port, password, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return false;
        }
    }

    public async Task<bool> IsPortReachableAsync(string iniPath, CancellationToken cancellationToken = default)
    {
        var settings = ReadRconSettings(iniPath, requirePassword: false);
        return await IsPortReachableAsync("127.0.0.1", settings.Port, cancellationToken);
    }

    public async Task<bool> IsPortReachableAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or IOException or OperationCanceledException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return false;
        }
    }

    public async Task StopGracefullyAsync(string iniPath, CancellationToken cancellationToken = default)
    {
        var settings = ReadRconSettings(iniPath, requirePassword: true);
        await StopGracefullyAsync("127.0.0.1", settings.Port, settings.Password, cancellationToken);
    }

    public async Task StopGracefullyAsync(string host, int port, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Un mot de passe RCON est requis pour sauvegarder et arrêter proprement Project Zomboid.");
        await SendSaveAndQuitAsync(host, port, password, cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await IsOnlineAsync(host, port, password, cancellationToken)) return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("Le serveur n'a pas fermé son port RCON dans le délai prévu. Aucune terminaison forcée n'a été effectuée.");
    }

    public async Task RequestRestartAsync(string host, int port, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Un mot de passe RCON est requis pour sauvegarder et redémarrer Project Zomboid.");
        await SendSaveAndQuitAsync(host, port, password, cancellationToken);
    }

    public async Task<string> ExecuteCommandAsync(string host, int port, string password, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Un mot de passe RCON est requis.");
        command = command.Trim();
        if (command.Length is < 1 or > 256 || command.Any(char.IsControl))
            throw new ArgumentException("La commande RCON doit contenir entre 1 et 256 caractères sans caractère de contrôle.", nameof(command));
        await using var rcon = await PzRconClient.ConnectAsync(host, port, password, cancellationToken);
        return await rcon.CommandAsync(command, cancellationToken);
    }

    private static async Task SendSaveAndQuitAsync(string host, int port, string password, CancellationToken cancellationToken)
    {
        await using var rcon = await PzRconClient.ConnectAsync(host, port, password, cancellationToken);
        await rcon.CommandAsync("save", cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        try { await rcon.CommandAsync("quit", cancellationToken); }
        catch (IOException) { /* The server may close the connection immediately after the quit command. */ }
    }

    public void Start(string serverName, string dedicatedServerRoot)
    {
        if (string.IsNullOrWhiteSpace(serverName) || serverName.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Nom de profil serveur invalide.", nameof(serverName));
        var script = Path.Combine(dedicatedServerRoot, OperatingSystem.IsWindows() ? "StartServer64.bat" : "start-server.sh");
        if (!File.Exists(script)) throw new FileNotFoundException("Script de démarrage du serveur dédié introuvable.", script);

        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dedicatedServerRoot
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add($"call \"{script}\" -servername \"{serverName}\" < NUL");
        }
        else
        {
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-servername");
            start.ArgumentList.Add(serverName);
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException("Le processus serveur n'a pas pu démarrer.");
        process.Dispose();
    }

    private static RconSettings ReadRconSettings(string iniPath, bool requirePassword)
    {
        if (!File.Exists(iniPath)) throw new FileNotFoundException("Configuration serveur introuvable.", iniPath);
        var config = ServerConfigDocument.Load(iniPath);
        var port = int.TryParse(config.Get("RCONPort"), out var parsed) ? parsed : 27015;
        var password = config.Get("RCONPassword");
        if (requirePassword && string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Définissez RCONPassword dans le profil serveur pour permettre un arrêt save/quit propre.");
        return new RconSettings(port, password);
    }

    private sealed record RconSettings(int Port, string Password);
}

internal sealed class PzRconClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private int _requestId = Random.Shared.Next(1, int.MaxValue - 1000);

    private PzRconClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public static async Task<PzRconClient> ConnectAsync(string host, int port, string password, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var client = new PzRconClient(tcp);
        var id = client.NextId();
        await client.SendAsync(id, 3, password, cancellationToken);
        for (var i = 0; i < 3; i++)
        {
            var response = await client.ReceiveAsync(cancellationToken);
            if (response.Id == -1) throw new UnauthorizedAccessException("Authentification RCON refusée.");
            if (response.Type == 2 && response.Id == id) return client;
        }
        await client.DisposeAsync();
        throw new IOException("Réponse d'authentification RCON invalide.");
    }

    public async Task<string> CommandAsync(string command, CancellationToken cancellationToken)
    {
        var id = NextId();
        await SendAsync(id, 2, command, cancellationToken);
        var response = await ReceiveAsync(cancellationToken);
        if (response.Id != id) throw new IOException("Identifiant de réponse RCON inattendu.");
        return response.Body;
    }

    private int NextId() => Interlocked.Increment(ref _requestId);

    private async Task SendAsync(int id, int type, string body, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var length = 4 + 4 + bodyBytes.Length + 2;
        var packet = new byte[length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), type);
        bodyBytes.CopyTo(packet, 12);
        await _stream.WriteAsync(packet, cancellationToken);
    }

    private async Task<RconPacket> ReceiveAsync(CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await ReadExactlyAsync(lengthBytes, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length < 10 || length > 1024 * 1024) throw new IOException("Taille de paquet RCON invalide.");
        var payload = new byte[length];
        await ReadExactlyAsync(payload, cancellationToken);
        var id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        var body = Encoding.UTF8.GetString(payload, 8, length - 10);
        return new RconPacket(id, type, body);
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await _stream.ReadAsync(buffer.AsMemory(read), cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (count == 0) throw new IOException("Connexion RCON fermée.");
            read += count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }

    private sealed record RconPacket(int Id, int Type, string Body);
}
