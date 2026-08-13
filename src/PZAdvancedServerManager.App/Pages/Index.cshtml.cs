using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Threading.Channels;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages;

public class IndexModel(PackageProjectStore store, PzEnvironmentService environment, ApplicationPaths paths, SteamCmdInstaller steamCmdInstaller) : PageModel
{
    public IReadOnlyList<PackageProject> Projects { get; private set; } = [];
    public PzInstallation Installation { get; private set; } = new();
    public string DataRoot => paths.DataRoot;
    public SteamCmdStatus SteamCmdStatus { get; private set; } = new(false, string.Empty, string.Empty, null, 0);

    public void OnGet()
    {
        Projects = store.GetAll();
        Installation = environment.Installation;
        SteamCmdStatus = steamCmdInstaller.GetStatus();
    }

    public IActionResult OnPostRefreshDiscovery()
    {
        environment.Invalidate();
        TempData["Message"] = "Détection locale actualisée.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostInstallSteamCmdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await steamCmdInstaller.InstallAsync(cancellationToken);
            environment.Invalidate();
            TempData[result.Bootstrapped ? "Message" : "Error"] = result.Bootstrapped
                ? $"SteamCMD installé et prêt : {result.ExecutablePath}"
                : $"SteamCMD extrait, mais son initialisation a échoué : {result.Output}";
        }
        catch (Exception exception) { TempData["Error"] = exception.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostInstallSteamCmdStreamAsync(CancellationToken cancellationToken)
    {
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Append("X-Accel-Buffering", "no");
        var channel = Channel.CreateUnbounded<OperationProgress>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var progress = new CallbackProgress<OperationProgress>(value => channel.Writer.TryWrite(value));
        var installTask = steamCmdInstaller.InstallAsync(cancellationToken, progress);
        _ = installTask.ContinueWith(_ => channel.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
                await WriteProgressAsync(new { type = "progress", phase = update.Phase, message = update.Message, current = update.Current, total = update.Total }, cancellationToken);
            var result = await installTask;
            if (!result.Bootstrapped)
                throw new InvalidOperationException("SteamCMD a été extrait, mais son initialisation a échoué : " + Tail(result.Output));
            environment.Invalidate();
            await WriteProgressAsync(new { type = "done", message = "SteamCMD portable téléchargé, initialisé et prêt", redirectUrl = Url.Page("/Index")! }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await WriteProgressAsync(new { type = "error", message = exception.Message }, CancellationToken.None);
        }
        return new EmptyResult();
    }

    private async Task WriteProgressAsync(object value, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(value) + "\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static string Tail(string value) => value.Length <= 1200 ? value : value[^1200..];

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
