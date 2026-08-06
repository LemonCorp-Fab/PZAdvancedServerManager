using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Packaging;

public sealed class PackageBuildService(ApplicationPaths paths, PackageValidator validator)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public PackageBuildResult Build(PackageProject project)
    {
        var validation = validator.Validate(project);
        if (!validation.CanBuild)
            throw new PackageBuildException("Le projet contient des erreurs qui empêchent sa construction.", validation);

        var finalRoot = EnsureScopedBuildPath(project.Id);
        var nextRoot = finalRoot + ".next";
        DeleteScopedDirectory(nextRoot);
        Directory.CreateDirectory(nextRoot);
        var contentsRoot = Path.Combine(nextRoot, "Contents");
        var modsRoot = Path.Combine(contentsRoot, "mods");
        Directory.CreateDirectory(modsRoot);

        try
        {
            var copied = project.Mode switch
            {
                PackageMode.Bundle => BuildBundle(project, modsRoot),
                PackageMode.FusionStrict => BuildFusion(project, modsRoot, validation),
                _ => throw new ArgumentOutOfRangeException(nameof(project.Mode))
            };

            if (project.InjectConnectionNotice && project.Mode == PackageMode.Bundle)
                NoticeModGenerator.GenerateStandalone(modsRoot, project);

            if (!validation.CanBuild)
                throw new PackageBuildException("La fusion a détecté des collisions incompatibles.", validation);

            var description = WorkshopDescriptionGenerator.Generate(project);
            WritePublicManifest(project, contentsRoot);
            var previewPath = PreparePreview(project, nextRoot);
            var workshopPath = Path.Combine(nextRoot, "workshop.txt");
            File.WriteAllText(workshopPath, GenerateWorkshopTxt(project, description), new UTF8Encoding(false));
            var vdfPath = Path.Combine(nextRoot, "steamcmd-item.vdf");
            File.WriteAllText(vdfPath, GenerateSteamCmdVdf(project, contentsRoot, previewPath, description), new UTF8Encoding(false));
            var serverSnippetPath = Path.Combine(nextRoot, "server-config.txt");
            File.WriteAllText(serverSnippetPath, GenerateServerConfig(project), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(nextRoot, "README-BUILD.txt"), GenerateBuildReadme(project, validation), new UTF8Encoding(false));

            var lockPath = Path.Combine(nextRoot, "pack.lock.json");
            var lockData = CreateLock(project, contentsRoot);
            File.WriteAllText(lockPath, JsonSerializer.Serialize(lockData, JsonOptions), new UTF8Encoding(false));

            var localSnapshot = new
            {
                warning = "Copie locale du projet. Peut contenir les chemins locaux de preuves privées; ce fichier n'est pas placé dans Contents et ne sera pas publié.",
                project
            };
            File.WriteAllText(Path.Combine(nextRoot, "project.snapshot.json"), JsonSerializer.Serialize(localSnapshot, JsonOptions), new UTF8Encoding(false));

            DeleteScopedDirectory(finalRoot);
            Directory.Move(nextRoot, finalRoot);
            project.LastBuiltAt = DateTimeOffset.UtcNow;

            return new PackageBuildResult
            {
                BuildRoot = finalRoot,
                WorkshopContentRoot = Path.Combine(finalRoot, "Contents"),
                WorkshopDescriptorPath = Path.Combine(finalRoot, "workshop.txt"),
                SteamCmdVdfPath = Path.Combine(finalRoot, "steamcmd-item.vdf"),
                LockFilePath = Path.Combine(finalRoot, "pack.lock.json"),
                ServerConfigSnippetPath = Path.Combine(finalRoot, "server-config.txt"),
                Validation = validation,
                CopiedFiles = copied.Files,
                CopiedBytes = copied.Bytes
            };
        }
        catch
        {
            DeleteScopedDirectory(nextRoot);
            throw;
        }
    }

    private static CopyStatistics BuildBundle(PackageProject project, string modsRoot)
    {
        var stats = new CopyStatistics();
        foreach (var mod in project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order).ThenBy(x => x.Name))
        {
            var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(mod.SourceModRoot));
            CopyTree(mod.SourceModRoot, Path.Combine(modsRoot, folder), stats);
        }
        return stats;
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
            foreach (var mediaRoot in PzVersionSelector.GetEffectiveMediaRoots(mod.SourceModRoot, mod.SelectedVersionFolder))
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
        return stats;
    }

    private static string PreparePreview(PackageProject project, string buildRoot)
    {
        var target = Path.Combine(buildRoot, "preview.png");
        if (!string.IsNullOrWhiteSpace(project.PreviewImagePath) && File.Exists(project.PreviewImagePath))
            File.Copy(project.PreviewImagePath, target, true);
        else
            SimplePngWriter.Write(target, 512, 512);
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

        var maps = project.MapOrder.Count > 0
            ? project.MapOrder.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : project.Mods.Where(x => x.Enabled).SelectMany(x => x.MapFolders.Length > 0 ? x.MapFolders : DiscoverMapFolders(x.SourceModRoot, x.SelectedVersionFolder)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

AVERTISSEMENT : LemonCorp et les développeurs de PZ Advanced Server Manager ne sont pas responsables des packs créés. L'utilisateur doit obtenir les autorisations de redistribution de chaque auteur. Une visibilité « non listée » ou un usage serveur ne dispense pas de ces autorisations.

Publication autorisée par le validateur : {(validation.CanPublish ? "oui" : "non")}
Mode : {project.Mode}
Workshop ID : {(project.PublishedWorkshopId == 0 ? "nouvel item" : project.PublishedWorkshopId)}
""";

    private static object CreateLock(PackageProject project, string contentsRoot)
    {
        var files = Directory.EnumerateFiles(contentsRoot, "*", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                path = Path.GetRelativePath(contentsRoot, x).Replace('\\', '/'),
                bytes = new FileInfo(x).Length,
                sha256 = ComputeHash(x)
            }).ToArray();
        return new
        {
            schemaVersion = 1,
            projectId = project.Id,
            projectName = project.Name,
            mode = project.Mode.ToString(),
            targetPzVersion = project.TargetPzVersion,
            workshopId = project.PublishedWorkshopId,
            builtAt = DateTimeOffset.UtcNow,
            sources = project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order).Select(x => new
            {
                x.WorkshopId,
                x.ModId,
                x.Name,
                x.Author,
                x.SelectedVersionFolder,
                x.SourceUrl,
                permissionStatus = x.Permission.Status.ToString()
            }),
            files
        };
    }

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
            legalNotice = "Les mods restent la propriété de leurs auteurs. L'éditeur du pack est seul responsable des autorisations et crédits. LemonCorp et les développeurs de PZASM ne sont pas responsables des packs créés par les utilisateurs.",
            sources = project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order).Select(x => new
            {
                x.Name,
                x.Author,
                x.ModId,
                x.WorkshopId,
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

    private static void CopyTree(string source, string destination, CopyStatistics stats)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            CopyFile(file, Path.Combine(destination, Path.GetRelativePath(source, file)), stats);
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

    private void DeleteScopedDirectory(string target)
    {
        if (!Directory.Exists(target)) return;
        var root = Path.GetFullPath(paths.BuildsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(target);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Suppression refusée hors du dossier de builds.");
        Directory.Delete(resolved, true);
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string CleanLine(string value) => value.Replace("\r", " ").Replace("\n", "\\n").Trim();
    private sealed class CopyStatistics { public int Files; public long Bytes; }
    private sealed record FusionCandidate(Guid ModReferenceId, string ModName, string SourceFile, string Hash);
}

public sealed class PackageBuildException(string message, PackageValidationResult validation) : Exception(message)
{
    public PackageValidationResult Validation { get; } = validation;
}

internal static class SimplePngWriter
{
    public static void Write(string path, int width, int height)
    {
        using var output = File.Create(path);
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        using var header = new MemoryStream();
        WriteBigEndian(header, (uint)width);
        WriteBigEndian(header, (uint)height);
        header.WriteByte(8); header.WriteByte(2); header.WriteByte(0); header.WriteByte(0); header.WriteByte(0);
        WriteChunk(output, "IHDR", header.ToArray());

        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var accent = ((x / 32) + (y / 32)) % 2 == 0;
                raw.WriteByte((byte)(accent ? 67 : 35));
                raw.WriteByte((byte)(accent ? 103 : 55));
                raw.WriteByte((byte)(accent ? 48 : 41));
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) raw.WriteTo(zlib);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        WriteBigEndian(output, (uint)data.Length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes); output.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0); data.CopyTo(crcInput, typeBytes.Length);
        WriteBigEndian(output, Crc32(crcInput));
    }

    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xffffffff;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xffffffff;
    }

    private static void WriteBigEndian(Stream output, uint value) => output.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
