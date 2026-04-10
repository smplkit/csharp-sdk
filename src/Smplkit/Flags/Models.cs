using System.Text.Json;

namespace Smplkit.Flags;

/// <summary>
/// Represents a flag resource from the smplkit Flags service.
/// Supports both management (CRUD via <see cref="SaveAsync"/>) and
/// runtime (evaluation via <see cref="Get"/>) operations.
/// </summary>
public class Flag
{
    private readonly FlagsClient _client;

    /// <summary>Gets the flag UUID. Null for unsaved flags.</summary>
    public string? Id { get; internal set; }

    /// <summary>Gets or sets the flag key.</summary>
    public string Key { get; internal set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; }

    /// <summary>Gets the flag type (BOOLEAN, STRING, NUMERIC, JSON).</summary>
    public string Type { get; internal set; }

    /// <summary>Gets or sets the flag-level default value.</summary>
    public object? Default { get; set; }

    /// <summary>Gets or sets the closed set of legal values (constrained), or null (unconstrained).</summary>
    public List<Dictionary<string, object?>>? Values { get; set; }

    /// <summary>Gets or sets the optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the environments configuration.</summary>
    public Dictionary<string, Dictionary<string, object?>> Environments { get; set; }

    /// <summary>Gets the creation timestamp.</summary>
    public DateTime? CreatedAt { get; internal set; }

    /// <summary>Gets the last-modified timestamp.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    internal Flag(
        FlagsClient client,
        string? id,
        string key,
        string name,
        string type,
        object? @default,
        List<Dictionary<string, object?>>? values,
        string? description,
        Dictionary<string, Dictionary<string, object?>> environments,
        DateTime? createdAt,
        DateTime? updatedAt)
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
    /// Persist this flag to the server. Creates if new, updates if existing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var saved = await _client.SaveFlagInternalAsync(this, ct).ConfigureAwait(false);
        Id = saved.Id;
        Key = saved.Key;
        Name = saved.Name;
        Default = saved.Default;
        Values = saved.Values;
        Description = saved.Description;
        Environments = saved.Environments;
        CreatedAt = saved.CreatedAt;
        UpdatedAt = saved.UpdatedAt;
    }

    /// <summary>
    /// Add a rule to an environment. This is a local mutation — call
    /// <see cref="SaveAsync"/> to persist. The built rule must include an
    /// "environment" key (set via <see cref="Rule.Environment"/>).
    /// </summary>
    /// <param name="builtRule">A rule dict from <see cref="Rule.Build"/>.</param>
    /// <returns>This flag for chaining.</returns>
    public Flag AddRule(Dictionary<string, object?> builtRule)
    {
        if (!builtRule.TryGetValue("environment", out var envObj) || envObj is not string envKey)
            throw new ArgumentException("Built rule must include an 'environment' key.", nameof(builtRule));

        if (!Environments.TryGetValue(envKey, out var envConfig))
        {
            envConfig = new Dictionary<string, object?> { ["enabled"] = true, ["rules"] = new List<object?>() };
            Environments[envKey] = envConfig;
        }

        var rules = envConfig.TryGetValue("rules", out var rulesObj) && rulesObj is List<object?> existingRules
            ? existingRules
            : new List<object?>();

        var ruleForStorage = new Dictionary<string, object?>(builtRule);
        ruleForStorage.Remove("environment");
        rules.Add(ruleForStorage);
        envConfig["rules"] = rules;

        return this;
    }

    /// <summary>
    /// Set whether an environment is enabled for this flag. Local mutation only.
    /// </summary>
    /// <param name="envKey">The environment key.</param>
    /// <param name="enabled">Whether the environment is enabled.</param>
    public void SetEnvironmentEnabled(string envKey, bool enabled)
    {
        if (!Environments.TryGetValue(envKey, out var envConfig))
        {
            envConfig = new Dictionary<string, object?>();
            Environments[envKey] = envConfig;
        }
        envConfig["enabled"] = enabled;
    }

    /// <summary>
    /// Set the default value for a specific environment. Local mutation only.
    /// </summary>
    /// <param name="envKey">The environment key.</param>
    /// <param name="defaultValue">The default value for the environment.</param>
    public void SetEnvironmentDefault(string envKey, object? defaultValue)
    {
        if (!Environments.TryGetValue(envKey, out var envConfig))
        {
            envConfig = new Dictionary<string, object?>();
            Environments[envKey] = envConfig;
        }
        envConfig["default"] = defaultValue;
    }

    /// <summary>
    /// Clear all rules for a specific environment. Local mutation only.
    /// </summary>
    /// <param name="envKey">The environment key.</param>
    public void ClearRules(string envKey)
    {
        if (Environments.TryGetValue(envKey, out var envConfig))
            envConfig["rules"] = new List<object?>();
    }

    /// <summary>
    /// Evaluate this flag and return its current value.
    /// </summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The evaluated value.</returns>
    public object? Get(IReadOnlyList<Context>? context = null)
    {
        return _client.EvaluateHandle(Key, Default, context);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Flag(Key={Key}, Type={Type}, Default={Default})";
}

/// <summary>Typed flag for boolean values.</summary>
public sealed class BooleanFlag : Flag
{
    internal BooleanFlag(
        FlagsClient client, string? id, string key, string name,
        object? @default, List<Dictionary<string, object?>> values,
        string? description, Dictionary<string, Dictionary<string, object?>> environments,
        DateTime? createdAt, DateTime? updatedAt)
        : base(client, id, key, name, "BOOLEAN", @default, values, description, environments, createdAt, updatedAt)
    {
    }

    /// <summary>Evaluate the flag and return a bool.</summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The evaluated boolean value.</returns>
    public new bool Get(IReadOnlyList<Context>? context = null)
    {
        var value = base.Get(context);
        if (value is bool b) return b;
        if (value is JsonElement je && je.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return je.GetBoolean();
        return Default is bool db ? db : false;
    }
}

/// <summary>Typed flag for string values.</summary>
public sealed class StringFlag : Flag
{
    internal StringFlag(
        FlagsClient client, string? id, string key, string name,
        object? @default, List<Dictionary<string, object?>>? values,
        string? description, Dictionary<string, Dictionary<string, object?>> environments,
        DateTime? createdAt, DateTime? updatedAt)
        : base(client, id, key, name, "STRING", @default, values, description, environments, createdAt, updatedAt)
    {
    }

    /// <summary>Evaluate the flag and return a string.</summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The evaluated string value.</returns>
    public new string Get(IReadOnlyList<Context>? context = null)
    {
        var value = base.Get(context);
        if (value is string s) return s;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString()!;
        return Default as string ?? string.Empty;
    }
}

/// <summary>Typed flag for numeric values.</summary>
public sealed class NumberFlag : Flag
{
    internal NumberFlag(
        FlagsClient client, string? id, string key, string name,
        object? @default, List<Dictionary<string, object?>>? values,
        string? description, Dictionary<string, Dictionary<string, object?>> environments,
        DateTime? createdAt, DateTime? updatedAt)
        : base(client, id, key, name, "NUMERIC", @default, values, description, environments, createdAt, updatedAt)
    {
    }

    /// <summary>Evaluate the flag and return a number.</summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The evaluated numeric value.</returns>
    public new double Get(IReadOnlyList<Context>? context = null)
    {
        var value = base.Get(context);
        if (value is double d) return d;
        if (value is int i) return i;
        if (value is long l) return l;
        if (value is float f) return f;
        if (value is decimal dec) return (double)dec;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number)
            return je.TryGetInt64(out var jl) ? jl : je.GetDouble();
        return Default is double dd ? dd : 0.0;
    }
}

/// <summary>Typed flag for JSON object values.</summary>
public sealed class JsonFlag : Flag
{
    internal JsonFlag(
        FlagsClient client, string? id, string key, string name,
        object? @default, List<Dictionary<string, object?>>? values,
        string? description, Dictionary<string, Dictionary<string, object?>> environments,
        DateTime? createdAt, DateTime? updatedAt)
        : base(client, id, key, name, "JSON", @default, values, description, environments, createdAt, updatedAt)
    {
    }

    /// <summary>Evaluate the flag and return a dictionary.</summary>
    /// <param name="context">Optional explicit context override.</param>
    /// <returns>The evaluated dictionary value.</returns>
    public new Dictionary<string, object?> Get(IReadOnlyList<Context>? context = null)
    {
        var value = base.Get(context);
        if (value is Dictionary<string, object?> dict) return dict;
        return Default as Dictionary<string, object?> ?? new Dictionary<string, object?>();
    }
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
