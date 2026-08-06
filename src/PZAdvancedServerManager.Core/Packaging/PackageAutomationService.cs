using System.Globalization;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.Core.Packaging;

public sealed class PackageAutomationService(ApplicationPaths paths, PackageProjectStore store, PackageLifecycleService lifecycle)
{
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    public IReadOnlyList<PackageProject> GetDueProjects(DateTimeOffset now)
    {
        return store.GetAll().Where(project => project.Automation.Enabled && IsDue(project, now)).ToList();
    }

    public async Task<IReadOnlyList<AutomationRunResult>> RunDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var schedulerLock = TryAcquireSchedulerLock();
        if (schedulerLock is null) return [];
        var results = new List<AutomationRunResult>();
        foreach (var project in GetDueProjects(now))
            results.Add(await RunProjectAsync(project, cancellationToken));
        return results;
    }

    public async Task<AutomationRunResult> RunProjectAsync(PackageProject project, CancellationToken cancellationToken = default)
    {
        await _executionLock.WaitAsync(cancellationToken);
        try
        {
            project.Automation.LastAttemptAt = DateTimeOffset.Now;
            store.Save(project);
            try
            {
                string message;
                if (project.Automation.PublishAfterBuild)
                {
                    var result = await lifecycle.PublishAsync(project, project.Automation.RefreshWorkshopSourcesBeforeBuild, requireCoordinatedServer: true, cancellationToken);
                    message = string.IsNullOrWhiteSpace(result.Output) ? "Publication planifiée terminée." : Tail(result.Output);
                }
                else
                {
                    if (project.Automation.RefreshWorkshopSourcesBeforeBuild)
                    {
                        var refresh = await lifecycle.RefreshSourcesAsync(project, cancellationToken);
                        if (!refresh.Success) throw new InvalidOperationException("Actualisation SteamCMD échouée : " + Tail(refresh.CombinedOutput));
                    }
                    var build = lifecycle.Build(project);
                    message = $"Build planifié terminé : {build.BuildRoot}";
                }
                project.Automation.LastSuccessAt = DateTimeOffset.Now;
                project.Automation.LastResult = message;
                store.Save(project);
                return new AutomationRunResult(project.Id, project.Name, true, message);
            }
            catch (Exception exception)
            {
                project.Automation.LastResult = exception.Message;
                store.Save(project);
                return new AutomationRunResult(project.Id, project.Name, false, exception.Message);
            }
        }
        finally
        {
            _executionLock.Release();
        }
    }

    private static bool IsDue(PackageProject project, DateTimeOffset now)
    {
        foreach (var value in project.Automation.DailyTimes)
        {
            if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            var dueAt = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hour, time.Minute, 0, now.Offset);
            if (now >= dueAt && now - dueAt < TimeSpan.FromMinutes(2) && (project.Automation.LastAttemptAt is null || project.Automation.LastAttemptAt < dueAt))
                return true;
        }
        return false;
    }

    private FileStream? TryAcquireSchedulerLock()
    {
        try { return new FileStream(paths.AutomationLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private static string Tail(string value) => value.Length <= 3000 ? value : value[^3000..];
}
