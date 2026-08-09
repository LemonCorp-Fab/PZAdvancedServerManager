using System.Text.Json;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Packaging;

public sealed class PackageLifecycleService(
    ApplicationPaths paths,
    PackageProjectStore store,
    PackageSourceSnapshotService snapshots,
    PackageBuildService builder,
    SteamCmdService steamCmd,
    ServerProfileService servers)
{
    public PackageBuildResult Build(PackageProject project)
    {
        using var operationLock = Acquire(project.Id);
        return BuildCore(project);
    }

    public async Task<SteamCmdResult> RefreshSourcesAsync(PackageProject project, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        await using var operationLock = Acquire(project.Id);
        var targets = project.Mods.Where(x => x.Enabled && x.IncludeInGlobalUpdates).ToArray();
        return await RefreshSourcesCoreAsync(project, targets, cancellationToken, progress);
    }

    public async Task<SteamCmdResult> RefreshModAsync(PackageProject project, Guid modReferenceId, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        await using var operationLock = Acquire(project.Id);
        var target = project.Mods.FirstOrDefault(x => x.Id == modReferenceId)
            ?? throw new KeyNotFoundException("Mod introuvable dans ce projet.");
        return await RefreshSourcesCoreAsync(project, [target], cancellationToken, progress);
    }

    public async Task<PackageOperationResult> PublishAsync(
        PackageProject project,
        bool refreshSources,
        bool requireCoordinatedServer,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        await using var operationLock = Acquire(project.Id);
        if (requireCoordinatedServer && string.IsNullOrWhiteSpace(project.Automation.CoordinatedServerName))
            throw new InvalidOperationException("Publication refusée sans profil serveur coordonné.");

        var output = new List<string>();
        if (refreshSources)
        {
            var targets = project.Mods.Where(x => x.Enabled && x.IncludeInGlobalUpdates).ToArray();
            progress?.Report(new OperationProgress("refresh", $"Actualisation de {targets.Length} source(s) Workshop."));
            var refresh = await RefreshSourcesCoreAsync(project, targets, cancellationToken, progress);
            output.Add(refresh.CombinedOutput);
            if (!refresh.Success) throw new InvalidOperationException("Actualisation SteamCMD échouée : " + Tail(refresh.CombinedOutput));
        }

        progress?.Report(new OperationProgress("build", "Validation des snapshots et construction atomique du pack."));
        var build = BuildCore(project);
        var serverName = project.Automation.CoordinatedServerName;
        var serverWasRunning = false;
        var serverStopped = false;
        var serverRestarted = false;
        var restartViaRconAfterPublish = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(serverName))
            {
                serverWasRunning = await servers.IsOnlineAsync(serverName, cancellationToken);
                if (!serverWasRunning && await servers.IsRconServiceAsync(serverName, cancellationToken))
                    throw new InvalidOperationException("Un service RCON Project Zomboid répond, mais son authentification a échoué. Vérifiez l'hôte, le port et le mot de passe avant la publication coordonnée.");
                if (serverWasRunning)
                {
                    if (!servers.CanCoordinateRestart(serverName))
                        throw new InvalidOperationException("Ce profil distant ne peut pas redémarrer Project Zomboid. Configurez une relance automatique après RCON quit ou une commande SSH de démarrage.");
                    if (servers.CanStart(serverName))
                    {
                        progress?.Report(new OperationProgress("server", "Sauvegarde et arrêt du processus Project Zomboid par RCON avant publication."));
                        await servers.StopAsync(serverName, cancellationToken);
                        serverStopped = true;
                    }
                    else
                    {
                        restartViaRconAfterPublish = true;
                        progress?.Report(new OperationProgress("server", "Profil RCON-only détecté : le jeu restera actif pendant l’envoi puis recevra save/quit après publication."));
                    }
                }
            }

            progress?.Report(new OperationProgress("publish", "Connexion au compte éditeur et envoi vers Steam Workshop."));
            var workshopIdBeforePublish = project.PublishedWorkshopId;
            var publish = await steamCmd.PublishAsync(project, build, cancellationToken, progress);
            var newWorkshopIdAssigned = project.PublishedWorkshopId != workshopIdBeforePublish;
            if (newWorkshopIdAssigned)
            {
                store.Save(project);
                progress?.Report(new OperationProgress("workshopid", $"Steam a attribué le Workshop ID {project.PublishedWorkshopId}; il a été enregistré même si l’envoi du contenu devait ensuite échouer."));
            }
            output.Add(publish.CombinedOutput);
            if (publish.Interaction != SteamCmdInteraction.None) throw SteamCmdInteractionRequiredException.FromResult(publish);
            if (!publish.Success)
                throw new InvalidOperationException(newWorkshopIdAssigned
                    ? $"L’item Workshop {project.PublishedWorkshopId} a été créé et son ID a été enregistré, mais l’envoi du contenu a échoué. Relancez Publier après correction; le même item sera mis à jour. Détail SteamCMD : {Tail(publish.CombinedOutput)}"
                    : "Publication SteamCMD échouée : " + Tail(publish.CombinedOutput));

            store.Save(project);
            if (restartViaRconAfterPublish)
            {
                progress?.Report(new OperationProgress("server", "Publication confirmée : envoi de save puis quit par RCON. Le superviseur distant relancera le jeu."));
                await servers.RestartViaRconAsync(serverName, cancellationToken);
                serverRestarted = true;
            }
            progress?.Report(new OperationProgress("finalize", "Workshop ID enregistré et configuration serveur régénérée."));
            build = BuildCore(project); // Rebuilds server-config.txt with the ID created by the first publish operation.
        }
        finally
        {
            if (serverStopped)
            {
                await servers.StartAsync(serverName, CancellationToken.None);
                serverRestarted = true;
            }
        }

        return new PackageOperationResult(build, string.Join(Environment.NewLine, output.Where(x => !string.IsNullOrWhiteSpace(x))), true, serverWasRunning, serverRestarted);
    }

    private PackageBuildResult BuildCore(PackageProject project)
    {
        var stateBeforeBuild = JsonSerializer.Serialize(project);
        snapshots.EnsurePinned(project, verifyIntegrity: false);
        var build = builder.Build(project);
        if (!build.IsNoOp || !stateBeforeBuild.Equals(JsonSerializer.Serialize(project), StringComparison.Ordinal))
            store.Save(project);
        return build;
    }

    private async Task<SteamCmdResult> RefreshSourcesCoreAsync(PackageProject project, IReadOnlyCollection<PackageModReference> targets, CancellationToken cancellationToken, IProgress<OperationProgress>? progress = null)
    {
        if (targets.Count == 0)
            return new SteamCmdResult(0, "Aucun mod n’est configuré pour la mise à jour globale.", string.Empty);
        var refresh = await steamCmd.RefreshSourcesIncrementalAsync(project, targets, cancellationToken, progress);
        if (refresh.SteamCmd.Success)
        {
            var changedWorkshopIds = refresh.ChangedReferenceIds.ToHashSet();
            var localTargets = targets.Where(target => target.WorkshopId == 0).ToArray();
            var changedTargets = targets
                .Where(target => changedWorkshopIds.Contains(target.Id) || target.WorkshopId == 0)
                .DistinctBy(target => target.Id)
                .ToArray();
            progress?.Report(new OperationProgress(
                "snapshot",
                changedTargets.Length == 0
                    ? "Aucun contenu n'a changé; les snapshots et mod.info existants sont conservés."
                    : $"Mise à jour atomique de {changedTargets.Length} snapshot(s); les sources inchangées sont conservées."));
            foreach (var target in localTargets) RefreshMetadata(project, target);
            snapshots.Update(project, changedTargets);
            store.Save(project);
        }
        return refresh.SteamCmd;
    }

    private static void RefreshMetadata(PackageProject project, PackageModReference reference)
    {
        if (!Directory.Exists(reference.SourceModRoot)) return;
        var manifest = PzVersionSelector.SelectManifest(reference.SourceModRoot, project.TargetPzVersion, out var selected);
        if (string.IsNullOrWhiteSpace(manifest)) return;
        var info = ModInfoParser.Parse(manifest);
        var previousAuthor = reference.Author;
        reference.Name = string.IsNullOrWhiteSpace(info.Name) ? reference.Name : info.Name;
        reference.Author = string.IsNullOrWhiteSpace(info.Author) ? reference.Author : info.Author;
        reference.Version = info.Version;
        reference.SelectedVersionFolder = selected;
        reference.RequiredModIds = info.Required;
        if (!string.IsNullOrWhiteSpace(reference.Author) &&
            (string.IsNullOrWhiteSpace(reference.Permission.RightsHolder) ||
             reference.Permission.Status == PermissionStatus.Unknown && reference.Permission.RightsHolder.Equals(previousAuthor, StringComparison.OrdinalIgnoreCase)))
            reference.Permission.RightsHolder = reference.Author;
    }

    private FileStream Acquire(Guid projectId)
    {
        try
        {
            return new FileStream(paths.ProjectLockFile(projectId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Une autre opération PZASM est déjà en cours pour ce projet.", exception);
        }
    }

    private static string Tail(string value) => value.Length <= 3000 ? value : value[^3000..];
}
