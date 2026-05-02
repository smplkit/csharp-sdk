using System.Text.Json;

namespace Smplkit.Management;

// ---------------------------------------------------------------------------
// Environment
// ---------------------------------------------------------------------------

/// <summary>
/// Represents a deployment environment. Modify properties and call
/// <see cref="SaveAsync"/> to persist.
/// </summary>
public sealed class Environment
{
    private readonly EnvironmentsClient _client;

    /// <summary>Gets the environment identifier (slug). Null for unsaved environments.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets the color (a <see cref="Smplkit.Management.Color"/> instance, or null).
    /// Hex strings assigned via the implicit <c>string → Color</c> conversion are validated
    /// at the SDK boundary (see <see cref="Smplkit.Management.Color(string)"/> for the
    /// accepted formats). Invalid hex throws <see cref="ArgumentException"/> at the
    /// assignment site, before any wire call.
    /// </summary>
    public Color? Color { get; set; }

    /// <summary>Gets or sets the classification (Standard or AdHoc).</summary>
    public EnvironmentClassification Classification { get; set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal Environment(
        EnvironmentsClient client,
        string? id,
        string name,
        Color? color,
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

    /// <summary>Deletes this environment from the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (Id is null)
            throw new InvalidOperationException("Cannot delete an unsaved environment.");
        return _client.DeleteAsync(Id, ct);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Environment(Id={Id}, Name={Name}, Classification={Classification})";
}

// ---------------------------------------------------------------------------
// ContextType
// ---------------------------------------------------------------------------

/// <summary>
/// Represents a context type — the schema of a targeting entity.
/// </summary>
public sealed class ContextType
{
    private readonly ContextTypesClient _client;

    /// <summary>Gets the context type identifier (slug). Null for unsaved types.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets the known-attribute map.</summary>
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

    /// <summary>Adds or replaces an attribute slot.</summary>
    public void AddAttribute(string name, Dictionary<string, object?>? metadata = null)
    {
        Attributes[name] = metadata ?? new Dictionary<string, object?>();
    }

    /// <summary>Removes an attribute slot.</summary>
    public void RemoveAttribute(string name) => Attributes.Remove(name);

    /// <summary>Replaces metadata for an existing attribute slot.</summary>
    public void UpdateAttribute(string name, Dictionary<string, object?>? metadata = null)
    {
        Attributes[name] = metadata ?? new Dictionary<string, object?>();
    }

    /// <summary>Persists this context type to the server.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Name = saved.Name;
        Attributes = saved.Attributes;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <summary>Deletes this context type from the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (Id is null)
            throw new InvalidOperationException("Cannot delete an unsaved context type.");
        return _client.DeleteAsync(Id, ct);
    }

    /// <inheritdoc />
    public override string ToString() => $"ContextType(Id={Id}, Name={Name})";
}

// ---------------------------------------------------------------------------
// AccountSettings
// ---------------------------------------------------------------------------

/// <summary>
/// Active-record account settings model.
/// </summary>
public sealed class AccountSettings
{
    private readonly AccountSettingsClient _client;
    private Dictionary<string, object?> _data;

    internal AccountSettings(AccountSettingsClient client, Dictionary<string, object?> data)
    {
        _client = client;
        _data = data;
    }

    /// <summary>The full settings dictionary. Direct mutations are written back on <see cref="SaveAsync"/>.</summary>
    public Dictionary<string, object?> Raw => _data;

    /// <summary>Canonical ordering of Standard environments.</summary>
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

    /// <summary>Persists the full settings object to the server.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveInternalAsync(_data, ct).ConfigureAwait(false);
        _data = saved._data;
    }

    internal void ApplyData(Dictionary<string, object?> data) => _data = data;

    /// <inheritdoc />
    public override string ToString() => $"AccountSettings({_data.Count} keys)";
}
