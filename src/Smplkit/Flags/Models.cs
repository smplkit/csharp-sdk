using System.Text.Json.Serialization;

namespace Smplkit.Flags;

/// <summary>
/// Represents a flag resource from the smplkit Flags service.
/// </summary>
public sealed class Flag
{
    private readonly FlagsClient _client;

    /// <summary>Gets the flag UUID.</summary>
    public string Id { get; internal set; }

    /// <summary>Gets the flag key.</summary>
    public string Key { get; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; internal set; }

    /// <summary>Gets the flag type (BOOLEAN, STRING, NUMERIC, JSON).</summary>
    public string Type { get; }

    /// <summary>Gets the flag-level default value.</summary>
    public object? Default { get; internal set; }

    /// <summary>Gets the closed set of legal values.</summary>
    public List<Dictionary<string, object?>> Values { get; internal set; }

    /// <summary>Gets the optional description.</summary>
    public string? Description { get; internal set; }

    /// <summary>Gets the environments configuration.</summary>
    public Dictionary<string, Dictionary<string, object?>> Environments { get; internal set; }

    /// <summary>Gets the creation timestamp.</summary>
    public object? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public object? UpdatedAt { get; internal set; }

    internal Flag(
        FlagsClient client,
        string id,
        string key,
        string name,
        string type,
        object? @default,
        List<Dictionary<string, object?>> values,
        string? description,
        Dictionary<string, Dictionary<string, object?>> environments,
        object? createdAt,
        object? updatedAt)
    {
        _client = client;
        Id = id;
        Key = key;
        Name = name;
        Type = type;
        Default = @default;
        Values = values;
        Description = description;
        Environments = environments;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Update this flag's definition on the server.
    /// </summary>
    /// <param name="environments">New environment configuration.</param>
    /// <param name="values">New values array.</param>
    /// <param name="default">New default value.</param>
    /// <param name="description">New description.</param>
    /// <param name="name">New display name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateAsync(
        Dictionary<string, Dictionary<string, object?>>? environments = null,
        List<Dictionary<string, object?>>? values = null,
        object? @default = null,
        string? description = null,
        string? name = null,
        CancellationToken ct = default)
    {
        var updated = await _client.UpdateFlagInternalAsync(
            flag: this,
            environments: environments,
            values: values,
            @default: @default,
            description: description,
            name: name,
            ct: ct).ConfigureAwait(false);

        // Apply result to self
        Id = updated.Id;
        Name = updated.Name;
        Default = updated.Default;
        Values = updated.Values;
        Description = updated.Description;
        Environments = updated.Environments;
        CreatedAt = updated.CreatedAt;
        UpdatedAt = updated.UpdatedAt;
    }

    /// <summary>
    /// Add a rule to an environment. The built rule must include an
    /// "environment" key (set via <see cref="Rule.Environment"/>).
    /// </summary>
    /// <param name="builtRule">A rule dict from <see cref="Rule.Build"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddRuleAsync(Dictionary<string, object?> builtRule, CancellationToken ct = default)
    {
        if (!builtRule.TryGetValue("environment", out var envObj) || envObj is not string envKey)
            throw new ArgumentException("Built rule must include an 'environment' key.", nameof(builtRule));

        // Re-fetch current state to avoid stale data
        var current = await _client.GetAsync(Id, ct).ConfigureAwait(false);

        var envs = new Dictionary<string, Dictionary<string, object?>>(current.Environments);

        if (!envs.TryGetValue(envKey, out var envConfig))
            envConfig = new Dictionary<string, object?> { ["enabled"] = true, ["rules"] = new List<object?>() };
        else
            envConfig = new Dictionary<string, object?>(envConfig);

        var rules = envConfig.TryGetValue("rules", out var rulesObj) && rulesObj is List<object?> existingRules
            ? new List<object?>(existingRules)
            : new List<object?>();

        // Remove the "environment" key from the rule before appending
        var ruleForApi = new Dictionary<string, object?>(builtRule);
        ruleForApi.Remove("environment");
        rules.Add(ruleForApi);
        envConfig["rules"] = rules;
        envs[envKey] = envConfig;

        await UpdateAsync(environments: envs, ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Flag(Key={Key}, Type={Type}, Default={Default})";
}

/// <summary>
/// Describes a flag definition change.
/// </summary>
/// <param name="Key">The flag key that changed.</param>
/// <param name="Source">How the change was delivered: "websocket" or "manual".</param>
public sealed record FlagChangeEvent(string Key, string Source);

/// <summary>
/// Cache statistics for the flags runtime.
/// </summary>
/// <param name="CacheHits">Number of cache hits.</param>
/// <param name="CacheMisses">Number of cache misses.</param>
public sealed record FlagStats(int CacheHits, int CacheMisses);

/// <summary>
/// A context type resource from the management API.
/// </summary>
public sealed class ContextType
{
    /// <summary>Gets the context type UUID.</summary>
    public string Id { get; }

    /// <summary>Gets the context type key.</summary>
    public string Key { get; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; }

    /// <summary>Gets the known attributes.</summary>
    public Dictionary<string, object?> Attributes { get; }

    internal ContextType(string id, string key, string name, Dictionary<string, object?> attributes)
    {
        Id = id;
        Key = key;
        Name = name;
        Attributes = attributes;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"ContextType(Key={Key}, Name={Name})";
}

// ------------------------------------------------------------------
// Internal: JSON:API envelope types for flags
// ------------------------------------------------------------------

internal sealed class FlagApiSingleResponse
{
    [JsonPropertyName("data")]
    public FlagApiResource? Data { get; set; }
}

internal sealed class FlagApiListResponse
{
    [JsonPropertyName("data")]
    public List<FlagApiResource>? Data { get; set; }
}

internal sealed class FlagApiResource
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("attributes")]
    public FlagApiAttributes? Attributes { get; set; }
}

internal sealed class FlagApiAttributes
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? FlagType { get; set; }

    [JsonPropertyName("default")]
    public object? Default { get; set; }

    [JsonPropertyName("values")]
    public List<FlagApiValue>? Values { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("environments")]
    public Dictionary<string, Dictionary<string, object?>>? Environments { get; set; }

    [JsonPropertyName("created_at")]
    public object? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public object? UpdatedAt { get; set; }
}

internal sealed class FlagApiValue
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

internal sealed class ContextTypeApiSingleResponse
{
    [JsonPropertyName("data")]
    public ContextTypeApiResource? Data { get; set; }
}

internal sealed class ContextTypeApiListResponse
{
    [JsonPropertyName("data")]
    public List<ContextTypeApiResource>? Data { get; set; }
}

internal sealed class ContextTypeApiResource
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("attributes")]
    public ContextTypeApiAttributes? Attributes { get; set; }
}

internal sealed class ContextTypeApiAttributes
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; set; }
}

internal sealed class ContextApiListResponse
{
    [JsonPropertyName("data")]
    public List<Dictionary<string, object?>>? Data { get; set; }
}
