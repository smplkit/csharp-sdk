using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smplkit.Errors;
using Smplkit.Internal;
using Smplkit.Logging.Adapters;
using GenLogging = Smplkit.Internal.Generated.Logging;
using DebugLog = Smplkit.Internal.Debug;

namespace Smplkit.Logging;

/// <summary>
/// Client for the smplkit Logging service. Provides operations for creating,
/// reading, updating, and deleting loggers and log groups, as well as dynamic
/// level control via <see cref="InstallAsync"/>.
/// </summary>
public sealed class LoggingClient
{
    private readonly GenLogging.LoggingClient _genClient;
    private readonly string _apiKey;
    private readonly Func<SharedWebSocket> _ensureWs;
    private readonly SmplClient? _parent;
    private volatile bool _started;
    private SharedWebSocket? _wsManager;
    private readonly List<ILoggingAdapter> _adapters = new();
    private readonly List<Action<LoggerChangeEvent>> _globalListeners = new();
    private readonly Dictionary<string, List<Action<LoggerChangeEvent>>> _scopedListeners = new();
    private readonly object _listenerLock = new();
    // Resolution state — guarded by _loggerCacheLock.
    //   _loggersCache: server-side loggers (id → full record) used to drive resolution
    //   _groupsCache:  server-side log groups (id → full record) used to walk group chains
    //   _loggerLevelCache: last-known *resolved* level per managed logger (drives diff/listener firing)
    private readonly Dictionary<string, Logger> _loggersCache = new();
    private readonly Dictionary<string, LogGroup> _groupsCache = new();
    private readonly Dictionary<string, LogLevel> _loggerLevelCache = new();
    private readonly object _loggerCacheLock = new();

    /// <summary>
    /// Initializes a new instance of <see cref="LoggingClient"/>.
    /// </summary>
    private readonly MetricsReporter? _metrics;
    private readonly LoggerRegistrationBuffer _loggerBuffer = new();
    private Timer? _loggerFlushTimer;

    // Exposed for tests to await fire-and-forget threshold-triggered flushes
    // and the websocket-handler async work (otherwise the lambda body
    // coverage races with process exit on CI).
    internal Task? _lastLoggerBufferFlushTask;
    internal Task? _lastLoggerChangedTask;
    internal Task? _lastGroupChangedTask;
    internal Task? _lastLoggersChangedTask;

    internal LoggingClient(GeneratedClientFactory clients, string apiKey, Func<SharedWebSocket> ensureWs, SmplClient? parent = null, MetricsReporter? metrics = null)
    {
        _genClient = clients.Logging;
        _apiKey = apiKey;
        _ensureWs = ensureWs;
        _parent = parent;
        _metrics = metrics;
    }

    // ------------------------------------------------------------------
    // Adapter registration
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers a logging adapter. Must be called before <see cref="InstallAsync"/>.
    /// </summary>
    /// <remarks>
    /// For Microsoft.Extensions.Logging, prefer the
    /// <see cref="SmplkitLoggingBuilderExtensions.AddSmplkit"/> extension on
    /// <see cref="ILoggingBuilder"/> — it constructs the adapter, registers it
    /// in DI as <see cref="ILoggerProvider"/> +
    /// <see cref="IConfigureOptions{LoggerFilterOptions}"/> +
    /// <see cref="IOptionsChangeTokenSource{LoggerFilterOptions}"/>, and calls
    /// this method in one step.
    /// </remarks>
    /// <param name="adapter">The adapter to register.</param>
    /// <exception cref="InvalidOperationException">If called after <see cref="InstallAsync"/>.</exception>
    public void RegisterAdapter(ILoggingAdapter adapter)
    {
        if (_started)
            throw new InvalidOperationException("Cannot register adapters after InstallAsync()");
        _adapters.Add(adapter);
    }

    // ------------------------------------------------------------------
    // Wire CRUD code (factory, GetAsync, ListAsync, DeleteAsync, Save,
    // request-body building, MapLoggerResource, MapLogGroupResource,
    // BuildLoggerRequestBody, BuildLogGroupRequestBody) lives in
    // Smplkit.Management.LoggersClient and Smplkit.Management.LogGroupsClient.
    // The runtime client routes its initial fetch / refresh / single-resource
    // refresh through the management plane via _parent.Manage.Loggers and
    // _parent.Manage.LogGroups — there is no duplicated wire code here.
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Runtime: InstallAsync
    // ------------------------------------------------------------------

    /// <summary>
    /// Starts dynamic log level control. Applies server-defined levels to registered
    /// adapters and subscribes to real-time level updates. Idempotent.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InstallAsync(CancellationToken ct = default)
    {
        if (_started) return;

        // 1. Discover existing loggers from each adapter and add to buffer.
        //    Adapters must be wired in explicitly — for Microsoft.Extensions.Logging,
        //    via the AddSmplkit ILoggingBuilder extension; for Serilog, via
        //    SerilogAdapter.GetOrCreateSwitch(...) wired into LoggerConfiguration.
        DebugLog.Log("websocket", "logging runtime initializing");
        var discovered = DiscoverAll();
        DebugLog.Log("discovery", $"discovered {discovered.Count} loggers from adapters");
        foreach (var d in discovered)
            _loggerBuffer.Add(d.Name, null, d.Level.ToWireString(), _parent?.Service, _parent?.Environment);

        // 3. Install hooks on each adapter
        InstallAllHooks();
        DebugLog.Log("registration", $"installed hooks on {_adapters.Count} adapters");

        // 4. Flush discovered loggers to server via buffer
        await FlushLoggerBufferAsync(ct).ConfigureAwait(false);

        // 5. Fetch all loggers and groups from the server, populating caches
        var loggers = await FetchAllLoggersAsync(ct).ConfigureAwait(false);
        var groups = await FetchAllLogGroupsAsync(ct).ConfigureAwait(false);
        lock (_loggerCacheLock)
        {
            ReplaceLoggersCacheLocked(loggers);
            ReplaceGroupsCacheLocked(groups);
        }

        // 6. Resolve and apply effective levels (logger override → logger base →
        //    group chain → dot-notation ancestor → INFO fallback). Seeds the
        //    level cache from the resolved levels, so subsequent diffs fire on
        //    *resolved* changes — including group-driven ones.
        ApplyResolvedLevels(seedDiffCache: true);

        // 7. Wire WebSocket
        DebugLog.Log("registration", "registering logger_changed, logger_deleted, group_changed, group_deleted, loggers_changed handlers");
        _wsManager = _ensureWs();
        _wsManager.On("logger_changed", HandleLoggerChanged);
        _wsManager.On("logger_deleted", HandleLoggerDeleted);
        _wsManager.On("group_changed", HandleGroupChanged);
        _wsManager.On("group_deleted", HandleGroupDeleted);
        _wsManager.On("loggers_changed", HandleLoggersChanged);
        _started = true;

        // 8. Start periodic flush timer for post-startup loggers
        _loggerFlushTimer = new Timer(_ => OnFlushTimer(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        DebugLog.Log("websocket", "logging runtime connected");
    }

    // ------------------------------------------------------------------
    // Runtime: change listeners
    // ------------------------------------------------------------------

    /// <summary>
    /// Register a global change listener that fires when any logger changes.
    /// </summary>
    /// <param name="callback">Called with a <see cref="LoggerChangeEvent"/> on each change.</param>
    public void OnChange(Action<LoggerChangeEvent> callback)
    {
        lock (_listenerLock)
        {
            _globalListeners.Add(callback);
        }
    }

    /// <summary>
    /// Register a change listener scoped to a specific logger id.
    /// </summary>
    /// <param name="loggerId">The logger identifier to listen for.</param>
    /// <param name="callback">Called with a <see cref="LoggerChangeEvent"/> when this logger changes.</param>
    public void OnChange(string loggerId, Action<LoggerChangeEvent> callback)
    {
        lock (_listenerLock)
        {
            if (!_scopedListeners.TryGetValue(loggerId, out var list))
            {
                list = new List<Action<LoggerChangeEvent>>();
                _scopedListeners[loggerId] = list;
            }
            list.Add(callback);
        }
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Stops dynamic log level control and releases resources.
    /// </summary>
    internal void Close()
    {
        foreach (var adapter in _adapters)
        {
            try { adapter.UninstallHook(); }
            catch { /* Best effort */ }
        }

        _loggerFlushTimer?.Dispose();
        _loggerFlushTimer = null;

        if (_wsManager is not null)
        {
            _wsManager.Off("logger_changed", HandleLoggerChanged);
            _wsManager.Off("logger_deleted", HandleLoggerDeleted);
            _wsManager.Off("group_changed", HandleGroupChanged);
            _wsManager.Off("group_deleted", HandleGroupDeleted);
            _wsManager.Off("loggers_changed", HandleLoggersChanged);
            _wsManager = null;
        }
        _started = false;
        DebugLog.Log("lifecycle", "LoggingClient closed");
    }

    // ------------------------------------------------------------------
    // Internal: adapter helpers
    // ------------------------------------------------------------------

    private List<DiscoveredLogger> DiscoverAll()
    {
        var allDiscovered = new List<DiscoveredLogger>();
        foreach (var adapter in _adapters)
        {
            try
            {
                var discovered = adapter.Discover();
                allDiscovered.AddRange(discovered);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[smplkit] Adapter {0} discovery failed: {1}", adapter.Name, ex.Message);
            }
        }
        if (allDiscovered.Count > 0)
            _metrics?.Record("logging.loggers_discovered", value: allDiscovered.Count, unit: "loggers");
        return allDiscovered;
    }

    private void InstallAllHooks()
    {
        foreach (var adapter in _adapters)
        {
            try { adapter.InstallHook(HandleAdapterNewLogger); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[smplkit] Adapter {0} hook installation failed: {1}", adapter.Name, ex.Message);
            }
        }
    }

    internal async Task BulkRegisterAsync(IReadOnlyList<DiscoveredLogger> discovered, CancellationToken ct = default)
    {
        var service = _parent?.Service;
        var environment = _parent?.Environment;
        var items = discovered
            .Select(d => BuildBulkItem(d, service, environment))
            .ToList();

        var request = new GenLogging.LoggerBulkRequest { Loggers = items };
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Bulk_register_loggersAsync(request, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers explicit logger sources. Accepts per-source service and environment overrides —
    /// useful for sample-data seeding, cross-service migration, and test fixtures.
    /// </summary>
    /// <param name="sources">Logger sources to register.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task RegisterSourcesAsync(IEnumerable<LoggerSource> sources, CancellationToken ct = default)
    {
        var items = sources.Select(s => new GenLogging.LoggerBulkItem
        {
            Id = s.Name,
            Level = s.Level?.ToWireString(),
            Resolved_level = s.ResolvedLevel?.ToWireString(),
            Service = s.Service,
            Environment = s.Environment,
        }).ToList();

        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Bulk_register_loggersAsync(
                new GenLogging.LoggerBulkRequest { Loggers = items }, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a <see cref="GenLogging.LoggerBulkItem"/> from a discovered logger.
    /// <para>
    /// MEL and Serilog adapters track only the effective (resolved) level — they have
    /// no concept of an explicitly-set vs. inherited level. The payload therefore sets
    /// <c>level</c> to <see langword="null"/> (not explicitly configured) and
    /// <c>resolved_level</c> to the adapter's current minimum level.
    /// </para>
    /// </summary>
    internal static GenLogging.LoggerBulkItem BuildBulkItem(DiscoveredLogger discovered, string? service = null, string? environment = null) =>
        new()
        {
            Id = discovered.Name,
            Level = null,
            Resolved_level = discovered.Level.ToWireString(),
            Service = service,
            Environment = environment,
        };

    /// <summary>
    /// Re-resolves every managed logger in the cache against the current group
    /// cache and pushes the resolved level to every registered adapter. When
    /// <paramref name="seedDiffCache"/> is <c>true</c>, also overwrites
    /// <see cref="_loggerLevelCache"/> so future calls can detect deltas; when
    /// <c>false</c>, returns the per-logger deltas (id → new resolved level)
    /// without touching the diff cache, so the caller can fire listeners.
    /// </summary>
    private List<(string Id, LogLevel Level)> ApplyResolvedLevels(bool seedDiffCache)
    {
        var changedLoggers = new List<(string Id, LogLevel Level)>();

        // Snapshot under the cache lock so cache mutations during apply can't
        // tear our view. Adapters are invoked outside the lock to avoid
        // holding it across user code.
        Dictionary<string, LogLevel> resolved;
        Dictionary<string, LogLevel> previous;
        lock (_loggerCacheLock)
        {
            resolved = SnapshotResolvedLevelsLocked();

            if (seedDiffCache)
            {
                previous = new Dictionary<string, LogLevel>();
                _loggerLevelCache.Clear();
                foreach (var (id, lvl) in resolved)
                    _loggerLevelCache[id] = lvl;
            }
            else
            {
                previous = new Dictionary<string, LogLevel>(_loggerLevelCache);
                foreach (var (id, lvl) in resolved)
                    _loggerLevelCache[id] = lvl;
                foreach (var staleId in previous.Keys.Where(k => !resolved.ContainsKey(k)).ToList())
                    _loggerLevelCache.Remove(staleId);

                var allIds = new HashSet<string>(previous.Keys);
                allIds.UnionWith(resolved.Keys);
                foreach (var id in allIds)
                {
                    var hadPrev = previous.TryGetValue(id, out var prev);
                    var hasNew = resolved.TryGetValue(id, out var next);
                    if (hasNew && (!hadPrev || !Equals(prev, next)))
                        changedLoggers.Add((id, next));
                }
            }
        }

        if (_adapters.Count > 0)
        {
            foreach (var (id, level) in resolved)
            {
                foreach (var adapter in _adapters)
                {
                    try { adapter.ApplyLevel(id, level); }
                    catch { /* Adapter failure is non-fatal */ }
                }
                _metrics?.Record("logging.level_changes", unit: "changes",
                    dimensions: new Dictionary<string, string> { ["logger"] = id });
            }
        }

        return changedLoggers;
    }

    /// <summary>
    /// Resolves the effective level for every managed logger currently in the
    /// cache. Called under <see cref="_loggerCacheLock"/>.
    /// </summary>
    private Dictionary<string, LogLevel> SnapshotResolvedLevelsLocked()
    {
        var environment = _parent?.Environment ?? string.Empty;
        var loggerEntries = BuildResolutionEntries(_loggersCache);
        var groupEntries = BuildResolutionEntries(_groupsCache);

        var result = new Dictionary<string, LogLevel>();
        foreach (var (id, logger) in _loggersCache)
        {
            if (!logger.Managed) continue;
            result[id] = Smplkit.Internal.LevelResolver.Resolve(id, environment, loggerEntries, groupEntries);
        }
        return result;
    }

    private static Dictionary<string, Smplkit.Internal.LevelEntry> BuildResolutionEntries(Dictionary<string, Logger> source)
    {
        var entries = new Dictionary<string, Smplkit.Internal.LevelEntry>(source.Count);
        foreach (var (id, l) in source)
            entries[id] = new Smplkit.Internal.LevelEntry(l.Level, l.Group, l.Environments);
        return entries;
    }

    private static Dictionary<string, Smplkit.Internal.LevelEntry> BuildResolutionEntries(Dictionary<string, LogGroup> source)
    {
        var entries = new Dictionary<string, Smplkit.Internal.LevelEntry>(source.Count);
        foreach (var (id, g) in source)
            entries[id] = new Smplkit.Internal.LevelEntry(g.Level, g.Group, g.Environments);
        return entries;
    }

    private void ReplaceLoggersCacheLocked(List<Logger> loggers)
    {
        _loggersCache.Clear();
        foreach (var l in loggers)
            if (l.Id is not null)
                _loggersCache[l.Id] = l;
    }

    private void ReplaceGroupsCacheLocked(List<LogGroup> groups)
    {
        _groupsCache.Clear();
        foreach (var g in groups)
            if (g.Id is not null)
                _groupsCache[g.Id] = g;
    }

    private void HandleAdapterNewLogger(string loggerName, LogLevel level)
    {
        var smplLevel = level.ToWireString();
        _loggerBuffer.Add(loggerName, null, smplLevel, _parent?.Service, _parent?.Environment);

        if (_loggerBuffer.PendingCount >= 50)
            _lastLoggerBufferFlushTask = FlushLoggerBufferAsync();

        // Still fire listeners for immediate in-process notification
        var evt = new LoggerChangeEvent(loggerName, level, "adapter");
        FireListeners(loggerName, evt);
    }

    internal async Task FlushLoggerBufferAsync(CancellationToken ct = default)
    {
        var batch = _loggerBuffer.Drain();
        if (batch.Count == 0) return;

        var items = batch.Select(e =>
        {
            var item = new GenLogging.LoggerBulkItem
            {
                Id = e.Id,
                Resolved_level = e.ResolvedLevel,
            };
            if (e.Level is not null) item.Level = e.Level;
            if (e.Service is not null) item.Service = e.Service;
            if (e.Environment is not null) item.Environment = e.Environment;
            return item;
        }).ToList();

        var request = new GenLogging.LoggerBulkRequest { Loggers = items };
        try
        {
            await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Bulk_register_loggersAsync(request, ct)).ConfigureAwait(false);
            DebugLog.Log("registration", $"bulk-registered {batch.Count} logger(s)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Logger buffer flush failed: {0}", ex.Message);
        }
    }

    internal void OnFlushTimer()
    {
        FlushLoggerBufferAsync().GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------
    // Internal: event handlers
    // ------------------------------------------------------------------

    private void HandleLoggerChanged(Dictionary<string, object?> data)
    {
        var loggerId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"logger_changed event received, id={loggerId ?? "<unknown>"}");
        if (loggerId is null || !_started) return;
        _lastLoggerChangedTask = HandleLoggerChangedAsync(loggerId);
    }

    private async Task HandleLoggerChangedAsync(string loggerId)
    {
        try
        {
            // Scoped fetch: GET just the single changed logger, refresh the cache
            // entry, then re-resolve and apply. Group-driven effects on this
            // logger would already be captured by group_changed; this handler
            // covers the logger-side config (own level, env overrides, group id).
            if (_parent?.Manage.Loggers is not { } mgmtL) return;
            var logger = await mgmtL.GetAsync(loggerId).ConfigureAwait(false);

            lock (_loggerCacheLock)
            {
                _loggersCache[loggerId] = logger;
            }

            FireDeltasFromApply("websocket");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Logger refresh failed: {0}", ex.Message);
            DebugLog.Log("websocket", $"Logger refresh failed: {ex}");
        }
    }

    private void HandleLoggerDeleted(Dictionary<string, object?> data)
    {
        var loggerId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"logger_deleted event received, id={loggerId ?? "<unknown>"}");
        if (loggerId is null || !_started) return;

        // Deletion is a cache eviction, not a level change — no event fires
        // for the deleted id itself. Dependent loggers that had inherited
        // from this logger via dot-ancestry re-resolve through the normal
        // apply path; their listeners fire only if the resolved level moves.
        bool wasKnown;
        lock (_loggerCacheLock)
        {
            wasKnown = _loggersCache.Remove(loggerId);
        }
        if (wasKnown)
            FireDeltasFromApply("websocket");
    }

    private void HandleGroupChanged(Dictionary<string, object?> data)
    {
        var groupId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"group_changed event received, id={groupId ?? "<unknown>"}");
        if (groupId is null || !_started) return;
        _lastGroupChangedTask = HandleGroupChangedAsync(groupId);
    }

    private async Task HandleGroupChangedAsync(string groupId)
    {
        try
        {
            // Scoped fetch: GET just the single changed group, update the cache,
            // then re-resolve every logger. Loggers that inherit (directly or
            // via the parent chain) will pick up the new level; loggers with
            // their own level resolve identically and produce no delta.
            if (_parent?.Manage.LogGroups is not { } mgmtG) return;
            var group = await mgmtG.GetAsync(groupId).ConfigureAwait(false);

            lock (_loggerCacheLock)
            {
                _groupsCache[groupId] = group;
            }

            FireDeltasFromApply("websocket");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Logger group refresh failed: {0}", ex.Message);
            DebugLog.Log("websocket", $"Logger group refresh failed: {ex}");
        }
    }

    private void HandleGroupDeleted(Dictionary<string, object?> data)
    {
        var groupId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"group_deleted event received, id={groupId ?? "<unknown>"}");
        if (groupId is null || !_started) return;

        // Drop the group then re-resolve. Loggers that inherited from this
        // group fall through to dot-notation ancestry / the INFO fallback;
        // listeners fire only on loggers whose resolved level actually moves.
        bool wasKnown;
        lock (_loggerCacheLock)
        {
            wasKnown = _groupsCache.Remove(groupId);
        }
        if (wasKnown)
            FireDeltasFromApply("websocket");
    }

    private void HandleLoggersChanged(Dictionary<string, object?> data)
    {
        DebugLog.Log("websocket", "loggers_changed event received — full refetch");
        if (!_started) return;
        _lastLoggersChangedTask = HandleLoggersChangedAsync();
    }

    private async Task HandleLoggersChangedAsync()
    {
        try
        {
            await RefetchAndApplyAsync("websocket").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Loggers bulk refresh failed: {0}", ex.Message);
            DebugLog.Log("websocket", $"Loggers bulk refresh failed: {ex}");
        }
    }

    /// <summary>
    /// Re-fetches managed loggers and log groups from the server and re-applies
    /// their levels onto every registered adapter. Fires change listeners with
    /// <c>Source = "manual"</c> for any logger whose effective level differs
    /// from the cached value. Errors propagate to the caller — the websocket
    /// path swallows them, but a user-initiated refresh does not.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task RefreshAsync(CancellationToken ct = default)
        => RefetchAndApplyAsync("manual", ct);

    private async Task RefetchAndApplyAsync(string source, CancellationToken ct = default)
    {
        // Full refetch of both loggers and groups.
        var loggers = await FetchAllLoggersAsync(ct).ConfigureAwait(false);
        var groups = await FetchAllLogGroupsAsync(ct).ConfigureAwait(false);

        lock (_loggerCacheLock)
        {
            ReplaceLoggersCacheLocked(loggers);
            ReplaceGroupsCacheLocked(groups);
        }

        // Per-logger fanout — global subscribers see one event per affected
        // logger, not a single summary event. Identical to the websocket
        // handlers; only the source label differs.
        FireDeltasFromApply(source);
    }

    /// <summary>
    /// Re-resolves every managed logger and fires <see cref="FireListeners"/>
    /// (every global subscriber + matching key-scoped subscribers) once per
    /// logger whose effective level moved. Shared by every handler that
    /// needs to drive an apply/fire cycle: logger_changed, logger_deleted,
    /// group_changed, group_deleted, loggers_changed, and the public
    /// <see cref="RefreshAsync"/>. The diff cache is updated in place —
    /// callers don't need to track previous state.
    /// </summary>
    private void FireDeltasFromApply(string source)
    {
        foreach (var (id, level) in ApplyResolvedLevels(seedDiffCache: false))
        {
            var evt = new LoggerChangeEvent(id, level, source);
            FireListeners(id, evt);
        }
    }

    /// <summary>
    /// Walks <c>Manage.Loggers.ListAsync</c> page by page until the server
    /// returns fewer rows than requested. The runtime needs every managed
    /// logger to apply effective levels to adapters; customers who want
    /// pagination control go through <c>Manage.Loggers.ListAsync</c>.
    /// </summary>
    private async Task<List<Logger>> FetchAllLoggersAsync(CancellationToken ct)
    {
        if (_parent?.Manage.Loggers is not { } mgmtLn) return new List<Logger>();
        return await Smplkit.Internal.Helpers.FetchAllPagesAsync(
            (page, size, c) => mgmtLn.ListAsync(page, size, c), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Walks <c>Manage.LogGroups.ListAsync</c> page by page until the server
    /// returns a short page. The runtime feeds the result into the group cache
    /// so the resolver can walk group chains for inheritance.
    /// </summary>
    private async Task<List<LogGroup>> FetchAllLogGroupsAsync(CancellationToken ct)
    {
        if (_parent?.Manage.LogGroups is not { } mgmtGn) return new List<LogGroup>();
        return await Smplkit.Internal.Helpers.FetchAllPagesAsync(
            (page, size, c) => mgmtGn.ListAsync(page, size, c), ct).ConfigureAwait(false);
    }

    private void FireListeners(string loggerId, LoggerChangeEvent evt)
    {
        List<Action<LoggerChangeEvent>> globalCopy;
        List<Action<LoggerChangeEvent>>? scopedCopy = null;

        lock (_listenerLock)
        {
            globalCopy = new List<Action<LoggerChangeEvent>>(_globalListeners);
            if (_scopedListeners.TryGetValue(loggerId, out var scoped))
                scopedCopy = new List<Action<LoggerChangeEvent>>(scoped);
        }

        foreach (var cb in globalCopy)
        {
            try { cb(evt); }
            catch { /* Ignore listener exceptions */ }
        }
        if (scopedCopy is not null)
        {
            foreach (var cb in scopedCopy)
            {
                try { cb(evt); }
                catch { /* Ignore listener exceptions */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // Inner: registration buffer
    // ------------------------------------------------------------------

    private sealed class LoggerRegistrationBuffer
    {
        private readonly HashSet<string> _seen = new();
        private readonly List<LoggerRegistrationEntry> _pending = new();
        private readonly object _lock = new();

        public void Add(string id, string? level, string resolvedLevel, string? service, string? environment)
        {
            lock (_lock)
            {
                if (_seen.Add(id))
                    _pending.Add(new(id, level, resolvedLevel, service, environment));
            }
        }

        public List<LoggerRegistrationEntry> Drain()
        {
            lock (_lock)
            {
                var batch = new List<LoggerRegistrationEntry>(_pending);
                _pending.Clear();
                return batch;
            }
        }

        public int PendingCount
        {
            get { lock (_lock) { return _pending.Count; } }
        }

        internal record LoggerRegistrationEntry(string Id, string? Level, string ResolvedLevel, string? Service, string? Environment);
    }
}
