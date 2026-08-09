namespace PZAdvancedServerManager.Core.Packaging;

internal sealed class IncrementalBuildState
{
    public int SchemaVersion { get; set; } = 3;
    public Guid ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public string TargetPzVersion { get; set; } = string.Empty;
    public ulong WorkshopId { get; set; }
    public DateTimeOffset BuiltAt { get; set; }
    public string BuildFingerprint { get; set; } = string.Empty;
    public List<IncrementalBuildComponent> Components { get; set; } = [];
    public List<IncrementalBuildSource> Sources { get; set; } = [];
    public IncrementalBuildTotals Totals { get; set; } = new();
}

internal sealed class IncrementalBuildComponent
{
    public string Key { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Guid? ModReferenceId { get; set; }
    public string ModId { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string SourceContentHash { get; set; } = string.Empty;
    public int Files { get; set; }
    public long Bytes { get; set; }
    public int HardLinkedFiles { get; set; }
    public long HardLinkedBytes { get; set; }
    public bool StatisticsComplete { get; set; }
}

internal sealed class IncrementalBuildSource
{
    public ulong WorkshopId { get; set; }
    public string ModId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SelectedVersionFolder { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset? PinnedAt { get; set; }
    public string PinnedContentHash { get; set; } = string.Empty;
    public string PinnedMetadataStamp { get; set; } = string.Empty;
    public bool IncludeInGlobalUpdates { get; set; }
    public string PermissionStatus { get; set; } = string.Empty;
}

internal sealed class IncrementalBuildTotals
{
    public int Files { get; set; }
    public long Bytes { get; set; }
    public int HardLinkedFiles { get; set; }
    public long HardLinkedBytes { get; set; }
}

internal sealed record DesiredBuildComponent(
    string Key,
    string Kind,
    Guid? ModReferenceId,
    string ModId,
    string DestinationFolder,
    string Fingerprint,
    string SourceContentHash,
    string SourceRoot,
    bool PreferHardLinks);
