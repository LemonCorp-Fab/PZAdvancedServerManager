using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Threading.Channels;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Publishing;
using PZAdvancedServerManager.Core.Pz;
using PZAdvancedServerManager.Core.Transfer;

namespace PZAdvancedServerManager.App.Pages;

[RequestSizeLimit(PackTransferService.MaximumUniqueArchiveBytes + 1024L * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = PackTransferService.MaximumUniqueArchiveBytes + 1024L * 1024 * 1024)]
public class IndexModel(PackageProjectStore store, PackageProjectService projects, PzEnvironmentService environment, ApplicationPaths paths, SteamCmdInstaller steamCmdInstaller, PackTransferService transfers) : PageModel
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

    public IActionResult OnPostCreate(string name)
    {
        var project = projects.Create(name);
        return RedirectToPage("/Projects/Edit", new { id = project.Id });
    }

    public IActionResult OnPostDuplicate(Guid id)
    {
        var project = projects.Duplicate(id);
        TempData["Message"] = $"Pack « {project.Name} » créé avec un nouvel identifiant et sans Workshop ID.";
        return RedirectToPage("/Projects/Edit", new { id = project.Id });
    }

    public IActionResult OnPostDelete(Guid id)
    {
        projects.Delete(id);
        TempData["Message"] = "Projet, snapshots et builds PZASM supprimés. Les mods d'origine et l'item Workshop n'ont pas été touchés.";
        return RedirectToPage();
    }

    public IActionResult OnPostExportPack(Guid id, PackTransferContentMode contentMode, string? downloadToken)
    {
        try
        {
            var result = transfers.Export(id, contentMode);
            MarkDownload(downloadToken, true);
            var stream = new FileStream(result.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            return File(stream, "application/vnd.pzasm.pack+zip", result.FileName);
        }
        catch (Exception exception)
        {
            MarkDownload(downloadToken, false);
            TempData["Error"] = exception.Message;
            return RedirectToPage();
        }
    }

    public IActionResult OnPostImportPack(IFormFile archive, bool replaceExisting)
    {
        try
        {
            if (archive is null || archive.Length == 0) throw new InvalidDataException("Sélectionnez une archive .pzasm-pack complète.");
            if (!Path.GetExtension(archive.FileName).Equals(".pzasm-pack", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Le fichier doit utiliser l'extension .pzasm-pack.");
            using var stream = archive.OpenReadStream();
            var result = transfers.Import(stream, replaceExisting);
            TempData["Message"] = result.ContentMode == PackTransferContentMode.ConfigurationOnly
                ? $"Configuration du pack « {result.Project.Name} » importée avec son identifiant {result.Project.Id}. Téléchargez maintenant les sources Workshop avant de construire ou publier."
                : $"Pack complet « {result.Project.Name} » importé avec son identifiant {result.Project.Id}, {result.Files:N0} fichiers et {result.UniqueBlobs:N0} blobs uniques vérifiés.";
            return RedirectToPage("/Projects/Edit", new { id = result.Project.Id });
        }
        catch (Exception exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToPage();
        }
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

    private void MarkDownload(string? token, bool success)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 80) return;
        Response.Cookies.Append("PZASM.Download", token + ":" + (success ? "ok" : "error"), new CookieOptions
        {
            HttpOnly = false,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = Request.IsHttps,
            Path = "/",
            MaxAge = TimeSpan.FromMinutes(5)
        });
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
