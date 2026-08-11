using PZAdvancedServerManager.Core.Transfer;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.App.Services;

public sealed class TransferCleanupWorker(TransferWorkspaceCleaner cleaner, StorageMaintenanceService storage, ILogger<TransferCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = cleaner.CleanupStale(TimeSpan.FromHours(6));
                if (result.Directories > 0 || result.Files > 0)
                    logger.LogInformation("Removed {Directories} stale transfer workspaces and {Files} files, reclaiming {Bytes} bytes.", result.Directories, result.Files, result.Bytes);
                var storageResult = storage.Run(DateTime.UtcNow);
                if (storageResult.Directories > 0 || storageResult.Files > 0)
                    logger.LogInformation("Removed or compacted {Directories} stale storage directories and {Files} files, reclaiming {Bytes} bytes.", storageResult.Directories, storageResult.Files, storageResult.Bytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Stale transfer cleanup could not finish and will be retried.");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
