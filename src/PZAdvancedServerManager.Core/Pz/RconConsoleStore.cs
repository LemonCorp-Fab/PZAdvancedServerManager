using System.Collections.Concurrent;

namespace PZAdvancedServerManager.Core.Pz;

public sealed class RconConsoleStore(int capacity = 100)
{
    private readonly ConcurrentDictionary<string, ConsoleBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity = Math.Max(10, capacity);

    public IReadOnlyList<RconConsoleEntry> List(string serverName)
    {
        if (!_buffers.TryGetValue(serverName, out var buffer)) return [];
        lock (buffer.Entries) return buffer.Entries.ToArray();
    }

    public RconConsoleEntry Add(string serverName, string command, string response, bool succeeded)
    {
        var buffer = _buffers.GetOrAdd(serverName, _ => new ConsoleBuffer());
        var entry = new RconConsoleEntry(
            DateTimeOffset.Now,
            SanitizeCommand(command),
            Truncate(response, 12_000),
            succeeded);
        lock (buffer.Entries)
        {
            buffer.Entries.Add(entry);
            if (buffer.Entries.Count > _capacity)
                buffer.Entries.RemoveRange(0, buffer.Entries.Count - _capacity);
        }
        return entry;
    }

    public void Clear(string serverName) => _buffers.TryRemove(serverName, out _);

    private static string SanitizeCommand(string command)
    {
        var trimmed = Truncate(command.Trim(), 256);
        var sensitivePrefixes = new[] { "adduser ", "changepwd ", "changeoption password " };
        return sensitivePrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ? trimmed.Split(' ', 2)[0] + " <arguments redacted>"
            : trimmed;
    }

    private static string Truncate(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength] + Environment.NewLine + "…";

    private sealed class ConsoleBuffer
    {
        public List<RconConsoleEntry> Entries { get; } = [];
    }
}

public sealed record RconConsoleEntry(DateTimeOffset Timestamp, string Command, string Response, bool Succeeded);
