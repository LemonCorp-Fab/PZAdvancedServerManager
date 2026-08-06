using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PZAdvancedServerManager.App.Services;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;

namespace PZAdvancedServerManager.App.Pages.Projects;

public class EditModel(
    PackageProjectStore store,
    DiscoveryCache discovery,
    PackageValidator validator,
    PackageBuildService builder,
    SteamCmdService steamCmd) : PageModel
{
    public PackageProject Project { get; private set; } = new();
    public IReadOnlyList<DiscoveredMod> InstalledMods { get; private set; } = [];
    public PackageValidationResult Validation { get; private set; } = new();
    public string WorkshopDescription { get; private set; } = string.Empty;

    [BindProperty] public ProjectForm Form { get; set; } = new();

    public IActionResult OnGet(Guid id, bool refresh = false)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        Load(project, refresh);
        Form = ProjectForm.From(project);
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
        var discovered = discovery.GetMods(project.TargetPzVersion);
        var selected = discovered.FirstOrDefault(x => SelectionKey(x) == selectionKey);
        if (selected is null)
        {
            TempData["Error"] = "La source choisie n'existe plus. Actualisez la détection.";
            return RedirectToPage(new { id });
        }
        var added = AddWithDependencies(project, selected, discovered);
        if (added > 0)
        {
            store.Save(project);
            TempData["Message"] = added == 1
                ? $"« {selected.Name} » ajouté. Renseignez maintenant son autorisation."
                : $"« {selected.Name} » et {added - 1} dépendance(s) détectée(s) ont été ajoutés. Renseignez leurs autorisations.";
        }
        return RedirectToPage(new { id });
    }

    public IActionResult OnPostUpdateMod(Guid id, Guid modReferenceId, PermissionStatus permissionStatus, string? rightsHolder, string? publicEvidenceUrl, string? privateAttachmentPath, string? permissionNotes)
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
        store.Save(project);
        TempData["Message"] = $"Droits et crédits enregistrés pour « {mod.Name} ».";
        return RedirectToPage(new { id });
    }

    public IActionResult OnPostRemoveMod(Guid id, Guid modReferenceId)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        project.Mods.RemoveAll(x => x.Id == modReferenceId);
        for (var i = 0; i < project.Mods.Count; i++) project.Mods[i].Order = i;
        store.Save(project);
        TempData["Message"] = "Mod retiré du projet. Les fichiers sources n'ont pas été modifiés.";
        return RedirectToPage(new { id });
    }

    public IActionResult OnPostMove(Guid id, Guid modReferenceId, int direction)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var ordered = project.Mods.OrderBy(x => x.Order).ToList();
        var index = ordered.FindIndex(x => x.Id == modReferenceId);
        var target = index + Math.Sign(direction);
        if (index >= 0 && target >= 0 && target < ordered.Count)
            (ordered[index].Order, ordered[target].Order) = (ordered[target].Order, ordered[index].Order);
        store.Save(project);
        return RedirectToPage(new { id });
    }

    public IActionResult OnPostBuild(Guid id)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            var result = builder.Build(project);
            store.Save(project);
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
        try
        {
            var build = builder.Build(project);
            var result = await steamCmd.PublishAsync(project, build, cancellationToken);
            project.Automation.LastResult = Limit(result.CombinedOutput, 4000);
            store.Save(project);
            if (result.Success) TempData["Message"] = $"Publication SteamCMD terminée. Workshop ID : {project.PublishedWorkshopId}.";
            else TempData["Error"] = "SteamCMD a échoué : " + Limit(result.CombinedOutput, 1200);
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToPage(new { id });
    }

    public static string SelectionKey(DiscoveredMod mod) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mod.WorkshopId}|{mod.ModId}|{mod.ModRoot}"));

    private void Load(PackageProject project, bool refresh)
    {
        Project = project;
        InstalledMods = discovery.GetMods(project.TargetPzVersion, refresh);
        Validation = validator.Validate(project);
        WorkshopDescription = WorkshopDescriptionGenerator.Generate(project);
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
        project.Automation.Enabled = Form.AutomationEnabled;
        project.Automation.RefreshWorkshopSourcesBeforeBuild = Form.RefreshSources;
        project.Automation.PublishAfterBuild = Form.PublishAfterBuild;
        project.Automation.DailyTimes = (Form.DailyTimes ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[^length..];
    private static string FormatBytes(long bytes) => bytes > 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.00} Gio" : $"{bytes / (1024d * 1024):0.00} Mio";

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
        public bool AutomationEnabled { get; set; }
        public bool RefreshSources { get; set; }
        public bool PublishAfterBuild { get; set; }
        public string? DailyTimes { get; set; }

        public static ProjectForm From(PackageProject project) => new()
        {
            Name = project.Name, Description = project.Description, Mode = project.Mode, TargetPzVersion = project.TargetPzVersion,
            InjectConnectionNotice = project.InjectConnectionNotice, NoticeTitle = project.NoticeTitle,
            PublishedWorkshopId = project.PublishedWorkshopId, Visibility = project.Visibility, PreviewImagePath = project.PreviewImagePath,
            MapOrder = string.Join(";", project.MapOrder),
            Tags = string.Join(", ", project.Tags), LegalWarningAccepted = project.LegalWarningAccepted,
            SteamCmdPath = project.Automation.SteamCmdPath, SteamUsername = project.Automation.SteamUsername,
            AutomationEnabled = project.Automation.Enabled, RefreshSources = project.Automation.RefreshWorkshopSourcesBeforeBuild,
            PublishAfterBuild = project.Automation.PublishAfterBuild, DailyTimes = string.Join(", ", project.Automation.DailyTimes)
        };
    }

    private static int AddWithDependencies(PackageProject project, DiscoveredMod root, IReadOnlyList<DiscoveredMod> discovered)
    {
        var added = 0;
        var queue = new Queue<DiscoveredMod>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (project.Mods.Any(x => x.ModId.Equals(current.ModId, StringComparison.OrdinalIgnoreCase))) continue;
            project.Mods.Add(new PackageModReference
            {
                WorkshopId = current.WorkshopId,
                ModId = current.ModId,
                Name = current.Name,
                Author = current.Author,
                SourceModRoot = current.ModRoot,
                SelectedVersionFolder = current.SelectedVersionFolder,
                SourceUrl = current.WorkshopUrl,
                RequiredModIds = current.RequiredModIds,
                MapFolders = current.MapFolders,
                Order = project.Mods.Count
            });
            foreach (var map in current.MapFolders)
                if (!project.MapOrder.Contains(map, StringComparer.OrdinalIgnoreCase)) project.MapOrder.Add(map);
            added++;
            foreach (var required in current.RequiredModIds)
            {
                var dependency = discovered.FirstOrDefault(x => x.ModId.Equals(required, StringComparison.OrdinalIgnoreCase));
                if (dependency is not null) queue.Enqueue(dependency);
            }
        }
        return added;
    }
}
