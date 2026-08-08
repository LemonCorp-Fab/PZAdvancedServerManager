namespace PZAdvancedServerManager.Core.Pz;

public enum ServerRuntimeState
{
    Stopped,
    Starting,
    StartingSlow,
    OnlineWithoutRcon,
    Online,
    MultipleInstances
}

public enum ServerRuntimeOrigin
{
    Unknown,
    LocalDedicated,
    LocalHostedSession,
    RemoteRcon
}

public sealed record ServerRuntimeInstance(
    int ProcessId,
    int? ParentProcessId,
    string ServerName,
    ServerRuntimeOrigin Origin,
    DateTimeOffset? StartedAt,
    string ExecutablePath);

public sealed record ServerRuntimeLogLine(
    long Sequence,
    DateTimeOffset? Timestamp,
    string Stream,
    string Message)
{
    public string Level => Classify(Stream, Message);

    private static string Classify(string stream, string message)
    {
        if (stream.Equals("ERR", StringComparison.OrdinalIgnoreCase)
            || stream.Equals("STDERR", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ERROR:", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Exception thrown", StringComparison.OrdinalIgnoreCase)
            || message.Contains("BindException", StringComparison.OrdinalIgnoreCase)) return "error";
        if (message.Contains("WARN :", StringComparison.OrdinalIgnoreCase)
            || message.Contains("WARNING", StringComparison.OrdinalIgnoreCase)) return "warning";
        if (message.Contains("*** SERVER STARTED ****", StringComparison.OrdinalIgnoreCase)) return "success";
        if (stream.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)) return "system";
        if (message.TrimStart().StartsWith("Stack trace:", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith('\t')) return "stack";
        return "info";
    }
}

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
    IReadOnlyList<ServerRuntimeLogLine> Output)
{
    public ServerRuntimeOrigin Origin { get; init; } = ServerRuntimeOrigin.Unknown;
    public IReadOnlyList<ServerRuntimeInstance> Instances { get; init; } = [];
    public int InactiveHostedHelperCount { get; init; }
}

public sealed record ForcedServerStopResult(
    string ServerName,
    IReadOnlyList<int> ProcessIds,
    DateTimeOffset CompletedAt);
