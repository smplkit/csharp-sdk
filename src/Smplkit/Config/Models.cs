namespace Smplkit.Config;

/// <summary>
/// Represents a configuration resource from the smplkit Config service.
/// </summary>
/// <param name="Id">Unique identifier (UUID).</param>
/// <param name="Key">Human-readable config key.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Parent">Parent config UUID, or null for root configs.</param>
/// <param name="Items">Base items dictionary (raw values extracted from typed wrappers).</param>
/// <param name="Environments">Dictionary mapping environment names to their override values (raw values extracted from wrappers).</param>
/// <param name="CreatedAt">Creation timestamp.</param>
/// <param name="UpdatedAt">Last-modified timestamp.</param>
public sealed record Config(
    string Id,
    string Key,
    string Name,
    string? Description,
    string? Parent,
    Dictionary<string, object?> Items,
    Dictionary<string, Dictionary<string, object?>> Environments,
    DateTime? CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>
/// Options for creating or updating a configuration.
/// </summary>
public sealed record CreateConfigOptions
{
    /// <summary>Gets the display name for the config.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the human-readable key. Auto-generated if omitted.</summary>
    public string? Key { get; init; }

    /// <summary>Gets the optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the parent config UUID.</summary>
    public string? Parent { get; init; }

    /// <summary>Gets the initial base items (raw key-value pairs).</summary>
    public Dictionary<string, object?>? Items { get; init; }

    /// <summary>
    /// Gets the environment-specific overrides. Each key is an environment name;
    /// each value is a dict of override key-value pairs.
    /// </summary>
    public Dictionary<string, object?>? Environments { get; init; }
}

/// <summary>
/// Describes a single config value change detected during refresh.
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

