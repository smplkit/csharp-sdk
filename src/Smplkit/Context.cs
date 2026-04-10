namespace Smplkit;

/// <summary>
/// Represents a single entity (user, account, device, etc.) used for targeting
/// in flag evaluation and config resolution.
/// </summary>
/// <example>
/// <code>
/// new Context("user", "user-123", new Dictionary&lt;string, object?&gt; { ["plan"] = "enterprise" })
/// new Context("user", "user-123", new Dictionary&lt;string, object?&gt; { ["plan"] = "enterprise" }, name: "Alice")
/// </code>
/// </example>
public sealed class Context
{
    /// <summary>Gets the context type (e.g., "user", "account", "device").</summary>
    public string Type { get; }

    /// <summary>Gets the entity identifier (e.g., "user-123", "acme-corp").</summary>
    public string Key { get; }

    /// <summary>Gets the optional display name.</summary>
    public string? Name { get; }

    /// <summary>Gets the context attributes used for targeting rules.</summary>
    public Dictionary<string, object?> Attributes { get; }

    /// <summary>
    /// Initializes a new <see cref="Context"/> with the specified type, key, and optional attributes.
    /// </summary>
    /// <param name="type">The context type (e.g., "user", "account").</param>
    /// <param name="key">The entity identifier.</param>
    /// <param name="attributes">Optional attribute dictionary.</param>
    /// <param name="name">Optional display name.</param>
    public Context(
        string type,
        string key,
        Dictionary<string, object?>? attributes = null,
        string? name = null)
    {
        Type = type;
        Key = key;
        Name = name;
        Attributes = attributes ?? new Dictionary<string, object?>();
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Context(Type={Type}, Key={Key}, Name={Name}, Attributes=[{string.Join(", ", Attributes.Keys)}])";
}
