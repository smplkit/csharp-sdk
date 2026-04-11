using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using GenConfig = Smplkit.Internal.Generated.Config;

namespace Smplkit.Config;

/// <summary>
/// Client for the smplkit Config service. Provides operations for creating,
/// reading, updating, and deleting configs, as well as resolving config values
/// for the current environment via <see cref="Resolve(string)"/>.
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
    private readonly List<(Action<ConfigChangeEvent> Callback, string? ConfigKey, string? ItemKey)> _listeners = new();
    private readonly object _listenerLock = new();
    private SharedWebSocket? _wsManager;

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
    // Management: factory
    // ------------------------------------------------------------------

    /// <summary>
    /// Create an unsaved config. Call <see cref="Config.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="key">The config key.</param>
    /// <param name="name">Display name. Auto-generated from key if null.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="parent">Optional parent config UUID.</param>
    /// <returns>An unsaved <see cref="Config"/>.</returns>
    public Config New(string key, string? name = null, string? description = null, string? parent = null)
    {
        return new Config(
            client: this,
            id: null,
            key: key,
            name: name ?? Helpers.KeyToDisplayName(key),
            description: description,
            parent: parent,
            items: new Dictionary<string, object?>(),
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    // ------------------------------------------------------------------
    // Management: CRUD by key
    // ------------------------------------------------------------------

    /// <summary>
    /// Fetches a single config by its human-readable key.
    /// </summary>
    /// <param name="key">The config key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Config"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching config exists.</exception>
    public async Task<Config> GetAsync(string key, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_configsAsync(filterkey: key, cancellationToken: ct)).ConfigureAwait(false);

        if (response.Data is null || response.Data.Count == 0)
            throw new SmplNotFoundException($"Config with key '{key}' not found");

        return MapResource(response.Data[0])
            ?? throw new SmplNotFoundException($"Config with key '{key}' not found");
    }

    /// <summary>
    /// Lists all configs for the account.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="Config"/> objects.</returns>
    public async Task<List<Config>> ListAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_configsAsync(cancellationToken: ct)).ConfigureAwait(false);

        if (response.Data is null)
            return new List<Config>();

        var results = new List<Config>(response.Data.Count);
        foreach (var resource in response.Data)
        {
            var config = MapResource(resource);
            if (config is not null)
                results.Add(config);
        }
        return results;
    }

    /// <summary>
    /// Deletes a config by its human-readable key.
    /// </summary>
    /// <param name="key">The config key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching config exists.</exception>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var config = await GetAsync(key, ct).ConfigureAwait(false);
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_configAsync(Guid.Parse(config.Id!), ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a config (create or update).</summary>
    internal async Task<Config> SaveConfigInternalAsync(Config config, CancellationToken ct = default)
    {
        var body = BuildRequestBody(config);
        if (config.Id is null)
        {
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Create_configAsync(body, ct)).ConfigureAwait(false);
            return MapResource(response.Data)
                ?? throw new SmplValidationException("Failed to create config");
        }
        else
        {
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Update_configAsync(Guid.Parse(config.Id), body, ct)).ConfigureAwait(false);
            return MapResource(response.Data)
                ?? throw new SmplValidationException("Failed to update config");
        }
    }

    // ------------------------------------------------------------------
    // Runtime: Resolve
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the resolved config values for the given key in the current environment.
    /// </summary>
    /// <param name="key">The config key.</param>
    /// <returns>A dictionary of resolved key-value pairs.</returns>
    /// <exception cref="SmplNotFoundException">If no config with the given key exists.</exception>
    public Dictionary<string, object?> Resolve(string key)
    {
        EnsureInitialized();

        if (!_configCache.TryGetValue(key, out var values))
            throw new SmplNotFoundException($"Config with key '{key}' not found in cache.");

        _metrics?.Record("config.resolutions", unit: "resolutions",
            dimensions: new Dictionary<string, string> { ["config_id"] = key });

        return new Dictionary<string, object?>(values);
    }

    /// <summary>
    /// Resolves config values for the given key and deserializes to a typed object.
    /// Dot-notation keys (e.g. <c>"db.host"</c>) map to nested properties on the target type.
    /// </summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="key">The config key.</param>
    /// <returns>A deserialized instance of <typeparamref name="T"/>.</returns>
    public T Resolve<T>(string key) where T : new()
    {
        var flat = Resolve(key);
        var nested = ExpandDotNotation(flat);
        var json = JsonSerializer.Serialize(nested, JsonOptions.Default);
        return JsonSerializer.Deserialize<T>(json, JsonOptions.Default)
            ?? throw new SmplException($"Failed to deserialize config '{key}' to {typeof(T).Name}");
    }

    // ------------------------------------------------------------------
    // Runtime: lazy initialization
    // ------------------------------------------------------------------

    /// <summary>
    /// Ensures config data is loaded before first use.
    /// </summary>
    internal void EnsureInitialized()
    {
        if (_runtimeConnected) return;
        lock (_initLock)
        {
            if (_runtimeConnected) return;

            var environment = _parent?.Environment
                ?? throw new SmplException("No environment set.");

            var allConfigs = ListAsync().GetAwaiter().GetResult();
            RebuildCache(allConfigs, environment);
            _runtimeConnected = true;

            // Register on the shared WebSocket
            if (_ensureWs is not null)
            {
                _wsManager = _ensureWs();
                _wsManager.On("config_changed", HandleConfigChanged);
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
            ?? throw new SmplException("No environment set.");

        var allConfigs = await ListAsync(ct).ConfigureAwait(false);

        var oldCache = _configCache;
        RebuildCache(allConfigs, environment);
        DiffAndFire(oldCache, _configCache, "manual");
    }

    // ------------------------------------------------------------------
    // Runtime: change listeners
    // ------------------------------------------------------------------

    /// <summary>
    /// Register a global change listener that fires when any config value changes.
    /// </summary>
    /// <param name="callback">Called with a <see cref="ConfigChangeEvent"/> on each change.</param>
    public void OnChange(Action<ConfigChangeEvent> callback)
    {
        lock (_listenerLock)
        {
            _listeners.Add((callback, null, null));
        }
    }

    /// <summary>
    /// Register a change listener scoped to a specific config key.
    /// </summary>
    /// <param name="configKey">The config key to listen for.</param>
    /// <param name="callback">Called with a <see cref="ConfigChangeEvent"/> on each change.</param>
    public void OnChange(string configKey, Action<ConfigChangeEvent> callback)
    {
        lock (_listenerLock)
        {
            _listeners.Add((callback, configKey, null));
        }
    }

    /// <summary>
    /// Register a change listener scoped to a specific config key and item key.
    /// </summary>
    /// <param name="configKey">The config key to listen for.</param>
    /// <param name="itemKey">The item key within the config to listen for.</param>
    /// <param name="callback">Called with a <see cref="ConfigChangeEvent"/> on each change.</param>
    public void OnChange(string configKey, string itemKey, Action<ConfigChangeEvent> callback)
    {
        lock (_listenerLock)
        {
            _listeners.Add((callback, configKey, itemKey));
        }
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
            cache[cfg.Key] = resolved;
        }

        _configCache = cache;
    }

    // ------------------------------------------------------------------
    // Internal: WebSocket event handler
    // ------------------------------------------------------------------

    private void HandleConfigChanged(Dictionary<string, object?> data)
    {
        if (!_runtimeConnected) return;

        var environment = _parent?.Environment;
        if (environment is null) return;

        try
        {
            var allConfigs = ListAsync().GetAwaiter().GetResult();
            var oldCache = _configCache;
            RebuildCache(allConfigs, environment);
            DiffAndFire(oldCache, _configCache, "websocket");
        }
        catch { /* Ignore refresh errors */ }
    }

    // ------------------------------------------------------------------
    // Internal: diff and fire listeners
    // ------------------------------------------------------------------

    internal void DiffAndFire(
        Dictionary<string, Dictionary<string, object?>> oldCache,
        Dictionary<string, Dictionary<string, object?>> newCache,
        string source)
    {
        List<(Action<ConfigChangeEvent> Callback, string? ConfigKey, string? ItemKey)> listeners;
        lock (_listenerLock)
        {
            listeners = new(_listeners);
        }

        var allConfigKeys = new HashSet<string>(oldCache.Keys);
        allConfigKeys.UnionWith(newCache.Keys);

        foreach (var cfgKey in allConfigKeys)
        {
            var oldItems = oldCache.GetValueOrDefault(cfgKey) ?? new Dictionary<string, object?>();
            var newItems = newCache.GetValueOrDefault(cfgKey) ?? new Dictionary<string, object?>();

            var allItemKeys = new HashSet<string>(oldItems.Keys);
            allItemKeys.UnionWith(newItems.Keys);

            foreach (var iKey in allItemKeys)
            {
                var oldVal = oldItems.GetValueOrDefault(iKey);
                var newVal = newItems.GetValueOrDefault(iKey);
                if (Equals(oldVal, newVal)) continue;

                _metrics?.Record("config.changes", unit: "changes",
                    dimensions: new Dictionary<string, string> { ["config_id"] = cfgKey });

                if (listeners.Count == 0) continue;

                var evt = new ConfigChangeEvent(cfgKey, iKey, oldVal, newVal, source);
                foreach (var (callback, filterCfgKey, filterItemKey) in listeners)
                {
                    if (filterCfgKey is not null && filterCfgKey != cfgKey) continue;
                    if (filterItemKey is not null && filterItemKey != iKey) continue;
                    try { callback(evt); }
                    catch { /* Ignore listener exceptions */ }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private static Dictionary<string, object?> ExpandDotNotation(Dictionary<string, object?> flat)
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

    private static GenConfig.Response_Config_ BuildRequestBody(Config config) =>
        new()
        {
            Data = new GenConfig.Resource_Config_
            {
                Type = "config",
                Attributes = new GenConfig.Config
                {
                    Name = config.Name,
                    Key = config.Key,
                    Description = config.Description,
                    Parent = config.Parent,
                    Items = WrapItemsForRequest(config.Items),
                    Environments = WrapEnvsForRequest(config.Environments),
                },
            },
        };

    private static IDictionary<string, GenConfig.ConfigItemDefinition>? WrapItemsForRequest(
        Dictionary<string, object?>? items)
    {
        if (items is null || items.Count == 0) return null;

        var result = new Dictionary<string, GenConfig.ConfigItemDefinition>(items.Count);
        foreach (var (key, value) in items)
        {
            result[key] = new GenConfig.ConfigItemDefinition
            {
                Value = value!,
                Type = InferType(value),
            };
        }
        return result;
    }

    private static IDictionary<string, GenConfig.EnvironmentOverride>? WrapEnvsForRequest(
        Dictionary<string, Dictionary<string, object?>>? environments)
    {
        if (environments is null || environments.Count == 0) return null;

        var result = new Dictionary<string, GenConfig.EnvironmentOverride>(environments.Count);
        foreach (var (envName, envData) in environments)
        {
            var values = new Dictionary<string, GenConfig.ConfigItemOverride>(envData.Count);
            foreach (var (key, value) in envData)
            {
                values[key] = new GenConfig.ConfigItemOverride { Value = value! };
            }
            result[envName] = new GenConfig.EnvironmentOverride { Values = values };
        }
        return result;
    }

    private static GenConfig.ConfigItemDefinitionType? InferType(object? value) => value switch
    {
        string => GenConfig.ConfigItemDefinitionType.STRING,
        bool => GenConfig.ConfigItemDefinitionType.BOOLEAN,
        int or long or float or double or decimal => GenConfig.ConfigItemDefinitionType.NUMBER,
        _ => null,
    };

    /// <summary>
    /// Maps a response resource to a <see cref="Config"/>.
    /// </summary>
    private Config? MapResource(GenConfig.ConfigResource? resource)
    {
        if (resource?.Attributes is null)
            return null;

        var attrs = resource.Attributes;
        var items = ExtractRawItems(attrs.Items);
        var environments = ExtractRawEnvironments(attrs.Environments);

        return new Config(
            client: this,
            id: resource.Id ?? string.Empty,
            key: attrs.Key ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            description: attrs.Description,
            parent: attrs.Parent,
            items: items,
            environments: environments,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime
        );
    }

    private static Dictionary<string, object?> ExtractRawItems(
        IDictionary<string, GenConfig.ConfigItemDefinition>? items)
    {
        if (items is null)
            return new Dictionary<string, object?>();

        var result = new Dictionary<string, object?>(items.Count);
        foreach (var (key, definition) in items)
        {
            result[key] = Resolver.Normalize(definition.Value);
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, object?>> ExtractRawEnvironments(
        IDictionary<string, GenConfig.EnvironmentOverride>? environments)
    {
        if (environments is null)
            return new Dictionary<string, Dictionary<string, object?>>();

        var result = new Dictionary<string, Dictionary<string, object?>>(environments.Count);
        foreach (var (envName, envOverride) in environments)
        {
            var envValues = new Dictionary<string, object?>();
            if (envOverride.Values is not null)
            {
                foreach (var (key, itemOverride) in envOverride.Values)
                {
                    envValues[key] = Resolver.Normalize(itemOverride.Value);
                }
            }
            result[envName] = envValues;
        }
        return result;
    }
}
