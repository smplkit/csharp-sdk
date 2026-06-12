namespace Smplkit.Config;

/// <summary>
/// Represents a configuration resource from the smplkit Config service.
/// Modify properties and call <see cref="SaveAsync"/> to persist changes.
/// </summary>
public sealed class Config
{
    private readonly ConfigClient? _client;

    /// <summary>Gets the config identifier (slug). Null for unsaved configs.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets or sets the optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the parent config identifier (slug).</summary>
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
        ConfigClient? client,
        string? id,
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
        Name = name;
        Description = description;
        Parent = parent;
        Items = items;
        Environments = environments;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Persists this config to the server.
    /// </summary>
    /// <remarks>
    /// Creates a new config if unsaved, or updates the existing one.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="Errors.NotFoundException">If the config no longer exists (update).</exception>
    /// <exception cref="Errors.ValidationException">If the server rejects the request.</exception>
    /// <exception cref="InvalidOperationException">If the model was constructed without a client.</exception>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Config was constructed without a client; cannot save");
        var other = CreatedAt is null
            ? await _client.CreateConfigInternalAsync(this, ct).ConfigureAwait(false)
            : await _client.UpdateConfigFromModelInternalAsync(this, ct).ConfigureAwait(false);
        Apply(other);
    }

    /// <summary>Deletes this config from the server.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (_client is null || Id is null)
            throw new InvalidOperationException("Config was constructed without a client or id; cannot delete");
        return _client.DeleteAsync(Id, ct);
    }

    /// <summary>Copy all properties from <paramref name="other"/> into this instance.</summary>
    private void Apply(Config other)
    {
        Id = other.Id;
        Name = other.Name;
        Description = other.Description;
        Parent = other.Parent;
        Items = other.Items;
        Environments = other.Environments;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
    }

    private Dictionary<string, object?> ItemsTarget(string? environment)
    {
        if (environment is null) return Items;
        if (!Environments.TryGetValue(environment, out var envValues))
        {
            envValues = new Dictionary<string, object?>();
            Environments[environment] = envValues;
        }
        return envValues;
    }

    /// <summary>
    /// Sets (or replaces) a typed item. With <paramref name="environment"/> = <c>null</c>,
    /// sets the base item; otherwise sets a per-environment override.
    /// </summary>
    /// <remarks>
    /// An environment override stores only the raw value; the declared type and
    /// description come from the base item, so the <see cref="ConfigItem"/>'s type
    /// and description are ignored when <paramref name="environment"/> is supplied.
    /// </remarks>
    /// <param name="item">The <see cref="ConfigItem"/> to set. Its name is the item key.</param>
    /// <param name="environment">When given, set the value as an override on this
    /// environment rather than on the base config.</param>
    public void Set(ConfigItem item, string? environment = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ItemsTarget(environment)[item.Name] = item.Value;
    }

    /// <summary>Convenience: set a STRING item (or environment override).</summary>
    /// <param name="name">The item key to set.</param>
    /// <param name="value">The string value.</param>
    /// <param name="description">Optional human-readable description. Ignored when
    /// setting an environment override.</param>
    /// <param name="environment">When given, set the value as an override on this
    /// environment rather than on the base config.</param>
    public void SetString(string name, string value, string? description = null, string? environment = null)
        => Set(new ConfigItem(name, value, ItemType.String, description), environment);

    /// <summary>Convenience: set a NUMBER item (or environment override).</summary>
    /// <param name="name">The item key to set.</param>
    /// <param name="value">The numeric value.</param>
    /// <param name="description">Optional human-readable description. Ignored when
    /// setting an environment override.</param>
    /// <param name="environment">When given, set the value as an override on this
    /// environment rather than on the base config.</param>
    public void SetNumber(string name, double value, string? description = null, string? environment = null)
        => Set(new ConfigItem(name, value, ItemType.Number, description), environment);

    /// <summary>Convenience: set a BOOLEAN item (or environment override).</summary>
    /// <param name="name">The item key to set.</param>
    /// <param name="value">The boolean value.</param>
    /// <param name="description">Optional human-readable description. Ignored when
    /// setting an environment override.</param>
    /// <param name="environment">When given, set the value as an override on this
    /// environment rather than on the base config.</param>
    public void SetBoolean(string name, bool value, string? description = null, string? environment = null)
        => Set(new ConfigItem(name, value, ItemType.Boolean, description), environment);

    /// <summary>Convenience: set a JSON item (or environment override).</summary>
    /// <param name="name">The item key to set.</param>
    /// <param name="value">Any JSON-serializable value (dictionary, list, or primitive).</param>
    /// <param name="description">Optional human-readable description. Ignored when
    /// setting an environment override.</param>
    /// <param name="environment">When given, set the value as an override on this
    /// environment rather than on the base config.</param>
    public void SetJson(string name, object? value, string? description = null, string? environment = null)
        => Set(new ConfigItem(name, value, ItemType.Json, description), environment);

    /// <summary>
    /// Removes an item by name. With <paramref name="environment"/> = <c>null</c>,
    /// removes from base; otherwise removes the per-environment override only.
    /// </summary>
    /// <remarks>Removing an item that isn't present is a no-op.</remarks>
    /// <param name="name">The item key to remove.</param>
    /// <param name="environment">When given, remove only this environment's override
    /// for <paramref name="name"/>, leaving the base item intact.</param>
    public void Remove(string name, string? environment = null)
    {
        ItemsTarget(environment).Remove(name);
    }

    /// <inheritdoc />
    public override string ToString() => $"Config(Id={Id}, Name={Name})";
}

/// <summary>
/// Describes a single config value change.
/// </summary>
public sealed record ConfigChangeEvent(
    string ConfigId,
    string ItemKey,
    object? OldValue,
    object? NewValue,
    string Source);
