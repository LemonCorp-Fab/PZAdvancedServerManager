using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PZAdvancedServerManager.Core.Domain;
using PZAdvancedServerManager.Core.Packaging;

namespace PZAdvancedServerManager.Core.Publishing;

public enum WorkshopPublicationMode
{
    NoOp,
    Full,
    Content,
    Metadata,
    Preview,
    Differential
}

public sealed record WorkshopPublicationSnapshot(
    string ContentFingerprint,
    string MetadataFingerprint,
    string PreviewFingerprint,
    string Title,
    string Description,
    WorkshopVisibility Visibility);

public sealed record WorkshopPublicationPlan(
    WorkshopPublicationMode Mode,
    WorkshopPublicationSnapshot Snapshot,
    ulong WorkshopIdBefore,
    WorkshopRemoteState? RemoteBefore,
    bool IncludeContent,
    bool IncludeMetadata,
    bool IncludePreview,
    bool Force,
    bool RemoteVerified,
    bool RemoteDiverged,
    bool RequiresServerRestart,
    string Summary)
{
    public bool IsNoOp => Mode == WorkshopPublicationMode.NoOp;
    public bool IsSubmitted => !IsNoOp;
}

public static class WorkshopPublicationPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static WorkshopPublicationSnapshot CreateSnapshot(PackageProject project, PackageBuildResult build)
    {
        if (string.IsNullOrWhiteSpace(build.ContentFingerprint))
            throw new InvalidOperationException("Le build ne contient pas d'empreinte de publication exploitable.");
        if (!File.Exists(build.WorkshopPreviewPath))
            throw new FileNotFoundException("La preview Workshop du build est introuvable.", build.WorkshopPreviewPath);

        var description = NormalizeNewLines(WorkshopDescriptionGenerator.Generate(project));
        var titleBytes = Encoding.UTF8.GetByteCount(project.Name);
        var descriptionBytes = Encoding.UTF8.GetByteCount(description);
        if (titleBytes > PzasmConstants.SteamWorkshopTitleMaximumUtf8Bytes)
            throw new InvalidOperationException($"Le titre Workshop fait {titleBytes:N0} octets UTF-8; Steam en accepte au maximum {PzasmConstants.SteamWorkshopTitleMaximumUtf8Bytes:N0}.");
        if (descriptionBytes >= PzasmConstants.SteamWorkshopDescriptionMaximumUtf8Bytes)
            throw new InvalidOperationException($"La description Workshop fait {descriptionBytes:N0} octets UTF-8; Steam exige moins de {PzasmConstants.SteamWorkshopDescriptionMaximumUtf8Bytes:N0}.");
        var metadataFingerprint = Fingerprint(new
        {
            project.Name,
            description,
            visibility = (int)project.Visibility
        });
        using var preview = File.OpenRead(build.WorkshopPreviewPath);
        var previewFingerprint = Convert.ToHexString(SHA256.HashData(preview)).ToLowerInvariant();
        return new WorkshopPublicationSnapshot(
            build.ContentFingerprint,
            metadataFingerprint,
            previewFingerprint,
            project.Name,
            description,
            project.Visibility);
    }

    public static WorkshopPublicationPlan CreatePlan(
        PackageProject project,
        WorkshopPublicationSnapshot snapshot,
        WorkshopRemoteState? remote,
        bool force)
    {
        var previous = project.Publication ?? new WorkshopPublicationState();
        var sameItem = project.PublishedWorkshopId != 0 && previous.WorkshopId == project.PublishedWorkshopId;
        var localContentChanged = !sameItem || !FixedEquals(previous.ContentFingerprint, snapshot.ContentFingerprint);
        var localMetadataChanged = !sameItem || !FixedEquals(previous.MetadataFingerprint, snapshot.MetadataFingerprint);
        var localPreviewChanged = !sameItem || !FixedEquals(previous.PreviewFingerprint, snapshot.PreviewFingerprint);

        var remoteIdentityValid = remote is not null &&
                                  remote.WorkshopId == project.PublishedWorkshopId &&
                                  remote.ConsumerAppId == long.Parse(PzasmConstants.ProjectZomboidSteamAppId) &&
                                  !remote.Banned;
        var remoteContentMatches = remoteIdentityValid &&
                                   !string.IsNullOrWhiteSpace(previous.RemoteContentHandle) &&
                                   previous.RemoteContentHandle.Equals(remote!.ContentHandle, StringComparison.Ordinal) &&
                                   previous.RemoteFileSize == remote.FileSize;
        var remotePreviewMatches = remoteIdentityValid &&
                                   !string.IsNullOrWhiteSpace(previous.RemotePreviewHandle) &&
                                   previous.RemotePreviewHandle.Equals(remote!.PreviewHandle, StringComparison.Ordinal);
        var remoteMetadataMatches = remoteIdentityValid &&
                                    snapshot.Title.Equals(remote!.Title, StringComparison.Ordinal) &&
                                    NormalizeNewLines(snapshot.Description).Equals(NormalizeNewLines(remote.Description), StringComparison.Ordinal) &&
                                    (int)snapshot.Visibility == remote.Visibility;
        var remoteTimestampMatches = remoteIdentityValid && previous.RemoteUpdatedAt is not null && remote!.UpdatedAt == previous.RemoteUpdatedAt;
        var remoteVerified = sameItem && previous.RemoteVerifiedAt is not null &&
                             remoteContentMatches && remotePreviewMatches && remoteMetadataMatches && remoteTimestampMatches;

        if (!force && !localContentChanged && !localMetadataChanged && !localPreviewChanged && remoteVerified)
            return new WorkshopPublicationPlan(
                WorkshopPublicationMode.NoOp,
                snapshot,
                project.PublishedWorkshopId,
                remote,
                false,
                false,
                false,
                false,
                true,
                false,
                false,
                "Le build local et le manifeste distant vérifié sont strictement identiques; SteamCMD ne sera pas lancé.");

        var conservative = project.PublishedWorkshopId == 0 || !sameItem || !remoteIdentityValid || previous.RemoteVerifiedAt is null;
        var remoteDiverged = sameItem && (!remoteContentMatches || !remotePreviewMatches || !remoteMetadataMatches || !remoteTimestampMatches);
        var includeContent = force || conservative || localContentChanged || !remoteContentMatches;
        var includeMetadata = force || conservative || localMetadataChanged || !remoteMetadataMatches || !remoteTimestampMatches;
        var includePreview = force || conservative || localPreviewChanged || !remotePreviewMatches;
        var mode = SelectMode(includeContent, includeMetadata, includePreview);
        var reason = force
            ? "Republication forcée : SteamCMD recevra le contenu, les métadonnées et la preview, puis Steam calculera son delta."
            : conservative
                ? "État distant insuffisamment vérifiable : publication complète conservatrice pour éviter un faux no-change."
                : remoteDiverged
                    ? "L'item distant ne correspond plus à l'état confirmé; seules les dimensions nécessaires seront restaurées."
                    : "Publication différentielle préparée à partir des empreintes confirmées.";
        return new WorkshopPublicationPlan(
            mode,
            snapshot,
            project.PublishedWorkshopId,
            remote,
            includeContent,
            includeMetadata,
            includePreview,
            force,
            remoteVerified,
            remoteDiverged,
            includeContent && (force || conservative || localContentChanged || !remoteContentMatches),
            reason);
    }

    public static bool IsRemoteConfirmation(
        PackageProject project,
        WorkshopPublicationPlan plan,
        WorkshopRemoteState remote,
        DateTimeOffset submittedAt,
        string publishedContentHandle = "",
        bool allowTimestampOnlyContentConfirmation = false)
    {
        if (remote.WorkshopId != project.PublishedWorkshopId ||
            remote.ConsumerAppId != long.Parse(PzasmConstants.ProjectZomboidSteamAppId) ||
            remote.Banned ||
            string.IsNullOrWhiteSpace(remote.ContentHandle) ||
            !remote.Title.Equals(plan.Snapshot.Title, StringComparison.Ordinal) ||
            !NormalizeNewLines(remote.Description).Equals(NormalizeNewLines(plan.Snapshot.Description), StringComparison.Ordinal) ||
            remote.Visibility != (int)plan.Snapshot.Visibility)
            return false;

        var previous = project.Publication ?? new WorkshopPublicationState();
        var before = plan.RemoteBefore;
        var baselineContentHandle = before?.ContentHandle ?? previous.RemoteContentHandle;
        var baselinePreviewHandle = before?.PreviewHandle ?? previous.RemotePreviewHandle;
        var baselineUpdatedAt = before?.UpdatedAt ?? previous.RemoteUpdatedAt;
        var remoteTimestampReachedSubmission = remote.UpdatedAt is not null &&
                                               remote.UpdatedAt.Value.ToUnixTimeSeconds() >= submittedAt.ToUnixTimeSeconds();
        var remoteTimestampAdvanced = baselineUpdatedAt is not null && remote.UpdatedAt > baselineUpdatedAt;
        var newItem = plan.WorkshopIdBefore == 0;

        if (!plan.IncludeContent &&
            (string.IsNullOrWhiteSpace(baselineContentHandle) ||
             !remote.ContentHandle.Equals(baselineContentHandle, StringComparison.Ordinal) ||
             previous.RemoteFileSize != 0 && remote.FileSize != previous.RemoteFileSize))
            return false;
        if (!plan.IncludePreview &&
            (string.IsNullOrWhiteSpace(baselinePreviewHandle) ||
             !remote.PreviewHandle.Equals(baselinePreviewHandle, StringComparison.Ordinal)))
            return false;
        if (plan.IncludePreview && string.IsNullOrWhiteSpace(remote.PreviewHandle)) return false;

        var localContentChanged = previous.WorkshopId != plan.WorkshopIdBefore ||
                                  !FixedEquals(previous.ContentFingerprint, plan.Snapshot.ContentFingerprint);
        var localMetadataChanged = previous.WorkshopId != plan.WorkshopIdBefore ||
                                   !FixedEquals(previous.MetadataFingerprint, plan.Snapshot.MetadataFingerprint);
        var localPreviewChanged = previous.WorkshopId != plan.WorkshopIdBefore ||
                                  !FixedEquals(previous.PreviewFingerprint, plan.Snapshot.PreviewFingerprint);
        var remoteContentWasDiverged = before is not null &&
                                       (!before.ContentHandle.Equals(previous.RemoteContentHandle, StringComparison.Ordinal) ||
                                        before.FileSize != previous.RemoteFileSize);
        var remotePreviewWasDiverged = before is not null &&
                                       !before.PreviewHandle.Equals(previous.RemotePreviewHandle, StringComparison.Ordinal);
        var remoteMetadataWasDiverged = before is not null &&
                                        (!before.Title.Equals(plan.Snapshot.Title, StringComparison.Ordinal) ||
                                         !NormalizeNewLines(before.Description).Equals(NormalizeNewLines(plan.Snapshot.Description), StringComparison.Ordinal) ||
                                         before.Visibility != (int)plan.Snapshot.Visibility);

        var contentMustChange = plan.IncludeContent && (localContentChanged || remoteContentWasDiverged);
        var previewMustChange = plan.IncludePreview && (localPreviewChanged || remotePreviewWasDiverged);
        var metadataMustChange = plan.IncludeMetadata && (localMetadataChanged || remoteMetadataWasDiverged);
        var contentChanged = !string.IsNullOrWhiteSpace(baselineContentHandle) &&
                             !remote.ContentHandle.Equals(baselineContentHandle, StringComparison.Ordinal);
        var previewChanged = !string.IsNullOrWhiteSpace(baselinePreviewHandle) &&
                             !remote.PreviewHandle.Equals(baselinePreviewHandle, StringComparison.Ordinal);

        if (newItem) return remoteTimestampReachedSubmission;
        if (contentMustChange && !contentChanged &&
            !(allowTimestampOnlyContentConfirmation && remoteTimestampAdvanced && remoteTimestampReachedSubmission)) return false;
        if (previewMustChange && !previewChanged) return false;
        if (metadataMustChange && !(remoteTimestampAdvanced && remoteTimestampReachedSubmission)) return false;

        if (!allowTimestampOnlyContentConfirmation && !string.IsNullOrWhiteSpace(publishedContentHandle) && plan.IncludeContent &&
            !remote.ContentHandle.Equals(publishedContentHandle, StringComparison.Ordinal))
            return false;

        var observableChange = contentChanged || previewChanged || remoteTimestampAdvanced;
        return observableChange && (contentChanged || previewChanged || remoteTimestampReachedSubmission);
    }

    public static void ApplyConfirmedState(
        PackageProject project,
        WorkshopPublicationSnapshot snapshot,
        WorkshopRemoteState? remote,
        string fallbackContentHandle = "")
    {
        project.Publication = new WorkshopPublicationState
        {
            WorkshopId = project.PublishedWorkshopId,
            ContentFingerprint = snapshot.ContentFingerprint,
            MetadataFingerprint = snapshot.MetadataFingerprint,
            PreviewFingerprint = snapshot.PreviewFingerprint,
            RemoteContentHandle = remote?.ContentHandle ?? fallbackContentHandle,
            RemotePreviewHandle = remote?.PreviewHandle ?? string.Empty,
            RemoteFileSize = remote?.FileSize ?? 0,
            RemoteUpdatedAt = remote?.UpdatedAt,
            RemoteVerifiedAt = remote is null ? null : DateTimeOffset.UtcNow
        };
    }

    public static string GenerateVdf(PackageProject project, PackageBuildResult build, WorkshopPublicationPlan plan)
    {
        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        var lines = new List<string>
        {
            "\"workshopitem\"",
            "{",
            $"    \"appid\"             \"{PzasmConstants.ProjectZomboidSteamAppId}\"",
            $"    \"publishedfileid\"   \"{project.PublishedWorkshopId}\""
        };
        if (plan.IncludeContent)
            lines.Add($"    \"contentfolder\"     \"{Escape(Path.GetFullPath(build.WorkshopContentRoot))}\"");
        if (plan.IncludePreview)
            lines.Add($"    \"previewfile\"       \"{Escape(Path.GetFullPath(build.WorkshopPreviewPath))}\"");
        if (plan.IncludeMetadata)
        {
            lines.Add($"    \"visibility\"        \"{(int)plan.Snapshot.Visibility}\"");
            lines.Add($"    \"title\"             \"{Escape(plan.Snapshot.Title)}\"");
            lines.Add($"    \"description\"       \"{Escape(plan.Snapshot.Description)}\"");
        }
        lines.Add($"    \"changenote\"        \"{(plan.Force ? "Republication forcée" : "Mise à jour incrémentale")} — {DateTimeOffset.Now:yyyy-MM-dd HH:mm}\"");
        lines.Add("}");
        return string.Join('\n', lines) + "\n";
    }

    private static WorkshopPublicationMode SelectMode(bool content, bool metadata, bool preview)
    {
        if (content && metadata && preview) return WorkshopPublicationMode.Full;
        if (content && !metadata && !preview) return WorkshopPublicationMode.Content;
        if (!content && metadata && !preview) return WorkshopPublicationMode.Metadata;
        if (!content && !metadata && preview) return WorkshopPublicationMode.Preview;
        return WorkshopPublicationMode.Differential;
    }

    private static string NormalizeNewLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static bool FixedEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Fingerprint(object value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))).ToLowerInvariant();
}

public sealed record WorkshopPublishResult(
    SteamCmdResult SteamCmd,
    WorkshopPublicationPlan Plan,
    WorkshopRemoteState? ConfirmedRemote,
    string PublishedContentHandle)
{
    public bool Success => SteamCmd.Success;
    public bool Submitted => Plan.IsSubmitted;
    public bool Skipped => Plan.IsNoOp;
}
