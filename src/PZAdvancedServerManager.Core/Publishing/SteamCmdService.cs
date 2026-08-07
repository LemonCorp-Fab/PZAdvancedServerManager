using System.Diagnostics;
using System.Text.RegularExpressions;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Publishing;

public sealed class SteamCmdService(PackageValidator validator)
{
    public async Task<WorkshopDownloadResult> DownloadWorkshopItemAsync(PackageProject project, ulong workshopId, CancellationToken cancellationToken = default)
    {
        if (workshopId == 0) throw new ArgumentOutOfRangeException(nameof(workshopId), "Workshop ID invalide.");
        ValidateExecutable(project.Automation.SteamCmdPath);
        var login = ResolveDownloadLogin(project);
        var result = await RunAsync(project.Automation.SteamCmdPath,
            ["+login", login, "+workshop_download_item", PzasmConstants.ProjectZomboidSteamAppId, workshopId.ToString(), "validate", "+quit"], cancellationToken);
        var steamCmdRoot = Path.GetDirectoryName(project.Automation.SteamCmdPath)!;
        var contentRoot = Path.Combine(steamCmdRoot, "steamapps", "workshop", "content", PzasmConstants.ProjectZomboidSteamAppId, workshopId.ToString());
        return new WorkshopDownloadResult(result, contentRoot);
    }

    public async Task<SteamCmdResult> RefreshSourcesAsync(PackageProject project, CancellationToken cancellationToken = default)
        => await RefreshSourcesAsync(project, project.Mods.Where(x => x.Enabled).ToArray(), cancellationToken);

    public async Task<SteamCmdResult> RefreshSourcesAsync(PackageProject project, IReadOnlyCollection<PackageModReference> references, CancellationToken cancellationToken = default)
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
        var result = await RunAsync(project.Automation.SteamCmdPath, arguments, cancellationToken);
        if (result.ExitCode == 0) RepointSourcesToSteamCmdCache(project, targets);
        return result;
    }

    public async Task<SteamCmdResult> PublishAsync(PackageProject project, PackageBuildResult build, CancellationToken cancellationToken = default)
    {
        var validation = validator.Validate(project);
        if (!validation.CanPublish)
            throw new InvalidOperationException("Publication bloquée : les droits ou la configuration du pack ne sont pas validés.");
        ValidateExecutable(project.Automation.SteamCmdPath);
        if (string.IsNullOrWhiteSpace(project.Automation.SteamUsername))
            throw new InvalidOperationException("Le nom de compte Steam est requis. Le mot de passe n'est jamais conservé par PZASM.");

        var result = await RunAsync(project.Automation.SteamCmdPath,
            ["+login", project.Automation.SteamUsername, "+workshop_build_item", build.SteamCmdVdfPath, "+quit"], cancellationToken);
        if (result.ExitCode == 0)
        {
            var id = ReadPublishedFileId(build.SteamCmdVdfPath);
            if (id != 0) project.PublishedWorkshopId = id;
            project.LastPublishedAt = DateTimeOffset.UtcNow;
        }
        return result;
    }

    public static ulong ReadPublishedFileId(string vdfPath)
    {
        if (!File.Exists(vdfPath)) return 0;
        var match = Regex.Match(File.ReadAllText(vdfPath), "\\\"publishedfileid\\\"\\s+\\\"(\\d+)\\\"", RegexOptions.IgnoreCase);
        return match.Success && ulong.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    private static async Task<SteamCmdResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return new SteamCmdResult(process.ExitCode, await stdoutTask, await stderrTask);
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
