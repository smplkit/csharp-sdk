using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using Smplkit.Logging.Adapters;
using GenLogging = Smplkit.Internal.Generated.Logging;

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
    // Management: Logger CRUD
    // ------------------------------------------------------------------

    /// <summary>
    /// Create an unsaved logger. Call <see cref="Logger.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The logger identifier (slug).</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="managed">Whether this logger is managed.</param>
    /// <returns>An unsaved <see cref="Logger"/>.</returns>
    public Logger New(string id, string? name = null, bool managed = false)
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

    /// <summary>
    /// Fetches a logger by its identifier.
    /// </summary>
    /// <param name="id">The logger identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Logger"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching logger exists.</exception>
    public async Task<Logger> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_loggerAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);
        return MapLoggerResource(response.Data)
            ?? throw new SmplNotFoundException($"Logger with id '{id}' not found");
    }

    /// <summary>
    /// Lists all loggers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="Logger"/> objects.</returns>
    public async Task<List<Logger>> ListAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_loggersAsync(cancellationToken: ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<Logger>();
        return response.Data.Select(r => MapLoggerResource(r)!).Where(l => l is not null).ToList();
    }

    /// <summary>
    /// Deletes a logger by its identifier.
    /// </summary>
    /// <param name="id">The logger identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching logger exists.</exception>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_loggerAsync(id, ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a logger (create or update).</summary>
    internal async Task<Logger> SaveLoggerInternalAsync(Logger logger, CancellationToken ct = default)
    {
        var body = BuildLoggerRequestBody(logger);
        if (logger.CreatedAt is null)
        {
            // Create (unsaved logger — CreatedAt is null until first server round-trip)
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Create_loggerAsync(body, ct)).ConfigureAwait(false);
            return MapLoggerResource(response.Data)
                ?? throw new SmplValidationException("Failed to create logger");
        }
        else
        {
            var loggerId = logger.Id ?? throw new SmplValidationException("Cannot update a logger without an id");
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Update_loggerAsync(loggerId, body, ct)).ConfigureAwait(false);
            return MapLoggerResource(response.Data)
                ?? throw new SmplValidationException("Failed to update logger");
        }
    }

    // ------------------------------------------------------------------
    // Management: LogGroup CRUD
    // ------------------------------------------------------------------

    /// <summary>
    /// Create an unsaved log group. Call <see cref="LogGroup.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The group identifier (slug).</param>
    /// <param name="name">Display name. Auto-generated from id if null.</param>
    /// <param name="group">Optional parent group identifier.</param>
    /// <returns>An unsaved <see cref="LogGroup"/>.</returns>
    public LogGroup NewGroup(string id, string? name = null, string? group = null)
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

    /// <summary>
    /// Fetches a log group by its identifier.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="LogGroup"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching group exists.</exception>
    public async Task<LogGroup> GetGroupAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_log_groupAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);
        return MapLogGroupResource(response.Data)
            ?? throw new SmplNotFoundException($"LogGroup with id '{id}' not found");
    }

    /// <summary>
    /// Lists all log groups.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of <see cref="LogGroup"/> objects.</returns>
    public async Task<List<LogGroup>> ListGroupsAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_log_groupsAsync(ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<LogGroup>();
        return response.Data.Select(r => MapLogGroupResource(r)!).Where(g => g is not null).ToList();
    }

    /// <summary>
    /// Deletes a log group by its identifier.
    /// </summary>
    /// <param name="id">The group identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching group exists.</exception>
    public async Task DeleteGroupAsync(string id, CancellationToken ct = default)
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

        // 2. Discover existing loggers from each adapter
        DiscoverAll();

        // 3. Install hooks on each adapter
        InstallAllHooks();

        // 4. Fetch all loggers and groups from the server
        var loggers = await ListAsync(ct).ConfigureAwait(false);
        await ListGroupsAsync(ct).ConfigureAwait(false);

        // 5. Apply levels from server-managed loggers to adapters
        ApplyLevels(loggers);

        // 6. Wire WebSocket
        _wsManager = _ensureWs();
        _wsManager.On("logger_changed", HandleLoggerChanged);
        _started = true;
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

        if (_wsManager is not null)
        {
            _wsManager.Off("logger_changed", HandleLoggerChanged);
            _wsManager = null;
        }
        _started = false;
    }

    // ------------------------------------------------------------------
    // Internal: adapter helpers
    // ------------------------------------------------------------------

    private void DiscoverAll()
    {
        var totalDiscovered = 0;
        foreach (var adapter in _adapters)
        {
            try
            {
                var discovered = adapter.Discover();
                totalDiscovered += discovered.Count;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[smplkit] Adapter {0} discovery failed: {1}", adapter.Name, ex.Message);
            }
        }
        if (totalDiscovered > 0)
            _metrics?.Record("logging.loggers_discovered", value: totalDiscovered, unit: "loggers");
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
        // When an adapter detects a new logger, fire change listeners
        var evt = new LoggerChangeEvent(loggerName, level, "adapter");
        FireListeners(loggerName, evt);
    }

    // ------------------------------------------------------------------
    // Internal: event handlers
    // ------------------------------------------------------------------

    private void HandleLoggerChanged(Dictionary<string, object?> data)
    {
        var loggerId = data.TryGetValue("id", out var k) ? k as string
            : data.TryGetValue("key", out var k2) ? k2 as string : null;
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
            group: attrs.Group,
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

    private static GenLogging.Response_Logger_ BuildLoggerRequestBody(Logger logger) =>
        new()
        {
            Data = new GenLogging.Resource_Logger_
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

    private static GenLogging.Response_LogGroup_ BuildLogGroupRequestBody(LogGroup logGroup) =>
        new()
        {
            Data = new GenLogging.Resource_LogGroup_
            {
                Type = "log_group",
                Id = logGroup.Id,
                Attributes = new GenLogging.LogGroup
                {
                    Name = logGroup.Name,
                    Level = logGroup.Level?.ToWireString(),
                    Group = logGroup.Group,
                    Environments = BuildEnvironmentsPayload(logGroup.Environments),
                },
            }
        };

    private static object? BuildEnvironmentsPayload(Dictionary<string, Dictionary<string, object?>> environments)
    {
        if (environments.Count == 0) return null;
        return environments;
    }
}
