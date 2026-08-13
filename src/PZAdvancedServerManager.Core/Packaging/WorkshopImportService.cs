using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Packaging;

public sealed class WorkshopImportService(
    SteamCmdService steamCmd,
    PzDiscoveryService discovery,
    PzEnvironmentService environment,
    PackageProjectService projects,
    SteamWorkshopCachePruner workshopCache)
{
    public async Task<WorkshopImportResult> ImportAsync(
        PackageProject project,
        ulong workshopId,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        var download = await DownloadAsync(project, workshopId, cancellationToken, progress);
        var allKnown = environment.GetMods(project.TargetPzVersion, refresh: true)
            .Concat(download.Mods)
            .GroupBy(x => $"{x.WorkshopId}:{x.ModId}:{x.ModRoot}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).ToList();
        var count = 0;
        foreach (var mod in download.Mods) count += projects.AddWithDependencies(project, mod, allKnown);
        projects.SetWorkshopSourceToken(project, workshopId, download.SourceUpdateToken);
        workshopCache.RemoveItems([workshopId]);
        return new WorkshopImportResult(workshopId, count, download.Mods.Select(x => x.ModId).ToArray(), download.Output);
    }

    public async Task<WorkshopDownloadResult> DownloadAsync(
        PackageProject project,
        ulong workshopId,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        var download = await steamCmd.DownloadWorkshopItemAsync(project, workshopId, cancellationToken, progress);
        if (!download.SteamCmd.Success)
            throw new InvalidOperationException("Téléchargement SteamCMD échoué : " + Tail(download.SteamCmd.CombinedOutput));
        if (!Directory.Exists(download.ContentRoot))
            throw new DirectoryNotFoundException($"SteamCMD n'a pas créé le dossier attendu : {download.ContentRoot}. Sortie SteamCMD : {Tail(download.SteamCmd.CombinedOutput)}");

        var imported = discovery.DiscoverWorkshopItem(download.ContentRoot, workshopId, project.TargetPzVersion);
        if (imported.Count == 0)
            throw new InvalidOperationException("L'item téléchargé ne contient aucun mod.info compatible avec la version PZ cible.");
        return new WorkshopDownloadResult(workshopId, imported, download.SteamCmd.CombinedOutput, download.SourceUpdateToken);
    }

    private static string Tail(string value) => value.Length <= 2000 ? value : value[^2000..];
}

public sealed record WorkshopImportResult(ulong WorkshopId, int AddedMods, IReadOnlyList<string> DiscoveredModIds, string Output);
public sealed record WorkshopDownloadResult(ulong WorkshopId, IReadOnlyList<DiscoveredMod> Mods, string Output, string SourceUpdateToken);
