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

    public async Task<SteamCmdResult> RefreshSourcesAsync(PackageProject project, CancellationToken cancellationToken = default)
    {
        await using var operationLock = Acquire(project.Id);
        var targets = project.Mods.Where(x => x.Enabled && x.IncludeInGlobalUpdates).ToArray();
        return await RefreshSourcesCoreAsync(project, targets, cancellationToken);
    }

    public async Task<SteamCmdResult> RefreshModAsync(PackageProject project, Guid modReferenceId, CancellationToken cancellationToken = default)
    {
        await using var operationLock = Acquire(project.Id);
        var target = project.Mods.FirstOrDefault(x => x.Id == modReferenceId)
            ?? throw new KeyNotFoundException("Mod introuvable dans ce projet.");
        return await RefreshSourcesCoreAsync(project, [target], cancellationToken);
    }

    public async Task<PackageOperationResult> PublishAsync(
        PackageProject project,
        bool refreshSources,
        bool requireCoordinatedServer,
        CancellationToken cancellationToken = default)
    {
        await using var operationLock = Acquire(project.Id);
        if (requireCoordinatedServer && string.IsNullOrWhiteSpace(project.Automation.CoordinatedServerName))
            throw new InvalidOperationException("Publication refusée sans profil serveur coordonné.");

        var output = new List<string>();
        if (refreshSources)
        {
            var targets = project.Mods.Where(x => x.Enabled && x.IncludeInGlobalUpdates).ToArray();
            var refresh = await RefreshSourcesCoreAsync(project, targets, cancellationToken);
            output.Add(refresh.CombinedOutput);
            if (!refresh.Success) throw new InvalidOperationException("Actualisation SteamCMD échouée : " + Tail(refresh.CombinedOutput));
        }

        var build = BuildCore(project);
        var serverName = project.Automation.CoordinatedServerName;
        var serverWasRunning = false;
        var serverStopped = false;
        var serverRestarted = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(serverName))
            {
                serverWasRunning = await servers.IsOnlineAsync(serverName, cancellationToken);
                if (serverWasRunning)
                {
                    await servers.StopAsync(serverName, cancellationToken);
                    serverStopped = true;
                }
            }

            var publish = await steamCmd.PublishAsync(project, build, cancellationToken);
            output.Add(publish.CombinedOutput);
            if (!publish.Success) throw new InvalidOperationException("Publication SteamCMD échouée : " + Tail(publish.CombinedOutput));

            store.Save(project);
            build = BuildCore(project); // Rebuilds server-config.txt with the ID created by the first publish operation.
        }
        finally
        {
            if (serverStopped)
            {
                servers.Start(serverName);
                serverRestarted = true;
            }
        }

        return new PackageOperationResult(build, string.Join(Environment.NewLine, output.Where(x => !string.IsNullOrWhiteSpace(x))), true, serverWasRunning, serverRestarted);
    }

    private PackageBuildResult BuildCore(PackageProject project)
    {
        snapshots.EnsurePinned(project);
        store.Save(project);
        var build = builder.Build(project);
        store.Save(project);
        return build;
    }

    private async Task<SteamCmdResult> RefreshSourcesCoreAsync(PackageProject project, IReadOnlyCollection<PackageModReference> targets, CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return new SteamCmdResult(0, "Aucun mod n’est configuré pour la mise à jour globale.", string.Empty);
        var refresh = await steamCmd.RefreshSourcesAsync(project, targets, cancellationToken);
        if (refresh.Success)
        {
            foreach (var target in targets) RefreshMetadata(project, target);
            snapshots.Update(project, targets);
            store.Save(project);
        }
        return refresh;
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
