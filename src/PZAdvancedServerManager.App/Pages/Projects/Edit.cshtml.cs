using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
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
    ApplicationPaths paths,
    PzEnvironmentService environment,
    PackageValidator validator,
    PackageProjectService projects,
    PackageLifecycleService lifecycle,
    WorkshopImportService workshopImport,
    ServerProfileService servers,
    MapPriorityService mapPriority,
    ModConflictAnalyzer conflicts,
    WorkshopCatalogService workshopCatalog,
    SteamCmdInstaller steamCmdInstaller,
    SteamCmdService steamCmd) : PageModel
{
    public PackageProject Project { get; private set; } = new();
    public IReadOnlyList<DiscoveredMod> InstalledMods { get; private set; } = [];
    public PackageValidationResult Validation { get; private set; } = new();
    public string WorkshopDescription { get; private set; } = string.Empty;
    public int WorkshopDescriptionUtf8Bytes { get; private set; }
    public bool WorkshopDescriptionIsCompact { get; private set; }
    public string WorkshopDescriptionError { get; private set; } = string.Empty;
    public IReadOnlyList<string> ServerConfigNames { get; private set; } = [];
    public MapOrderAnalysis MapAnalysis { get; private set; } = new([], []);
    public ModConflictAnalysis ConflictAnalysis { get; private set; } = new([], [], [], 0, 0, TimeSpan.Zero, string.Empty);
    public IReadOnlyList<ModConflictIssue> VisibleConflictIssues { get; private set; } = [];
    public string ConflictFilter { get; private set; } = "action";
    public string ConflictCategory { get; private set; } = "all";
    public string ConflictType { get; private set; } = "all";
    public int ConflictPage { get; private set; } = 1;
    public int ConflictPageCount { get; private set; } = 1;
    public int FilteredConflictCount { get; private set; }
    public int ConflictPageSize => 12;
    public IReadOnlyList<PackageModReference> VerifiedIncompatibleMods { get; private set; } = [];
    public IReadOnlyList<PackageModReference> UnavailableSourceMods { get; private set; } = [];
    public IReadOnlyList<PackageModReference> VisibleProjectMods { get; private set; } = [];
    public string ModQuery { get; private set; } = string.Empty;
    public string ModFilter { get; private set; } = "all";
    public int ModPage { get; private set; } = 1;
    public int ModPageCount { get; private set; } = 1;
    public int FilteredModCount { get; private set; }
    public int ModPageSize => 20;
    public Guid? ExpandedModId { get; private set; }
    public SteamCmdStatus SteamCmdStatus { get; private set; } = new(false, string.Empty, string.Empty, null, 0);
    public bool PreviewAvailable { get; private set; }
    public string PreviewSourceLabel { get; private set; } = "Preview générée par le manager";
    public ModListImportPreview? ModImportPreview { get; private set; }

    [BindProperty] public ProjectForm Form { get; set; } = new();
    [BindProperty] public IFormFile? PreviewUpload { get; set; }
    [BindProperty] public IFormFile? ModListUpload { get; set; }
    [BindProperty] public string ModListText { get; set; } = string.Empty;

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

    public async Task<IActionResult> OnPostSaveAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            ApplyForm(project);
            if (Form.ClearPreviewImage) project.PreviewImagePath = null;
            if (PreviewUpload is { Length: > 0 })
                project.PreviewImagePath = await SavePreviewUploadAsync(project.Id, PreviewUpload, cancellationToken);
            store.Save(project);
            TempData["Message"] = "Projet enregistré. Son identifiant stable, son Workshop ID et sa preview sont conservés.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { id });
    }

    public IActionResult OnGetPreview(Guid id)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var preview = ResolvePreviewPath(project);
        if (preview is null) return NotFound();
        var contentType = Path.GetExtension(preview).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            _ => "image/png"
        };
        return PhysicalFile(preview, contentType);
    }

    public async Task<IActionResult> OnPostAddModDependencyPlanAsync(Guid id, string selectionKey, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var discovered = environment.GetMods(project.TargetPzVersion);
        var selected = discovered.FirstOrDefault(mod => SelectionKey(mod) == selectionKey);
        if (selected is null) return new JsonResult(new { error = "La source choisie n'existe plus." }) { StatusCode = 404 };

        var local = PackageProjectComposer.PlanDependencies(project, [selected], discovered);
        var remote = selected.WorkshopId == 0
            ? []
            : await workshopCatalog.GetRequiredItemsAsync(selected.WorkshopId, cancellationToken);
        return DependencyPlanResult(project, local, remote);
    }

    public async Task<IActionResult> OnPostWorkshopDependencyPlanAsync(Guid id, ulong workshopId, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var remote = await workshopCatalog.GetRequiredItemsAsync(workshopId, cancellationToken);
        return DependencyPlanResult(project, new PackageDependencyPlan([], []), remote);
    }

    public async Task<IActionResult> OnPostAddModAsync(
        Guid id,
        string selectionKey,
        bool includeDependencies,
        bool dependencyChoiceAcknowledged,
        CancellationToken cancellationToken)
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
            var localPlan = PackageProjectComposer.PlanDependencies(project, [selected], discovered);
            var remotePlan = selected.WorkshopId == 0 || (dependencyChoiceAcknowledged && !includeDependencies)
                ? []
                : await workshopCatalog.GetRequiredItemsAsync(selected.WorkshopId, cancellationToken);
            var missingRemote = FilterMissingRemoteDependencies(project, remotePlan);
            if ((localPlan.AvailableDependencies.Count > 0 || missingRemote.Count > 0) && !dependencyChoiceAcknowledged)
                throw new InvalidOperationException("Confirmez le choix des dépendances dans le dialogue du manager.");

            var importedDependencies = 0;
            if (includeDependencies)
            {
                foreach (var dependency in missingRemote)
                {
                    var imported = await workshopImport.ImportAsync(project, dependency.WorkshopId, cancellationToken);
                    importedDependencies += imported.AddedMods;
                }
                if (missingRemote.Count > 0) discovered = environment.GetMods(project.TargetPzVersion, refresh: true);
            }
            var added = projects.AddWithDependencies(project, selected, discovered, includeDependencies) + importedDependencies;
            if (added > 0)
                TempData["Message"] = includeDependencies && added > 1
                    ? $"« {selected.Name} » et {added - 1} dépendance(s) ont été ajoutés, ordonnés et figés. Renseignez leurs autorisations."
                    : $"« {selected.Name} » ajouté et figé. Renseignez maintenant son autorisation.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { id, tab = "mods" });
    }

    public async Task<IActionResult> OnPostAddMissingDependencyAsync(
        Guid id,
        Guid modReferenceId,
        string requiredModId,
        CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var requester = project.Mods.FirstOrDefault(mod => mod.Id == modReferenceId);
        if (requester is null) return NotFound();
        var normalizedRequired = ModInfoParser.NormalizeDependencyId(requiredModId);
        if (project.Mods.Any(mod => ModInfoParser.NormalizeDependencyId(mod.ModId).Equals(normalizedRequired, StringComparison.OrdinalIgnoreCase)))
        {
            TempData["Message"] = $"La dépendance « {normalizedRequired} » est déjà présente dans le pack.";
            return DependencyRedirect(id);
        }

        try
        {
            var available = environment.GetMods(project.TargetPzVersion);
            var dependency = available.FirstOrDefault(mod => ModInfoParser.NormalizeDependencyId(mod.ModId).Equals(normalizedRequired, StringComparison.OrdinalIgnoreCase));
            if (dependency is null && requester.WorkshopId != 0)
            {
                var requiredItems = await workshopCatalog.GetRequiredItemsAsync(requester.WorkshopId, cancellationToken);
                foreach (var item in FilterMissingRemoteDependencies(project, requiredItems))
                {
                    var download = await workshopImport.DownloadAsync(project, item.WorkshopId, cancellationToken);
                    dependency = download.Mods.FirstOrDefault(mod => ModInfoParser.NormalizeDependencyId(mod.ModId).Equals(normalizedRequired, StringComparison.OrdinalIgnoreCase));
                    if (dependency is null) continue;
                    available = environment.GetMods(project.TargetPzVersion, refresh: true).Concat(download.Mods).ToArray();
                    break;
                }
            }
            if (dependency is null)
                throw new InvalidOperationException($"Aucune source locale ni dépendance Workshop officielle ne fournit le Mod ID « {normalizedRequired} ». Ouvrez le catalogue Workshop et recherchez ce Mod ID.");

            var added = projects.AddWithDependencies(project, dependency, available);
            var repaired = conflicts.Analyze(project, refresh: true);
            if (!repaired.Issues.Any(issue => issue.Code == "CYCLE_ORDER" && !issue.IsResolved))
            {
                ApplyRecommendedOrder(project, repaired, includeMaps: false);
                store.Save(project);
            }
            TempData["Message"] = $"Dépendance « {normalizedRequired} » ajoutée avec {Math.Max(0, added - 1)} dépendance(s) transitive(s), puis ordre de chargement recalculé.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return DependencyRedirect(id);
    }

    public async Task<IActionResult> OnPostPreviewModListAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        Load(project, refresh: false);
        Form = ProjectForm.From(project);
        Form.MapOrder = string.Join(';', MapAnalysis.Entries.Select(x => x.FolderName));
        try
        {
            var content = await ReadModListSourceAsync(ModListUpload, ModListText, cancellationToken);
            var parsed = ModListImportParser.Parse(content);
            ModImportPreview = BuildModImportPreview(project, parsed, InstalledMods, ModListUpload?.FileName);
        }
        catch (Exception exception)
        {
            TempData["Error"] = "Impossible d'analyser la liste de mods : " + exception.Message;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostApplyModListAsync(Guid id, string[] selectedEntries, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            TempData["Message"] = await ApplyModListAsync(project, selectedEntries, cancellationToken);
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToPage(new { id, tab = "mods" });
    }

    public async Task<IActionResult> OnPostApplyModListStreamAsync(Guid id, string[] selectedEntries, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        return await StreamOperationAsync(
            progress => ApplyModListAsync(project, selectedEntries, cancellationToken, progress),
            Url.Page("/Projects/Edit", null, new { id, tab = "mods" })!,
            cancellationToken);
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

    public IActionResult OnPostReorder(Guid id, Guid modReferenceId, string placement, Guid? targetModReferenceId, string? modQuery, string? modFilter, int modPage = 1)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        if (!Enum.TryParse<ModPlacement>(placement, ignoreCase: true, out var parsedPlacement))
        {
            TempData["Error"] = "La position demandée n'est pas reconnue.";
            return RedirectToMod(project, modReferenceId, modQuery, modFilter, modPage);
        }

        try
        {
            projects.Reorder(project, modReferenceId, parsedPlacement, targetModReferenceId);
            var moved = project.Mods.First(mod => mod.Id == modReferenceId);
            TempData["Message"] = $"« {moved.Name} » occupe maintenant la position {moved.Order + 1} sur {project.Mods.Count}.";
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToMod(project, modReferenceId, modQuery, modFilter, modPage);
    }

    public IActionResult OnPostApplyRecommendedOrder(Guid id)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var analysis = conflicts.Analyze(project, refresh: true);
        if (analysis.Issues.Any(issue => issue.Code == "CYCLE_ORDER" && !issue.IsResolved))
        {
            TempData["Error"] = "L'ordre ne peut pas être appliqué tant qu'un cycle de contraintes subsiste. Utilisez le correctif proposé sur le blocage concerné ou examinez ses preuves.";
            return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id, tab = "compatibility", conflictFilter = "errors" }, fragment: "conflict-workbench");
        }
        ApplyRecommendedOrder(project, analysis, includeMaps: true);
        store.Save(project);
        TempData["Message"] = "Ordre recommand\u00e9 appliqu\u00e9 aux mods et aux cartes. Les d\u00e9pendances et contraintes mod.info sont maintenant respect\u00e9es; les choix manuels de priorit\u00e9 sont conserv\u00e9s.";
        return RedirectToPage(new { id, tab = "compatibility" });
    }

    public IActionResult OnPostRepairOrderIssue(Guid id, string conflictKey)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var analysis = conflicts.Analyze(project, refresh: true);
        var issue = analysis.Issues.FirstOrDefault(candidate => candidate.Key.Equals(conflictKey, StringComparison.Ordinal));
        if (issue is null || !issue.CanAutoFixOrder)
        {
            TempData["Error"] = "Ce diagnostic a changé ou ne possède plus de correction automatique vérifiable. Relancez l'analyse.";
            return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id, tab = "compatibility", conflictFilter = "errors" }, fragment: "conflict-workbench");
        }

        var removedDecisions = issue.AutomaticOrderFixKeys
            .Where(project.ConflictWinners.ContainsKey)
            .ToDictionary(key => key, key => project.ConflictWinners[key], StringComparer.OrdinalIgnoreCase);
        foreach (var key in removedDecisions.Keys) project.ConflictWinners.Remove(key);

        var repaired = conflicts.Analyze(project, refresh: true);
        var affectedIds = issue.ModReferenceIds.ToHashSet();
        var targetedCycleRemains = repaired.Issues.Any(candidate =>
            candidate.Code == "CYCLE_ORDER"
            && !candidate.IsResolved
            && candidate.ModReferenceIds.Any(affectedIds.Contains));
        if (targetedCycleRemains)
        {
            foreach (var decision in removedDecisions) project.ConflictWinners[decision.Key] = decision.Value;
            TempData["Error"] = "La correction automatique n'a pas produit un graphe d'ordre valide. Aucun choix ni ordre n'a été modifié.";
            return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id, tab = "compatibility", conflictFilter = "errors" }, fragment: "conflict-workbench");
        }

        var remainingCycles = repaired.Issues.Any(candidate => candidate.Code == "CYCLE_ORDER" && !candidate.IsResolved);
        if (!remainingCycles) ApplyRecommendedOrder(project, repaired, includeMaps: false);
        store.Save(project);

        TempData["Message"] = removedDecisions.Count > 0
            ? $"Ordre réparé : {removedDecisions.Count} priorité manuelle contradictoire retirée, puis dépendances et contraintes de chargement recalculées. Aucun mod n'a été désactivé."
            : "Ordre réparé : les dépendances et contraintes de chargement ont été replacées automatiquement. Aucun mod n'a été désactivé.";
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id, tab = "compatibility", conflictFilter = "action" }, fragment: "conflict-workbench");
    }

    public IActionResult OnPostApplyResolutionBatch(Guid id, string recipe)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();

        var analysis = conflicts.Analyze(project, refresh: true);
        var issueCodes = recipe switch
        {
            "verified-incompatible" => new HashSet<string>(["B42_LEGACY"], StringComparer.OrdinalIgnoreCase),
            "unavailable-sources" => new HashSet<string>(["SOURCE_MISSING", "MANIFEST_MISSING"], StringComparer.OrdinalIgnoreCase),
            "all-safe" => new HashSet<string>(["B42_LEGACY", "SOURCE_MISSING", "MANIFEST_MISSING"], StringComparer.OrdinalIgnoreCase),
            _ => null
        };
        if (issueCodes is null)
        {
            TempData["Error"] = "Cette recette de résolution n'existe plus. Relancez l'analyse avant de continuer.";
            return RedirectToPage(new { id, tab = "compatibility" });
        }

        var targetIds = analysis.Issues
            .Where(issue => !issue.IsResolved && issueCodes.Contains(issue.Code))
            .SelectMany(issue => issue.ModReferenceIds)
            .ToHashSet();
        var targets = project.Mods.Where(mod => mod.Enabled && targetIds.Contains(mod.Id)).ToArray();
        var applyOrder = recipe == "all-safe";
        if (targets.Length == 0 && (!applyOrder || !analysis.HasOrderChange))
        {
            TempData["Message"] = "Aucune correction vérifiée n'est nécessaire pour cette recette. Le pack n'a pas été modifié.";
            return RedirectToPage(new { id, tab = "compatibility" });
        }

        DisableMods(project, analysis, targets);
        if (applyOrder)
        {
            analysis = conflicts.Analyze(project, refresh: true);
            ApplyRecommendedOrder(project, analysis, includeMaps: true);
        }
        store.Save(project);

        var disabledNames = targets.Select(mod => mod.ModId).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var disabledSummary = disabledNames.Length == 0
            ? "aucun mod désactivé"
            : $"{disabledNames.Length} mod(s) désactivé(s) : {string.Join(", ", disabledNames.Take(12))}{(disabledNames.Length > 12 ? $" et {disabledNames.Length - 12} autre(s)" : string.Empty)}";
        TempData["Message"] = applyOrder
            ? $"Corrections sûres appliquées : {disabledSummary}; ordre des mods et des cartes recalculé. Aucun snapshot ni fichier source n'a été supprimé."
            : $"Résolution appliquée : {disabledSummary}. Les snapshots sont conservés et chaque mod peut être réactivé depuis « Mods & droits ».";
        return RedirectToPage(new { id, tab = "compatibility" });
    }

    public IActionResult OnPostResolveConflict(Guid id, string conflictKey, string winnerModId)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var analysis = conflicts.Analyze(project, refresh: true);
        var issue = analysis.Issues.FirstOrDefault(candidate => candidate.Key.Equals(conflictKey, StringComparison.Ordinal));
        if (issue is null || !issue.CanChooseWinner || !issue.ModIds.Contains(winnerModId, StringComparer.OrdinalIgnoreCase))
        {
            TempData["Error"] = "La collision a chang\u00e9 depuis son affichage. Relancez l'analyse avant de choisir une priorit\u00e9.";
            return RedirectToPage(new { id, tab = "compatibility" });
        }
        project.ConflictWinners[issue.Key] = winnerModId;
        project.AcknowledgedConflicts.RemoveAll(key => key.Equals(issue.Key, StringComparison.OrdinalIgnoreCase));
        analysis = conflicts.Analyze(project, refresh: true);
        ApplyRecommendedOrder(project, analysis, includeMaps: false);
        store.Save(project);
        TempData["Message"] = $"Priorit\u00e9 enregistr\u00e9e : \u00ab {winnerModId} \u00bb sera charg\u00e9 apr\u00e8s les autres mods concern\u00e9s. Reconstruisez puis testez le comportement associ\u00e9.";
        return RedirectToPage(new { id, tab = "compatibility" });
    }

    public IActionResult OnPostClearConflictResolution(Guid id, string conflictKey)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        project.ConflictWinners.Remove(conflictKey);
        project.AcknowledgedConflicts.RemoveAll(key => key.Equals(conflictKey, StringComparison.OrdinalIgnoreCase));
        store.Save(project);
        TempData["Message"] = "Choix manuel retir\u00e9. L'analyseur utilisera de nouveau les seules contraintes d\u00e9clar\u00e9es par les mods.";
        return RedirectToPage(new { id, tab = "compatibility" });
    }

    public IActionResult OnPostAcknowledgeConflict(Guid id, string conflictKey)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var issue = conflicts.Analyze(project, refresh: true).Issues.FirstOrDefault(candidate => candidate.Key.Equals(conflictKey, StringComparison.Ordinal));
        if (issue is null)
        {
            TempData["Error"] = "Le conflit n'existe plus dans la version actuelle du pack.";
            return RedirectToPage(new { id, tab = "compatibility" });
        }
        if (!project.AcknowledgedConflicts.Contains(issue.Key, StringComparer.OrdinalIgnoreCase)) project.AcknowledgedConflicts.Add(issue.Key);
        store.Save(project);
        TempData["Message"] = "Conflit document\u00e9 comme volontaire. Il restera visible et pourra \u00eatre rouvert si les fichiers ou les mods changent.";
        return RedirectToPage(new { id, tab = "compatibility" });
    }

    public IActionResult OnPostOpenConflictFile(Guid id, string conflictKey, int fileIndex)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();

        var issue = conflicts.Analyze(project).Issues.FirstOrDefault(candidate => candidate.Key.Equals(conflictKey, StringComparison.Ordinal));
        if (issue is null || fileIndex < 0 || fileIndex >= issue.FileEvidence.Count)
        {
            TempData["Error"] = "La preuve physique n'est plus disponible. Rafraîchissez l'analyse avant de réessayer.";
            return RedirectToPage(new { id, tab = "compatibility" });
        }

        var evidence = issue.FileEvidence[fileIndex];
        var candidatePath = Path.GetFullPath(evidence.PhysicalPath);
        var sourceRoots = project.Mods
            .Select(mod => mod.BuildSourceRoot)
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .ToArray();
        if (!System.IO.File.Exists(candidatePath) || !sourceRoots.Any(root => IsPathWithin(candidatePath, root)))
        {
            TempData["Error"] = "Le fichier demandé n'appartient plus à une source de mod contrôlée par ce pack.";
            return RedirectToPage(new { id, tab = "compatibility", conflictType = issue.EffectiveTypeLabel });
        }

        try
        {
            var startInfo = new ProcessStartInfo { UseShellExecute = OperatingSystem.IsWindows() };
            if (OperatingSystem.IsWindows())
            {
                startInfo.FileName = candidatePath;
            }
            else
            {
                startInfo.FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                startInfo.ArgumentList.Add(candidatePath);
            }
            Process.Start(startInfo)?.Dispose();
            TempData["Message"] = $"Preuve ouverte : {evidence.VirtualPath} ({evidence.ModId}).";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            TempData["Error"] = $"Impossible d'ouvrir ce fichier avec l'application du système : {exception.Message}";
        }
        return RedirectToPage(new { id, tab = "compatibility", conflictType = issue.EffectiveTypeLabel });
    }

    public IActionResult OnPostSetModEnabled(Guid id, Guid modReferenceId, bool enabled)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var mod = project.Mods.FirstOrDefault(candidate => candidate.Id == modReferenceId);
        if (mod is null) return NotFound();
        mod.Enabled = enabled;
        if (!enabled)
        {
            foreach (var key in project.ConflictWinners.Where(entry => entry.Value.Equals(mod.ModId, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToArray()) project.ConflictWinners.Remove(key);
        }
        store.Save(project);
        TempData["Message"] = enabled ? $"{mod.Name} r\u00e9activ\u00e9 dans le pack." : $"{mod.Name} d\u00e9sactiv\u00e9 du prochain build. Son snapshot est conserv\u00e9.";
        return RedirectToPage(new { id, tab = "compatibility" });
    }

    public IActionResult OnPostBuild(Guid id)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            var result = lifecycle.Build(project);
            var physicallyCopiedBytes = Math.Max(0, result.CopiedBytes - result.HardLinkedBytes);
            TempData["Message"] = result.IsNoOp
                ? $"Pack déjà à jour : {result.ReusedComponents:N0} composants et {result.ReusedFiles:N0} fichiers réutilisés, aucune reconstruction du contenu. Dossier : {result.BuildRoot}"
                : result.IsIncremental
                    ? $"Pack mis à jour incrémentalement : {result.RebuiltComponents:N0} composant(s) reconstruit(s), {result.ReusedComponents:N0} réutilisé(s), {result.RemovedComponents:N0} retiré(s); {result.HardLinkedFiles:N0} fichiers liés ({FormatBytes(result.HardLinkedBytes)} sans duplication), {FormatBytes(physicallyCopiedBytes)} copiés. Dossier : {result.BuildRoot}"
                    : result.HardLinkedFiles > 0
                        ? $"Pack construit : {result.CopiedFiles:N0} fichiers; {result.HardLinkedFiles:N0} liés ({FormatBytes(result.HardLinkedBytes)} sans duplication), {FormatBytes(physicallyCopiedBytes)} copiés. Dossier : {result.BuildRoot}"
                        : $"Pack construit : {result.CopiedFiles:N0} fichiers, {FormatBytes(result.CopiedBytes)} copiés. Dossier : {result.BuildRoot}";
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

    public IActionResult OnPostOpenBuildFolder(Guid id)
    {
        if (store.Get(id) is null) return NotFound();
        var buildRoot = Path.GetFullPath(paths.BuildRoot(id));
        if (!Directory.Exists(buildRoot))
        {
            TempData["Error"] = "Aucun dossier de package n'existe encore. Construisez d'abord le pack.";
            return RedirectToPage(new { id });
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(buildRoot);
            Process.Start(start)?.Dispose();
            TempData["Message"] = $"Dossier du package ouvert : {buildRoot}";
        }
        catch (Exception exception)
        {
            TempData["Error"] = $"Impossible d'ouvrir le gestionnaire de fichiers : {exception.Message} Dossier : {buildRoot}";
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
            TempData["Message"] = result.PublicationSkipped
                ? "Aucun changement local ou distant : SteamCMD n'a pas été lancé et le serveur n'a pas été interrompu."
                : (createsWorkshopItem ? "Nouvel item Workshop créé" : "Item Workshop mis à jour") + $". Workshop ID : {project.PublishedWorkshopId}." +
                  (result.ServerWasRunning ? " Le serveur est resté actif pendant l'upload, puis a été sauvegardé et redémarré après le délai de propagation." : string.Empty);
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
            return result.PublicationSkipped
                ? "Aucun changement confirmé · SteamCMD non lancé"
                : (createsWorkshopItem ? "Nouvel item Workshop créé" : "Item Workshop mis à jour") + $" · ID {project.PublishedWorkshopId} · {result.PublicationMode}";
        }, Url.Page("/Projects/Edit", null, new { id, tab = "distribution" })!, cancellationToken);
    }

    public async Task<IActionResult> OnPostForcePublishAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var validation = validator.Validate(project);
        if (!validation.CanPublish)
        {
            var blockers = validation.Issues.Where(x => x.IsError && x.Scope != ValidationScope.AutomationOnly).Take(5).Select(x => x.Message);
            TempData["Error"] = "Republication forcée non lancée : " + string.Join(" ", blockers);
            return RedirectToPage(new { id, tab = "distribution" });
        }
        try
        {
            var result = await lifecycle.PublishAsync(project, false, false, cancellationToken, force: true);
            project.Automation.LastResult = Limit(result.Output, 4000);
            project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
            store.Save(project);
            TempData["Message"] = $"Republication forcée confirmée. Workshop ID : {project.PublishedWorkshopId}.";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage(new { id, tab = "distribution" });
    }

    public async Task<IActionResult> OnPostForcePublishStreamAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        var validation = validator.Validate(project);
        if (!validation.CanPublish)
        {
            await PrepareProgressResponseAsync();
            var blockers = validation.Issues.Where(x => x.IsError && x.Scope != ValidationScope.AutomationOnly).Take(5).Select(x => x.Message);
            await WriteProgressAsync(new { type = "error", message = "Republication forcée non lancée : " + string.Join(" ", blockers) }, cancellationToken);
            return new EmptyResult();
        }
        return await StreamOperationAsync(async progress =>
        {
            var result = await lifecycle.PublishAsync(project, false, false, cancellationToken, progress, force: true);
            project.Automation.LastResult = Limit(result.Output, 4000);
            project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
            store.Save(project);
            return $"Republication forcée confirmée · ID {project.PublishedWorkshopId} · {result.PublicationMode}";
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

    public async Task<IActionResult> OnPostVerifySteamSessionAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            ApplyForm(project);
            store.Save(project);
            var result = await steamCmd.VerifyCachedSessionAsync(project, cancellationToken);
            if (result.Interaction != SteamCmdInteraction.None) throw SteamCmdInteractionRequiredException.FromResult(result);
            if (!result.Success) throw new InvalidOperationException("Vérification de la session SteamCMD échouée : " + Limit(result.CombinedOutput, 1800));
            project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
            store.Save(project);
            TempData["Message"] = "Session SteamCMD existante vérifiée sans mot de passe et sans créer de nouveau jeton.";
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

    public async Task<IActionResult> OnPostVerifySteamSessionStreamAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        ApplyForm(project);
        store.Save(project);
        return await StreamOperationAsync(async progress =>
        {
            var result = await steamCmd.VerifyCachedSessionAsync(project, cancellationToken, progress);
            if (result.Interaction != SteamCmdInteraction.None) throw SteamCmdInteractionRequiredException.FromResult(result);
            if (!result.Success) throw new InvalidOperationException("Vérification de la session SteamCMD échouée : " + Limit(result.CombinedOutput, 1800));
            project.Automation.SteamSessionVerifiedAt = DateTimeOffset.UtcNow;
            store.Save(project);
            return "Session SteamCMD existante vérifiée sans mot de passe ni nouveau jeton";
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

    public async Task<IActionResult> OnPostImportWorkshopAsync(
        Guid id,
        ulong workshopId,
        bool includeDependencies,
        bool dependencyChoiceAcknowledged,
        CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        try
        {
            var dependencies = dependencyChoiceAcknowledged && !includeDependencies
                ? []
                : FilterMissingRemoteDependencies(project, await workshopCatalog.GetRequiredItemsAsync(workshopId, cancellationToken));
            if (dependencies.Count > 0 && !dependencyChoiceAcknowledged)
                throw new InvalidOperationException("Confirmez le choix des dépendances dans le dialogue du manager.");
            var ids = includeDependencies
                ? dependencies.Select(item => item.WorkshopId).Append(workshopId)
                : [workshopId];
            var added = 0;
            foreach (var itemId in ids.Distinct()) added += (await workshopImport.ImportAsync(project, itemId, cancellationToken)).AddedMods;
            TempData["Message"] = $"Item Workshop {workshopId} téléchargé : {added} nouveau(x) Mod ID ajouté(s), ordonné(s) et figé(s).";
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

    public async Task<IActionResult> OnPostInstallSteamCmdStreamAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        return await StreamOperationAsync(async progress =>
        {
            var result = await steamCmdInstaller.InstallAsync(cancellationToken, progress);
            if (!result.Bootstrapped)
                throw new InvalidOperationException("SteamCMD a été extrait, mais son initialisation a échoué : " + Limit(result.Output, 1200));
            project.Automation.SteamCmdPath = result.ExecutablePath;
            store.Save(project);
            environment.Invalidate();
            return "SteamCMD portable téléchargé, initialisé et prêt";
        }, Url.Page("/Projects/Edit", null, new { id, tab = "distribution" })!, cancellationToken);
    }

    public static string SelectionKey(DiscoveredMod mod) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mod.WorkshopId}|{mod.ModId}|{mod.ModRoot}"));

    public static string ModImportEntryValue(string modId) => "mod:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(modId));

    private static async Task<string> ReadModListSourceAsync(IFormFile? upload, string? pastedList, CancellationToken cancellationToken)
    {
        if (upload is not { Length: > 0 })
        {
            if (string.IsNullOrWhiteSpace(pastedList)) throw new InvalidDataException("Choisissez un fichier .ini ou collez une liste séparée par des points-virgules.");
            if (Encoding.UTF8.GetByteCount(pastedList) > 1024 * 1024) throw new InvalidDataException("La liste collée dépasse 1 Mio.");
            return pastedList;
        }

        if (upload.Length > 1024 * 1024) throw new InvalidDataException("Le fichier dépasse 1 Mio.");
        var extension = Path.GetExtension(upload.FileName).ToLowerInvariant();
        if (extension is not ".ini" and not ".txt" and not ".cfg")
            throw new InvalidDataException("Formats acceptés : .ini, .txt et .cfg.");

        await using var memory = new MemoryStream((int)upload.Length);
        await upload.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            return Encoding.UTF8.GetString(bytes[3..]);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static ModListImportPreview BuildModImportPreview(PackageProject project, ParsedModList parsed, IReadOnlyList<DiscoveredMod> installed, string? fileName)
    {
        var candidates = new List<ModListImportCandidate>();
        var listedWorkshopIds = parsed.WorkshopIds.ToHashSet();
        var installedWorkshopIds = installed.Where(mod => mod.WorkshopId > 0).Select(mod => mod.WorkshopId).ToHashSet();
        var hasPendingWorkshopDownload = parsed.WorkshopIds.Any(workshopId => !installedWorkshopIds.Contains(workshopId));
        var existingModIds = project.Mods.Select(mod => mod.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddModCandidate(string modId)
        {
            if (!addedModIds.Add(modId)) return;
            var source = installed
                .Where(mod => mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(mod => listedWorkshopIds.Contains(mod.WorkshopId))
                .ThenByDescending(mod => mod.SourceUpdatedAt)
                .FirstOrDefault();
            var alreadyAdded = existingModIds.Contains(modId);
            var pendingDownload = source is null && hasPendingWorkshopDownload;
            var selectable = !alreadyAdded && (source is not null || pendingDownload);
            var title = source?.Name ?? modId;
            var author = source is null || string.IsNullOrWhiteSpace(source.Author) ? "Auteur non déclaré" : source.Author;
            var detail = source is null
                ? pendingDownload ? "La source sera recherchée dans les items Workshop sélectionnés." : "Aucune source locale ou Workshop détectée pour ce Mod ID."
                : $"{author} · {(source.WorkshopId == 0 ? "source locale" : $"Workshop {source.WorkshopId}")} · version {(string.IsNullOrWhiteSpace(source.Version) ? "non déclarée" : source.Version)}";
            var status = alreadyAdded ? "DÉJÀ DANS LE PACK" : source is not null ? "SOURCE DÉTECTÉE" : pendingDownload ? "APRÈS TÉLÉCHARGEMENT" : "SOURCE INTROUVABLE";
            var tone = alreadyAdded ? "existing" : source is not null ? "resolved" : pendingDownload ? "pending" : "missing";
            var value = source is null ? ModImportEntryValue(modId) : "source:" + SelectionKey(source);
            candidates.Add(new ModListImportCandidate(value, "Mod ID", modId, title, detail, status, tone, selectable, selectable));
        }

        foreach (var modId in parsed.ModIds) AddModCandidate(modId);
        foreach (var workshopId in parsed.WorkshopIds)
        {
            var workshopMods = installed.Where(mod => mod.WorkshopId == workshopId).OrderBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (parsed.ModIds.Count == 0)
                foreach (var mod in workshopMods) AddModCandidate(mod.ModId);

            if (workshopMods.Count == 0)
            {
                var detail = parsed.ModIds.Count == 0
                    ? "SteamCMD téléchargera l'item puis proposera tous ses Mod IDs compatibles."
                    : "SteamCMD téléchargera l'item pour résoudre les Mod IDs sélectionnés ci-dessus.";
                candidates.Add(new ModListImportCandidate($"workshop:{workshopId}", "Workshop ID", workshopId.ToString(), $"Workshop item {workshopId}", detail, "TÉLÉCHARGEMENT REQUIS", "download", true, true));
            }
            else
            {
                candidates.Add(new ModListImportCandidate(string.Empty, "Workshop ID", workshopId.ToString(), $"Workshop item {workshopId}", $"{workshopMods.Count} Mod ID(s) compatible(s) déjà détecté(s) sur le disque.", "DISPONIBLE", "resolved", false, false));
            }
        }

        foreach (var invalid in parsed.InvalidWorkshopIds)
            candidates.Add(new ModListImportCandidate(string.Empty, "Workshop ID", invalid, $"Workshop ID invalide : {invalid}", "Cette valeur ne peut pas être téléchargée et a été exclue.", "VALEUR INVALIDE", "missing", false, false));

        var sourceLabel = parsed.SourceKind == ModListSourceKind.ServerIni
            ? string.IsNullOrWhiteSpace(fileName) ? "configuration INI collée" : fileName
            : "liste de Mod IDs collée";
        return new ModListImportPreview(sourceLabel, parsed.ModIds.Count, parsed.WorkshopIds.Count, candidates);
    }

    private async Task<string> ApplyModListAsync(PackageProject project, string[] selectedEntries, CancellationToken cancellationToken, IProgress<OperationProgress>? progress = null)
    {
        if (selectedEntries.Length == 0) throw new InvalidOperationException("Sélectionnez au moins une entrée à importer.");
        if (selectedEntries.Length > 1000) throw new InvalidOperationException("La sélection dépasse la limite de 1 000 entrées.");

        progress?.Report(new OperationProgress("validate", $"Validation de {selectedEntries.Length} entrée(s) sélectionnée(s)."));
        var requestedMods = new List<(bool IsSource, string Value)>();
        var workshopIds = new List<ulong>();
        foreach (var entry in selectedEntries.Distinct(StringComparer.Ordinal))
        {
            if (entry.StartsWith("mod:", StringComparison.Ordinal))
            {
                try
                {
                    var modId = Encoding.UTF8.GetString(Convert.FromBase64String(entry[4..])).Trim();
                    if (modId.Length > 0 && !requestedMods.Any(item => !item.IsSource && item.Value.Equals(modId, StringComparison.OrdinalIgnoreCase)))
                        requestedMods.Add((false, modId));
                }
                catch (FormatException) { throw new InvalidOperationException("Une entrée Mod ID sélectionnée est invalide."); }
            }
            else if (entry.StartsWith("source:", StringComparison.Ordinal) && entry.Length > 7)
            {
                var sourceKey = entry[7..];
                if (!requestedMods.Any(item => item.IsSource && item.Value.Equals(sourceKey, StringComparison.Ordinal)))
                    requestedMods.Add((true, sourceKey));
            }
            else if (entry.StartsWith("workshop:", StringComparison.Ordinal)
                && ulong.TryParse(entry[9..], out var workshopId) && workshopId > 0
                && !workshopIds.Contains(workshopId))
            {
                workshopIds.Add(workshopId);
            }
        }
        if (requestedMods.Count == 0 && workshopIds.Count == 0) throw new InvalidOperationException("La sélection ne contient aucune entrée importable.");

        var downloaded = new List<DiscoveredMod>();
        if (workshopIds.Count == 0)
            progress?.Report(new OperationProgress("download", "Toutes les sources sélectionnées sont déjà disponibles sur le disque."));
        for (var index = 0; index < workshopIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workshopId = workshopIds[index];
            progress?.Report(new OperationProgress("download", $"Téléchargement Workshop {workshopId} ({index + 1}/{workshopIds.Count}).", index + 1, workshopIds.Count));
            var result = await workshopImport.DownloadAsync(project, workshopId, cancellationToken);
            downloaded.AddRange(result.Mods);
        }

        progress?.Report(new OperationProgress("resolve", "Résolution des Mod IDs et de leurs dépendances."));
        var allKnown = environment.GetMods(project.TargetPzVersion, refresh: workshopIds.Count > 0)
            .Concat(downloaded)
            .GroupBy(mod => $"{mod.WorkshopId}:{mod.ModId}:{mod.ModRoot}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var selectedMods = new List<DiscoveredMod>();
        foreach (var requested in requestedMods)
        {
            if (requested.IsSource)
            {
                var match = allKnown.FirstOrDefault(mod => SelectionKey(mod).Equals(requested.Value, StringComparison.Ordinal));
                if (match is null) throw new InvalidOperationException("Une source sélectionnée n'est plus disponible. Relancez l'analyse de la liste.");
                selectedMods.Add(match);
                continue;
            }

            var resolved = allKnown
                .Where(mod => mod.ModId.Equals(requested.Value, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(mod => workshopIds.Contains(mod.WorkshopId))
                .ThenByDescending(mod => mod.SourceUpdatedAt)
                .FirstOrDefault();
            if (resolved is null) throw new InvalidOperationException($"Le Mod ID « {requested.Value} » reste introuvable après analyse des sources sélectionnées.");
            selectedMods.Add(resolved);
        }
        if (requestedMods.Count == 0)
            selectedMods.AddRange(allKnown.Where(mod => workshopIds.Contains(mod.WorkshopId)));
        selectedMods = selectedMods.GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        if (selectedMods.Count == 0) throw new InvalidOperationException("Aucun Mod ID compatible n'a été trouvé dans les sources sélectionnées.");

        var added = 0;
        for (var index = 0; index < selectedMods.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mod = selectedMods[index];
            progress?.Report(new OperationProgress("snapshot", $"Ajout et snapshot de « {mod.Name} » ({index + 1}/{selectedMods.Count}).", index + 1, selectedMods.Count));
            added += projects.AddWithDependencies(project, mod, allKnown);
        }
        progress?.Report(new OperationProgress("save", "Pack enregistré avec l'ordre de la liste importée."));
        return added == 0
            ? "La sélection était déjà entièrement présente dans le pack. Aucun snapshot n'a été remplacé."
            : $"{added} Mod ID(s) ajouté(s) avec leurs dépendances disponibles et leurs versions figées.";
    }

    private JsonResult DependencyPlanResult(
        PackageProject project,
        PackageDependencyPlan local,
        IReadOnlyList<WorkshopRequiredItem> remote)
    {
        var dependencies = local.AvailableDependencies
            .Select(mod => new { id = mod.ModId, name = mod.Name, source = mod.WorkshopId == 0 ? "Source locale" : $"Workshop {mod.WorkshopId}" })
            .Concat(FilterMissingRemoteDependencies(project, remote).Select(item => new { id = item.WorkshopId.ToString(), name = item.Title, source = $"Workshop {item.WorkshopId}" }))
            .DistinctBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new JsonResult(new { dependencies, unresolved = local.UnresolvedModIds });
    }

    private static IReadOnlyList<WorkshopRequiredItem> FilterMissingRemoteDependencies(
        PackageProject project,
        IReadOnlyList<WorkshopRequiredItem> dependencies)
    {
        var includedWorkshopIds = project.Mods.Where(mod => mod.WorkshopId != 0).Select(mod => mod.WorkshopId).ToHashSet();
        return dependencies.Where(item => !includedWorkshopIds.Contains(item.WorkshopId)).DistinctBy(item => item.WorkshopId).ToArray();
    }

    private RedirectToPageResult DependencyRedirect(Guid id) =>
        RedirectToPage(pageName: null, pageHandler: null, routeValues: new { id, tab = "compatibility", conflictCategory = "Dependency" }, fragment: "conflict-workbench");

    private void Load(PackageProject project, bool refresh)
    {
        Project = project;
        SteamCmdStatus = steamCmdInstaller.GetStatus();
        if (string.IsNullOrWhiteSpace(project.Automation.SteamCmdPath) && SteamCmdStatus.Installed)
            project.Automation.SteamCmdPath = SteamCmdStatus.ExecutablePath;
        InstalledMods = environment.GetMods(project.TargetPzVersion, refresh);
        Validation = validator.Validate(project);
        var workshopDescription = WorkshopDescriptionGenerator.GenerateResult(project);
        WorkshopDescription = workshopDescription.Text;
        WorkshopDescriptionUtf8Bytes = workshopDescription.Utf8Bytes;
        WorkshopDescriptionIsCompact = workshopDescription.IsCompact;
        WorkshopDescriptionError = workshopDescription.ErrorMessage;
        ServerConfigNames = servers.List().Select(x => x.Name).ToList();
        MapAnalysis = mapPriority.Analyze(project);
        ConflictAnalysis = conflicts.Analyze(project, refresh);
        VerifiedIncompatibleMods = FindBatchTargets(project, ConflictAnalysis, "B42_LEGACY");
        UnavailableSourceMods = FindBatchTargets(project, ConflictAnalysis, "SOURCE_MISSING", "MANIFEST_MISSING");
        ConfigureConflictView();
        ConfigureModView();
        var preview = ResolvePreviewPath(project);
        PreviewAvailable = preview is not null;
        PreviewSourceLabel = !string.IsNullOrWhiteSpace(project.PreviewImagePath) && System.IO.File.Exists(project.PreviewImagePath)
            ? "Image personnalisée"
            : "Preview PZASM générée automatiquement";
    }

    private void ConfigureConflictView()
    {
        var requestedFilter = Request.Query["conflictFilter"].FirstOrDefault()?.Trim().ToLowerInvariant();
        ConflictFilter = requestedFilter is "all" or "errors" or "warnings" or "resolved" ? requestedFilter : "action";

        var requestedCategory = Request.Query["conflictCategory"].FirstOrDefault()?.Trim();
        ConflictCategory = Enum.TryParse<ModConflictCategory>(requestedCategory, ignoreCase: true, out var parsedCategory)
            ? parsedCategory.ToString()
            : "all";

        var requestedType = Request.Query["conflictType"].FirstOrDefault()?.Trim();
        ConflictType = ConflictAnalysis.Issues
            .Select(issue => issue.EffectiveTypeLabel)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault(type => type.Equals(requestedType, StringComparison.CurrentCultureIgnoreCase)) ?? "all";

        IEnumerable<ModConflictIssue> query = ConflictAnalysis.Issues;
        query = ConflictFilter switch
        {
            "all" => query,
            "errors" => query.Where(issue => issue.Severity == ModConflictSeverity.Error && !issue.IsResolved),
            "warnings" => query.Where(issue => issue.Severity == ModConflictSeverity.Warning && !issue.IsResolved),
            "resolved" => query.Where(issue => issue.IsResolved),
            _ => query.Where(issue => !issue.IsResolved && issue.Severity != ModConflictSeverity.Information)
        };
        if (ConflictCategory != "all" && Enum.TryParse<ModConflictCategory>(ConflictCategory, out parsedCategory))
            query = query.Where(issue => issue.Category == parsedCategory);
        if (ConflictType != "all")
            query = query.Where(issue => issue.EffectiveTypeLabel.Equals(ConflictType, StringComparison.CurrentCultureIgnoreCase));

        var filtered = query.ToArray();
        FilteredConflictCount = filtered.Length;
        ConflictPageCount = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)ConflictPageSize));
        ConflictPage = int.TryParse(Request.Query["conflictPage"].FirstOrDefault(), out var requestedPage)
            ? Math.Clamp(requestedPage, 1, ConflictPageCount)
            : 1;
        VisibleConflictIssues = filtered.Skip((ConflictPage - 1) * ConflictPageSize).Take(ConflictPageSize).ToArray();
    }

    private static bool IsPathWithin(string candidatePath, string rootPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        return candidatePath.Equals(normalizedRoot, comparison)
            || candidatePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private void ConfigureModView()
    {
        ExpandedModId = Guid.TryParse(Request.Query["expandedMod"].FirstOrDefault(), out var expandedModId)
            ? expandedModId
            : null;
        ModQuery = Request.Query["modQuery"].FirstOrDefault()?.Trim() ?? string.Empty;
        var requestedFilter = Request.Query["modFilter"].FirstOrDefault()?.Trim().ToLowerInvariant();
        ModFilter = requestedFilter is "enabled" or "disabled" or "manual" or "rights" ? requestedFilter : "all";

        IEnumerable<PackageModReference> query = Project.Mods.OrderBy(mod => mod.Order);
        query = ModFilter switch
        {
            "enabled" => query.Where(mod => mod.Enabled),
            "disabled" => query.Where(mod => !mod.Enabled),
            "manual" => query.Where(mod => !mod.IncludeInGlobalUpdates),
            "rights" => query.Where(mod => mod.Permission.Status is PermissionStatus.Unknown or PermissionStatus.Denied),
            _ => query
        };
        if (!string.IsNullOrWhiteSpace(ModQuery))
        {
            query = query.Where(mod => mod.Name.Contains(ModQuery, StringComparison.CurrentCultureIgnoreCase)
                || mod.ModId.Contains(ModQuery, StringComparison.OrdinalIgnoreCase)
                || mod.Author.Contains(ModQuery, StringComparison.CurrentCultureIgnoreCase)
                || mod.WorkshopId.ToString().Contains(ModQuery, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query.ToArray();
        FilteredModCount = filtered.Length;
        ModPageCount = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)ModPageSize));
        ModPage = int.TryParse(Request.Query["modPage"].FirstOrDefault(), out var requestedPage)
            ? Math.Clamp(requestedPage, 1, ModPageCount)
            : 1;
        VisibleProjectMods = filtered.Skip((ModPage - 1) * ModPageSize).Take(ModPageSize).ToArray();
    }

    private IActionResult RedirectToMod(PackageProject project, Guid modReferenceId, string? modQuery, string? modFilter, int requestedPage)
    {
        var normalizedFilter = modFilter is "enabled" or "disabled" or "manual" or "rights" ? modFilter : "all";
        var query = modQuery?.Trim() ?? string.Empty;
        IEnumerable<PackageModReference> filtered = project.Mods.OrderBy(mod => mod.Order);
        filtered = normalizedFilter switch
        {
            "enabled" => filtered.Where(mod => mod.Enabled),
            "disabled" => filtered.Where(mod => !mod.Enabled),
            "manual" => filtered.Where(mod => !mod.IncludeInGlobalUpdates),
            "rights" => filtered.Where(mod => mod.Permission.Status is PermissionStatus.Unknown or PermissionStatus.Denied),
            _ => filtered
        };
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(mod => mod.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || mod.ModId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || mod.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || mod.WorkshopId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var orderedIds = filtered.Select(mod => mod.Id).ToArray();
        var visibleIndex = Array.IndexOf(orderedIds, modReferenceId);
        var page = visibleIndex >= 0 ? visibleIndex / ModPageSize + 1 : Math.Max(1, requestedPage);
        var url = Url.Page("/Projects/Edit", null, new
        {
            id = project.Id,
            tab = "mods",
            modQuery = query,
            modFilter = normalizedFilter,
            modPage = page,
            expandedMod = modReferenceId
        });
        return LocalRedirect($"{url}#mod-{modReferenceId:N}");
    }

    private string? ResolvePreviewPath(PackageProject project)
    {
        if (!string.IsNullOrWhiteSpace(project.PreviewImagePath) && System.IO.File.Exists(project.PreviewImagePath)) return project.PreviewImagePath;
        var buildRoot = paths.BuildRoot(project.Id);
        if (!Directory.Exists(buildRoot)) return null;
        return Directory.EnumerateFiles(buildRoot, "preview.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif");
    }

    private async Task<string> SavePreviewUploadAsync(Guid projectId, IFormFile upload, CancellationToken cancellationToken)
    {
        if (upload.Length > WorkshopPreviewFile.MaximumBytes)
            throw new InvalidDataException("La preview Workshop dépasse 1 Mio.");
        var assetRoot = paths.ProjectAssetsRoot(projectId);
        Directory.CreateDirectory(assetRoot);
        var temporary = Path.Combine(assetRoot, "preview.upload.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await upload.CopyToAsync(output, cancellationToken);
            var extension = WorkshopPreviewFile.Validate(temporary);
            var destination = Path.Combine(assetRoot, "preview" + extension);
            foreach (var candidate in Directory.EnumerateFiles(assetRoot, "preview.*", SearchOption.TopDirectoryOnly).Where(x => !x.Equals(temporary, StringComparison.OrdinalIgnoreCase)))
                System.IO.File.Delete(candidate);
            System.IO.File.Move(temporary, destination, true);
            return destination;
        }
        finally
        {
            if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
        }
    }

    private void ApplyForm(PackageProject project)
    {
        project.Name = Form.Name.Trim();
        project.Description = Form.Description?.Trim() ?? string.Empty;
        project.Mode = Form.Mode;
        project.TargetPzVersion = string.IsNullOrWhiteSpace(Form.TargetPzVersion) ? "42.20.2" : Form.TargetPzVersion.Trim();
        project.InjectConnectionNotice = Form.InjectConnectionNotice;
        project.InjectInGameControl = Form.InjectInGameControl;
        project.NoticeTitle = string.IsNullOrWhiteSpace(Form.NoticeTitle) ? "PZ Advanced Server Manager" : Form.NoticeTitle.Trim();
        if (project.PublishedWorkshopId != Form.PublishedWorkshopId)
        {
            project.PublishedWorkshopId = Form.PublishedWorkshopId;
            project.Publication = new WorkshopPublicationState();
        }
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
        project.Automation.PostPublishRestartDelayMinutes = Math.Clamp(Form.PostPublishRestartDelayMinutes, 5, 60);
        project.Automation.DailyTimes = (Form.DailyTimes ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void ApplyRecommendedOrder(PackageProject project, ModConflictAnalysis analysis, bool includeMaps)
    {
        var rank = analysis.RecommendedModOrder.Select((modId, index) => (modId, index)).ToDictionary(item => item.modId, item => item.index);
        var enabledCount = rank.Count;
        foreach (var mod in project.Mods)
            mod.Order = rank.TryGetValue(mod.Id, out var position) ? position : enabledCount++;
        project.Mods = project.Mods.OrderBy(mod => mod.Order).ToList();
        if (includeMaps) project.MapOrder = analysis.RecommendedMapOrder.ToList();
    }

    private static IReadOnlyList<PackageModReference> FindBatchTargets(PackageProject project, ModConflictAnalysis analysis, params string[] issueCodes)
    {
        var codes = issueCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = analysis.Issues
            .Where(issue => !issue.IsResolved && codes.Contains(issue.Code))
            .SelectMany(issue => issue.ModReferenceIds)
            .ToHashSet();
        return project.Mods
            .Where(mod => mod.Enabled && ids.Contains(mod.Id))
            .OrderBy(mod => mod.Order)
            .ThenBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void DisableMods(PackageProject project, ModConflictAnalysis analysis, IReadOnlyCollection<PackageModReference> targets)
    {
        var referenceIds = targets.Select(mod => mod.Id).ToHashSet();
        var modIds = targets.Select(mod => mod.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in targets) mod.Enabled = false;

        var affectedIssueKeys = analysis.Issues
            .Where(issue => issue.ModReferenceIds.Any(referenceIds.Contains))
            .Select(issue => issue.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in project.ConflictWinners
                     .Where(entry => affectedIssueKeys.Contains(entry.Key) || modIds.Contains(entry.Value))
                     .Select(entry => entry.Key)
                     .ToArray())
            project.ConflictWinners.Remove(key);
        project.AcknowledgedConflicts.RemoveAll(affectedIssueKeys.Contains);
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

    public sealed record ModListImportPreview(
        string SourceLabel,
        int ModIdCount,
        int WorkshopIdCount,
        IReadOnlyList<ModListImportCandidate> Candidates);

    public sealed record ModListImportCandidate(
        string Value,
        string Kind,
        string Identifier,
        string Title,
        string Detail,
        string Status,
        string Tone,
        bool Selectable,
        bool SelectedByDefault);

    public sealed class ProjectForm
    {
        [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public PackageMode Mode { get; set; }
        public string TargetPzVersion { get; set; } = "42.20.2";
        public bool InjectConnectionNotice { get; set; }
        public bool InjectInGameControl { get; set; }
        public string NoticeTitle { get; set; } = string.Empty;
        public ulong PublishedWorkshopId { get; set; }
        public WorkshopVisibility Visibility { get; set; }
        public string? PreviewImagePath { get; set; }
        public bool ClearPreviewImage { get; set; }
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
        [Range(5, 60)] public int PostPublishRestartDelayMinutes { get; set; } = 5;
        public string? DailyTimes { get; set; }

        public static ProjectForm From(PackageProject project) => new()
        {
            Name = project.Name,
            Description = project.Description,
            Mode = project.Mode,
            TargetPzVersion = project.TargetPzVersion,
            InjectConnectionNotice = project.InjectConnectionNotice,
            InjectInGameControl = project.InjectInGameControl,
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
            CoordinatedServerName = project.Automation.CoordinatedServerName,
            PostPublishRestartDelayMinutes = project.Automation.PostPublishRestartDelayMinutes
        };
    }
}
