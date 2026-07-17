// The Smpl Logging client — one unified LoggingClient.
//
// Smpl Logging has two surfaces on a single client, mirroring how the config,
// flags, audit, and jobs clients expose their full surface from one class:
//
// * CRUD surface — works without Install().
//   Two sub-clients:
//
//   * client.Logging.Loggers — logger CRUD + discovery: New / List /
//     Get / Delete plus Register / Flush / PendingCount.
//   * client.Logging.LogGroups — log-group CRUD: New / List /
//     Get / Delete.
//
//   The fused client owns the logger-discovery buffer directly; the Loggers
//   sub-client shares that same buffer so discovery and explicit registration
//   drain through one queue.
//
// * Live surface — directly on the client. RegisterAdapter is a
//   PRE-install configuration call (allowed before Install()).
//   Install() opens the live connection (wires into the app's logging
//   framework via the registered adapter, discovers loggers, fetches +
//   applies levels, opens the shared
//   WebSocket). OnChange / Refresh require Install() first;
//   calling them earlier raises NotInstalledException.
//
// The client supports two construction shapes:
//
// * Wired into SmplClient — borrows the parent's logging
//   transport for both runtime fetch and CRUD and the parent's shared WebSocket
//   for the live channel. This is the common path.
// * Standalone — new LoggingClient(apiKey: ..., baseDomain: ..., ...) builds and
//   owns its own logging transport and an app transport (the WebSocket gateway
//   lives on the app service), and on Install() opens and owns its own
//   WebSocket. Dispose() tears down only the owned transports and owned
//   WebSocket.

using Smplkit.Errors;
using Smplkit.Internal;
using Smplkit.Logging.Adapters;
using GenLogging = Smplkit.Internal.Generated.Logging;
using DebugLog = Smplkit.Internal.Debug;

namespace Smplkit.Logging;

/// <summary>
/// The Smpl Logging client.
/// </summary>
/// <remarks>
/// <para>One client exposes the full surface, reachable as <c>client.Logging</c>
/// (<see cref="Smplkit.SmplClient"/>) or constructed directly:</para>
/// <code>
/// using var logging = new LoggingClient(environment: "production", service: "my-svc");
/// await logging.Loggers.New("sqlalchemy.engine").SaveAsync();
/// await logging.InstallAsync();
/// </code>
/// <para>The CRUD surface (<see cref="Loggers"/> / <see cref="LogGroups"/>
/// sub-clients) works immediately. <see cref="RegisterAdapter"/> is a pre-install
/// configuration call. The live surface (<see cref="InstallAsync"/> /
/// <see cref="OnChange(Action{LoggerChangeEvent})"/> / <see cref="RefreshAsync"/>)
/// requires <see cref="InstallAsync"/> first; calling <c>OnChange</c> / <c>Refresh</c>
/// earlier raises <see cref="NotInstalledException"/>.</para>
/// <para>For serverless or short-lived processes, construct with
/// <c>streaming: false</c>: <see cref="InstallAsync"/> still hooks adapters and
/// applies levels once, <see cref="RefreshAsync"/> re-fetches on demand, and no
/// WebSocket, timer, or background work is ever created.</para>
/// </remarks>
public sealed class LoggingClient : IDisposable
{
    private const string NotInstalledMessage =
        "Smpl Logging live operations require InstallAsync() first — this opens a live " +
        "connection to your running service and hooks into your application's " +
        "logging framework. Call client.Logging.InstallAsync() before " +
        "OnChange()/RefreshAsync().";

    private readonly GenLogging.LoggingClient _genClient;
    private readonly SmplClient? _parent;
    private readonly MetricsReporter? _metrics;
    private readonly string? _environment;
    private readonly string? _service;

    // Live-updates mode: true (default) opens a WebSocket + periodic flush
    // timer on install; false keeps the client fully stateless — install
    // fetches and applies levels once, discovery flushes run inline, and
    // RefreshAsync re-fetches on demand.
    private readonly bool _streaming;

    // Standalone construction owns its transport + WebSocket; wired construction
    // borrows the parent's. _ensureWs returns the parent's shared WebSocket when
    // wired, else lazily builds and owns one.
    private readonly Func<SharedWebSocket>? _ensureWs;
    private readonly HttpClient? _ownedHttpClient;
    private readonly string? _appBaseUrl;
    private readonly string? _standaloneApiKey;
    private readonly bool _ownsTransport;
    private bool _ownsWs;

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

    private readonly LoggerRegistrationBuffer _loggerBuffer = new();
    private Timer? _loggerFlushTimer;

    // Exposed for tests to await fire-and-forget threshold-triggered flushes
    // and the websocket-handler async work (otherwise the lambda body
    // coverage races with process exit on CI).
    internal Task? _lastLoggerBufferFlushTask;
    internal Task? _lastLoggerChangedTask;
    internal Task? _lastGroupChangedTask;
    internal Task? _lastLoggersChangedTask;

    /// <summary>The <c>Loggers</c> sub-client — logger CRUD + discovery.</summary>
    public LoggersClient Loggers { get; }

    /// <summary>The <c>LogGroups</c> sub-client — log-group CRUD.</summary>
    public LogGroupsClient LogGroups { get; }

    /// <summary>
    /// Initializes a new standalone <see cref="LoggingClient"/>.
    /// </summary>
    /// <param name="apiKey">API key. When omitted, resolved from <c>SMPLKIT_API_KEY</c> or <c>~/.smplkit</c>.</param>
    /// <param name="environment">Deployment environment used to resolve runtime levels and
    /// to scope discovery declarations. When omitted, resolved from
    /// <c>SMPLKIT_ENVIRONMENT</c> or <c>~/.smplkit</c>. Optional.</param>
    /// <param name="service">Service name used to scope discovery declarations.
    /// When omitted, resolved from <c>SMPLKIT_SERVICE</c> or <c>~/.smplkit</c>. Optional.</param>
    /// <param name="profile">Named <c>~/.smplkit</c> profile section.</param>
    /// <param name="baseDomain">Base domain for API requests (default <c>smplkit.com</c>).</param>
    /// <param name="scheme">URL scheme (default <c>https</c>).</param>
    /// <param name="debug">Enable SDK debug logging.</param>
    /// <param name="telemetry">Enable anonymous SDK usage telemetry. Defaults to enabled.</param>
    /// <param name="extraHeaders">Extra headers attached to every request.</param>
    /// <param name="streaming">When <c>true</c> (the default),
    /// <see cref="InstallAsync"/> opens a WebSocket for push level updates and
    /// starts a periodic discovery flush. Set to <c>false</c> for serverless or
    /// short-lived processes: <see cref="InstallAsync"/> still hooks adapters and
    /// fetches and applies levels once, discovery flushes run inline, and
    /// <see cref="RefreshAsync"/> re-fetches on demand — no WebSocket, timer, or
    /// background work is ever created.</param>
    public LoggingClient(
        string? apiKey = null,
        string? environment = null,
        string? service = null,
        string? profile = null,
        string? baseDomain = null,
        string? scheme = null,
        bool? debug = null,
        bool? telemetry = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        bool streaming = true)
    {
        // Reuse the account-global config resolver (logging CRUD is not scoped
        // to an environment) and the shared per-service URL helper, so a
        // standalone logging client resolves credentials/base-domain — and the
        // runtime environment/service scope — from ~/.smplkit / env vars /
        // constructor args exactly like the top-level clients do.
        var resolved = ConfigResolver.ResolveAccountGlobal(new SmplClientOptions
        {
            ApiKey = apiKey,
            Environment = environment,
            Service = service,
            Profile = profile,
            BaseDomain = baseDomain,
            Scheme = scheme,
            Debug = debug,
            Telemetry = telemetry,
        });
        if (resolved.Debug)
            DebugLog.Enabled = true;

        _parent = null;
        _environment = string.IsNullOrEmpty(environment) ? NullIfEmpty(resolved.Environment) : environment;
        _service = string.IsNullOrEmpty(service) ? NullIfEmpty(resolved.Service) : service;
        _streaming = streaming;
        _ensureWs = null;

        _ownedHttpClient = new HttpClient();
        var clients = new GeneratedClientFactory(_ownedHttpClient, new SmplClientOptions
        {
            ApiKey = resolved.ApiKey,
            BaseDomain = resolved.BaseDomain,
            Scheme = resolved.Scheme,
            ExtraHeaders = extraHeaders is null ? null : new Dictionary<string, string>(extraHeaders),
        });
        _genClient = clients.Logging;
        // The WebSocket gateway lives on the app service (like flags); a
        // standalone client opens its own WebSocket against it on Install().
        _appBaseUrl = ConfigResolver.ServiceUrl(resolved.Scheme, "app", resolved.BaseDomain);
        _standaloneApiKey = resolved.ApiKey;
        _ownsTransport = true;
        // A standalone client owns its metrics reporter (disposed in Dispose()).
        // Telemetry is opt-in: on by default, disabled when telemetry is false
        // or SMPLKIT_TELEMETRY resolves to false.
        _metrics = resolved.Telemetry
            ? new MetricsReporter(_ownedHttpClient, _environment ?? string.Empty, _service ?? string.Empty, appBaseUrl: _appBaseUrl)
            : null;

        Loggers = new LoggersClient(_genClient, _loggerBuffer);
        LogGroups = new LogGroupsClient(_genClient);
    }

    /// <summary>
    /// Internal — wired by a top-level client so the logging surface shares one
    /// connection pool and the parent's shared WebSocket. The borrowed generated
    /// client and WebSocket are owned by the parent; this instance must not tear
    /// them down.
    /// </summary>
    internal LoggingClient(GeneratedClientFactory clients, string apiKey, Func<SharedWebSocket> ensureWs, SmplClient? parent = null, MetricsReporter? metrics = null, bool streaming = true)
    {
        _genClient = clients.Logging;
        _ensureWs = ensureWs;
        _parent = parent;
        _metrics = metrics;
        _environment = parent?.Environment;
        _service = parent?.Service;
        _ownsTransport = false;
        _streaming = streaming;

        Loggers = new LoggersClient(_genClient, _loggerBuffer);
        LogGroups = new LogGroupsClient(_genClient);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ------------------------------------------------------------------
    // Adapter registration (pre-install, ungated)
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers a logging adapter. Must be called before <see cref="InstallAsync"/>.
    /// </summary>
    /// <remarks>
    /// If called at least once, only explicitly registered adapters are used.
    /// This is a pre-install configuration call: it is intentionally NOT gated
    /// by <see cref="InstallAsync"/>.
    /// <para>For Microsoft.Extensions.Logging, prefer the
    /// <see cref="SmplkitLoggingBuilderExtensions.AddSmplkit"/> extension on
    /// <see cref="Microsoft.Extensions.Logging.ILoggingBuilder"/> — it constructs
    /// the adapter, registers it in DI as
    /// <see cref="Microsoft.Extensions.Logging.ILoggerProvider"/> +
    /// <see cref="Microsoft.Extensions.Options.IConfigureOptions{LoggerFilterOptions}"/> +
    /// <see cref="Microsoft.Extensions.Options.IOptionsChangeTokenSource{LoggerFilterOptions}"/>,
    /// and calls this method in one step.</para>
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
    // Live surface: Install (gate) + WebSocket helper
    // ------------------------------------------------------------------

    private void RequireInstalled()
    {
        if (!_started)
            throw new NotInstalledException(NotInstalledMessage);
    }

    /// <summary>Return the shared WebSocket — the parent's when wired, else our own.</summary>
    private SharedWebSocket EnsureWs()
    {
        if (_ensureWs is not null)
            return _ensureWs();
        if (_wsManager is null)
        {
            _wsManager = new SharedWebSocket(
                _standaloneApiKey!,
                metrics: _metrics,
                appBaseUrl: _appBaseUrl!);
            _wsManager.Start();
            _ownsWs = true;
        }
        return _wsManager;
    }

    /// <summary>
    /// Hook smplkit into the application's logging machinery.
    /// </summary>
    /// <remarks>
    /// Loads adapters, scans existing loggers, applies levels from the
    /// smplkit server, and wires WebSocket handlers for live updates. This
    /// IS the explicit consent gate — <see cref="OnChange(Action{LoggerChangeEvent})"/> /
    /// <see cref="RefreshAsync"/> require it first.
    /// <para>Idempotent — safe to call multiple times.</para>
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    public async Task InstallAsync(CancellationToken ct = default)
    {
        DebugLog.Log("lifecycle", "LoggingClient.InstallAsync() called");
        if (_started) return;

        // 1. Discover existing loggers from each adapter and add to buffer.
        //    Adapters must be wired in explicitly — for Microsoft.Extensions.Logging,
        //    via the AddSmplkit ILoggingBuilder extension; for Serilog, via
        //    SerilogAdapter.GetOrCreateSwitch(...) wired into LoggerConfiguration.
        DebugLog.Log("websocket", "logging runtime initializing");
        var discovered = DiscoverAll();
        DebugLog.Log("discovery", $"discovered {discovered.Count} loggers from adapters");
        foreach (var d in discovered)
            _loggerBuffer.Add(LoggersClient.NormalizeLoggerName(d.Name), null, d.Level.ToWireString(), _service, _environment);

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

        // Stateless mode (streaming: false): the one-time fetch + apply above is
        // the whole install — no WebSocket, no periodic flush timer. RefreshAsync
        // re-fetches on demand.
        if (!_streaming)
        {
            _started = true;
            DebugLog.Log("websocket", "logging runtime connected (streaming disabled)");
            return;
        }

        // 7. Wire WebSocket
        DebugLog.Log("registration", "registering logger_changed, logger_deleted, group_changed, group_deleted, loggers_changed handlers");
        _wsManager = EnsureWs();
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
    // Live surface: change listeners
    // ------------------------------------------------------------------

    /// <summary>
    /// Register a global change listener that fires when any logger changes.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="InstallAsync"/> first; raises
    /// <see cref="NotInstalledException"/> otherwise.
    /// </remarks>
    /// <param name="callback">Called with a <see cref="LoggerChangeEvent"/> on each change.</param>
    /// <exception cref="NotInstalledException">If called before <see cref="InstallAsync"/>.</exception>
    public void OnChange(Action<LoggerChangeEvent> callback)
    {
        RequireInstalled();
        lock (_listenerLock)
        {
            _globalListeners.Add(callback);
        }
    }

    /// <summary>
    /// Register a change listener scoped to a specific logger id.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="InstallAsync"/> first; raises
    /// <see cref="NotInstalledException"/> otherwise.
    /// </remarks>
    /// <param name="loggerId">The logger identifier to listen for.</param>
    /// <param name="callback">Called with a <see cref="LoggerChangeEvent"/> when this logger changes.</param>
    /// <exception cref="NotInstalledException">If called before <see cref="InstallAsync"/>.</exception>
    public void OnChange(string loggerId, Action<LoggerChangeEvent> callback)
    {
        RequireInstalled();
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

    /// <summary>
    /// Re-fetches managed loggers and log groups from the server and re-applies
    /// their levels onto every registered adapter. Fires change listeners with
    /// <c>Source = "manual"</c> for any logger whose effective level differs
    /// from the cached value. Errors propagate to the caller — the websocket
    /// path swallows them, but a user-initiated refresh does not.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="InstallAsync"/> first; raises
    /// <see cref="NotInstalledException"/> otherwise.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotInstalledException">If called before <see cref="InstallAsync"/>.</exception>
    public Task RefreshAsync(CancellationToken ct = default)
    {
        RequireInstalled();
        DebugLog.Log("resolution", "RefreshAsync() called, triggering full resolution pass");
        return RefetchAndApplyAsync("manual", ct);
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Release resources — only those this client owns.
    /// </summary>
    /// <remarks>
    /// Uninstalls the adapter hooks, unsubscribes from the WebSocket, and
    /// tears down the owned WebSocket (standalone install) and the owned
    /// logging + app HTTP transport (standalone construction). A wired
    /// client borrows the parent's transport and WebSocket and closes
    /// neither.
    /// </remarks>
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
            if (_ownsWs)
            {
                _wsManager.StopAsync().GetAwaiter().GetResult();
                _ownsWs = false;
            }
            _wsManager = null;
        }
        _started = false;
        DebugLog.Log("lifecycle", "LoggingClient closed");
    }

    /// <summary>
    /// Release HTTP resources — only when this client owns its transport.
    /// </summary>
    /// <remarks>
    /// A logging client wired by a top-level client shares that client's
    /// transport and WebSocket; the owning client's <c>Dispose()</c> handles
    /// teardown via <see cref="Close"/>.
    /// </remarks>
    public void Dispose()
    {
        Close();
        if (_ownsTransport)
        {
            _metrics?.Dispose();
            _ownedHttpClient?.Dispose();
        }
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
        var environment = _environment ?? string.Empty;
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
            entries[id] = new Smplkit.Internal.LevelEntry(l.Level, l.Group, l.EnvironmentsRaw);
        return entries;
    }

    private static Dictionary<string, Smplkit.Internal.LevelEntry> BuildResolutionEntries(Dictionary<string, LogGroup> source)
    {
        var entries = new Dictionary<string, Smplkit.Internal.LevelEntry>(source.Count);
        foreach (var (id, g) in source)
            entries[id] = new Smplkit.Internal.LevelEntry(g.Level, g.Group, g.EnvironmentsRaw);
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
        _loggerBuffer.Add(LoggersClient.NormalizeLoggerName(loggerName), null, smplLevel, _service, _environment);

        if (_loggerBuffer.PendingCount >= 50)
        {
            _lastLoggerBufferFlushTask = FlushLoggerBufferAsync();
            // Stateless mode: the threshold flush runs inline (best-effort,
            // errors swallowed) instead of floating in the background.
            if (!_streaming)
                _lastLoggerBufferFlushTask.GetAwaiter().GetResult();
        }

        // Still fire listeners for immediate in-process notification
        var evt = new LoggerChangeEvent(loggerName, level, "adapter");
        FireListeners(loggerName, evt);
    }

    internal async Task FlushLoggerBufferAsync(CancellationToken ct = default)
    {
        var request = LoggersClient.BuildLoggerBulkRequest(_loggerBuffer);
        if (request is null) return;

        try
        {
            await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Bulk_register_loggersAsync(request, ct)).ConfigureAwait(false);
            DebugLog.Log("registration", $"bulk-registered {request.Loggers.Count} logger(s)");
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
            var logger = await Loggers.GetAsync(loggerId).ConfigureAwait(false);

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
            var group = await LogGroups.GetAsync(groupId).ConfigureAwait(false);

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
    /// Walks <c>Loggers.ListAsync</c> page by page until the server returns
    /// fewer rows than requested. The runtime needs every managed logger to
    /// apply effective levels to adapters; customers who want pagination
    /// control go through <c>Loggers.ListAsync</c>.
    /// </summary>
    private Task<List<Logger>> FetchAllLoggersAsync(CancellationToken ct)
        => Smplkit.Internal.Helpers.FetchAllPagesAsync(
            (page, size, c) => Loggers.ListAsync(page, size, c), ct);

    /// <summary>
    /// Walks <c>LogGroups.ListAsync</c> page by page until the server returns a
    /// short page. The runtime feeds the result into the group cache so the
    /// resolver can walk group chains for inheritance.
    /// </summary>
    private Task<List<LogGroup>> FetchAllLogGroupsAsync(CancellationToken ct)
        => Smplkit.Internal.Helpers.FetchAllPagesAsync(
            (page, size, c) => LogGroups.ListAsync(page, size, c), ct);

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
}
