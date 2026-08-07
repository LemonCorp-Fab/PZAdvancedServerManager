using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Publishing;

public sealed class SteamCmdService(PackageValidator validator)
{
    public async Task<WorkshopDownloadResult> DownloadWorkshopItemAsync(PackageProject project, ulong workshopId, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        if (workshopId == 0) throw new ArgumentOutOfRangeException(nameof(workshopId), "Workshop ID invalide.");
        ValidateExecutable(project.Automation.SteamCmdPath);
        var login = ResolveDownloadLogin(project);
        var result = await RunAsync(project.Automation.SteamCmdPath,
            ["+login", login, "+workshop_download_item", PzasmConstants.ProjectZomboidSteamAppId, workshopId.ToString(), "validate", "+quit"], cancellationToken, progress: progress);
        var steamCmdRoot = Path.GetDirectoryName(project.Automation.SteamCmdPath)!;
        var contentRoot = Path.Combine(steamCmdRoot, "steamapps", "workshop", "content", PzasmConstants.ProjectZomboidSteamAppId, workshopId.ToString());
        return new WorkshopDownloadResult(result, contentRoot);
    }

    public async Task<SteamCmdResult> RefreshSourcesAsync(PackageProject project, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
        => await RefreshSourcesAsync(project, project.Mods.Where(x => x.Enabled).ToArray(), cancellationToken, progress);

    public async Task<SteamCmdResult> RefreshSourcesAsync(PackageProject project, IReadOnlyCollection<PackageModReference> references, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        var targets = references.DistinctBy(x => x.Id).ToArray();
        var workshopIds = targets.Where(x => x.WorkshopId != 0).Select(x => x.WorkshopId).Distinct().ToArray();
        if (workshopIds.Length == 0) return new SteamCmdResult(0, "Aucune source Workshop à actualiser.", string.Empty);
        ValidateExecutable(project.Automation.SteamCmdPath);

        var login = ResolveDownloadLogin(project);
        var arguments = new List<string> { "+login", login };
        foreach (var id in workshopIds)
        {
            arguments.Add("+workshop_download_item");
            arguments.Add(PzasmConstants.ProjectZomboidSteamAppId);
            arguments.Add(id.ToString());
            arguments.Add("validate");
        }
        arguments.Add("+quit");
        var result = await RunAsync(project.Automation.SteamCmdPath, arguments, cancellationToken, progress: progress, timeout: TimeSpan.FromHours(1));
        if (result.ExitCode == 0) RepointSourcesToSteamCmdCache(project, targets);
        return result;
    }

    public async Task<SteamCmdResult> PublishAsync(PackageProject project, PackageBuildResult build, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        var validation = validator.Validate(project);
        if (!validation.CanPublish)
            throw new InvalidOperationException("Publication bloquée par une erreur technique dans la configuration ou le contenu du pack.");
        ValidateExecutable(project.Automation.SteamCmdPath);
        if (string.IsNullOrWhiteSpace(project.Automation.SteamUsername))
            throw new InvalidOperationException("Le nom de compte Steam est requis. Le mot de passe n'est jamais conservé par PZASM.");

        var result = await RunAsync(project.Automation.SteamCmdPath,
            ["+login", project.Automation.SteamUsername, "+workshop_build_item", build.SteamCmdVdfPath, "+quit"], cancellationToken, progress: progress, timeout: TimeSpan.FromMinutes(45));
        if (result.ExitCode == 0)
        {
            var id = ReadPublishedFileId(build.SteamCmdVdfPath);
            if (id == 0)
                return new SteamCmdResult(-1, result.StandardOutput, string.Join(Environment.NewLine, result.StandardError, "SteamCMD n’a renvoyé aucun Workshop ID. La publication ne peut pas être confirmée."));
            project.PublishedWorkshopId = id;
            project.LastPublishedAt = DateTimeOffset.UtcNow;
        }
        return result;
    }

    public async Task<SteamCmdResult> AuthenticateAsync(PackageProject project, SteamCredentials credentials, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        ValidateExecutable(project.Automation.SteamCmdPath);
        if (string.IsNullOrWhiteSpace(project.Automation.SteamUsername))
            throw new InvalidOperationException("Enregistrez d'abord le nom du compte Steam éditeur.");
        if (string.IsNullOrEmpty(credentials.Password))
            throw new InvalidOperationException("Le mot de passe Steam est requis pour créer ou renouveler la session portable.");
        progress?.Report(new OperationProgress("authentication", "Ouverture de la session SteamCMD portable."));
        return await RunAsync(project.Automation.SteamCmdPath,
            [], cancellationToken, credentials, progress, TimeSpan.FromMinutes(5), project.Automation.SteamUsername);
    }

    public static ulong ReadPublishedFileId(string vdfPath)
    {
        if (!File.Exists(vdfPath)) return 0;
        var match = Regex.Match(File.ReadAllText(vdfPath), "\\\"publishedfileid\\\"\\s+\\\"(\\d+)\\\"", RegexOptions.IgnoreCase);
        return match.Success && ulong.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    private static async Task<SteamCmdResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        SteamCredentials? credentials = null,
        IProgress<OperationProgress>? progress = null,
        TimeSpan? timeout = null,
        string? authenticationUsername = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        var consoleLogPath = Path.Combine(start.WorkingDirectory, "logs", "console_log.txt");
        var consoleLogOffset = File.Exists(consoleLogPath) ? new FileInfo(consoleLogPath).Length : 0;
        using var process = new Process { StartInfo = start };
        process.Start();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
        var token = timeoutCancellation.Token;
        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var promptGate = new SemaphoreSlim(1, 1);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var promptWindow = string.Empty;
        var exposeRawOutput = string.IsNullOrWhiteSpace(authenticationUsername);
        var passwordSent = false;
        var authenticationCompleted = false;
        var quitSent = false;
        var guardPromptResponseSent = false;
        var interaction = SteamCmdInteraction.None;
        string? interventionError = null;

        async Task StopForInteractionAsync(SteamCmdInteraction requestedInteraction, string message)
        {
            if (interaction != SteamCmdInteraction.None) return;
            interaction = requestedInteraction;
            interventionError = message;
            progress?.Report(new OperationProgress("authentication", message));
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await Task.CompletedTask;
        }

        async Task ObserveAsync(string rawChunk, StringBuilder? destination)
        {
            if (string.IsNullOrEmpty(rawChunk)) return;
            var chunk = RedactSecrets(rawChunk, credentials);
            if (destination is not null && exposeRawOutput)
                lock (destination) destination.Append(chunk);

            await promptGate.WaitAsync(CancellationToken.None);
            try
            {
                promptWindow = (promptWindow + chunk).ToLowerInvariant();
                if (promptWindow.Length > 8192) promptWindow = promptWindow[^8192..];

                var message = Regex.Replace(chunk, "\\s+", " ").Trim();
                if (exposeRawOutput && message.Length > 0 && !SteamCmdPromptClassifier.IsSecretPrompt(message))
                    progress?.Report(new OperationProgress("steamcmd", message.Length <= 500 ? message : message[^500..]));

                if (!passwordSent && SteamCmdPromptClassifier.RequestsPassword(promptWindow))
                {
                    passwordSent = true;
                    if (string.IsNullOrEmpty(credentials?.Password))
                    {
                        await StopForInteractionAsync(
                            SteamCmdInteraction.SessionRequired,
                            "La session SteamCMD portable n’est plus valide. Reconnectez le compte éditeur avant de publier.");
                        return;
                    }

                    await process.StandardInput.WriteLineAsync(credentials.Password.AsMemory(), token);
                    await process.StandardInput.FlushAsync(token);
                    progress?.Report(new OperationProgress("authentication", "Mot de passe transmis à SteamCMD par l’entrée sécurisée du processus."));
                }

                if (SteamCmdPromptClassifier.RequiresSteamGuard(promptWindow) && !authenticationCompleted)
                {
                    var rejectedCode = !string.IsNullOrWhiteSpace(credentials?.GuardCode) && SteamCmdPromptClassifier.RejectsSteamGuardCode(promptWindow);
                    if (string.IsNullOrWhiteSpace(credentials?.GuardCode) || rejectedCode || string.IsNullOrWhiteSpace(authenticationUsername))
                    {
                        await StopForInteractionAsync(
                            string.IsNullOrWhiteSpace(authenticationUsername) ? SteamCmdInteraction.SessionRequired : SteamCmdInteraction.SteamGuardCode,
                            string.IsNullOrWhiteSpace(authenticationUsername)
                                ? "La session SteamCMD doit être renouvelée depuis la section Compte éditeur avant cette publication."
                                : rejectedCode
                                    ? "Steam a refusé ce code Steam Guard. Saisissez le nouveau code affiché par l’application Steam ou reçu par e-mail."
                                    : "Steam Guard protège ce compte. Saisissez le code actuel pour autoriser cette machine, puis la session portable sera réutilisable.");
                        return;
                    }

                    if (!guardPromptResponseSent && SteamCmdPromptClassifier.RequestsSteamGuardCode(promptWindow))
                    {
                        guardPromptResponseSent = true;
                        await process.StandardInput.WriteLineAsync(credentials!.GuardCode.Trim().AsMemory(), token);
                        await process.StandardInput.FlushAsync(token);
                        progress?.Report(new OperationProgress("steamguard", "Code transmis à l’invite Steam Guard interactive de SteamCMD."));
                    }
                }

                if (!authenticationCompleted && !string.IsNullOrWhiteSpace(authenticationUsername) && SteamCmdPromptClassifier.LoginSucceeded(promptWindow))
                {
                    authenticationCompleted = true;
                    progress?.Report(new OperationProgress("session", "Steam a validé le compte et enregistré la session dans l’installation SteamCMD portable."));
                    if (!quitSent)
                    {
                        quitSent = true;
                        await process.StandardInput.WriteLineAsync("quit".AsMemory(), token);
                        await process.StandardInput.FlushAsync(token);
                    }
                }

                if (!authenticationCompleted && !string.IsNullOrWhiteSpace(authenticationUsername) && SteamCmdPromptClassifier.LoginFailed(promptWindow))
                {
                    interventionError = "Steam a refusé les identifiants du compte. Vérifiez le nom de compte et le mot de passe, puis recommencez.";
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
            }
            finally
            {
                promptGate.Release();
            }
        }

        async Task PumpAsync(StreamReader reader, StringBuilder destination)
        {
            var buffer = new char[64];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token);
                if (count == 0) break;
                await ObserveAsync(new string(buffer, 0, count), destination);
            }
        }

        if (!string.IsNullOrWhiteSpace(authenticationUsername))
        {
            ValidateAccountName(authenticationUsername);
            if (!string.IsNullOrWhiteSpace(credentials?.GuardCode))
            {
                await process.StandardInput.WriteLineAsync($"set_steam_guard_code {credentials.GuardCode.Trim()}".AsMemory(), token);
                progress?.Report(new OperationProgress("steamguard", "Code Steam Guard appliqué à SteamCMD pour cette tentative, sans l’ajouter à la ligne de commande."));
            }
            await process.StandardInput.WriteLineAsync($"login {authenticationUsername}".AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            progress?.Report(new OperationProgress("authentication", "Demande de connexion envoyée à SteamCMD; attente de sa réponse sécurisée."));
        }

        var stdoutTask = PumpAsync(process.StandardOutput, standardOutput);
        var stderrTask = PumpAsync(process.StandardError, standardError);
        var consoleLogTask = TailConsoleLogAsync(consoleLogPath, consoleLogOffset, chunk => ObserveAsync(chunk, standardOutput), process, pumpCancellation.Token);
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            pumpCancellation.Cancel();
            try { await Task.WhenAll(stdoutTask, stderrTask, consoleLogTask); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
            throw;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            interventionError = $"SteamCMD a dépassé le délai maximal de {(timeout ?? TimeSpan.FromMinutes(30)).TotalMinutes:N0} minutes et a été arrêté.";
        }
        pumpCancellation.Cancel();
        try { await Task.WhenAll(stdoutTask, stderrTask, consoleLogTask); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is (OperationCanceledException or IOException)) { }
        if (!string.IsNullOrWhiteSpace(authenticationUsername) && !authenticationCompleted && interaction == SteamCmdInteraction.None && string.IsNullOrWhiteSpace(interventionError))
            interventionError = "SteamCMD s’est fermé avant de confirmer la session portable. Vérifiez la connexion réseau et réessayez.";
        var error = standardError.ToString();
        if (!string.IsNullOrWhiteSpace(interventionError)) error = string.Join(Environment.NewLine, error, interventionError);
        return new SteamCmdResult(interventionError is null ? process.ExitCode : -1, standardOutput.ToString(), error, interaction);
    }

    private static async Task TailConsoleLogAsync(
        string path,
        long initialOffset,
        Func<string, Task> observe,
        Process process,
        CancellationToken cancellationToken)
    {
        var offset = initialOffset;
        var buffer = new byte[512];
        while (!cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            if (File.Exists(path))
            {
                try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.Asynchronous);
                    if (stream.Length < offset) offset = 0;
                    stream.Position = Math.Min(offset, stream.Length);
                    while (stream.Position < stream.Length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                        if (count == 0) break;
                        offset += count;
                        await observe(Encoding.UTF8.GetString(buffer, 0, count));
                    }
                }
                catch (IOException) { }
            }
            await Task.Delay(100, cancellationToken);
        }
    }

    private static string RedactSecrets(string value, SteamCredentials? credentials)
    {
        if (!string.IsNullOrEmpty(credentials?.Password))
            value = value.Replace(credentials.Password, "********", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(credentials?.GuardCode))
            value = value.Replace(credentials.GuardCode.Trim(), "*****", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static void ValidateAccountName(string username)
    {
        if (username.Any(char.IsWhiteSpace) || username.IndexOfAny(['\"', '\'', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Le nom de compte Steam contient des caractères incompatibles avec la connexion SteamCMD interactive.");
    }

    private static void RepointSourcesToSteamCmdCache(PackageProject project, IReadOnlyCollection<PackageModReference> references)
    {
        var steamCmdRoot = Path.GetDirectoryName(project.Automation.SteamCmdPath)!;
        var contentRoot = Path.Combine(steamCmdRoot, "steamapps", "workshop", "content", PzasmConstants.ProjectZomboidSteamAppId);
        foreach (var reference in references.Where(x => x.WorkshopId != 0))
        {
            var modsRoot = Path.Combine(contentRoot, reference.WorkshopId.ToString(), "mods");
            if (!Directory.Exists(modsRoot)) continue;
            foreach (var candidate in Directory.EnumerateDirectories(modsRoot))
            {
                var manifest = PzVersionSelector.SelectManifest(candidate, project.TargetPzVersion, out var selected);
                if (string.IsNullOrWhiteSpace(manifest)) continue;
                var info = ModInfoParser.Parse(manifest);
                if (!info.Id.Equals(reference.ModId, StringComparison.OrdinalIgnoreCase)) continue;
                var previousAuthor = reference.Author;
                reference.SourceModRoot = candidate;
                reference.Name = string.IsNullOrWhiteSpace(info.Name) ? reference.Name : info.Name;
                reference.Author = string.IsNullOrWhiteSpace(info.Author) ? reference.Author : info.Author;
                reference.Version = info.Version;
                reference.SelectedVersionFolder = selected;
                reference.RequiredModIds = info.Required;
                if (!string.IsNullOrWhiteSpace(reference.Author) &&
                    (string.IsNullOrWhiteSpace(reference.Permission.RightsHolder) ||
                     reference.Permission.Status == PermissionStatus.Unknown && reference.Permission.RightsHolder.Equals(previousAuthor, StringComparison.OrdinalIgnoreCase)))
                    reference.Permission.RightsHolder = reference.Author;
                break;
            }
        }
    }

    private static void ValidateExecutable(string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || (!fileName.Equals("steamcmd.exe", StringComparison.OrdinalIgnoreCase) && !fileName.Equals("steamcmd.sh", StringComparison.OrdinalIgnoreCase)))
            throw new FileNotFoundException("SteamCMD est introuvable. Indiquez le chemin exact vers steamcmd.exe ou steamcmd.sh.", path);
    }

    private static string ResolveDownloadLogin(PackageProject project) =>
        project.Automation.AnonymousWorkshopDownloads || string.IsNullOrWhiteSpace(project.Automation.SteamUsername)
            ? "anonymous"
            : project.Automation.SteamUsername;
}

public enum SteamCmdInteraction
{
    None,
    SteamGuardCode,
    SessionRequired
}

public static class SteamCmdPromptClassifier
{
    public static bool RequestsPassword(string value) => value.Contains("password:", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresSteamGuard(string value) =>
        value.Contains("two-factor code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("two factor code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("account login denied", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid login auth code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("steam guard", StringComparison.OrdinalIgnoreCase) &&
        (value.Contains("code", StringComparison.OrdinalIgnoreCase) || value.Contains("protected", StringComparison.OrdinalIgnoreCase));

    public static bool RequestsSteamGuardCode(string value) =>
        value.Contains("two-factor code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("two factor code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("steam guard", StringComparison.OrdinalIgnoreCase) && value.Contains("code", StringComparison.OrdinalIgnoreCase);

    public static bool RejectsSteamGuardCode(string value) =>
        value.Contains("account login denied", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid login auth code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid two-factor", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid steam guard", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("incorrect steam guard", StringComparison.OrdinalIgnoreCase);

    public static bool LoginSucceeded(string value) =>
        value.Contains("waiting for user info", StringComparison.OrdinalIgnoreCase) && value.Contains("ok", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("logged in ok", StringComparison.OrdinalIgnoreCase);

    public static bool LoginFailed(string value) =>
        !RequiresSteamGuard(value) &&
        (value.Contains("invalid password", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("invalid credentials", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("login failure", StringComparison.OrdinalIgnoreCase));

    public static bool IsSecretPrompt(string value) => RequestsPassword(value) || value.Trim().Equals("password", StringComparison.OrdinalIgnoreCase);
}

public sealed record SteamCmdResult(int ExitCode, string StandardOutput, string StandardError, SteamCmdInteraction Interaction = SteamCmdInteraction.None)
{
    public bool Success => ExitCode == 0;
    public string CombinedOutput => string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed class SteamCmdInteractionRequiredException(SteamCmdInteraction interaction, string message) : Exception(message)
{
    public SteamCmdInteraction Interaction { get; } = interaction;

    public static SteamCmdInteractionRequiredException FromResult(SteamCmdResult result)
    {
        var fallback = result.Interaction == SteamCmdInteraction.SteamGuardCode
            ? "Steam Guard demande un nouveau code pour autoriser cette machine."
            : "La session SteamCMD portable doit être renouvelée avant de continuer.";
        var message = result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? fallback;
        return new SteamCmdInteractionRequiredException(result.Interaction, message);
    }
}

public sealed record WorkshopDownloadResult(SteamCmdResult SteamCmd, string ContentRoot);
public sealed record SteamCredentials(string Password, string GuardCode);
