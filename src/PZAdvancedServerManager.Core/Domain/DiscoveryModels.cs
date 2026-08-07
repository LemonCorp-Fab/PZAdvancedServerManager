using System.Text.Json.Serialization;

namespace PZAdvancedServerManager.Core.Domain;

public sealed class PzInstallation
{
    public string? ClientRoot { get; init; }
    public string? DedicatedServerRoot { get; init; }
    public string? WorkshopRoot { get; init; }
    public string UserZomboidRoot { get; init; } = string.Empty;
    public string? SteamCmdPath { get; init; }
}

public sealed class DiscoveredMod
{
    public ulong WorkshopId { get; init; }
    public required string ModRoot { get; init; }
    public required string ModId { get; init; }
    public required string Name { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Poster { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string EffectiveManifestPath { get; init; } = string.Empty;
    public string SelectedVersionFolder { get; init; } = string.Empty;
    public string[] RequiredModIds { get; init; } = [];
    public string[] MapFolders { get; init; } = [];
    public DateTimeOffset SourceUpdatedAt { get; init; }
    public string WorkshopUrl => WorkshopId == 0 ? string.Empty : $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopId}";
}

public sealed class ModInfo
{
    public string Name { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Poster { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string[] Required { get; init; } = [];
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "servertest";
    public string IniPath { get; set; } = string.Empty;
    public string SandboxVarsPath { get; set; } = string.Empty;
    public string SpawnRegionsPath { get; set; } = string.Empty;
    public string ServerExecutablePath { get; set; } = string.Empty;
    public string SteamCmdPath { get; set; } = string.Empty;
    public string RestartSchedule { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServerConnectionKind
{
    Local,
    Remote
}

public sealed class RemoteServerConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = string.Empty;
    public string SshPrivateKeyPath { get; set; } = string.Empty;
    public string RemoteIniPath { get; set; } = string.Empty;
    public string StartCommand { get; set; } = string.Empty;
    public string RconHost { get; set; } = string.Empty;
    public int RconPort { get; set; } = 27015;
    public string RconPassword { get; set; } = string.Empty;
    public bool AutoRestartAfterRconQuit { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool HasSshConnection =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(SshUser);

    [JsonIgnore]
    public bool HasSshManagement =>
        HasSshConnection &&
        !string.IsNullOrWhiteSpace(RemoteIniPath);
}

public sealed record OperationProgress(string Phase, string Message, int? Current = null, int? Total = null);
