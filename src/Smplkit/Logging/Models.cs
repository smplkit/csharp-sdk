using System.Text.Json;

namespace Smplkit.Logging;

/// <summary>
/// Represents a logger resource from the smplkit Logging service.
/// Mutable active record — modify properties and call <see cref="SaveAsync"/> to persist.
/// </summary>
public sealed class Logger
{
    private readonly LoggingClient _client;

    /// <summary>Gets the logger UUID. Null for unsaved loggers.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the logger key.</summary>
    public string Key { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets the current log level. Set via <see cref="SetLevel"/>.</summary>
    public LogLevel? Level { get; internal set; }

    /// <summary>Gets or sets the log group UUID.</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets whether this logger is managed.</summary>
    public bool Managed { get; set; }

    /// <summary>Gets the logger sources.</summary>
    public List<Dictionary<string, object?>> Sources { get; internal set; }

    /// <summary>Gets the per-environment configuration.</summary>
    public Dictionary<string, Dictionary<string, object?>> Environments { get; internal set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal Logger(
        LoggingClient client,
        string? id,
        string key,
        string name,
        LogLevel? level,
        string? group,
        bool managed,
        List<Dictionary<string, object?>> sources,
        Dictionary<string, Dictionary<string, object?>> environments,
        DateTime? createdAt,
        DateTime? updatedAt)
    {
        _client = client;
        Id = id;
        Key = key;
        Name = name;
        Level = level;
        Group = group;
        Managed = managed;
        Sources = sources;
        Environments = environments;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Persist this logger to the server. Creates if new, updates if existing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveLoggerInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Key = saved.Key;
        Name = saved.Name;
        Level = saved.Level;
        Group = saved.Group;
        Managed = saved.Managed;
        Sources = saved.Sources;
        Environments = saved.Environments;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <summary>Set the log level. Local mutation only.</summary>
    /// <param name="level">The log level to set.</param>
    public void SetLevel(LogLevel level) { Level = level; }

    /// <summary>Clear the log level. Local mutation only.</summary>
    public void ClearLevel() { Level = null; }

    /// <summary>Set the log level for a specific environment. Local mutation only.</summary>
    /// <param name="env">The environment key.</param>
    /// <param name="level">The log level to set.</param>
    public void SetEnvironmentLevel(string env, LogLevel level)
    {
        Environments[env] = new Dictionary<string, object?> { ["level"] = level.ToWireString() };
    }

    /// <summary>Clear the log level for a specific environment. Local mutation only.</summary>
    /// <param name="env">The environment key.</param>
    public void ClearEnvironmentLevel(string env) { Environments.Remove(env); }

    /// <summary>Clear all environment-specific level overrides. Local mutation only.</summary>
    public void ClearAllEnvironmentLevels() { Environments.Clear(); }

    /// <inheritdoc />
    public override string ToString() =>
        $"Logger(Key={Key}, Level={Level})";
}

/// <summary>
/// Represents a log group resource from the smplkit Logging service.
/// Mutable active record — modify properties and call <see cref="SaveAsync"/> to persist.
/// </summary>
public sealed class LogGroup
{
    private readonly LoggingClient _client;

    /// <summary>Gets the log group UUID. Null for unsaved groups.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the log group key.</summary>
    public string Key { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets the current log level. Set via <see cref="SetLevel"/>.</summary>
    public LogLevel? Level { get; internal set; }

    /// <summary>Gets or sets the parent group UUID.</summary>
    public string? Group { get; set; }

    /// <summary>Gets the per-environment configuration.</summary>
    public Dictionary<string, Dictionary<string, object?>> Environments { get; internal set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal LogGroup(
        LoggingClient client,
        string? id,
        string key,
        string name,
        LogLevel? level,
        string? group,
        Dictionary<string, Dictionary<string, object?>> environments,
        DateTime? createdAt,
        DateTime? updatedAt)
    {
        _client = client;
        Id = id;
        Key = key;
        Name = name;
        Level = level;
        Group = group;
        Environments = environments;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Persist this log group to the server. Creates if new, updates if existing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveLogGroupInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Key = saved.Key;
        Name = saved.Name;
        Level = saved.Level;
        Group = saved.Group;
        Environments = saved.Environments;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <summary>Set the log level. Local mutation only.</summary>
    /// <param name="level">The log level to set.</param>
    public void SetLevel(LogLevel level) { Level = level; }

    /// <summary>Clear the log level. Local mutation only.</summary>
    public void ClearLevel() { Level = null; }

    /// <summary>Set the log level for a specific environment. Local mutation only.</summary>
    /// <param name="env">The environment key.</param>
    /// <param name="level">The log level to set.</param>
    public void SetEnvironmentLevel(string env, LogLevel level)
    {
        Environments[env] = new Dictionary<string, object?> { ["level"] = level.ToWireString() };
    }

    /// <summary>Clear the log level for a specific environment. Local mutation only.</summary>
    /// <param name="env">The environment key.</param>
    public void ClearEnvironmentLevel(string env) { Environments.Remove(env); }

    /// <summary>Clear all environment-specific level overrides. Local mutation only.</summary>
    public void ClearAllEnvironmentLevels() { Environments.Clear(); }

    /// <inheritdoc />
    public override string ToString() =>
        $"LogGroup(Key={Key}, Level={Level})";
}

/// <summary>
/// Describes a logger change event.
/// </summary>
/// <param name="Key">The logger key that changed.</param>
/// <param name="Level">The new log level, or null if cleared.</param>
/// <param name="Source">How the change was delivered: "websocket" or "manual".</param>
public sealed record LoggerChangeEvent(string Key, LogLevel? Level, string Source);
