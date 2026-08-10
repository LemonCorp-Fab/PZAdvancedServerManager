using System.Text;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Packaging;

public static class WorkshopDescriptionGenerator
{
    public static string Generate(PackageProject project)
    {
        var result = GenerateResult(project);
        if (!result.CanPublish)
            throw new InvalidOperationException(result.ErrorMessage);
        return result.Text;
    }

    public static WorkshopDescriptionResult GenerateResult(PackageProject project)
    {
        var detailed = GenerateDetailed(project);
        if (GetUtf8ByteCount(detailed) <= PzasmConstants.GeneratedWorkshopDescriptionMaximumUtf8Bytes)
            return new WorkshopDescriptionResult(detailed, GetUtf8ByteCount(detailed), false);

        try
        {
            var compact = GenerateCompact(project);
            return new WorkshopDescriptionResult(compact, GetUtf8ByteCount(compact), true);
        }
        catch (WorkshopDescriptionLimitException exception)
        {
            return new WorkshopDescriptionResult(
                GenerateOverflowPreview(project, exception.Message),
                exception.RequiredUtf8Bytes,
                true,
                exception.Message);
        }
    }

    public static int GetUtf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);

    private static string GenerateDetailed(PackageProject project)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[h1]" + OneLine(project.Name) + "[/h1]");
        builder.AppendLine(project.Description);
        builder.AppendLine();
        builder.AppendLine("[h2]Pack serveur géré par PZ Advanced Server Manager[/h2]");
        builder.AppendLine(project.Mode == PackageMode.Bundle
            ? "Ce Workshop item contient plusieurs Mod IDs conservés tels quels. Le serveur ne référence qu'un seul Workshop ID pour figer leur distribution ensemble."
            : "Ce Workshop item utilise le mode Fusion stricte : les fichiers compatibles ont été réunis sous un Mod ID propre au pack.");
        builder.AppendLine();
        builder.AppendLine("[h2]Mods inclus — liste exhaustive[/h2]");
        foreach (var mod in project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order).ThenBy(x => x.Name))
        {
            var source = mod.WorkshopId == 0 ? "source locale" : $"[url={mod.SourceUrl}]Workshop {mod.WorkshopId}[/url]";
            var author = string.IsNullOrWhiteSpace(mod.Author) ? "auteur non renseigné" : mod.Author;
            builder.AppendLine($"[*][b]{OneLine(mod.Name)}[/b] — Mod ID: {OneLine(mod.ModId)} — {OneLine(author)} — {source} — droits: {PermissionLabel(mod.Permission.Status)}");
        }
        builder.AppendLine();
        builder.AppendLine("[h2]Droits, crédits et responsabilité[/h2]");
        builder.AppendLine("Les mods inclus restent la propriété de leurs auteurs respectifs. PZ Advanced Server Manager est uniquement un outil technique : LemonCorp et les développeurs du programme ne donnent aucune autorisation de redistribution et ne sont pas responsables des packs créés ou publiés par les utilisateurs. Le créateur et l'éditeur de ce pack sont seuls responsables de vérifier et conserver les autorisations, licences et crédits requis, y compris pour un item non listé ou destiné à un serveur.");
        builder.AppendLine();
        builder.AppendLine($"Identifiant stable du projet : {project.Id:N}");
        return builder.ToString().Trim();
    }

    private static string GenerateCompact(PackageProject project)
    {
        var mods = project.Mods
            .Where(mod => mod.Enabled)
            .OrderBy(mod => mod.Order)
            .ThenBy(mod => mod.Name)
            .ToArray();
        var prefix = new StringBuilder()
            .AppendLine($"[h1]{OneLine(project.Name)}[/h1]")
            .ToString();
        var suffixBuilder = new StringBuilder();
        suffixBuilder.AppendLine();
        suffixBuilder.AppendLine("[h2]Pack serveur géré par PZ Advanced Server Manager[/h2]");
        suffixBuilder.AppendLine(project.Mode == PackageMode.Bundle
            ? "Ce Workshop item distribue ensemble plusieurs Mod IDs conservés tels quels sous un seul Workshop ID."
            : "Ce Workshop item utilise le mode Fusion stricte sous un Mod ID propre au pack.");
        suffixBuilder.AppendLine("La présentation est automatiquement condensée pour respecter la limite Steam. Chaque ligne associe un Workshop ID (W) à un ou plusieurs Mod IDs (M).");
        suffixBuilder.AppendLine();
        suffixBuilder.AppendLine($"[h2]Mods inclus — {mods.Length} références exhaustives[/h2]");
        foreach (var group in mods.GroupBy(mod => mod.WorkshopId))
        {
            var workshop = group.Key == 0 ? "LOCAL" : group.Key.ToString();
            var modIds = group
                .Select(mod => OneLine(mod.ModId))
                .Where(modId => modId.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            suffixBuilder.AppendLine($"[*]W:{workshop}·M:{string.Join(',', modIds)}");
        }
        suffixBuilder.AppendLine();
        suffixBuilder.AppendLine("Les noms complets, auteurs, versions et déclarations de droits restent disponibles dans Contents/pzasm-pack-manifest.json et dans le panneau PZASM en jeu.");
        suffixBuilder.AppendLine();
        suffixBuilder.AppendLine("[h2]Droits, crédits et responsabilité[/h2]");
        suffixBuilder.AppendLine("Les mods restent la propriété de leurs auteurs. L'éditeur du pack est seul responsable des autorisations, licences et crédits requis. PZ Advanced Server Manager est un outil technique et n'accorde aucun droit de redistribution.");
        suffixBuilder.AppendLine();
        suffixBuilder.AppendLine($"Identifiant stable du projet : {project.Id:N}");
        var suffix = suffixBuilder.ToString().TrimEnd();
        var fixedBytes = GetUtf8ByteCount(prefix) + GetUtf8ByteCount(suffix) + GetUtf8ByteCount(Environment.NewLine + Environment.NewLine);
        if (fixedBytes > PzasmConstants.GeneratedWorkshopDescriptionMaximumUtf8Bytes)
            throw new WorkshopDescriptionLimitException(
                fixedBytes,
                $"La liste exhaustive des Mod IDs dépasse à elle seule la limite de description Steam ({fixedBytes} octets). Scindez ce pack en plusieurs Workshop items avant de publier.");

        var descriptionBudget = PzasmConstants.GeneratedWorkshopDescriptionMaximumUtf8Bytes - fixedBytes;
        var description = TruncateUtf8(OneLine(project.Description), descriptionBudget);
        var result = string.IsNullOrWhiteSpace(description)
            ? prefix + Environment.NewLine + suffix
            : prefix + description + Environment.NewLine + Environment.NewLine + suffix;
        var bytes = GetUtf8ByteCount(result);
        if (bytes > PzasmConstants.GeneratedWorkshopDescriptionMaximumUtf8Bytes)
            throw new WorkshopDescriptionLimitException(
                bytes,
                $"La description Workshop générée dépasse la limite de sécurité Steam ({bytes} octets). Scindez ce pack avant de publier.");
        return result.Trim();
    }

    private static string GenerateOverflowPreview(PackageProject project, string errorMessage)
    {
        var enabledCount = project.Mods.Count(mod => mod.Enabled);
        var builder = new StringBuilder();
        builder.AppendLine($"[h1]{OneLine(project.Name)}[/h1]");
        builder.AppendLine();
        builder.AppendLine("[h2]Description Workshop à corriger[/h2]");
        builder.AppendLine(errorMessage);
        builder.AppendLine();
        builder.AppendLine($"Le pack reste entièrement modifiable. {enabledCount} Mod ID(s) sont actuellement activés.");
        builder.AppendLine("La publication est bloquée avant tout envoi à Steam, sans modifier les snapshots ni la configuration du pack.");
        return builder.ToString().Trim();
    }

    private static string OneLine(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(' ', value.Replace('\t', ' ').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (maximumBytes <= 0 || string.IsNullOrEmpty(value)) return string.Empty;
        if (GetUtf8ByteCount(value) <= maximumBytes) return value;
        const string ellipsis = "…";
        var contentBudget = Math.Max(0, maximumBytes - GetUtf8ByteCount(ellipsis));
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > contentBudget) break;
            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }
        return builder.Append(ellipsis).ToString();
    }

    private static string PermissionLabel(PermissionStatus status) => status switch
    {
        PermissionStatus.AuthorOwned => "créateur du pack = auteur/détenteur",
        PermissionStatus.ExplicitPermission => "autorisation explicite déclarée",
        PermissionStatus.CompatibleLicense => "licence compatible déclarée",
        PermissionStatus.Denied => "autorisation refusée",
        _ => "non documentés"
    };
}

public sealed record WorkshopDescriptionResult(
    string Text,
    int Utf8Bytes,
    bool IsCompact,
    string ErrorMessage = "")
{
    public bool CanPublish => string.IsNullOrWhiteSpace(ErrorMessage);
}

internal sealed class WorkshopDescriptionLimitException(int requiredUtf8Bytes, string message) : InvalidOperationException(message)
{
    public int RequiredUtf8Bytes { get; } = requiredUtf8Bytes;
}
