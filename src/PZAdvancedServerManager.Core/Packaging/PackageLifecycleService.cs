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
        return await RefreshSourcesCoreAsync(project, cancellationToken);
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
            var refresh = await RefreshSourcesCoreAsync(project, cancellationToken);
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

    private async Task<SteamCmdResult> RefreshSourcesCoreAsync(PackageProject project, CancellationToken cancellationToken)
    {
        var refresh = await steamCmd.RefreshSourcesAsync(project, cancellationToken);
        if (refresh.Success)
        {
            snapshots.UpdateAll(project);
            store.Save(project);
        }
        return refresh;
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
