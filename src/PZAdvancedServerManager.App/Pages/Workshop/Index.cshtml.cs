using System.Text;
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

    public async Task<IActionResult> OnPostAddWorkshopAsync(ulong[] selectedWorkshopIds, CancellationToken cancellationToken)
    {
        var contextResult = LoadContext();
        if (contextResult is not null) return contextResult;
        var ids = selectedWorkshopIds.Where(id => id != 0).Distinct().Take(50).ToArray();
        if (ids.Length == 0)
        {
            TempData["Error"] = "Sélectionnez au moins un item Workshop.";
            return RedirectBack();
        }

        try
        {
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

    public IActionResult OnPostAddLocal(string[] selectedMods)
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
            if (Project is not null)
            {
                var added = selected.Sum(mod => projects.AddWithDependencies(Project, mod, available));
                TempData["Message"] = $"{added} nouveau(x) Mod ID local(aux) et dépendance(s) figé(s) dans le pack.";
            }
            else if (Server is not null)
            {
                var expanded = ExpandDependencies(selected, available);
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
}
