using System.Text.Json;

namespace Smplkit.Management;

/// <summary>
/// Represents a deployment environment. Modify properties and call
/// <see cref="SaveAsync"/> to create or update the resource on the server.
/// </summary>
public sealed class SmplEnvironment
{
    private readonly EnvironmentsClient _client;

    /// <summary>Gets the environment identifier (slug). Null for unsaved environments.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets or sets the hex color string (e.g., "#ef4444"), or null.</summary>
    public string? Color { get; set; }

    /// <summary>Gets or sets the classification (Standard or AdHoc).</summary>
    public EnvironmentClassification Classification { get; set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal SmplEnvironment(
        EnvironmentsClient client,
        string? id,
        string name,
        string? color,
        EnvironmentClassification classification,
        DateTime? createdAt,
        DateTime? updatedAt)
    {
        _client = client;
        Id = id;
        Name = name;
        Color = color;
        Classification = classification;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Persists this environment to the server. Creates if new, updates if existing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Name = saved.Name;
        Color = saved.Color;
        Classification = saved.Classification;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"SmplEnvironment(Id={Id}, Name={Name}, Classification={Classification})";
}

/// <summary>
/// Represents a context type — the schema of a targeting entity. Modify properties
/// and attribute slots, then call <see cref="SaveAsync"/> to persist.
/// </summary>
/// <remarks>
/// Context types describe the entity kinds used in flag-targeting rules
/// (e.g., "user", "account", "device"). Use <see cref="AddAttribute"/>,
/// <see cref="RemoveAttribute"/>, and <see cref="UpdateAttribute"/> to manage the
/// known-attribute schema; these are local changes until <see cref="SaveAsync"/> is called.
/// </remarks>
public sealed class ContextType
{
    private readonly ContextTypesClient _client;

    /// <summary>Gets the context type identifier (slug). Null for unsaved types.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>
    /// Gets the known-attribute map. Keys are attribute names; values are metadata
    /// dicts (e.g., display label, data type hints). Mutate via
    /// <see cref="AddAttribute"/>, <see cref="RemoveAttribute"/>,
    /// <see cref="UpdateAttribute"/>.
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>> Attributes { get; private set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal ContextType(
        ContextTypesClient client,
        string? id,
        string name,
        Dictionary<string, Dictionary<string, object?>>? attributes,
        DateTime? createdAt,
        DateTime? updatedAt)
    {
        _client = client;
        Id = id;
        Name = name;
        Attributes = attributes ?? new Dictionary<string, Dictionary<string, object?>>();
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Adds or replaces a known-attribute slot. Call <see cref="SaveAsync"/> to persist.
    /// </summary>
    /// <param name="name">Attribute name.</param>
    /// <param name="metadata">Optional metadata dict (e.g., display label, data type).</param>
    public void AddAttribute(string name, Dictionary<string, object?>? metadata = null)
    {
        Attributes[name] = metadata ?? new Dictionary<string, object?>();
    }

    /// <summary>
    /// Removes a known-attribute slot. Call <see cref="SaveAsync"/> to persist.
    /// </summary>
    /// <param name="name">Attribute name to remove.</param>
    public void RemoveAttribute(string name) => Attributes.Remove(name);

    /// <summary>
    /// Replaces the metadata for an existing attribute slot. Call <see cref="SaveAsync"/> to persist.
    /// </summary>
    /// <param name="name">Attribute name.</param>
    /// <param name="metadata">Replacement metadata dict.</param>
    public void UpdateAttribute(string name, Dictionary<string, object?>? metadata = null)
    {
        Attributes[name] = metadata ?? new Dictionary<string, object?>();
    }

    /// <summary>
    /// Persists this context type to the server. Creates if new, updates if existing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Name = saved.Name;
        Attributes = saved.Attributes;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <inheritdoc />
    public override string ToString() => $"ContextType(Id={Id}, Name={Name})";
}

/// <summary>
/// A single observed context entity, as returned by the management API list/get operations.
/// The write path is <c>ContextsClient.RegisterAsync</c>.
/// </summary>
public sealed class ContextEntity
{
    /// <summary>Gets the context type key (e.g., "user", "account").</summary>
    public string Type { get; }

    /// <summary>Gets the entity identifier within its type.</summary>
    public string Key { get; }

    /// <summary>Gets the optional display name.</summary>
    public string? Name { get; }

    /// <summary>Gets the observed attributes.</summary>
    public Dictionary<string, object?> Attributes { get; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; }

    /// <summary>Gets the composite identifier in <c>"{type}:{key}"</c> form.</summary>
    public string Id => $"{Type}:{Key}";

    internal ContextEntity(
        string type,
        string key,
        string? name,
        Dictionary<string, object?> attributes,
        DateTime? createdAt,
        DateTime? updatedAt)
    {
        Type = type;
        Key = key;
        Name = name;
        Attributes = attributes;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <inheritdoc />
    public override string ToString() => $"ContextEntity(Type={Type}, Key={Key})";
}

/// <summary>
/// Active-record account settings model. Modify properties and call
/// <see cref="SaveAsync"/> to write the full settings object back.
/// </summary>
/// <remarks>
/// The wire format is an opaque JSON object. Documented keys are exposed as typed
/// properties; all keys (including unknown ones) are accessible via <see cref="Raw"/>.
/// </remarks>
public sealed class AccountSettings
{
    private readonly AccountSettingsClient _client;
    private Dictionary<string, object?> _data;

    internal AccountSettings(AccountSettingsClient client, Dictionary<string, object?> data)
    {
        _client = client;
        _data = data;
    }

    /// <summary>
    /// The full settings dictionary. Direct mutations are written back on <see cref="SaveAsync"/>.
    /// </summary>
    public Dictionary<string, object?> Raw => _data;

    /// <summary>
    /// Canonical ordering of Standard environments (e.g., ["production", "staging", "development"]).
    /// Returns an empty list if the setting is absent.
    /// </summary>
    public List<string> EnvironmentOrder
    {
        get
        {
            if (!_data.TryGetValue("environment_order", out var raw)) return new List<string>();
            return raw switch
            {
                List<object?> list => list.Select(x => x?.ToString() ?? "").ToList(),
                List<string> typed => new List<string>(typed),
                object?[] arr => arr.Select(x => x?.ToString() ?? "").ToList(),
                JsonElement je when je.ValueKind == JsonValueKind.Array =>
                    je.EnumerateArray().Select(e => e.GetString() ?? "").ToList(),
                _ => new List<string>(),
            };
        }
        set => _data["environment_order"] = value.Cast<object?>().ToList();
    }

    /// <summary>
    /// Persists the full settings object to the server.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveInternalAsync(_data, ct).ConfigureAwait(false);
        _data = saved._data;
    }

    internal void ApplyData(Dictionary<string, object?> data) => _data = data;

    /// <inheritdoc />
    public override string ToString() => $"AccountSettings({_data.Count} keys)";
}
