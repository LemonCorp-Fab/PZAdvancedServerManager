using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages.Projects;

public class EditModel(
    PackageProjectStore store,
    PzEnvironmentService environment,
    PackageValidator validator,
    PackageProjectService projects,
    PackageLifecycleService lifecycle,
    WorkshopImportService workshopImport,
    ServerProfileService servers,
    MapPriorityService mapPriority,
    SteamCmdInstaller steamCmdInstaller,
    SteamCmdService steamCmd) : PageModel
{
    public PackageProject Project { get; private set; } = new();
    public IReadOnlyList<DiscoveredMod> InstalledMods { get; private set; } = [];
    public PackageValidationResult Validation { get; private set; } = new();
    public string WorkshopDescription { get; private set; } = string.Empty;
    public IReadOnlyList<string> ServerConfigNames { get; private set; } = [];
    public MapOrderAnalysis MapAnalysis { get; private set; } = new([], []);
    public SteamCmdStatus SteamCmdStatus { get; private set; } = new(false, string.Empty, string.Empty, null, 0);

    [BindProperty] public ProjectForm Form { get; set; } = new();

    public IActionResult OnGet(Guid id, bool refresh = false)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        Load(project, refresh);
        Form = ProjectForm.From(project);
        Form.MapOrder = string.Join(';', MapAnalysis.Entries.Select(x => x.FolderName));
        if (string.IsNullOrWhiteSpace(Form.SteamCmdPath) && SteamCmdStatus.Installed) Form.SteamCmdPath = SteamCmdStatus.ExecutablePath;
        return Page();
    }

    public IActionResult OnPostSave(Guid id)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        ApplyForm(project);
        store.Save(project);
        TempData["Message"] = "Projet enregistré. Son identifiant stable et son Workshop ID sont conservés.";
        return RedirectToPage(new { id });
    }

    public IActionResult OnPostAddMod(Guid id, string selectionKey)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var discovered = environment.GetMods(project.TargetPzVersion);
        var selected = discovered.FirstOrDefault(x => SelectionKey(x) == selectionKey);
        if (selected is null)
        {
            TempData["Error"] = "La source choisie n'existe plus. Actualisez la détection.";
            return RedirectToPage(new { id, tab = "mods" });
        }
        try
        {
            var added = projects.AddWithDependencies(project, selected, discovered);
            if (added > 0)
                TempData["Message"] = added == 1
                    ? $"« {selected.Name} » ajouté et figé. Renseignez maintenant son autorisation."
                    : $"« {selected.Name} » et {added - 1} dépendance(s) ont été ajoutés et figés. Renseignez leurs autorisations.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { id, tab = "mods" });
    }

    public IActionResult OnPostUpdateMod(Guid id, Guid modReferenceId, PermissionStatus permissionStatus, bool includeInGlobalUpdates, string? rightsHolder, string? publicEvidenceUrl, string? privateAttachmentPath, string? permissionNotes)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var mod = project.Mods.FirstOrDefault(x => x.Id == modReferenceId);
        if (mod is null) return NotFound();
        mod.Permission.Status = permissionStatus;
        mod.Permission.RightsHolder = rightsHolder?.Trim() ?? string.Empty;
        mod.Permission.PublicEvidenceUrl = publicEvidenceUrl?.Trim() ?? string.Empty;
        mod.Permission.PrivateAttachmentPath = privateAttachmentPath?.Trim() ?? string.Empty;
        mod.Permission.Notes = permissionNotes?.Trim() ?? string.Empty;
        mod.IncludeInGlobalUpdates = includeInGlobalUpdates;
        store.Save(project);
        TempData["Message"] = $"Droits et crédits enregistrés pour « {mod.Name} ».";
        return RedirectToPage(new { id, tab = "mods" });
    }

    public IActionResult OnPostRemoveMod(Guid id, Guid modReferenceId)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        projects.Remove(project, modReferenceId);
        TempData["Message"] = "Mod et snapshot PZASM retirés du projet. La source d'origine n'a pas été modifiée.";
        return RedirectToPage(new { id, tab = "mods" });
    }

    public IActionResult OnPostMove(Guid id, Guid modReferenceId, int direction)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        projects.Move(project, modReferenceId, direction);
        return RedirectToPage(new { id, tab = "mods" });
    }

    public IActionResult OnPostBuild(Guid id)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            var result = lifecycle.Build(project);
            TempData["Message"] = $"Pack construit : {result.CopiedFiles:N0} fichiers, {FormatBytes(result.CopiedBytes)}. Dossier : {result.BuildRoot}";
        }
        catch (PackageBuildException exception)
        {
            TempData["Error"] = string.Join(" ", exception.Validation.Issues.Where(x => x.IsError).Take(5).Select(x => x.Message));
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPublishAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var validation = validator.Validate(project);
        if (!validation.CanPublish)
        {
            var blockers = validation.Issues.Where(x => x.IsError && x.Scope != ValidationScope.AutomationOnly).Take(5).Select(x => x.Message);
            TempData["Error"] = "Publication non lancée : " + string.Join(" ", blockers);
            return RedirectToPage(new { id, tab = "distribution" });
        }
        try
        {
            var createsWorkshopItem = project.PublishedWorkshopId == 0;
            var result = await lifecycle.PublishAsync(project, refreshSources: false, requireCoordinatedServer: false, cancellationToken);
            project.Automation.LastResult = Limit(result.Output, 4000);
            project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
            store.Save(project);
            TempData["Message"] = (createsWorkshopItem ? "Nouvel item Workshop créé" : "Item Workshop mis à jour") + $". Workshop ID : {project.PublishedWorkshopId}." +
                (result.ServerWasRunning ? " Le serveur coordonné a été sauvegardé, arrêté puis redémarré." : string.Empty);
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPublishStreamAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var validation = validator.Validate(project);
        if (!validation.CanPublish)
        {
            await PrepareProgressResponseAsync();
            var blockers = validation.Issues.Where(x => x.IsError && x.Scope != ValidationScope.AutomationOnly).Take(5).Select(x => x.Message);
            await WriteProgressAsync(new { type = "error", message = "Publication non lancée : " + string.Join(" ", blockers) }, cancellationToken);
            return new EmptyResult();
        }
        var createsWorkshopItem = project.PublishedWorkshopId == 0;
        return await StreamOperationAsync(async progress =>
        {
            var result = await lifecycle.PublishAsync(project, false, false, cancellationToken, progress: progress);
            project.Automation.LastResult = Limit(result.Output, 4000);
            project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
            store.Save(project);
            return (createsWorkshopItem ? "Nouvel item Workshop créé" : "Item Workshop mis à jour") + $" · ID {project.PublishedWorkshopId}";
        }, Url.Page("/Projects/Edit", null, new { id, tab = "distribution" })!, cancellationToken);
    }

    public async Task<IActionResult> OnPostAuthenticateSteamAsync(Guid id, string steamPassword, string? steamGuardCode, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            ApplyForm(project);
            store.Save(project);
            var result = await steamCmd.AuthenticateAsync(project, new SteamCredentials(steamPassword, steamGuardCode ?? string.Empty), cancellationToken);
            if (result.Interaction != SteamCmdInteraction.None) throw SteamCmdInteractionRequiredException.FromResult(result);
            if (!result.Success) throw new InvalidOperationException("Connexion SteamCMD échouée : " + Limit(result.CombinedOutput, 1800));
            project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
            store.Save(project);
            TempData["Message"] = "Session SteamCMD portable vérifiée. Le service peut la réutiliser sans conserver votre mot de passe ni votre code Steam Guard.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { id, tab = "distribution" });
    }

    public async Task<IActionResult> OnPostAuthenticateSteamStreamAsync(Guid id, string steamPassword, string? steamGuardCode, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        ApplyForm(project);
        store.Save(project);
        return await StreamOperationAsync(async progress =>
        {
            var result = await steamCmd.AuthenticateAsync(project, new SteamCredentials(steamPassword, steamGuardCode ?? string.Empty), cancellationToken, progress);
            if (result.Interaction != SteamCmdInteraction.None) throw SteamCmdInteractionRequiredException.FromResult(result);
            if (!result.Success) throw new InvalidOperationException("Connexion SteamCMD échouée : " + Limit(result.CombinedOutput, 1800));
            project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
            store.Save(project);
            return "Session SteamCMD portable vérifiée et prête pour le service";
        }, Url.Page("/Projects/Edit", null, new { id, tab = "distribution" })!, cancellationToken);
    }

    public async Task<IActionResult> OnPostRefreshSourcesStreamAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var targetCount = project.Mods.Count(x => x.Enabled && x.IncludeInGlobalUpdates);
        return await StreamOperationAsync(async progress =>
        {
            progress.Report(new OperationProgress("prepare", $"Préparation de {targetCount} mod(s) sélectionné(s) pour la mise à jour globale."));
            var result = await lifecycle.RefreshSourcesAsync(project, cancellationToken, progress);
            if (!result.Success) throw new InvalidOperationException("Actualisation SteamCMD échouée : " + Limit(result.CombinedOutput, 1200));
            return $"{targetCount} mod(s) mis à jour et snapshots figés";
        }, Url.Page("/Projects/Edit", null, new { id, tab = "mods" })!, cancellationToken);
    }

    public async Task<IActionResult> OnPostRefreshModStreamAsync(Guid id, Guid modReferenceId, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var mod = project.Mods.FirstOrDefault(x => x.Id == modReferenceId);
        if (mod is null) return NotFound();
        return await StreamOperationAsync(async progress =>
        {
            progress.Report(new OperationProgress("prepare", $"Préparation de « {mod.Name} » et vérification de son Workshop ID."));
            var result = await lifecycle.RefreshModAsync(project, modReferenceId, cancellationToken, progress);
            if (!result.Success) throw new InvalidOperationException($"Mise à jour de « {mod.Name} » échouée : " + Limit(result.CombinedOutput, 1200));
            return $"« {mod.Name} » mis à jour et nouveau snapshot figé";
        }, Url.Page("/Projects/Edit", null, new { id, tab = "mods" })!, cancellationToken);
    }

    public async Task<IActionResult> OnPostRefreshSourcesAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            var targetCount = project.Mods.Count(x => x.Enabled && x.IncludeInGlobalUpdates);
            var result = await lifecycle.RefreshSourcesAsync(project, cancellationToken);
            if (!result.Success) TempData["Error"] = "Actualisation SteamCMD échouée : " + Limit(result.CombinedOutput, 1200);
            else TempData["Message"] = $"{targetCount} mod(s) mis à jour et nouveaux snapshots figés. Les mods exclus sont restés inchangés. Aucun publish n'a été effectué.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { id, tab = "mods" });
    }

    public async Task<IActionResult> OnPostRefreshModAsync(Guid id, Guid modReferenceId, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var mod = project.Mods.FirstOrDefault(x => x.Id == modReferenceId);
        if (mod is null) return NotFound();
        try
        {
            var result = await lifecycle.RefreshModAsync(project, modReferenceId, cancellationToken);
            if (!result.Success) TempData["Error"] = $"Mise à jour de « {mod.Name} » échouée : " + Limit(result.CombinedOutput, 1200);
            else TempData["Message"] = $"« {mod.Name} » a été mis à jour individuellement et son nouveau snapshot est figé.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { id, tab = "mods" });
    }

    public async Task<IActionResult> OnPostImportWorkshopAsync(Guid id, ulong workshopId, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            var result = await workshopImport.ImportAsync(project, workshopId, cancellationToken);
            TempData["Message"] = $"Item Workshop {workshopId} téléchargé : {result.AddedMods} nouveau(x) Mod ID ajouté(s) et figé(s).";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { id, tab = "mods" });
    }

    public async Task<IActionResult> OnPostInstallSteamCmdAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            var result = await steamCmdInstaller.InstallAsync(cancellationToken);
            project.Automation.SteamCmdPath = result.ExecutablePath;
            store.Save(project);
            environment.Invalidate();
            TempData[result.Bootstrapped ? "Message" : "Error"] = result.Bootstrapped
                ? $"SteamCMD installé et initialisé dans l'espace portable du gestionnaire : {result.ExecutablePath}"
                : $"SteamCMD a été extrait dans {result.ExecutablePath}, mais son initialisation a échoué : {Limit(result.Output, 800)}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { id });
    }

    public static string SelectionKey(DiscoveredMod mod) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mod.WorkshopId}|{mod.ModId}|{mod.ModRoot}"));

    private void Load(PackageProject project, bool refresh)
    {
        Project = project;
        SteamCmdStatus = steamCmdInstaller.GetStatus();
        if (string.IsNullOrWhiteSpace(project.Automation.SteamCmdPath) && SteamCmdStatus.Installed)
            project.Automation.SteamCmdPath = SteamCmdStatus.ExecutablePath;
        InstalledMods = environment.GetMods(project.TargetPzVersion, refresh);
        Validation = validator.Validate(project);
        WorkshopDescription = WorkshopDescriptionGenerator.Generate(project);
        ServerConfigNames = servers.List().Select(x => x.Name).ToList();
        MapAnalysis = mapPriority.Analyze(project);
    }

    private void ApplyForm(PackageProject project)
    {
        project.Name = Form.Name.Trim();
        project.Description = Form.Description?.Trim() ?? string.Empty;
        project.Mode = Form.Mode;
        project.TargetPzVersion = string.IsNullOrWhiteSpace(Form.TargetPzVersion) ? "42.20.2" : Form.TargetPzVersion.Trim();
        project.InjectConnectionNotice = Form.InjectConnectionNotice;
        project.NoticeTitle = string.IsNullOrWhiteSpace(Form.NoticeTitle) ? "PZ Advanced Server Manager" : Form.NoticeTitle.Trim();
        project.PublishedWorkshopId = Form.PublishedWorkshopId;
        project.Visibility = Form.Visibility;
        project.PreviewImagePath = Form.PreviewImagePath?.Trim();
        project.MapOrder = (Form.MapOrder ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        project.Tags = (Form.Tags ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        project.LegalWarningAccepted = Form.LegalWarningAccepted;
        if (Form.LegalWarningAccepted && project.LegalWarningAcceptedAt is null) project.LegalWarningAcceptedAt = DateTimeOffset.UtcNow;
        project.Automation.SteamCmdPath = Form.SteamCmdPath?.Trim() ?? string.Empty;
        project.Automation.SteamUsername = Form.SteamUsername?.Trim() ?? string.Empty;
        project.Automation.AnonymousWorkshopDownloads = Form.AnonymousWorkshopDownloads;
        project.Automation.Enabled = Form.AutomationEnabled;
        project.Automation.RefreshWorkshopSourcesBeforeBuild = Form.RefreshSources;
        project.Automation.PublishAfterBuild = Form.PublishAfterBuild;
        project.Automation.CoordinatedServerName = Form.CoordinatedServerName?.Trim() ?? string.Empty;
        project.Automation.DailyTimes = (Form.DailyTimes ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<IActionResult> StreamOperationAsync(
        Func<IProgress<OperationProgress>, Task<string>> operation,
        string redirectUrl,
        CancellationToken cancellationToken)
    {
        await PrepareProgressResponseAsync();
        var channel = Channel.CreateUnbounded<OperationProgress>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var progress = new CallbackProgress<OperationProgress>(value => channel.Writer.TryWrite(value));
        var operationTask = operation(progress);
        _ = operationTask.ContinueWith(_ => channel.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
                await WriteProgressAsync(new { type = "progress", phase = update.Phase, message = update.Message, current = update.Current, total = update.Total }, cancellationToken);
            var message = await operationTask;
            await WriteProgressAsync(new { type = "done", message, redirectUrl }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SteamCmdInteractionRequiredException exception)
        {
            var kind = exception.Interaction switch
            {
                SteamCmdInteraction.SteamGuardCode => "steam_guard_code",
                SteamCmdInteraction.SteamGuardMobileApprovalExpired => "steam_guard_mobile_expired",
                _ => "steam_session_required"
            };
            await WriteProgressAsync(new { type = "interaction", kind, message = exception.Message }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await WriteProgressAsync(new { type = "error", message = exception.Message }, CancellationToken.None);
        }
        return new EmptyResult();
    }

    private Task PrepareProgressResponseAsync()
    {
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Append("X-Accel-Buffering", "no");
        return Task.CompletedTask;
    }

    private async Task WriteProgressAsync(object value, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(value) + "\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[^length..];
    private static string FormatBytes(long bytes) => bytes > 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.00} Gio" : $"{bytes / (1024d * 1024):0.00} Mio";

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    public sealed class ProjectForm
    {
        [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public PackageMode Mode { get; set; }
        public string TargetPzVersion { get; set; } = "42.20.2";
        public bool InjectConnectionNotice { get; set; }
        public string NoticeTitle { get; set; } = string.Empty;
        public ulong PublishedWorkshopId { get; set; }
        public WorkshopVisibility Visibility { get; set; }
        public string? PreviewImagePath { get; set; }
        public string? MapOrder { get; set; }
        public string? Tags { get; set; }
        public bool LegalWarningAccepted { get; set; }
        public string? SteamCmdPath { get; set; }
        public string? SteamUsername { get; set; }
        public bool AnonymousWorkshopDownloads { get; set; } = true;
        public bool AutomationEnabled { get; set; }
        public bool RefreshSources { get; set; }
        public bool PublishAfterBuild { get; set; }
        public string? CoordinatedServerName { get; set; }
        public string? DailyTimes { get; set; }

        public static ProjectForm From(PackageProject project) => new()
        {
            Name = project.Name,
            Description = project.Description,
            Mode = project.Mode,
            TargetPzVersion = project.TargetPzVersion,
            InjectConnectionNotice = project.InjectConnectionNotice,
            NoticeTitle = project.NoticeTitle,
            PublishedWorkshopId = project.PublishedWorkshopId,
            Visibility = project.Visibility,
            PreviewImagePath = project.PreviewImagePath,
            MapOrder = string.Join(";", project.MapOrder),
            Tags = string.Join(", ", project.Tags),
            LegalWarningAccepted = project.LegalWarningAccepted,
            SteamCmdPath = project.Automation.SteamCmdPath,
            SteamUsername = project.Automation.SteamUsername,
            AnonymousWorkshopDownloads = project.Automation.AnonymousWorkshopDownloads,
            AutomationEnabled = project.Automation.Enabled,
            RefreshSources = project.Automation.RefreshWorkshopSourcesBeforeBuild,
            PublishAfterBuild = project.Automation.PublishAfterBuild,
            DailyTimes = string.Join(", ", project.Automation.DailyTimes),
            CoordinatedServerName = project.Automation.CoordinatedServerName
        };
    }
}
