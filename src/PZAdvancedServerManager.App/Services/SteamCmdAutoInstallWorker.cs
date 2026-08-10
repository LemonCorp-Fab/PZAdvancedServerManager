using PZAdvancedServerManager.Core.Publishing;

namespace PZAdvancedServerManager.App.Services;

public sealed class SteamCmdAutoInstallWorker(
    SteamCmdInstaller installer,
    IConfiguration configuration,
    ILogger<SteamCmdAutoInstallWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("PZASM_STEAMCMD_AUTO_INSTALL", false) || installer.GetStatus().Installed) return;

        try
        {
            logger.LogInformation("SteamCMD is not installed in the persistent data directory; automatic installation is starting.");
            var result = await installer.EnsureInstalledAsync(stoppingToken);
            if (result.Bootstrapped) logger.LogInformation("SteamCMD was installed and bootstrapped successfully.");
            else logger.LogError("SteamCMD was extracted but its bootstrap failed: {Output}", result.Output);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Automatic SteamCMD installation failed. It can be retried from the manager dashboard.");
        }
    }
}
