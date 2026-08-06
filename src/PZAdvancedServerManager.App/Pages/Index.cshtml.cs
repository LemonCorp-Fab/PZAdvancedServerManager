using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using PZAdvancedServerManager.App.Services;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;

namespace PZAdvancedServerManager.App.Pages;

public class IndexModel(PackageProjectStore store, DiscoveryCache discovery, ApplicationPaths paths) : PageModel
{
    public IReadOnlyList<PackageProject> Projects { get; private set; } = [];
    public PzInstallation Installation { get; private set; } = new();
    public string DataRoot => paths.DataRoot;

    public void OnGet()
    {
        Projects = store.GetAll();
        Installation = discovery.Installation;
    }

    public IActionResult OnPostCreate(string name)
    {
        var project = store.Create(name);
        return RedirectToPage("/Projects/Edit", new { id = project.Id });
    }

    public IActionResult OnPostRefreshDiscovery()
    {
        discovery.Invalidate();
        TempData["Message"] = "Détection locale actualisée.";
        return RedirectToPage();
    }
}
