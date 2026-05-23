using System.Reflection;
using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using GenConfig = Smplkit.Internal.Generated.Config;
using DebugLog = Smplkit.Internal.Debug;

namespace Smplkit.Config;

/// <summary>
/// Runtime client for the smplkit Config service. Exposes the declarative
/// <see cref="Bind{T}(string, T, object?)"/> path (the recommended API),
/// the lookup-only <see cref="Get(string)"/> /
/// <see cref="GetValue(string, string)"/> /
/// <see cref="GetValueOr{T}(string, string, T)"/> escape hatches, and
/// change listeners. CRUD lives on <see cref="SmplManagementClient"/>
/// (<c>client.Manage.Config.*</c>).
/// </summary>
public sealed class ConfigClient
{
    private readonly GenConfig.ConfigClient _genClient;
    private readonly SmplClient? _parent;
    private readonly Func<SharedWebSocket>? _ensureWs;
    private readonly MetricsReporter? _metrics;
    private volatile bool _runtimeConnected;
    private readonly object _initLock = new();
    private Dictionary<string, Dictionary<string, object?>> _configCache = new();
    private readonly List<(Action<ConfigChangeEvent> Callback, string? ConfigId, string? ItemKey)> _listeners = new();
    private readonly object _listenerLock = new();
    private SharedWebSocket? _wsManager;

    // LiveConfigProxy instances for Get(id) callers; one per config id.
    private readonly Dictionary<string, LiveConfigProxy> _proxies = new();
    private readonly object _proxyLock = new();

    // POCO / dict instances bound via Bind(id, ...); one per config id.
    // WebSocket dispatch mutates these in place when values change.
    private readonly Dictionary<string, object> _bindings = new();
    private readonly object _bindingsLock = new();

    /// <summary>
    /// Initializes a new instance of <see cref="ConfigClient"/>.
    /// </summary>
    /// <param name="clients">The generated client factory.</param>
    /// <param name="ensureWs">Factory for the shared WebSocket.</param>
    /// <param name="parent">The parent <see cref="SmplClient"/>, if any.</param>
    /// <param name="metrics">Optional metrics reporter for telemetry.</param>
    internal ConfigClient(GeneratedClientFactory clients, Func<SharedWebSocket>? ensureWs = null, SmplClient? parent = null, MetricsReporter? metrics = null)
    {
        _genClient = clients.Config;
        _ensureWs = ensureWs;
        _parent = parent;
        _metrics = metrics;
    }

    // ------------------------------------------------------------------
    // Public API: Bind
    // ------------------------------------------------------------------

    /// <summary>
    /// Bind a POCO instance or dictionary to a config id; return the same
    /// object back, live. Declarative, code-first API.
    /// </summary>
    /// <remarks>
    /// <para>Two flavors:</para>
    /// <list type="bullet">
    /// <item><description><b>POCO instance:</b> the class is the schema;
    /// the instance carries the in-code defaults. Public read/write
    /// properties are walked; nested POCO properties flatten to
    /// dot-notation. PascalCase property names are converted to snake_case
    /// on the wire (e.g. <c>MaxSeats</c> → <c>max_seats</c>). Every
    /// reachable leaf property is registered as an explicit override —
    /// C# has no equivalent of Python's <c>model_fields_set</c>, so to get
    /// omit-to-inherit semantics, use a dictionary instead.</description></item>
    /// <item><description><b>Dictionary:</b> every key in the dict is a leaf
    /// to register, with its value as the in-code default. Nested
    /// <c>IDictionary&lt;string, object?&gt;</c> values flatten to
    /// dot-notation. Keys you want to inherit from a parent are simply
    /// omitted from the dict.</description></item>
    /// </list>
    /// <para>On first call the schema and values are registered with the
    /// server; the bound object's values are then synced from the cache (so
    /// existing server-side overrides apply). On every WebSocket-delivered
    /// change thereafter the bound object is mutated in place — readers
    /// always see the current resolved value with no proxy indirection.</para>
    /// <para>Idempotent. Repeated calls with the same <paramref name="id"/>
    /// return the originally-bound object; the new <paramref name="target"/>
    /// argument is ignored.</para>
    /// </remarks>
    /// <typeparam name="T">The POCO type, or <c>IDictionary&lt;string, object?&gt;</c>.</typeparam>
    /// <param name="id">The config identifier (slug).</param>
    /// <param name="target">A populated POCO instance or
    /// <c>IDictionary&lt;string, object?&gt;</c>. Supplies the schema and
    /// the in-code defaults.</param>
    /// <param name="parent">Optional parent: any object previously returned
    /// from a <see cref="Bind{T}(string, T, object?)"/> call. Activates
    /// parent-chain inheritance at the server.</param>
    /// <returns>The same <paramref name="target"/> instance, registered and live.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="target"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="parent"/> was not previously bound via <see cref="Bind{T}(string, T, object?)"/>.</exception>
    public T Bind<T>(string id, T target, object? parent = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_bindingsLock)
        {
            if (_bindings.TryGetValue(id, out var existing))
                return (T)existing;
        }

        string? parentId = null;
        if (parent is not null)
        {
            parentId = ConfigIdFor(parent);
            if (parentId is null)
                throw new ArgumentException(
                    "Bind(): parent must be an object previously returned from Config.Bind(). " +
                    "Bind the parent first.", nameof(parent));
        }

        string? configName = null;
        string? configDescription = null;
        var isDict = target is IDictionary<string, object?>;
        if (!isDict)
        {
            configName = target.GetType().Name;
            // No runtime-accessible XML doc comments → no description for POCO bind.
        }

        ObserveConfigDeclaration(id, parentId, configName, configDescription);

        var items = isDict
            ? IterDictItems((IDictionary<string, object?>)target)
            : IterPocoItems(target);
        foreach (var (key, type, value) in items)
            ObserveItemDeclaration(id, key, type, value, null);

        // Register the binding BEFORE EnsureInitialized so any WebSocket
        // dispatch firing during the initial fetch finds it.
        lock (_bindingsLock)
        {
            if (_bindings.TryGetValue(id, out var existing))
                return (T)existing;
            _bindings[id] = target;
        }

        EnsureInitialized();
        SyncTargetFromCache(target, id);
        return target;
    }

    // ------------------------------------------------------------------
    // Public API: Get / GetValue / GetValueOr
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns a <see cref="LiveConfigProxy"/> — a live, dict-like view of
    /// the resolved config values for <paramref name="id"/>. Always reflects
    /// the latest server-pushed state.
    /// </summary>
    /// <param name="id">The config identifier.</param>
    /// <returns>A live, read-only proxy.</returns>
    /// <exception cref="NotFoundException">If no config with the given id exists.</exception>
    public LiveConfigProxy Get(string id)
    {
        EnsureInitialized();

        if (!_configCache.ContainsKey(id))
            throw new NotFoundException($"Config with id '{id}' not found in cache.");

        _metrics?.Record("config.resolutions", unit: "resolutions",
            dimensions: new Dictionary<string, string> { ["config"] = id });

        return CachedProxy(id);
    }

    /// <summary>
    /// Returns the resolved value of <paramref name="key"/> within
    /// <paramref name="id"/>. Raises if either is missing. No registration.
    /// </summary>
    /// <exception cref="NotFoundException">If the config is missing.</exception>
    /// <exception cref="KeyNotFoundException">If the key is missing within the config.</exception>
    public object? GetValue(string id, string key)
    {
        EnsureInitialized();

        if (!_configCache.TryGetValue(id, out var values))
            throw new NotFoundException($"Config with id '{id}' not found in cache.");
        if (!values.TryGetValue(key, out var value))
            throw new KeyNotFoundException($"Config item '{key}' not found in config '{id}'.");
        return value;
    }

    /// <summary>
    /// Returns the resolved value of <paramref name="key"/> within
    /// <paramref name="id"/>, falling back to <paramref name="defaultValue"/>
    /// if either is missing. Never raises. <b>Registers</b> the config and
    /// key (with <paramref name="defaultValue"/> as the in-code default) for
    /// code-first observability — the reference shows up in the smplkit
    /// console even if no schema was declared via <see cref="Bind{T}(string, T, object?)"/>.
    /// </summary>
    public T GetValueOr<T>(string id, string key, T defaultValue)
    {
        // Register the config + key so the reference shows up in the
        // console even when no schema was declared via Bind(). The buffer
        // is idempotent at the (configId, itemKey) level.
        ObserveConfigDeclaration(id, parent: null, name: null, description: null);
        ObserveItemDeclaration(id, key, InferItemType(defaultValue), defaultValue, null);

        EnsureInitialized();

        if (!_configCache.TryGetValue(id, out var values)) return defaultValue;
        if (!values.TryGetValue(key, out var raw)) return defaultValue;
        if (raw is null) return defaultValue!;

        var coerced = CoerceValue(raw, typeof(T));
        return coerced is T t ? t : defaultValue;
    }

    // ------------------------------------------------------------------
    // Internal: binding helpers
    // ------------------------------------------------------------------

    /// <summary>Return the config_id this object was bound under, or null.</summary>
    private string? ConfigIdFor(object target)
    {
        lock (_bindingsLock)
        {
            foreach (var (cid, bound) in _bindings)
            {
                if (ReferenceEquals(bound, target)) return cid;
            }
        }
        return null;
    }

    /// <summary>Apply current cached values to a freshly-bound target.</summary>
    private void SyncTargetFromCache(object target, string configId)
    {
        if (!_configCache.TryGetValue(configId, out var cache)) return;
        foreach (var (dottedKey, value) in cache)
            ApplyChangeToTarget(target, dottedKey, value);
    }

    private LiveConfigProxy CachedProxy(string id)
    {
        lock (_proxyLock)
        {
            if (!_proxies.TryGetValue(id, out var proxy))
            {
                proxy = new LiveConfigProxy(this, id);
                _proxies[id] = proxy;
            }
            return proxy;
        }
    }

    /// <summary>Internal: queue a config declaration with the management buffer.</summary>
    internal void ObserveConfigDeclaration(string configId, string? parent, string? name, string? description)
    {
        var mgmt = _parent?.Manage;
        if (mgmt is null) return;
        mgmt.Config.RegisterConfig(
            configId,
            _parent!.Service,
            _parent.Environment,
            parent,
            name,
            description);
    }

    /// <summary>Internal: queue a config item declaration with the management buffer.</summary>
    internal void ObserveItemDeclaration(string configId, string itemKey, string itemType, object? defaultValue, string? description)
    {
        var mgmt = _parent?.Manage;
        if (mgmt is null) return;
        mgmt.Config.RegisterConfigItem(configId, itemKey, itemType, defaultValue, description);
    }

    /// <summary>Internal: used by <see cref="LiveConfigProxy"/> to read cached values.</summary>
    internal IReadOnlyDictionary<string, object?>? GetCachedValues(string id)
        => _configCache.TryGetValue(id, out var values)
            ? new Dictionary<string, object?>(values)
            : null;

    /// <summary>Internal: used by <see cref="LiveConfigProxy"/> to test cache without forcing init.</summary>
    internal bool HasResolved(string id) => _runtimeConnected && _configCache.ContainsKey(id);

    // ------------------------------------------------------------------
    // Runtime: lazy initialization
    // ------------------------------------------------------------------

    /// <summary>
    /// Ensures config data is loaded before first use. Idempotent — safe to
    /// call multiple times. Called automatically on first
    /// <see cref="Get(string)"/> / <see cref="Bind{T}(string, T, object?)"/>.
    /// </summary>
    internal void EnsureInitialized()
    {
        if (_runtimeConnected) return;
        lock (_initLock)
        {
            if (_runtimeConnected) return;

            var environment = _parent?.Environment
                ?? throw new SmplkitException("No environment set.");

            // Per ADR-037 §2.14: flush any buffered discovery declarations
            // BEFORE the initial fetch so newly-discovered configs appear
            // in the cache. FlushAsync swallows network/server failures
            // internally; no outer defensive wrapper needed.
            _parent?.Manage?.Config.FlushAsync().GetAwaiter().GetResult();

            var allConfigs = FetchAllConfigsAsync(default).GetAwaiter().GetResult();
            RebuildCache(allConfigs, environment);
            _runtimeConnected = true;

            if (_ensureWs is not null)
            {
                DebugLog.Log("registration", "registering config_changed, config_deleted, and configs_changed handlers");
                _wsManager = _ensureWs();
                _wsManager.On("config_changed", HandleConfigChanged);
                _wsManager.On("config_deleted", HandleConfigDeleted);
                _wsManager.On("configs_changed", HandleConfigsChanged);
                _wsManager.WaitForInitialConnectAsync().GetAwaiter().GetResult();
                DebugLog.Log("websocket", "config runtime connected");
            }
        }
    }

    // ------------------------------------------------------------------
    // Runtime: refresh
    // ------------------------------------------------------------------

    /// <summary>
    /// Refreshes all config values from the server and notifies change listeners
    /// for any values that differ from the previous state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var environment = _parent?.Environment
            ?? throw new SmplkitException("No environment set.");

        var allConfigs = await FetchAllConfigsAsync(ct).ConfigureAwait(false);

        var oldCache = _configCache;
        RebuildCache(allConfigs, environment);
        DiffAndFire(oldCache, _configCache, "manual");
    }

    // ------------------------------------------------------------------
    // Runtime: change listeners
    // ------------------------------------------------------------------

    /// <summary>Register a global change listener that fires on any config change.</summary>
    public void OnChange(Action<ConfigChangeEvent> callback)
    {
        lock (_listenerLock) { _listeners.Add((callback, null, null)); }
    }

    /// <summary>Register a change listener scoped to a specific config id.</summary>
    public void OnChange(string configId, Action<ConfigChangeEvent> callback)
    {
        lock (_listenerLock) { _listeners.Add((callback, configId, null)); }
    }

    /// <summary>Register a change listener scoped to a specific config id and item key.</summary>
    public void OnChange(string configId, string itemKey, Action<ConfigChangeEvent> callback)
    {
        lock (_listenerLock) { _listeners.Add((callback, configId, itemKey)); }
    }

    // ------------------------------------------------------------------
    // Internal: cache management
    // ------------------------------------------------------------------

    private void RebuildCache(List<Config> allConfigs, string environment)
    {
        var configById = new Dictionary<string, Config>();
        foreach (var cfg in allConfigs)
        {
            if (cfg.Id is not null)
                configById[cfg.Id] = cfg;
        }

        var cache = new Dictionary<string, Dictionary<string, object?>>();

        foreach (var cfg in allConfigs)
        {
            var chain = new List<ConfigChainEntry> { Resolver.ToChainEntry(cfg) };
            var current = cfg;
            while (current.Parent is not null && configById.TryGetValue(current.Parent, out var parent))
            {
                chain.Add(Resolver.ToChainEntry(parent));
                current = parent;
            }

            var resolved = Resolver.Resolve(chain, environment);
            cache[cfg.Id!] = resolved;
        }

        _configCache = cache;
    }

    // ------------------------------------------------------------------
    // Internal: WebSocket event handlers
    // ------------------------------------------------------------------

    private void HandleConfigChanged(Dictionary<string, object?> data)
    {
        var configId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"config_changed event received, id={configId ?? "<unknown>"}");
        if (!_runtimeConnected || configId is null) return;

        var environment = _parent?.Environment;
        if (environment is null) return;

        try
        {
            if (_parent?.Manage.Config is not { } mgmtConfig) return;
            var config = mgmtConfig.GetAsync(configId).GetAwaiter().GetResult();

            var oldCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);

            var chain = new List<ConfigChainEntry> { Resolver.ToChainEntry(config) };
            var newCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);
            newCache[configId] = Resolver.Resolve(chain, environment);
            _configCache = newCache;

            DiffAndFire(oldCache, _configCache, "websocket");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("[smplkit] Config refresh failed: {0}", ex.Message);
            DebugLog.Log("websocket", $"Config refresh failed: {ex}");
        }
    }

    private void HandleConfigDeleted(Dictionary<string, object?> data)
    {
        var configId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"config_deleted event received, id={configId ?? "<unknown>"}");
        if (!_runtimeConnected || configId is null) return;

        var oldCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);
        var newCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);
        newCache.Remove(configId);
        _configCache = newCache;

        DiffAndFire(oldCache, _configCache, "websocket");
    }

    private void HandleConfigsChanged(Dictionary<string, object?> data)
    {
        DebugLog.Log("websocket", "configs_changed event received — full list refetch");
        if (!_runtimeConnected) return;

        var environment = _parent?.Environment;
        if (environment is null) return;

        try
        {
            var allConfigs = FetchAllConfigsAsync(default).GetAwaiter().GetResult();
            var oldCache = _configCache;
            RebuildCache(allConfigs, environment);
            DiffAndFire(oldCache, _configCache, "websocket");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("[smplkit] Configs bulk refresh failed: {0}", ex.Message);
            DebugLog.Log("websocket", $"Configs bulk refresh failed: {ex}");
        }
    }

    private async Task<List<Config>> FetchAllConfigsAsync(CancellationToken ct)
    {
        if (_parent?.Manage.Config is not { } mgmtConfig) return new List<Config>();
        return await Helpers.FetchAllPagesAsync(
            (page, size, c) => mgmtConfig.ListAsync(page, size, c), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Internal: diff and fire listeners
    // ------------------------------------------------------------------

    internal void DiffAndFire(
        Dictionary<string, Dictionary<string, object?>> oldCache,
        Dictionary<string, Dictionary<string, object?>> newCache,
        string source)
    {
        List<(Action<ConfigChangeEvent> Callback, string? ConfigId, string? ItemKey)> listeners;
        lock (_listenerLock)
        {
            listeners = new(_listeners);
        }

        var allConfigIds = new HashSet<string>(oldCache.Keys);
        allConfigIds.UnionWith(newCache.Keys);

        foreach (var cfgId in allConfigIds)
        {
            var oldItems = oldCache.GetValueOrDefault(cfgId) ?? new Dictionary<string, object?>();
            var newItems = newCache.GetValueOrDefault(cfgId) ?? new Dictionary<string, object?>();

            var allItemKeys = new HashSet<string>(oldItems.Keys);
            allItemKeys.UnionWith(newItems.Keys);

            object? target;
            lock (_bindingsLock) { _bindings.TryGetValue(cfgId, out target); }

            foreach (var iKey in allItemKeys)
            {
                var oldVal = oldItems.GetValueOrDefault(iKey);
                var newVal = newItems.GetValueOrDefault(iKey);
                if (Equals(oldVal, newVal)) continue;

                // Apply to bound target first so listeners reading the
                // object see the new value.
                if (target is not null) ApplyChangeToTarget(target, iKey, newVal);

                _metrics?.Record("config.changes", unit: "changes",
                    dimensions: new Dictionary<string, string> { ["config"] = cfgId });

                if (listeners.Count == 0) continue;

                var evt = new ConfigChangeEvent(cfgId, iKey, oldVal, newVal, source);
                foreach (var (callback, filterCfgId, filterItemKey) in listeners)
                {
                    if (filterCfgId is not null && filterCfgId != cfgId) continue;
                    if (filterItemKey is not null && filterItemKey != iKey) continue;
                    try { callback(evt); }
                    catch { /* Ignore listener exceptions */ }
                }
            }
        }
    }

    // ==================================================================
    // Discovery helpers (POCO + dict iteration, change application)
    // ==================================================================

    /// <summary>
    /// Map a runtime value (POCO leaf, dict value, or GetValueOr default) to
    /// a Config item type. <c>bool</c> is checked before number — although
    /// C# bool isn't a subclass of int, mirroring the Python ordering keeps
    /// the cross-language semantics identical.
    /// </summary>
    internal static string InferItemType(object? value) => value switch
    {
        bool => "BOOLEAN",
        int or long or float or double or decimal => "NUMBER",
        string => "STRING",
        _ => "STRING",
    };

    /// <summary>
    /// Walk an <c>IDictionary&lt;string, object?&gt;</c> and yield
    /// <c>(key, type, value)</c> triples flattened to dot-notation. Nested
    /// dicts are descended; every key is treated as an explicit override.
    /// </summary>
    private static IEnumerable<(string Key, string Type, object? Value)> IterDictItems(
        IDictionary<string, object?> dict, string prefix = "")
    {
        foreach (var (rawKey, value) in dict)
        {
            var flatKey = prefix + rawKey;
            if (value is IDictionary<string, object?> sub)
            {
                foreach (var nested in IterDictItems(sub, flatKey + "."))
                    yield return nested;
                continue;
            }
            yield return (flatKey, InferItemType(value), value);
        }
    }

    /// <summary>
    /// Walk a POCO instance's public, readable, non-indexer properties and
    /// yield <c>(key, type, value)</c> triples flattened to dot-notation.
    /// Property names are converted PascalCase → snake_case on the wire.
    /// Nested POCO properties (non-primitive, non-string, non-enumerable
    /// reference types) are descended; arrays, dictionaries, and other
    /// collections are treated as opaque leaves.
    /// </summary>
    private static IEnumerable<(string Key, string Type, object? Value)> IterPocoItems(
        object instance, string prefix = "")
    {
        var type = instance.GetType();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (!prop.CanRead) continue;

            var flatKey = prefix + ToSnakeCase(prop.Name);
            object? value;
            try { value = prop.GetValue(instance); }
            catch { continue; }

            if (value is not null && IsNestablePocoType(prop.PropertyType))
            {
                foreach (var nested in IterPocoItems(value, flatKey + "."))
                    yield return nested;
                continue;
            }

            yield return (flatKey, InferItemType(value), value);
        }
    }

    /// <summary>
    /// Tells whether a property type should be recursed into (true) or
    /// treated as a leaf (false). Primitives, strings, value types, enums,
    /// and any <see cref="System.Collections.IEnumerable"/> (arrays, lists,
    /// dictionaries) are leaves.
    /// </summary>
    private static bool IsNestablePocoType(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string)) return false;
        if (u.IsPrimitive) return false;
        if (u.IsValueType) return false;
        if (u == typeof(object)) return false;
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(u)) return false;
        return true;
    }

    /// <summary>
    /// Apply a server-pushed value to a bound target in place. Walks the
    /// dotted key path to the leaf's parent, then assigns the value.
    /// Handles dicts (via indexer), POCOs (via setter or backing field),
    /// and mixed nesting. Bails silently if any intermediate is missing.
    /// </summary>
    private static void ApplyChangeToTarget(object target, string dottedKey, object? value)
    {
        var parts = dottedKey.Split('.');
        object? current = target;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (current is null) return;
            var part = parts[i];
            if (current is IDictionary<string, object?> dict)
            {
                if (!dict.TryGetValue(part, out var next)) return;
                current = next;
            }
            else
            {
                var prop = FindPropertyBySnakeName(current.GetType(), part);
                if (prop is null) return;
                try { current = prop.GetValue(current); }
                catch { return; }
            }
        }

        if (current is null) return;
        var last = parts[^1];
        if (current is IDictionary<string, object?> dictCurrent)
        {
            dictCurrent[last] = value;
        }
        else
        {
            var prop = FindPropertyBySnakeName(current.GetType(), last);
            if (prop is null) return;
            var coerced = CoerceValue(value, prop.PropertyType);
            SetPropertyValue(current, prop, coerced);
        }
    }

    private static PropertyInfo? FindPropertyBySnakeName(Type type, string snakeName)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.Name == snakeName) return prop;
            if (ToSnakeCase(prop.Name) == snakeName) return prop;
        }
        return null;
    }

    /// <summary>
    /// Set a property value, using the setter if available, otherwise the
    /// compiler-generated backing field. This is the C# equivalent of
    /// Python's <c>object.__setattr__</c> — it bypasses init-only setters
    /// on records so the in-place mutation contract holds for any binding
    /// target with a backing field.
    /// </summary>
    private static void SetPropertyValue(object target, PropertyInfo prop, object? value)
    {
        if (prop.CanWrite && prop.SetMethod is not null && prop.SetMethod.IsPublic)
        {
            try
            {
                prop.SetValue(target, value);
                return;
            }
            catch { /* Init-only on a record — fall through to the backing field. */ }
        }

        var backingField = target.GetType().GetField(
            $"<{prop.Name}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        try { backingField?.SetValue(target, value); }
        catch { /* Best-effort: malformed/non-standard layouts are skipped. */ }
    }

    /// <summary>
    /// Coerce a server-supplied value (typically <c>long</c> / <c>double</c>
    /// / <c>string</c> / <c>bool</c> after resolver normalization) to the
    /// target property type. Numeric widening / narrowing handled
    /// explicitly; everything else falls back to <see cref="Convert.ChangeType(object, Type)"/>
    /// or passes the raw value through.
    /// </summary>
    private static object? CoerceValue(object? value, Type targetType)
    {
        if (value is null) return null;
        if (targetType.IsInstanceOfType(value)) return value;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // JsonElement passthrough — rare after resolver normalization, but
        // the resolver doesn't touch single-config-replace WS payloads if
        // the management mapper hasn't already unwrapped them.
        if (value is JsonElement je) value = UnwrapJsonElement(je);

        if (value is null) return null;
        if (underlying.IsInstanceOfType(value)) return value;

        if (underlying.IsEnum && value is string s) return Enum.Parse(underlying, s, ignoreCase: true);

        try { return Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return value; }
    }

    private static object? UnwrapJsonElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => je,
    };

    /// <summary>
    /// Convert a PascalCase identifier to snake_case. Mirrors
    /// <c>JsonNamingPolicy.SnakeCaseLower</c> closely enough for property
    /// names (which never contain spaces or punctuation), but is a simple
    /// in-place loop so we don't allocate a serializer just to map a name.
    /// </summary>
    internal static string ToSnakeCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c))
            {
                bool prevLower = char.IsLower(s[i - 1]);
                bool nextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);
                if (prevLower || nextLower) sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
