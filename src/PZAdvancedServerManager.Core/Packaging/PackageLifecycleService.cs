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
        IProgress<OperationProgress>? progress = null,
        bool force = false)
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
        var plan = await steamCmd.PlanPublicationAsync(project, build, force, cancellationToken, progress);
        if (plan.IsNoOp)
        {
            output.Add(plan.Summary);
            return new PackageOperationResult(
                build,
                string.Join(Environment.NewLine, output.Where(x => !string.IsNullOrWhiteSpace(x))),
                true,
                false,
                true,
                plan.Mode.ToString(),
                false,
                false);
        }

        var serverName = project.Automation.CoordinatedServerName;
        var serverWasRunning = false;
        var serverRestarted = false;
        if (plan.RequiresServerRestart && !string.IsNullOrWhiteSpace(serverName))
        {
            serverWasRunning = await servers.IsOnlineAsync(serverName, cancellationToken);
            if (!serverWasRunning && await servers.IsRconServiceAsync(serverName, cancellationToken))
                throw new InvalidOperationException("Un service RCON Project Zomboid répond, mais son authentification a échoué. Vérifiez l'hôte, le port et le mot de passe avant la publication coordonnée.");
            if (serverWasRunning && !servers.CanCoordinateRestart(serverName))
                throw new InvalidOperationException("Ce profil distant ne peut pas redémarrer Project Zomboid. Configurez une relance automatique après RCON quit ou une commande SSH de démarrage.");
            if (serverWasRunning)
                progress?.Report(new OperationProgress("server-preflight", "Serveur coordonné détecté : il restera actif pendant toute la préparation et tout l'upload."));
        }

        progress?.Report(new OperationProgress("publish", "Connexion au compte éditeur et envoi incrémental vers Steam Workshop. Le serveur reste en ligne."));
        var workshopIdBeforePublish = project.PublishedWorkshopId;
        var publish = await steamCmd.PublishAsync(project, build, plan, cancellationToken, progress);
        var newWorkshopIdAssigned = project.PublishedWorkshopId != workshopIdBeforePublish;
        if (newWorkshopIdAssigned)
        {
            store.Save(project);
            progress?.Report(new OperationProgress("workshopid", $"Steam a attribué le Workshop ID {project.PublishedWorkshopId}; il a été enregistré immédiatement."));
        }
        output.Add(publish.SteamCmd.CombinedOutput);
        ThrowIfPublishFailed(publish.SteamCmd, project, newWorkshopIdAssigned);

        store.Save(project);
        var finalPlan = plan;
        if (newWorkshopIdAssigned)
        {
            progress?.Report(new OperationProgress("workshopid", "Reconstruction des petits manifestes injectés avec le nouvel ID, puis synchronisation différentielle finale."));
            build = BuildCore(project);
            finalPlan = await steamCmd.PlanPublicationAsync(project, build, false, cancellationToken, progress);
            if (!finalPlan.IsNoOp)
            {
                var finalPublish = await steamCmd.PublishAsync(project, build, finalPlan, cancellationToken, progress);
                output.Add(finalPublish.SteamCmd.CombinedOutput);
                ThrowIfPublishFailed(finalPublish.SteamCmd, project, false, finalSynchronization: true);
                store.Save(project);
            }
        }

        if (serverWasRunning && (plan.RequiresServerRestart || finalPlan.RequiresServerRestart))
        {
            var delayMinutes = Math.Clamp(project.Automation.PostPublishRestartDelayMinutes, 5, 60);
            await WaitBeforeServerRestartAsync(delayMinutes, cancellationToken, progress);
            try
            {
                serverRestarted = await RestartCoordinatedServerAsync(serverName, cancellationToken, progress);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"La publication Workshop est confirmée, mais le redémarrage coordonné du serveur a échoué : {exception.Message}", exception);
            }
        }

        progress?.Report(new OperationProgress("finalize", "État de publication enregistré; aucun téléchargement de contrôle du package n'a été effectué."));
        build = BuildCore(project);
        return new PackageOperationResult(
            build,
            string.Join(Environment.NewLine, output.Where(x => !string.IsNullOrWhiteSpace(x))),
            true,
            true,
            false,
            finalPlan.Mode.ToString(),
            serverWasRunning,
            serverRestarted);
    }

    private static void ThrowIfPublishFailed(SteamCmdResult result, PackageProject project, bool newWorkshopIdAssigned, bool finalSynchronization = false)
    {
        if (result.Interaction != SteamCmdInteraction.None) throw SteamCmdInteractionRequiredException.FromResult(result);
        if (result.Success) return;
        if (finalSynchronization)
            throw new InvalidOperationException($"L'item Workshop {project.PublishedWorkshopId} existe, mais la synchronisation finale du nouvel ID a échoué : {Tail(result.CombinedOutput)}");
        throw new InvalidOperationException(newWorkshopIdAssigned
            ? $"L'item Workshop {project.PublishedWorkshopId} a été créé et son ID a été enregistré, mais l'envoi du contenu a échoué. Relancez Publier après correction; le même item sera mis à jour. Détail SteamCMD : {Tail(result.CombinedOutput)}"
            : "Publication SteamCMD échouée : " + Tail(result.CombinedOutput));
    }

    private static async Task WaitBeforeServerRestartAsync(int delayMinutes, CancellationToken cancellationToken, IProgress<OperationProgress>? progress)
    {
        for (var remaining = delayMinutes; remaining > 0; remaining--)
        {
            progress?.Report(new OperationProgress("restart-delay", $"Publication confirmée. Le serveur reste actif encore {remaining} minute(s) avant save/quit et redémarrage.", delayMinutes - remaining, delayMinutes));
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        }
    }

    private async Task<bool> RestartCoordinatedServerAsync(string serverName, CancellationToken cancellationToken, IProgress<OperationProgress>? progress)
    {
        if (!await servers.IsOnlineAsync(serverName, cancellationToken))
        {
            progress?.Report(new OperationProgress("server", "Le serveur coordonné n'est plus en ligne; aucun processus n'a été arrêté."));
            return false;
        }
        if (!servers.CanStart(serverName))
        {
            progress?.Report(new OperationProgress("server", "Envoi de save puis quit par RCON; le superviseur distant assurera la relance."));
            await servers.RestartViaRconAsync(serverName, cancellationToken);
            return true;
        }

        var stopped = false;
        try
        {
            progress?.Report(new OperationProgress("server", "Envoi de save puis quit au processus Project Zomboid après le délai de propagation."));
            await servers.StopAsync(serverName, cancellationToken);
            stopped = true;
            progress?.Report(new OperationProgress("server", "Relance du processus Project Zomboid avec la version Workshop confirmée."));
            await servers.StartAsync(serverName, cancellationToken);
            return true;
        }
        catch
        {
            if (stopped)
            {
                try { await servers.StartAsync(serverName, CancellationToken.None); }
                catch { }
            }
            throw;
        }
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
            return new SteamCmdResult(0, "Aucun mod n'est configuré pour la mise à jour globale.", string.Empty);
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
