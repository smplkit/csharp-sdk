using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using GenLogging = Smplkit.Internal.Generated.Logging;

namespace Smplkit.Logging;

/// <summary>
/// Client for the smplkit Logging service. Provides management CRUD for loggers
/// and log groups, plus runtime level control via <see cref="StartAsync"/>.
/// </summary>
public sealed class LoggingClient
{
    private readonly GenLogging.LoggingClient _genClient;
    private readonly string _apiKey;
    private readonly Func<SharedWebSocket> _ensureWs;
    private readonly SmplClient? _parent;
    private volatile bool _started;
    private SharedWebSocket? _wsManager;
    private readonly List<Action<LoggerChangeEvent>> _globalListeners = new();
    private readonly Dictionary<string, List<Action<LoggerChangeEvent>>> _scopedListeners = new();
    private readonly object _listenerLock = new();

    /// <summary>
    /// Initializes a new instance of <see cref="LoggingClient"/>.
    /// </summary>
    internal LoggingClient(GeneratedClientFactory clients, string apiKey, Func<SharedWebSocket> ensureWs, SmplClient? parent = null)
    {
        _genClient = clients.Logging;
        _apiKey = apiKey;
        _ensureWs = ensureWs;
        _parent = parent;
    }

    // ------------------------------------------------------------------
    // Management: Logger CRUD
    // ------------------------------------------------------------------

    /// <summary>
    /// Create an unsaved logger. Call <see cref="Logger.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="key">The logger key.</param>
    /// <param name="name">Display name. Auto-generated from key if null.</param>
    /// <param name="managed">Whether this logger is managed.</param>
    /// <returns>An unsaved <see cref="Logger"/>.</returns>
    public Logger New(string key, string? name = null, bool managed = false)
    {
        return new Logger(
            client: this,
            id: null,
            key: key,
            name: name ?? Helpers.KeyToDisplayName(key),
            level: null,
            group: null,
            managed: managed,
            sources: new List<Dictionary<string, object?>>(),
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>
    /// Fetches a logger by its human-readable key.
    /// </summary>
    /// <param name="key">The logger key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Logger"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching logger exists.</exception>
    public async Task<Logger> GetAsync(string key, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_loggersAsync(filterkey: key, cancellationToken: ct)).ConfigureAwait(false);
        if (response.Data is null || response.Data.Count == 0)
            throw new SmplNotFoundException($"Logger with key '{key}' not found");
        return MapLoggerResource(response.Data[0])
            ?? throw new SmplNotFoundException($"Logger with key '{key}' not found");
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
    /// Deletes a logger by its human-readable key.
    /// </summary>
    /// <param name="key">The logger key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching logger exists.</exception>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var logger = await GetAsync(key, ct).ConfigureAwait(false);
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_loggerAsync(Guid.Parse(logger.Id!), ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a logger (create or update).</summary>
    internal async Task<Logger> SaveLoggerInternalAsync(Logger logger, CancellationToken ct = default)
    {
        var body = BuildLoggerRequestBody(logger);
        if (logger.Id is null)
        {
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Create_loggerAsync(body, ct)).ConfigureAwait(false);
            return MapLoggerResource(response.Data)
                ?? throw new SmplValidationException("Failed to create logger");
        }
        else
        {
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Update_loggerAsync(Guid.Parse(logger.Id), body, ct)).ConfigureAwait(false);
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
    /// <param name="key">The group key.</param>
    /// <param name="name">Display name. Auto-generated from key if null.</param>
    /// <param name="group">Optional parent group UUID.</param>
    /// <returns>An unsaved <see cref="LogGroup"/>.</returns>
    public LogGroup NewGroup(string key, string? name = null, string? group = null)
    {
        return new LogGroup(
            client: this,
            id: null,
            key: key,
            name: name ?? Helpers.KeyToDisplayName(key),
            level: null,
            group: group,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>
    /// Fetches a log group by its human-readable key.
    /// </summary>
    /// <param name="key">The group key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="LogGroup"/>.</returns>
    /// <exception cref="SmplNotFoundException">If no matching group exists.</exception>
    public async Task<LogGroup> GetGroupAsync(string key, CancellationToken ct = default)
    {
        // LogGroup list endpoint doesn't support filterkey, so we list all and filter locally
        var all = await ListGroupsAsync(ct).ConfigureAwait(false);
        var match = all.FirstOrDefault(g => g.Key == key);
        return match ?? throw new SmplNotFoundException($"LogGroup with key '{key}' not found");
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
    /// Deletes a log group by its human-readable key.
    /// </summary>
    /// <param name="key">The group key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching group exists.</exception>
    public async Task DeleteGroupAsync(string key, CancellationToken ct = default)
    {
        var group = await GetGroupAsync(key, ct).ConfigureAwait(false);
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_log_groupAsync(Guid.Parse(group.Id!), ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a log group (create or update).</summary>
    internal async Task<LogGroup> SaveLogGroupInternalAsync(LogGroup logGroup, CancellationToken ct = default)
    {
        var body = BuildLogGroupRequestBody(logGroup);
        if (logGroup.Id is null)
        {
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Create_log_groupAsync(body, ct)).ConfigureAwait(false);
            return MapLogGroupResource(response.Data)
                ?? throw new SmplValidationException("Failed to create log group");
        }
        else
        {
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Update_log_groupAsync(Guid.Parse(logGroup.Id), body, ct)).ConfigureAwait(false);
            return MapLogGroupResource(response.Data)
                ?? throw new SmplValidationException("Failed to update log group");
        }
    }

    // ------------------------------------------------------------------
    // Runtime: StartAsync
    // ------------------------------------------------------------------

    /// <summary>
    /// Explicitly start the logging runtime. Idempotent. Fetches all loggers/groups,
    /// opens the shared WebSocket, and applies levels.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;

        await ListAsync(ct).ConfigureAwait(false);
        await ListGroupsAsync(ct).ConfigureAwait(false);

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
    /// Register a change listener scoped to a specific logger key.
    /// </summary>
    /// <param name="loggerKey">The logger key to listen for.</param>
    /// <param name="callback">Called with a <see cref="LoggerChangeEvent"/> when this logger changes.</param>
    public void OnChange(string loggerKey, Action<LoggerChangeEvent> callback)
    {
        lock (_listenerLock)
        {
            if (!_scopedListeners.TryGetValue(loggerKey, out var list))
            {
                list = new List<Action<LoggerChangeEvent>>();
                _scopedListeners[loggerKey] = list;
            }
            list.Add(callback);
        }
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Stop the logging runtime — unregister WebSocket listeners.
    /// </summary>
    internal void Close()
    {
        if (_wsManager is not null)
        {
            _wsManager.Off("logger_changed", HandleLoggerChanged);
            _wsManager = null;
        }
        _started = false;
    }

    // ------------------------------------------------------------------
    // Internal: event handlers
    // ------------------------------------------------------------------

    private void HandleLoggerChanged(Dictionary<string, object?> data)
    {
        var loggerKey = data.TryGetValue("key", out var k) ? k as string : null;
        if (loggerKey is null) return;

        LogLevel? newLevel = null;
        if (data.TryGetValue("level", out var levelObj) && levelObj is string levelStr)
        {
            try { newLevel = LogLevelExtensions.ParseLogLevel(levelStr); }
            catch { /* Unknown level */ }
        }

        var evt = new LoggerChangeEvent(loggerKey, newLevel, "websocket");
        FireListeners(loggerKey, evt);
    }

    private void FireListeners(string loggerKey, LoggerChangeEvent evt)
    {
        List<Action<LoggerChangeEvent>> globalCopy;
        List<Action<LoggerChangeEvent>>? scopedCopy = null;

        lock (_listenerLock)
        {
            globalCopy = new List<Action<LoggerChangeEvent>>(_globalListeners);
            if (_scopedListeners.TryGetValue(loggerKey, out var scoped))
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
            key: attrs.Key ?? string.Empty,
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
            key: attrs.Key ?? string.Empty,
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
                Attributes = new GenLogging.Logger
                {
                    Key = logger.Key,
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
                Attributes = new GenLogging.LogGroup
                {
                    Key = logGroup.Key,
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
