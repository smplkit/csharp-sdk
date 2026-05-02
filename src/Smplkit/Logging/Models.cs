namespace Smplkit.Logging;

/// <summary>
/// Represents a logger resource from the smplkit Logging service.
/// Modify properties and call <see cref="SaveAsync"/> to persist changes.
/// </summary>
public sealed class Logger
{
    private readonly LoggingClient _client;

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

    /// <summary>Gets the per-environment configuration.</summary>
    public Dictionary<string, Dictionary<string, object?>> Environments { get; internal set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal Logger(
        LoggingClient client,
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
        Environments = environments;
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
        Environments = saved.Environments;
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
            Environments[environment] = new Dictionary<string, object?> { ["level"] = level.ToWireString() };
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
            Environments.Remove(environment);
    }

    /// <summary>Clears all environment-specific level overrides.</summary>
    public void ClearAllEnvironmentLevels() { Environments.Clear(); }

    /// <inheritdoc />
    public override string ToString() => $"Logger(Id={Id}, Level={Level})";
}

/// <summary>
/// Represents a log group resource from the smplkit Logging service.
/// </summary>
public sealed class LogGroup
{
    private readonly LoggingClient _client;

    /// <summary>Gets the log group identifier (slug). Null for unsaved groups.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets the current log level. Set via <see cref="SetLevel"/>.</summary>
    public LogLevel? Level { get; internal set; }

    /// <summary>Gets or sets the parent group identifier (slug).</summary>
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
        Environments = environments;
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
        Environments = saved.Environments;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <summary>Deletes this log group from the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (Id is null)
            throw new InvalidOperationException("Cannot delete an unsaved log group.");
        return _client.DeleteGroupAsync(Id, ct);
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
            Environments[environment] = new Dictionary<string, object?> { ["level"] = level.ToWireString() };
    }

    /// <summary>Clears the log level (base or per-env override).</summary>
    public void ClearLevel(string? environment = null)
    {
        if (environment is null)
            Level = null;
        else
            Environments.Remove(environment);
    }

    /// <summary>Clears all environment-specific level overrides.</summary>
    public void ClearAllEnvironmentLevels() { Environments.Clear(); }

    /// <inheritdoc />
    public override string ToString() => $"LogGroup(Id={Id}, Level={Level})";
}

/// <summary>Describes a logger change.</summary>
public sealed record LoggerChangeEvent(string Id, LogLevel? Level, string Source, bool Deleted = false);
