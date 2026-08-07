using System.Text.RegularExpressions;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class MapPriorityService
{
    private static readonly Regex HighPriorityName = new("road|connector|bridge|access|patch|fix|overlay|extension|(^|[ _-])ext($|[ _-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CellFileName = new("^(?<x>-?\\d+)_(?<y>-?\\d+)\\.lotheader$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public MapOrderAnalysis Analyze(PackageProject project)
    {
        var maps = new Dictionary<string, MapCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order))
        {
            foreach (var mediaRoot in PzVersionSelector.GetEffectiveMediaRoots(mod.BuildSourceRoot, mod.SelectedVersionFolder))
            {
                var mapsRoot = Path.Combine(mediaRoot, "maps");
                if (!Directory.Exists(mapsRoot)) continue;
                foreach (var mapRoot in Directory.EnumerateDirectories(mapsRoot))
                {
                    var folderName = Path.GetFileName(mapRoot);
                    if (string.IsNullOrWhiteSpace(folderName)) continue;
                    if (!maps.TryGetValue(folderName, out var candidate))
                    {
                        candidate = new MapCandidate(folderName, mod.Name, mod.ModId, mod.Order);
                        maps.Add(folderName, candidate);
                    }
                    candidate.Merge(mapRoot);
                }
            }
        }

        foreach (var manual in project.MapOrder.Where(x => !string.IsNullOrWhiteSpace(x)))
            if (!maps.ContainsKey(manual) && !manual.Equals("Muldraugh, KY", StringComparison.OrdinalIgnoreCase))
                maps.Add(manual, new MapCandidate(manual, "Entrée manuelle", string.Empty, int.MaxValue - 1) { IsManual = true });

        maps["Muldraugh, KY"] = new MapCandidate("Muldraugh, KY", "Carte vanilla", "Base game", int.MaxValue) { IsVanilla = true };
        DetectConflicts(maps.Values.Where(x => !x.IsVanilla).ToList());
        var recommended = Recommend(maps);
        var current = project.MapOrder.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (current.Count == 0) current.AddRange(recommended);
        else foreach (var map in recommended.Where(x => !current.Contains(x, StringComparer.OrdinalIgnoreCase))) current.Add(map);

        var entries = current.Select((name, index) => maps.TryGetValue(name, out var candidate)
            ? candidate.ToEntry(index, recommended.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)))
            : new MapPriorityEntry(name, "Entrée manuelle", string.Empty, string.Empty, 0, [], "Ordre saisi manuellement.", false, true, index, index)).ToList();
        return new MapOrderAnalysis(entries, recommended);
    }

    private static void DetectConflicts(IReadOnlyList<MapCandidate> maps)
    {
        for (var left = 0; left < maps.Count; left++)
        {
            for (var right = left + 1; right < maps.Count; right++)
            {
                if (maps[left].Cells.Count == 0 || !maps[left].Cells.Overlaps(maps[right].Cells)) continue;
                var overlap = maps[left].Cells.Intersect(maps[right].Cells, StringComparer.OrdinalIgnoreCase).Count();
                maps[left].Conflicts.Add($"{maps[right].FolderName} ({overlap} cellule(s))");
                maps[right].Conflicts.Add($"{maps[left].FolderName} ({overlap} cellule(s))");
            }
        }
    }

    private static List<string> Recommend(IReadOnlyDictionary<string, MapCandidate> maps)
    {
        var incoming = maps.Keys.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outgoing = maps.Keys.ToDictionary(x => x, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var map in maps.Values)
        {
            if (string.IsNullOrWhiteSpace(map.Lots) || map.Lots.Equals("NONE", StringComparison.OrdinalIgnoreCase) || !maps.ContainsKey(map.Lots)) continue;
            outgoing[map.FolderName].Add(map.Lots);
            incoming[map.Lots]++;
        }

        var remaining = new HashSet<string>(maps.Keys, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(x => incoming[x] == 0).OrderBy(x => PriorityScore(maps[x])).ThenBy(x => maps[x].ModOrder).ThenBy(x => maps[x].Cells.Count).ThenBy(x => x, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault();
            ready ??= remaining.OrderBy(x => PriorityScore(maps[x])).ThenBy(x => maps[x].ModOrder).ThenBy(x => x, StringComparer.CurrentCultureIgnoreCase).First();
            result.Add(ready);
            remaining.Remove(ready);
            foreach (var dependent in outgoing[ready]) incoming[dependent]--;
        }

        result.RemoveAll(x => x.Equals("Muldraugh, KY", StringComparison.OrdinalIgnoreCase));
        result.Add("Muldraugh, KY");
        return result;
    }

    private static int PriorityScore(MapCandidate map)
    {
        if (map.IsVanilla) return 1000;
        if (HighPriorityName.IsMatch(map.FolderName)) return 0;
        if (map.Conflicts.Count > 0) return 10;
        if (map.IsManual) return 30;
        return 20;
    }

    private sealed class MapCandidate(string folderName, string sourceModName, string sourceModId, int modOrder)
    {
        public string FolderName { get; } = folderName;
        public string SourceModName { get; } = sourceModName;
        public string SourceModId { get; } = sourceModId;
        public int ModOrder { get; } = modOrder;
        public string Lots { get; private set; } = string.Empty;
        public HashSet<string> Cells { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Conflicts { get; } = [];
        public bool IsVanilla { get; init; }
        public bool IsManual { get; init; }

        public void Merge(string mapRoot)
        {
            var mapInfo = Path.Combine(mapRoot, "map.info");
            if (File.Exists(mapInfo))
            {
                foreach (var line in File.ReadLines(mapInfo))
                {
                    var separator = line.IndexOf('=');
                    if (separator <= 0 || !line[..separator].Trim().Equals("lots", StringComparison.OrdinalIgnoreCase)) continue;
                    Lots = line[(separator + 1)..].Trim();
                    break;
                }
            }
            foreach (var file in Directory.EnumerateFiles(mapRoot, "*.lotheader", SearchOption.AllDirectories))
            {
                var match = CellFileName.Match(Path.GetFileName(file));
                if (match.Success) Cells.Add($"{match.Groups["x"].Value}:{match.Groups["y"].Value}");
            }
        }

        public MapPriorityEntry ToEntry(int currentRank, int recommendedRank)
        {
            var reason = IsVanilla
                ? "Carte de base : recommandée en dernière position."
                : !string.IsNullOrWhiteSpace(Lots) && !Lots.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                    ? $"Utilise « {Lots} » comme base; cette carte doit rester au-dessus."
                    : HighPriorityName.IsMatch(FolderName)
                        ? "Connexion, correctif ou extension détecté : priorité haute recommandée."
                        : Conflicts.Count > 0
                            ? "Chevauche d'autres cartes; la première de la liste gagne les cellules en conflit."
                            : "Aucun conflit de cellules détecté avec les autres cartes du pack.";
            return new MapPriorityEntry(FolderName, SourceModName, SourceModId, Lots, Cells.Count, Conflicts, reason, IsVanilla, IsManual, currentRank, recommendedRank);
        }
    }
}

public sealed record MapPriorityEntry(string FolderName, string SourceModName, string SourceModId, string Lots, int CellCount, IReadOnlyList<string> Conflicts, string Recommendation, bool IsVanilla, bool IsManual, int CurrentRank, int RecommendedRank);
public sealed record MapOrderAnalysis(IReadOnlyList<MapPriorityEntry> Entries, IReadOnlyList<string> RecommendedOrder);
