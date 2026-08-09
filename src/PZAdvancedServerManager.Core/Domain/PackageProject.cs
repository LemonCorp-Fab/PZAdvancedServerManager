using System.Text.Json.Serialization;

namespace PZAdvancedServerManager.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PackageMode
{
    Bundle,
    FusionStrict
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PermissionStatus
{
    Unknown,
    AuthorOwned,
    ExplicitPermission,
    CompatibleLicense,
    Denied
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkshopVisibility
{
    Private = 2,
    FriendsOnly = 1,
    Public = 0,
    Unlisted = 3
}

public sealed class PackageProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int SchemaVersion { get; set; } = PzasmConstants.CurrentProjectSchemaVersion;
    public string Name { get; set; } = "Nouveau pack serveur";
    public string Description { get; set; } = string.Empty;
    public PackageMode Mode { get; set; } = PackageMode.Bundle;
    public string TargetPzVersion { get; set; } = PzasmConstants.DefaultTargetVersion;
    public bool InjectConnectionNotice { get; set; } = true;
    public bool InjectInGameControl { get; set; } = true;
    public string NoticeTitle { get; set; } = PzasmConstants.ProductName;
    public ulong PublishedWorkshopId { get; set; }
    public WorkshopVisibility Visibility { get; set; } = WorkshopVisibility.Unlisted;
    public string[] Tags { get; set; } = ["Mod", "Build 42"];
    public string? PreviewImagePath { get; set; }
    public List<string> MapOrder { get; set; } = [];
    public List<PackageModReference> Mods { get; set; } = [];
    public bool LegalWarningAccepted { get; set; }
    public DateTimeOffset? LegalWarningAcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastBuiltAt { get; set; }
    public DateTimeOffset? LastPublishedAt { get; set; }
    public WorkshopPublicationState Publication { get; set; } = new();
    public PackageAutomationSettings Automation { get; set; } = new();

    [JsonIgnore]
    public string StableSuffix => Id.ToString("N")[..10].ToUpperInvariant();

    [JsonIgnore]
    public string NoticeModId => $"PZASM_Notice_{StableSuffix}";

    [JsonIgnore]
    public string ControlModId => $"PZASM_Control_{StableSuffix}";

    [JsonIgnore]
    public string FusionModId => $"PZASM_Pack_{StableSuffix}";
}

public sealed class PackageAutomationSettings
{
    public bool Enabled { get; set; }
    public string[] DailyTimes { get; set; } = [];
    public string SteamCmdPath { get; set; } = string.Empty;
    public string SteamUsername { get; set; } = string.Empty;
    public DateTimeOffset? SteamSessionVerifiedAt { get; set; }
    public bool AnonymousWorkshopDownloads { get; set; } = true;
    public bool RefreshWorkshopSourcesBeforeBuild { get; set; } = true;
    public bool PublishAfterBuild { get; set; } = true;
    public string CoordinatedServerName { get; set; } = string.Empty;
    public int PostPublishRestartDelayMinutes { get; set; } = 5;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string LastResult { get; set; } = string.Empty;
}

public sealed class WorkshopPublicationState
{
    public ulong WorkshopId { get; set; }
    public string ContentFingerprint { get; set; } = string.Empty;
    public string MetadataFingerprint { get; set; } = string.Empty;
    public string PreviewFingerprint { get; set; } = string.Empty;
    public string RemoteContentHandle { get; set; } = string.Empty;
    public string RemotePreviewHandle { get; set; } = string.Empty;
    public long RemoteFileSize { get; set; }
    public DateTimeOffset? RemoteUpdatedAt { get; set; }
    public DateTimeOffset? RemoteVerifiedAt { get; set; }
}

public sealed class PackageModReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ulong WorkshopId { get; set; }
    public string ModId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string SourceModRoot { get; set; } = string.Empty;
    public string SourceFolderName { get; set; } = string.Empty;
    public string PinnedSourceRoot { get; set; } = string.Empty;
    public DateTimeOffset? PinnedAt { get; set; }
    public string PinnedContentHash { get; set; } = string.Empty;
    public string PinnedMetadataStamp { get; set; } = string.Empty;
    public string ValidatedContentHash { get; set; } = string.Empty;
    public string[] ForbiddenFiles { get; set; } = [];
    public string SourceUpdateToken { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SelectedVersionFolder { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IncludeInGlobalUpdates { get; set; } = true;
    public string[] RequiredModIds { get; set; } = [];
    public string[] MapFolders { get; set; } = [];
    public PermissionEvidence Permission { get; set; } = new();

    [JsonIgnore]
    public string BuildSourceRoot => Directory.Exists(PinnedSourceRoot) ? PinnedSourceRoot : SourceModRoot;

    [JsonIgnore]
    public string EffectiveFolderName => string.IsNullOrWhiteSpace(SourceFolderName)
        ? Path.GetFileName(Path.TrimEndingDirectorySeparator(SourceModRoot))
        : SourceFolderName;

    [JsonIgnore]
    public string DisplayVersion => !string.IsNullOrWhiteSpace(Version)
        ? Version
        : !string.IsNullOrWhiteSpace(SelectedVersionFolder)
            ? $"PZ {SelectedVersionFolder}"
            : "non déclarée";
}

public sealed class PermissionEvidence
{
    public PermissionStatus Status { get; set; } = PermissionStatus.Unknown;
    public string RightsHolder { get; set; } = string.Empty;
    public string PublicEvidenceUrl { get; set; } = string.Empty;
    public string PrivateAttachmentPath { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateOnly? GrantedOn { get; set; }
}

public enum ValidationScope
{
    BuildAndPublish,
    PublishOnly,
    AutomationOnly,
    Warning
}

public sealed record PackageValidationIssue(
    string Code,
    string Message,
    bool IsError,
    Guid? ModReferenceId = null,
    ValidationScope Scope = ValidationScope.BuildAndPublish);

public sealed class PackageValidationResult
{
    public List<PackageValidationIssue> Issues { get; } = [];
    public bool CanBuild => Issues.All(x => !x.IsError || x.Scope is ValidationScope.PublishOnly or ValidationScope.AutomationOnly or ValidationScope.Warning);
    public bool CanPublish => Issues.All(x => !x.IsError || x.Scope is ValidationScope.AutomationOnly or ValidationScope.Warning);
    public bool CanAutomate => Issues.All(x => !x.IsError || x.Scope == ValidationScope.Warning);
}

public sealed class PackageBuildResult
{
    public required string BuildRoot { get; init; }
    public required string WorkshopContentRoot { get; init; }
    public required string WorkshopDescriptorPath { get; init; }
    public required string WorkshopPreviewPath { get; init; }
    public required string SteamCmdVdfPath { get; init; }
    public required string LockFilePath { get; init; }
    public required string ServerConfigSnippetPath { get; init; }
    public required PackageValidationResult Validation { get; init; }
    public required string ContentFingerprint { get; init; }
    public int CopiedFiles { get; init; }
    public long CopiedBytes { get; init; }
    public int HardLinkedFiles { get; init; }
    public long HardLinkedBytes { get; init; }
    public int ReusedFiles { get; init; }
    public long ReusedBytes { get; init; }
    public int RebuiltComponents { get; init; }
    public int ReusedComponents { get; init; }
    public int RemovedComponents { get; init; }
    public bool IsIncremental { get; init; }
    public bool IsNoOp { get; init; }
}

public sealed record PackageOperationResult(
    PackageBuildResult Build,
    string Output,
    bool Published,
    bool PublicationSubmitted,
    bool PublicationSkipped,
    string PublicationMode,
    bool ServerWasRunning,
    bool ServerRestarted);

public sealed record AutomationRunResult(Guid ProjectId, string ProjectName, bool Success, string Message);
