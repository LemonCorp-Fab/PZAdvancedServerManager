using PZAdvancedServerManager.Core.Packaging;

namespace PZAdvancedServerManager.App.Services;

public sealed class PackageAutomationWorker(
    PackageAutomationService automation,
    ILogger<PackageAutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PZAdvancedServerManager.Core.Domain.PzasmConstants.AutomationPollInterval);
        do
        {
            try
            {
                foreach (var result in await automation.RunDueAsync(DateTimeOffset.Now, stoppingToken))
                {
                    if (result.Success) logger.LogInformation("Automatisation terminée pour le pack {ProjectName}: {Message}", result.ProjectName, result.Message);
                    else logger.LogError("Automatisation échouée pour le pack {ProjectName}: {Message}", result.ProjectName, result.Message);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Erreur globale du planificateur PZASM"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

}
