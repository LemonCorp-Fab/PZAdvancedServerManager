using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class ServerOrchestrationService
{
    private readonly ConcurrentDictionary<string, Process> _managedServerProcesses = new(StringComparer.OrdinalIgnoreCase);

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

    public async Task<bool> IsRconServiceAsync(string iniPath, CancellationToken cancellationToken = default)
    {
        var settings = ReadRconSettings(iniPath, requirePassword: false);
        return await IsRconServiceAsync("127.0.0.1", settings.Port, settings.Password, cancellationToken);
    }

    public async Task<bool> IsRconServiceAsync(string host, int port, string password, CancellationToken cancellationToken = default)
    {
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var rcon = await PzRconClient.ConnectAsync(
                host,
                port,
                string.IsNullOrWhiteSpace(password) ? $"pzasm-probe-{Guid.NewGuid():N}" : password,
                probeTimeout.Token);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
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

    public void Start(string serverName, string dedicatedServerRoot) =>
        Start(serverName, dedicatedServerRoot, null, TimeSpan.FromSeconds(12));

    public void Start(string serverName, string dedicatedServerRoot, TimeSpan startupProbeDuration)
        => Start(serverName, dedicatedServerRoot, null, startupProbeDuration);

    public void Start(string serverName, string dedicatedServerRoot, string? initialAdminPassword)
        => Start(serverName, dedicatedServerRoot, initialAdminPassword, TimeSpan.FromSeconds(12));

    public void Start(string serverName, string dedicatedServerRoot, string? initialAdminPassword, TimeSpan startupProbeDuration)
    {
        if (string.IsNullOrWhiteSpace(serverName) || serverName.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Nom de profil serveur invalide.", nameof(serverName));
        if (startupProbeDuration < TimeSpan.FromMilliseconds(250) || startupProbeDuration > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(startupProbeDuration));
        if (IsManagedProcessRunning(serverName))
            throw new InvalidOperationException($"Le serveur « {serverName} » a déjà été lancé par le manager.");
        initialAdminPassword = ValidateInitialAdminPassword(initialAdminPassword);
        var script = Path.Combine(dedicatedServerRoot, OperatingSystem.IsWindows() ? "StartServer64.bat" : "start-server.sh");
        if (!File.Exists(script)) throw new FileNotFoundException("Script de démarrage du serveur dédié introuvable.", script);
        var launcher = OperatingSystem.IsWindows() ? PrepareWindowsLauncher(script, dedicatedServerRoot) : script;

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dedicatedServerRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = $"/D /S /C \"\"{launcher}\" -servername \"{serverName}\"\"";
        }
        else
        {
            start.FileName = "/bin/bash";
            start.ArgumentList.Add(launcher);
            start.ArgumentList.Add("-servername");
            start.ArgumentList.Add(serverName);
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException("Le processus serveur n'a pas pu démarrer.");
        var adminPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passwordSubmissionError = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingAdminPassword = initialAdminPassword;
        var adminPasswordStage = 0;
        var adminPasswordLock = new object();
        DataReceivedEventHandler outputHandler = (_, eventArgs) =>
        {
            var line = eventArgs.Data;
            var isInitialPrompt = line?.Contains("Enter new administrator password", StringComparison.OrdinalIgnoreCase) == true;
            var isConfirmationPrompt = line?.Contains("Confirm the password", StringComparison.OrdinalIgnoreCase) == true;
            if (!isInitialPrompt && !isConfirmationPrompt) return;
            adminPrompt.TrySetResult();
            lock (adminPasswordLock)
            {
                if (pendingAdminPassword is null)
                {
                    if (adminPasswordStage < 2)
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                        {
                            passwordSubmissionError.TrySetResult(exception);
                        }
                    }
                    return;
                }

                if ((isInitialPrompt && adminPasswordStage != 0) || (isConfirmationPrompt && adminPasswordStage != 1)) return;
                try
                {
                    process.StandardInput.WriteLine(pendingAdminPassword);
                    process.StandardInput.Flush();
                    adminPasswordStage++;
                    if (adminPasswordStage == 2)
                    {
                        pendingAdminPassword = null;
                        passwordSubmissionError.TrySetResult(null);
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    pendingAdminPassword = null;
                    passwordSubmissionError.TrySetResult(exception);
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (Exception) { }
                }
            }
        };
        process.OutputDataReceived += outputHandler;
        process.ErrorDataReceived += outputHandler;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _managedServerProcesses[serverName] = process;
        var startedAtUtc = DateTime.UtcNow;
        if (process.WaitForExit(startupProbeDuration))
        {
            var exitCode = process.ExitCode;
            _managedServerProcesses.TryRemove(new KeyValuePair<string, Process>(serverName, process));
            process.Dispose();
            TryDeleteLauncher(launcher, script);
            if (adminPrompt.Task.IsCompleted && initialAdminPassword is null)
                throw new InvalidOperationException("Le serveur doit créer son compte « admin ». Saisissez un mot de passe administrateur initial dans la carte de démarrage, puis relancez-le.");
            if (passwordSubmissionError.Task.IsCompletedSuccessfully && passwordSubmissionError.Task.Result is { } submissionError)
                throw new InvalidOperationException($"Le mot de passe administrateur initial n'a pas pu être transmis au serveur : {submissionError.Message}", submissionError);
            throw new InvalidOperationException($"Le serveur dédié s'est arrêté pendant son initialisation (code {exitCode}). {ReadRecentStartupFailure(startedAtUtc)}".Trim());
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            ReleaseManagedProcess(serverName, process);
            TryDeleteLauncher(launcher, script);
        };
        if (process.HasExited)
        {
            ReleaseManagedProcess(serverName, process);
            TryDeleteLauncher(launcher, script);
        }
    }

    private static string? ValidateInitialAdminPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        if (password.Length > 128 || password.Any(char.IsControl))
            throw new ArgumentException("Le mot de passe administrateur initial doit contenir entre 1 et 128 caractères sans caractère de contrôle.", nameof(password));
        return password;
    }

    private static string PrepareWindowsLauncher(string sourceScript, string dedicatedServerRoot)
    {
        var source = File.ReadAllText(sourceScript);
        var installationPrefix = Path.GetFullPath(dedicatedServerRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var lines = source.Replace("%~dp0", installationPrefix, StringComparison.OrdinalIgnoreCase)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line =>
            {
                var command = line.Trim().TrimStart('@').TrimStart();
                return !command.Equals("pause", StringComparison.OrdinalIgnoreCase)
                    && !command.StartsWith("pause ", StringComparison.OrdinalIgnoreCase);
            });
        var launcherRoot = Path.Combine(Path.GetTempPath(), "PZAdvancedServerManager", "launchers");
        Directory.CreateDirectory(launcherRoot);
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourceScript))))[..16].ToLowerInvariant();
        var target = Path.Combine(launcherRoot, $"pzasm-{sourceHash}-{Guid.NewGuid():N}.bat");
        File.WriteAllText(target, string.Join("\r\n", lines), new UTF8Encoding(false));
        return target;
    }

    private static void TryDeleteLauncher(string launcher, string sourceScript)
    {
        if (launcher.Equals(sourceScript, StringComparison.OrdinalIgnoreCase)) return;
        try { if (File.Exists(launcher)) File.Delete(launcher); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public bool IsManagedProcessRunning(string serverName)
    {
        if (!_managedServerProcesses.TryGetValue(serverName, out var process)) return false;
        try
        {
            if (!process.HasExited) return true;
        }
        catch (InvalidOperationException) { }
        ReleaseManagedProcess(serverName, process);
        return false;
    }

    private void ReleaseManagedProcess(string serverName, Process process)
    {
        if (!_managedServerProcesses.TryRemove(new KeyValuePair<string, Process>(serverName, process))) return;
        process.Dispose();
    }

    private static string ReadRecentStartupFailure(DateTime startedAtUtc)
    {
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Zomboid", "server-console.txt");
        if (!File.Exists(logPath) || File.GetLastWriteTimeUtc(logPath) < startedAtUtc.AddSeconds(-2))
            return "Vérifiez l'installation dédiée et son journal server-console.txt.";
        try
        {
            var recentLines = File.ReadLines(logPath).TakeLast(160).ToArray();
            if (recentLines.Any(line => line.Contains("Enter new administrator password", StringComparison.OrdinalIgnoreCase)))
                return "Project Zomboid doit créer son compte « admin ». Saisissez le mot de passe administrateur initial dans la carte de démarrage, puis réessayez.";
            var clues = recentLines
                .Where(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("onItemNotDownloaded", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Server exited", StringComparison.OrdinalIgnoreCase))
                .TakeLast(6)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();
            return clues.Length == 0
                ? $"Consultez {logPath}."
                : $"Journal : {string.Join(" | ", clues)}";
        }
        catch
        {
            return $"Consultez {logPath}.";
        }
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
        PzRconClient? client = null;
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            client = new PzRconClient(tcp);
            var id = client.NextId();
            await client.SendAsync(id, 3, password, cancellationToken);
            for (var i = 0; i < 3; i++)
            {
                var response = await client.ReceiveAsync(cancellationToken);
                if (response.Id == -1) throw new UnauthorizedAccessException("Authentification RCON refusée.");
                if (response.Type == 2 && response.Id == id) return client;
            }
            throw new IOException("Réponse d'authentification RCON invalide.");
        }
        catch
        {
            if (client is not null) await client.DisposeAsync();
            else tcp.Dispose();
            throw;
        }
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
