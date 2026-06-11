// Active-record models for client.platform.* resources.

namespace Smplkit.Platform;

// ---------------------------------------------------------------------------
// Environment
// ---------------------------------------------------------------------------

/// <summary>
/// Environment resource. Mutate fields, then call <see cref="SaveAsync"/>.
/// </summary>
public sealed class Environment
{
    private readonly EnvironmentsClient? _client;
    private Color? _color;

    /// <summary>Gets or sets the environment identifier (slug). Null for unsaved environments.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets the color. Accepts a <see cref="Smplkit.Platform.Color"/>, a hex
    /// string (via the implicit <c>string → Color</c> conversion), or null. Hex strings
    /// are validated at the SDK boundary (see <see cref="Smplkit.Platform.Color(string)"/>
    /// for the accepted formats); invalid hex throws <see cref="ArgumentException"/> at
    /// the assignment site, before any wire call.
    /// </summary>
    public Color? Color
    {
        get => _color;
        set => _color = value;
    }

    /// <summary>Gets or sets the classification.</summary>
    public EnvironmentClassification Classification { get; set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal Environment(
        EnvironmentsClient? client,
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
        _color = color;
        Classification = classification;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>Create or update this environment on the server.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Environment was constructed without a client; cannot save");
        var other = await _client.SaveInternalAsync(this, ct).ConfigureAwait(false);
        Apply(other);
    }

    /// <summary>Delete this environment from the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (_client is null || Id is null)
            throw new InvalidOperationException("Environment was constructed without a client or id; cannot delete");
        return _client.DeleteAsync(Id, ct);
    }

    private void Apply(Environment other)
    {
        Id = other.Id;
        Name = other.Name;
        _color = other._color;
        Classification = other.Classification;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Environment(Id={Id}, Name={Name}, Classification={Classification})";
}

// ---------------------------------------------------------------------------
// Service
// ---------------------------------------------------------------------------

/// <summary>
/// Service resource. Mutate fields, then call <see cref="SaveAsync"/>.
/// </summary>
public sealed class Service
{
    private readonly ServicesClient? _client;

    /// <summary>Gets or sets the service identifier (slug). Null for unsaved services.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal Service(
        ServicesClient? client,
        string? id,
        string name,
        DateTime? createdAt,
        DateTime? updatedAt)
    {
        _client = client;
        Id = id;
        Name = name;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>Create or update this service on the server.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Service was constructed without a client; cannot save");
        var other = await _client.SaveInternalAsync(this, ct).ConfigureAwait(false);
        Apply(other);
    }

    /// <summary>Delete this service from the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (_client is null || Id is null)
            throw new InvalidOperationException("Service was constructed without a client or id; cannot delete");
        return _client.DeleteAsync(Id, ct);
    }

    private void Apply(Service other)
    {
        Id = other.Id;
        Name = other.Name;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
    }

    /// <inheritdoc />
    public override string ToString() => $"Service(Id={Id}, Name={Name})";
}

// ---------------------------------------------------------------------------
// ContextType
// ---------------------------------------------------------------------------

/// <summary>
/// Context type resource. Mutate fields, then call <see cref="SaveAsync"/>.
/// </summary>
public sealed class ContextType
{
    private readonly ContextTypesClient? _client;

    /// <summary>Gets or sets the context type identifier (slug). Null for unsaved types.</summary>
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
        ContextTypesClient? client,
        string? id,
        string name,
        Dictionary<string, Dictionary<string, object?>>? attributes,
        DateTime? createdAt,
        DateTime? updatedAt)
    {
        _client = client;
        Id = id;
        Name = name;
        Attributes = attributes is not null
            ? new Dictionary<string, Dictionary<string, object?>>(attributes)
            : new Dictionary<string, Dictionary<string, object?>>();
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>Add a known-attribute slot. Local; call <see cref="SaveAsync"/> to persist.</summary>
    public void AddAttribute(string name, Dictionary<string, object?>? metadata = null)
    {
        Attributes[name] = metadata is not null
            ? new Dictionary<string, object?>(metadata)
            : new Dictionary<string, object?>();
    }

    /// <summary>Remove a known-attribute slot. Local; call <see cref="SaveAsync"/> to persist.</summary>
    public void RemoveAttribute(string name) => Attributes.Remove(name);

    /// <summary>Replace a known-attribute slot's metadata. Local; call <see cref="SaveAsync"/>.</summary>
    public void UpdateAttribute(string name, Dictionary<string, object?>? metadata = null)
    {
        Attributes[name] = metadata is not null
            ? new Dictionary<string, object?>(metadata)
            : new Dictionary<string, object?>();
    }

    /// <summary>Create or update this context type on the server.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("ContextType was constructed without a client; cannot save");
        var other = await _client.SaveInternalAsync(this, ct).ConfigureAwait(false);
        Apply(other);
    }

    /// <summary>Delete this context type from the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (_client is null || Id is null)
            throw new InvalidOperationException("ContextType was constructed without a client or id; cannot delete");
        return _client.DeleteAsync(Id, ct);
    }

    private void Apply(ContextType other)
    {
        Id = other.Id;
        Name = other.Name;
        Attributes = new Dictionary<string, Dictionary<string, object?>>(other.Attributes);
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
    }

    /// <inheritdoc />
    public override string ToString() => $"ContextType(Id={Id}, Name={Name})";
}
