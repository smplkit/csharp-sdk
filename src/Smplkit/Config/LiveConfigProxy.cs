using System.Collections;
using Smplkit.Errors;

namespace Smplkit.Config;

/// <summary>
/// A live, dict-like view of resolved config values.
/// </summary>
/// <remarks>
/// <para>Returned by <see cref="ConfigClient.Subscribe(string)"/>. Always reflects
/// the latest server-pushed state — every read sees current values.</para>
/// <para>For typed access via a declarative schema, use
/// <see cref="ConfigClient.Bind{T}(string, T, object?)"/> instead — bound
/// instances stay live on the same WebSocket cache, with attribute access
/// typed by the customer's POCO class.</para>
/// <para>Implements <see cref="IReadOnlyDictionary{TKey,TValue}"/> so
/// idiomatic C# patterns work: <c>proxy["key"]</c>,
/// <c>proxy.ContainsKey("key")</c>, <c>foreach (var kv in proxy)</c>,
/// <c>proxy.Count</c>, <c>proxy.Keys</c>, <c>proxy.Values</c>,
/// <c>proxy.TryGetValue(key, out var v)</c>.</para>
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

    private IReadOnlyDictionary<string, object?> Snapshot()
    {
        if (!_client.HasResolved(_configId))
            _client.EnsureConnected();

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

    /// <summary>Get a value with a fallback default if the key is missing.</summary>
    public object? GetOrDefault(string key, object? defaultValue = null)
        => Snapshot().TryGetValue(key, out var value) ? value : defaultValue;

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
}
