using Smplkit.Errors;

namespace Smplkit;

/// <summary>
/// Represents a single entity (user, account, device, etc.) used for targeting
/// in flag evaluation and config resolution.
/// </summary>
/// <remarks>
/// <para>Used for both authoring (<c>flag.Get(context: ...)</c>,
/// <c>client.SetContext([...])</c>, <c>mgmt.Contexts.RegisterAsync([...])</c>) and
/// reading (<c>mgmt.Contexts.GetAsync</c> / <c>ListAsync</c> return populated
/// <see cref="Context"/> instances with <see cref="SaveAsync"/> / <see cref="DeleteAsync"/>
/// ready to call).</para>
/// <para>Identity (<see cref="Type"/> / <see cref="Key"/>) is get-only at all times —
/// stricter than the Python SDK, which permits pre-save reassignment. In C# the
/// once-construct-then-immutable shape is more idiomatic and prevents the silent
/// "different entity" bug class entirely.</para>
/// </remarks>
/// <example>
/// <code>
/// new Context("user", "user-123", new Dictionary&lt;string, object?&gt; { ["plan"] = "enterprise" });
/// new Context("account", "acme-corp", new Dictionary&lt;string, object?&gt; { ["region"] = "us" });
/// </code>
/// </example>
public sealed class Context
{
    private readonly Smplkit.Management.IContextSink? _sink;
    private string _type;
    private string _key;

    /// <summary>Gets the context type (e.g., "user", "account", "device").</summary>
    public string Type => _type;

    /// <summary>Gets the entity identifier (e.g., "user-123", "acme-corp").</summary>
    public string Key => _key;

    /// <summary>Gets the optional display name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets the context attributes used for targeting rules.</summary>
    public Dictionary<string, object?> Attributes { get; set; }

    /// <summary>Gets the creation timestamp (null for unsaved contexts).</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp (null for unsaved contexts).</summary>
    public DateTime? UpdatedAt { get; internal set; }

    /// <summary>Gets the composite <c>"{Type}:{Key}"</c> identifier.</summary>
    public string Id => $"{_type}:{_key}";

    /// <summary>
    /// Initializes a new <see cref="Context"/> with the specified type, key, and optional attributes.
    /// </summary>
    /// <param name="type">The context type (e.g., "user", "account"). Must be non-null.</param>
    /// <param name="key">The entity identifier. Must be non-null.</param>
    /// <param name="attributes">Optional attribute dictionary.</param>
    /// <param name="name">Optional display name.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="type"/> or <paramref name="key"/> is null.</exception>
    public Context(
        string type,
        string key,
        Dictionary<string, object?>? attributes = null,
        string? name = null)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type),
                "Context type must be a string.");
        if (key is null)
            throw new ArgumentNullException(nameof(key),
                "Context key must be a string. If your identifier is numeric, "
                + "stringify it at the SDK boundary.");
        _type = type;
        _key = key;
        Name = name;
        Attributes = attributes is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(attributes);
    }

    internal Context(
        Smplkit.Management.IContextSink sink,
        string type,
        string key,
        Dictionary<string, object?>? attributes,
        string? name,
        DateTime? createdAt,
        DateTime? updatedAt)
        : this(type, key, attributes, name)
    {
        _sink = sink;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Persists this context to the server (create or update).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">If this context was constructed without a client.</exception>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_sink is null)
            throw new InvalidOperationException(
                "Context was constructed without a client; cannot save. "
                + "Use mgmt.Contexts.RegisterAsync(...) for input-only contexts.");
        var saved = await _sink.SaveContextAsync(this, ct).ConfigureAwait(false);
        ApplyServerState(saved);
    }

    /// <summary>
    /// Deletes this context from the server.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">If this context was constructed without a client.</exception>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (_sink is null)
            throw new InvalidOperationException(
                "Context was constructed without a client; cannot delete.");
        return _sink.DeleteContextAsync(Id, ct);
    }

    internal void ApplyServerState(Context other)
    {
        _type = other._type;
        _key = other._key;
        Name = other.Name;
        Attributes = new Dictionary<string, object?>(other.Attributes);
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Context(Type={_type}, Key={_key}, Name={Name}, Attributes=[{string.Join(", ", Attributes.Keys)}])";
}
