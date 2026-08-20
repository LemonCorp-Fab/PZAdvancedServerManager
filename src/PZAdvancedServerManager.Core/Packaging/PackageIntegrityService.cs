using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PZAdvancedServerManager.Core.Packaging;

public sealed class PackageIntegrityManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Algorithm { get; set; } = "sha256-tree-v1";
    public string PayloadFingerprint { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public int CaseCorrections { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<PackageIntegrityFile> Files { get; set; } = [];
}

public sealed record PackageIntegrityFile(string Path, long Bytes, string Sha256);

public sealed record PackageIntegritySource(
    string SourceRoot,
    string DestinationPrefix,
    bool ReusePreviousHashes,
    bool TopLevelOnly = false);

public sealed record PackageCaseCorrection(string File, string Before, string After);

public sealed record PackageCaseRepairReport(
    int FilesInspected,
    int ReferencesCorrected,
    IReadOnlyList<PackageCaseCorrection> Corrections,
    IReadOnlyList<string> Warnings)
{
    public static PackageCaseRepairReport Empty { get; } = new(0, 0, [], []);
}

public sealed record PackageIntegrityVerification(
    bool Success,
    string PayloadFingerprint,
    int FilesVerified,
    long BytesVerified,
    string Message);

public sealed class PackageIntegrityException(string message) : IOException(message);

public static partial class PackageIntegrityService
{
    public const string ManifestFileName = "pzasm-integrity-manifest.json";
    private const long MaximumTextFileBytes = 8L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lua", ".txt", ".xml", ".json", ".properties", ".ini", ".info", ".cfg", ".md"
    };
    private static readonly HashSet<string> ReferenceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lua", ".txt", ".xml", ".json", ".png", ".dds", ".tga", ".jpg", ".jpeg", ".gif",
        ".fbx", ".x", ".obj", ".wav", ".ogg", ".bank", ".tiles", ".pack", ".bin", ".ttf"
    };
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static PackageCaseRepairReport RepairCaseReferences(string componentRoot)
    {
        var root = ResolveRoot(componentRoot);
        ValidatePortableTree(root);
        var targets = EnumerateFiles(root)
            .Where(file => !file.RelativePath.Equals(ManifestFileName, StringComparison.Ordinal))
            .Select(file => new ReferenceTarget(file.RelativePath, ScopeOf(file.RelativePath)))
            .ToArray();
        var aliases = BuildAliasIndex(targets);
        var corrections = new List<PackageCaseCorrection>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var inspected = 0;

        foreach (var file in EnumerateFiles(root))
        {
            var extension = Path.GetExtension(file.RelativePath);
            if (!TextExtensions.Contains(extension) || file.Length > MaximumTextFileBytes) continue;
            var bytes = File.ReadAllBytes(file.FullPath);
            var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            string text;
            try
            {
                text = StrictUtf8.GetString(hasBom ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes);
            }
            catch (DecoderFallbackException)
            {
                warnings.Add($"{file.RelativePath} : encodage non UTF-8, références non corrigées automatiquement.");
                continue;
            }

            inspected++;
            var edits = FindCaseEdits(text, file.RelativePath, aliases, warnings);
            if (edits.Count == 0) continue;
            var updated = new StringBuilder(text);
            foreach (var edit in edits.OrderByDescending(edit => edit.Start))
            {
                updated.Remove(edit.Start, edit.Length);
                updated.Insert(edit.Start, edit.Replacement);
                corrections.Add(new PackageCaseCorrection(file.RelativePath, edit.Original, edit.Replacement));
            }
            AtomicWriteUtf8(file.FullPath, updated.ToString(), hasBom);
        }

        ValidatePortableTree(root);
        return new PackageCaseRepairReport(inspected, corrections.Count, corrections, warnings.Order(StringComparer.Ordinal).ToArray());
    }

    public static PackageIntegrityManifest CreateManifest(
        string contentRoot,
        int caseCorrections = 0,
        IEnumerable<string>? warnings = null)
        => CreateManifest(contentRoot, [new PackageIntegritySource(contentRoot, string.Empty, false)], null, caseCorrections, warnings);

    public static PackageIntegrityManifest CreateManifest(
        string manifestRoot,
        IReadOnlyCollection<PackageIntegritySource> sources,
        PackageIntegrityManifest? previous,
        int caseCorrections = 0,
        IEnumerable<string>? warnings = null)
    {
        var root = ResolveRoot(manifestRoot);
        var files = new List<PackageIntegrityFile>();
        foreach (var source in sources)
        {
            var sourceRoot = ResolveRoot(source.SourceRoot);
            ValidatePortableTree(sourceRoot);
            var prefix = NormalizePrefix(source.DestinationPrefix);
            var actual = EnumerateFiles(sourceRoot, source.TopLevelOnly)
                .Where(file => !file.RelativePath.Equals(ManifestFileName, StringComparison.Ordinal))
                .Select(file => new PublishedFile(file, CombinePublishedPath(prefix, file.RelativePath)))
                .ToArray();
            var reused = source.ReusePreviousHashes
                ? TryReusePreviousFiles(actual, previous)
                : null;
            if (reused is not null)
            {
                files.AddRange(reused);
                continue;
            }
            foreach (var file in actual)
            {
                using var stream = File.OpenRead(file.Source.FullPath);
                files.Add(new PackageIntegrityFile(
                    file.PublishedPath,
                    file.Source.Length,
                    Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()));
            }
        }
        var collision = files.GroupBy(file => PortableKey(file.Path), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() > 1);
        if (collision is not null)
            throw new PackageIntegrityException($"Plusieurs sources produisent le même chemin portable : {string.Join(" <> ", collision.Select(file => file.Path))}");
        var manifest = new PackageIntegrityManifest
        {
            Files = files,
            FileCount = files.Count,
            TotalBytes = files.Sum(file => file.Bytes),
            CaseCorrections = caseCorrections,
            Warnings = warnings?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? []
        };
        manifest.PayloadFingerprint = ComputeTreeFingerprint(manifest.Files);
        AtomicWriteUtf8(Path.Combine(root, ManifestFileName), JsonSerializer.Serialize(manifest, JsonOptions), false);
        return manifest;
    }

    public static PackageIntegrityManifest ReadManifest(string contentRoot)
    {
        var path = Path.Combine(ResolveRoot(contentRoot), ManifestFileName);
        if (!File.Exists(path)) throw new PackageIntegrityException($"Manifeste d'intégrité absent : {path}");
        try
        {
            return JsonSerializer.Deserialize<PackageIntegrityManifest>(File.ReadAllText(path), JsonOptions)
                   ?? throw new PackageIntegrityException($"Manifeste d'intégrité vide : {path}");
        }
        catch (JsonException exception)
        {
            throw new PackageIntegrityException($"Manifeste d'intégrité illisible : {exception.Message}");
        }
    }

    public static PackageIntegrityVerification VerifyManifest(string contentRoot, string expectedFingerprint = "")
    {
        var root = ResolveRoot(contentRoot);
        ValidatePortableTree(root);
        var manifest = ReadManifest(root);
        if (manifest.SchemaVersion != 1 || !manifest.Algorithm.Equals("sha256-tree-v1", StringComparison.Ordinal))
            throw new PackageIntegrityException($"Version de manifeste d'intégrité non prise en charge : {manifest.SchemaVersion}/{manifest.Algorithm}.");
        if (manifest.FileCount != manifest.Files.Count || manifest.TotalBytes != manifest.Files.Sum(file => file.Bytes))
            throw new PackageIntegrityException("Les totaux du manifeste d'intégrité sont incohérents.");

        var duplicate = manifest.Files.GroupBy(file => PortableKey(file.Path), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new PackageIntegrityException($"Le manifeste contient plusieurs entrées incompatibles pour « {duplicate.Key} ».");

        var declaredFingerprint = ComputeTreeFingerprint(manifest.Files);
        if (!declaredFingerprint.Equals(manifest.PayloadFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new PackageIntegrityException("L'empreinte racine déclarée ne correspond pas aux entrées du manifeste.");
        if (!string.IsNullOrWhiteSpace(expectedFingerprint) &&
            !declaredFingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new PackageIntegrityException($"Le package téléchargé expose l'empreinte {declaredFingerprint}, attendue : {expectedFingerprint}.");

        var actualFiles = EnumerateFiles(root)
            .Where(file => !file.RelativePath.Equals(ManifestFileName, StringComparison.Ordinal))
            .ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var declaredFiles = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var missing = declaredFiles.Keys.Except(actualFiles.Keys, StringComparer.Ordinal).Take(5).ToArray();
        var unexpected = actualFiles.Keys.Except(declaredFiles.Keys, StringComparer.Ordinal).Take(5).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0)
            throw new PackageIntegrityException(
                $"Arborescence différente du manifeste. Manquants : {FormatPaths(missing)}. Inattendus : {FormatPaths(unexpected)}.");

        var verifiedBytes = 0L;
        foreach (var expected in manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var actual = actualFiles[expected.Path];
            if (actual.Length != expected.Bytes)
                throw new PackageIntegrityException($"Taille incorrecte pour {expected.Path} : {actual.Length} au lieu de {expected.Bytes} octets.");
            using var stream = File.OpenRead(actual.FullPath);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new PackageIntegrityException($"Hash SHA-256 incorrect pour {expected.Path}.");
            verifiedBytes += actual.Length;
        }
        return new PackageIntegrityVerification(
            true,
            declaredFingerprint,
            manifest.Files.Count,
            verifiedBytes,
            $"{manifest.Files.Count:N0} fichiers et {verifiedBytes:N0} octets vérifiés par SHA-256.");
    }

    public static void ValidatePortableTree(string root)
    {
        var resolved = ResolveRoot(root);
        var entries = Directory.EnumerateFileSystemEntries(resolved, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(resolved, path).Replace('\\', '/'))
            .ToArray();
        var errors = new List<string>();
        var collision = entries.GroupBy(PortableKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Distinct(StringComparer.Ordinal).Count() > 1);
        if (collision is not null)
            errors.Add($"collision de casse ou Unicode : {string.Join(" <> ", collision.Order(StringComparer.Ordinal))}");

        foreach (var relative in entries)
        {
            foreach (var segment in relative.Split('/'))
            {
                if (segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
                    errors.Add($"nom incompatible Windows : {relative}");
                if (segment.EndsWith(' ') || segment.EndsWith('.'))
                    errors.Add($"nom se terminant par un espace ou un point : {relative}");
                if (WindowsReservedNames.Contains(Path.GetFileNameWithoutExtension(segment)))
                    errors.Add($"nom réservé Windows : {relative}");
            }
            if (errors.Count >= 12) break;
        }
        if (errors.Count > 0)
            throw new PackageIntegrityException("Le contenu n'est pas portable entre Windows et Linux : " + string.Join("; ", errors.Distinct(StringComparer.Ordinal).Take(12)));
    }

    private static List<TextEdit> FindCaseEdits(
        string text,
        string textFile,
        IReadOnlyDictionary<string, Dictionary<string, List<ReferenceAlias>>> aliases,
        ISet<string> warnings)
    {
        var candidates = QuotedValueRegex().Matches(text).Select(match => match.Groups["value"])
            .Concat(AssignmentValueRegex().Matches(text).Select(match => match.Groups["value"]))
            .Where(group => group.Success)
            .GroupBy(group => (group.Index, group.Length))
            .Select(group => group.First())
            .ToArray();
        var edits = new List<TextEdit>();
        foreach (var group in candidates)
        {
            var original = group.Value;
            var leading = original.Length - original.TrimStart().Length;
            var trimmed = original.Trim();
            if (!LooksLikePathReference(trimmed)) continue;
            var resolution = ResolveReference(trimmed, textFile, aliases);
            if (resolution.Ambiguous)
            {
                warnings.Add($"{textFile} : référence de casse ambiguë « {trimmed} » non modifiée.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(resolution.Canonical) || resolution.Canonical.Equals(trimmed, StringComparison.Ordinal)) continue;
            var replacement = PreserveReferenceStyle(trimmed, resolution.Canonical);
            edits.Add(new TextEdit(group.Index + leading, trimmed.Length, trimmed, replacement));
        }
        return edits;
    }

    private static ReferenceResolution ResolveReference(
        string token,
        string textFile,
        IReadOnlyDictionary<string, Dictionary<string, List<ReferenceAlias>>> aliases)
    {
        var normalized = token.Replace('\\', '/');
        var prefix = normalized.StartsWith("./", StringComparison.Ordinal) ? "./" : string.Empty;
        normalized = normalized.TrimStart('.').TrimStart('/');
        var scope = ScopeOf(textFile);
        foreach (var candidateScope in new[] { scope, string.Empty }.Distinct(StringComparer.Ordinal))
        {
            if (!aliases.TryGetValue(candidateScope, out var byAlias) || !byAlias.TryGetValue(normalized, out var matches)) continue;
            var unique = matches.DistinctBy(match => (match.TargetPath, match.CanonicalAlias)).ToArray();
            var exact = unique.FirstOrDefault(match => match.CanonicalAlias.Equals(normalized, StringComparison.Ordinal));
            if (exact is not null) return new ReferenceResolution(prefix + exact.CanonicalAlias, false);
            if (unique.Length == 1) return new ReferenceResolution(prefix + unique[0].CanonicalAlias, false);
            return new ReferenceResolution(string.Empty, true);
        }
        return new ReferenceResolution(string.Empty, false);
    }

    private static IReadOnlyDictionary<string, Dictionary<string, List<ReferenceAlias>>> BuildAliasIndex(IEnumerable<ReferenceTarget> targets)
    {
        var result = new Dictionary<string, Dictionary<string, List<ReferenceAlias>>>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            foreach (var (scope, alias) in CreateAliases(target))
            {
                if (!result.TryGetValue(scope, out var byAlias))
                {
                    byAlias = new Dictionary<string, List<ReferenceAlias>>(StringComparer.OrdinalIgnoreCase);
                    result[scope] = byAlias;
                }
                if (!byAlias.TryGetValue(alias, out var values))
                {
                    values = [];
                    byAlias[alias] = values;
                }
                values.Add(new ReferenceAlias(target.RelativePath, alias));
            }
        }
        return result;
    }

    private static IEnumerable<(string Scope, string Alias)> CreateAliases(ReferenceTarget target)
    {
        var aliases = new HashSet<(string Scope, string Alias)>();
        Add(target.Scope, target.RelativePath);
        Add(string.Empty, target.RelativePath);
        var scoped = StripScope(target.RelativePath, target.Scope);
        Add(target.Scope, scoped);
        var mediaIndex = scoped.IndexOf("media/", StringComparison.OrdinalIgnoreCase);
        if (mediaIndex >= 0)
        {
            var media = scoped[mediaIndex..];
            Add(target.Scope, media);
            var belowMedia = media["media/".Length..];
            Add(target.Scope, belowMedia);
            foreach (var root in new[] { "models/", "models_X/", "textures/", "scripts/", "lua/", "sound/", "anims/" })
                if (belowMedia.StartsWith(root, StringComparison.OrdinalIgnoreCase)) Add(target.Scope, belowMedia[root.Length..]);
        }
        Add(target.Scope, Path.GetFileName(target.RelativePath));
        return aliases;

        void Add(string scope, string alias)
        {
            alias = alias.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(alias)) return;
            aliases.Add((scope, alias));
            var extension = Path.GetExtension(alias);
            if (ReferenceExtensions.Contains(extension) && alias.Contains('/'))
                aliases.Add((scope, alias[..^extension.Length]));
        }
    }

    private static bool LooksLikePathReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(value)) return false;
        if (value.Split(['/', '\\']).Any(segment => segment == "..")) return false;
        return value.Contains('/') || value.Contains('\\') || ReferenceExtensions.Contains(Path.GetExtension(value));
    }

    private static string PreserveReferenceStyle(string original, string canonical)
    {
        if (original.Contains('\\') && !original.Contains('/')) canonical = canonical.Replace('/', '\\');
        return canonical;
    }

    private static string ScopeOf(string relativePath)
    {
        var first = relativePath.Replace('\\', '/').Split('/', 2)[0];
        return first.Equals("common", StringComparison.OrdinalIgnoreCase) || VersionFolderRegex().IsMatch(first)
            ? first
            : string.Empty;
    }

    private static string StripScope(string relativePath, string scope) =>
        string.IsNullOrWhiteSpace(scope) ? relativePath : relativePath[(scope.Length + 1)..];

    private static string ComputeTreeFingerprint(IEnumerable<PackageIntegrityFile> files)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var number = new byte[8];
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            aggregate.AppendData(Encoding.UTF8.GetBytes(file.Path));
            aggregate.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(number, file.Bytes);
            aggregate.AppendData(number);
            aggregate.AppendData(Convert.FromHexString(file.Sha256));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<ContentFile> EnumerateFiles(string root, bool topLevelOnly = false)
    {
        var files = Directory.EnumerateFiles(root, "*", topLevelOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new ContentFile(path, Path.GetRelativePath(root, path).Replace('\\', '/'), info.Length);
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        return files;
    }

    private static string ResolveRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Le dossier de contenu est requis.", nameof(root));
        var resolved = Path.GetFullPath(root);
        if (!Directory.Exists(resolved)) throw new DirectoryNotFoundException($"Dossier de contenu introuvable : {resolved}");
        return resolved;
    }

    private static string PortableKey(string relativePath) => relativePath.Replace('\\', '/').Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static IReadOnlyList<PackageIntegrityFile>? TryReusePreviousFiles(
        IReadOnlyCollection<PublishedFile> actual,
        PackageIntegrityManifest? previous)
    {
        if (previous is null) return null;
        var byPath = previous.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var reused = new List<PackageIntegrityFile>(actual.Count);
        foreach (var file in actual)
        {
            if (!byPath.TryGetValue(file.PublishedPath, out var existing) || existing.Bytes != file.Source.Length) return null;
            reused.Add(existing);
        }
        return reused.Count == actual.Count ? reused : null;
    }

    private static string NormalizePrefix(string prefix) => prefix.Replace('\\', '/').Trim('/');

    private static string CombinePublishedPath(string prefix, string relative) =>
        string.IsNullOrWhiteSpace(prefix) ? relative : $"{prefix}/{relative}";

    private static string FormatPaths(IEnumerable<string> paths)
    {
        var values = paths.ToArray();
        return values.Length == 0 ? "aucun" : string.Join(", ", values);
    }

    private static void AtomicWriteUtf8(string path, string content, bool includeBom)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        var existed = File.Exists(path);
        var originalAttributes = existed ? File.GetAttributes(path) : FileAttributes.Normal;
        try
        {
            var payload = Utf8WithoutBom.GetBytes(content);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (includeBom) stream.Write(Encoding.UTF8.Preamble);
                stream.Write(payload);
                stream.Flush(true);
            }
            if (existed && (originalAttributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, originalAttributes & ~FileAttributes.ReadOnly);
            File.Move(temporary, path, true);
            if (existed) File.SetAttributes(path, originalAttributes);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [GeneratedRegex("(?<quote>[\\\"'])(?<value>[^\\\"'\\r\\n]{2,512})\\k<quote>", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValueRegex();

    [GeneratedRegex("(?im)\\b(?:mesh|texture|file|path|image|model|xml|lua|script|sound|animation|animationsMesh)\\s*=\\s*(?<value>[^,\\r\\n}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentValueRegex();

    [GeneratedRegex("^\\d+(?:\\.\\d+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolderRegex();

    private sealed record ContentFile(string FullPath, string RelativePath, long Length);
    private sealed record PublishedFile(ContentFile Source, string PublishedPath);
    private sealed record ReferenceTarget(string RelativePath, string Scope);
    private sealed record ReferenceAlias(string TargetPath, string CanonicalAlias);
    private sealed record ReferenceResolution(string Canonical, bool Ambiguous);
    private sealed record TextEdit(int Start, int Length, string Original, string Replacement);
}
