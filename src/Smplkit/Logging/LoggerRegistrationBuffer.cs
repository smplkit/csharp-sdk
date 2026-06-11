namespace Smplkit.Logging;

/// <summary>
/// In-memory de-duplicating buffer for logger-source registrations awaiting a
/// bulk flush. Owned by the fused <see cref="LoggingClient"/> and shared with
/// its <see cref="LoggersClient"/> sub-client so discovery and explicit
/// registration drain through one queue.
/// </summary>
internal sealed class LoggerRegistrationBuffer
{
    private readonly HashSet<string> _seen = new();
    private readonly List<LoggerRegistrationEntry> _pending = new();
    private readonly object _lock = new();

    public void Add(string id, string? level, string? resolvedLevel, string? service, string? environment)
    {
        lock (_lock)
        {
            if (_seen.Add(id))
                _pending.Add(new(id, level, resolvedLevel, service, environment));
        }
    }

    public List<LoggerRegistrationEntry> Drain()
    {
        lock (_lock)
        {
            var batch = new List<LoggerRegistrationEntry>(_pending);
            _pending.Clear();
            return batch;
        }
    }

    public int PendingCount
    {
        get { lock (_lock) { return _pending.Count; } }
    }

    internal record LoggerRegistrationEntry(string Id, string? Level, string? ResolvedLevel, string? Service, string? Environment);
}
