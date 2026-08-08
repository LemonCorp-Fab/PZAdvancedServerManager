using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Packaging;

public static class ModListImportParser
{
    public static ParsedModList Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("La liste de mods est vide.");

        var isServerConfig = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.TrimStart())
            .Any(line => line.StartsWith("Mods=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("WorkshopItems=", StringComparison.OrdinalIgnoreCase));

        if (!isServerConfig)
        {
            var modIds = SplitList(content);
            if (modIds.Count == 0) throw new InvalidDataException("Aucun Mod ID n'a été trouvé dans la liste.");
            return new ParsedModList(modIds, [], [], ModListSourceKind.SemicolonList);
        }

        var document = ServerConfigDocument.Parse(content);
        var modIdsFromConfig = Distinct(document.GetList("Mods"));
        var workshopIds = new List<ulong>();
        var invalidWorkshopIds = new List<string>();
        foreach (var value in document.GetList("WorkshopItems"))
        {
            if (ulong.TryParse(value, out var workshopId) && workshopId > 0)
            {
                if (!workshopIds.Contains(workshopId)) workshopIds.Add(workshopId);
            }
            else if (!invalidWorkshopIds.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                invalidWorkshopIds.Add(value);
            }
        }

        if (modIdsFromConfig.Count == 0 && workshopIds.Count == 0 && invalidWorkshopIds.Count == 0)
            throw new InvalidDataException("Le fichier ne contient aucun Mod ID dans Mods= ni aucun Workshop ID dans WorkshopItems=.");

        return new ParsedModList(modIdsFromConfig, workshopIds, invalidWorkshopIds, ModListSourceKind.ServerIni);
    }

    private static IReadOnlyList<string> SplitList(string content) => Distinct(content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => value.Trim().Trim('"')));

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values.Select(value => value.Trim()).Where(value => value.Length > 0))
            if (seen.Add(value)) result.Add(value);
        return result;
    }
}

public sealed record ParsedModList(
    IReadOnlyList<string> ModIds,
    IReadOnlyList<ulong> WorkshopIds,
    IReadOnlyList<string> InvalidWorkshopIds,
    ModListSourceKind SourceKind);

public enum ModListSourceKind
{
    SemicolonList,
    ServerIni
}
