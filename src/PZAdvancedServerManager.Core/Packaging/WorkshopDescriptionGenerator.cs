using System.Text;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Packaging;

public static class WorkshopDescriptionGenerator
{
    public static string Generate(PackageProject project)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[h1]" + project.Name + "[/h1]");
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
            builder.AppendLine($"[*][b]{mod.Name}[/b] — Mod ID: {mod.ModId} — {author} — {source} — droits: {PermissionLabel(mod.Permission.Status)}");
        }
        builder.AppendLine();
        builder.AppendLine("[h2]Droits, crédits et responsabilité[/h2]");
        builder.AppendLine("Les mods inclus restent la propriété de leurs auteurs respectifs. PZ Advanced Server Manager est uniquement un outil technique : LemonCorp et les développeurs du programme ne donnent aucune autorisation de redistribution et ne sont pas responsables des packs créés ou publiés par les utilisateurs. Le créateur et l'éditeur de ce pack sont seuls responsables de vérifier et conserver les autorisations, licences et crédits requis, y compris pour un item non listé ou destiné à un serveur.");
        builder.AppendLine();
        builder.AppendLine($"Identifiant stable du projet : {project.Id:N}");
        return builder.ToString().Trim();
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
