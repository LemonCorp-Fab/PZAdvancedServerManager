using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages;

public class IndexModel(PackageProjectStore store, PackageProjectService projects, PzEnvironmentService environment, ApplicationPaths paths) : PageModel
{
    public IReadOnlyList<PackageProject> Projects { get; private set; } = [];
    public PzInstallation Installation { get; private set; } = new();
    public string DataRoot => paths.DataRoot;

    public void OnGet()
    {
        Projects = store.GetAll();
        Installation = environment.Installation;
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

    public IActionResult OnPostRefreshDiscovery()
    {
        environment.Invalidate();
        TempData["Message"] = "Détection locale actualisée.";
        return RedirectToPage();
    }
}
