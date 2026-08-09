using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Pz;

public enum ModConflictSeverity
{
    Information,
    Warning,
    Error
}

public enum ModConflictCategory
{
    Compatibility,
    Dependency,
    Order,
    Identity,
    Lua,
    Script,
    Map,
    Asset
}

public sealed record ModConflictIssue(
    string Key,
    string Code,
    string Title,
    string Detail,
    ModConflictSeverity Severity,
    ModConflictCategory Category,
    IReadOnlyList<Guid> ModReferenceIds,
    IReadOnlyList<string> ModIds,
    IReadOnlyList<string> Evidence,
    bool CanChooseWinner = false,
    bool CanDisableMods = false,
    bool IsResolved = false,
    string SelectedWinnerModId = "");

public sealed record ModConflictAnalysis(
    IReadOnlyList<ModConflictIssue> Issues,
    IReadOnlyList<Guid> RecommendedModOrder,
    IReadOnlyList<string> RecommendedMapOrder,
    int ScannedFiles,
    int ComparedFilePaths,
    TimeSpan Duration,
    string Fingerprint)
{
    public int ErrorCount => Issues.Count(issue => issue.Severity == ModConflictSeverity.Error && !issue.IsResolved);
    public int WarningCount => Issues.Count(issue => issue.Severity == ModConflictSeverity.Warning && !issue.IsResolved);
    public int ResolvedCount => Issues.Count(issue => issue.IsResolved);
    public bool HasModOrderChange => Issues.Any(issue => issue.Code == "MOD_ORDER" && !issue.IsResolved);
    public bool HasMapOrderChange => Issues.Any(issue => issue.Code == "MAP_ORDER" && !issue.IsResolved);
    public bool HasOrderChange => HasModOrderChange || HasMapOrderChange;
}

public sealed class ModConflictAnalyzer(MapPriorityService mapPriority)
{
    private readonly ConcurrentDictionary<string, Lazy<ModConflictAnalysis>> _cache = new(StringComparer.Ordinal);

    public ModConflictAnalysis Analyze(PackageProject project, bool refresh = false)
    {
        var fingerprint = ComputeFingerprint(project);
        if (refresh) _cache.TryRemove(fingerprint, out _);
        var lazy = _cache.GetOrAdd(fingerprint, _ => new Lazy<ModConflictAnalysis>(() => AnalyzeCore(project, fingerprint), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return lazy.Value; }
        catch
        {
            _cache.TryRemove(fingerprint, out _);
            throw;
        }
    }

    private ModConflictAnalysis AnalyzeCore(PackageProject project, string fingerprint)
    {
        var timer = Stopwatch.StartNew();
        var issues = new List<ModConflictIssue>();
        var mods = project.Mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Order).ThenBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var byModId = mods.GroupBy(mod => Normalize(mod.ModId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var edges = mods.ToDictionary(mod => mod.Id, _ => new HashSet<Guid>());

        AnalyzeIdentity(mods, issues);
        AnalyzeCompatibility(project, mods, issues);
        AnalyzeDeclaredRelations(project, mods, byModId, edges, issues);

        var fileIndex = new Dictionary<string, Dictionary<Guid, FileOwner>>(StringComparer.OrdinalIgnoreCase);
        var scannedFiles = IndexEffectiveFiles(mods, fileIndex);
        var comparedPaths = AnalyzeFileCollisions(project, mods, fileIndex, edges, issues);

        var mapAnalysis = mapPriority.Analyze(project);
        AnalyzeMaps(project, mods, mapAnalysis, issues);

        var recommended = StableTopologicalSort(mods, edges, out var cyclicIds);
        if (cyclicIds.Count > 0)
        {
            var cycleMods = mods.Where(mod => cyclicIds.Contains(mod.Id)).ToList();
            issues.Add(Issue(
                "MOD_ORDER_CYCLE",
                "CYCLE_ORDER",
                "Cycle dans les contraintes de chargement",
                "Les champs require/loadAfter/loadBefore et les priorit\u00e9s manuelles forment un cycle. Aucun ordre ne peut satisfaire toutes ces contraintes; ouvrez les preuves et retirez au moins une priorit\u00e9 manuelle.",
                ModConflictSeverity.Error,
                ModConflictCategory.Order,
                cycleMods,
                [string.Join(" -> ", cycleMods.Select(mod => mod.ModId))],
                canChooseWinner: false,
                canDisableMods: true));
        }

        var currentOrder = mods.Select(mod => mod.Id).ToArray();
        if (!currentOrder.SequenceEqual(recommended))
        {
            issues.Add(Issue(
                "MOD_ORDER",
                "MOD_ORDER",
                "Ordre des mods perfectible",
                "L'ordre recommand\u00e9 place d'abord les d\u00e9pendances et respecte require, loadAfter, loadBefore ainsi que les gagnants choisis dans l'atelier.",
                ModConflictSeverity.Warning,
                ModConflictCategory.Order,
                mods,
                recommended.Select((id, index) => $"{index + 1}. {mods.First(mod => mod.Id == id).ModId}").ToArray()));
        }

        var recommendedMaps = mapAnalysis.RecommendedOrder.ToArray();
        var currentMaps = mapAnalysis.Entries.Select(entry => entry.FolderName).ToArray();
        if (!currentMaps.SequenceEqual(recommendedMaps, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new ModConflictIssue(
                "MAP_ORDER",
                "MAP_ORDER",
                "Ordre des cartes perfectible",
                "Les connecteurs et correctifs sont plac\u00e9s avant leurs bases, les d\u00e9pendances lots sont respect\u00e9es et Muldraugh, KY reste en dernier.",
                ModConflictSeverity.Warning,
                ModConflictCategory.Map,
                [],
                [],
                recommendedMaps.Select((map, index) => $"{index + 1}. {map}").ToArray()));
        }

        timer.Stop();
        var orderedIssues = issues
            .OrderByDescending(issue => issue.Severity)
            .ThenBy(issue => issue.IsResolved)
            .ThenBy(issue => issue.Category)
            .ThenBy(issue => issue.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new ModConflictAnalysis(orderedIssues, recommended, recommendedMaps, scannedFiles, comparedPaths, timer.Elapsed, fingerprint);
    }

    private static void AnalyzeIdentity(IReadOnlyList<PackageModReference> mods, ICollection<ModConflictIssue> issues)
    {
        foreach (var duplicate in mods.GroupBy(mod => Normalize(mod.ModId), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var affected = duplicate.ToList();
            issues.Add(Issue(
                $"DUPLICATE_ID:{duplicate.Key}",
                "DUPLICATE_MOD_ID",
                $"Mod ID dupliqu\u00e9 : {duplicate.Key}",
                "Project Zomboid identifie les modules par Mod ID. Deux contenus diff\u00e9rents avec le m\u00eame ID sont ambigus et l'un peut masquer l'autre.",
                ModConflictSeverity.Error,
                ModConflictCategory.Identity,
                affected,
                affected.Select(mod => mod.BuildSourceRoot).ToArray(),
                canChooseWinner: true,
                canDisableMods: true));
        }

        foreach (var duplicate in mods.GroupBy(mod => mod.EffectiveFolderName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var affected = duplicate.ToList();
            issues.Add(Issue(
                $"DUPLICATE_FOLDER:{duplicate.Key}",
                "DUPLICATE_FOLDER",
                $"Dossier de bundle dupliqu\u00e9 : {duplicate.Key}",
                "Les deux sources produisent le m\u00eame dossier sous mods/. Le package ne peut pas conserver les deux sans renommage ou exclusion explicite.",
                ModConflictSeverity.Error,
                ModConflictCategory.Identity,
                affected,
                affected.Select(mod => mod.BuildSourceRoot).ToArray(),
                canChooseWinner: true,
                canDisableMods: true));
        }
    }

    private static void AnalyzeCompatibility(PackageProject project, IReadOnlyList<PackageModReference> mods, ICollection<ModConflictIssue> issues)
    {
        foreach (var mod in mods)
        {
            if (!Directory.Exists(mod.BuildSourceRoot))
            {
                issues.Add(Issue(
                    $"SOURCE_MISSING:{mod.Id:N}",
                    "SOURCE_MISSING",
                    $"Source absente : {mod.Name}",
                    "Le snapshot ou le dossier source n'existe plus. Le mod ne peut pas \u00eatre analys\u00e9 ni reconstruit.",
                    ModConflictSeverity.Error,
                    ModConflictCategory.Compatibility,
                    [mod],
                    [mod.BuildSourceRoot],
                    canDisableMods: true));
                continue;
            }

            var manifest = PzVersionSelector.SelectManifest(mod.BuildSourceRoot, project.TargetPzVersion, out var selectedFolder);
            if (string.IsNullOrWhiteSpace(manifest))
            {
                issues.Add(Issue(
                    $"MANIFEST_MISSING:{mod.Id:N}",
                    "MANIFEST_MISSING",
                    $"mod.info introuvable : {mod.Name}",
                    "Project Zomboid ne pourra pas d\u00e9couvrir ce Mod ID dans le package.",
                    ModConflictSeverity.Error,
                    ModConflictCategory.Compatibility,
                    [mod],
                    [mod.BuildSourceRoot],
                    canDisableMods: true));
                continue;
            }

            if (TargetMajor(project.TargetPzVersion) < 42 || IsBuild42Manifest(manifest, selectedFolder)) continue;
            issues.Add(Issue(
                $"B42_LEGACY:{mod.Id:N}",
                "B42_LEGACY",
                $"Version Build 42 absente : {mod.Name}",
                $"Le seul manifeste compatible trouv\u00e9 est \u00e0 la racine ({Path.GetFileName(Path.GetDirectoryName(manifest))}/mod.info) et ne d\u00e9clare ni dossier 42.x, ni pzversion/versionMin 42. Build 42 ignore les fichiers de mod Build 41 : le Mod ID appara\u00eetra ABSENT en jeu m\u00eame s'il est pr\u00e9sent dans Mods=.",
                ModConflictSeverity.Error,
                ModConflictCategory.Compatibility,
                [mod],
                [manifest, $"Profil s\u00e9lectionn\u00e9 : {(string.IsNullOrWhiteSpace(selectedFolder) ? "racine / legacy" : selectedFolder)}", $"Cible : {project.TargetPzVersion}"],
                canDisableMods: true));
        }
    }

    private static void AnalyzeDeclaredRelations(
        PackageProject project,
        IReadOnlyList<PackageModReference> mods,
        IReadOnlyDictionary<string, List<PackageModReference>> byModId,
        IDictionary<Guid, HashSet<Guid>> edges,
        ICollection<ModConflictIssue> issues)
    {
        foreach (var mod in mods)
        {
            var manifestInfo = ReadEffectiveManifest(mod, project.TargetPzVersion);
            var requiredIds = mod.RequiredModIds.Concat(manifestInfo?.Required ?? []).Select(Normalize).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            var loadAfterIds = mod.LoadAfterModIds.Concat(manifestInfo?.LoadAfter ?? []).Select(Normalize).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            var loadBeforeIds = mod.LoadBeforeModIds.Concat(manifestInfo?.LoadBefore ?? []).Select(Normalize).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            var incompatibleIds = mod.IncompatibleModIds.Concat(manifestInfo?.Incompatible ?? []).Select(Normalize).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var requiredId in requiredIds)
            {
                if (!byModId.TryGetValue(requiredId, out var requiredMods))
                {
                    issues.Add(Issue(
                        $"REQUIRED_MISSING:{mod.Id:N}:{requiredId}",
                        "MISSING_DEPENDENCY",
                        $"D\u00e9pendance absente : {requiredId}",
                        $"{mod.Name} d\u00e9clare require={requiredId}. Project Zomboid peut refuser son chargement tant que ce Mod ID n'est pas ajout\u00e9.",
                        ModConflictSeverity.Error,
                        ModConflictCategory.Dependency,
                        [mod],
                        [$"{mod.ModId} -> {requiredId}"],
                        canDisableMods: true));
                    continue;
                }
                foreach (var required in requiredMods) AddEdge(edges, required.Id, mod.Id);
            }

            foreach (var afterId in loadAfterIds)
                if (byModId.TryGetValue(afterId, out var beforeMods)) foreach (var before in beforeMods) AddEdge(edges, before.Id, mod.Id);

            foreach (var beforeId in loadBeforeIds)
                if (byModId.TryGetValue(beforeId, out var afterMods)) foreach (var after in afterMods) AddEdge(edges, mod.Id, after.Id);

            foreach (var incompatibleId in incompatibleIds)
            {
                if (!byModId.TryGetValue(incompatibleId, out var incompatibleMods)) continue;
                var affected = new[] { mod }.Concat(incompatibleMods).DistinctBy(item => item.Id).ToList();
                issues.Add(Issue(
                    $"DECLARED_INCOMPATIBLE:{string.Join(':', affected.Select(item => item.ModId).Order(StringComparer.OrdinalIgnoreCase))}",
                    "DECLARED_INCOMPATIBLE",
                    $"Incompatibilit\u00e9 d\u00e9clar\u00e9e par {mod.Name}",
                    $"Le mod.info de {mod.ModId} d\u00e9clare incompatible={incompatibleId}. L'ordre ne peut pas rendre ces deux modules compatibles.",
                    ModConflictSeverity.Error,
                    ModConflictCategory.Dependency,
                    affected,
                    [$"{mod.ModId} incompatible avec {incompatibleId}"],
                    canDisableMods: true));
            }
        }

        foreach (var entry in project.ConflictWinners)
        {
            var winner = mods.FirstOrDefault(mod => mod.ModId.Equals(entry.Value, StringComparison.OrdinalIgnoreCase));
            if (winner is null) continue;
            var participantIds = ParseParticipantIds(entry.Key);
            foreach (var loser in mods.Where(mod => participantIds.Contains(Normalize(mod.ModId)) && mod.Id != winner.Id)) AddEdge(edges, loser.Id, winner.Id);
        }
    }

    private static int IndexEffectiveFiles(IReadOnlyList<PackageModReference> mods, IDictionary<string, Dictionary<Guid, FileOwner>> index)
    {
        var count = 0;
        foreach (var mod in mods)
        {
            var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mediaRoot in PzVersionSelector.GetEffectiveMediaRoots(mod.BuildSourceRoot, mod.SelectedVersionFolder))
            {
                foreach (var file in Directory.EnumerateFiles(mediaRoot, "*", SearchOption.AllDirectories))
                {
                    var relative = NormalizePath(Path.GetRelativePath(mediaRoot, file));
                    effective[relative] = file;
                    count++;
                }
            }

            foreach (var file in effective)
            {
                if (!index.TryGetValue(file.Key, out var owners)) index[file.Key] = owners = new Dictionary<Guid, FileOwner>();
                owners[mod.Id] = new FileOwner(mod, file.Value);
            }
        }
        return count;
    }

    private static int AnalyzeFileCollisions(
        PackageProject project,
        IReadOnlyList<PackageModReference> mods,
        IReadOnlyDictionary<string, Dictionary<Guid, FileOwner>> index,
        IDictionary<Guid, HashSet<Guid>> edges,
        ICollection<ModConflictIssue> issues)
    {
        var groups = new Dictionary<FileConflictGroupKey, List<string>>();
        var compared = 0;
        foreach (var bucket in index.Where(entry => entry.Value.Count > 1))
        {
            compared++;
            var owners = bucket.Value.Values.OrderBy(owner => owner.Mod.Order).ToArray();
            var identical = FilesAreIdentical(owners);
            var category = Classify(bucket.Key);
            var signature = string.Join("|", owners.Select(owner => Normalize(owner.Mod.ModId)).Order(StringComparer.OrdinalIgnoreCase));
            var key = new FileConflictGroupKey(category, signature, identical);
            if (!groups.TryGetValue(key, out var paths)) groups[key] = paths = [];
            paths.Add(bucket.Key);
        }

        foreach (var group in groups)
        {
            var participantIds = group.Key.Participants.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var affected = mods.Where(mod => participantIds.Contains(Normalize(mod.ModId), StringComparer.OrdinalIgnoreCase)).ToList();
            var evidence = group.Value.Order(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
            if (group.Value.Count > evidence.Count) evidence.Add($"... {group.Value.Count - evidence.Count} autre(s) chemin(s)");
            var resolutionKey = ConflictKey(group.Key.Category, participantIds, group.Key.Identical ? "identical" : "different");
            project.ConflictWinners.TryGetValue(resolutionKey, out var winnerId);
            var winner = affected.FirstOrDefault(mod => mod.ModId.Equals(winnerId, StringComparison.OrdinalIgnoreCase));
            if (winner is not null)
                foreach (var loser in affected.Where(mod => mod.Id != winner.Id)) AddEdge(edges, loser.Id, winner.Id);

            var resolved = group.Key.Identical || winner is not null || project.AcknowledgedConflicts.Contains(resolutionKey, StringComparer.OrdinalIgnoreCase);
            var categoryLabel = group.Key.Category switch
            {
                ModConflictCategory.Lua => "Lua",
                ModConflictCategory.Script => "scripts",
                ModConflictCategory.Map => "cartes",
                _ => "assets"
            };
            issues.Add(new ModConflictIssue(
                resolutionKey,
                group.Key.Identical ? "IDENTICAL_FILES" : "FILE_COLLISION",
                group.Key.Identical
                    ? $"Fichiers {categoryLabel} identiques partag\u00e9s"
                    : $"Collision de {group.Value.Count} fichier(s) {categoryLabel}",
                group.Key.Identical
                    ? $"{string.Join(", ", affected.Select(mod => mod.Name))} fournissent exactement le m\u00eame contenu sous les m\u00eames chemins. Aucune donn\u00e9e ne diff\u00e8re; l'information est conserv\u00e9e pour l'audit."
                    : $"{string.Join(", ", affected.Select(mod => mod.Name))} fournissent des contenus diff\u00e9rents sous les m\u00eames chemins virtuels. Le mod charg\u00e9 apr\u00e8s les autres prend la priorit\u00e9 pour ces chemins; choisissez explicitement le gagnant puis testez les fonctions concern\u00e9es.",
                group.Key.Identical ? ModConflictSeverity.Information : ModConflictSeverity.Warning,
                group.Key.Category,
                affected.Select(mod => mod.Id).ToArray(),
                affected.Select(mod => mod.ModId).ToArray(),
                evidence,
                CanChooseWinner: !group.Key.Identical,
                CanDisableMods: !group.Key.Identical,
                IsResolved: resolved,
                SelectedWinnerModId: winner?.ModId ?? string.Empty));
        }
        return compared;
    }

    private static void AnalyzeMaps(PackageProject project, IReadOnlyList<PackageModReference> mods, MapOrderAnalysis mapAnalysis, ICollection<ModConflictIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in mapAnalysis.Entries.Where(entry => entry.Conflicts.Count > 0))
        {
            foreach (var conflict in map.Conflicts)
            {
                var otherName = conflict[..conflict.LastIndexOf(" (", StringComparison.Ordinal)];
                var pair = new[] { map.FolderName, otherName }.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var key = $"MAP_CELLS:{string.Join('|', pair)}";
                if (!seen.Add(key)) continue;
                var other = mapAnalysis.Entries.FirstOrDefault(entry => entry.FolderName.Equals(otherName, StringComparison.OrdinalIgnoreCase));
                var affected = mods.Where(mod => mod.ModId.Equals(map.SourceModId, StringComparison.OrdinalIgnoreCase) || mod.ModId.Equals(other?.SourceModId, StringComparison.OrdinalIgnoreCase)).ToList();
                var resolved = project.AcknowledgedConflicts.Contains(key, StringComparer.OrdinalIgnoreCase);
                issues.Add(new ModConflictIssue(
                    key,
                    "MAP_CELL_OVERLAP",
                    $"Cellules de carte superpos\u00e9es : {pair[0]} / {pair[1]}",
                    "Les deux cartes contiennent au moins une m\u00eame cellule .lotheader. Dans la liste Map=, la premi\u00e8re carte gagne la zone superpos\u00e9e. V\u00e9rifiez si l'une est un connecteur, correctif ou remplacement volontaire.",
                    ModConflictSeverity.Warning,
                    ModConflictCategory.Map,
                    affected.Select(mod => mod.Id).ToArray(),
                    affected.Select(mod => mod.ModId).ToArray(),
                    [map.FolderName + " : " + conflict, other?.Recommendation ?? ""],
                    CanChooseWinner: false,
                    CanDisableMods: affected.Count > 0,
                    IsResolved: resolved));
            }
        }
    }

    private static IReadOnlyList<Guid> StableTopologicalSort(IReadOnlyList<PackageModReference> mods, IReadOnlyDictionary<Guid, HashSet<Guid>> edges, out HashSet<Guid> cyclicIds)
    {
        var incoming = mods.ToDictionary(mod => mod.Id, _ => 0);
        foreach (var edge in edges.SelectMany(entry => entry.Value.Select(target => (Source: entry.Key, Target: target))))
            if (incoming.ContainsKey(edge.Source) && incoming.ContainsKey(edge.Target)) incoming[edge.Target]++;

        var remaining = mods.Select(mod => mod.Id).ToHashSet();
        var currentRank = mods.Select((mod, index) => (mod.Id, index)).ToDictionary(item => item.Id, item => item.index);
        var result = new List<Guid>(mods.Count);
        while (remaining.Count > 0)
        {
            var next = remaining.Where(id => incoming[id] == 0).OrderBy(id => currentRank[id]).FirstOrDefault();
            if (next == Guid.Empty) break;
            result.Add(next);
            remaining.Remove(next);
            foreach (var target in edges[next]) incoming[target]--;
        }
        cyclicIds = remaining;
        result.AddRange(mods.Where(mod => remaining.Contains(mod.Id)).Select(mod => mod.Id));
        return result;
    }

    private static ModConflictIssue Issue(
        string key,
        string code,
        string title,
        string detail,
        ModConflictSeverity severity,
        ModConflictCategory category,
        IReadOnlyList<PackageModReference> mods,
        IReadOnlyList<string> evidence,
        bool canChooseWinner = false,
        bool canDisableMods = false) => new(
            key,
            code,
            title,
            detail,
            severity,
            category,
            mods.Select(mod => mod.Id).ToArray(),
            mods.Select(mod => mod.ModId).ToArray(),
            evidence,
            canChooseWinner,
            canDisableMods);

    private static bool IsBuild42Manifest(string manifest, string selectedFolder)
    {
        if (TryVersionMajor(selectedFolder, out var folderMajor) && folderMajor >= 42) return true;
        var info = ModInfoParser.Parse(manifest);
        foreach (var key in new[] { "pzversion", "versionMin", "versionMax" })
            if (info.Properties.TryGetValue(key, out var value) && TryVersionMajor(value, out var major) && major >= 42) return true;
        return false;
    }

    private static ModInfo? ReadEffectiveManifest(PackageModReference mod, string targetVersion)
    {
        if (!Directory.Exists(mod.BuildSourceRoot)) return null;
        var manifest = PzVersionSelector.SelectManifest(mod.BuildSourceRoot, targetVersion, out _);
        if (string.IsNullOrWhiteSpace(manifest)) return null;
        try { return ModInfoParser.Parse(manifest); }
        catch (IOException) { return null; }
    }

    private static bool FilesAreIdentical(IReadOnlyList<FileOwner> owners)
    {
        var sizes = owners.Select(owner => new FileInfo(owner.Path).Length).Distinct().Take(2).ToArray();
        if (sizes.Length > 1) return false;
        string? first = null;
        foreach (var owner in owners)
        {
            using var stream = new FileStream(owner.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            first ??= hash;
            if (!hash.Equals(first, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static ModConflictCategory Classify(string path)
    {
        if (path.StartsWith("lua/", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".lua", StringComparison.OrdinalIgnoreCase)) return ModConflictCategory.Lua;
        if (path.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) && path.Contains("script", StringComparison.OrdinalIgnoreCase)) return ModConflictCategory.Script;
        if (path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path) is ".lotheader" or ".lotpack" or ".bin") return ModConflictCategory.Map;
        return ModConflictCategory.Asset;
    }

    public static string ConflictKey(ModConflictCategory category, IEnumerable<string> modIds, string discriminator)
    {
        var participants = string.Join('|', modIds.Select(Normalize).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        return $"FILES:{category}:{participants}:{discriminator}";
    }

    private static HashSet<string> ParseParticipantIds(string key)
    {
        if (!key.StartsWith("FILES:", StringComparison.OrdinalIgnoreCase)) return [];
        var parts = key.Split(':');
        return parts.Length >= 4
            ? parts[2].Split('|', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
    }

    private static string ComputeFingerprint(PackageProject project)
    {
        var builder = new StringBuilder()
            .Append(project.TargetPzVersion).Append('|').Append(project.Mode).Append('|')
            .AppendJoin(';', project.MapOrder).Append('|');
        foreach (var mod in project.Mods.OrderBy(mod => mod.Order))
        {
            builder.Append(mod.Id).Append(':').Append(mod.Enabled).Append(':').Append(mod.Order).Append(':')
                .Append(mod.ModId).Append(':').Append(mod.SelectedVersionFolder).Append(':')
                .Append(mod.PinnedContentHash).Append(':').Append(mod.BuildSourceRoot).Append(':')
                .AppendJoin(',', mod.RequiredModIds).Append(':').AppendJoin(',', mod.LoadAfterModIds).Append(':')
                .AppendJoin(',', mod.LoadBeforeModIds).Append(':').AppendJoin(',', mod.IncompatibleModIds).Append('|');
        }
        foreach (var resolution in project.ConflictWinners.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)) builder.Append(resolution.Key).Append('=').Append(resolution.Value).Append('|');
        builder.AppendJoin('|', project.AcknowledgedConflicts.Order(StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AddEdge(IDictionary<Guid, HashSet<Guid>> edges, Guid before, Guid after)
    {
        if (before != after && edges.TryGetValue(before, out var targets) && edges.ContainsKey(after)) targets.Add(after);
    }

    private static int TargetMajor(string value) => TryVersionMajor(value, out var major) ? major : 0;
    private static bool TryVersionMajor(string value, out int major)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var first = normalized.Split('.', '-', '_', ' ').FirstOrDefault();
        return int.TryParse(first, out major);
    }

    private static string Normalize(string value) => ModInfoParser.NormalizeDependencyId(value).ToLowerInvariant();
    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    private sealed record FileOwner(PackageModReference Mod, string Path);
    private readonly record struct FileConflictGroupKey(ModConflictCategory Category, string Participants, bool Identical);
}
