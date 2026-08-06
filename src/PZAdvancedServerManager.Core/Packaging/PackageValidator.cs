using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Packaging;

public sealed class PackageValidator
{
    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".app", ".dylib", ".sh", ".so", ".zip"
    };

    public PackageValidationResult Validate(PackageProject project)
    {
        var result = new PackageValidationResult();
        if (string.IsNullOrWhiteSpace(project.Name))
            result.Issues.Add(new("PROJECT_NAME", "Le pack doit avoir un nom.", true));
        if (project.Mods.All(x => !x.Enabled))
            result.Issues.Add(new("NO_MODS", "Ajoutez au moins un mod activé au pack.", true));
        if (!project.LegalWarningAccepted)
            result.Issues.Add(new("LEGAL_ACK", "L'avertissement sur les droits et autorisations doit être accepté.", true));
        if (project.Automation.Enabled && project.Automation.PublishAfterBuild && string.IsNullOrWhiteSpace(project.Automation.CoordinatedServerName))
            result.Issues.Add(new("AUTOMATION_SERVER", "La publication planifiée exige un profil serveur coordonné afin d'éviter un mismatch pendant que l'ancienne version est en mémoire.", true));

        foreach (var duplicate in project.Mods.Where(x => x.Enabled).GroupBy(x => x.ModId, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            result.Issues.Add(new("DUPLICATE_MOD_ID", $"Le Mod ID « {duplicate.Key} » apparaît plusieurs fois.", true));

        if (project.Mode == PackageMode.Bundle)
        {
            foreach (var duplicate in project.Mods.Where(x => x.Enabled).GroupBy(x => Path.GetFileName(Path.TrimEndingDirectorySeparator(x.SourceModRoot)), StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
                result.Issues.Add(new("DUPLICATE_FOLDER", $"Deux mods produiraient le même dossier « {duplicate.Key} » dans le bundle.", true));
        }

        foreach (var mod in project.Mods.Where(x => x.Enabled))
        {
            if (string.IsNullOrWhiteSpace(mod.ModId))
                result.Issues.Add(new("MOD_ID", $"Le mod « {mod.Name} » n'a pas de Mod ID.", true, mod.Id));
            if (!Directory.Exists(mod.SourceModRoot))
            {
                result.Issues.Add(new("SOURCE_MISSING", $"Source introuvable pour « {mod.Name} » : {mod.SourceModRoot}", true, mod.Id));
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(mod.SourceModRoot, "*", SearchOption.AllDirectories))
            {
                if (ForbiddenExtensions.Contains(Path.GetExtension(file)))
                    result.Issues.Add(new("FORBIDDEN_FILE", $"Fichier refusé par le Workshop PZ dans « {mod.Name} » : {Path.GetFileName(file)}", true, mod.Id));
            }

            var includedIds = project.Mods.Where(x => x.Enabled).Select(x => x.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var required in mod.RequiredModIds.Where(x => !includedIds.Contains(x)))
                result.Issues.Add(new("MISSING_DEPENDENCY", $"« {mod.Name} » requiert le Mod ID « {required} », absent du pack.", true, mod.Id));

            switch (mod.Permission.Status)
            {
                case PermissionStatus.Unknown:
                    result.Issues.Add(new("RIGHTS_UNKNOWN", $"Autorisation non documentée pour « {mod.Name} ». Construction locale possible, publication bloquée.", true, mod.Id));
                    break;
                case PermissionStatus.Denied:
                    result.Issues.Add(new("RIGHTS_DENIED", $"Le détenteur des droits a refusé l'inclusion de « {mod.Name} ».", true, mod.Id));
                    break;
                case PermissionStatus.ExplicitPermission or PermissionStatus.CompatibleLicense when string.IsNullOrWhiteSpace(mod.Permission.PublicEvidenceUrl) && string.IsNullOrWhiteSpace(mod.Permission.PrivateAttachmentPath) && string.IsNullOrWhiteSpace(mod.Permission.Notes):
                    result.Issues.Add(new("RIGHTS_EVIDENCE", $"Ajoutez une preuve ou une note d'autorisation pour « {mod.Name} ».", true, mod.Id));
                    break;
            }
        }

        return result;
    }
}
