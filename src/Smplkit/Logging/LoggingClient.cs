using System.Text.Json;
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
    private bool _explicitAdapters;
    private readonly List<Action<LoggerChangeEvent>> _globalListeners = new();
    private readonly Dictionary<string, List<Action<LoggerChangeEvent>>> _scopedListeners = new();
    private readonly object _listenerLock = new();
    // Cache of last-known logger levels for diff-based listener firing
    private readonly Dictionary<string, LogLevel?> _loggerLevelCache = new();
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
    /// When called, only explicitly registered adapters are used.
    /// </summary>
    /// <param name="adapter">The adapter to register.</param>
    /// <exception cref="InvalidOperationException">If called after <see cref="InstallAsync"/>.</exception>
    public void RegisterAdapter(ILoggingAdapter adapter)
    {
        if (_started)
            throw new InvalidOperationException("Cannot register adapters after InstallAsync()");
        _explicitAdapters = true;
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

        // 1. Auto-load adapters if none registered explicitly
        if (!_explicitAdapters)
            AutoLoadAdapters();

        // 2. Discover existing loggers from each adapter and add to buffer
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

        // 5. Fetch all loggers and groups from the server
        var loggers = _parent?.Manage.Loggers is { } mgmtL ? await mgmtL.ListAsync(ct).ConfigureAwait(false) : new List<Logger>();
        if (_parent?.Manage.LogGroups is { } mgmtG) { await mgmtG.ListAsync(ct).ConfigureAwait(false); }

        // 6. Apply levels from server-managed loggers to adapters, seed level cache
        ApplyLevels(loggers);
        lock (_loggerCacheLock)
        {
            foreach (var l in loggers)
                if (l.Id is not null)
                    _loggerLevelCache[l.Id] = l.Level;
        }

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

    private void AutoLoadAdapters()
    {
        var builtins = new[]
        {
            ("Smplkit.Logging.Adapters.MicrosoftLoggingAdapter", "Microsoft.Extensions.Logging"),
            ("Smplkit.Logging.Adapters.SerilogAdapter", "Serilog"),
        };
        foreach (var (adapterType, probeAssembly) in builtins)
        {
            var adapter = TryLoadAdapter(adapterType, probeAssembly);
            if (adapter != null)
                _adapters.Add(adapter);
        }
    }

    internal static ILoggingAdapter? TryLoadAdapter(string adapterTypeName, string probeAssembly)
    {
        try
        {
            System.Reflection.Assembly.Load(probeAssembly);
            var type = Type.GetType(adapterTypeName + ", Smplkit");
            if (type != null)
                return (ILoggingAdapter)Activator.CreateInstance(type)!;
            return null;
        }
        catch
        {
            // Framework not available — skip
            return null;
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

    private void ApplyLevels(List<Logger> loggers)
    {
        if (_adapters.Count == 0) return;

        foreach (var logger in loggers)
        {
            if (logger.Level is null) continue;

            foreach (var adapter in _adapters)
            {
                try { adapter.ApplyLevel(logger.Id!, logger.Level.Value); }
                catch { /* Adapter failure is non-fatal */ }
            }

            _metrics?.Record("logging.level_changes", unit: "changes",
                dimensions: new Dictionary<string, string> { ["logger"] = logger.Id! });
        }
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
            // Scoped fetch: GET just the single changed logger
            if (_parent?.Manage.Loggers is not { } mgmtL) return;
            var logger = await mgmtL.GetAsync(loggerId).ConfigureAwait(false);
            ApplyLevels(new List<Logger> { logger });

            // Only fire listeners if level changed
            LogLevel? prevLevel;
            lock (_loggerCacheLock)
            {
                _loggerLevelCache.TryGetValue(loggerId, out prevLevel);
                _loggerLevelCache[loggerId] = logger.Level;
            }

            if (!Equals(prevLevel, logger.Level))
            {
                var evt = new LoggerChangeEvent(loggerId, logger.Level, "websocket");
                FireListeners(loggerId, evt);
            }
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

        lock (_loggerCacheLock)
        {
            _loggerLevelCache.Remove(loggerId);
        }

        var evt = new LoggerChangeEvent(loggerId, null, "websocket", Deleted: true);
        FireListeners(loggerId, evt);
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
            // Scoped fetch: GET just the single changed group
            if (_parent?.Manage.LogGroups is not { } mgmtG) return;
            var group = await mgmtG.GetAsync(groupId).ConfigureAwait(false);
            // A group level change affects all loggers in that group — re-apply all
            var loggers = _parent?.Manage.Loggers is { } mgmtLn ? await mgmtLn.ListAsync().ConfigureAwait(false) : new List<Logger>();
            ApplyLevels(loggers);

            // Diff and fire for loggers whose effective level changed
            var changedLoggers = new List<(string Id, LogLevel? Level)>();
            lock (_loggerCacheLock)
            {
                foreach (var logger in loggers)
                {
                    if (logger.Id is null) continue;
                    _loggerLevelCache.TryGetValue(logger.Id, out var prev);
                    if (!Equals(prev, logger.Level))
                    {
                        _loggerLevelCache[logger.Id] = logger.Level;
                        changedLoggers.Add((logger.Id, logger.Level));
                    }
                }
            }
            foreach (var (id, level) in changedLoggers)
            {
                var evt = new LoggerChangeEvent(id, level, "websocket");
                FireListeners(id, evt);
            }
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

        // Fire a logger change event for the group using loggerId = groupId (group-level event)
        var evt = new LoggerChangeEvent(groupId, null, "websocket", Deleted: true);
        FireListeners(groupId, evt);
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
            // Full refetch of both loggers and groups
            var loggers = _parent?.Manage.Loggers is { } mgmtLn ? await mgmtLn.ListAsync().ConfigureAwait(false) : new List<Logger>();
            if (_parent?.Manage.LogGroups is { } mgmtGn) { await mgmtGn.ListAsync().ConfigureAwait(false); }
            ApplyLevels(loggers);

            // Diff and fire per-key listeners for changed loggers
            var changedLoggers = new List<(string Id, LogLevel? Level)>();
            lock (_loggerCacheLock)
            {
                var allIds = new HashSet<string>(_loggerLevelCache.Keys);
                foreach (var l in loggers)
                    if (l.Id is not null) allIds.Add(l.Id);

                foreach (var id in allIds)
                {
                    _loggerLevelCache.TryGetValue(id, out var prev);
                    var current = loggers.FirstOrDefault(l => l.Id == id);
                    var newLevel = current?.Level;
                    if (!Equals(prev, newLevel))
                    {
                        if (current is not null)
                            _loggerLevelCache[id] = newLevel;
                        else
                            _loggerLevelCache.Remove(id);
                        changedLoggers.Add((id, newLevel));
                    }
                }
            }

            if (changedLoggers.Count == 0) return;

            // Fire global listener exactly once
            var globalEvt = new LoggerChangeEvent(changedLoggers[0].Id, changedLoggers[0].Level, "websocket");
            FireGlobalListeners(globalEvt);

            // Fire per-key listeners for each changed logger
            foreach (var (changedId, level) in changedLoggers)
            {
                var evt = new LoggerChangeEvent(changedId, level, "websocket");
                FireScopedListeners(changedId, evt);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Loggers bulk refresh failed: {0}", ex.Message);
            DebugLog.Log("websocket", $"Loggers bulk refresh failed: {ex}");
        }
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

    private void FireGlobalListeners(LoggerChangeEvent evt)
    {
        List<Action<LoggerChangeEvent>> globalCopy;
        lock (_listenerLock)
        {
            globalCopy = new List<Action<LoggerChangeEvent>>(_globalListeners);
        }
        foreach (var cb in globalCopy)
        {
            try { cb(evt); }
            catch { /* Ignore listener exceptions */ }
        }
    }

    private void FireScopedListeners(string loggerId, LoggerChangeEvent evt)
    {
        List<Action<LoggerChangeEvent>>? scopedCopy;
        lock (_listenerLock)
        {
            if (!_scopedListeners.TryGetValue(loggerId, out var scoped)) return;
            scopedCopy = new List<Action<LoggerChangeEvent>>(scoped);
        }
        foreach (var cb in scopedCopy)
        {
            try { cb(evt); }
            catch { /* Ignore listener exceptions */ }
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
