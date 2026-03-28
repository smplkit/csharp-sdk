using System.Text.Json;

namespace Smplkit.Config;

/// <summary>
/// Internal config chain entry holding normalized values for one config in the parent hierarchy.
/// The chain is ordered child-first, root-last.
/// </summary>
internal sealed class ConfigChainEntry
{
    /// <summary>Gets or sets the config UUID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets or sets the normalized base values dict.</summary>
    public Dictionary<string, object?> Values { get; set; } = new();

    /// <summary>
    /// Gets or sets the normalized per-environment values.
    /// Key: environment name. Value: flat resolved values dict for that environment.
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>> EnvValues { get; set; } = new();
}

/// <summary>
/// Deep-merge resolution algorithm for config inheritance chains.
/// Mirrors ADR-024 sections 2.5 and 2.6 from the Python SDK.
/// </summary>
internal static class Resolver
{
    /// <summary>
    /// Resolve the full configuration for an environment from a child-to-root chain.
    /// Walks from root to child so that child values override parent values.
    /// </summary>
    public static Dictionary<string, object?> Resolve(
        IReadOnlyList<ConfigChainEntry> chain, string environment)
    {
        var accumulated = new Dictionary<string, object?>();

        // Walk root → child (reverse of the child-first chain)
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var entry = chain[i];
            var envValues = entry.EnvValues.GetValueOrDefault(environment, new());
            // Env overrides win over base values within the same config
            var configResolved = DeepMerge(entry.Values, envValues);
            // Child configs win over parent configs
            accumulated = DeepMerge(accumulated, configResolved);
        }

        return accumulated;
    }

    /// <summary>
    /// Recursively merge two dicts, with <paramref name="override"/> taking precedence.
    /// Nested dicts are merged recursively; non-dict values are replaced wholesale.
    /// </summary>
    public static Dictionary<string, object?> DeepMerge(
        Dictionary<string, object?> @base,
        Dictionary<string, object?> @override)
    {
        var result = new Dictionary<string, object?>(@base);
        foreach (var (key, value) in @override)
        {
            if (result.TryGetValue(key, out var existing)
                && existing is Dictionary<string, object?> existingDict
                && value is Dictionary<string, object?> overrideDict)
            {
                result[key] = DeepMerge(existingDict, overrideDict);
            }
            else
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// Build a <see cref="ConfigChainEntry"/> from a <see cref="Config"/> record,
    /// normalizing all values so there are no <see cref="JsonElement"/> references.
    /// </summary>
    public static ConfigChainEntry ToChainEntry(Config config)
    {
        var values = NormalizeDict(config.Values);

        var envValues = new Dictionary<string, Dictionary<string, object?>>(
            config.Environments.Count);

        foreach (var (envName, envData) in config.Environments)
        {
            // envData is Dictionary<string, object?> shaped as {"values": <JsonElement>}
            var normalized = NormalizeDict(envData);
            if (normalized.TryGetValue("values", out var v)
                && v is Dictionary<string, object?> vals)
            {
                envValues[envName] = vals;
            }
            // If no "values" key, this env has no overrides — skip it
        }

        return new ConfigChainEntry
        {
            Id = config.Id,
            Values = values,
            EnvValues = envValues,
        };
    }

    /// <summary>Normalize a <c>Dictionary&lt;string, object?&gt;</c>, converting any
    /// <see cref="JsonElement"/> values to native .NET types.</summary>
    public static Dictionary<string, object?> NormalizeDict(Dictionary<string, object?> dict)
    {
        var result = new Dictionary<string, object?>(dict.Count);
        foreach (var (k, v) in dict)
            result[k] = Normalize(v);
        return result;
    }

    /// <summary>Recursively normalize a value, converting <see cref="JsonElement"/> to
    /// native .NET types.</summary>
    public static object? Normalize(object? value)
    {
        if (value is JsonElement je)
            return NormalizeJsonElement(je);
        if (value is Dictionary<string, object?> d)
            return NormalizeDict(d);
        return value;
    }

    private static object? NormalizeJsonElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Object => NormalizeDict(
            je.EnumerateObject().ToDictionary(p => p.Name, p => NormalizeJsonElement(p.Value))),
        JsonValueKind.Array => je.EnumerateArray().Select(NormalizeJsonElement).ToArray(),
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetInt64(out long l) ? (object?)l : je.GetDouble(),
        JsonValueKind.True => (object?)true,
        JsonValueKind.False => (object?)false,
        _ => null,
    };
}
