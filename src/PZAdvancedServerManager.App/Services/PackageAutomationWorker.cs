using System.Globalization;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;

namespace PZAdvancedServerManager.App.Services;

public sealed class PackageAutomationWorker(
    PackageProjectStore store,
    PackageValidator validator,
    PackageBuildService builder,
    SteamCmdService steamCmd,
    ILogger<PackageAutomationWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _executionLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await CheckSchedules(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Erreur globale du planificateur PZASM"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckSchedules(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        foreach (var project in store.GetAll().Where(x => x.Automation.Enabled))
        {
            var dueAt = project.Automation.DailyTimes
                .Select(x => TimeOnly.TryParseExact(x, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : (TimeOnly?)null)
                .Where(x => x.HasValue)
                .Select(x => new DateTimeOffset(now.Year, now.Month, now.Day, x!.Value.Hour, x.Value.Minute, 0, now.Offset))
                .FirstOrDefault(x => now >= x && now - x < TimeSpan.FromMinutes(2) && (project.Automation.LastAttemptAt is null || project.Automation.LastAttemptAt < x));
            if (dueAt == default) continue;
            await ExecuteProject(project, cancellationToken);
        }
    }

    private async Task ExecuteProject(PackageProject project, CancellationToken cancellationToken)
    {
        if (!await _executionLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            project.Automation.LastAttemptAt = DateTimeOffset.Now;
            store.Save(project);
            var validation = validator.Validate(project);
            if (!validation.CanPublish && project.Automation.PublishAfterBuild)
                throw new InvalidOperationException("Automatisation bloquée : les droits ou paramètres du pack ne permettent pas la publication.");

            if (project.Automation.RefreshWorkshopSourcesBeforeBuild)
            {
                var refresh = await steamCmd.RefreshSourcesAsync(project, cancellationToken);
                if (!refresh.Success) throw new InvalidOperationException("Actualisation SteamCMD échouée : " + Tail(refresh.CombinedOutput));
            }

            var build = builder.Build(project);
            if (project.Automation.PublishAfterBuild)
            {
                var publish = await steamCmd.PublishAsync(project, build, cancellationToken);
                if (!publish.Success) throw new InvalidOperationException("Publication SteamCMD échouée : " + Tail(publish.CombinedOutput));
                project.Automation.LastResult = Tail(publish.CombinedOutput);
            }
            else project.Automation.LastResult = $"Build planifié terminé : {build.BuildRoot}";
            project.Automation.LastSuccessAt = DateTimeOffset.Now;
            store.Save(project);
            logger.LogInformation("Automatisation terminée pour le pack {ProjectName}", project.Name);
        }
        catch (Exception exception)
        {
            project.Automation.LastResult = exception.Message;
            store.Save(project);
            logger.LogError(exception, "Automatisation échouée pour le pack {ProjectName}", project.Name);
        }
        finally { _executionLock.Release(); }
    }

    private static string Tail(string value) => value.Length <= 3000 ? value : value[^3000..];
}
