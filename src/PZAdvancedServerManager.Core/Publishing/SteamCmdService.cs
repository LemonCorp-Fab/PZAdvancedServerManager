using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Infrastructure;
using PZAdvancedServerManager.Core.Packaging;
using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.Core.Publishing;

public sealed class SteamCmdService(PackageValidator validator, WorkshopCatalogService catalog, SteamCmdInstaller installer, ApplicationPaths paths)
{
    public async Task<SteamCmdResult> UpdateDedicatedServerAsync(
        string steamCmdPath,
        string dedicatedServerRoot,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        steamCmdPath = await ResolveExecutableAsync(steamCmdPath, cancellationToken, progress);
        if (string.IsNullOrWhiteSpace(dedicatedServerRoot))
            throw new ArgumentException("Le dossier du serveur dédié est requis.", nameof(dedicatedServerRoot));

        var root = Path.GetFullPath(dedicatedServerRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Le dossier du serveur dédié n'existe pas : {root}");
        var startScript = Path.Combine(root, OperatingSystem.IsWindows() ? "StartServer64.bat" : "start-server.sh");
        if (!File.Exists(startScript))
            throw new FileNotFoundException("Le dossier choisi ne contient pas le script de démarrage Project Zomboid attendu.", startScript);

        progress?.Report(new OperationProgress("dedicated", "Vérification anonyme de l'installation du serveur dédié et rafraîchissement de ses droits Workshop."));
        return await RunAsync(
            steamCmdPath,
            [
                "+force_install_dir", root,
                "+login", "anonymous",
                "+app_update", PzasmConstants.ProjectZomboidDedicatedServerSteamAppId, "validate",
                "+quit"
            ],
            cancellationToken,
            progress: progress,
            timeout: TimeSpan.FromHours(2));
    }

    public async Task<WorkshopDownloadResult> DownloadWorkshopItemAsync(PackageProject project, ulong workshopId, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        if (workshopId == 0) throw new ArgumentOutOfRangeException(nameof(workshopId), "Workshop ID invalide.");
        var steamCmdPath = await ResolveExecutableAsync(project, cancellationToken, progress);
        var login = ResolveDownloadLogin(project);
        var result = await RunAsync(steamCmdPath,
            ["+login", login, "+workshop_download_item", PzasmConstants.ProjectZomboidSteamAppId, workshopId.ToString(), "validate", "+quit"], cancellationToken, progress: progress);
        var contentRoot = paths.ResolveSteamWorkshopItemRoot(steamCmdPath, workshopId);
        var workshopRoot = paths.ResolveSteamWorkshopRoot(steamCmdPath, [workshopId]);
        var manifest = SteamWorkshopManifestReader.Read(Path.Combine(workshopRoot, $"appworkshop_{PzasmConstants.ProjectZomboidSteamAppId}.acf"));
        var sourceUpdateToken = manifest.TryGetValue(workshopId, out var state) ? SteamWorkshopSourceToken.Create(state) : string.Empty;
        return new WorkshopDownloadResult(result, contentRoot, sourceUpdateToken);
    }

    public async Task<WorkshopDownloadResult> VerifyWorkshopItemAvailableAsync(PackageProject project, ulong workshopId, int maximumAttempts = 1, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        WorkshopDownloadResult? latest = null;
        var delays = new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(15) };
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            progress?.Report(new OperationProgress("availability", $"Vérification du téléchargement Workshop {workshopId} ({attempt}/{maximumAttempts}).", attempt, maximumAttempts));
            latest = await DownloadWorkshopItemAsync(project, workshopId, cancellationToken);
            if (latest.SteamCmd.Success && Directory.Exists(latest.ContentRoot) && Directory.EnumerateFiles(latest.ContentRoot, "*", SearchOption.AllDirectories).Any())
                return latest;
            if (attempt < maximumAttempts)
                await Task.Delay(delays[Math.Min(attempt - 1, delays.Length - 1)], cancellationToken);
        }
        return latest!;
    }

    public async Task<SteamCmdResult> RefreshSourcesAsync(PackageProject project, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
        => (await RefreshSourcesIncrementalAsync(project, project.Mods.Where(x => x.Enabled).ToArray(), cancellationToken, progress)).SteamCmd;

    public async Task<SteamCmdResult> RefreshSourcesAsync(PackageProject project, IReadOnlyCollection<PackageModReference> references, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
        => (await RefreshSourcesIncrementalAsync(project, references, cancellationToken, progress)).SteamCmd;

    public async Task<SteamWorkshopRefreshResult> RefreshSourcesIncrementalAsync(PackageProject project, IReadOnlyCollection<PackageModReference> references, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        var targets = references.DistinctBy(x => x.Id).ToArray();
        var workshopIds = targets.Where(x => x.WorkshopId != 0).Select(x => x.WorkshopId).Distinct().ToArray();
        if (workshopIds.Length == 0)
            return new SteamWorkshopRefreshResult(
                new SteamCmdResult(0, "Aucune source Workshop à actualiser.", string.Empty),
                [], 0, 0, 0, 0);
        var steamCmdPath = await ResolveExecutableAsync(project, cancellationToken, progress);

        var workshopRoot = paths.ResolveSteamWorkshopRoot(steamCmdPath, workshopIds);
        var contentRoot = Path.Combine(workshopRoot, "content", PzasmConstants.ProjectZomboidSteamAppId);
        var manifestPath = Path.Combine(workshopRoot, $"appworkshop_{PzasmConstants.ProjectZomboidSteamAppId}.acf");
        var installedBefore = SteamWorkshopManifestReader.Read(manifestPath);

        progress?.Report(new OperationProgress("workshop-check", $"Contrôle groupé de {workshopIds.Length} Workshop item(s) auprès de Steam."));
        Dictionary<ulong, long> remoteUpdateTimes;
        try
        {
            var details = await catalog.GetDetailsAsync(workshopIds, cancellationToken);
            remoteUpdateTimes = details
                .Where(item => item.UpdatedAt is not null)
                .ToDictionary(item => item.WorkshopId, item => item.UpdatedAt!.Value.ToUnixTimeSeconds());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            remoteUpdateTimes = [];
            progress?.Report(new OperationProgress("workshop-check", $"Le contrôle groupé est indisponible ({exception.Message}). SteamCMD vérifiera tous les items dans une seule session."));
        }

        var referencesByWorkshopId = targets
            .Where(reference => reference.WorkshopId != 0)
            .GroupBy(reference => reference.WorkshopId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var pendingIds = workshopIds
            .Where(id => !IsWorkshopItemCurrent(contentRoot, id, installedBefore, remoteUpdateTimes) &&
                         !IsImportedSnapshotCurrent(id, referencesByWorkshopId, remoteUpdateTimes))
            .ToArray();
        var reusedCount = workshopIds.Length - pendingIds.Length;
        progress?.Report(new OperationProgress("workshop-check", $"{reusedCount} item(s) déjà à jour; {pendingIds.Length} téléchargement(s) ou contrôle(s) SteamCMD requis."));

        SteamCmdResult commandResult;
        if (pendingIds.Length == 0)
        {
            commandResult = new SteamCmdResult(0, "Tous les Workshop items sont déjà à jour. SteamCMD n'a pas été lancé.", string.Empty);
        }
        else
        {
            var login = ResolveDownloadLogin(project);
            var arguments = new List<string> { "+login", login };
            foreach (var id in pendingIds)
            {
                arguments.Add("+workshop_download_item");
                arguments.Add(PzasmConstants.ProjectZomboidSteamAppId);
                arguments.Add(id.ToString());
                if (installedBefore.ContainsKey(id) && !HasWorkshopContent(contentRoot, id)) arguments.Add("validate");
            }
            arguments.Add("+quit");
            commandResult = await RunAsync(steamCmdPath, arguments, cancellationToken, progress: progress, timeout: TimeSpan.FromHours(1));
            if (!commandResult.Success)
                return new SteamWorkshopRefreshResult(commandResult, [], workshopIds.Length, pendingIds.Length, reusedCount, 0);
        }

        workshopRoot = paths.ResolveSteamWorkshopRoot(steamCmdPath, workshopIds);
        contentRoot = Path.Combine(workshopRoot, "content", PzasmConstants.ProjectZomboidSteamAppId);
        manifestPath = Path.Combine(workshopRoot, $"appworkshop_{PzasmConstants.ProjectZomboidSteamAppId}.acf");
        var installedAfter = SteamWorkshopManifestReader.Read(manifestPath);
        var importedSnapshotIds = workshopIds
            .Where(id => IsImportedSnapshotCurrent(id, referencesByWorkshopId, remoteUpdateTimes))
            .ToHashSet();
        var unavailableIds = workshopIds
            .Where(id => !importedSnapshotIds.Contains(id))
            .Where(id => !installedAfter.TryGetValue(id, out var state) || string.IsNullOrWhiteSpace(state.ManifestId) || !HasWorkshopContent(contentRoot, id))
            .ToArray();
        if (unavailableIds.Length > 0)
        {
            var error = "SteamCMD n'a pas fourni de contenu exploitable pour les Workshop IDs : " + string.Join(", ", unavailableIds);
            commandResult = new SteamCmdResult(-4, commandResult.StandardOutput, string.Join(Environment.NewLine, commandResult.StandardError, error));
            return new SteamWorkshopRefreshResult(commandResult, [], workshopIds.Length, pendingIds.Length, reusedCount, 0);
        }

        var changedReferenceIds = new HashSet<Guid>();
        var indexedItems = 0;
        var missingMods = new List<string>();
        foreach (var group in targets.Where(x => x.WorkshopId != 0).GroupBy(x => x.WorkshopId))
        {
            if (importedSnapshotIds.Contains(group.Key)) continue;
            if (!installedAfter.TryGetValue(group.Key, out var state) || string.IsNullOrWhiteSpace(state.ManifestId)) continue;
            var itemRoot = Path.Combine(contentRoot, group.Key.ToString());
            if (!Directory.Exists(itemRoot)) continue;
            var token = SteamWorkshopSourceToken.Create(state);
            var referenceStates = group.ToDictionary(
                reference => reference.Id,
                reference => ClassifyReference(reference, token, state, itemRoot));
            var needsIndex = referenceStates.Values.Any(status => status.RequiresIndex);
            Dictionary<string, DiscoveredMod>? discoveredById = null;
            if (needsIndex)
            {
                indexedItems++;
                discoveredById = PzDiscoveryService
                    .DiscoverWorkshopItemContent(itemRoot, group.Key, project.TargetPzVersion)
                    .GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(mods => mods.Key, mods => mods.First(), StringComparer.OrdinalIgnoreCase);
            }

            foreach (var reference in group)
            {
                var status = referenceStates[reference.Id];
                if (status.RequiresSnapshot) changedReferenceIds.Add(reference.Id);
                if (status.RequiresIndex)
                {
                    if (discoveredById is null || !discoveredById.TryGetValue(reference.ModId, out var discovered))
                    {
                        missingMods.Add($"{reference.ModId} (Workshop {reference.WorkshopId})");
                        continue;
                    }
                    ApplyDiscoveredMod(reference, discovered);
                }
                reference.SourceUpdateToken = token;
            }
        }

        if (missingMods.Count > 0)
        {
            var error = "Les Mod IDs suivants ne sont plus présents dans leur Workshop item : " + string.Join(", ", missingMods.Distinct(StringComparer.OrdinalIgnoreCase));
            commandResult = new SteamCmdResult(-3, commandResult.StandardOutput, string.Join(Environment.NewLine, commandResult.StandardError, error));
        }

        var summary = $"Contrôle incrémental terminé : {workshopIds.Length} item(s) contrôlé(s), {reusedCount} réutilisé(s), {pendingIds.Length} transmis à SteamCMD, {indexedItems} réindexé(s), {changedReferenceIds.Count} snapshot(s) à remplacer.";
        commandResult = new SteamCmdResult(
            commandResult.ExitCode,
            string.Join(Environment.NewLine, new[] { commandResult.StandardOutput, summary }.Where(value => !string.IsNullOrWhiteSpace(value))),
            commandResult.StandardError,
            commandResult.Interaction);
        return new SteamWorkshopRefreshResult(commandResult, changedReferenceIds, workshopIds.Length, pendingIds.Length, reusedCount, indexedItems);
    }

    public async Task<WorkshopPublicationPlan> PlanPublicationAsync(
        PackageProject project,
        PackageBuildResult build,
        bool force,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        var validation = validator.Validate(project);
        if (!validation.CanPublish)
            throw new InvalidOperationException("Publication bloquée par une erreur technique dans la configuration ou le contenu du pack.");
        var snapshot = WorkshopPublicationPlanner.CreateSnapshot(project, build);
        WorkshopRemoteState? remote = null;
        if (project.PublishedWorkshopId != 0)
        {
            progress?.Report(new OperationProgress("remote-check", "Vérification légère du manifeste Workshop distant avant toute décision de no-change."));
            try
            {
                remote = await catalog.GetRemoteStateAsync(project.PublishedWorkshopId, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                              exception is (HttpRequestException or IOException or TimeoutException or OperationCanceledException or JsonException or InvalidOperationException))
            {
                progress?.Report(new OperationProgress("remote-check", $"État distant non vérifiable ({exception.Message}); la publication ne sera pas ignorée."));
            }
        }
        var plan = WorkshopPublicationPlanner.CreatePlan(project, snapshot, remote, force);
        progress?.Report(new OperationProgress("publish-plan", plan.Summary));
        return plan;
    }

    public async Task<WorkshopPublishResult> PublishAsync(
        PackageProject project,
        PackageBuildResult build,
        WorkshopPublicationPlan plan,
        CancellationToken cancellationToken = default,
        IProgress<OperationProgress>? progress = null)
    {
        if (plan.IsNoOp)
        {
            var noOp = new SteamCmdResult(0, plan.Summary, string.Empty);
            return new WorkshopPublishResult(noOp, plan, plan.RemoteBefore, project.Publication.RemoteContentHandle);
        }

        var steamCmdPath = await ResolveExecutableAsync(project, cancellationToken, progress);
        if (string.IsNullOrWhiteSpace(project.Automation.SteamUsername))
            throw new InvalidOperationException("Le nom de compte Steam est requis. Le mot de passe n'est jamais conservé par PZASM.");

        var publishVdfPath = Path.Combine(build.BuildRoot, "steamcmd-publish.vdf");
        var temporaryVdfPath = publishVdfPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryVdfPath, WorkshopPublicationPlanner.GenerateVdf(project, build, plan), new UTF8Encoding(false));
            File.Move(temporaryVdfPath, publishVdfPath, true);
        }
        finally
        {
            if (File.Exists(temporaryVdfPath)) File.Delete(temporaryVdfPath);
        }
        progress?.Report(new OperationProgress("prevalidation", "Contrôle local des limites Steam, des chemins du build et de la preview avant tout calcul du manifeste."));
        ValidatePublishPayload(build, publishVdfPath, plan.IncludeContent, plan.IncludePreview);
        progress?.Report(plan.IncludeContent
            ? new OperationProgress("manifest-scan", "Prévalidation réussie. Steam peut maintenant inventorier le contenu et calculer le delta.")
            : new OperationProgress("workshop-commit", "Prévalidation réussie. Aucun scan du contenu n'est requis pour cette mise à jour."));

        var workshopLogPath = GetWorkshopLogPath(steamCmdPath);
        var workshopLogOffset = GetFileLength(workshopLogPath);
        var depotBuildLogPath = GetDepotBuildLogPath(steamCmdPath);
        var submittedAt = DateTimeOffset.UtcNow;
        var result = await RunAsync(steamCmdPath,
            ["+login", project.Automation.SteamUsername, "+workshop_build_item", publishVdfPath, "+quit"], cancellationToken, progress: progress, timeout: TimeSpan.FromHours(12), workshopBuildLogPath: depotBuildLogPath);
        var id = ApplyPublishedFileId(project, publishVdfPath);
        var workshopActivityLog = ReadAppendedLog(workshopLogPath, workshopLogOffset);
        var requiresRemoteProof = RequiresRemoteProof(result, id, workshopActivityLog);
        result = ValidateWorkshopSubmissionResult(result, id, workshopActivityLog);
        WorkshopRemoteState? confirmedRemote = null;
        var publishedContentHandle = ReadPublishedContentHandle(steamCmdPath);
        if (result.ExitCode == 0)
        {
            if (id == 0)
            {
                var missingId = new SteamCmdResult(-1, result.StandardOutput, string.Join(Environment.NewLine, result.StandardError, "SteamCMD n’a renvoyé aucun Workshop ID. La publication ne peut pas être confirmée."));
                return new WorkshopPublishResult(missingId, plan, null, publishedContentHandle);
            }
            confirmedRemote = await WaitForRemoteConfirmationAsync(project, plan, submittedAt, publishedContentHandle, requiresRemoteProof, cancellationToken, progress);
            if (requiresRemoteProof && confirmedRemote is null)
            {
                var unconfirmed = "SteamCMD a terminé le commit sans inclure le Workshop ID dans sa sortie Linux, et l'API Steam n'a pas encore confirmé le nouveau manifeste. L'envoi peut avoir réussi, mais aucun redémarrage de serveur ne sera déclenché tant que l'état distant n'est pas vérifié.";
                var pending = new SteamCmdResult(-4, result.StandardOutput, string.Join(Environment.NewLine, result.StandardError, unconfirmed), result.Interaction);
                return new WorkshopPublishResult(pending, plan, null, publishedContentHandle);
            }
            WorkshopPublicationPlanner.ApplyConfirmedState(project, plan.Snapshot, confirmedRemote, publishedContentHandle);
            project.LastPublishedAt = DateTimeOffset.UtcNow;
        }
        return new WorkshopPublishResult(result, plan, confirmedRemote, publishedContentHandle);
    }

    public async Task<SteamCmdResult> AuthenticateAsync(PackageProject project, SteamCredentials credentials, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        var steamCmdPath = await ResolveExecutableAsync(project, cancellationToken, progress);
        if (string.IsNullOrWhiteSpace(project.Automation.SteamUsername))
            throw new InvalidOperationException("Enregistrez d'abord le nom du compte Steam éditeur.");
        if (string.IsNullOrEmpty(credentials.Password))
            throw new InvalidOperationException("Le mot de passe Steam est requis pour créer ou renouveler la session portable.");
        progress?.Report(new OperationProgress("steamcmd", "Ouverture de la session SteamCMD portable."));
        if (!string.IsNullOrWhiteSpace(credentials.GuardCode)) ValidateSteamGuardCode(credentials.GuardCode);
        return await RunAsync(steamCmdPath,
            string.IsNullOrWhiteSpace(credentials.GuardCode) ? CreateAuthenticationArguments(project.Automation.SteamUsername) : [],
            cancellationToken, credentials, progress, TimeSpan.FromMinutes(5), project.Automation.SteamUsername);
    }

    public async Task<SteamCmdResult> VerifyCachedSessionAsync(PackageProject project, CancellationToken cancellationToken = default, IProgress<OperationProgress>? progress = null)
    {
        var steamCmdPath = await ResolveExecutableAsync(project, cancellationToken, progress);
        if (string.IsNullOrWhiteSpace(project.Automation.SteamUsername))
            throw new InvalidOperationException("Enregistrez d'abord le nom du compte Steam éditeur.");

        progress?.Report(new OperationProgress("steamcmd", "Vérification de la session déjà conservée par SteamCMD, sans mot de passe ni renouvellement de jeton."));
        return await RunAsync(
            steamCmdPath,
            CreateCachedSessionVerificationArguments(project.Automation.SteamUsername),
            cancellationToken,
            new SteamCredentials(string.Empty, string.Empty),
            progress,
            TimeSpan.FromMinutes(2),
            project.Automation.SteamUsername);
    }

    public static IReadOnlyList<string> CreateAuthenticationArguments(string username)
    {
        ValidateAccountName(username);
        return ["+login", username, "+quit"];
    }

    public static IReadOnlyList<string> CreateCachedSessionVerificationArguments(string username)
    {
        ValidateAccountName(username);
        return ["+login", username, "+info", "+quit"];
    }

    public static ulong ReadPublishedFileId(string vdfPath)
    {
        if (!File.Exists(vdfPath)) return 0;
        var match = Regex.Match(File.ReadAllText(vdfPath), "\\\"publishedfileid\\\"\\s+\\\"(\\d+)\\\"", RegexOptions.IgnoreCase);
        return match.Success && ulong.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    public static ulong ApplyPublishedFileId(PackageProject project, string vdfPath)
    {
        var id = ReadPublishedFileId(vdfPath);
        if (id != 0) project.PublishedWorkshopId = id;
        return id;
    }

    public static void ValidatePublishPayload(PackageBuildResult build)
    {
        ValidatePublishPayload(build, build.SteamCmdVdfPath, requireContent: true, requirePreview: true);
    }

    public static void ValidatePublishPayload(PackageBuildResult build, string vdfPath, bool requireContent, bool requirePreview)
    {
        if (!File.Exists(vdfPath))
            throw new FileNotFoundException("Le manifeste SteamCMD du build est introuvable. Reconstruisez le pack avant de publier.", vdfPath);

        var vdf = File.ReadAllText(vdfPath);
        var mappedContent = ReadVdfString(vdf, "contentfolder");
        var mappedPreview = ReadVdfString(vdf, "previewfile");
        var mappedTitle = ReadVdfString(vdf, "title");
        var mappedDescription = ReadVdfString(vdf, "description");
        var mappedVisibility = ReadVdfString(vdf, "visibility");
        var expectedContent = Path.GetFullPath(build.WorkshopContentRoot);
        var expectedPreview = Path.GetFullPath(build.WorkshopPreviewPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (requireContent)
        {
            if (string.IsNullOrWhiteSpace(mappedContent) ||
                !Path.GetFullPath(mappedContent).Equals(expectedContent, comparison) ||
                !Directory.Exists(mappedContent))
                throw new InvalidOperationException($"Le contentfolder du manifeste SteamCMD ne correspond pas au build actuel ou n’existe plus : {mappedContent ?? "non renseigné"}. Reconstruisez le pack avant de publier.");
            if (!Directory.EnumerateFiles(mappedContent, "*", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("Le contenu Workshop du build est vide. La publication n’a pas été lancée.");
        }
        else if (!string.IsNullOrWhiteSpace(mappedContent))
            throw new InvalidOperationException("Le manifeste différentiel contient un contentfolder alors que le contenu n'a pas changé.");
        if (requirePreview)
        {
            if (string.IsNullOrWhiteSpace(mappedPreview) ||
                !Path.GetFullPath(mappedPreview).Equals(expectedPreview, comparison) ||
                !File.Exists(mappedPreview))
                throw new InvalidOperationException($"Le previewfile du manifeste SteamCMD ne correspond pas au build actuel ou n’existe plus : {mappedPreview ?? "non renseigné"}. Reconstruisez le pack avant de publier.");
            var previewBytes = new FileInfo(mappedPreview).Length;
            if (previewBytes < WorkshopPreviewFile.MinimumBytes || previewBytes > WorkshopPreviewFile.MaximumBytes)
                throw new InvalidOperationException($"La preview Workshop fait {previewBytes:N0} octets. Steam exige une image d'au moins {WorkshopPreviewFile.MinimumBytes} octets et strictement inférieure à 1 Mo.");
        }
        else if (!string.IsNullOrWhiteSpace(mappedPreview))
            throw new InvalidOperationException("Le manifeste différentiel contient une preview alors que son empreinte n'a pas changé.");

        if (mappedTitle is not null)
        {
            var titleBytes = Encoding.UTF8.GetByteCount(mappedTitle);
            if (string.IsNullOrWhiteSpace(mappedTitle))
                throw new InvalidOperationException("Le titre Workshop est vide. La publication n'a pas été lancée.");
            if (titleBytes > PzasmConstants.SteamWorkshopTitleMaximumUtf8Bytes)
                throw new InvalidOperationException($"Le titre Workshop fait {titleBytes:N0} octets UTF-8; Steam en accepte au maximum {PzasmConstants.SteamWorkshopTitleMaximumUtf8Bytes:N0}. Raccourcissez le nom du pack avant de publier.");
        }
        if (mappedDescription is not null)
        {
            var descriptionBytes = Encoding.UTF8.GetByteCount(mappedDescription);
            if (descriptionBytes >= PzasmConstants.SteamWorkshopDescriptionMaximumUtf8Bytes)
                throw new InvalidOperationException($"La description Workshop fait {descriptionBytes:N0} octets UTF-8; Steam exige moins de {PzasmConstants.SteamWorkshopDescriptionMaximumUtf8Bytes:N0}. La publication a été arrêtée avant le calcul du manifeste.");
        }
        if (mappedVisibility is not null && (!int.TryParse(mappedVisibility, out var visibility) || visibility is < 0 or > 3))
            throw new InvalidOperationException($"La visibilité Workshop '{mappedVisibility}' est invalide. La publication n'a pas été lancée.");
    }

    private async Task<WorkshopRemoteState?> WaitForRemoteConfirmationAsync(
        PackageProject project,
        WorkshopPublicationPlan plan,
        DateTimeOffset submittedAt,
        string publishedContentHandle,
        bool allowTimestampOnlyContentConfirmation,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress)
    {
        var delays = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };
        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            if (delays[attempt] > TimeSpan.Zero) await Task.Delay(delays[attempt], cancellationToken);
            try
            {
                var remote = await catalog.GetRemoteStateAsync(project.PublishedWorkshopId, cancellationToken);
                if (remote is not null && WorkshopPublicationPlanner.IsRemoteConfirmation(project, plan, remote, submittedAt, publishedContentHandle, allowTimestampOnlyContentConfirmation))
                {
                    progress?.Report(new OperationProgress("remote-confirmed", $"Manifeste Workshop {remote.ContentHandle} confirmé par l'API Steam."));
                    return remote;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                              exception is (HttpRequestException or IOException or TimeoutException or OperationCanceledException or JsonException or InvalidOperationException))
            {
                progress?.Report(new OperationProgress("remote-confirmation", $"Propagation distante encore non vérifiable ({exception.Message}).", attempt + 1, delays.Length));
            }
        }
        progress?.Report(new OperationProgress("remote-confirmation", "SteamCMD a confirmé l'envoi. L'API publique ne permet pas encore de relire cet item; aucun futur no-change ne sera accepté sans vérification distante."));
        return null;
    }

    public static SteamCmdResult ValidateWorkshopSubmissionResult(SteamCmdResult result, ulong workshopId, string workshopActivityLog)
    {
        var combined = string.Join('\n', result.StandardOutput, result.StandardError, workshopActivityLog);
        var escapedId = workshopId == 0 ? "\\d+" : Regex.Escape(workshopId.ToString());
        var completion = Regex.Match(combined, $"Upload finished for workshop item\\s+{escapedId}\\s*:\\s*([^\\r\\n]+)", RegexOptions.IgnoreCase);
        var completionFailure = completion.Success && !completion.Groups[1].Value.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);
        var explicitFailure = Regex.IsMatch(combined, $"Upload workshop item\\s+{escapedId}\\s+failed", RegexOptions.IgnoreCase) ||
                              completionFailure ||
                              Regex.IsMatch(combined, "(?:ERROR!.*Failed to update workshop item|Update canceled:.*workshop|Build for workshop item has no content)", RegexOptions.IgnoreCase);
        var explicitSuccess = HasExplicitWorkshopSuccess(combined, workshopId);
        var commitCandidate = HasSuccessfulCommitSequence(combined);
        if (result.Success && (explicitSuccess || commitCandidate) && !explicitFailure) return result;
        if (!result.Success && !explicitFailure) return result;

        var invalidParameter = Regex.IsMatch(combined, "Invalid Parameter", RegexOptions.IgnoreCase);
        var reason = explicitFailure
            ? invalidParameter
                ? "Steam a refusé un paramètre au commit final. Vérifiez en priorité les limites UTF-8 du titre et de la description, la visibilité et la preview; les nouvelles publications sont désormais prévalidées avant le scan."
                : "SteamCMD a signalé explicitement l'échec de l'envoi Workshop."
            : "SteamCMD s'est fermé sans confirmation explicite de fin d'upload Workshop; l'opération reste non confirmée.";
        return new SteamCmdResult(-4, result.StandardOutput, string.Join(Environment.NewLine, result.StandardError, reason), result.Interaction);
    }

    public static bool RequiresRemoteProof(SteamCmdResult result, ulong workshopId, string workshopActivityLog)
    {
        if (!result.Success || workshopId == 0) return false;
        var combined = string.Join('\n', result.StandardOutput, result.StandardError, workshopActivityLog);
        return !HasExplicitWorkshopSuccess(combined, workshopId) &&
               HasSuccessfulCommitSequence(combined) &&
               !HasExplicitWorkshopFailure(combined, workshopId);
    }

    private static bool HasExplicitWorkshopSuccess(string combined, ulong workshopId)
    {
        if (workshopId == 0) return false;
        var escapedId = Regex.Escape(workshopId.ToString());
        return Regex.IsMatch(combined, $"Upload finished for workshop item\\s+{escapedId}\\s*:\\s*OK", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(combined, $"Success\\.\\s*(?:Published new Workshop item|Updated item)\\D*{escapedId}", RegexOptions.IgnoreCase);
    }

    private static bool HasSuccessfulCommitSequence(string combined) =>
        Regex.IsMatch(
            combined,
            "Committing update[\\s\\S]{0,4096}?Success\\.[\\s\\S]{0,1024}?Unloading Steam API[\\s\\S]{0,256}?OK",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static bool HasExplicitWorkshopFailure(string combined, ulong workshopId)
    {
        var escapedId = workshopId == 0 ? "\\d+" : Regex.Escape(workshopId.ToString());
        var completion = Regex.Match(combined, $"Upload finished for workshop item\\s+{escapedId}\\s*:\\s*([^\\r\\n]+)", RegexOptions.IgnoreCase);
        return Regex.IsMatch(combined, $"Upload workshop item\\s+{escapedId}\\s+failed", RegexOptions.IgnoreCase) ||
               completion.Success && !completion.Groups[1].Value.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(combined, "(?:ERROR!.*Failed to update workshop item|Update canceled:.*workshop|Build for workshop item has no content)", RegexOptions.IgnoreCase);
    }

    private static string GetWorkshopLogPath(string steamCmdPath) =>
        Path.Combine(Path.GetDirectoryName(steamCmdPath) ?? string.Empty, "logs", "workshop_log.txt");

    private static string GetDepotBuildLogPath(string steamCmdPath) =>
        Path.Combine(Path.GetDirectoryName(steamCmdPath) ?? string.Empty, "workshopbuilds", $"depot_build_{PzasmConstants.ProjectZomboidSteamAppId}.log");

    private static long GetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    private static string ReadAppendedLog(string path, long offset)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = Math.Min(Math.Max(0, offset), stream.Length);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static string ReadPublishedContentHandle(string steamCmdPath)
    {
        var root = Path.GetDirectoryName(steamCmdPath);
        if (string.IsNullOrWhiteSpace(root)) return string.Empty;
        var statePath = Path.Combine(root, "workshopbuilds", $"depot_build_{PzasmConstants.ProjectZomboidSteamAppId}.vdf");
        if (!File.Exists(statePath)) return string.Empty;
        var match = Regex.Match(File.ReadAllText(statePath), "\\\"manifest\\\"\\s+\\\"(\\d+)\\\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string? ReadVdfString(string vdf, string key)
    {
        var match = Regex.Match(vdf, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return UnescapeVdfString(match.Groups[1].Value);
    }

    private static string UnescapeVdfString(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }
            index++;
            builder.Append(value[index] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '"' => '"',
                _ => value[index]
            });
        }
        return builder.ToString();
    }

    public static OperationProgress? ParseWorkshopBuildProgress(string line)
    {
        if (line.Contains("Building file listing", StringComparison.OrdinalIgnoreCase))
            return new OperationProgress("manifest-scan", "Inventaire des fichiers du package par Steam.");
        var files = Regex.Match(line, "Found\\s+(\\d+)\\s+files\\s+\\(([^)]+)\\)", RegexOptions.IgnoreCase);
        if (files.Success)
            return new OperationProgress("manifest-scan", $"Inventaire terminé : {int.Parse(files.Groups[1].Value):N0} fichiers, {files.Groups[2].Value} à analyser.");
        var baseline = Regex.Match(line, "Building depot\\s+\\d+,\\s+baseline manifest\\s+(\\d+)\\s+\\((\\d+) files\\)", RegexOptions.IgnoreCase);
        if (baseline.Success)
            return new OperationProgress("manifest-hash", $"Comparaison avec le manifeste Steam {baseline.Groups[1].Value} ({int.Parse(baseline.Groups[2].Value):N0} fichiers de référence).");
        var chunks = Regex.Match(line, "Found\\s+(\\d+)\\s+new chunks\\s+\\(\\s*(\\d+)\\s+used previously\\s*\\).*?took\\s+(.+)$", RegexOptions.IgnoreCase);
        if (chunks.Success)
            return new OperationProgress("manifest-delta", $"Delta calculé en {chunks.Groups[3].Value.Trim()} : {int.Parse(chunks.Groups[1].Value):N0} chunks analysés, {int.Parse(chunks.Groups[2].Value):N0} déjà réutilisés.");
        if (line.Contains("Summary:", StringComparison.OrdinalIgnoreCase))
            return new OperationProgress("manifest-delta", Regex.Replace(line[(line.IndexOf("Summary:", StringComparison.OrdinalIgnoreCase) + 8)..].Trim(), "\\s+", " "));
        var manifest = Regex.Match(line, "Uploading new manifest.+?([\\d,]+) bytes", RegexOptions.IgnoreCase);
        if (manifest.Success)
            return new OperationProgress("workshop-upload", $"Envoi du manifeste ({manifest.Groups[1].Value} octets).");
        var upload = Regex.Match(line, "Uploading\\s+(\\d+)\\s+out of\\s+(\\d+)\\s+missing chunks", RegexOptions.IgnoreCase);
        if (upload.Success)
            return new OperationProgress("workshop-upload", $"Envoi des chunks absents : {upload.Groups[1].Value}/{upload.Groups[2].Value}.", int.Parse(upload.Groups[1].Value), int.Parse(upload.Groups[2].Value));
        var success = Regex.Match(line, "Success! New manifestID\\s+(\\d+)\\s+created and\\s+(\\d+)\\s+new chunks uploaded", RegexOptions.IgnoreCase);
        if (success.Success)
            return new OperationProgress("workshop-upload", $"Manifeste {success.Groups[1].Value} créé; {int.Parse(success.Groups[2].Value):N0} nouveau(x) chunk(s) envoyé(s).");
        return null;
    }

    private static async Task<SteamCmdResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        SteamCredentials? credentials = null,
        IProgress<OperationProgress>? progress = null,
        TimeSpan? timeout = null,
        string? authenticationUsername = null,
        string? workshopBuildLogPath = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(authenticationUsername)) ValidateAccountName(authenticationUsername);

        var consoleLogPath = Path.Combine(start.WorkingDirectory, "logs", "console_log.txt");
        var connectionLogPath = Path.Combine(start.WorkingDirectory, "logs", "connection_log.txt");
        var consoleLogOffset = File.Exists(consoleLogPath) ? new FileInfo(consoleLogPath).Length : 0;
        var connectionLogOffset = File.Exists(connectionLogPath) ? new FileInfo(connectionLogPath).Length : 0;
        var workshopBuildLogOffset = string.IsNullOrWhiteSpace(workshopBuildLogPath) ? 0 : GetFileLength(workshopBuildLogPath);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
        var token = timeoutCancellation.Token;
        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var promptGate = new SemaphoreSlim(1, 1);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var promptWindow = string.Empty;
        var exposeRawOutput = string.IsNullOrWhiteSpace(authenticationUsername);
        var passwordSent = false;
        var authenticationCompleted = false;
        var guardPromptResponseSent = false;
        var guardCodeBootstrap = !string.IsNullOrWhiteSpace(authenticationUsername) && !string.IsNullOrWhiteSpace(credentials?.GuardCode);
        var mobileApprovalPending = false;
        var mobileApprovalExpired = false;
        var lastMobileApprovalProgress = DateTimeOffset.MinValue;
        var lastClassifiedProgress = string.Empty;
        var interaction = SteamCmdInteraction.None;
        string? interventionError = null;

        if (guardCodeBootstrap)
        {
            await process.StandardInput.WriteLineAsync($"set_steam_guard_code {credentials!.GuardCode.Trim()}".AsMemory(), token);
            await process.StandardInput.WriteLineAsync($"login {authenticationUsername}".AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            progress?.Report(new OperationProgress("steamguard", "Code Steam Guard appliqué à cette tentative par l’entrée sécurisée de SteamCMD."));
        }

        async Task StopForInteractionAsync(SteamCmdInteraction requestedInteraction, string message)
        {
            if (interaction != SteamCmdInteraction.None) return;
            interaction = requestedInteraction;
            interventionError = message;
            progress?.Report(new OperationProgress(requestedInteraction is SteamCmdInteraction.SteamGuardCode or SteamCmdInteraction.SteamGuardMobileApprovalExpired ? "steamguard" : "session", message));
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await Task.CompletedTask;
        }

        async Task ObserveAsync(string rawChunk, StringBuilder? destination, bool reportClassifiedProgress = true)
        {
            if (string.IsNullOrEmpty(rawChunk)) return;
            var chunk = StripTerminalControlSequences(RedactSecrets(rawChunk, credentials));
            if (destination is not null && exposeRawOutput)
                lock (destination) destination.Append(chunk);

            await promptGate.WaitAsync(CancellationToken.None);
            try
            {
                promptWindow = (promptWindow + chunk).ToLowerInvariant();
                if (promptWindow.Length > 8192) promptWindow = promptWindow[^8192..];

                var message = Regex.Replace(chunk, "\\s+", " ").Trim();
                if (reportClassifiedProgress && exposeRawOutput && message.Length > 0 && !SteamCmdPromptClassifier.IsSecretPrompt(message))
                {
                    var classified = ClassifySteamCmdProgress(promptWindow);
                    var classificationKey = classified is null ? string.Empty : classified.Phase + "\n" + classified.Message;
                    if (classified is not null && !classificationKey.Equals(lastClassifiedProgress, StringComparison.Ordinal))
                    {
                        lastClassifiedProgress = classificationKey;
                        progress?.Report(classified);
                    }
                }

                if (!passwordSent && SteamCmdPromptClassifier.RequestsPassword(promptWindow))
                {
                    passwordSent = true;
                    if (string.IsNullOrEmpty(credentials?.Password))
                    {
                        await StopForInteractionAsync(
                            SteamCmdInteraction.SessionRequired,
                            "La session SteamCMD portable n’est plus valide. Reconnectez le compte éditeur avant de publier.");
                        return;
                    }

                    await process.StandardInput.WriteLineAsync(credentials.Password.AsMemory(), token);
                    await process.StandardInput.FlushAsync(token);
                    progress?.Report(new OperationProgress("credentials", "Mot de passe transmis à SteamCMD par l’entrée sécurisée du processus."));
                }

                if (!authenticationCompleted && string.IsNullOrWhiteSpace(credentials?.GuardCode) && SteamCmdPromptClassifier.AwaitsMobileApproval(promptWindow))
                {
                    mobileApprovalPending = true;
                    if (DateTimeOffset.UtcNow - lastMobileApprovalProgress >= TimeSpan.FromSeconds(4))
                    {
                        lastMobileApprovalProgress = DateTimeOffset.UtcNow;
                        progress?.Report(new OperationProgress("mobileapproval", "Une demande a été envoyée à l’application Steam Mobile. Approuvez cette connexion; SteamCMD vérifie automatiquement la réponse."));
                    }
                }

                if (SteamCmdPromptClassifier.MobileApprovalExpired(promptWindow))
                    mobileApprovalExpired = true;

                if (SteamCmdPromptClassifier.RequiresSteamGuard(promptWindow) && !authenticationCompleted)
                {
                    var rejectedCode = !string.IsNullOrWhiteSpace(credentials?.GuardCode) && SteamCmdPromptClassifier.RejectsSteamGuardCode(promptWindow);
                    if (string.IsNullOrWhiteSpace(credentials?.GuardCode) && mobileApprovalPending && !mobileApprovalExpired)
                        return;

                    if (string.IsNullOrWhiteSpace(credentials?.GuardCode) || rejectedCode || string.IsNullOrWhiteSpace(authenticationUsername))
                    {
                        await StopForInteractionAsync(
                            string.IsNullOrWhiteSpace(authenticationUsername)
                                ? SteamCmdInteraction.SessionRequired
                                : mobileApprovalExpired
                                    ? SteamCmdInteraction.SteamGuardMobileApprovalExpired
                                    : SteamCmdInteraction.SteamGuardCode,
                            string.IsNullOrWhiteSpace(authenticationUsername)
                                ? "La session SteamCMD doit être renouvelée depuis la section Compte éditeur avant cette publication."
                                : rejectedCode
                                    ? "Steam a refusé ce code Steam Guard. Saisissez le nouveau code affiché par l’application Steam ou reçu par e-mail."
                                    : mobileApprovalExpired
                                        ? "L’approbation mobile n’a pas été confirmée avant son expiration. Réessayez la notification ou utilisez le code actuel de l’application Steam ou reçu par e-mail."
                                        : "Steam Guard demande un code pour ce compte. Saisissez le code actuel de l’application Steam ou reçu par e-mail.");
                        return;
                    }

                    if (!guardPromptResponseSent && SteamCmdPromptClassifier.RequestsSteamGuardCode(promptWindow))
                    {
                        guardPromptResponseSent = true;
                        await process.StandardInput.WriteLineAsync(credentials!.GuardCode.Trim().AsMemory(), token);
                        await process.StandardInput.FlushAsync(token);
                        progress?.Report(new OperationProgress("steamguard", "Code transmis à l’invite Steam Guard interactive de SteamCMD."));
                    }
                }

                if (!authenticationCompleted && !string.IsNullOrWhiteSpace(authenticationUsername) && SteamCmdPromptClassifier.LoginSucceeded(promptWindow))
                {
                    authenticationCompleted = true;
                    progress?.Report(new OperationProgress("session", "Steam a validé le compte et enregistré la session dans l’installation SteamCMD portable."));
                    if (guardCodeBootstrap && !process.HasExited)
                    {
                        await process.StandardInput.WriteLineAsync("quit".AsMemory(), token);
                        await process.StandardInput.FlushAsync(token);
                    }
                }

                if (!authenticationCompleted && !string.IsNullOrWhiteSpace(authenticationUsername) && SteamCmdPromptClassifier.LoginFailed(promptWindow))
                {
                    interventionError = "Steam a refusé les identifiants du compte. Vérifiez le nom de compte et le mot de passe, puis recommencez.";
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
            }
            finally
            {
                promptGate.Release();
            }
        }

        async Task PumpAsync(StreamReader reader, StringBuilder destination)
        {
            var buffer = new char[64];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token);
                if (count == 0) break;
                await ObserveAsync(new string(buffer, 0, count), destination);
            }
        }

        async Task WatchAuthenticationPromptAsync()
        {
            if (string.IsNullOrWhiteSpace(authenticationUsername)) return;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), pumpCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await promptGate.WaitAsync(CancellationToken.None);
            try
            {
                if (passwordSent || authenticationCompleted || interaction != SteamCmdInteraction.None || process.HasExited) return;
                interventionError = "SteamCMD n’a présenté aucune invite de mot de passe après 45 secondes. Le processus a été arrêté pour éviter une attente bloquée.";
                progress?.Report(new OperationProgress("credentials", interventionError));
                process.Kill(entireProcessTree: true);
            }
            finally
            {
                promptGate.Release();
            }
        }

        var stdoutTask = PumpAsync(process.StandardOutput, standardOutput);
        var stderrTask = PumpAsync(process.StandardError, standardError);
        var consoleLogTask = TailLogAsync(consoleLogPath, consoleLogOffset, chunk => ObserveAsync(chunk, null, false), process, pumpCancellation.Token);
        var connectionLogTask = TailLogAsync(connectionLogPath, connectionLogOffset, chunk => ObserveAsync(chunk, null, false), process, pumpCancellation.Token);
        var workshopProgressBuffer = new StringBuilder();
        Task ObserveWorkshopBuildLogAsync(string chunk)
        {
            workshopProgressBuffer.Append(chunk);
            while (true)
            {
                var text = workshopProgressBuffer.ToString();
                var lineEnd = text.IndexOf('\n');
                if (lineEnd < 0) break;
                var line = text[..lineEnd].TrimEnd('\r');
                workshopProgressBuffer.Remove(0, lineEnd + 1);
                var parsed = ParseWorkshopBuildProgress(line);
                if (parsed is not null) progress?.Report(parsed);
            }
            return Task.CompletedTask;
        }
        var workshopBuildLogTask = string.IsNullOrWhiteSpace(workshopBuildLogPath)
            ? Task.CompletedTask
            : TailLogAsync(workshopBuildLogPath, workshopBuildLogOffset, ObserveWorkshopBuildLogAsync, process, pumpCancellation.Token);
        var authenticationPromptTask = WatchAuthenticationPromptAsync();
        if (!string.IsNullOrWhiteSpace(authenticationUsername))
            progress?.Report(new OperationProgress("credentials", guardCodeBootstrap
                ? "Code de secours et commande de connexion transmis à SteamCMD; attente de l’invite de mot de passe sécurisée."
                : "Commande de connexion transmise à SteamCMD; attente de l’invite de mot de passe sécurisée."));
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            pumpCancellation.Cancel();
            try { await Task.WhenAll(stdoutTask, stderrTask, consoleLogTask, connectionLogTask, workshopBuildLogTask, authenticationPromptTask); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
            throw;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            interventionError = $"SteamCMD a dépassé le délai maximal de {(timeout ?? TimeSpan.FromMinutes(30)).TotalMinutes:N0} minutes et a été arrêté.";
        }
        pumpCancellation.Cancel();
        try { await Task.WhenAll(stdoutTask, stderrTask, consoleLogTask, connectionLogTask, workshopBuildLogTask, authenticationPromptTask); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is (OperationCanceledException or IOException)) { }
        if (workshopProgressBuffer.Length > 0)
        {
            var parsed = ParseWorkshopBuildProgress(workshopProgressBuffer.ToString());
            if (parsed is not null) progress?.Report(parsed);
        }
        if (!string.IsNullOrWhiteSpace(authenticationUsername) && !authenticationCompleted && interaction == SteamCmdInteraction.None && string.IsNullOrWhiteSpace(interventionError) && mobileApprovalPending)
        {
            interaction = SteamCmdInteraction.SteamGuardMobileApprovalExpired;
            interventionError = mobileApprovalExpired
                ? "L’approbation mobile a expiré. Vous pouvez recommencer pour recevoir une nouvelle notification ou utiliser un code Steam Guard actuel."
                : "SteamCMD s’est fermé avant la confirmation mobile. Réessayez la notification ou utilisez un code Steam Guard actuel.";
        }
        if (!string.IsNullOrWhiteSpace(authenticationUsername) && !authenticationCompleted && interaction == SteamCmdInteraction.None && string.IsNullOrWhiteSpace(interventionError))
            interventionError = "SteamCMD s’est fermé avant de confirmer la session portable. Vérifiez la connexion réseau et réessayez.";
        var output = StripTerminalControlSequences(standardOutput.ToString());
        var error = StripTerminalControlSequences(standardError.ToString());
        if (!string.IsNullOrWhiteSpace(interventionError)) error = string.Join(Environment.NewLine, error, interventionError);
        return new SteamCmdResult(interventionError is null ? process.ExitCode : -1, output, error, interaction);
    }

    public static string StripTerminalControlSequences(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return Regex.Replace(value, "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))", string.Empty);
    }

    private static OperationProgress? ClassifySteamCmdProgress(string message)
    {
        var candidates = new List<(int Index, OperationProgress Progress)>
        {
            (Index: message.LastIndexOf("Preparing update", StringComparison.OrdinalIgnoreCase), Progress: new OperationProgress("manifest-scan", "Steam prépare la comparaison du package avec le manifeste distant.")),
            (Index: message.LastIndexOf("Uploading content", StringComparison.OrdinalIgnoreCase), Progress: new OperationProgress("workshop-upload", "Envoi du contenu différentiel vers Steam Workshop.")),
            (Index: message.LastIndexOf("Uploading preview", StringComparison.OrdinalIgnoreCase), Progress: new OperationProgress("workshop-upload", "Envoi de la preview Workshop.")),
            (Index: message.LastIndexOf("Committing update", StringComparison.OrdinalIgnoreCase), Progress: new OperationProgress("workshop-commit", "Validation finale des paramètres et rattachement du manifeste à l'item Workshop.")),
            (Index: message.LastIndexOf("Connecting anonymously to Steam Public", StringComparison.OrdinalIgnoreCase), Progress: new OperationProgress("steam-connect", "Connexion anonyme au réseau Steam.")),
            (Index: message.LastIndexOf("Waiting for client config", StringComparison.OrdinalIgnoreCase), Progress: new OperationProgress("steam-config", "Configuration du client Steam reçue; préparation du téléchargement.")),
            (Index: message.LastIndexOf("Waiting for user info", StringComparison.OrdinalIgnoreCase), Progress: new OperationProgress("steam-account", "Session Steam validée; récupération des droits Workshop."))
        };

        AddLatestRegex("Downloading item\\s+(\\d+)\\s+\\.\\.\\.", match => new OperationProgress("workshop-download", $"Téléchargement du Workshop item {match.Groups[1].Value}."));
        AddLatestRegex("Success\\.\\s*Downloaded item\\s+(\\d+)\\s+to\\s+", match => new OperationProgress("workshop-download", $"Workshop item {match.Groups[1].Value} téléchargé et vérifié."));
        AddLatestRegex("Update state .*?progress:\\s*([0-9.]+)", match =>
        {
            var percentage = double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? (int)Math.Clamp(Math.Round(value), 0, 100)
                : 0;
            return new OperationProgress("dedicated", $"Mise à jour de l'installation dédiée : {percentage} %.", percentage, 100);
        });
        AddLatestRegex("Success!\\s+App ['\"]?(\\d+)['\"]? fully installed", match => new OperationProgress("dedicated", $"Installation Steam {match.Groups[1].Value} vérifiée et à jour."));

        var latest = candidates.Where(candidate => candidate.Index >= 0).OrderByDescending(candidate => candidate.Index).FirstOrDefault();
        return latest.Index >= 0 ? latest.Progress : null;

        void AddLatestRegex(string pattern, Func<Match, OperationProgress> create)
        {
            var matches = Regex.Matches(message, pattern, RegexOptions.IgnoreCase);
            if (matches.Count == 0) return;
            var match = matches[^1];
            candidates.Add((match.Index, create(match)));
        }
    }

    private static async Task TailLogAsync(
        string path,
        long initialOffset,
        Func<string, Task> observe,
        Process process,
        CancellationToken cancellationToken)
    {
        var offset = initialOffset;
        var buffer = new byte[512];
        while (!cancellationToken.IsCancellationRequested)
        {
            var readAny = false;
            if (File.Exists(path))
            {
                try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.Asynchronous);
                    if (stream.Length < offset) offset = 0;
                    stream.Position = Math.Min(offset, stream.Length);
                    while (stream.Position < stream.Length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                        if (count == 0) break;
                        readAny = true;
                        offset += count;
                        await observe(Encoding.UTF8.GetString(buffer, 0, count));
                    }
                }
                catch (IOException) { }
            }
            if (process.HasExited && !readAny) break;
            await Task.Delay(100, cancellationToken);
        }
    }

    private static string RedactSecrets(string value, SteamCredentials? credentials)
    {
        if (!string.IsNullOrEmpty(credentials?.Password))
            value = value.Replace(credentials.Password, "********", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(credentials?.GuardCode))
            value = value.Replace(credentials.GuardCode.Trim(), "*****", StringComparison.OrdinalIgnoreCase);
        return value;
    }

    private static void ValidateAccountName(string username)
    {
        if (username.Any(char.IsWhiteSpace) || username.IndexOfAny(['\"', '\'', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Le nom de compte Steam contient des caractères incompatibles avec la connexion SteamCMD interactive.");
    }

    private static void ValidateSteamGuardCode(string code)
    {
        if (!Regex.IsMatch(code.Trim(), "^[A-Za-z0-9]{4,12}$"))
            throw new InvalidOperationException("Le code Steam Guard doit contenir uniquement 4 à 12 lettres ou chiffres.");
    }

    private static bool IsWorkshopItemCurrent(
        string contentRoot,
        ulong workshopId,
        IReadOnlyDictionary<ulong, SteamWorkshopItemState> installed,
        IReadOnlyDictionary<ulong, long> remoteUpdateTimes)
    {
        return remoteUpdateTimes.TryGetValue(workshopId, out var remoteUpdateTime) &&
               installed.TryGetValue(workshopId, out var state) &&
               !string.IsNullOrWhiteSpace(state.ManifestId) &&
               state.TimeUpdated == remoteUpdateTime &&
               HasWorkshopContent(contentRoot, workshopId);
    }

    private static bool HasWorkshopContent(string contentRoot, ulong workshopId)
    {
        var itemRoot = Path.Combine(contentRoot, workshopId.ToString());
        return Directory.Exists(itemRoot) && Directory.EnumerateFileSystemEntries(itemRoot).Any();
    }

    private static bool IsImportedSnapshotCurrent(
        ulong workshopId,
        IReadOnlyDictionary<ulong, PackageModReference[]> referencesByWorkshopId,
        IReadOnlyDictionary<ulong, long> remoteUpdateTimes)
    {
        return remoteUpdateTimes.TryGetValue(workshopId, out var remoteUpdateTime) &&
               referencesByWorkshopId.TryGetValue(workshopId, out var references) &&
               references.Length > 0 &&
               references.All(reference => SteamWorkshopSourceToken.MatchesRemote(reference, workshopId, remoteUpdateTime));
    }

    private static ReferenceRefreshStatus ClassifyReference(
        PackageModReference reference,
        string currentToken,
        SteamWorkshopItemState state,
        string itemRoot)
    {
        var sourceIsCurrentCache = Directory.Exists(reference.SourceModRoot) && IsPathWithin(reference.SourceModRoot, itemRoot);
        var snapshotExists = Directory.Exists(reference.PinnedSourceRoot) && !string.IsNullOrWhiteSpace(reference.PinnedContentHash);
        var tokenMatches = reference.SourceUpdateToken.Equals(currentToken, StringComparison.Ordinal);
        var installedAt = state.TimeUpdated > 0 ? DateTimeOffset.FromUnixTimeSeconds(state.TimeUpdated) : DateTimeOffset.MaxValue;
        var reusableLegacySnapshot = string.IsNullOrWhiteSpace(reference.SourceUpdateToken) &&
                                     snapshotExists &&
                                     sourceIsCurrentCache &&
                                     reference.PinnedAt is not null &&
                                     reference.PinnedAt.Value >= installedAt;
        var sourceChanged = !tokenMatches && !reusableLegacySnapshot;
        return new ReferenceRefreshStatus(
            RequiresIndex: !sourceIsCurrentCache || sourceChanged,
            RequiresSnapshot: !snapshotExists || sourceChanged);
    }

    private static bool IsPathWithin(string path, string parent)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(path));
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void ApplyDiscoveredMod(PackageModReference reference, DiscoveredMod discovered)
    {
        var previousAuthor = reference.Author;
        reference.SourceModRoot = discovered.ModRoot;
        reference.SourceFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(discovered.ModRoot));
        reference.Name = string.IsNullOrWhiteSpace(discovered.Name) ? reference.Name : discovered.Name;
        reference.Author = string.IsNullOrWhiteSpace(discovered.Author) ? reference.Author : discovered.Author;
        reference.Version = discovered.Version;
        reference.SelectedVersionFolder = discovered.SelectedVersionFolder;
        reference.RequiredModIds = discovered.RequiredModIds;
        reference.LoadAfterModIds = discovered.LoadAfterModIds;
        reference.LoadBeforeModIds = discovered.LoadBeforeModIds;
        reference.IncompatibleModIds = discovered.IncompatibleModIds;
        reference.MapFolders = discovered.MapFolders;
        if (!string.IsNullOrWhiteSpace(reference.Author) &&
            (string.IsNullOrWhiteSpace(reference.Permission.RightsHolder) ||
             reference.Permission.Status == PermissionStatus.Unknown && reference.Permission.RightsHolder.Equals(previousAuthor, StringComparison.OrdinalIgnoreCase)))
            reference.Permission.RightsHolder = reference.Author;
    }

    private readonly record struct ReferenceRefreshStatus(bool RequiresIndex, bool RequiresSnapshot);

    private async Task<string> ResolveExecutableAsync(
        PackageProject project,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress)
    {
        var executable = await ResolveExecutableAsync(project.Automation.SteamCmdPath, cancellationToken, progress);
        project.Automation.SteamCmdPath = executable;
        return executable;
    }

    private async Task<string> ResolveExecutableAsync(
        string? configuredPath,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress)
    {
        if (IsExecutable(configuredPath)) return Path.GetFullPath(configuredPath!);

        if (!string.IsNullOrWhiteSpace(configuredPath))
            progress?.Report(new OperationProgress("steamcmd-fallback", $"Le chemin SteamCMD configuré n'est plus utilisable ({configuredPath}). Bascule vers l'installation portable gérée."));

        var installed = await installer.EnsureInstalledAsync(cancellationToken, progress);
        ValidateExecutable(installed.ExecutablePath);
        return Path.GetFullPath(installed.ExecutablePath);
    }

    private static bool IsExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var fileName = Path.GetFileName(path);
        return fileName.Equals("steamcmd.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("steamcmd.sh", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateExecutable(string path)
    {
        if (!IsExecutable(path))
            throw new FileNotFoundException("SteamCMD est introuvable et l'installation portable automatique n'a pas abouti.", path);
    }

    private static string ResolveDownloadLogin(PackageProject project) =>
        project.Automation.AnonymousWorkshopDownloads || string.IsNullOrWhiteSpace(project.Automation.SteamUsername)
            ? "anonymous"
            : project.Automation.SteamUsername;
}

public enum SteamCmdInteraction
{
    None,
    SteamGuardCode,
    SteamGuardMobileApprovalExpired,
    SessionRequired
}

public static class SteamCmdPromptClassifier
{
    public static bool RequestsPassword(string value) => value.Contains("password:", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresSteamGuard(string value) =>
        value.Contains("two-factor code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("two factor code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("account login denied", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid login auth code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("steam guard", StringComparison.OrdinalIgnoreCase) &&
        (value.Contains("code", StringComparison.OrdinalIgnoreCase) || value.Contains("protected", StringComparison.OrdinalIgnoreCase));

    public static bool RequestsSteamGuardCode(string value) =>
        value.Contains("two-factor code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("two factor code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("steam guard", StringComparison.OrdinalIgnoreCase) && value.Contains("code", StringComparison.OrdinalIgnoreCase);

    public static bool AwaitsMobileApproval(string value) =>
        value.Contains("waiting for confirmation", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("pollauthsessionstatus succeeded, no refresh token yet", StringComparison.OrdinalIgnoreCase);

    public static bool MobileApprovalExpired(string value) =>
        value.Contains("timed out waiting for confirmation", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("account logon denied, need two-factor code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("account logon denied, need two factor code", StringComparison.OrdinalIgnoreCase);

    public static bool RejectsSteamGuardCode(string value) =>
        value.Contains("account login denied", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid login auth code", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid two-factor", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("invalid steam guard", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("incorrect steam guard", StringComparison.OrdinalIgnoreCase);

    public static bool LoginSucceeded(string value) =>
        value.Contains("waiting for user info", StringComparison.OrdinalIgnoreCase) && value.Contains("ok", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("logged in ok", StringComparison.OrdinalIgnoreCase);

    public static bool LoginFailed(string value) =>
        !RequiresSteamGuard(value) &&
        (value.Contains("invalid password", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("invalid credentials", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("login failure", StringComparison.OrdinalIgnoreCase));

    public static bool IsSecretPrompt(string value) => RequestsPassword(value) || value.Trim().Equals("password", StringComparison.OrdinalIgnoreCase);
}

public sealed record SteamCmdResult(int ExitCode, string StandardOutput, string StandardError, SteamCmdInteraction Interaction = SteamCmdInteraction.None)
{
    public bool Success => ExitCode == 0;
    public string CombinedOutput => string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed record SteamWorkshopRefreshResult(
    SteamCmdResult SteamCmd,
    IReadOnlyCollection<Guid> ChangedReferenceIds,
    int CheckedWorkshopItems,
    int SteamCmdWorkshopItems,
    int ReusedWorkshopItems,
    int IndexedWorkshopItems);

public sealed class SteamCmdInteractionRequiredException(SteamCmdInteraction interaction, string message) : Exception(message)
{
    public SteamCmdInteraction Interaction { get; } = interaction;

    public static SteamCmdInteractionRequiredException FromResult(SteamCmdResult result)
    {
        var fallback = result.Interaction is SteamCmdInteraction.SteamGuardCode or SteamCmdInteraction.SteamGuardMobileApprovalExpired
            ? "Steam Guard demande un nouveau code pour autoriser cette machine."
            : "La session SteamCMD portable doit être renouvelée avant de continuer.";
        var message = result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? fallback;
        return new SteamCmdInteractionRequiredException(result.Interaction, message);
    }
}

public sealed record WorkshopDownloadResult(SteamCmdResult SteamCmd, string ContentRoot, string SourceUpdateToken);
public sealed record SteamCredentials(string Password, string GuardCode);
