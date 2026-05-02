namespace Smplkit.Config;

/// <summary>
/// Represents a configuration resource from the smplkit Config service.
/// Modify properties and call <see cref="SaveAsync"/> to persist changes.
/// </summary>
public sealed class Config
{
    private readonly ConfigClient _client;

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
        ConfigClient client,
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

    /// <summary>Persists this config to the server.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveConfigInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Name = saved.Name;
        Description = saved.Description;
        Parent = saved.Parent;
        Items = saved.Items;
        Environments = saved.Environments;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <summary>Deletes this config from the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (Id is null)
            throw new InvalidOperationException("Cannot delete an unsaved config.");
        return _client.DeleteAsync(Id, ct);
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
    public void Set(ConfigItem item, string? environment = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ItemsTarget(environment)[item.Name] = item.Value;
    }

    /// <summary>Convenience: set a STRING item (or environment override).</summary>
    public void SetString(string name, string value, string? description = null, string? environment = null)
        => Set(new ConfigItem(name, value, ItemType.String, description), environment);

    /// <summary>Convenience: set a NUMBER item (or environment override).</summary>
    public void SetNumber(string name, double value, string? description = null, string? environment = null)
        => Set(new ConfigItem(name, value, ItemType.Number, description), environment);

    /// <summary>Convenience: set a BOOLEAN item (or environment override).</summary>
    public void SetBoolean(string name, bool value, string? description = null, string? environment = null)
        => Set(new ConfigItem(name, value, ItemType.Boolean, description), environment);

    /// <summary>Convenience: set a JSON item (or environment override).</summary>
    public void SetJson(string name, object? value, string? description = null, string? environment = null)
        => Set(new ConfigItem(name, value, ItemType.Json, description), environment);

    /// <summary>
    /// Removes an item by name. With <paramref name="environment"/> = <c>null</c>,
    /// removes from base; otherwise removes the per-environment override only.
    /// </summary>
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
