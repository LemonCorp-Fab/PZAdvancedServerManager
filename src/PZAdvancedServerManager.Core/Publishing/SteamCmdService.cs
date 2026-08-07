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

    public async Task<SteamCmdResult> PublishAsync(PackageProject project, PackageBuildResult build, CancellationToken cancellationToken = default, SteamCredentials? credentials = null, IProgress<OperationProgress>? progress = null)
    {
        var validation = validator.Validate(project);
        if (!validation.CanPublish)
            throw new InvalidOperationException("Publication bloquée par une erreur technique dans la configuration ou le contenu du pack.");
        ValidateExecutable(project.Automation.SteamCmdPath);
        if (string.IsNullOrWhiteSpace(project.Automation.SteamUsername))
            throw new InvalidOperationException("Le nom de compte Steam est requis. Le mot de passe n'est jamais conservé par PZASM.");

        var result = await RunAsync(project.Automation.SteamCmdPath,
            ["+login", project.Automation.SteamUsername, "+workshop_build_item", build.SteamCmdVdfPath, "+quit"], cancellationToken, credentials, progress, TimeSpan.FromMinutes(45));
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
            ["+login", project.Automation.SteamUsername, "+quit"], cancellationToken, credentials, progress, TimeSpan.FromMinutes(5));
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
        TimeSpan? timeout = null)
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
        using var process = new Process { StartInfo = start };
        process.Start();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
        var token = timeoutCancellation.Token;
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var promptGate = new object();
        var passwordSent = false;
        var guardCodeSent = false;
        string? interventionError = null;

        async Task PumpAsync(StreamReader reader, StringBuilder destination)
        {
            var buffer = new char[768];
            var promptWindow = string.Empty;
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token);
                if (count == 0) break;
                var chunk = new string(buffer, 0, count);
                lock (destination) destination.Append(chunk);
                promptWindow = (promptWindow + chunk).ToLowerInvariant();
                if (promptWindow.Length > 4096) promptWindow = promptWindow[^4096..];

                var message = Regex.Replace(chunk, "\\s+", " ").Trim();
                if (message.Length > 0)
                    progress?.Report(new OperationProgress("steamcmd", message.Length <= 500 ? message : message[^500..]));

                if (!passwordSent && promptWindow.Contains("password:", StringComparison.Ordinal))
                {
                    lock (promptGate)
                    {
                        if (passwordSent) continue;
                        passwordSent = true;
                        if (string.IsNullOrEmpty(credentials?.Password))
                            interventionError = "SteamCMD demande le mot de passe du compte. Saisissez-le dans la fenêtre de publication; il sera utilisé uniquement pour cette opération et ne sera pas enregistré.";
                    }
                    if (interventionError is not null)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        continue;
                    }
                    await process.StandardInput.WriteLineAsync(credentials!.Password.AsMemory(), token);
                    await process.StandardInput.FlushAsync(token);
                    progress?.Report(new OperationProgress("authentication", "Mot de passe transmis directement à SteamCMD pour cette session."));
                }

                var guardPrompt = promptWindow.Contains("steam guard", StringComparison.Ordinal) && promptWindow.Contains("code", StringComparison.Ordinal)
                    || promptWindow.Contains("two-factor code", StringComparison.Ordinal);
                if (!guardCodeSent && guardPrompt)
                {
                    lock (promptGate)
                    {
                        if (guardCodeSent) continue;
                        guardCodeSent = true;
                        if (string.IsNullOrWhiteSpace(credentials?.GuardCode))
                            interventionError = "SteamCMD demande un code Steam Guard. Relancez la publication avec le code actuel; il ne sera pas enregistré.";
                    }
                    if (interventionError is not null)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        continue;
                    }
                    await process.StandardInput.WriteLineAsync(credentials!.GuardCode.AsMemory(), token);
                    await process.StandardInput.FlushAsync(token);
                    progress?.Report(new OperationProgress("authentication", "Code Steam Guard transmis pour cette session."));
                }
            }
        }

        var stdoutTask = PumpAsync(process.StandardOutput, standardOutput);
        var stderrTask = PumpAsync(process.StandardError, standardError);
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdoutTask, stderrTask); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
            throw;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            interventionError = $"SteamCMD a dépassé le délai maximal de {(timeout ?? TimeSpan.FromMinutes(30)).TotalMinutes:N0} minutes et a été arrêté.";
        }
        try { await Task.WhenAll(stdoutTask, stderrTask); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is (OperationCanceledException or IOException)) { }
        var error = standardError.ToString();
        if (!string.IsNullOrWhiteSpace(interventionError)) error = string.Join(Environment.NewLine, error, interventionError);
        return new SteamCmdResult(interventionError is null ? process.ExitCode : -1, standardOutput.ToString(), error);
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

public sealed record SteamCmdResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
    public string CombinedOutput => string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed record WorkshopDownloadResult(SteamCmdResult SteamCmd, string ContentRoot);
public sealed record SteamCredentials(string Password, string GuardCode);
