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
/// level control via <see cref="StartAsync"/>.
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

    /// <summary>
    /// Initializes a new instance of <see cref="LoggingClient"/>.
    /// </summary>
    private readonly MetricsReporter? _metrics;
    private readonly LoggerRegistrationBuffer _loggerBuffer = new();
    private Timer? _loggerFlushTimer;

    internal LoggingClient(GeneratedClientFactory clients, string apiKey, Func<SharedWebSocket> ensureWs, SmplClient? parent = null, MetricsReporter? metrics = null)
    {
        _genClient = clients.Logging;
        _apiKey = apiKey;
        _ensureWs = ensureWs;
        _parent = parent;
        _metrics = metrics;
        Management = new LoggingManagement(this);
    }

    /// <summary>
    /// Provides management (CRUD) operations for loggers and log groups: create, get, list, and delete.
    /// </summary>
    public LoggingManagement Management { get; }

    // ------------------------------------------------------------------
    // Adapter registration
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers a logging adapter. Must be called before <see cref="StartAsync"/>.
    /// When called, only explicitly registered adapters are used.
    /// </summary>
    /// <param name="adapter">The adapter to register.</param>
    /// <exception cref="InvalidOperationException">If called after <see cref="StartAsync"/>.</exception>
    public void RegisterAdapter(ILoggingAdapter adapter)
    {
        if (_started)
            throw new InvalidOperationException("Cannot register adapters after StartAsync()");
        _explicitAdapters = true;
        _adapters.Add(adapter);
    }

    // ------------------------------------------------------------------
    // Management: Logger CRUD (internal — public surface is via Management)
    // ------------------------------------------------------------------

    internal Logger New(string id, string? name = null, bool managed = false)
    {
        return new Logger(
            client: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            level: null,
            group: null,
            managed: managed,
            sources: new List<Dictionary<string, object?>>(),
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    internal async Task<Logger> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_loggerAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);
        return MapLoggerResource(response.Data)
            ?? throw new SmplNotFoundException($"Logger with id '{id}' not found");
    }

    internal async Task<List<Logger>> ListAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_loggersAsync(cancellationToken: ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<Logger>();
        return response.Data.Select(r => MapLoggerResource(r)!).Where(l => l is not null).ToList();
    }

    internal async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_loggerAsync(id, ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a logger (create or update). PUT has upsert semantics.</summary>
    internal async Task<Logger> SaveLoggerInternalAsync(Logger logger, CancellationToken ct = default)
    {
        var loggerId = logger.Id ?? throw new SmplValidationException("Cannot save a logger without an id");

        var body = BuildLoggerRequestBody(logger);
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Update_loggerAsync(loggerId, body, ct)).ConfigureAwait(false);
        return MapLoggerResource(response.Data)
            ?? throw new SmplValidationException("Failed to save logger");
    }

    // ------------------------------------------------------------------
    // Management: LogGroup CRUD (internal — public surface is via Management)
    // ------------------------------------------------------------------

    internal LogGroup NewGroup(string id, string? name = null, string? group = null)
    {
        return new LogGroup(
            client: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            level: null,
            group: group,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    internal async Task<LogGroup> GetGroupAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_log_groupAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);
        return MapLogGroupResource(response.Data)
            ?? throw new SmplNotFoundException($"LogGroup with id '{id}' not found");
    }

    internal async Task<List<LogGroup>> ListGroupsAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_log_groupsAsync(ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<LogGroup>();
        return response.Data.Select(r => MapLogGroupResource(r)!).Where(g => g is not null).ToList();
    }

    internal async Task DeleteGroupAsync(string id, CancellationToken ct = default)
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_log_groupAsync(id, ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a log group (create or update).</summary>
    internal async Task<LogGroup> SaveLogGroupInternalAsync(LogGroup logGroup, CancellationToken ct = default)
    {
        var body = BuildLogGroupRequestBody(logGroup);
        if (logGroup.CreatedAt is null)
        {
            // Create (unsaved log group — CreatedAt is null until first server round-trip)
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Create_log_groupAsync(body, ct)).ConfigureAwait(false);
            return MapLogGroupResource(response.Data)
                ?? throw new SmplValidationException("Failed to create log group");
        }
        else
        {
            var groupId = logGroup.Id ?? throw new SmplValidationException("Cannot update a log group without an id");
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Update_log_groupAsync(groupId, body, ct)).ConfigureAwait(false);
            return MapLogGroupResource(response.Data)
                ?? throw new SmplValidationException("Failed to update log group");
        }
    }

    // ------------------------------------------------------------------
    // Runtime: StartAsync
    // ------------------------------------------------------------------

    /// <summary>
    /// Starts dynamic log level control. Applies server-defined levels to registered
    /// adapters and subscribes to real-time level updates. Idempotent.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartAsync(CancellationToken ct = default)
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
        var loggers = await ListAsync(ct).ConfigureAwait(false);
        await ListGroupsAsync(ct).ConfigureAwait(false);

        // 6. Apply levels from server-managed loggers to adapters
        ApplyLevels(loggers);

        // 7. Wire WebSocket
        DebugLog.Log("registration", "registering logger_changed, logger_deleted, group_changed, group_deleted handlers");
        _wsManager = _ensureWs();
        _wsManager.On("logger_changed", HandleLoggerChanged);
        _wsManager.On("logger_deleted", HandleLoggerChanged);
        _wsManager.On("group_changed", HandleGroupChanged);
        _wsManager.On("group_deleted", HandleGroupChanged);
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
            _wsManager.Off("logger_deleted", HandleLoggerChanged);
            _wsManager.Off("group_changed", HandleGroupChanged);
            _wsManager.Off("group_deleted", HandleGroupChanged);
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
            Task.Run(() => FlushLoggerBufferAsync());

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
        // FlushLoggerBufferAsync swallows all exceptions internally — safe to call synchronously
        FlushLoggerBufferAsync().GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------
    // Internal: event handlers
    // ------------------------------------------------------------------

    private void HandleLoggerChanged(Dictionary<string, object?> data)
    {
        var loggerId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"logger event received, id={loggerId ?? "<unknown>"}");
        if (loggerId is null) return;

        LogLevel? newLevel = null;
        if (data.TryGetValue("level", out var levelObj) && levelObj is string levelStr)
        {
            try { newLevel = LogLevelExtensions.ParseLogLevel(levelStr); }
            catch { /* Unknown level */ }
        }

        var evt = new LoggerChangeEvent(loggerId, newLevel, "websocket");
        FireListeners(loggerId, evt);
    }

    private void HandleGroupChanged(Dictionary<string, object?> data)
    {
        var groupId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"group event received, id={groupId ?? "<unknown>"}");
        // A group change may affect any logger in that group — re-fetch and re-apply.
        if (!_started) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var loggers = await ListAsync().ConfigureAwait(false);
                ApplyLevels(loggers);
            }
            catch { /* Ignore refresh errors */ }
        });
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
    // Helpers: model mapping
    // ------------------------------------------------------------------

    private Logger? MapLoggerResource(GenLogging.LoggerResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        LogLevel? level = null;
        if (attrs.Level is not null)
        {
            try { level = LogLevelExtensions.ParseLogLevel(attrs.Level); }
            catch { /* Unknown level */ }
        }

        var sources = new List<Dictionary<string, object?>>();
        if (attrs.Sources is not null)
        {
            foreach (var s in attrs.Sources)
            {
                if (s is Dictionary<string, object?> dict)
                    sources.Add(dict);
                else if (s is JsonElement je)
                    sources.Add(NormalizeJsonToDict(je));
            }
        }

        var environments = NormalizeEnvironments(attrs.Environments);

        return new Logger(
            client: this,
            id: resource.Id ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            level: level,
            group: attrs.Group,
            managed: attrs.Managed ?? false,
            sources: sources,
            environments: environments,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private LogGroup? MapLogGroupResource(GenLogging.LogGroupResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        LogLevel? level = null;
        if (attrs.Level is not null)
        {
            try { level = LogLevelExtensions.ParseLogLevel(attrs.Level); }
            catch { /* Unknown level */ }
        }

        var environments = NormalizeEnvironments(attrs.Environments);

        return new LogGroup(
            client: this,
            id: resource.Id ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            level: level,
            group: attrs.Parent_id,
            environments: environments,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static Dictionary<string, Dictionary<string, object?>> NormalizeEnvironments(object? environments)
    {
        if (environments is null) return new Dictionary<string, Dictionary<string, object?>>();
        if (environments is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var prop in je.EnumerateObject())
            {
                result[prop.Name] = NormalizeJsonToDict(prop.Value);
            }
            return result;
        }
        return new Dictionary<string, Dictionary<string, object?>>();
    }

    private static Dictionary<string, object?> NormalizeJsonToDict(JsonElement je)
    {
        if (je.ValueKind != JsonValueKind.Object) return new Dictionary<string, object?>();
        var result = new Dictionary<string, object?>();
        foreach (var prop in je.EnumerateObject())
            result[prop.Name] = Config.Resolver.Normalize(prop.Value);
        return result;
    }

    // ------------------------------------------------------------------
    // Helpers: request body building
    // ------------------------------------------------------------------

    private static GenLogging.LoggerResponse BuildLoggerRequestBody(Logger logger) =>
        new()
        {
            Data = new GenLogging.LoggerResource
            {
                Type = "logger",
                Id = logger.Id,
                Attributes = new GenLogging.Logger
                {
                    Name = logger.Name,
                    Level = logger.Level?.ToWireString(),
                    Group = logger.Group,
                    Managed = logger.Managed,
                    Environments = BuildEnvironmentsPayload(logger.Environments),
                },
            }
        };

    private static GenLogging.LogGroupResponse BuildLogGroupRequestBody(LogGroup logGroup) =>
        new()
        {
            Data = new GenLogging.LogGroupResource
            {
                Type = "log_group",
                Id = logGroup.Id,
                Attributes = new GenLogging.LogGroup
                {
                    Name = logGroup.Name,
                    Level = logGroup.Level?.ToWireString(),
                    Parent_id = logGroup.Group,
                    Environments = BuildEnvironmentsPayload(logGroup.Environments),
                },
            }
        };

    private static object? BuildEnvironmentsPayload(Dictionary<string, Dictionary<string, object?>> environments)
    {
        if (environments.Count == 0) return null;
        return environments;
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
