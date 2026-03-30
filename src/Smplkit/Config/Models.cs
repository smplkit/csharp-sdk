using System.Text.Json.Serialization;

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
/// Describes a single value change pushed by the config service.
/// </summary>
/// <param name="Key">The config key that changed.</param>
/// <param name="OldValue">The previous value.</param>
/// <param name="NewValue">The updated value.</param>
/// <param name="Source">How the change was delivered: <c>"websocket"</c>, <c>"poll"</c>, or <c>"manual"</c>.</param>
public sealed record ConfigChangeEvent(
    string Key,
    object? OldValue,
    object? NewValue,
    string Source
);

/// <summary>
/// Diagnostic statistics for a <see cref="ConfigRuntime"/> instance.
/// </summary>
/// <param name="FetchCount">Total HTTP fetches performed (initial connect + reconnects + manual refreshes).</param>
/// <param name="LastFetchAt">ISO-8601 timestamp of the most recent fetch, or <c>null</c> if none.</param>
public sealed record ConfigStats(
    int FetchCount,
    string? LastFetchAt
);

/// <summary>
/// Internal JSON:API envelope for a single config resource response.
/// </summary>
internal sealed class JsonApiSingleResponse
{
    /// <summary>Gets or sets the data element.</summary>
    [JsonPropertyName("data")]
    public JsonApiResource? Data { get; set; }
}

/// <summary>
/// Internal JSON:API envelope for a list of config resources.
/// </summary>
internal sealed class JsonApiListResponse
{
    /// <summary>Gets or sets the data array.</summary>
    [JsonPropertyName("data")]
    public List<JsonApiResource>? Data { get; set; }
}

/// <summary>
/// Internal JSON:API resource object.
/// </summary>
internal sealed class JsonApiResource
{
    /// <summary>Gets or sets the resource ID.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets the resource type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Gets or sets the resource attributes.</summary>
    [JsonPropertyName("attributes")]
    public JsonApiConfigAttributes? Attributes { get; set; }
}

/// <summary>
/// Internal JSON:API config attributes.
/// </summary>
internal sealed class JsonApiConfigAttributes
{
    /// <summary>Gets or sets the config key.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>Gets or sets the config name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the config description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Gets or sets the parent config UUID.</summary>
    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    /// <summary>Gets or sets the base items (typed wrappers on the wire).</summary>
    [JsonPropertyName("items")]
    public Dictionary<string, Dictionary<string, object?>>? Items { get; set; }

    /// <summary>Gets or sets the environment overrides (value wrappers on the wire).</summary>
    [JsonPropertyName("environments")]
    public Dictionary<string, Dictionary<string, object?>>? Environments { get; set; }

    /// <summary>Gets or sets the creation timestamp.</summary>
    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    /// <summary>Gets or sets the last-modified timestamp.</summary>
    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Internal JSON:API request body for create/update operations.
/// </summary>
internal sealed class JsonApiRequestBody
{
    /// <summary>Gets or sets the data element.</summary>
    [JsonPropertyName("data")]
    public JsonApiRequestResource? Data { get; set; }
}

/// <summary>
/// Internal JSON:API request resource.
/// </summary>
internal sealed class JsonApiRequestResource
{
    /// <summary>Gets or sets the resource type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "config";

    /// <summary>Gets or sets the resource attributes.</summary>
    [JsonPropertyName("attributes")]
    public JsonApiRequestAttributes? Attributes { get; set; }
}

/// <summary>
/// Internal JSON:API request attributes for config creation.
/// </summary>
internal sealed class JsonApiRequestAttributes
{
    /// <summary>Gets or sets the config name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the config key.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>Gets or sets the config description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Gets or sets the parent config UUID.</summary>
    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    /// <summary>Gets or sets the base items (typed wrappers for the wire).</summary>
    [JsonPropertyName("items")]
    public Dictionary<string, object?>? Items { get; set; }

    /// <summary>Gets or sets the environment-specific overrides (value wrappers for the wire).</summary>
    [JsonPropertyName("environments")]
    public Dictionary<string, object?>? Environments { get; set; }
}
