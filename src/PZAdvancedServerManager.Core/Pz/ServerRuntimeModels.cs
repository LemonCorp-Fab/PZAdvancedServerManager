namespace PZAdvancedServerManager.Core.Pz;

public enum ServerRuntimeState
{
    Stopped,
    Starting,
    StartingSlow,
    OnlineWithoutRcon,
    Online
}

public sealed record ServerRuntimeLogLine(
    long Sequence,
    DateTimeOffset? Timestamp,
    string Stream,
    string Message);

public sealed record ServerRuntimeSnapshot(
    ServerRuntimeState State,
    bool IsRunning,
    bool IsGameReady,
    bool IsRconAuthenticated,
    bool RconBindFailed,
    bool IsManagedByCurrentSession,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastOutputAt,
    IReadOnlyList<ServerRuntimeLogLine> Output);
