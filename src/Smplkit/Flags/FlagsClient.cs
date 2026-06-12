// The Smpl Flags client — one unified FlagsClient.
//
// Smpl Flags has two surfaces on a single client, mirroring how the config,
// audit, and jobs clients expose their full surface from one class:
//
// * CRUD surface — pure CRUD, no live connection:
//   NewBooleanFlag / NewStringFlag / NewNumberFlag / NewJsonFlag
//   constructors, GetAsync / ListAsync / DeleteAsync CRUD, and the
//   flag-declaration discovery buffer (Register / FlushAsync /
//   FlushSync / PendingCount). The client owns the discovery buffer
//   directly.
// * Live surface — lazily connects to your running service on first use:
//   the typed handle declarations (BooleanFlag / StringFlag / NumberFlag
//   / JsonFlag) whose .Get() evaluates against the cached definitions,
//   plus RefreshAsync / Stats / OnChange. The first live call
//   transparently flushes discovery, fetches all flag definitions into
//   the local cache, and opens the live-updates WebSocket — no explicit
//   install step.
//
// The client supports two construction shapes:
//
// * Wired into Smplkit.SmplClient — borrows the parent's flags transport for
//   both runtime fetch and CRUD, the parent's shared WebSocket for the live
//   channel, and client.Platform.Contexts (the shared context buffer) for
//   evaluation-context registration. This is the common path.
// * Standalone — new FlagsClient(apiKey: ..., environment: ..., ...) builds
//   and owns its own flags transport and a contexts buffer (against its own
//   app transport), and on first live use opens and owns its own WebSocket.
//   Dispose() tears down only the owned transports and owned WebSocket.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonLogic.Net;
using Newtonsoft.Json.Linq;
using Smplkit.Errors;
using Smplkit.Internal;
using DebugLog = Smplkit.Internal.Debug;
using GenApp = Smplkit.Internal.Generated.App;
using GenFlags = Smplkit.Internal.Generated.Flags;

namespace Smplkit.Flags;

/// <summary>
/// The Smpl Flags client.
/// </summary>
/// <remarks>
/// <para>One client exposes the full surface, reachable as <c>client.Flags</c>
/// (<see cref="Smplkit.SmplClient"/>) or constructed directly:</para>
/// <code>
/// using var flags = new FlagsClient(environment: "production");
/// var newFlag = flags.NewBooleanFlag("beta", defaultValue: false);
/// await newFlag.SaveAsync();
/// var beta = flags.BooleanFlag("beta", defaultValue: false);
/// if (beta.Get())
/// {
///     // ...
/// }
/// </code>
/// <para>The CRUD surface (<c>NewXxx</c> / <c>GetAsync</c> / <c>ListAsync</c> /
/// <c>DeleteAsync</c> and discovery) is pure CRUD. The live surface
/// (<c>BooleanFlag</c> / <c>StringFlag</c> / <c>NumberFlag</c> / <c>JsonFlag</c> /
/// <c>RefreshAsync</c> / <c>Stats</c> / <c>OnChange</c>) connects lazily on first
/// use — the first call flushes discovery, fetches all flag definitions into the
/// local cache, and opens the live-updates WebSocket. No explicit install step is
/// required.</para>
/// </remarks>
public sealed class FlagsClient : IDisposable
{
    private const int CacheMaxSize = 10_000;

    private readonly GenFlags.FlagsClient _genFlagsClient;
    private readonly GenApp.AppClient _genAppClient;
    private readonly string _apiKey;
    private readonly Func<SharedWebSocket> _ensureWs;
    private readonly SmplClient? _parent;
    private readonly MetricsReporter? _metrics;

    // Standalone-owned transports (null when wired into a parent client).
    private readonly HttpClient? _ownedHttpClient;
    private readonly string? _appBaseUrl;

    // Runtime state
    private string? _environment;
    private readonly string? _service;
    private readonly ConcurrentDictionary<string, Dictionary<string, object?>> _flagStore = new();
    internal volatile bool _connected;
    private readonly object _initLock = new();

    // Backoff retry state for start/EnsureConnected
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

    // Context-flush sizing (matches the discovery threshold).
    private const int ContextBatchFlushSize = 100;

    // Shared WebSocket — the parent's when wired, our own when standalone.
    private SharedWebSocket? _wsManager;
    private bool _ownsWs;

    // Flag auto-registration (the discovery buffer, owned directly).
    private readonly FlagRegistrationBuffer _flagBuffer = new();
    private Timer? _flagFlushTimer;

    // Exposed for tests to await the fire-and-forget context registration task
    internal Task? _initRegistrationTask;

    // Exposed for tests to await fire-and-forget threshold-triggered flushes
    // (otherwise the lambda body coverage races with process exit on CI).
    internal Task? _lastFlagBufferFlushTask;
    internal Task? _lastContextBufferFlushTask;

    /// <summary>
    /// Wired constructor used by <see cref="Smplkit.SmplClient"/>: borrows the parent's
    /// flags transport for both runtime fetch and CRUD, the parent's shared WebSocket for
    /// the live channel, and the shared context registration buffer
    /// (<c>client.Platform.Contexts</c>) as the evaluation-context registration seam.
    /// </summary>
    internal FlagsClient(GeneratedClientFactory clients, string apiKey, Func<SharedWebSocket> ensureWs, ContextRegistrationBuffer contextBuffer, SmplClient? parent = null, MetricsReporter? metrics = null)
    {
        _genFlagsClient = clients.Flags;
        _genAppClient = clients.App;
        _apiKey = apiKey;
        _ensureWs = ensureWs;
        _contextBuffer = contextBuffer;
        _parent = parent;
        _metrics = metrics;
        _ownedHttpClient = null;
        _appBaseUrl = null;
        _environment = parent?.Environment;
        _service = parent?.Service;
    }

    /// <summary>
    /// Initializes a standalone <see cref="FlagsClient"/> that builds and owns its own
    /// flags transport and a contexts buffer (against its own app transport), and on first
    /// live use opens and owns its own WebSocket.
    /// </summary>
    /// <param name="apiKey">API key. When omitted, resolved from <c>SMPLKIT_API_KEY</c> or <c>~/.smplkit</c>.</param>
    /// <param name="environment">Deployment environment used to resolve runtime flag
    /// values and to scope discovery declarations. Optional.</param>
    /// <param name="service">Service identifier auto-injected as evaluation context and
    /// attached to discovery declarations. Optional.</param>
    /// <param name="profile">Named <c>~/.smplkit</c> profile section.</param>
    /// <param name="baseDomain">Base domain for API requests (default <c>smplkit.com</c>).</param>
    /// <param name="scheme">URL scheme (default <c>https</c>).</param>
    /// <param name="debug">Enable SDK debug logging.</param>
    /// <param name="telemetry">Enable usage telemetry. Defaults to enabled.</param>
    /// <param name="extraHeaders">Extra headers attached to every request.</param>
    public FlagsClient(
        string? apiKey = null,
        string? environment = null,
        string? service = null,
        string? profile = null,
        string? baseDomain = null,
        string? scheme = null,
        bool? debug = null,
        bool? telemetry = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        // Reuse the account-global config resolver (flags CRUD is not scoped to
        // an environment) and the shared per-service URL helper, so a standalone
        // flags client resolves credentials/base-domain from ~/.smplkit / env
        // vars / constructor args exactly like the top-level clients do. Runtime
        // evaluation is scoped by the supplied environment/service.
        var resolved = ConfigResolver.ResolveAccountGlobal(new SmplClientOptions
        {
            ApiKey = apiKey,
            Profile = profile,
            BaseDomain = baseDomain,
            Scheme = scheme,
            Debug = debug,
            Telemetry = telemetry,
        });
        if (resolved.Debug)
            DebugLog.Enabled = true;

        var resolvedKey = apiKey ?? resolved.ApiKey;
        _apiKey = resolvedKey;
        _appBaseUrl = ConfigResolver.ServiceUrl(resolved.Scheme, "app", resolved.BaseDomain);
        _environment = environment;
        _service = service;

        _ownedHttpClient = new HttpClient();
        var factory = new GeneratedClientFactory(_ownedHttpClient, new SmplClientOptions
        {
            ApiKey = resolvedKey,
            BaseDomain = resolved.BaseDomain,
            Scheme = resolved.Scheme,
            Environment = environment,
            ExtraHeaders = extraHeaders is null ? null : new Dictionary<string, string>(extraHeaders),
        });
        _genFlagsClient = factory.Flags;
        _genAppClient = factory.App;

        _metrics = resolved.Telemetry
            ? new MetricsReporter(_ownedHttpClient, environment ?? string.Empty, service ?? string.Empty, appBaseUrl: _appBaseUrl)
            : null;

        // Standalone: build our own contexts buffer (the evaluation-context seam) and
        // open our own WebSocket on first live use.
        _contextBuffer = new ContextRegistrationBuffer(lruSize: 10_000, flushSize: 100);
        _parent = null;
        _ensureWs = EnsureOwnedWebSocket;
    }

    // ------------------------------------------------------------------
    // CRUD surface: builders (no live connection)
    // ------------------------------------------------------------------

    /// <summary>Return a new unsaved boolean <see cref="BooleanFlag"/>. Call <see cref="Flag.SaveAsync"/> to persist.</summary>
    /// <param name="id">Stable flag identifier. Unique per account.</param>
    /// <param name="defaultValue">Value served when no environment override or rule applies.</param>
    /// <param name="name">Human-readable display name. Defaults to a title-cased form of <paramref name="id"/>.</param>
    /// <param name="description">Optional free-text description of the flag.</param>
    /// <returns>An unsaved <see cref="BooleanFlag"/>; call <see cref="Flag.SaveAsync"/> to persist it.</returns>
    public BooleanFlag NewBooleanFlag(string id, bool defaultValue, string? name = null, string? description = null)
    {
        return new BooleanFlag(
            evalClient: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "True", ["value"] = true },
                new() { ["name"] = "False", ["value"] = false },
            },
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>Return a new unsaved string <see cref="StringFlag"/>. Call <see cref="Flag.SaveAsync"/> to persist.</summary>
    /// <param name="id">Stable flag identifier. Unique per account.</param>
    /// <param name="defaultValue">Value served when no environment override or rule applies.</param>
    /// <param name="name">Human-readable display name. Defaults to a title-cased form of <paramref name="id"/>.</param>
    /// <param name="description">Optional free-text description of the flag.</param>
    /// <param name="values">Optional list of allowed values constraining what the flag may serve. When omitted, the flag is unconstrained.</param>
    /// <returns>An unsaved <see cref="StringFlag"/>; call <see cref="Flag.SaveAsync"/> to persist it.</returns>
    public StringFlag NewStringFlag(string id, string defaultValue, string? name = null, string? description = null, IEnumerable<FlagValue>? values = null)
    {
        return new StringFlag(
            evalClient: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            @default: defaultValue,
            values: FlagValuesToInternal(values),
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>Return a new unsaved numeric <see cref="NumberFlag"/>. Call <see cref="Flag.SaveAsync"/> to persist.</summary>
    /// <param name="id">Stable flag identifier. Unique per account.</param>
    /// <param name="defaultValue">Value served when no environment override or rule applies.</param>
    /// <param name="name">Human-readable display name. Defaults to a title-cased form of <paramref name="id"/>.</param>
    /// <param name="description">Optional free-text description of the flag.</param>
    /// <param name="values">Optional list of allowed values constraining what the flag may serve. When omitted, the flag is unconstrained.</param>
    /// <returns>An unsaved <see cref="NumberFlag"/>; call <see cref="Flag.SaveAsync"/> to persist it.</returns>
    public NumberFlag NewNumberFlag(string id, double defaultValue, string? name = null, string? description = null, IEnumerable<FlagValue>? values = null)
    {
        return new NumberFlag(
            evalClient: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            @default: defaultValue,
            values: FlagValuesToInternal(values),
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>Return a new unsaved JSON <see cref="JsonFlag"/>. Call <see cref="Flag.SaveAsync"/> to persist.</summary>
    /// <param name="id">Stable flag identifier. Unique per account.</param>
    /// <param name="defaultValue">Value served when no environment override or rule applies.</param>
    /// <param name="name">Human-readable display name. Defaults to a title-cased form of <paramref name="id"/>.</param>
    /// <param name="description">Optional free-text description of the flag.</param>
    /// <param name="values">Optional list of allowed values constraining what the flag may serve. When omitted, the flag is unconstrained.</param>
    /// <returns>An unsaved <see cref="JsonFlag"/>; call <see cref="Flag.SaveAsync"/> to persist it.</returns>
    public JsonFlag NewJsonFlag(string id, Dictionary<string, object?> defaultValue, string? name = null, string? description = null, IEnumerable<FlagValue>? values = null)
    {
        return new JsonFlag(
            evalClient: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            @default: defaultValue,
            values: FlagValuesToInternal(values),
            description: description,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    // ------------------------------------------------------------------
    // CRUD surface: CRUD (no live connection)
    // ------------------------------------------------------------------

    /// <summary>Fetch the editable <see cref="Flag"/> resource by id.</summary>
    /// <param name="id">Identifier of the flag to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="Flag"/>, ready to mutate and <see cref="Flag.SaveAsync"/>.</returns>
    /// <exception cref="NotFoundException">If no matching flag exists.</exception>
    public async Task<Flag> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.Get_flagAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);
        return MapFlagResource(response.Data)
            ?? throw new NotFoundException($"Flag with id '{id}' not found");
    }

    /// <summary>List flags for the authenticated account.</summary>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Flag>> ListAsync(
        int? pageNumber = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.List_flagsAsync(
                pagenumber: pageNumber,
                pagesize: pageSize,
                cancellationToken: ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<Flag>();
        return response.Data.Select(r => MapFlagResource(r)!).Where(f => f is not null).ToList();
    }

    /// <summary>Delete a flag by id.</summary>
    /// <param name="id">Identifier of the flag to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotFoundException">No flag with that id exists for the account.</exception>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => _genFlagsClient.Delete_flagAsync(id, ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a flag (create or update). Called by <see cref="Flag.SaveAsync"/>.</summary>
    internal async Task<Flag> SaveFlagInternalAsync(Flag flag, CancellationToken ct = default)
    {
        if (flag.CreatedAt is null)
        {
            // Create — POST /flags. Send the full environments map so a
            // caller that built the flag with EnableRules + AddRule before
            // first save doesn't silently lose them.
            var body = BuildCreateFlagBody(flag.Id, flag.Name, flag.Type, flag.Default, flag.Description, flag.Values, flag.Environments);
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genFlagsClient.Create_flagAsync(body, ct)).ConfigureAwait(false);
            return MapFlagResource(response.Data)
                ?? throw new ValidationException("Failed to create flag");
        }
        else
        {
            // Update — PUT /flags/{id}
            var flagId = flag.Id ?? throw new ValidationException("Cannot update a flag without an id");
            var body = BuildUpdateFlagBody(flagId, flag.Name, flag.Type, flag.Default, flag.Values, flag.Description, flag.Environments);
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genFlagsClient.Update_flagAsync(flagId, body, ct)).ConfigureAwait(false);
            return MapFlagResource(response.Data)
                ?? throw new ValidationException("Failed to update flag");
        }
    }

    // ------------------------------------------------------------------
    // CRUD surface: discovery buffer (owned directly)
    // ------------------------------------------------------------------

    /// <summary>Buffer flag declarations for bulk-discovery upload; optionally flush now.</summary>
    /// <param name="declaration">A single <see cref="FlagDeclaration"/> to queue.</param>
    /// <param name="flush">When <c>true</c>, send the buffered declarations immediately via
    /// <see cref="FlushAsync"/> before returning. When <c>false</c> (the default), they
    /// stay buffered and are sent on the next flush — automatic once the buffer reaches
    /// its batch size, or on the first live call.</param>
    public void Register(FlagDeclaration declaration, bool flush = false)
        => Register(new[] { declaration }, flush);

    /// <summary>Buffer flag declarations for bulk-discovery upload; optionally flush now.</summary>
    /// <param name="items">A single <see cref="FlagDeclaration"/> or a list of them to queue.</param>
    /// <param name="flush">When <c>true</c>, send the buffered declarations immediately via
    /// <see cref="FlushAsync"/> before returning. When <c>false</c> (the default), they
    /// stay buffered and are sent on the next flush — automatic once the buffer reaches
    /// its batch size, or on the first live call.</param>
    public void Register(IEnumerable<FlagDeclaration> items, bool flush = false)
    {
        foreach (var d in items)
            _flagBuffer.Add(d.Id, d.Type, d.Default, d.Service, d.Environment);
        if (flush)
        {
            _lastFlagBufferFlushTask = FlushAsync();
            return;
        }
        if (_flagBuffer.PendingCount >= 50)
            _lastFlagBufferFlushTask = SafeFlushFlagsAsync();
    }

    /// <summary>
    /// POST pending declarations to the flags bulk endpoint.
    /// </summary>
    /// <remarks>
    /// Items remain in the buffer until the request succeeds, so a flush
    /// against an unhealthy <c>flags</c> service is automatically retried by
    /// the next <see cref="FlushAsync"/> call (periodic background flush,
    /// connect retry, or final flush on close). Uses peek+commit so items are
    /// removed only after a successful response.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushAsync(CancellationToken ct = default)
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

    /// <summary>Synchronous flush — alias of <see cref="FlushAsync"/> for the periodic-flush path.</summary>
    public void FlushSync() => FlushAsync().GetAwaiter().GetResult();

    /// <summary>Number of pending flag declarations awaiting flush.</summary>
    public int PendingCount => _flagBuffer.PendingCount;

    /// <summary>
    /// Number of flag declarations currently queued for registration. Exposed for tests.
    /// </summary>
    internal int PendingFlagRegistrations => _flagBuffer.PendingCount;

    /// <summary>Queue a declared flag with the owned discovery buffer.</summary>
    private void ObserveDeclaration(string flagId, string flagType, object? defaultValue)
    {
        Register(new FlagDeclaration(flagId, flagType, defaultValue, _service, _environment));
    }

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
    // Live surface: lazy connect
    // ------------------------------------------------------------------

    /// <summary>
    /// Open the live connection to the running Smpl Flags service.
    /// </summary>
    /// <remarks>
    /// <para>Flushes any buffered discovery declarations, fetches all flag
    /// definitions into the local cache, opens the shared WebSocket, and
    /// subscribes to <c>flag_changed</c> / <c>flag_deleted</c> / <c>flags_changed</c>
    /// events.</para>
    /// <para>Idempotent and internal — every live method calls it on first use, so
    /// the live surface auto-connects with no explicit step. If the flags service is
    /// unhealthy the first time (e.g. a pod starts before the schema is loaded),
    /// pending declarations are kept in the buffer, <c>_connected</c> stays
    /// <c>false</c>, and the next call retries after an exponential back-off
    /// (1 s → 60 s cap). Evaluations during the window fall back to handle defaults.</para>
    /// </remarks>
    internal void EnsureConnected()
    {
        if (_connected) return;
        if (Environment.TickCount64 < Interlocked.Read(ref _nextStartAttemptAt)) return;
        lock (_initLock)
        {
            if (_connected) return;
            if (Environment.TickCount64 < _nextStartAttemptAt) return;

            _environment = _parent is not null ? _parent.Environment : _environment;

            // Fire-and-forget environment + service context registration (best-effort, once).
            if (_service is { Length: > 0 } svc)
            {
                var env = _environment;
                _initRegistrationTask = RegisterInitialContextsAsync(svc, env);
            }

            DebugLog.Log("websocket", "flags runtime initializing");
            try
            {
                // Flush declarations BEFORE fetching definitions; items stay queued
                // until the POST succeeds so a 500 doesn't lose them.
                FlushAsync().GetAwaiter().GetResult();
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
                DebugLog.Log("registration", "registering flag_changed, flag_deleted, and flags_changed handlers");
                _wsManager.On("flag_changed", HandleFlagChanged);
                _wsManager.On("flag_deleted", HandleFlagDeleted);
                _wsManager.On("flags_changed", HandleFlagsChanged);
                _wsSubscribed = true;
            }
            DebugLog.Log("websocket", "flags runtime connected");
        }
    }

    private void ScheduleStartRetry(Exception ex)
    {
        var delayS = _startRetryDelayS;
        _nextStartAttemptAt = Environment.TickCount64 + (long)(delayS * 1000.0);
        _startRetryDelayS = Math.Min(delayS * 2.0, MaxStartRetryDelayS);
        System.Diagnostics.Trace.TraceWarning(
            "[smplkit] Flags start failed (will retry in {0:F1}s): {1}", delayS, ex.Message);
        DebugLog.Log("registration", $"Flags start failed: {ex}");
    }

    /// <summary>
    /// Return the shared WebSocket — the parent's when wired, else our own (lazily started).
    /// </summary>
    private SharedWebSocket EnsureOwnedWebSocket()
    {
        if (_wsManager is not null) return _wsManager;
        _wsManager = new SharedWebSocket(_apiKey, metrics: _metrics, appBaseUrl: _appBaseUrl ?? "https://app.smplkit.com");
        _wsManager.Start();
        _ownsWs = true;
        return _wsManager;
    }

    // ------------------------------------------------------------------
    // Live surface: typed flag handles
    // ------------------------------------------------------------------

    /// <summary>Declare a boolean flag handle for live evaluation. Connects lazily on first use.</summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="defaultValue">The default value used when no server-side value is available.</param>
    /// <returns>A typed flag handle.</returns>
    public BooleanFlag BooleanFlag(string id, bool defaultValue)
    {
        EnsureConnected();
        var handle = new BooleanFlag(
            evalClient: this,
            id: id, name: id,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[id] = handle;
        ObserveDeclaration(id, "BOOLEAN", defaultValue);
        return handle;
    }

    /// <summary>Declare a string flag handle for live evaluation. Connects lazily on first use.</summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="defaultValue">The default value used when no server-side value is available.</param>
    /// <returns>A typed flag handle.</returns>
    public StringFlag StringFlag(string id, string defaultValue)
    {
        EnsureConnected();
        var handle = new StringFlag(
            evalClient: this,
            id: id, name: id,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[id] = handle;
        ObserveDeclaration(id, "STRING", defaultValue);
        return handle;
    }

    /// <summary>Declare a numeric flag handle for live evaluation. Connects lazily on first use.</summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="defaultValue">The default value used when no server-side value is available.</param>
    /// <returns>A typed flag handle.</returns>
    public NumberFlag NumberFlag(string id, double defaultValue)
    {
        EnsureConnected();
        var handle = new NumberFlag(
            evalClient: this,
            id: id, name: id,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[id] = handle;
        ObserveDeclaration(id, "NUMERIC", defaultValue);
        return handle;
    }

    /// <summary>Declare a JSON flag handle for live evaluation. Connects lazily on first use.</summary>
    /// <param name="id">The flag identifier.</param>
    /// <param name="defaultValue">The default value used when no server-side value is available.</param>
    /// <returns>A typed flag handle.</returns>
    public JsonFlag JsonFlag(string id, Dictionary<string, object?> defaultValue)
    {
        EnsureConnected();
        var handle = new JsonFlag(
            evalClient: this,
            id: id, name: id,
            @default: defaultValue,
            values: new List<Dictionary<string, object?>>(),
            description: null,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null, updatedAt: null);
        _handles[id] = handle;
        ObserveDeclaration(id, "JSON", defaultValue);
        return handle;
    }

    // ------------------------------------------------------------------
    // Live surface: refresh / stats / change listeners
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-fetch all flag definitions and clear cache.
    /// </summary>
    /// <remarks>Connects lazily on first use — no explicit install step.</remarks>
    /// <param name="ct">Cancellation token.</param>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        await FetchAllFlagsAsync(ct).ConfigureAwait(false);
        _cache.Clear();
        FireChangeListenersAll("manual");
    }

    /// <summary>
    /// Gets the current real-time connection status.
    /// </summary>
    public string ConnectionStatus => _wsManager?.ConnectionStatus ?? "disconnected";

    /// <summary>Return evaluation statistics. Connects lazily on first use.</summary>
    public FlagStats Stats
    {
        get
        {
            EnsureConnected();
            return new FlagStats(_cache.CacheHits, _cache.CacheMisses);
        }
    }

    /// <summary>
    /// Register a global change listener that fires when any flag changes.
    /// </summary>
    /// <remarks>Connects lazily on first use — no explicit install step.</remarks>
    /// <param name="callback">Called with a <see cref="FlagChangeEvent"/> on each change.</param>
    public void OnChange(Action<FlagChangeEvent> callback)
    {
        EnsureConnected();
        _globalListeners.Add(callback);
    }

    /// <summary>
    /// Register a change listener scoped to a specific flag id.
    /// </summary>
    /// <remarks>Connects lazily on first use — no explicit install step.</remarks>
    /// <param name="flagId">The flag identifier to listen for.</param>
    /// <param name="callback">Called with a <see cref="FlagChangeEvent"/> when this flag changes.</param>
    public void OnChange(string flagId, Action<FlagChangeEvent> callback)
    {
        EnsureConnected();
        var list = _scopedListeners.GetOrAdd(flagId, _ => new List<Action<FlagChangeEvent>>());
        lock (list)
        {
            list.Add(callback);
        }
    }

    /// <summary>
    /// Best-effort initial context registration: registers the configured
    /// environment and service with the contexts service. Failures are logged
    /// and swallowed; this is fired-and-forgotten from <see cref="EnsureConnected"/>.
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
            DebugLog.Log("registration", $"Context registration failed: {ex}");
        }
    }

    // ------------------------------------------------------------------
    // Internal: context flush (called from EvaluateHandle auto-flush path)
    // ------------------------------------------------------------------

    /// <summary>
    /// Sends any pending context registrations to the server.
    /// Public context registration is via <c>client.Platform.Contexts.RegisterAsync()</c>.
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
            DebugLog.Log("registration", $"Context flush failed: {ex}");
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
    /// Safe wrapper around <see cref="FlushAsync"/>: swallows errors and
    /// logs a warning. Items stay queued for the next attempt. Used by the
    /// periodic timer and the threshold-triggered flush paths.
    /// </summary>
    private async Task SafeFlushFlagsAsync(CancellationToken ct = default)
    {
        try
        {
            await FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "[smplkit] Flag registration flush failed: {0}", ex.Message);
            DebugLog.Log("registration", $"Flag registration flush failed: {ex}");
        }
    }

    // ------------------------------------------------------------------
    // Internal: evaluation
    // ------------------------------------------------------------------

    internal object? EvaluateHandle(string id, object? defaultValue, IReadOnlyList<Context>? context)
    {
        EnsureConnected();

        Dictionary<string, object?> evalDict;
        if (context is not null)
        {
            // Explicit context: register here. (Implicit set-context registers
            // at the entry point, so the provider branch below doesn't need to.)
            _contextBuffer.Observe(context);
            if (_contextBuffer.PendingCount >= ContextBatchFlushSize)
                _lastContextBufferFlushTask = FlushContextsAsync();
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

        // Auto-inject service context if set and not already provided
        if (_service is { Length: > 0 } svc && !evalDict.ContainsKey("service"))
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
        DebugLog.Log("websocket", $"flag_changed event received, id={flagId ?? "<unknown>"}");
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
            DebugLog.Log("websocket", $"Flag refresh failed: {ex}");
        }
    }

    private void HandleFlagDeleted(Dictionary<string, object?> data)
    {
        var flagId = data.TryGetValue("id", out var k) ? k as string : null;
        DebugLog.Log("websocket", $"flag_deleted event received, id={flagId ?? "<unknown>"}");
        if (flagId is null) return;

        _flagStore.TryRemove(flagId, out _);
        _cache.Clear();
        FireChangeListeners(flagId, "websocket", deleted: true);
    }

    private void HandleFlagsChanged(Dictionary<string, object?> data)
    {
        DebugLog.Log("websocket", "flags_changed event received — full list refetch");
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
            DebugLog.Log("websocket", $"Flags bulk refresh failed: {ex}");
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
    /// pagination control go through <see cref="ListAsync"/>.
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
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Release resources — only those this client owns.
    /// </summary>
    /// <remarks>
    /// Stops the periodic flag flush timer and unregisters WebSocket event handlers.
    /// Tears down the owned WebSocket (standalone) and the owned flags + app HTTP
    /// transports (standalone construction). A wired client borrows the parent's
    /// transport, WebSocket, and context buffer and closes none of them.
    /// </remarks>
    public void Dispose()
    {
        Close();
        if (_ownsWs && _wsManager is not null)
        {
            _wsManager.StopAsync().GetAwaiter().GetResult();
            _wsManager = null;
            _ownsWs = false;
        }
        _metrics?.Dispose();
        _ownedHttpClient?.Dispose();
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
            if (!_ownsWs)
                _wsManager = null;
        }
        _wsSubscribed = false;
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

    // ------------------------------------------------------------------
    // Wire helpers — CRUD response mapping + request-body building, owned by
    // the fused client (one path for CRUD and live evaluation).
    // ------------------------------------------------------------------

    private Flag? MapFlagResource(GenFlags.FlagResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        List<Dictionary<string, object?>>? values = null;
        if (attrs.Values is not null)
        {
            values = new List<Dictionary<string, object?>>();
            foreach (var v in attrs.Values)
                values.Add(new Dictionary<string, object?>
                {
                    ["name"] = v.Name,
                    ["value"] = NormalizeValue(v.Value),
                });
        }

        var environments = ExtractEnvironments(attrs.Environments);

        DateTime? createdAt = null;
        if (attrs.Created_at is DateTimeOffset createdDto) createdAt = createdDto.DateTime;

        DateTime? updatedAt = null;
        if (attrs.Updated_at is DateTimeOffset updatedDto) updatedAt = updatedDto.DateTime;

        return new Flag(
            evalClient: this,
            id: resource.Id ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            type: attrs.Type.ToString(),
            @default: NormalizeValue(attrs.Default),
            values: values,
            description: attrs.Description,
            environments: environments,
            createdAt: createdAt,
            updatedAt: updatedAt);
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

    private static List<Dictionary<string, object?>>? FlagValuesToInternal(IEnumerable<FlagValue>? values)
    {
        if (values is null) return null;
        return values.Select(v => new Dictionary<string, object?>
        {
            ["name"] = v.Name,
            ["value"] = v.Value,
        }).ToList();
    }

    private static GenFlags.FlagCreateRequest BuildCreateFlagBody(
        string? id, string name, string type, object? @default,
        string? description, List<Dictionary<string, object?>>? values,
        Dictionary<string, Dictionary<string, object?>> environments)
    {
        var flagValues = values?.Select(v => new GenFlags.FlagValue
        {
            Name = v.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "",
            Value = v.TryGetValue("value", out var val) ? val! : new object(),
        }).ToList();

        return new GenFlags.FlagCreateRequest
        {
            Data = new GenFlags.FlagCreateResource
            {
                Type = "flag",
                Id = id ?? throw new ValidationException("Cannot create a flag without an id"),
                Attributes = new GenFlags.Flag
                {
                    Name = name,
                    Type = Enum.Parse<GenFlags.FlagType>(type),
                    Default = @default ?? new object(),
                    Description = description ?? "",
                    Values = flagValues!,
                    Environments = BuildEnvironmentsWire(environments),
                },
            }
        };
    }

    private static GenFlags.FlagRequest BuildUpdateFlagBody(
        string? id, string name, string type, object? @default,
        List<Dictionary<string, object?>>? values, string? description,
        Dictionary<string, Dictionary<string, object?>> environments)
    {
        var flagValues = values?.Select(v => new GenFlags.FlagValue
        {
            Name = v.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "",
            Value = v.TryGetValue("value", out var val) ? val! : new object(),
        }).ToList();

        return new GenFlags.FlagRequest
        {
            Data = new GenFlags.FlagResource
            {
                Type = "flag",
                Id = id,
                Attributes = new GenFlags.Flag
                {
                    Name = name,
                    Type = Enum.Parse<GenFlags.FlagType>(type),
                    Default = @default ?? new object(),
                    Description = description ?? "",
                    Values = flagValues!,
                    Environments = BuildEnvironmentsWire(environments),
                },
            }
        };
    }

    private static Dictionary<string, GenFlags.FlagEnvironment> BuildEnvironmentsWire(
        Dictionary<string, Dictionary<string, object?>> environments)
    {
        var flagEnvs = new Dictionary<string, GenFlags.FlagEnvironment>();
        foreach (var (envName, envData) in environments)
        {
            var flagEnv = new GenFlags.FlagEnvironment
            {
                Enabled = envData.TryGetValue("enabled", out var e) && e is bool eb && eb,
                Default = envData.TryGetValue("default", out var d) ? d : null,
            };
            if (envData.TryGetValue("rules", out var rulesObj) && rulesObj is List<object?> rulesList)
            {
                flagEnv.Rules = rulesList
                    .OfType<Dictionary<string, object?>>()
                    .Select(r => new GenFlags.FlagRule
                    {
                        Description = r.TryGetValue("description", out var desc) ? desc?.ToString() : null,
                        Logic = r.TryGetValue("logic", out var logic) ? logic ?? new object() : new object(),
                        Value = r.TryGetValue("value", out var v) ? v! : new object(),
                    }).ToList();
            }
            else
            {
                flagEnv.Rules = new List<GenFlags.FlagRule>();
            }
            flagEnvs[envName] = flagEnv;
        }
        return flagEnvs;
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
