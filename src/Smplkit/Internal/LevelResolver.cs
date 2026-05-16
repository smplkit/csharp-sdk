namespace Smplkit.Internal;

/// <summary>
/// One entry in a loggers- or groups-resolution cache. Holds only the fields
/// the resolution algorithm consults, so callers can construct test fixtures
/// without dragging in the full <see cref="Smplkit.Logging.Logger"/> /
/// <see cref="Smplkit.Logging.LogGroup"/> object graph.
/// </summary>
internal sealed record LevelEntry(
    LogLevel? Level,
    string? Group,
    IDictionary<string, Dictionary<string, object?>>? Environments);

/// <summary>
/// Client-side level resolution for managed loggers, per the algorithm in
/// ADR-034 §3.1. The server stores configured levels (logger, group, per-env)
/// as raw data; the SDK is responsible for walking the chain to decide which
/// level to apply to an adapter. First non-null source wins:
/// <list type="number">
///   <item>Logger's own <c>environments[env].level</c></item>
///   <item>Logger's own base <c>level</c></item>
///   <item>Group chain (recursive: group's env override → group's base → parent group → …)</item>
///   <item>Dot-notation ancestry (<c>com.acme.payments</c> → <c>com.acme</c> → <c>com</c>, applying 1–3 at each)</item>
///   <item>System fallback <see cref="LogLevel.Info"/></item>
/// </list>
/// Mirrors <c>smplkit.logging._resolution.resolve_level</c> in python-sdk.
/// </summary>
internal static class LevelResolver
{
    private const LogLevel FallbackLevel = LogLevel.Info;

    /// <summary>Resolves the effective level for a logger, returning the system fallback if nothing else matches.</summary>
    internal static LogLevel Resolve(
        string loggerId,
        string environment,
        IReadOnlyDictionary<string, LevelEntry> loggers,
        IReadOnlyDictionary<string, LevelEntry> groups)
    {
        var direct = ResolveForEntry(loggerId, environment, loggers, groups);
        if (direct is { } d) return d;

        // Dot-notation ancestry: walk com.acme.payments → com.acme → com
        var parts = loggerId.Split('.');
        for (var i = parts.Length - 1; i > 0; i--)
        {
            var ancestorId = string.Join('.', parts, 0, i);
            var fromAncestor = ResolveForEntry(ancestorId, environment, loggers, groups);
            if (fromAncestor is { } a) return a;
        }

        return FallbackLevel;
    }

    /// <summary>Debug-only helper describing which resolution step produced the level.</summary>
    internal static string FindResolutionSource(
        string loggerId,
        string environment,
        IReadOnlyDictionary<string, LevelEntry> loggers,
        IReadOnlyDictionary<string, LevelEntry> groups)
    {
        if (!loggers.TryGetValue(loggerId, out var entry)) return "not found";

        if (EnvLevel(entry, environment) is not null) return $"env override \"{environment}\"";
        if (entry.Level is not null) return "base level";
        if (ResolveGroupChain(entry.Group, environment, groups) is not null) return $"group \"{entry.Group}\"";
        return "unknown";
    }

    private static LogLevel? ResolveForEntry(
        string id,
        string environment,
        IReadOnlyDictionary<string, LevelEntry> loggers,
        IReadOnlyDictionary<string, LevelEntry> groups)
    {
        if (!loggers.TryGetValue(id, out var entry)) return null;

        if (EnvLevel(entry, environment) is { } envLevel) return envLevel;
        if (entry.Level is { } baseLevel) return baseLevel;
        return ResolveGroupChain(entry.Group, environment, groups);
    }

    private static LogLevel? ResolveGroupChain(
        string? groupId,
        string environment,
        IReadOnlyDictionary<string, LevelEntry> groups)
    {
        var visited = new HashSet<string>();
        var current = groupId;
        while (current is not null && visited.Add(current))
        {
            if (!groups.TryGetValue(current, out var group)) return null;
            if (EnvLevel(group, environment) is { } envLevel) return envLevel;
            if (group.Level is { } baseLevel) return baseLevel;
            current = group.Group;
        }
        return null;
    }

    private static LogLevel? EnvLevel(LevelEntry entry, string environment)
    {
        if (entry.Environments is null) return null;
        if (!entry.Environments.TryGetValue(environment, out var envData) || envData is null) return null;
        if (!envData.TryGetValue("level", out var raw) || raw is null) return null;
        return raw switch
        {
            LogLevel lvl => lvl,
            string s => LogLevelExtensions.TryParseLogLevel(s),
            _ => null,
        };
    }
}
