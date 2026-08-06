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
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Nouveau pack serveur";
    public string Description { get; set; } = string.Empty;
    public PackageMode Mode { get; set; } = PackageMode.Bundle;
    public string TargetPzVersion { get; set; } = "42.20.2";
    public bool InjectConnectionNotice { get; set; } = true;
    public string NoticeTitle { get; set; } = "PZ Advanced Server Manager";
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
    public PackageAutomationSettings Automation { get; set; } = new();

    [JsonIgnore]
    public string StableSuffix => Id.ToString("N")[..10].ToUpperInvariant();

    [JsonIgnore]
    public string NoticeModId => $"PZASM_Notice_{StableSuffix}";

    [JsonIgnore]
    public string FusionModId => $"PZASM_Pack_{StableSuffix}";
}

public sealed class PackageAutomationSettings
{
    public bool Enabled { get; set; }
    public string[] DailyTimes { get; set; } = [];
    public string SteamCmdPath { get; set; } = string.Empty;
    public string SteamUsername { get; set; } = string.Empty;
    public bool RefreshWorkshopSourcesBeforeBuild { get; set; } = true;
    public bool PublishAfterBuild { get; set; } = true;
    public string CoordinatedServerName { get; set; } = string.Empty;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string LastResult { get; set; } = string.Empty;
}

public sealed class PackageModReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ulong WorkshopId { get; set; }
    public string ModId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string SourceModRoot { get; set; } = string.Empty;
    public string SelectedVersionFolder { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public string[] RequiredModIds { get; set; } = [];
    public string[] MapFolders { get; set; } = [];
    public PermissionEvidence Permission { get; set; } = new();
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

public sealed record PackageValidationIssue(string Code, string Message, bool IsError, Guid? ModReferenceId = null);

public sealed class PackageValidationResult
{
    public List<PackageValidationIssue> Issues { get; } = [];
    public bool CanBuild => Issues.All(x => !x.IsError || x.Code == "RIGHTS_UNKNOWN");
    public bool CanPublish => Issues.All(x => !x.IsError);
}

public sealed class PackageBuildResult
{
    public required string BuildRoot { get; init; }
    public required string WorkshopContentRoot { get; init; }
    public required string WorkshopDescriptorPath { get; init; }
    public required string SteamCmdVdfPath { get; init; }
    public required string LockFilePath { get; init; }
    public required string ServerConfigSnippetPath { get; init; }
    public required PackageValidationResult Validation { get; init; }
    public int CopiedFiles { get; init; }
    public long CopiedBytes { get; init; }
}
