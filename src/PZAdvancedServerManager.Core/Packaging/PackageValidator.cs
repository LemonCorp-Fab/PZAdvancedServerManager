using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Pz;

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
        else if (project.Name.Length > 120)
            result.Issues.Add(new("PROJECT_NAME_LENGTH", "Le nom du pack ne peut pas dépasser 120 caractères.", true));
        if (project.Mods.All(x => !x.Enabled))
            result.Issues.Add(new("NO_MODS", "Ajoutez au moins un mod activé au pack.", true));
        if (!project.LegalWarningAccepted)
            result.Issues.Add(new("LEGAL_ACK", "L'avertissement sur les droits et autorisations n'a pas été marqué comme lu. Cela ne bloque ni la construction ni la publication.", false, Scope: ValidationScope.Warning));
        if (string.IsNullOrWhiteSpace(project.Automation.SteamCmdPath) || !File.Exists(project.Automation.SteamCmdPath))
            result.Issues.Add(new("STEAMCMD_PATH", "Indiquez un exécutable SteamCMD existant avant publication.", true, Scope: ValidationScope.PublishOnly));
        if (string.IsNullOrWhiteSpace(project.Automation.SteamUsername))
            result.Issues.Add(new("STEAM_USERNAME", "Le compte Steam éditeur est requis avant publication; aucun mot de passe n'est stocké.", true, Scope: ValidationScope.PublishOnly));
        if (project.Automation.PostPublishRestartDelayMinutes is < 5 or > 60)
            result.Issues.Add(new("POST_PUBLISH_DELAY", "Le délai de redémarrage après publication doit être compris entre 5 et 60 minutes.", true, Scope: ValidationScope.AutomationOnly));
        var configuredMaps = project.MapOrder.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var vanillaMapIndex = configuredMaps.FindIndex(x => x.Equals("Muldraugh, KY", StringComparison.OrdinalIgnoreCase));
        if (vanillaMapIndex >= 0 && vanillaMapIndex != configuredMaps.Count - 1)
            result.Issues.Add(new("MAP_BASE_PRIORITY", "La carte vanilla « Muldraugh, KY » devrait être la dernière de l'ordre des cartes afin que les cartes de mods restent prioritaires.", false, Scope: ValidationScope.Warning));
        if (project.Automation.Enabled)
        {
            if (project.Automation.DailyTimes.Length == 0)
                result.Issues.Add(new("AUTOMATION_SCHEDULE", "Ajoutez au moins une heure HH:mm pour activer la planification.", true, Scope: ValidationScope.AutomationOnly));
            foreach (var invalid in project.Automation.DailyTimes.Where(x => !TimeOnly.TryParseExact(x, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)))
                result.Issues.Add(new("AUTOMATION_TIME", $"Heure de planification invalide : « {invalid} ». Format attendu : HH:mm.", true, Scope: ValidationScope.AutomationOnly));
        }

        foreach (var duplicate in project.Mods.Where(x => x.Enabled).GroupBy(x => x.ModId, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            result.Issues.Add(new("DUPLICATE_MOD_ID", $"Le Mod ID « {duplicate.Key} » apparaît plusieurs fois.", true));

        if (project.Mode == PackageMode.Bundle)
        {
            foreach (var duplicate in project.Mods.Where(x => x.Enabled).GroupBy(x => x.EffectiveFolderName, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
                result.Issues.Add(new("DUPLICATE_FOLDER", $"Deux mods produiraient le même dossier « {duplicate.Key} » dans le bundle.", true));
        }

        var includedIds = project.Mods.Where(x => x.Enabled).Select(x => ModInfoParser.NormalizeDependencyId(x.ModId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in project.Mods.Where(x => x.Enabled))
        {
            if (string.IsNullOrWhiteSpace(mod.ModId))
                result.Issues.Add(new("MOD_ID", $"Le mod « {mod.Name} » n'a pas de Mod ID.", true, mod.Id));
            var buildSource = mod.BuildSourceRoot;
            if (!Directory.Exists(buildSource))
            {
                result.Issues.Add(new("SOURCE_MISSING", $"Source introuvable pour « {mod.Name} » : {buildSource}", true, mod.Id));
                continue;
            }
            if (!Directory.Exists(mod.PinnedSourceRoot))
                result.Issues.Add(new("SOURCE_NOT_PINNED", $"La source de « {mod.Name} » sera figée dans le cache PZASM avant le prochain build.", false, mod.Id, ValidationScope.Warning));

            var pinnedValidationToken = Directory.Exists(mod.PinnedSourceRoot) ? mod.PinnedContentHash : string.Empty;
            if (string.IsNullOrWhiteSpace(pinnedValidationToken) ||
                !pinnedValidationToken.Equals(mod.ValidatedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                mod.ForbiddenFiles = Directory.EnumerateFiles(buildSource, "*", SearchOption.AllDirectories)
                    .Where(file => ForbiddenExtensions.Contains(Path.GetExtension(file)))
                    .Select(file => Path.GetRelativePath(buildSource, file).Replace('\\', '/'))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                mod.ValidatedContentHash = pinnedValidationToken;
            }
            foreach (var file in mod.ForbiddenFiles)
                result.Issues.Add(new("FORBIDDEN_FILE", $"Fichier refusé par le Workshop PZ dans « {mod.Name} » : {Path.GetFileName(file)}", true, mod.Id));

            foreach (var required in mod.RequiredModIds.Select(ModInfoParser.NormalizeDependencyId).Where(x => !includedIds.Contains(x)))
                result.Issues.Add(new("MISSING_DEPENDENCY", $"« {mod.Name} » requiert le Mod ID « {required} », absent du pack.", true, mod.Id));

            switch (mod.Permission.Status)
            {
                case PermissionStatus.Unknown:
                    result.Issues.Add(new("RIGHTS_UNKNOWN", $"Autorisation non documentée pour « {mod.Name} ». Information consultative : la publication reste disponible.", false, mod.Id, ValidationScope.Warning));
                    break;
                case PermissionStatus.Denied:
                    result.Issues.Add(new("RIGHTS_DENIED", $"Le statut enregistré indique un refus pour « {mod.Name} ». Cet avertissement ne bloque pas les actions de l'administrateur.", false, mod.Id, ValidationScope.Warning));
                    break;
                case PermissionStatus.ExplicitPermission or PermissionStatus.CompatibleLicense when string.IsNullOrWhiteSpace(mod.Permission.PublicEvidenceUrl) && string.IsNullOrWhiteSpace(mod.Permission.PrivateAttachmentPath) && string.IsNullOrWhiteSpace(mod.Permission.Notes):
                    result.Issues.Add(new("RIGHTS_EVIDENCE", $"Aucune preuve ou note d'autorisation n'est jointe pour « {mod.Name} ». Cette documentation est facultative et non bloquante.", false, mod.Id, ValidationScope.Warning));
                    break;
            }
        }

        return result;
    }
}
