namespace Smplkit.Config;

/// <summary>
/// Represents a configuration resource from the smplkit Config service.
/// Mutable active record — modify properties and call <see cref="SaveAsync"/> to persist.
/// </summary>
public sealed class Config
{
    private readonly ConfigClient _client;

    /// <summary>Gets the config UUID. Null for unsaved configs.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the human-readable config key.</summary>
    public string Key { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets or sets the optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the parent config UUID.</summary>
    public string? Parent { get; set; }

    /// <summary>Gets or sets the base items dictionary (raw key-value pairs).</summary>
    public Dictionary<string, object?> Items { get; set; }

    /// <summary>Gets or sets the environment-specific override values.</summary>
    public Dictionary<string, Dictionary<string, object?>> Environments { get; set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal Config(
        ConfigClient client,
        string? id,
        string key,
        string name,
        string? description,
        string? parent,
        Dictionary<string, object?> items,
        Dictionary<string, Dictionary<string, object?>> environments,
        DateTime? createdAt,
        DateTime? updatedAt)
    {
        _client = client;
        Id = id;
        Key = key;
        Name = name;
        Description = description;
        Parent = parent;
        Items = items;
        Environments = environments;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Persist this config to the server. Creates (POST) if <see cref="Id"/> is null,
    /// updates (PUT) if it already exists.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveConfigInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Key = saved.Key;
        Name = saved.Name;
        Description = saved.Description;
        Parent = saved.Parent;
        Items = saved.Items;
        Environments = saved.Environments;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Config(Key={Key}, Name={Name})";
}

/// <summary>
/// Describes a single config value change detected during refresh or WebSocket update.
/// </summary>
/// <param name="ConfigKey">The config key (e.g. <c>"user_service"</c>).</param>
/// <param name="ItemKey">The item key within the config (e.g. <c>"timeout"</c>).</param>
/// <param name="OldValue">The previous value.</param>
/// <param name="NewValue">The updated value.</param>
/// <param name="Source">How the change was delivered: <c>"websocket"</c> or <c>"manual"</c>.</param>
public sealed record ConfigChangeEvent(
    string ConfigKey,
    string ItemKey,
    object? OldValue,
    object? NewValue,
    string Source
);
