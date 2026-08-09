using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages.Projects;

public sealed class ConflictDiffModel(
    PackageProjectStore store,
    ModConflictAnalyzer conflicts,
    TextConflictDiffService textDiff) : PageModel
{
    public PackageProject Project { get; private set; } = new();
    public ModConflictIssue Conflict { get; private set; } = default!;
    public IReadOnlyList<ConflictTextPath> Paths { get; private set; } = [];
    public string SelectedPath { get; private set; } = string.Empty;
    public IReadOnlyList<ModConflictFileEvidence> Sources { get; private set; } = [];
    public TextConflictDiff Diff { get; private set; } = default!;
    public Guid SelectedLeft { get; private set; }
    public Guid SelectedRight { get; private set; }
    public bool IgnoreWhitespace { get; private set; }

    public IActionResult OnGet(Guid id, string conflictKey, string? virtualPath, Guid? left, Guid? right, bool ignoreWhitespace = false)
    {
        var project = store.Get(id);
        if (project is null) return NotFound();
        Project = project;
        Conflict = conflicts.Analyze(project).Issues.FirstOrDefault(issue => issue.Key.Equals(conflictKey, StringComparison.Ordinal))!;
        if (Conflict is null)
        {
            TempData["Error"] = "Le conflit demandé n'existe plus dans la version actuelle du pack.";
            return RedirectToPage("/Projects/Edit", new { id, tab = "compatibility" });
        }

        var roots = project.Mods
            .Select(mod => mod.BuildSourceRoot)
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .ToArray();
        var eligibleSources = Conflict.FileEvidence
            .Where(source => TextConflictDiffService.IsSupportedPath(source.PhysicalPath))
            .Where(source => System.IO.File.Exists(source.PhysicalPath))
            .Where(source => new FileInfo(source.PhysicalPath).Length <= TextConflictDiffService.MaximumFileBytes)
            .Where(source => roots.Any(root => IsPathWithin(Path.GetFullPath(source.PhysicalPath), root)))
            .ToArray();
        Paths = eligibleSources
            .GroupBy(source => source.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(source => source.ModReferenceId).Distinct().Count() >= 2)
            .Select(group => new ConflictTextPath(group.Key, group.Count()))
            .OrderBy(path => path.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (Paths.Count == 0)
        {
            TempData["Error"] = "Ce conflit ne contient pas au moins deux copies texte compatibles avec le comparateur.";
            return RedirectToPage("/Projects/Edit", new { id, tab = "compatibility", conflictType = Conflict.EffectiveTypeLabel });
        }
        SelectedPath = Paths.FirstOrDefault(path => path.VirtualPath.Equals(virtualPath, StringComparison.OrdinalIgnoreCase))?.VirtualPath
            ?? Paths.FirstOrDefault(path => path.VirtualPath.Equals(Conflict.PrimaryEvidence, StringComparison.OrdinalIgnoreCase))?.VirtualPath
            ?? Paths[0].VirtualPath;
        Sources = eligibleSources.Where(source => source.VirtualPath.Equals(SelectedPath, StringComparison.OrdinalIgnoreCase)).ToArray();

        var selectedLeft = Sources.FirstOrDefault(source => source.ModReferenceId == left) ?? Sources[0];
        var selectedRight = Sources.FirstOrDefault(source => source.ModReferenceId == right && source.ModReferenceId != selectedLeft.ModReferenceId)
            ?? Sources.First(source => source.ModReferenceId != selectedLeft.ModReferenceId);
        SelectedLeft = selectedLeft.ModReferenceId;
        SelectedRight = selectedRight.ModReferenceId;
        IgnoreWhitespace = ignoreWhitespace;

        try
        {
            Diff = textDiff.Compare(selectedLeft.PhysicalPath, selectedRight.PhysicalPath, ignoreWhitespace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TempData["Error"] = $"Comparaison impossible : {exception.Message}";
            return RedirectToPage("/Projects/Edit", new { id, tab = "compatibility", conflictType = Conflict.EffectiveTypeLabel });
        }
        return Page();
    }

    public ModConflictFileEvidence Source(Guid id) => Sources.First(source => source.ModReferenceId == id);

    public sealed record ConflictTextPath(string VirtualPath, int Copies);

    private static bool IsPathWithin(string candidatePath, string rootPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        return candidatePath.Equals(normalizedRoot, comparison)
            || candidatePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
}
