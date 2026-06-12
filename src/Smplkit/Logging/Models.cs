namespace Smplkit.Logging;

/// <summary>
/// Represents a logger resource from the smplkit Logging service.
/// Modify properties and call <see cref="SaveAsync"/> to persist changes.
/// </summary>
public sealed class Logger
{
    private readonly LoggersClient _client;

    /// <summary>Gets the logger identifier (slug). Null for unsaved loggers.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets the current log level. Set via <see cref="SetLevel"/>.</summary>
    public LogLevel? Level { get; internal set; }

    /// <summary>Gets or sets the log group identifier (slug).</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets whether this logger is managed.</summary>
    public bool Managed { get; set; }

    /// <summary>Gets the logger sources.</summary>
    public List<Dictionary<string, object?>> Sources { get; internal set; }

    /// <summary>
    /// Read-only view of per-environment level overrides, keyed by environment
    /// name. Mutate via <see cref="SetLevel"/> / <see cref="ClearLevel"/> /
    /// <see cref="ClearAllEnvironmentLevels"/> with an <c>environment</c> argument.
    /// </summary>
    public IReadOnlyDictionary<string, LoggerEnvironment> Environments =>
        EnvironmentsRaw.ToDictionary(kv => kv.Key, kv => LoggerEnvironment.FromRaw(kv.Value));

    // Raw wire-shaped per-environment data ({env: {"level": "ERROR"}}); the
    // source of truth that serialization and level resolution read.
    internal Dictionary<string, Dictionary<string, object?>> EnvironmentsRaw { get; private set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal Logger(
        LoggersClient client,
        string? id,
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
        Name = name;
        Level = level;
        Group = group;
        Managed = managed;
        Sources = sources;
        EnvironmentsRaw = environments;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>Persists this logger to the server.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveLoggerInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Name = saved.Name;
        Level = saved.Level;
        Group = saved.Group;
        Managed = saved.Managed;
        Sources = saved.Sources;
        EnvironmentsRaw = saved.EnvironmentsRaw;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <summary>Deletes this logger from the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (Id is null)
            throw new InvalidOperationException("Cannot delete an unsaved logger.");
        return _client.DeleteAsync(Id, ct);
    }

    /// <summary>
    /// Sets the log level. With <paramref name="environment"/> = <c>null</c>,
    /// sets the base level. Otherwise, sets the per-env override.
    /// </summary>
    public void SetLevel(LogLevel level, string? environment = null)
    {
        if (environment is null)
            Level = level;
        else
            EnvironmentsRaw[environment] = new Dictionary<string, object?> { ["level"] = level.ToWireString() };
    }

    /// <summary>
    /// Clears the log level. With <paramref name="environment"/> = <c>null</c>,
    /// clears the base level. Otherwise, clears the per-env override only.
    /// </summary>
    public void ClearLevel(string? environment = null)
    {
        if (environment is null)
            Level = null;
        else
            EnvironmentsRaw.Remove(environment);
    }

    /// <summary>Clears all environment-specific level overrides.</summary>
    public void ClearAllEnvironmentLevels() { EnvironmentsRaw.Clear(); }

    /// <inheritdoc />
    public override string ToString() => $"Logger(Id={Id}, Level={Level})";
}

/// <summary>
/// Represents a log group resource from the smplkit Logging service.
/// </summary>
public sealed class LogGroup
{
    private readonly LogGroupsClient _client;

    /// <summary>Gets the log group identifier (slug). Null for unsaved groups.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets the current log level. Set via <see cref="SetLevel"/>.</summary>
    public LogLevel? Level { get; internal set; }

    /// <summary>Gets or sets the parent group identifier (slug).</summary>
    public string? Group { get; set; }

    /// <summary>
    /// Read-only view of per-environment level overrides, keyed by environment
    /// name. Mutate via <see cref="SetLevel"/> / <see cref="ClearLevel"/> /
    /// <see cref="ClearAllEnvironmentLevels"/> with an <c>environment</c> argument.
    /// </summary>
    public IReadOnlyDictionary<string, LoggerEnvironment> Environments =>
        EnvironmentsRaw.ToDictionary(kv => kv.Key, kv => LoggerEnvironment.FromRaw(kv.Value));

    // Raw wire-shaped per-environment data ({env: {"level": "ERROR"}}); the
    // source of truth that serialization and level resolution read.
    internal Dictionary<string, Dictionary<string, object?>> EnvironmentsRaw { get; private set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal LogGroup(
        LogGroupsClient client,
        string? id,
        string name,
        LogLevel? level,
        string? group,
        Dictionary<string, Dictionary<string, object?>> environments,
        DateTime? createdAt,
        DateTime? updatedAt)
    {
        _client = client;
        Id = id;
        Name = name;
        Level = level;
        Group = group;
        EnvironmentsRaw = environments;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>Persists this log group to the server.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveLogGroupInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Name = saved.Name;
        Level = saved.Level;
        Group = saved.Group;
        EnvironmentsRaw = saved.EnvironmentsRaw;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <summary>Deletes this log group from the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (Id is null)
            throw new InvalidOperationException("Cannot delete an unsaved log group.");
        return _client.DeleteAsync(Id, ct);
    }

    /// <summary>
    /// Sets the log level. With <paramref name="environment"/> = <c>null</c>,
    /// sets the base level. Otherwise, sets the per-env override.
    /// </summary>
    public void SetLevel(LogLevel level, string? environment = null)
    {
        if (environment is null)
            Level = level;
        else
            EnvironmentsRaw[environment] = new Dictionary<string, object?> { ["level"] = level.ToWireString() };
    }

    /// <summary>Clears the log level (base or per-env override).</summary>
    public void ClearLevel(string? environment = null)
    {
        if (environment is null)
            Level = null;
        else
            EnvironmentsRaw.Remove(environment);
    }

    /// <summary>Clears all environment-specific level overrides.</summary>
    public void ClearAllEnvironmentLevels() { EnvironmentsRaw.Clear(); }

    /// <inheritdoc />
    public override string ToString() => $"LogGroup(Id={Id}, Level={Level})";
}

/// <summary>
/// Describes one effective-level change for a single managed logger. Emitted
/// to global and key-scoped subscribers in lockstep — every adapter
/// <c>ApplyLevel</c> call is paired with one <see cref="LoggerChangeEvent"/>
/// per subscriber.
/// </summary>
/// <param name="Id">The affected logger's normalized id.</param>
/// <param name="Level">The newly-applied effective level. Always non-null —
/// resolution falls back to <see cref="LogLevel.Info"/> if nothing else matches.</param>
/// <param name="Source">Trigger label: <c>"websocket"</c> for push updates
/// from the server, <c>"manual"</c> for <see cref="LoggingClient.RefreshAsync"/>.</param>
public sealed record LoggerChangeEvent(string Id, LogLevel Level, string Source);
