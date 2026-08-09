using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Packaging;

public sealed class PackageBuildService(ApplicationPaths paths, PackageValidator validator)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public PackageBuildResult Build(PackageProject project)
    {
        var finalRoot = EnsureScopedBuildPath(project.Id);
        var preview = InspectPreview(project);
        var desiredComponents = CreateDesiredComponents(project);
        var previousState = LoadPreviousState(finalRoot, project, desiredComponents);
        PrimeValidationCache(project, previousState);
        var validation = validator.Validate(project);
        if (!validation.CanBuild)
            throw new PackageBuildException("Le projet contient des erreurs qui empêchent sa construction.", validation);

        var contentFingerprint = ComputeContentFingerprint(project, desiredComponents);
        var buildFingerprint = ComputeBuildFingerprint(project, desiredComponents, contentFingerprint, preview.Token);
        if (CanReuseCompletedBuild(finalRoot, previousState, desiredComponents, buildFingerprint, preview.Extension))
            return CreateResult(
                project,
                validation,
                finalRoot,
                preview.Extension,
                contentFingerprint,
                new CopyStatistics(),
                previousState!.Components,
                rebuiltComponents: 0,
                removedComponents: 0,
                isIncremental: true,
                isNoOp: true);

        var nextRoot = finalRoot + ".next";
        SafeFileTree.DeleteScopedDirectory(paths.BuildsRoot, nextRoot);
        Directory.CreateDirectory(nextRoot);
        var contentsRoot = Path.Combine(nextRoot, "Contents");
        var modsRoot = Path.Combine(contentsRoot, "mods");
        Directory.CreateDirectory(modsRoot);

        try
        {
            var reusableComponents = FindReusableComponents(finalRoot, previousState, desiredComponents);
            var rebuiltComponents = new List<IncrementalBuildComponent>();
            var copied = new CopyStatistics();
            foreach (var desired in desiredComponents.Where(component => !reusableComponents.ContainsKey(component.Key)))
            {
                var componentStats = MaterializeComponent(project, desired, modsRoot, validation);
                copied.Add(componentStats);
                rebuiltComponents.Add(ToBuildComponent(
                    desired,
                    componentStats,
                    SafeFileTree.ComputeDirectoryMetadataStamp(Path.Combine(modsRoot, desired.DestinationFolder))));
            }

            if (!validation.CanBuild)
                throw new PackageBuildException("La fusion a détecté des collisions incompatibles.", validation);

            var allComponents = desiredComponents
                .Select(desired => reusableComponents.TryGetValue(desired.Key, out var reusable)
                    ? reusable
                    : rebuiltComponents.Single(component => component.Key.Equals(desired.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var description = WorkshopDescriptionGenerator.Generate(project);
            WritePublicManifest(project, contentsRoot);
            var publicManifestHash = ComputeHash(Path.Combine(contentsRoot, "pzasm-pack-manifest.json"));
            var previewPath = PreparePreview(project, nextRoot, preview.Extension);
            var finalPreviewPath = Path.Combine(finalRoot, Path.GetFileName(previewPath));
            var workshopPath = Path.Combine(nextRoot, "workshop.txt");
            File.WriteAllText(workshopPath, GenerateWorkshopTxt(project, description), new UTF8Encoding(false));
            var vdfPath = Path.Combine(nextRoot, "steamcmd-item.vdf");
            File.WriteAllText(vdfPath, GenerateSteamCmdVdf(
                project,
                Path.Combine(finalRoot, "Contents"),
                finalPreviewPath,
                description), new UTF8Encoding(false));
            var serverSnippetPath = Path.Combine(nextRoot, "server-config.txt");
            File.WriteAllText(serverSnippetPath, GenerateServerConfig(project), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(nextRoot, "README-BUILD.txt"), GenerateBuildReadme(project, validation), new UTF8Encoding(false));

            var lockPath = Path.Combine(nextRoot, "pack.lock.json");
            var lockData = CreateBuildState(project, buildFingerprint, publicManifestHash, allComponents);
            File.WriteAllText(lockPath, JsonSerializer.Serialize(lockData, JsonOptions), new UTF8Encoding(false));

            var localSnapshot = new
            {
                warning = "Copie locale du projet. Peut contenir les chemins locaux de preuves privées; ce fichier n'est pas placé dans Contents et ne sera pas publié.",
                project
            };
            File.WriteAllText(Path.Combine(nextRoot, "project.snapshot.json"), JsonSerializer.Serialize(localSnapshot, JsonOptions), new UTF8Encoding(false));

            var desiredFolders = desiredComponents.Select(component => component.DestinationFolder).ToHashSet(PathComparer);
            var changedFolders = rebuiltComponents.Select(component => component.DestinationFolder).ToHashSet(PathComparer);
            var removedComponents = CommitIncrementalBuild(finalRoot, nextRoot, desiredFolders, changedFolders, preview.Extension);
            ProtectHardLinkedPayload(finalRoot, rebuiltComponents.Where(component => component.HardLinkedFiles > 0).Select(component => component.DestinationFolder));
            project.LastBuiltAt = DateTimeOffset.UtcNow;

            return CreateResult(
                project,
                validation,
                finalRoot,
                preview.Extension,
                contentFingerprint,
                copied,
                reusableComponents.Values,
                rebuiltComponents.Count,
                removedComponents,
                isIncremental: previousState is not null,
                isNoOp: false);
        }
        catch
        {
            SafeFileTree.DeleteScopedDirectory(paths.BuildsRoot, nextRoot);
            throw;
        }
    }

    private static CopyStatistics MaterializeComponent(PackageProject project, DesiredBuildComponent desired, string modsRoot, PackageValidationResult validation)
    {
        var stats = new CopyStatistics();
        var destination = Path.Combine(modsRoot, desired.DestinationFolder);
        switch (desired.Kind)
        {
            case "bundle-mod":
                CopyTree(desired.SourceRoot, destination, stats, desired.PreferHardLinks);
                break;
            case "notice":
                NoticeModGenerator.GenerateStandalone(modsRoot, project);
                stats.Measure(destination);
                break;
            case "control":
                ControlModGenerator.GenerateStandalone(modsRoot, project);
                stats.Measure(destination);
                break;
            case "fusion":
                stats = BuildFusion(project, modsRoot, validation);
                break;
            default:
                throw new InvalidOperationException($"Composant de build inconnu : {desired.Kind}");
        }
        return stats;
    }

    private static void ProtectHardLinkedPayload(string buildRoot, IEnumerable<string> folders)
    {
        foreach (var folder in folders.Distinct(PathComparer))
        {
            var modRoot = Path.Combine(buildRoot, "Contents", "mods", folder);
            if (!Directory.Exists(modRoot)) continue;
            foreach (var file in Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }
    }

    private static CopyStatistics BuildFusion(PackageProject project, string modsRoot, PackageValidationResult validation)
    {
        var stats = new CopyStatistics();
        var fusionRoot = Path.Combine(modsRoot, project.FusionModId);
        Directory.CreateDirectory(fusionRoot);
        NoticeModGenerator.WriteManifest(fusionRoot, project.FusionModId, project.Name, project.Description);

        var outputs = new Dictionary<string, FusionCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order).ThenBy(x => x.Name))
        {
            foreach (var mediaRoot in PzVersionSelector.GetEffectiveMediaRoots(mod.BuildSourceRoot, mod.SelectedVersionFolder))
            {
                foreach (var file in Directory.EnumerateFiles(mediaRoot, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(mediaRoot, file);
                    var hash = ComputeHash(file);
                    if (outputs.TryGetValue(relative, out var previous))
                    {
                        if (previous.ModReferenceId == mod.Id)
                        {
                            outputs[relative] = new FusionCandidate(mod.Id, mod.Name, file, hash);
                            continue;
                        }
                        if (!hash.Equals(previous.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            validation.Issues.Add(new(
                                "FUSION_COLLISION",
                                $"Collision non identique sur media/{relative} entre « {previous.ModName} » et « {mod.Name} ». La fusion stricte refuse de choisir silencieusement.",
                                true,
                                mod.Id));
                        }
                        continue;
                    }
                    outputs[relative] = new FusionCandidate(mod.Id, mod.Name, file, hash);
                }
            }
        }

        if (!validation.CanBuild) return stats;
        var destinationMedia = Path.Combine(fusionRoot, "common", "media");
        foreach (var (relative, candidate) in outputs)
            CopyFile(candidate.SourceFile, Path.Combine(destinationMedia, relative), stats);
        if (project.InjectConnectionNotice)
            NoticeModGenerator.InjectIntoFusion(fusionRoot, project);
        if (project.InjectInGameControl)
            ControlModGenerator.InjectIntoFusion(fusionRoot, project);
        return stats;
    }

    private static PreviewDescriptor InspectPreview(PackageProject project)
    {
        if (string.IsNullOrWhiteSpace(project.PreviewImagePath))
            return new PreviewDescriptor(".png", "default-workshop-preview-v1");
        if (!File.Exists(project.PreviewImagePath))
            throw new FileNotFoundException("L'image Workshop personnalisée est introuvable.", project.PreviewImagePath);
        var extension = WorkshopPreviewFile.Validate(project.PreviewImagePath);
        return new PreviewDescriptor(extension, ComputeHash(project.PreviewImagePath));
    }

    private static string PreparePreview(PackageProject project, string buildRoot, string extension)
    {
        if (!string.IsNullOrWhiteSpace(project.PreviewImagePath))
        {
            var customTarget = Path.Combine(buildRoot, "preview" + extension);
            File.Copy(project.PreviewImagePath, customTarget, true);
            return customTarget;
        }

        var target = Path.Combine(buildRoot, "preview.png");
        using var source = typeof(PackageBuildService).Assembly.GetManifestResourceStream("PZAdvancedServerManager.Core.Assets.default-workshop-preview.png")
            ?? throw new InvalidOperationException("La preview Workshop générée est absente de l'application.");
        using var output = File.Create(target);
        source.CopyTo(output);
        return target;
    }

    private static string GenerateWorkshopTxt(PackageProject project, string description)
    {
        var visibility = project.Visibility.ToString().ToLowerInvariant();
        return $"version=1\nid={project.PublishedWorkshopId}\ntitle={CleanLine(project.Name)}\ndescription={CleanLine(description)}\ntags={string.Join(';', project.Tags)}\nvisibility={visibility}\n";
    }

    private static string GenerateSteamCmdVdf(PackageProject project, string contentsRoot, string previewPath, string description)
    {
        static string Vdf(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        return $$"""
"workshopitem"
{
    "appid"             "108600"
    "publishedfileid"   "{{project.PublishedWorkshopId}}"
    "contentfolder"     "{{Vdf(Path.GetFullPath(contentsRoot))}}"
    "previewfile"       "{{Vdf(Path.GetFullPath(previewPath))}}"
    "visibility"        "{{(int)project.Visibility}}"
    "title"             "{{Vdf(project.Name)}}"
    "description"       "{{Vdf(description)}}"
    "changenote"        "Mise à jour gérée par PZ Advanced Server Manager — {{DateTimeOffset.Now:yyyy-MM-dd HH:mm}}"
}
""";
    }

    private static string GenerateServerConfig(PackageProject project)
    {
        var modIds = project.Mode == PackageMode.Bundle
            ? project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order).Select(x => x.ModId).ToList()
            : [project.FusionModId];
        if (project.InjectConnectionNotice && project.Mode == PackageMode.Bundle) modIds.Add(project.NoticeModId);
        if (project.InjectInGameControl && project.Mode == PackageMode.Bundle) modIds.Add(project.ControlModId);

        var maps = project.MapOrder.Count > 0
            ? project.MapOrder.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : project.Mods.Where(x => x.Enabled).SelectMany(x => x.MapFolders.Length > 0 ? x.MapFolders : DiscoverMapFolders(x.BuildSourceRoot, x.SelectedVersionFolder)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!maps.Contains("Muldraugh, KY", StringComparer.OrdinalIgnoreCase)) maps.Add("Muldraugh, KY");
        var workshopId = project.PublishedWorkshopId == 0 ? "<WORKSHOP_ID_APRES_PREMIERE_PUBLICATION>" : project.PublishedWorkshopId.ToString();
        return $"# Un seul Workshop item est contrôlé/versionné par le serveur.\nWorkshopItems={workshopId}\nMods={string.Join(';', modIds)}\nMap={string.Join(';', maps)}\n";
    }

    private static IEnumerable<string> DiscoverMapFolders(string modRoot, string selectedFolder) =>
        PzVersionSelector.GetEffectiveMediaRoots(modRoot, selectedFolder)
            .Select(x => Path.Combine(x, "maps"))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>();

    private static string GenerateBuildReadme(PackageProject project, PackageValidationResult validation) => $"""
PZ Advanced Server Manager — build de « {project.Name} »

Contents\               contenu envoyé au Workshop
workshop.txt             descripteur Project Zomboid
steamcmd-item.vdf        fichier de publication SteamCMD
server-config.txt        lignes à appliquer au fichier serveur .ini
pack.lock.json           inventaire et empreintes du build
project.snapshot.json    sauvegarde locale, jamais publiée par le VDF

OPTIMISATION LOCALE : en mode Bundle, les fichiers des mods peuvent être matérialisés par des liens physiques vers les instantanés figés du manager. Pour SteamCMD et Project Zomboid, ce sont des fichiers ordinaires. N'éditez pas le contenu des mods directement dans ce dossier généré : effectuez les changements ou mises à jour depuis le manager, puis reconstruisez le pack.

AVERTISSEMENT : LemonCorp et les développeurs de PZ Advanced Server Manager ne sont pas responsables des packs créés. L'utilisateur doit obtenir les autorisations de redistribution de chaque auteur. Une visibilité « non listée » ou un usage serveur ne dispense pas de ces autorisations.

Publication autorisée par le validateur : {(validation.CanPublish ? "oui" : "non")}
Mode : {project.Mode}
Workshop ID : {(project.PublishedWorkshopId == 0 ? "nouvel item" : project.PublishedWorkshopId)}
""";

    private static List<DesiredBuildComponent> CreateDesiredComponents(PackageProject project)
    {
        var enabled = project.Mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Order).ThenBy(mod => mod.Name).ToArray();
        var components = new List<DesiredBuildComponent>();
        if (project.Mode == PackageMode.Bundle)
        {
            foreach (var mod in enabled)
            {
                var sourceHash = ResolveContentHash(mod);
                var canLink = Directory.Exists(mod.PinnedSourceRoot) &&
                              Path.GetFullPath(mod.BuildSourceRoot).Equals(Path.GetFullPath(mod.PinnedSourceRoot), PathComparison) &&
                              !string.IsNullOrWhiteSpace(mod.PinnedContentHash);
                components.Add(new DesiredBuildComponent(
                    $"mod:{mod.Id:N}",
                    "bundle-mod",
                    mod.Id,
                    mod.ModId,
                    mod.EffectiveFolderName,
                    Fingerprint(new { engine = "bundle-mod-v2", sourceHash }),
                    sourceHash,
                    mod.BuildSourceRoot,
                    canLink));
            }
            if (project.InjectConnectionNotice)
                components.Add(new DesiredBuildComponent(
                    "generated:notice",
                    "notice",
                    null,
                    project.NoticeModId,
                    project.NoticeModId,
                    Fingerprint(new { engine = "notice-v3", metadata = GeneratedMetadata(project) }),
                    string.Empty,
                    string.Empty,
                    false));
            if (project.InjectInGameControl)
                components.Add(new DesiredBuildComponent(
                    "generated:control",
                    "control",
                    null,
                    project.ControlModId,
                    project.ControlModId,
                    Fingerprint(new { engine = "control-v3", metadata = GeneratedMetadata(project) }),
                    string.Empty,
                    string.Empty,
                    false));
        }
        else
        {
            var sourceHash = Fingerprint(enabled.Select(mod => new
            {
                mod.Id,
                mod.ModId,
                hash = ResolveContentHash(mod),
                mod.SelectedVersionFolder
            }).ToArray());
            components.Add(new DesiredBuildComponent(
                "generated:fusion",
                "fusion",
                null,
                project.FusionModId,
                project.FusionModId,
                Fingerprint(new { engine = "fusion-v3", sourceHash, metadata = GeneratedMetadata(project) }),
                sourceHash,
                string.Empty,
                false));
        }

        var duplicate = components.GroupBy(component => component.DestinationFolder, PathComparer).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Plusieurs composants produiraient le dossier « {duplicate.Key} ».");
        return components;
    }

    private static object GeneratedMetadata(PackageProject project) => new
    {
        project.Name,
        project.Description,
        project.NoticeTitle,
        project.PublishedWorkshopId,
        project.InjectConnectionNotice,
        project.InjectInGameControl,
        mods = project.Mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Order).ThenBy(mod => mod.Name).Select(mod => new
        {
            mod.ModId,
            mod.Name,
            mod.Author,
            mod.Version,
            mod.SelectedVersionFolder,
            mod.PinnedContentHash,
            mod.WorkshopId
        }).ToArray()
    };

    private static string ResolveContentHash(PackageModReference mod) =>
        !string.IsNullOrWhiteSpace(mod.PinnedContentHash)
            ? mod.PinnedContentHash
            : Directory.Exists(mod.BuildSourceRoot)
                ? SafeFileTree.ComputeDirectoryHash(mod.BuildSourceRoot)
                : string.Empty;

    private static string ComputeContentFingerprint(PackageProject project, IReadOnlyCollection<DesiredBuildComponent> components) => Fingerprint(new
    {
        engine = "workshop-content-v1",
        project.Id,
        project.Name,
        project.Description,
        project.Mode,
        project.TargetPzVersion,
        project.InjectConnectionNotice,
        project.InjectInGameControl,
        components = components.Select(component => new { component.Key, component.Kind, component.DestinationFolder, component.Fingerprint }).ToArray(),
        mods = project.Mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Order).ThenBy(mod => mod.Name).Select(mod => new
        {
            mod.WorkshopId,
            mod.ModId,
            mod.Name,
            mod.Author,
            mod.Version,
            mod.SelectedVersionFolder,
            mod.PinnedAt,
            mod.PinnedContentHash,
            mod.PinnedMetadataStamp,
            mod.SourceUrl,
            mod.IncludeInGlobalUpdates,
            mod.RequiredModIds,
            mod.MapFolders,
            permission = new
            {
                mod.Permission.Status,
                mod.Permission.RightsHolder,
                mod.Permission.PublicEvidenceUrl
            }
        }).ToArray()
    });

    private static string ComputeBuildFingerprint(
        PackageProject project,
        IReadOnlyCollection<DesiredBuildComponent> components,
        string contentFingerprint,
        string previewToken) => Fingerprint(new
        {
            engine = "incremental-build-v5",
            contentFingerprint,
            previewToken,
            project.PublishedWorkshopId,
            project.Visibility,
            project.Tags,
            project.MapOrder,
            project.LegalWarningAccepted,
            project.LegalWarningAcceptedAt,
            components = components.Select(component => new { component.Key, component.DestinationFolder, component.Fingerprint }).ToArray(),
            mods = project.Mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Order).ThenBy(mod => mod.Name).Select(mod => new
            {
                mod.Id,
                mod.Order,
                mod.SourceUrl,
                mod.IncludeInGlobalUpdates,
                permission = new
                {
                    mod.Permission.Status,
                    mod.Permission.RightsHolder,
                    mod.Permission.PublicEvidenceUrl,
                    mod.Permission.PrivateAttachmentPath,
                    mod.Permission.Notes,
                    mod.Permission.GrantedOn
                }
            }).ToArray()
        });

    private static IncrementalBuildState CreateBuildState(
        PackageProject project,
        string buildFingerprint,
        string publicManifestHash,
        List<IncrementalBuildComponent> components) => new()
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            Mode = project.Mode.ToString(),
            TargetPzVersion = project.TargetPzVersion,
            WorkshopId = project.PublishedWorkshopId,
            BuiltAt = DateTimeOffset.UtcNow,
            BuildFingerprint = buildFingerprint,
            PublicManifestHash = publicManifestHash,
            Components = components,
            Sources = project.Mods.Where(mod => mod.Enabled).OrderBy(mod => mod.Order).Select(mod => new IncrementalBuildSource
            {
                WorkshopId = mod.WorkshopId,
                ModId = mod.ModId,
                Name = mod.Name,
                Author = mod.Author,
                Version = mod.Version,
                SelectedVersionFolder = mod.SelectedVersionFolder,
                SourceUrl = mod.SourceUrl,
                PinnedAt = mod.PinnedAt,
                PinnedContentHash = mod.PinnedContentHash,
                PinnedMetadataStamp = mod.PinnedMetadataStamp,
                IncludeInGlobalUpdates = mod.IncludeInGlobalUpdates,
                PermissionStatus = mod.Permission.Status.ToString()
            }).ToList(),
            Totals = new IncrementalBuildTotals
            {
                Files = components.Sum(component => component.Files),
                Bytes = components.Sum(component => component.Bytes),
                HardLinkedFiles = components.Sum(component => component.HardLinkedFiles),
                HardLinkedBytes = components.Sum(component => component.HardLinkedBytes)
            }
        };

    private static IncrementalBuildComponent ToBuildComponent(DesiredBuildComponent desired, CopyStatistics stats, string metadataStamp) => new()
    {
        Key = desired.Key,
        Kind = desired.Kind,
        ModReferenceId = desired.ModReferenceId,
        ModId = desired.ModId,
        DestinationFolder = desired.DestinationFolder,
        Fingerprint = desired.Fingerprint,
        SourceContentHash = desired.SourceContentHash,
        MetadataStamp = metadataStamp,
        Files = stats.Files,
        Bytes = stats.Bytes,
        HardLinkedFiles = stats.HardLinkedFiles,
        HardLinkedBytes = stats.HardLinkedBytes,
        StatisticsComplete = true
    };

    private static IncrementalBuildState? LoadPreviousState(string finalRoot, PackageProject project, IReadOnlyCollection<DesiredBuildComponent> desiredComponents)
    {
        var lockPath = Path.Combine(finalRoot, "pack.lock.json");
        if (!File.Exists(lockPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaElement)) return null;
            var schemaVersion = schemaElement.GetInt32();
            if (schemaVersion >= 3)
            {
                var state = document.RootElement.Deserialize<IncrementalBuildState>(JsonOptions);
                if (state?.ProjectId != project.Id) return null;
                var repairedState = false;
                foreach (var component in state.Components)
                {
                    var componentRoot = Path.Combine(finalRoot, "Contents", "mods", component.DestinationFolder);
                    if (!Directory.Exists(componentRoot)) continue;
                    if (!component.StatisticsComplete)
                    {
                        var statistics = MeasureDirectory(componentRoot);
                        component.Files = statistics.Files;
                        component.Bytes = statistics.Bytes;
                        component.StatisticsComplete = true;
                        repairedState = true;
                    }
                    if (string.IsNullOrWhiteSpace(component.MetadataStamp))
                    {
                        component.MetadataStamp = SafeFileTree.ComputeDirectoryMetadataStamp(componentRoot);
                        repairedState = true;
                    }
                }
                if (repairedState) state.BuildFingerprint = string.Empty;
                return state;
            }
            if (schemaVersion != 2 || project.Mode != PackageMode.Bundle) return null;
            return MigrateVersionTwoState(document.RootElement, finalRoot, project, desiredComponents);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IncrementalBuildState MigrateVersionTwoState(JsonElement root, string finalRoot, PackageProject project, IReadOnlyCollection<DesiredBuildComponent> desiredComponents)
    {
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<IncrementalBuildSource>();
        if (root.TryGetProperty("sources", out var sourceElements))
        {
            foreach (var source in sourceElements.EnumerateArray())
            {
                var modId = ReadString(source, "modId");
                var pinnedHash = ReadString(source, "pinnedContentHash");
                if (!string.IsNullOrWhiteSpace(modId)) sourceHashes[modId] = pinnedHash;
                sources.Add(new IncrementalBuildSource
                {
                    WorkshopId = source.TryGetProperty("workshopId", out var workshopId) && workshopId.TryGetUInt64(out var parsedWorkshopId) ? parsedWorkshopId : 0,
                    ModId = modId,
                    Name = ReadString(source, "name"),
                    Author = ReadString(source, "author"),
                    Version = ReadString(source, "version"),
                    SelectedVersionFolder = ReadString(source, "selectedVersionFolder"),
                    SourceUrl = ReadString(source, "sourceUrl"),
                    PinnedContentHash = pinnedHash,
                    PinnedMetadataStamp = ReadString(source, "pinnedMetadataStamp"),
                    IncludeInGlobalUpdates = !source.TryGetProperty("includeInGlobalUpdates", out var includeUpdates) || includeUpdates.GetBoolean(),
                    PermissionStatus = ReadString(source, "permissionStatus")
                });
            }
        }

        var fileTotals = new Dictionary<string, (int Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var folderTotals = new Dictionary<string, (int Files, long Bytes)>(PathComparer);
        if (root.TryGetProperty("files", out var fileElements))
        {
            foreach (var file in fileElements.EnumerateArray())
            {
                var bytes = file.TryGetProperty("bytes", out var byteElement) ? byteElement.GetInt64() : 0;
                var path = ReadString(file, "path");
                if (path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                {
                    var remainder = path[5..];
                    var separator = remainder.IndexOf('/');
                    if (separator > 0)
                    {
                        var folder = remainder[..separator];
                        var folderCurrent = folderTotals.GetValueOrDefault(folder);
                        folderTotals[folder] = (folderCurrent.Files + 1, folderCurrent.Bytes + bytes);
                    }
                }
                var sourceModId = ReadString(file, "sourceModId");
                if (string.IsNullOrWhiteSpace(sourceModId)) continue;
                var current = fileTotals.GetValueOrDefault(sourceModId);
                fileTotals[sourceModId] = (current.Files + 1, current.Bytes + bytes);
            }
        }

        var components = new List<IncrementalBuildComponent>();
        foreach (var desired in desiredComponents.Where(component => component.Kind == "bundle-mod"))
        {
            var componentRoot = Path.Combine(finalRoot, "Contents", "mods", desired.DestinationFolder);
            if (!sourceHashes.TryGetValue(desired.ModId, out var sourceHash) ||
                !sourceHash.Equals(desired.SourceContentHash, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(componentRoot))
                continue;
            var totals = folderTotals.GetValueOrDefault(desired.DestinationFolder);
            var hardLinkedTotals = fileTotals.GetValueOrDefault(desired.ModId);
            components.Add(new IncrementalBuildComponent
            {
                Key = desired.Key,
                Kind = desired.Kind,
                ModReferenceId = desired.ModReferenceId,
                ModId = desired.ModId,
                DestinationFolder = desired.DestinationFolder,
                Fingerprint = desired.Fingerprint,
                SourceContentHash = desired.SourceContentHash,
                MetadataStamp = SafeFileTree.ComputeDirectoryMetadataStamp(componentRoot),
                Files = totals.Files,
                Bytes = totals.Bytes,
                HardLinkedFiles = hardLinkedTotals.Files,
                HardLinkedBytes = hardLinkedTotals.Bytes,
                StatisticsComplete = true
            });
        }

        return new IncrementalBuildState
        {
            SchemaVersion = 5,
            ProjectId = project.Id,
            ProjectName = project.Name,
            Mode = project.Mode.ToString(),
            TargetPzVersion = project.TargetPzVersion,
            WorkshopId = project.PublishedWorkshopId,
            Components = components,
            Sources = sources,
            Totals = new IncrementalBuildTotals
            {
                Files = components.Sum(component => component.Files),
                Bytes = components.Sum(component => component.Bytes),
                HardLinkedFiles = components.Sum(component => component.HardLinkedFiles),
                HardLinkedBytes = components.Sum(component => component.HardLinkedBytes)
            }
        };
    }

    private static void PrimeValidationCache(PackageProject project, IncrementalBuildState? previousState)
    {
        if (previousState is null) return;
        foreach (var mod in project.Mods.Where(mod => mod.Enabled && !string.IsNullOrWhiteSpace(mod.PinnedContentHash)))
        {
            var source = previousState.Sources.FirstOrDefault(candidate => candidate.ModId.Equals(mod.ModId, StringComparison.OrdinalIgnoreCase));
            if (source is null || !source.PinnedContentHash.Equals(mod.PinnedContentHash, StringComparison.OrdinalIgnoreCase)) continue;
            mod.ValidatedContentHash = mod.PinnedContentHash;
            mod.ForbiddenFiles = [];
        }
    }

    private static Dictionary<string, IncrementalBuildComponent> FindReusableComponents(
        string finalRoot,
        IncrementalBuildState? previousState,
        IEnumerable<DesiredBuildComponent> desiredComponents)
    {
        var reusable = new Dictionary<string, IncrementalBuildComponent>(StringComparer.OrdinalIgnoreCase);
        if (previousState is null) return reusable;
        var previousByKey = previousState.Components.ToDictionary(component => component.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var desired in desiredComponents)
        {
            if (!previousByKey.TryGetValue(desired.Key, out var previous) ||
                !previous.Fingerprint.Equals(desired.Fingerprint, StringComparison.OrdinalIgnoreCase) ||
                !previous.DestinationFolder.Equals(desired.DestinationFolder, PathComparison) ||
                string.IsNullOrWhiteSpace(previous.MetadataStamp))
                continue;
            var componentRoot = Path.Combine(finalRoot, "Contents", "mods", desired.DestinationFolder);
            if (!Directory.Exists(componentRoot))
                continue;
            if (!SafeFileTree.ComputeDirectoryMetadataStamp(componentRoot).Equals(previous.MetadataStamp, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(desired.SourceRoot) && Directory.Exists(desired.SourceRoot) &&
                    !SafeFileTree.ComputeDirectoryHash(desired.SourceRoot).Equals(desired.SourceContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Le snapshot figé du composant « {desired.ModId} » a été modifié hors de PZASM. Actualisez explicitement les sources avant de reconstruire ou publier.");
                continue;
            }
            reusable[desired.Key] = previous;
        }
        return reusable;
    }

    private static bool CanReuseCompletedBuild(
        string finalRoot,
        IncrementalBuildState? previousState,
        IReadOnlyCollection<DesiredBuildComponent> desiredComponents,
        string buildFingerprint,
        string previewExtension)
    {
        if (previousState is null || previousState.SchemaVersion < 3 ||
            !previousState.BuildFingerprint.Equals(buildFingerprint, StringComparison.OrdinalIgnoreCase))
            return false;
        var publicManifestPath = Path.Combine(finalRoot, "Contents", "pzasm-pack-manifest.json");
        if (string.IsNullOrWhiteSpace(previousState.PublicManifestHash) ||
            !File.Exists(publicManifestPath) ||
            !ComputeHash(publicManifestPath).Equals(previousState.PublicManifestHash, StringComparison.OrdinalIgnoreCase))
            return false;
        var reusable = FindReusableComponents(finalRoot, previousState, desiredComponents);
        if (reusable.Count != desiredComponents.Count) return false;
        var modsRoot = Path.Combine(finalRoot, "Contents", "mods");
        if (!Directory.Exists(modsRoot)) return false;
        var actualFolders = Directory.EnumerateDirectories(modsRoot).Select(Path.GetFileName).Where(name => name is not null).Cast<string>().ToHashSet(PathComparer);
        var desiredFolders = desiredComponents.Select(component => component.DestinationFolder).ToHashSet(PathComparer);
        if (!actualFolders.SetEquals(desiredFolders)) return false;
        return RequiredBuildFiles(previewExtension).All(relative => File.Exists(Path.Combine(finalRoot, relative)));
    }

    private static PackageBuildResult CreateResult(
        PackageProject project,
        PackageValidationResult validation,
        string finalRoot,
        string previewExtension,
        string contentFingerprint,
        CopyStatistics copied,
        IEnumerable<IncrementalBuildComponent> reusedComponents,
        int rebuiltComponents,
        int removedComponents,
        bool isIncremental,
        bool isNoOp)
    {
        var reused = reusedComponents.ToArray();
        return new PackageBuildResult
        {
            BuildRoot = finalRoot,
            WorkshopContentRoot = Path.Combine(finalRoot, "Contents"),
            WorkshopDescriptorPath = Path.Combine(finalRoot, "workshop.txt"),
            WorkshopPreviewPath = Path.Combine(finalRoot, "preview" + previewExtension),
            SteamCmdVdfPath = Path.Combine(finalRoot, "steamcmd-item.vdf"),
            LockFilePath = Path.Combine(finalRoot, "pack.lock.json"),
            ServerConfigSnippetPath = Path.Combine(finalRoot, "server-config.txt"),
            Validation = validation,
            ContentFingerprint = contentFingerprint,
            CopiedFiles = copied.Files,
            CopiedBytes = copied.Bytes,
            HardLinkedFiles = copied.HardLinkedFiles,
            HardLinkedBytes = copied.HardLinkedBytes,
            ReusedFiles = reused.Sum(component => component.Files),
            ReusedBytes = reused.Sum(component => component.Bytes),
            RebuiltComponents = rebuiltComponents,
            ReusedComponents = reused.Length,
            RemovedComponents = removedComponents,
            IsIncremental = isIncremental,
            IsNoOp = isNoOp
        };
    }

    private int CommitIncrementalBuild(
        string finalRoot,
        string stagedRoot,
        IReadOnlySet<string> desiredFolders,
        IReadOnlySet<string> changedFolders,
        string previewExtension)
    {
        var backupRoot = finalRoot + $".previous-{Guid.NewGuid():N}";
        Directory.CreateDirectory(finalRoot);
        Directory.CreateDirectory(Path.Combine(finalRoot, "Contents", "mods"));
        Directory.CreateDirectory(backupRoot);
        var backups = new List<(string Destination, string Backup)>();
        var installed = new List<(string Staged, string Destination)>();
        var removedComponents = 0;
        try
        {
            var finalModsRoot = Path.Combine(finalRoot, "Contents", "mods");
            foreach (var directory in Directory.EnumerateDirectories(finalModsRoot).ToArray())
            {
                var folder = Path.GetFileName(directory);
                if (desiredFolders.Contains(folder) && !changedFolders.Contains(folder)) continue;
                if (!desiredFolders.Contains(folder)) removedComponents++;
                BackupEntry(finalRoot, backupRoot, directory, backups);
            }

            var stagedModsRoot = Path.Combine(stagedRoot, "Contents", "mods");
            foreach (var folder in changedFolders)
            {
                var staged = Path.Combine(stagedModsRoot, folder);
                if (!Directory.Exists(staged)) throw new DirectoryNotFoundException($"Composant préparé introuvable : {staged}");
                var destination = Path.Combine(finalModsRoot, folder);
                MoveEntry(staged, destination);
                installed.Add((staged, destination));
            }

            foreach (var oldPreview in Directory.EnumerateFiles(finalRoot, "preview.*", SearchOption.TopDirectoryOnly)
                         .Where(path => !Path.GetFileName(path).Equals("preview" + previewExtension, PathComparison)).ToArray())
                BackupEntry(finalRoot, backupRoot, oldPreview, backups);

            foreach (var relative in RequiredBuildFiles(previewExtension))
            {
                var staged = Path.Combine(stagedRoot, relative);
                var destination = Path.Combine(finalRoot, relative);
                if (!File.Exists(staged)) throw new FileNotFoundException("Fichier de build préparé introuvable.", staged);
                if (File.Exists(destination) && FilesEqual(staged, destination))
                {
                    File.Delete(staged);
                    continue;
                }
                if (File.Exists(destination)) BackupEntry(finalRoot, backupRoot, destination, backups);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                MoveEntry(staged, destination);
                installed.Add((staged, destination));
            }

            SafeFileTree.DeleteScopedDirectory(paths.BuildsRoot, backupRoot);
            SafeFileTree.DeleteScopedDirectory(paths.BuildsRoot, stagedRoot);
            return removedComponents;
        }
        catch
        {
            foreach (var entry in installed.AsEnumerable().Reverse())
            {
                if (!File.Exists(entry.Destination) && !Directory.Exists(entry.Destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Staged)!);
                MoveEntry(entry.Destination, entry.Staged);
            }
            foreach (var entry in backups.AsEnumerable().Reverse())
            {
                if (!File.Exists(entry.Backup) && !Directory.Exists(entry.Backup)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
                MoveEntry(entry.Backup, entry.Destination);
            }
            if (Directory.Exists(backupRoot)) SafeFileTree.DeleteScopedDirectory(paths.BuildsRoot, backupRoot);
            throw;
        }
    }

    private static void BackupEntry(string finalRoot, string backupRoot, string destination, ICollection<(string Destination, string Backup)> backups)
    {
        var backup = Path.Combine(backupRoot, Path.GetRelativePath(finalRoot, destination));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        MoveEntry(destination, backup);
        backups.Add((destination, backup));
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        return leftInfo.Length == rightInfo.Length && ComputeHash(left).Equals(ComputeHash(right), StringComparison.OrdinalIgnoreCase);
    }

    private static CopyStatistics MeasureDirectory(string root)
    {
        var statistics = new CopyStatistics();
        statistics.Measure(root);
        return statistics;
    }

    private static string[] RequiredBuildFiles(string previewExtension) =>
    [
        Path.Combine("Contents", "pzasm-pack-manifest.json"),
        "workshop.txt",
        "steamcmd-item.vdf",
        "server-config.txt",
        "README-BUILD.txt",
        "pack.lock.json",
        "project.snapshot.json",
        "preview" + previewExtension
    ];

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string Fingerprint(object value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))).ToLowerInvariant();

    private static void WritePublicManifest(PackageProject project, string contentsRoot)
    {
        var manifest = new
        {
            schemaVersion = 1,
            generatedBy = "PZ Advanced Server Manager",
            projectId = project.Id,
            project.Name,
            project.Description,
            mode = project.Mode.ToString(),
            targetPzVersion = project.TargetPzVersion,
            noticeInjected = project.InjectConnectionNotice,
            controlInjected = project.InjectInGameControl,
            legalNotice = "Les mods restent la propriété de leurs auteurs. L'éditeur du pack est seul responsable des autorisations et crédits. LemonCorp et les développeurs de PZASM ne sont pas responsables des packs créés par les utilisateurs.",
            sources = project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order).Select(x => new
            {
                x.Name,
                x.Author,
                x.ModId,
                x.WorkshopId,
                x.Version,
                x.SelectedVersionFolder,
                x.PinnedAt,
                x.PinnedContentHash,
                x.PinnedMetadataStamp,
                x.IncludeInGlobalUpdates,
                x.SourceUrl,
                x.RequiredModIds,
                x.MapFolders,
                permissionStatus = x.Permission.Status.ToString(),
                x.Permission.RightsHolder,
                x.Permission.PublicEvidenceUrl
            })
        };
        File.WriteAllText(Path.Combine(contentsRoot, "pzasm-pack-manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
    }

    private static bool CopyTree(string source, string destination, CopyStatistics stats, bool preferHardLinks = false)
    {
        if (!preferHardLinks)
        {
            SafeFileTree.CopyDirectory(source, destination, (file, _) =>
            {
                stats.Files++;
                stats.Bytes += new FileInfo(file).Length;
            });
            return false;
        }

        var allLinked = true;
        SafeFileTree.LinkOrCopyDirectory(source, destination, (file, _, linked) =>
        {
            var bytes = new FileInfo(file).Length;
            stats.Files++;
            stats.Bytes += bytes;
            if (!linked)
            {
                allLinked = false;
                return;
            }
            stats.HardLinkedFiles++;
            stats.HardLinkedBytes += bytes;
        });
        return allLinked;
    }

    private static void CopyFile(string source, string destination, CopyStatistics stats)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
        stats.Files++;
        stats.Bytes += new FileInfo(source).Length;
    }

    private string EnsureScopedBuildPath(Guid projectId)
    {
        var root = Path.GetFullPath(paths.BuildsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(paths.BuildRoot(projectId));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Le chemin de build sort du dossier de données autorisé.");
        return target;
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string CleanLine(string value) => value.Replace("\r", " ").Replace("\n", "\\n").Trim();
    private sealed class CopyStatistics
    {
        public int Files;
        public long Bytes;
        public int HardLinkedFiles;
        public long HardLinkedBytes;

        public void Add(CopyStatistics other)
        {
            Files += other.Files;
            Bytes += other.Bytes;
            HardLinkedFiles += other.HardLinkedFiles;
            HardLinkedBytes += other.HardLinkedBytes;
        }

        public void Measure(string root)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                Files++;
                Bytes += new FileInfo(file).Length;
            }
        }
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record PreviewDescriptor(string Extension, string Token);
    private sealed record FusionCandidate(Guid ModReferenceId, string ModName, string SourceFile, string Hash);
}

public sealed class PackageBuildException(string message, PackageValidationResult validation) : Exception(message)
{
    public PackageValidationResult Validation { get; } = validation;
}
