using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using GenConfig = Smplkit.Internal.Generated.Config;
using DebugLog = Smplkit.Internal.Debug;

namespace Smplkit.Config;

/// <summary>
/// Client for the smplkit Config service. Provides management operations via
/// <see cref="Management"/>, and resolves config values for the current environment
/// via <see cref="Get(string)"/>.
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
    // Runtime: Get (resolved values)
    //
    // Wire CRUD code (factory, GetAsync, ListAsync, DeleteAsync, Save,
    // request-body building, MapResource, ExtractRaw helpers) lives in
    // Smplkit.Management.ConfigsClient. The runtime client routes its
    // initial fetch / refresh / single-config refresh through the
    // management plane via _parent.Manage.Config — there is no duplicated
    // wire code here.
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns a <see cref="LiveConfigProxy"/> — a live, dict-like view of the
    /// resolved config values for the given id. Always reflects the latest
    /// server-pushed state; no <c>Subscribe</c> step is required.
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

    private readonly Dictionary<string, LiveConfigProxy> _proxies = new();
    private readonly object _proxyLock = new();

    /// <summary>
    /// Declares a configuration from code; returns a live, read-only proxy.
    /// Idempotent — repeat calls with the same id return the same
    /// <see cref="LiveConfigProxy"/> instance, so callers can hold one as a
    /// parent reference. The first call queues a discovery payload (the
    /// config and any items declared via typed getters on the returned
    /// handle) for upload to <c>POST /api/v1/configs/bulk</c> on next flush.
    /// </summary>
    public LiveConfigProxy GetOrCreate(
        string id,
        object? parent = null,
        string? name = null,
        string? description = null)
    {
        string? parentId = parent switch
        {
            string s => s,
            LiveConfigProxy p => p.ConfigId,
            null => null,
            _ => throw new ArgumentException(
                $"parent must be a string id or LiveConfigProxy; got {parent.GetType().Name}"),
        };
        ObserveConfigDeclaration(id, parentId, name, description);
        EnsureInitialized();
        return CachedProxy(id);
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

    /// <summary>
    /// Returns the resolved config values for the given id and deserializes to a typed object.
    /// Dot-notation keys (e.g. <c>"db.host"</c>) map to nested properties on the target type.
    /// </summary>
    /// <typeparam name="T">The target type.</typeparam>
    /// <param name="id">The config identifier.</param>
    /// <returns>A deserialized instance of <typeparamref name="T"/>.</returns>
    public T Get<T>(string id) where T : new() => Get(id).Into<T>();

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
    /// Ensures config data is loaded before first use.
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
            // in the cache.
            try
            {
                _parent?.Manage?.Config.FlushAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                DebugLog.Log("registration", "pre-start discovery flush failed: " + ex.Message);
            }

            var allConfigs = FetchAllConfigsAsync(default).GetAwaiter().GetResult();
            RebuildCache(allConfigs, environment);
            _runtimeConnected = true;

            // Register on the shared WebSocket
            if (_ensureWs is not null)
            {
                DebugLog.Log("registration", "registering config_changed, config_deleted, and configs_changed handlers");
                _wsManager = _ensureWs();
                _wsManager.On("config_changed", HandleConfigChanged);
                _wsManager.On("config_deleted", HandleConfigDeleted);
                _wsManager.On("configs_changed", HandleConfigsChanged);
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
    /// Register a change listener scoped to a specific config id.
    /// </summary>
    /// <param name="configId">The config identifier to listen for.</param>
    /// <param name="callback">Called with a <see cref="ConfigChangeEvent"/> on each change.</param>
    public void OnChange(string configId, Action<ConfigChangeEvent> callback)
    {
        lock (_listenerLock)
        {
            _listeners.Add((callback, configId, null));
        }
    }

    /// <summary>
    /// Register a change listener scoped to a specific config id and item key.
    /// </summary>
    /// <param name="configId">The config identifier to listen for.</param>
    /// <param name="itemKey">The item key within the config to listen for.</param>
    /// <param name="callback">Called with a <see cref="ConfigChangeEvent"/> on each change.</param>
    public void OnChange(string configId, string itemKey, Action<ConfigChangeEvent> callback)
    {
        lock (_listenerLock)
        {
            _listeners.Add((callback, configId, itemKey));
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
            // Scoped fetch: GET just the single changed config (via management plane)
            if (_parent?.Manage.Config is not { } mgmtConfig) return;
            var config = mgmtConfig.GetAsync(configId).GetAwaiter().GetResult();

            var oldCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);

            // Resolve the updated config (standalone — no parent lookup needed for diff)
            var chain = new List<ConfigChainEntry> { Resolver.ToChainEntry(config) };
            var newCache = new Dictionary<string, Dictionary<string, object?>>(_configCache);
            newCache[configId] = Resolver.Resolve(chain, environment);
            _configCache = newCache;

            DiffAndFire(oldCache, _configCache, "websocket");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Config refresh failed: {0}", ex.Message);
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
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Configs bulk refresh failed: {0}", ex.Message);
            DebugLog.Log("websocket", $"Configs bulk refresh failed: {ex}");
        }
    }

    /// <summary>
    /// Walks <c>Manage.Config.ListAsync</c> page by page until the server
    /// returns fewer rows than requested. The runtime needs the full set to
    /// build its resolved-value cache, so paging silently here is correct;
    /// customers can still page the management surface directly.
    /// </summary>
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

            foreach (var iKey in allItemKeys)
            {
                var oldVal = oldItems.GetValueOrDefault(iKey);
                var newVal = newItems.GetValueOrDefault(iKey);
                if (Equals(oldVal, newVal)) continue;

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

}
