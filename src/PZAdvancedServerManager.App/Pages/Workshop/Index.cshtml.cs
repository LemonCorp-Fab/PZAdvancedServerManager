using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages.Workshop;

public class IndexModel(
    WorkshopCatalogService catalog,
    PackageProjectStore projectStore,
    PackageProjectService projects,
    WorkshopImportService workshopImport,
    PzEnvironmentService environment,
    PzDiscoveryService discovery,
    SteamCmdService steamCmd,
    SteamCmdInstaller steamCmdInstaller,
    ServerProfileService servers) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public string? ServerName { get; set; }
    [BindProperty(SupportsGet = true)] public string Source { get; set; } = "workshop";
    [BindProperty(SupportsGet = true)] public string Q { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string Sort { get; set; } = "trend";
    [BindProperty(SupportsGet = true)] public string Tag { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public PackageProject? Project { get; private set; }
    public ServerConfigEntry? Server { get; private set; }
    public WorkshopCatalogPage Catalog { get; private set; } = new([], 1, false, false, new());
    public IReadOnlyList<DiscoveredMod> LocalMods { get; private set; } = [];
    public int LocalTotal { get; private set; }
    public bool LocalHasPrevious => PageNumber > 1;
    public bool LocalHasNext => PageNumber * 60 < LocalTotal;
    public IReadOnlySet<ulong> IncludedWorkshopIds { get; private set; } = new HashSet<ulong>();
    public IReadOnlySet<string> IncludedModIds { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public SteamCmdStatus SteamCmdStatus { get; private set; } = new(false, string.Empty, string.Empty, null, 0);
    public string TargetVersion => Project?.TargetPzVersion ?? PzasmConstants.DefaultTargetVersion;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var contextResult = LoadContext();
        if (contextResult is not null) return contextResult;
        SteamCmdStatus = steamCmdInstaller.GetStatus();
        if (string.Equals(Source, "local", StringComparison.OrdinalIgnoreCase))
        {
            LocalMods = FilterLocalMods(environment.GetMods(TargetVersion));
        }
        else
        {
            Source = "workshop";
            try
            {
                Catalog = await catalog.SearchAsync(new WorkshopCatalogQuery(Q, Sort, PageNumber, Tag), cancellationToken);
            }
            catch (Exception exception)
            {
                TempData["Error"] = "Le catalogue Steam est temporairement indisponible : " + exception.Message;
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostWorkshopDependencyPlanAsync(ulong[] selectedWorkshopIds, CancellationToken cancellationToken)
    {
        var contextResult = LoadContext();
        if (contextResult is not null) return contextResult;
        var dependencies = await GetMissingWorkshopDependenciesAsync(selectedWorkshopIds, cancellationToken);
        return new JsonResult(new
        {
            dependencies = dependencies.Select(item => new { id = item.WorkshopId.ToString(), name = item.Title, source = $"Workshop {item.WorkshopId}" }),
            unresolved = Array.Empty<string>()
        });
    }

    public IActionResult OnPostLocalDependencyPlan(string[] selectedMods)
    {
        var contextResult = LoadContext();
        if (contextResult is not null) return contextResult;
        var available = environment.GetMods(TargetVersion);
        var keys = selectedMods.ToHashSet(StringComparer.Ordinal);
        var selected = available.Where(mod => keys.Contains(SelectionKey(mod))).ToArray();
        var placeholder = Project ?? new PackageProject { Mods = IncludedModIds.Select(id => new PackageModReference { ModId = id }).ToList() };
        var plan = PackageProjectComposer.PlanDependencies(placeholder, selected, available);
        return new JsonResult(new
        {
            dependencies = plan.AvailableDependencies.Select(mod => new { id = mod.ModId, name = mod.Name, source = mod.WorkshopId == 0 ? "Source locale" : $"Workshop {mod.WorkshopId}" }),
            unresolved = plan.UnresolvedModIds
        });
    }

    public async Task<IActionResult> OnPostAddWorkshopAsync(
        ulong[] selectedWorkshopIds,
        bool includeDependencies,
        bool dependencyChoiceAcknowledged,
        CancellationToken cancellationToken)
    {
        var contextResult = LoadContext();
        if (contextResult is not null) return contextResult;
        var ids = selectedWorkshopIds.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            TempData["Error"] = "Sélectionnez au moins un item Workshop.";
            return RedirectBack();
        }

        try
        {
            var dependencies = dependencyChoiceAcknowledged && !includeDependencies
                ? []
                : await GetMissingWorkshopDependenciesAsync(ids, cancellationToken);
            if (dependencies.Count > 0 && !dependencyChoiceAcknowledged)
                throw new InvalidOperationException("Confirmez le choix des dépendances dans le dialogue du manager.");
            if (includeDependencies) ids = dependencies.Select(item => item.WorkshopId).Concat(ids).Distinct().ToArray();
            if (Project is not null)
            {
                EnsureProjectSteamCmd(Project);
                var importedItems = 0;
                var importedMods = 0;
                foreach (var id in ids)
                {
                    var result = await workshopImport.ImportAsync(Project, id, cancellationToken);
                    importedItems++;
                    importedMods += result.AddedMods;
                }
                TempData["Message"] = $"{importedItems} item(s) Workshop téléchargé(s), {importedMods} nouveau(x) Mod ID figé(s) dans le pack.";
            }
            else if (Server is not null)
            {
                var status = steamCmdInstaller.GetStatus();
                if (!status.Installed) throw new FileNotFoundException("Installez SteamCMD depuis le tableau de bord avant de télécharger des mods serveur.", status.ExecutablePath);
                var downloadSettings = new PackageProject
                {
                    Name = "Server content import",
                    TargetPzVersion = PzasmConstants.DefaultTargetVersion,
                    Automation = { SteamCmdPath = status.ExecutablePath, AnonymousWorkshopDownloads = true }
                };
                var discovered = new List<DiscoveredMod>();
                foreach (var id in ids)
                {
                    var download = await steamCmd.DownloadWorkshopItemAsync(downloadSettings, id, cancellationToken);
                    if (!download.SteamCmd.Success) throw new InvalidOperationException($"Téléchargement de l’item {id} échoué : {Tail(download.SteamCmd.CombinedOutput)}");
                    discovered.AddRange(discovery.DiscoverWorkshopItem(download.ContentRoot, id, TargetVersion));
                }
                if (discovered.Count == 0) throw new InvalidOperationException("Les items téléchargés ne contiennent aucun mod.info compatible.");
                environment.Invalidate();
                var expanded = ExpandDependencies(discovered, environment.GetMods(TargetVersion, refresh: true).Concat(discovered));
                var result = servers.AddContent(Server.Name, expanded.Select(mod => mod.WorkshopId).Concat(ids), expanded.Select(mod => mod.ModId));
                TempData["Message"] = $"Configuration serveur mise à jour : {result.AddedWorkshopItems} Workshop ID et {result.AddedMods} Mod ID ajoutés. Sauvegarde : {DisplayBackup(result.BackupPath)}";
            }
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectBack();
    }

    public async Task<IActionResult> OnPostImportWorkshopStreamAsync(
        ulong[] selectedWorkshopIds,
        bool includeDependencies,
        bool dependencyChoiceAcknowledged,
        CancellationToken cancellationToken)
    {
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Append("X-Accel-Buffering", "no");
        var ids = selectedWorkshopIds.Where(id => id != 0).Distinct().ToArray();
        var contextResult = LoadContext();
        if (contextResult is not null || ids.Length == 0)
        {
            await WriteProgressAsync(new { type = "error", message = ids.Length == 0 ? "Sélection vide." : "Destination introuvable." }, cancellationToken);
            return new EmptyResult();
        }

        try
        {
            var dependencies = dependencyChoiceAcknowledged && !includeDependencies
                ? []
                : await GetMissingWorkshopDependenciesAsync(ids, cancellationToken);
            if (dependencies.Count > 0 && !dependencyChoiceAcknowledged)
                throw new InvalidOperationException("Confirmez le choix des dépendances dans le dialogue du manager.");
            if (includeDependencies) ids = dependencies.Select(item => item.WorkshopId).Concat(ids).Distinct().ToArray();
            var addedMods = 0;
            if (Project is not null)
            {
                EnsureProjectSteamCmd(Project);
                for (var index = 0; index < ids.Length; index++)
                {
                    var id = ids[index];
                    await WriteProgressAsync(new { type = "progress", phase = "download", index, total = ids.Length, workshopId = id, message = "Téléchargement SteamCMD et vérification des fichiers…" }, cancellationToken);
                    var result = await workshopImport.ImportAsync(Project, id, cancellationToken);
                    addedMods += result.AddedMods;
                    await WriteProgressAsync(new { type = "progress", phase = "complete", index, total = ids.Length, workshopId = id, message = $"Snapshot figé · {result.AddedMods} nouveau(x) Mod ID" }, cancellationToken);
                }
                TempData["Message"] = $"{ids.Length} item(s) Workshop téléchargé(s), {addedMods} nouveau(x) Mod ID figé(s) dans le pack.";
            }
            else if (Server is not null)
            {
                var status = steamCmdInstaller.GetStatus();
                if (!status.Installed) throw new FileNotFoundException("Installez SteamCMD depuis le tableau de bord avant de télécharger des mods serveur.", status.ExecutablePath);
                var downloadSettings = new PackageProject
                {
                    Name = "Server content import",
                    TargetPzVersion = PzasmConstants.DefaultTargetVersion,
                    Automation = { SteamCmdPath = status.ExecutablePath, AnonymousWorkshopDownloads = true }
                };
                var discovered = new List<DiscoveredMod>();
                for (var index = 0; index < ids.Length; index++)
                {
                    var id = ids[index];
                    await WriteProgressAsync(new { type = "progress", phase = "download", index, total = ids.Length, workshopId = id, message = "Téléchargement SteamCMD…" }, cancellationToken);
                    var download = await steamCmd.DownloadWorkshopItemAsync(downloadSettings, id, cancellationToken);
                    if (!download.SteamCmd.Success) throw new InvalidOperationException($"Téléchargement de l’item {id} échoué : {Tail(download.SteamCmd.CombinedOutput)}");
                    await WriteProgressAsync(new { type = "progress", phase = "inspect", index, total = ids.Length, workshopId = id, message = "Lecture des mod.info, versions et dépendances…" }, cancellationToken);
                    var itemMods = discovery.DiscoverWorkshopItem(download.ContentRoot, id, TargetVersion);
                    discovered.AddRange(itemMods);
                    await WriteProgressAsync(new { type = "progress", phase = "complete", index, total = ids.Length, workshopId = id, message = $"{itemMods.Count} Mod ID compatible(s) détecté(s)" }, cancellationToken);
                }
                if (discovered.Count == 0) throw new InvalidOperationException("Les items téléchargés ne contiennent aucun mod.info compatible.");
                await WriteProgressAsync(new { type = "finalizing", message = "Résolution globale des dépendances et sauvegarde du profil serveur…" }, cancellationToken);
                environment.Invalidate();
                var expanded = ExpandDependencies(discovered, environment.GetMods(TargetVersion, refresh: true).Concat(discovered));
                var result = servers.AddContent(Server.Name, expanded.Select(mod => mod.WorkshopId).Concat(ids), expanded.Select(mod => mod.ModId));
                addedMods = result.AddedMods;
                TempData["Message"] = $"Configuration serveur mise à jour : {result.AddedWorkshopItems} Workshop ID et {result.AddedMods} Mod ID ajoutés. Sauvegarde : {DisplayBackup(result.BackupPath)}";
            }

            var redirectUrl = Project is not null
                ? Url.Page("/Projects/Edit", null, new { id = Project.Id, tab = "mods" })
                : Url.Page("/Server/Index", null, new { name = Server!.Name, tab = "content" });
            await WriteProgressAsync(new { type = "done", message = $"Import terminé · {addedMods} nouveau(x) Mod ID", redirectUrl }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await WriteProgressAsync(new { type = "error", message = exception.Message }, CancellationToken.None);
        }
        return new EmptyResult();
    }

    public IActionResult OnPostAddLocal(
        string[] selectedMods,
        bool includeDependencies,
        bool dependencyChoiceAcknowledged)
    {
        var contextResult = LoadContext();
        if (contextResult is not null) return contextResult;
        var available = environment.GetMods(TargetVersion);
        var keys = selectedMods.ToHashSet(StringComparer.Ordinal);
        var selected = available.Where(mod => keys.Contains(SelectionKey(mod))).ToArray();
        if (selected.Length == 0)
        {
            TempData["Error"] = "Sélectionnez au moins un mod installé.";
            return RedirectBack();
        }

        try
        {
            var placeholder = Project ?? new PackageProject { Mods = IncludedModIds.Select(id => new PackageModReference { ModId = id }).ToList() };
            var plan = PackageProjectComposer.PlanDependencies(placeholder, selected, available);
            if ((plan.AvailableDependencies.Count > 0 || plan.UnresolvedModIds.Count > 0) && !dependencyChoiceAcknowledged)
                throw new InvalidOperationException("Confirmez le choix des dépendances dans le dialogue du manager.");
            if (Project is not null)
            {
                var added = selected.Sum(mod => projects.AddWithDependencies(Project, mod, available, includeDependencies));
                TempData["Message"] = $"{added} nouveau(x) Mod ID local(aux) et dépendance(s) figé(s) dans le pack.";
            }
            else if (Server is not null)
            {
                var expanded = includeDependencies ? ExpandDependencies(selected, available) : selected;
                var result = servers.AddContent(Server.Name, expanded.Select(mod => mod.WorkshopId), expanded.Select(mod => mod.ModId));
                TempData["Message"] = $"Configuration serveur mise à jour : {result.AddedWorkshopItems} Workshop ID et {result.AddedMods} Mod ID ajoutés. Sauvegarde : {DisplayBackup(result.BackupPath)}";
            }
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectBack();
    }

    public static string SelectionKey(DiscoveredMod mod) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mod.WorkshopId}|{mod.ModId}|{mod.ModRoot}"));

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} Gio",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} Mio",
        >= 1024 => $"{bytes / 1024d:0.0} Kio",
        _ => $"{bytes} o"
    };

    public static string DisplayVersion(DiscoveredMod mod) => !string.IsNullOrWhiteSpace(mod.Version)
        ? mod.Version
        : !string.IsNullOrWhiteSpace(mod.SelectedVersionFolder)
            ? $"PZ {mod.SelectedVersionFolder}"
            : "non déclarée";

    private IActionResult? LoadContext()
    {
        if (ProjectId is not null && !string.IsNullOrWhiteSpace(ServerName)) return BadRequest("Choisissez soit un pack, soit un serveur.");
        if (ProjectId is { } projectId)
        {
            Project = projectStore.Get(projectId);
            if (Project is null) return NotFound("Pack introuvable.");
            IncludedWorkshopIds = Project.Mods.Where(mod => mod.WorkshopId != 0).Select(mod => mod.WorkshopId).ToHashSet();
            IncludedModIds = Project.Mods.Select(mod => mod.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else if (!string.IsNullOrWhiteSpace(ServerName))
        {
            try
            {
                Server = servers.Get(ServerName);
                var summary = servers.ReadSummary(Server.Name);
                IncludedWorkshopIds = summary.WorkshopItems.Select(value => ulong.TryParse(value, out var id) ? id : 0).Where(id => id != 0).ToHashSet();
                IncludedModIds = summary.Mods.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException or FileNotFoundException)
            {
                return NotFound("Profil serveur introuvable.");
            }
        }
        return null;
    }

    private IReadOnlyList<DiscoveredMod> FilterLocalMods(IReadOnlyList<DiscoveredMod> mods)
    {
        var query = (Q ?? string.Empty).Trim();
        var filtered = mods
            .Where(mod => query.Length == 0 || new[] { mod.Name, mod.ModId, mod.Author, mod.Description, mod.WorkshopId.ToString() }.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(mod => $"{mod.WorkshopId}:{mod.ModId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(mod => mod.SourceUpdatedAt).First())
            .OrderBy(mod => IncludedModIds.Contains(mod.ModId))
            .ThenBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        LocalTotal = filtered.Length;
        PageNumber = Math.Clamp(PageNumber, 1, Math.Max(1, (int)Math.Ceiling(LocalTotal / 60d)));
        return filtered.Skip((PageNumber - 1) * 60).Take(60).ToArray();
    }

    private async Task<IReadOnlyList<WorkshopRequiredItem>> GetMissingWorkshopDependenciesAsync(
        IEnumerable<ulong> workshopIds,
        CancellationToken cancellationToken)
    {
        var requested = workshopIds.Where(id => id != 0).Distinct().ToHashSet();
        if (requested.Count == 0) return [];
        var results = await Task.WhenAll(requested.Select(id => catalog.GetRequiredItemsAsync(id, cancellationToken)));
        return results
            .SelectMany(items => items)
            .Where(item => !requested.Contains(item.WorkshopId) && !IncludedWorkshopIds.Contains(item.WorkshopId))
            .DistinctBy(item => item.WorkshopId)
            .ToArray();
    }

    private void EnsureProjectSteamCmd(PackageProject project)
    {
        if (!string.IsNullOrWhiteSpace(project.Automation.SteamCmdPath) && System.IO.File.Exists(project.Automation.SteamCmdPath)) return;
        var status = steamCmdInstaller.GetStatus();
        if (!status.Installed) throw new FileNotFoundException("Installez SteamCMD depuis le tableau de bord avant l’import Workshop.", status.ExecutablePath);
        project.Automation.SteamCmdPath = status.ExecutablePath;
        project.Automation.AnonymousWorkshopDownloads = true;
        projectStore.Save(project);
    }

    private IReadOnlyList<DiscoveredMod> ExpandDependencies(IEnumerable<DiscoveredMod> selected, IEnumerable<DiscoveredMod> available)
    {
        var all = available.GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, DiscoveredMod>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<DiscoveredMod>(selected);
        while (queue.Count > 0)
        {
            var mod = queue.Dequeue();
            if (!result.TryAdd(mod.ModId, mod)) continue;
            foreach (var dependency in mod.RequiredModIds)
                if (all.TryGetValue(dependency, out var found)) queue.Enqueue(found);
        }
        return result.Values.ToArray();
    }

    private RedirectToPageResult RedirectBack() => Project is not null
        ? RedirectToPage("/Projects/Edit", new { id = Project.Id, tab = "mods" })
        : Server is not null
            ? RedirectToPage("/Server/Index", new { name = Server.Name, tab = "content" })
            : RedirectToPage(new { ProjectId, ServerName, Source, Q, Sort, Tag, PageNumber });

    private static string Tail(string value) => value.Length <= 1200 ? value : value[^1200..];
    private static string DisplayBackup(string value) => string.IsNullOrWhiteSpace(value) ? "aucun changement nécessaire" : value;

    private async Task WriteProgressAsync(object value, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(value) + "\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
