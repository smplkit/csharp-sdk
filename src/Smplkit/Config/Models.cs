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

    /// <summary>
    /// Read-only plain <c>{key: rawValue}</c> view of the base items.
    /// Mutate via <see cref="Set"/> / <see cref="SetString"/> / <see cref="SetNumber"/> /
    /// <see cref="SetBoolean"/> / <see cref="SetJson"/> / <see cref="Remove"/>, then call
    /// <see cref="SaveAsync"/> to persist.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Items => new Dictionary<string, object?>(ItemsBacking);

    // Flat base items ({key: rawValue}); the source of truth that the mutation
    // path, serialization, and resolution read. Internal so ConfigClient /
    // Resolver can serialize and normalize it directly without exposing a
    // mutable public dict. Mirrors EnvironmentsRaw.
    internal Dictionary<string, object?> ItemsBacking { get; private set; }

    /// <summary>
    /// Read-only typed view of the base items, keyed by item key, with each
    /// value a <c>{value, type}</c> dictionary. The <c>type</c> entry is one of
    /// <c>STRING</c> / <c>NUMBER</c> / <c>BOOLEAN</c>, derived from the value;
    /// values whose type can't be determined (objects, arrays) carry no
    /// <c>type</c> entry. Mutate the underlying items via <see cref="Set"/> /
    /// <see cref="SetString"/> / <see cref="SetNumber"/> / <see cref="SetBoolean"/> /
    /// <see cref="SetJson"/> / <see cref="Remove"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> ItemsRaw
    {
        get
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(ItemsBacking.Count);
            foreach (var (key, value) in ItemsBacking)
            {
                var def = new Dictionary<string, object?> { ["value"] = value };
                var type = InferWireType(value);
                if (type is not null) def["type"] = type;
                result[key] = def;
            }
            return result;
        }
    }

    /// <summary>
    /// Read-only view of per-environment overrides, keyed by environment name.
    /// Mutate via the <c>environment</c> argument on <see cref="Set"/> /
    /// <see cref="SetString"/> / <see cref="SetNumber"/> / <see cref="SetBoolean"/> /
    /// <see cref="SetJson"/> / <see cref="Remove"/>.
    /// </summary>
    public IReadOnlyDictionary<string, ConfigEnvironment> Environments =>
        EnvironmentsRaw.ToDictionary(kv => kv.Key, kv => ConfigEnvironment.FromRaw(kv.Value));

    // Raw wire-shaped per-environment overrides ({env: {key: rawValue}}); the
    // source of truth that serialization and resolution read.
    internal Dictionary<string, Dictionary<string, object?>> EnvironmentsRaw { get; private set; }

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
        ItemsBacking = items;
        EnvironmentsRaw = environments;
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
        ItemsBacking = other.ItemsBacking;
        EnvironmentsRaw = other.EnvironmentsRaw;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
    }

    /// <summary>Infer the wire-format type string for a base-item value, or
    /// <c>null</c> when it can't be inferred (objects, arrays, null).</summary>
    private static string? InferWireType(object? value) => value switch
    {
        string => "STRING",
        bool => "BOOLEAN",
        int or long or float or double or decimal => "NUMBER",
        _ => null,
    };

    private Dictionary<string, object?> ItemsTarget(string? environment)
    {
        if (environment is null) return ItemsBacking;
        if (!EnvironmentsRaw.TryGetValue(environment, out var envValues))
        {
            envValues = new Dictionary<string, object?>();
            EnvironmentsRaw[environment] = envValues;
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
