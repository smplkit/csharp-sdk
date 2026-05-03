using System.Collections;
using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;

namespace Smplkit.Config;

/// <summary>
/// A live, dict-like view of resolved config values. Returned by
/// <see cref="ConfigClient.Get(string)"/>. Always reflects the latest
/// server-pushed state — every read goes through the client's resolved-config
/// cache, so WebSocket updates are picked up automatically with no
/// <c>Subscribe</c> step.
/// </summary>
/// <remarks>
/// <para>Mirrors the Python SDK's <c>LiveConfigProxy</c> (PR #127 rule 10):
/// dict-like access, listener sugar, read-only mutation guards.</para>
/// <para>Also implements <see cref="IReadOnlyDictionary{TKey,TValue}"/> so
/// idiomatic C# patterns work: <c>proxy["key"]</c>, <c>proxy.ContainsKey("key")</c>,
/// <c>foreach (var kv in proxy)</c>, <c>proxy.Count</c>, <c>proxy.Keys</c>,
/// <c>proxy.Values</c>, <c>proxy.TryGetValue(key, out var v)</c>.</para>
/// <para>Mutation paths (indexer setter, etc.) are not exposed —
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/> doesn't surface them.
/// To mutate, use <c>client.Manage.Config.*</c>.</para>
/// </remarks>
public sealed class LiveConfigProxy : IReadOnlyDictionary<string, object?>
{
    private readonly ConfigClient _client;
    private readonly string _configId;

    /// <summary>The config id this proxy is bound to.</summary>
    public string ConfigId => _configId;

    internal LiveConfigProxy(ConfigClient client, string configId)
    {
        _client = client;
        _configId = configId;
    }

    /// <summary>
    /// Reads the current resolved values from the client's cache. Each call
    /// returns a fresh snapshot — the proxy itself is identity-stable, but the
    /// values it exposes are always current.
    /// </summary>
    private IReadOnlyDictionary<string, object?> Snapshot()
    {
        // Trigger lazy init if not yet connected
        if (!_client.HasResolved(_configId))
            _client.EnsureInitialized();

        return _client.GetCachedValues(_configId)
            ?? throw new NotFoundException($"Config with id '{_configId}' not found in cache.");
    }

    /// <summary>Lookup a value by key. Throws <see cref="KeyNotFoundException"/> if absent.</summary>
    public object? this[string key] => Snapshot()[key];

    /// <inheritdoc />
    public IEnumerable<string> Keys => Snapshot().Keys;

    /// <inheritdoc />
    public IEnumerable<object?> Values => Snapshot().Values;

    /// <inheritdoc />
    public int Count => Snapshot().Count;

    /// <inheritdoc />
    public bool ContainsKey(string key) => Snapshot().ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(string key, out object? value) => Snapshot().TryGetValue(key, out value);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Get a value with a fallback default if the key is missing. Mirrors
    /// Python's <c>proxy.get(key, default)</c>.
    /// </summary>
    public object? GetOrDefault(string key, object? defaultValue = null)
        => Snapshot().TryGetValue(key, out var value) ? value : defaultValue;

    /// <summary>
    /// Deserializes the current snapshot into a typed model. Dot-notation keys
    /// (e.g. <c>"db.host"</c>) map to nested properties on the target type.
    /// Each call deserializes anew, so reads see the latest values.
    /// </summary>
    public T Into<T>() where T : new()
    {
        var flat = Snapshot();
        var nested = ExpandDotNotation(flat);
        var json = JsonSerializer.Serialize(nested, JsonOptions.Default);
        return JsonSerializer.Deserialize<T>(json, JsonOptions.Default)
            ?? throw new SmplkitException(
                $"Failed to deserialize config '{_configId}' to {typeof(T).Name}");
    }

    /// <summary>
    /// Register a change listener scoped to this config — sugar for
    /// <c>client.Config.OnChange(configId, callback)</c>.
    /// </summary>
    public void OnChange(Action<ConfigChangeEvent> callback)
        => _client.OnChange(_configId, callback);

    /// <summary>
    /// Register a change listener scoped to a specific item within this
    /// config — sugar for <c>client.Config.OnChange(configId, itemKey, callback)</c>.
    /// </summary>
    public void OnChange(string itemKey, Action<ConfigChangeEvent> callback)
        => _client.OnChange(_configId, itemKey, callback);

    /// <inheritdoc />
    public override string ToString() => $"LiveConfigProxy(ConfigId={_configId})";

    private static Dictionary<string, object?> ExpandDotNotation(IReadOnlyDictionary<string, object?> flat)
    {
        var nested = new Dictionary<string, object?>();
        foreach (var (key, value) in flat)
        {
            var parts = key.Split('.');
            var current = nested;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!current.TryGetValue(parts[i], out var next) || next is not Dictionary<string, object?> nextDict)
                {
                    nextDict = new Dictionary<string, object?>();
                    current[parts[i]] = nextDict;
                }
                current = nextDict;
            }
            current[parts[^1]] = value;
        }
        return nested;
    }
}
