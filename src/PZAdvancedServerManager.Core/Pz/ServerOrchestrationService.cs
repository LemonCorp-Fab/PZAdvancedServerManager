using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class ServerOrchestrationService(ApplicationPaths? paths = null)
{
    private readonly string _launcherRoot = paths is null
        ? Path.Combine(Path.GetTempPath(), "PZAdvancedServerManager", "launchers")
        : Path.Combine(paths.RuntimeHomeRoot, "launchers");
    private readonly ConcurrentDictionary<string, Process> _managedServerProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RuntimeLogBuffer> _runtimeLogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RuntimeLogFileCache> _serverConsoleCache = new(StringComparer.OrdinalIgnoreCase);

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

    public async Task<ServerRuntimeOverview> ReadRconOverviewAsync(
        string host,
        int port,
        string password,
        int? maxPlayers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            return ServerRuntimeOverview.Empty with { MaxPlayers = maxPlayers, CapturedAt = DateTimeOffset.UtcNow };

        var stopwatch = Stopwatch.StartNew();
        await using var rcon = await PzRconClient.ConnectAsync(host, port, password, cancellationToken);
        var response = await rcon.CommandAsync("players", cancellationToken);
        stopwatch.Stop();
        var parsed = ParsePlayersResponse(response);
        return new ServerRuntimeOverview(
            parsed.Count,
            maxPlayers,
            parsed.Players,
            stopwatch.Elapsed.TotalMilliseconds,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            "rcon");
    }

    public static (int Count, IReadOnlyList<ServerPlayerSnapshot> Players) ParsePlayersResponse(string response)
    {
        response ??= string.Empty;
        var lines = response.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var players = new List<ServerPlayerSnapshot>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.Contains("Players connected", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("-", StringComparison.Ordinal)) line = line[1..].Trim();
            else if (line.StartsWith("*", StringComparison.Ordinal)) line = line[1..].Trim();
            else continue;
            if (line.Length == 0) continue;

            string? steamId = null;
            int? ping = null;
            var steamMatch = Regex.Match(line, @"(?<!\d)(7656119\d{10})(?!\d)", RegexOptions.CultureInvariant);
            if (steamMatch.Success) steamId = steamMatch.Value;
            var pingMatch = Regex.Match(line, @"(?i)\bping\s*[:=]?\s*(\d{1,5})\s*ms\b", RegexOptions.CultureInvariant);
            if (pingMatch.Success && int.TryParse(pingMatch.Groups[1].Value, out var parsedPing)) ping = parsedPing;

            var name = Regex.Replace(line, @"(?i)\s*[\[(].*?(?:ping\s*[:=]?\s*\d+\s*ms|7656119\d{10}).*?[\])]\s*$", string.Empty).Trim();
            if (name.Length == 0) name = line;
            players.Add(new ServerPlayerSnapshot(name, steamId, ping));
        }

        var countMatch = Regex.Match(response, @"(?i)players\s+connected\s*(?:\(|:)?\s*(\d+)", RegexOptions.CultureInvariant);
        var count = countMatch.Success && int.TryParse(countMatch.Groups[1].Value, out var parsedCount)
            ? parsedCount
            : players.Count;
        return (count, players);
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
        if (IsLocalServerProcessRunning(serverName))
            throw new InvalidOperationException($"Un processus Project Zomboid utilise déjà le profil « {serverName} ».");
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
        var runtimeLog = new RuntimeLogBuffer();
        runtimeLog.Add("SYSTEM", $"Lanceur Project Zomboid démarré (PID {process.Id}) pour le profil {serverName}.");
        _runtimeLogs[serverName] = runtimeLog;
        var adminPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passwordSubmissionError = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingAdminPassword = initialAdminPassword;
        var adminPasswordStage = 0;
        var adminPasswordLock = new object();
        void HandleOutput(string? line, string stream)
        {
            if (line is null) return;
            runtimeLog.Add(stream, line);
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
        }
        DataReceivedEventHandler outputHandler = (_, eventArgs) => HandleOutput(eventArgs.Data, "OUT");
        DataReceivedEventHandler errorHandler = (_, eventArgs) => HandleOutput(eventArgs.Data, "ERR");
        process.OutputDataReceived += outputHandler;
        process.ErrorDataReceived += errorHandler;
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
            runtimeLog.Add("SYSTEM", $"Le processus de lancement s'est terminé avec le code {SafeExitCode(process)}.");
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

    private string PrepareWindowsLauncher(string sourceScript, string dedicatedServerRoot)
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
        Directory.CreateDirectory(_launcherRoot);
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourceScript))))[..16].ToLowerInvariant();
        var target = Path.Combine(_launcherRoot, $"pzasm-{sourceHash}-{Guid.NewGuid():N}.bat");
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

    public bool IsLocalServerProcessRunning(string serverName)
        => IsManagedProcessRunning(serverName)
            || FindLocalServerProcesses(serverName).Any(process => process.Origin == ServerRuntimeOrigin.LocalDedicated);

    public bool HasLocalDedicatedProcess(string serverName)
        => FindLocalServerProcesses(serverName).Any(process => process.Origin == ServerRuntimeOrigin.LocalDedicated);

    public async Task<ForcedServerStopResult> ForceStopLocalDedicatedAsync(
        string serverName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverName) || serverName.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Nom de profil serveur invalide.", nameof(serverName));

        var candidates = FindLocalServerProcesses(serverName)
            .Where(process => process.Origin == ServerRuntimeOrigin.LocalDedicated)
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException($"Aucun processus serveur dédié actif ne correspond au profil « {serverName} ».");

        var terminated = new List<int>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                process.Kill(entireProcessTree: true);
                terminated.Add(candidate.ProcessId);
            }
            catch (ArgumentException)
            {
                terminated.Add(candidate.ProcessId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                throw new InvalidOperationException($"Le processus dédié PID {candidate.ProcessId} n'a pas pu être terminé : {exception.Message}", exception);
            }
        }

        if (_managedServerProcesses.TryGetValue(serverName, out var launcher))
        {
            try
            {
                if (!launcher.HasExited) launcher.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasLocalDedicatedProcess(serverName))
                return new ForcedServerStopResult(serverName, terminated, DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        var remaining = FindLocalServerProcesses(serverName)
            .Where(process => process.Origin == ServerRuntimeOrigin.LocalDedicated)
            .Select(process => process.ProcessId)
            .ToArray();
        throw new TimeoutException($"Les processus dédiés PID {string.Join(", ", remaining)} sont toujours actifs après la demande d'arrêt forcé.");
    }

    public async Task<ServerRuntimeSnapshot> InspectLocalRuntimeAsync(
        string serverName,
        string iniPath,
        string serverConsolePath,
        CancellationToken cancellationToken = default)
        => await InspectLocalRuntimeAsync(serverName, iniPath, serverConsolePath, null, cancellationToken);

    public async Task<ServerRuntimeSnapshot> InspectLocalRuntimeAsync(
        string serverName,
        string iniPath,
        string serverConsolePath,
        string? coopConsolePath,
        CancellationToken cancellationToken = default)
    {
        var managedRunning = IsManagedProcessRunning(serverName);
        var discoveredInstances = FindLocalServerProcesses(serverName);
        var dedicatedInstances = discoveredInstances
            .Where(instance => instance.Origin != ServerRuntimeOrigin.LocalHostedSession)
            .ToArray();
        var hostedCandidates = discoveredInstances
            .Where(instance => instance.Origin == ServerRuntimeOrigin.LocalHostedSession)
            .OrderByDescending(instance => instance.StartedAt)
            .ToArray();
        var hostedCandidate = hostedCandidates.FirstOrDefault();
        var hostedLog = hostedCandidate is not null && !string.IsNullOrWhiteSpace(coopConsolePath)
            ? ReadServerConsoleTail(coopConsolePath, hostedCandidate.StartedAt, 240)
            : RuntimeLogReadResult.Empty;
        var hostedActive = hostedCandidate is not null
            && IsHostedSessionActive(
                hostedCandidate.StartedAt,
                hostedLog.LastOutputAt,
                hostedLog.GameReady,
                hostedLog.StartupFailed,
                DateTimeOffset.UtcNow);
        var activeInstances = hostedActive
            ? dedicatedInstances.Append(hostedCandidate!).ToArray()
            : dedicatedInstances;
        var inactiveHostedHelperCount = hostedCandidates.Length - (hostedActive ? 1 : 0);
        var orderedInstances = activeInstances
            .OrderBy(instance => instance.Origin == ServerRuntimeOrigin.LocalDedicated ? 0 : 1)
            .ThenByDescending(instance => instance.StartedAt)
            .ToArray();
        var primary = orderedInstances.FirstOrDefault();
        var processRunning = managedRunning || primary is not null;
        var processId = primary?.ProcessId;
        var startedAt = primary?.StartedAt;
        if (managedRunning && _managedServerProcesses.TryGetValue(serverName, out var launcher))
        {
            processId ??= launcher.Id;
            startedAt ??= SafeStartTime(launcher);
        }

        IReadOnlyList<ServerRuntimeLogLine> output;
        DateTimeOffset? lastOutputAt;
        bool logConfirmsGameReady;
        bool logConfirmsRconBindFailure;
        if (_runtimeLogs.TryGetValue(serverName, out var managedLog) && managedLog.Count > 0)
        {
            output = managedLog.List(240);
            lastOutputAt = output.LastOrDefault()?.Timestamp;
            logConfirmsGameReady = managedLog.GameReady;
            logConfirmsRconBindFailure = managedLog.RconBindFailed;
        }
        else
        {
            var logRead = primary?.Origin == ServerRuntimeOrigin.LocalHostedSession
                ? hostedLog
                : ReadServerConsoleTail(serverConsolePath, startedAt, 240);
            output = logRead.Lines;
            lastOutputAt = logRead.LastOutputAt;
            logConfirmsGameReady = logRead.GameReady;
            logConfirmsRconBindFailure = logRead.RconBindFailed;
        }

        var rconAuthenticated = false;
        try
        {
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            rconAuthenticated = await IsOnlineAsync(iniPath, probeTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        var gameReady = rconAuthenticated || processRunning && logConfirmsGameReady;
        var rconBindFailed = processRunning && logConfirmsRconBindFailure;
        var slow = processRunning && !gameReady && startedAt is { } started
            && DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(3)
            && (lastOutputAt is null || DateTimeOffset.UtcNow - lastOutputAt > TimeSpan.FromSeconds(90));
        var state = orderedInstances.Length > 1
            ? ServerRuntimeState.MultipleInstances
            : rconAuthenticated
                ? ServerRuntimeState.Online
                : processRunning && gameReady
                    ? ServerRuntimeState.OnlineWithoutRcon
                    : slow
                        ? ServerRuntimeState.StartingSlow
                        : processRunning
                            ? ServerRuntimeState.Starting
                            : ServerRuntimeState.Stopped;
        var isHostedLog = primary?.Origin == ServerRuntimeOrigin.LocalHostedSession;
        var logFileName = isHostedLog ? "coop-console.txt" : "server-console.txt";
        var logIsLive = processRunning || rconAuthenticated;
        var logSource = logIsLive
            ? logFileName
            : $"{logFileName} · dernière session";
        var logStatus = logIsLive
            ? output.Count > 0
                ? $"Journal actif : {output.Count} lignes récentes disponibles."
                : "Le serveur est actif, mais aucune ligne récente n'est encore disponible."
            : output.Count > 0
                ? $"Serveur arrêté : {output.Count} lignes archivées de la dernière session."
                : "Serveur arrêté : aucun journal de session n'est disponible.";

        return new ServerRuntimeSnapshot(
            state,
            processRunning || rconAuthenticated,
            gameReady,
            rconAuthenticated,
            rconBindFailed,
            managedRunning,
            processId,
            startedAt,
            lastOutputAt,
            output)
        {
            Origin = primary?.Origin ?? ServerRuntimeOrigin.Unknown,
            Instances = orderedInstances.Select(instance => (ServerRuntimeInstance)instance).ToArray(),
            InactiveHostedHelperCount = inactiveHostedHelperCount,
            LogSource = logSource,
            LogStatus = logStatus
        };
    }

    public static bool IsHostedSessionActive(
        DateTimeOffset? processStartedAt,
        DateTimeOffset? lastOutputAt,
        bool gameReady,
        bool startupFailed,
        DateTimeOffset now)
    {
        if (startupFailed) return false;
        if (gameReady) return true;
        if (processStartedAt is not { } started || lastOutputAt is not { } lastOutput) return false;
        var age = now - started;
        var outputAge = now - lastOutput;
        return age >= TimeSpan.Zero
            && age <= TimeSpan.FromMinutes(3)
            && lastOutput >= started.AddSeconds(-5)
            && outputAge >= TimeSpan.Zero
            && outputAge <= TimeSpan.FromSeconds(90);
    }

    public static string? ParseServerNameFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)
            || !commandLine.Contains("zombie.network.GameServer", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(
            commandLine,
            @"(?:^|\s)-servername(?:\s+|=)(?:""(?<quoted>[^""]+)""|'(?<single>[^']+)'|(?<bare>[^\s""']+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var value = match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["single"].Success
                ? match.Groups["single"].Value
                : match.Groups["bare"].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<LocalServerProcess> FindLocalServerProcesses(string serverName)
        => EnumerateLocalServerProcesses()
            .Where(process => process.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(process => process.StartedAt)
            .ToArray();

    private static IReadOnlyList<LocalServerProcess> EnumerateLocalServerProcesses()
    {
        if (OperatingSystem.IsWindows()) return EnumerateWindowsServerProcesses();
        if (OperatingSystem.IsLinux()) return EnumerateLinuxServerProcesses();
        return [];
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<LocalServerProcess> EnumerateWindowsServerProcesses()
    {
        var processes = new List<LocalServerProcess>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='java.exe' OR Name='javaw.exe'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                var commandLine = item["CommandLine"] as string;
                var serverName = ParseServerNameFromCommandLine(commandLine);
                if (serverName is null || !ConvertToProcessId(item["ProcessId"], out var processId)) continue;
                var parentProcessId = ConvertToProcessId(item["ParentProcessId"], out var parentId) ? (int?)parentId : null;
                var executablePath = item["ExecutablePath"] as string ?? string.Empty;
                processes.Add(new LocalServerProcess(
                    processId,
                    parentProcessId,
                    serverName,
                    ParseRuntimeOriginFromCommandLine(commandLine),
                    SafeStartTime(processId),
                    executablePath));
            }
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        return processes;
    }

    private static IReadOnlyList<LocalServerProcess> EnumerateLinuxServerProcesses()
    {
        var processes = new List<LocalServerProcess>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var processId)) continue;
                var commandLinePath = Path.Combine(directory, "cmdline");
                string commandLine;
                try { commandLine = File.ReadAllText(commandLinePath).Replace('\0', ' '); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                var serverName = ParseServerNameFromCommandLine(commandLine);
                if (serverName is null) continue;
                var executablePath = string.Empty;
                try { executablePath = File.ResolveLinkTarget(Path.Combine(directory, "exe"), returnFinalTarget: true)?.FullName ?? string.Empty; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                processes.Add(new LocalServerProcess(
                    processId,
                    ReadLinuxParentProcessId(directory),
                    serverName,
                    ParseRuntimeOriginFromCommandLine(commandLine),
                    SafeStartTime(processId),
                    executablePath));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return processes;
    }

    public static ServerRuntimeOrigin ParseRuntimeOriginFromCommandLine(string? commandLine)
    {
        if (ParseServerNameFromCommandLine(commandLine) is null) return ServerRuntimeOrigin.Unknown;
        return Regex.IsMatch(commandLine!, @"(?:^|\s)-coop(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? ServerRuntimeOrigin.LocalHostedSession
            : ServerRuntimeOrigin.LocalDedicated;
    }

    private static int? ReadLinuxParentProcessId(string processDirectory)
    {
        try
        {
            var statusLine = File.ReadLines(Path.Combine(processDirectory, "status"))
                .FirstOrDefault(line => line.StartsWith("PPid:", StringComparison.Ordinal));
            return statusLine is not null
                && int.TryParse(statusLine.AsSpan(5).Trim(), out var parentProcessId)
                    ? parentProcessId
                    : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool ConvertToProcessId(object? value, out int processId)
    {
        try
        {
            processId = checked((int)Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture));
            return processId > 0;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            processId = 0;
            return false;
        }
    }

    private static DateTimeOffset? SafeStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return SafeStartTime(process);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static DateTimeOffset? SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private RuntimeLogReadResult ReadServerConsoleTail(
        string path,
        DateTimeOffset? processStartedAt,
        int capacity)
    {
        if (!File.Exists(path)) return RuntimeLogReadResult.Empty;
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(path);
            if (processStartedAt is { } started && lastWrite < started.UtcDateTime.AddSeconds(-5))
                return new([], lastWrite, false, false, false);
            var length = new FileInfo(path).Length;
            if (_serverConsoleCache.TryGetValue(path, out var cached)
                && cached.Length == length
                && cached.LastWriteUtc == lastWrite
                && cached.Capacity == capacity)
                return cached.Result;
            long sequence = 0;
            var lines = new Queue<ServerRuntimeLogLine>();
            var lifecycle = ServerLogLifecycle.Unknown;
            var rconBindFailed = false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } rawLine)
            {
                if (rawLine.Contains("*** SERVER STARTED ****", StringComparison.OrdinalIgnoreCase))
                    lifecycle = ServerLogLifecycle.Ready;
                else if (IsServerStartupFailure(rawLine) || IsServerStoppedMarker(rawLine))
                    lifecycle = ServerLogLifecycle.StoppedOrFailed;
                rconBindFailed |= IsRconBindFailure(rawLine);
                lines.Enqueue(new ServerRuntimeLogLine(++sequence, null, "LOG", RedactSensitiveOutput(rawLine)));
                while (lines.Count > capacity) lines.Dequeue();
            }
            var result = new RuntimeLogReadResult(
                lines.ToArray(),
                lastWrite,
                lifecycle == ServerLogLifecycle.Ready,
                rconBindFailed,
                lifecycle == ServerLogLifecycle.StoppedOrFailed);
            _serverConsoleCache[path] = new RuntimeLogFileCache(length, lastWrite, capacity, result);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return RuntimeLogReadResult.Empty;
        }
    }

    private static bool IsRconBindFailure(string value)
        => value.Contains("RCON: error creating socket", StringComparison.OrdinalIgnoreCase)
            || value.Contains("RCON port is already in use", StringComparison.OrdinalIgnoreCase);

    private static bool IsServerStartupFailure(string value)
        => value.Contains("Connection Startup Failed", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Failed to start the server", StringComparison.OrdinalIgnoreCase);

    private static bool IsServerStoppedMarker(string value)
        => value.Contains("SERVER SHUTDOWN", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Server exited", StringComparison.OrdinalIgnoreCase);

    private static string RedactSensitiveOutput(string value)
        => Regex.Replace(
            value,
            @"(?i)((?:RCON|Admin|Server)?Password\s*[=:]\s*)(\S+)",
            "$1<redacted>",
            RegexOptions.CultureInvariant);

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
    private sealed record LocalServerProcess(
        int ProcessId,
        int? ParentProcessId,
        string ServerName,
        ServerRuntimeOrigin Origin,
        DateTimeOffset? StartedAt,
        string ExecutablePath)
    {
        public static implicit operator ServerRuntimeInstance(LocalServerProcess process) => new(
            process.ProcessId,
            process.ParentProcessId,
            process.ServerName,
            process.Origin,
            process.StartedAt,
            process.ExecutablePath);
    }
    private sealed record RuntimeLogReadResult(
        IReadOnlyList<ServerRuntimeLogLine> Lines,
        DateTimeOffset? LastOutputAt,
        bool GameReady,
        bool RconBindFailed,
        bool StartupFailed)
    {
        public static RuntimeLogReadResult Empty { get; } = new([], null, false, false, false);
    }
    private sealed record RuntimeLogFileCache(long Length, DateTime LastWriteUtc, int Capacity, RuntimeLogReadResult Result);
    private enum ServerLogLifecycle { Unknown, Ready, StoppedOrFailed }

    private sealed class RuntimeLogBuffer(int capacity = 600)
    {
        private readonly object _gate = new();
        private readonly Queue<ServerRuntimeLogLine> _lines = new();
        private long _sequence;
        private bool _gameReady;
        private bool _rconBindFailed;

        public bool GameReady
        {
            get { lock (_gate) return _gameReady; }
        }

        public bool RconBindFailed
        {
            get { lock (_gate) return _rconBindFailed; }
        }

        public int Count
        {
            get { lock (_gate) return _lines.Count; }
        }

        public void Add(string stream, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_gate)
            {
                _gameReady |= message.Contains("*** SERVER STARTED ****", StringComparison.OrdinalIgnoreCase);
                _rconBindFailed |= IsRconBindFailure(message);
                _lines.Enqueue(new ServerRuntimeLogLine(++_sequence, DateTimeOffset.UtcNow, stream, RedactSensitiveOutput(message)));
                while (_lines.Count > capacity) _lines.Dequeue();
            }
        }

        public IReadOnlyList<ServerRuntimeLogLine> List(int count)
        {
            lock (_gate) return _lines.TakeLast(Math.Max(1, count)).ToArray();
        }
    }
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
