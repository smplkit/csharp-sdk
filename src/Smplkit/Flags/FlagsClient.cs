using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonLogic.Net;
using Newtonsoft.Json.Linq;
using Smplkit.Errors;
using Smplkit.Internal;
using GenApp = Smplkit.Internal.Generated.App;
using GenFlags = Smplkit.Internal.Generated.Flags;

namespace Smplkit.Flags;

/// <summary>
/// Client for the smplkit Flags service. Provides operations for creating,
/// reading, updating, and deleting flags, as well as evaluating flags via
/// typed flag handles.
/// </summary>
public sealed class FlagsClient
{
    private const int CacheMaxSize = 10_000;
    private const int ContextRegistrationLruSize = 10_000;
    private const int ContextBatchFlushSize = 100;

    private readonly GenFlags.FlagsClient _genFlagsClient;
    private readonly GenApp.AppClient _genAppClient;
    private readonly string _apiKey;
    private readonly Func<SharedWebSocket> _ensureWs;
    private readonly SmplClient? _parent;

    // Runtime state
    private string? _environment;
    private readonly ConcurrentDictionary<string, Dictionary<string, object?>> _flagStore = new();
    internal volatile bool _connected;
    private readonly object _initLock = new();

    // Backoff retry state for start/EnsureInitialized
    internal const double MaxStartRetryDelayS = 60.0;
    internal double _startRetryDelayS = 1.0;
    internal long _nextStartAttemptAt = 0L;
    internal bool _wsSubscribed;
    private readonly ResolutionCache _cache = new(CacheMaxSize);
    private Func<IReadOnlyList<Context>>? _contextProvider;
    private readonly ContextRegistrationBuffer _contextBuffer;
    private readonly ConcurrentDictionary<string, Flag> _handles = new();
    private readonly List<Action<FlagChangeEvent>> _globalListeners = new();
    private readonly ConcurrentDictionary<string, List<Action<FlagChangeEvent>>> _scopedListeners = new();

    // Shared WebSocket
    private SharedWebSocket? _wsManager;

    private readonly MetricsReporter? _metrics;

    // Flag auto-registration
    private readonly FlagRegistrationBuffer _flagBuffer = new();
    private Timer? _flagFlushTimer;

    // Exposed for tests to await the fire-and-forget context registration task
    internal Task? _initRegistrationTask;

    // Exposed for tests to await fire-and-forget threshold-triggered flushes
    // (otherwise the lambda body coverage races with process exit on CI).
    internal Task? _lastFlagBufferFlushTask;
    internal Task? _lastContextBufferFlushTask;

    internal FlagsClient(GeneratedClientFactory clients, string apiKey, Func<SharedWebSocket> ensureWs, ContextRegistrationBuffer contextBuffer, SmplClient? parent = null, MetricsReporter? metrics = null)
    {
        _genFlagsClient = clients.Flags;
        _genAppClient = clients.App;
        _apiKey = apiKey;
        _ensureWs = ensureWs;
        _contextBuffer = contextBuffer;
        _parent = parent;
        _metrics = metrics;
    }

    // ------------------------------------------------------------------
    // Runtime: typed flag handles
    // ------------------------------------------------------------------

    /// <summary>
    /// Declares a boolean flag handle with the specified default value.
    /// </summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="defaultValue">The default value used when no server-side value is available.</param>
    /// <returns>A typed flag handle.</returns>
    public BooleanFlag BooleanFlag(string id, bool defaultValue)
    {
        var handle = new BooleanFlag(
            evalClient: this, mgmtClient: _parent?.Manage.Flags,
            id: id, name: id,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[id] = handle;
        _flagBuffer.Add(id, "BOOLEAN", defaultValue, _parent?.Service, _parent?.Environment);
        if (_flagBuffer.PendingCount >= 50)
            _lastFlagBufferFlushTask = SafeFlushFlagsAsync();
        return handle;
    }

    /// <summary>
    /// Declares a string flag handle with the specified default value.
    /// </summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="defaultValue">The default value used when no server-side value is available.</param>
    /// <returns>A typed flag handle.</returns>
    public StringFlag StringFlag(string id, string defaultValue)
    {
        var handle = new StringFlag(
            evalClient: this, mgmtClient: _parent?.Manage.Flags,
            id: id, name: id,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[id] = handle;
        _flagBuffer.Add(id, "STRING", defaultValue, _parent?.Service, _parent?.Environment);
        if (_flagBuffer.PendingCount >= 50)
            _lastFlagBufferFlushTask = SafeFlushFlagsAsync();
        return handle;
    }

    /// <summary>
    /// Declares a number flag handle with the specified default value.
    /// </summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="defaultValue">The default value used when no server-side value is available.</param>
    /// <returns>A typed flag handle.</returns>
    public NumberFlag NumberFlag(string id, double defaultValue)
    {
        var handle = new NumberFlag(
            evalClient: this, mgmtClient: _parent?.Manage.Flags,
            id: id, name: id,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[id] = handle;
        _flagBuffer.Add(id, "NUMERIC", defaultValue, _parent?.Service, _parent?.Environment);
        if (_flagBuffer.PendingCount >= 50)
            _lastFlagBufferFlushTask = SafeFlushFlagsAsync();
        return handle;
    }

    /// <summary>
    /// Declares a JSON flag handle with the specified default value.
    /// </summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="defaultValue">The default value used when no server-side value is available.</param>
    /// <returns>A typed flag handle.</returns>
    public JsonFlag JsonFlag(string id, Dictionary<string, object?> defaultValue)
    {
        var handle = new JsonFlag(
            evalClient: this, mgmtClient: _parent?.Manage.Flags,
            id: id, name: id,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[id] = handle;
        _flagBuffer.Add(id, "JSON", defaultValue, _parent?.Service, _parent?.Environment);
        if (_flagBuffer.PendingCount >= 50)
            _lastFlagBufferFlushTask = SafeFlushFlagsAsync();
        return handle;
    }

    /// <summary>
    /// Number of flag declarations currently queued for registration. Exposed for tests.
    /// </summary>
    internal int PendingFlagRegistrations => _flagBuffer.PendingCount;

    // ------------------------------------------------------------------
    // Runtime: context provider
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers a context provider function that supplies the current request
    /// context for flag evaluation.
    /// </summary>
    /// <param name="provider">A function returning the current contexts.</param>
    public void SetContextProvider(Func<IReadOnlyList<Context>> provider)
    {
        _contextProvider = provider;
    }

    // ------------------------------------------------------------------
    // Runtime: lazy initialization
    // ------------------------------------------------------------------

    /// <summary>
    /// Ensures flag data is loaded before first use.
    /// Idempotent — safe to call on every evaluation. If the flags service is
    /// unhealthy the first time (e.g. a pod starts before the schema is loaded),
    /// pending declarations are kept in the buffer, <c>_connected</c> stays
    /// <c>false</c>, and the next call retries after an exponential back-off
    /// (1 s → 60 s cap). Evaluations during the window fall back to handle defaults.
    /// </summary>
    internal void EnsureInitialized()
    {
        if (_connected) return;
        if (Environment.TickCount64 < Interlocked.Read(ref _nextStartAttemptAt)) return;
        lock (_initLock)
        {
            if (_connected) return;
            if (Environment.TickCount64 < _nextStartAttemptAt) return;

            _environment = _parent?.Environment;

            // Fire-and-forget environment + service context registration (best-effort, once).
            if (_parent?.Service is { Length: > 0 } svc)
            {
                var env = _parent?.Environment;
                _initRegistrationTask = RegisterInitialContextsAsync(svc, env);
            }

            Debug.Log("websocket", "flags runtime initializing");
            try
            {
                // Flush declarations BEFORE fetching definitions; items stay queued
                // until the POST succeeds so a 500 doesn't lose them.
                FlushFlagsAsync().GetAwaiter().GetResult();
                FetchAllFlagsAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ScheduleStartRetry(ex);
                return;
            }

            _connected = true;
            _startRetryDelayS = 1.0;
            _nextStartAttemptAt = 0L;
            _cache.Clear();

            _flagFlushTimer = new Timer(_ => FlushTimerCallback(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _wsManager = _ensureWs();
            if (!_wsSubscribed)
            {
                Debug.Log("registration", "registering flag_changed, flag_deleted, and flags_changed handlers");
                _wsManager.On("flag_changed", HandleFlagChanged);
                _wsManager.On("flag_deleted", HandleFlagDeleted);
                _wsManager.On("flags_changed", HandleFlagsChanged);
                _wsSubscribed = true;
            }
            Debug.Log("websocket", "flags runtime connected");
        }
    }

    private void ScheduleStartRetry(Exception ex)
    {
        var delayS = _startRetryDelayS;
        _nextStartAttemptAt = Environment.TickCount64 + (long)(delayS * 1000.0);
        _startRetryDelayS = Math.Min(delayS * 2.0, MaxStartRetryDelayS);
        System.Diagnostics.Trace.TraceWarning(
            "[smplkit] Flags start failed (will retry in {0:F1}s): {1}", delayS, ex.Message);
        Debug.Log("registration", $"Flags start failed: {ex}");
    }

    // ------------------------------------------------------------------
    // Runtime: refresh
    // ------------------------------------------------------------------

    /// <summary>
    /// Refreshes all flag definitions from the server and notifies change listeners.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await FetchAllFlagsAsync(ct).ConfigureAwait(false);
        _cache.Clear();
        FireChangeListenersAll("manual");
    }

    /// <summary>
    /// Gets the current real-time connection status.
    /// </summary>
    public string ConnectionStatus => _wsManager?.ConnectionStatus ?? "disconnected";

    /// <summary>
    /// Gets evaluation statistics.
    /// </summary>
    public FlagStats Stats => new(_cache.CacheHits, _cache.CacheMisses);

    // ------------------------------------------------------------------
    // Runtime: change listeners
    // ------------------------------------------------------------------

    /// <summary>
    /// Register a global change listener that fires when any flag changes.
    /// </summary>
    /// <param name="callback">Called with a <see cref="FlagChangeEvent"/> on each change.</param>
    public void OnChange(Action<FlagChangeEvent> callback)
    {
        _globalListeners.Add(callback);
    }

    /// <summary>
    /// Register a change listener scoped to a specific flag id.
    /// </summary>
    /// <param name="flagId">The flag identifier to listen for.</param>
    /// <param name="callback">Called with a <see cref="FlagChangeEvent"/> when this flag changes.</param>
    public void OnChange(string flagId, Action<FlagChangeEvent> callback)
    {
        var list = _scopedListeners.GetOrAdd(flagId, _ => new List<Action<FlagChangeEvent>>());
        lock (list)
        {
            list.Add(callback);
        }
    }

    /// <summary>
    /// Best-effort initial context registration: registers the configured
    /// environment and service with the contexts service. Failures are logged
    /// and swallowed; this is fired-and-forgotten from <see cref="EnsureInitialized"/>.
    /// </summary>
    private async Task RegisterInitialContextsAsync(string svc, string? env)
    {
        try
        {
            var items = new List<GenApp.ContextBulkItem>();
            if (!string.IsNullOrEmpty(env))
            {
                // ContextBulkItem.Attributes is generated as `object` with no
                // null-omit serializer hint, so leaving it default sends
                // "attributes": null and the server rejects with
                // "Input should be a valid dictionary". Send an empty dict.
                items.Add(new() { Type = "environment", Key = env, Attributes = new Dictionary<string, object?>() });
            }
            items.Add(new()
            {
                Type = "service",
                Key = svc,
                Attributes = new Dictionary<string, object?> { ["name"] = svc },
            });
            await ApiExceptionMapper.ExecuteAsync(async () =>
                await _genAppClient.Bulk_register_contextsAsync(
                    new GenApp.ContextBulkRegister
                    {
                        Contexts = items,
                    }).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Context registration failed: {0}", ex.Message);
            Debug.Log("registration", $"Context registration failed: {ex}");
        }
    }

    // ------------------------------------------------------------------
    // Internal: context flush (called from EvaluateHandle auto-flush path)
    // ------------------------------------------------------------------

    /// <summary>
    /// Sends any pending context registrations to the server.
    /// Public context registration is via <c>client.Management.Contexts.RegisterAsync()</c>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    internal async Task FlushContextsAsync(CancellationToken ct = default)
    {
        var batch = _contextBuffer.Drain();
        if (batch.Count == 0) return;
        try
        {
            var items = batch.Select(b => new GenApp.ContextBulkItem
            {
                Type = b.TryGetValue("id", out var id) && id is string idStr && idStr.Contains(':')
                    ? idStr.Split(':')[0]
                    : "",
                Key = b.TryGetValue("id", out var id2) && id2 is string idStr2 && idStr2.Contains(':')
                    ? idStr2[(idStr2.IndexOf(':') + 1)..]
                    : "",
                Attributes = b.TryGetValue("attributes", out var attrs) ? attrs ?? new object() : new object(),
            }).ToList();

            await ApiExceptionMapper.ExecuteAsync(
                () => _genAppClient.Bulk_register_contextsAsync(
                    new GenApp.ContextBulkRegister { Contexts = items }, ct)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Context flush failed: {0}", ex.Message);
            Debug.Log("registration", $"Context flush failed: {ex}");
        }
    }

    /// <summary>
    /// Timer callback: flush the flag buffer. Errors are swallowed — items
    /// stay queued for the next attempt.
    /// </summary>
    internal void FlushTimerCallback()
    {
        SafeFlushFlagsAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Sends any pending flag registrations to the server.
    /// Uses peek+commit so items remain queued when the POST fails; they are
    /// removed only after a successful response. Throws on any HTTP or network
    /// error so callers can decide how to handle the failure.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    internal async Task FlushFlagsAsync(CancellationToken ct = default)
    {
        var batch = _flagBuffer.Peek();
        if (batch.Count == 0) return;
        var items = batch.Select(e => new GenFlags.FlagBulkItem
        {
            Id = e.Id,
            Type = Enum.Parse<GenFlags.FlagBulkItemType>(e.Type),
            Default = e.DefaultValue ?? new object(),
            Service = e.Service,
            Environment = e.Environment,
        }).ToList();
        var request = new GenFlags.FlagBulkRequest { Flags = items };
        await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.Bulk_register_flagsAsync(request, ct)).ConfigureAwait(false);
        _flagBuffer.Commit(batch.Select(e => e.Id));
    }

    /// <summary>
    /// Safe wrapper around <see cref="FlushFlagsAsync"/>: swallows errors and
    /// logs a warning. Items stay queued for the next attempt. Used by the
    /// periodic timer and the threshold-triggered flush paths.
    /// </summary>
    private async Task SafeFlushFlagsAsync(CancellationToken ct = default)
    {
        try
        {
            await FlushFlagsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Flag registration flush failed: {0}", ex.Message);
            Debug.Log("registration", $"Flag registration flush failed: {ex}");
        }
    }

    /// <summary>
    /// Stops the periodic flag flush timer and unregisters WebSocket event handlers.
    /// </summary>
    internal void Close()
    {
        _flagFlushTimer?.Dispose();
        _flagFlushTimer = null;
        if (_wsManager is not null)
        {
            _wsManager.Off("flag_changed", HandleFlagChanged);
            _wsManager.Off("flag_deleted", HandleFlagDeleted);
            _wsManager.Off("flags_changed", HandleFlagsChanged);
            _wsManager = null;
        }
        _wsSubscribed = false;
    }

    // ------------------------------------------------------------------
    // Internal: evaluation
    // ------------------------------------------------------------------

    internal object? EvaluateHandle(string id, object? defaultValue, IReadOnlyList<Context>? context)
    {
        EnsureInitialized();

        Dictionary<string, object?> evalDict;
        if (context is not null)
        {
            evalDict = ContextsToEvalDict(context);
        }
        else if (_contextProvider is not null)
        {
            var contexts = _contextProvider();
            evalDict = ContextsToEvalDict(contexts);
            _contextBuffer.Observe(contexts);
            if (_contextBuffer.PendingCount >= ContextBatchFlushSize)
                _lastContextBufferFlushTask = FlushContextsAsync();
        }
        else
        {
            evalDict = new Dictionary<string, object?>();
        }

        // Auto-inject service context from parent SmplClient
        if (_parent?.Service is { Length: > 0 } svc && !evalDict.ContainsKey("service"))
            evalDict["service"] = new Dictionary<string, object?> { ["key"] = svc };

        var ctxHash = HashContext(evalDict);
        var cacheKey = $"{id}:{ctxHash}";

        var (hit, cachedValue) = _cache.Get(cacheKey);
        if (hit)
        {
            _metrics?.Record("flags.cache_hits", unit: "hits");
            _metrics?.Record("flags.evaluations", unit: "evaluations",
                dimensions: new Dictionary<string, string> { ["flag"] = id });
            return cachedValue;
        }

        if (!_flagStore.TryGetValue(id, out var flagDef))
        {
            _cache.Put(cacheKey, defaultValue);
            return defaultValue;
        }

        var value = EvaluateFlag(flagDef, _environment, evalDict);
        value ??= defaultValue;

        _cache.Put(cacheKey, value);

        _metrics?.Record("flags.cache_misses", unit: "misses");
        _metrics?.Record("flags.evaluations", unit: "evaluations",
            dimensions: new Dictionary<string, string> { ["flag"] = id });

        return value;
    }

    // ------------------------------------------------------------------
    // Internal: event handlers (called by SharedWebSocket)
    // ------------------------------------------------------------------

    private void HandleFlagChanged(Dictionary<string, object?> data)
    {
        var flagId = data.TryGetValue("id", out var k) ? k as string : null;
        Debug.Log("websocket", $"flag_changed event received, id={flagId ?? "<unknown>"}");
        if (flagId is null) return;

        try
        {
            var preState = _flagStore.TryGetValue(flagId, out var prev) ? prev : null;

            var response = _genFlagsClient.Get_flagAsync(flagId).GetAwaiter().GetResult();
            var newState = ParseFlagDef(response.Data);
            if (newState is not null)
                _flagStore[flagId] = newState;

            // Only fire listeners if content actually changed
            if (!FlagDefEquals(preState, newState))
            {
                _cache.Clear();
                FireChangeListeners(flagId, "websocket");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Flag refresh failed: {0}", ex.Message);
            Debug.Log("websocket", $"Flag refresh failed: {ex}");
        }
    }

    private void HandleFlagDeleted(Dictionary<string, object?> data)
    {
        var flagId = data.TryGetValue("id", out var k) ? k as string : null;
        Debug.Log("websocket", $"flag_deleted event received, id={flagId ?? "<unknown>"}");
        if (flagId is null) return;

        _flagStore.TryRemove(flagId, out _);
        _cache.Clear();
        FireChangeListeners(flagId, "websocket", deleted: true);
    }

    private void HandleFlagsChanged(Dictionary<string, object?> data)
    {
        Debug.Log("websocket", "flags_changed event received — full list refetch");
        try
        {
            // Snapshot pre-state
            var preStore = _flagStore.ToDictionary(kv => kv.Key, kv => kv.Value);

            var resources = FetchAllFlagResourcesAsync(default).GetAwaiter().GetResult();
            _flagStore.Clear();
            foreach (var resource in resources)
            {
                var flag = ParseFlagDef(resource);
                if (flag is not null && flag.TryGetValue("id", out var fk) && fk is string fks)
                    _flagStore[fks] = flag;
            }

            _cache.Clear();

            // Compute changed keys (added, modified, removed)
            var allKeys = new HashSet<string>(preStore.Keys);
            allKeys.UnionWith(_flagStore.Keys);
            var changedKeys = allKeys
                .Where(id =>
                {
                    preStore.TryGetValue(id, out var pre);
                    _flagStore.TryGetValue(id, out var post);
                    return !FlagDefEquals(pre, post);
                })
                .ToList();

            if (changedKeys.Count == 0) return;

            // Fire global listener exactly once
            FireGlobalListeners("flags_changed", "websocket");

            // Fire per-key listeners for each changed key
            foreach (var id in changedKeys)
                FireScopedListeners(id, "websocket");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Flags bulk refresh failed: {0}", ex.Message);
            Debug.Log("websocket", $"Flags bulk refresh failed: {ex}");
        }
    }

    // ------------------------------------------------------------------
    // Internal: flag store
    // ------------------------------------------------------------------

    private async Task FetchAllFlagsAsync(CancellationToken ct = default)
    {
        var resources = await FetchAllFlagResourcesAsync(ct).ConfigureAwait(false);
        _flagStore.Clear();
        foreach (var resource in resources)
        {
            var flag = ParseFlagDef(resource);
            if (flag is not null && flag.TryGetValue("id", out var k) && k is string ks)
                _flagStore[ks] = flag;
        }
    }

    /// <summary>
    /// Walks the generated flags-list endpoint page by page until the server
    /// returns fewer rows than requested. The runtime needs every flag for
    /// evaluation, so paging silently here is correct; customers who want
    /// pagination control go through <c>Manage.Flags.ListAsync</c>.
    /// </summary>
    private async Task<List<GenFlags.FlagResource>> FetchAllFlagResourcesAsync(CancellationToken ct)
    {
        return await Helpers.FetchAllPagesAsync<GenFlags.FlagResource>(
            async (page, size, c) =>
            {
                var response = await ApiExceptionMapper.ExecuteAsync(
                    () => _genFlagsClient.List_flagsAsync(
                        pagenumber: page,
                        pagesize: size,
                        cancellationToken: c)).ConfigureAwait(false);
                return response.Data?.ToList() ?? new List<GenFlags.FlagResource>();
            }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Internal: change listeners
    // ------------------------------------------------------------------

    private void FireChangeListeners(string? flagId, string source, bool deleted = false)
    {
        if (flagId is null) return;
        var evt = new FlagChangeEvent(flagId, source, deleted);
        foreach (var cb in _globalListeners)
        {
            try { cb(evt); }
            catch { /* Ignore listener exceptions */ }
        }
        if (_scopedListeners.TryGetValue(flagId, out var scopedList))
        {
            List<Action<FlagChangeEvent>> snapshot;
            lock (scopedList)
            {
                snapshot = new List<Action<FlagChangeEvent>>(scopedList);
            }
            foreach (var cb in snapshot)
            {
                try { cb(evt); }
                catch { /* Ignore listener exceptions */ }
            }
        }
    }

    private void FireGlobalListeners(string flagId, string source)
    {
        var evt = new FlagChangeEvent(flagId, source);
        foreach (var cb in _globalListeners)
        {
            try { cb(evt); }
            catch { /* Ignore listener exceptions */ }
        }
    }

    private void FireScopedListeners(string flagId, string source)
    {
        if (!_scopedListeners.TryGetValue(flagId, out var scopedList)) return;
        List<Action<FlagChangeEvent>> snapshot;
        lock (scopedList)
        {
            snapshot = new List<Action<FlagChangeEvent>>(scopedList);
        }
        var evt = new FlagChangeEvent(flagId, source);
        foreach (var cb in snapshot)
        {
            try { cb(evt); }
            catch { /* Ignore listener exceptions */ }
        }
    }

    private void FireChangeListenersAll(string source)
    {
        foreach (var id in _flagStore.Keys)
            FireChangeListeners(id, source);
    }

    // ------------------------------------------------------------------
    // Helpers: flag def comparison
    // ------------------------------------------------------------------

    private static bool FlagDefEquals(Dictionary<string, object?>? a, Dictionary<string, object?>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        foreach (var (key, val) in a)
        {
            if (!b.TryGetValue(key, out var bVal)) return false;
            // Use JSON-serialized comparison for deep equality
            var aJson = System.Text.Json.JsonSerializer.Serialize(val, JsonOptions.Default);
            var bJson = System.Text.Json.JsonSerializer.Serialize(bVal, JsonOptions.Default);
            if (aJson != bJson) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Helpers: JSON Logic evaluation
    // ------------------------------------------------------------------

    private static readonly JsonLogicEvaluator JsonLogicEval = new(EvaluateOperators.Default);

    internal static object? EvaluateFlag(
        Dictionary<string, object?> flagDef,
        string? environment,
        Dictionary<string, object?> evalDict)
    {
        var flagDefault = flagDef.TryGetValue("default", out var fd) ? fd : null;

        if (environment is null || !flagDef.TryGetValue("environments", out var envsObj) || envsObj is null)
            return flagDefault;

        Dictionary<string, object?>? envConfig = null;
        if (envsObj is Dictionary<string, Dictionary<string, object?>> typedEnvs)
        {
            if (!typedEnvs.TryGetValue(environment, out var ec)) return flagDefault;
            envConfig = ec;
        }
        else if (envsObj is Dictionary<string, object?> untypedEnvs)
        {
            if (!untypedEnvs.TryGetValue(environment, out var ecObj)) return flagDefault;
            envConfig = ecObj as Dictionary<string, object?>;
        }

        if (envConfig is null) return flagDefault;

        var envDefault = envConfig.TryGetValue("default", out var ed) ? ed : null;
        var fallback = envDefault ?? flagDefault;

        if (envConfig.TryGetValue("enabled", out var enabledObj))
        {
            bool enabled = enabledObj switch
            {
                bool b => b,
                JsonElement je when je.ValueKind == JsonValueKind.True => true,
                JsonElement je when je.ValueKind == JsonValueKind.False => false,
                _ => false,
            };
            if (!enabled) return fallback;
        }
        else
        {
            return fallback;
        }

        var rules = GetRulesList(envConfig);
        foreach (var rule in rules)
        {
            if (rule is not Dictionary<string, object?> ruleDict) continue;
            var logic = ruleDict.TryGetValue("logic", out var l) ? l : null;
            if (logic is null || (logic is Dictionary<string, object?> ld && ld.Count == 0))
                continue;

            try
            {
                var logicJson = JsonSerializer.Serialize(logic, JsonOptions.Default);
                var dataJson = JsonSerializer.Serialize(evalDict, JsonOptions.Default);
                var logicToken = JToken.Parse(logicJson);
                var dataToken = JToken.Parse(dataJson);

                var result = JsonLogicEval.Apply(logicToken, dataToken);
                if (IsTruthy(result as JToken ?? JToken.FromObject(result ?? false)))
                    return ruleDict.TryGetValue("value", out var v) ? NormalizeValue(v) : null;
            }
            catch
            {
                continue;
            }
        }

        return fallback;
    }

    private static List<object?> GetRulesList(Dictionary<string, object?> envConfig)
    {
        if (!envConfig.TryGetValue("rules", out var rulesObj)) return new List<object?>();

        if (rulesObj is List<object?> list) return list;

        if (rulesObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            var result = new List<object?>();
            foreach (var elem in je.EnumerateArray())
            {
                result.Add(Config.Resolver.Normalize(elem));
            }
            return result;
        }

        if (rulesObj is object?[] arr)
            return arr.ToList();

        return new List<object?>();
    }

    private static bool IsTruthy(JToken? token)
    {
        if (token is null) return false;
        return token.Type switch
        {
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.Integer => token.Value<long>() != 0,
            JTokenType.Float => token.Value<double>() != 0.0,
            JTokenType.String => !string.IsNullOrEmpty(token.Value<string>()),
            JTokenType.Null => false,
            _ => true,
        };
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is JsonElement je)
            return Config.Resolver.Normalize(je);
        return value;
    }

    // ------------------------------------------------------------------
    // Helpers: context
    // ------------------------------------------------------------------

    private static Dictionary<string, object?> ContextsToEvalDict(IEnumerable<Context> contexts)
    {
        var result = new Dictionary<string, object?>();
        foreach (var ctx in contexts)
        {
            var entry = new Dictionary<string, object?>(ctx.Attributes) { ["key"] = ctx.Key };
            result[ctx.Type] = entry;
        }
        return result;
    }

    private static string HashContext(Dictionary<string, object?> evalDict)
    {
        var serialized = JsonSerializer.Serialize(evalDict, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
        });
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(serialized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // Helpers: runtime flag-store parsing (used by HandleFlag* + FetchAllFlagsAsync).
    // CRUD wire helpers (MapFlagResource, BuildCreate/UpdateFlagBody) live in
    // Smplkit.Management.FlagsClient — runtime owns only what eval needs.
    // ------------------------------------------------------------------

    private static Dictionary<string, object?>? ParseFlagDef(GenFlags.FlagResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        List<Dictionary<string, object?>>? values = null;
        if (attrs.Values is not null)
        {
            values = new List<Dictionary<string, object?>>();
            foreach (var v in attrs.Values)
                values.Add(new Dictionary<string, object?> { ["name"] = v.Name, ["value"] = NormalizeValue(v.Value) });
        }

        var environments = ExtractEnvironments(attrs.Environments);

        return new Dictionary<string, object?>
        {
            ["id"] = resource.Id,
            ["name"] = attrs.Name,
            ["type"] = attrs.Type,
            ["default"] = NormalizeValue(attrs.Default),
            ["values"] = values,
            ["description"] = attrs.Description,
            ["environments"] = environments,
        };
    }

    private static Dictionary<string, Dictionary<string, object?>> ExtractEnvironments(
        IDictionary<string, GenFlags.FlagEnvironment>? environments)
    {
        if (environments is null) return new Dictionary<string, Dictionary<string, object?>>();

        var result = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var (envName, envData) in environments)
        {
            var normalized = new Dictionary<string, object?>
            {
                ["enabled"] = envData.Enabled,
                ["default"] = NormalizeValue(envData.Default),
            };
            if (envData.Rules is not null)
            {
                var rules = new List<object?>();
                foreach (var rule in envData.Rules)
                {
                    rules.Add(new Dictionary<string, object?>
                    {
                        ["description"] = rule.Description,
                        ["logic"] = NormalizeValue(rule.Logic),
                        ["value"] = NormalizeValue(rule.Value),
                    });
                }
                normalized["rules"] = rules;
            }
            result[envName] = normalized;
        }
        return result;
    }

}

// ------------------------------------------------------------------
// Resolution cache
// ------------------------------------------------------------------

internal sealed class ResolutionCache
{
    private readonly int _maxSize;
    private readonly LinkedList<(string Key, object? Value)> _list = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, object? Value)>> _map = new();
    private readonly object _lock = new();

    internal int CacheHits;
    internal int CacheMisses;

    internal ResolutionCache(int maxSize)
    {
        _maxSize = maxSize;
    }

    internal (bool Hit, object? Value) Get(string cacheKey)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(cacheKey, out var node))
            {
                _list.Remove(node);
                _list.AddLast(node);
                Interlocked.Increment(ref CacheHits);
                return (true, node.Value.Value);
            }
            Interlocked.Increment(ref CacheMisses);
            return (false, null);
        }
    }

    internal void Put(string cacheKey, object? value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(cacheKey, out var existing))
            {
                _list.Remove(existing);
                _map.Remove(cacheKey);
            }
            var node = _list.AddLast((cacheKey, value));
            _map[cacheKey] = node;
            if (_map.Count > _maxSize)
            {
                var oldest = _list.First!;
                _map.Remove(oldest.Value.Key);
                _list.RemoveFirst();
            }
        }
    }

    internal void Clear()
    {
        lock (_lock)
        {
            _list.Clear();
            _map.Clear();
        }
    }
}

// ------------------------------------------------------------------
// Context registration buffer
// ------------------------------------------------------------------

internal sealed class ContextRegistrationBuffer
{
    private readonly int _lruSize;
    private readonly int _flushSize;
    private readonly LinkedList<(string Type, string Key)> _seenOrder = new();
    private readonly Dictionary<(string Type, string Key), LinkedListNode<(string Type, string Key)>> _seenMap = new();
    private readonly List<Dictionary<string, object?>> _pending = new();
    private readonly object _lock = new();

    internal ContextRegistrationBuffer(int lruSize, int flushSize)
    {
        _lruSize = lruSize;
        _flushSize = flushSize;
    }

    internal void Observe(IEnumerable<Context> contexts)
    {
        lock (_lock)
        {
            foreach (var ctx in contexts)
            {
                var cacheKey = (ctx.Type, ctx.Key);
                if (!_seenMap.ContainsKey(cacheKey))
                {
                    if (_seenMap.Count >= _lruSize)
                    {
                        var oldest = _seenOrder.First!;
                        _seenMap.Remove(oldest.Value);
                        _seenOrder.RemoveFirst();
                    }
                    var node = _seenOrder.AddLast(cacheKey);
                    _seenMap[cacheKey] = node;
                    _pending.Add(new Dictionary<string, object?>
                    {
                        ["id"] = $"{ctx.Type}:{ctx.Key}",
                        ["attributes"] = new Dictionary<string, object?>(ctx.Attributes),
                    });
                }
            }
        }
    }

    internal List<Dictionary<string, object?>> Drain()
    {
        lock (_lock)
        {
            var batch = new List<Dictionary<string, object?>>(_pending);
            _pending.Clear();
            return batch;
        }
    }

    internal int PendingCount
    {
        get { lock (_lock) return _pending.Count; }
    }
}

// ------------------------------------------------------------------
// Flag registration buffer
// ------------------------------------------------------------------

internal sealed class FlagRegistrationBuffer
{
    private readonly HashSet<string> _seen = new();
    private readonly List<FlagRegistrationEntry> _pending = new();
    private readonly object _lock = new();

    internal void Add(string id, string type, object? defaultValue, string? service, string? environment)
    {
        lock (_lock)
        {
            if (_seen.Add(id))
            {
                _pending.Add(new FlagRegistrationEntry(id, type, defaultValue, service, environment));
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of pending entries without removing them.
    /// Used by the send path: call <see cref="Commit"/> after a successful POST.
    /// </summary>
    internal List<FlagRegistrationEntry> Peek()
    {
        lock (_lock)
        {
            return new List<FlagRegistrationEntry>(_pending);
        }
    }

    /// <summary>
    /// Removes entries with the specified ids from the pending list.
    /// Call this after a successful bulk-register POST. Any entries added
    /// between the preceding <see cref="Peek"/> and this call are left intact.
    /// </summary>
    internal void Commit(IEnumerable<string> ids)
    {
        var committed = new HashSet<string>(ids);
        if (committed.Count == 0) return;
        lock (_lock)
        {
            _pending.RemoveAll(e => committed.Contains(e.Id));
        }
    }

    /// <summary>
    /// Returns and clears all pending entries unconditionally.
    /// Used only by tests and teardown paths where retaining on failure is not needed.
    /// </summary>
    internal List<FlagRegistrationEntry> Drain()
    {
        lock (_lock)
        {
            var batch = new List<FlagRegistrationEntry>(_pending);
            _pending.Clear();
            return batch;
        }
    }

    internal int PendingCount
    {
        get { lock (_lock) { return _pending.Count; } }
    }

    internal record FlagRegistrationEntry(string Id, string Type, object? DefaultValue, string? Service, string? Environment);
}
