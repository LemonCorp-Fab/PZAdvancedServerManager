using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Transfer;

namespace PZAdvancedServerManager.App.Pages;

[RequestSizeLimit(PackTransferService.MaximumUniqueArchiveBytes + 1024L * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = PackTransferService.MaximumUniqueArchiveBytes + 1024L * 1024 * 1024)]
public class ModpacksModel(PackageProjectStore store, PackageProjectService projects, PackTransferService transfers) : PageModel
{
    public IReadOnlyList<PackageProject> Projects { get; private set; } = [];

    public void OnGet() => Projects = store.GetAll();

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
        TempData["Message"] = "Projet, snapshots et builds PZASM supprimés. Les mods d’origine et l’item Workshop n’ont pas été touchés.";
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
                throw new InvalidDataException("Le fichier doit utiliser l’extension .pzasm-pack.");
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
}
