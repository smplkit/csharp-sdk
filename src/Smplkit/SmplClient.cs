using Smplkit.Audit;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Internal;
using Smplkit.Logging;
using DebugLog = Smplkit.Internal.Debug;

namespace Smplkit;

/// <summary>
/// Top-level client for the smplkit runtime plane: flag evaluation, config reads,
/// log emission. Construction may register the service, start metrics, open a
/// WebSocket, and install the logging discovery hooks.
/// </summary>
/// <remarks>
/// <para>For pure CRUD work (setup scripts, CI tooling, admin tasks) prefer
/// <see cref="SmplManagementClient"/>, which has zero side effects on construction.</para>
/// <para>If you need both planes in one process, use <see cref="Manage"/> to
/// access the management client wired against the same HTTP transport.</para>
/// </remarks>
public sealed class SmplClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly string _appBaseUrl;
    private readonly GeneratedClientFactory _clients;
    private readonly MetricsReporter? _metrics;
    private readonly ContextRegistrationBuffer _contextBuffer;
    private SharedWebSocket? _sharedWs;
    private readonly object _wsLock = new();
    private readonly AsyncLocal<IReadOnlyList<Context>?> _ambientContext = new();

    /// <summary>Gets the resolved environment key.</summary>
    public string Environment { get; }

    /// <summary>Gets the resolved service identifier.</summary>
    public string Service { get; }

    /// <summary>Runtime config reads + listeners.</summary>
    public ConfigClient Config { get; }

    /// <summary>Flag evaluation + listeners.</summary>
    public FlagsClient Flags { get; }

    /// <summary>Runtime logging integration.</summary>
    public LoggingClient Logging { get; }

    /// <summary>
    /// Audit-product surface (ADR-047). Use <c>client.Audit.Events.Record(...)</c>
    /// to record an event; the call is fire-and-forget and returns immediately
    /// while a background task issues the POST and retries transient failures.
    /// </summary>
    public AuditClient Audit { get; }

    /// <summary>
    /// Management client wired against the same HTTP transport as this runtime
    /// client. Use this for setup scripts, CI tasks, and admin tooling without
    /// constructing a separate <see cref="SmplManagementClient"/>.
    /// </summary>
    public SmplManagementClient Manage { get; }

    /// <summary>
    /// Initializes a new <see cref="SmplClient"/> with automatic config resolution.
    /// </summary>
    public SmplClient()
        : this(new SmplClientOptions(), new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>Initializes a new <see cref="SmplClient"/> with the specified options.</summary>
    public SmplClient(SmplClientOptions options)
        : this(options, new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>Initializes a new <see cref="SmplClient"/> with caller-owned <see cref="HttpClient"/>.</summary>
    public SmplClient(SmplClientOptions options, HttpClient httpClient)
        : this(options, httpClient, ownsHttpClient: false)
    {
    }

    private SmplClient(SmplClientOptions options, HttpClient httpClient, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);

        var resolved = ConfigResolver.Resolve(options);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _apiKey = resolved.ApiKey;
        _appBaseUrl = ConfigResolver.ServiceUrl(resolved.Scheme, "app", resolved.BaseDomain);
        Environment = resolved.Environment;
        Service = resolved.Service;

        if (resolved.Debug)
            DebugLog.Enabled = true;

        var resolvedOptions = new SmplClientOptions
        {
            ApiKey = resolved.ApiKey,
            Timeout = options.Timeout,
            BaseDomain = resolved.BaseDomain,
            Scheme = resolved.Scheme,
            // Runtime audit ops are environment-scoped (ADR-055): the factory
            // stamps X-Smplkit-Environment from this on the runtime audit client.
            Environment = resolved.Environment,
            ExtraHeaders = options.ExtraHeaders,
        };
        _clients = new GeneratedClientFactory(_httpClient, resolvedOptions);

        _metrics = resolved.DisableTelemetry
            ? null
            : new MetricsReporter(_httpClient, resolved.Environment, resolved.Service, appBaseUrl: _appBaseUrl);

        _contextBuffer = new ContextRegistrationBuffer(lruSize: 10_000, flushSize: 100);

        // Construct the management plane first so the runtime sub-clients can
        // reference _parent.Manage.* for their CRUD fetches. Management is a
        // peer of the runtime — it does not wrap or own any runtime sub-client.
        Manage = new SmplManagementClient(_httpClient, _clients, _appBaseUrl, _contextBuffer);

        Config = new ConfigClient(_clients, EnsureSharedWebSocket, this, _metrics);
        Flags = new FlagsClient(_clients, _apiKey, EnsureSharedWebSocket, _contextBuffer, this, _metrics);
        Logging = new LoggingClient(_clients, _apiKey, EnsureSharedWebSocket, this, _metrics);
        Audit = new AuditClient(_clients.AuditRuntime);

        // Wire up ambient-context bridge for flag evaluation.
        Flags.SetContextProvider(GetAmbientContext);

        var maskedKey = resolved.ApiKey.Length > 10
            ? resolved.ApiKey[..10] + "..."
            : resolved.ApiKey + "...";
        DebugLog.Log("lifecycle", $"SmplClient created (api_key={maskedKey}, environment={resolved.Environment}, service={resolved.Service})");
    }

    /// <summary>
    /// Sets the active eval context for the calling async-flow / thread.
    /// The returned <see cref="IDisposable"/> reverts the context when disposed
    /// (e.g. <c>using (client.SetContext(ctx)) { ... }</c>).
    /// </summary>
    /// <remarks>
    /// In a real app, set the context once per request from middleware — not
    /// scattered through your handlers. The method also queues the contexts
    /// for background registration via the management plane.
    /// </remarks>
    public IDisposable SetContext(IEnumerable<Context> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        var list = contexts as IReadOnlyList<Context> ?? contexts.ToList();
        var previous = _ambientContext.Value;
        _ambientContext.Value = list;

        // Queue contexts for background registration (best-effort).
        // The discarded Task automatically captures any failure — the
        // registration is best-effort and never propagates back to the caller.
        _ = Manage.Contexts.RegisterAsync(list);

        return new ContextScope(this, previous);
    }

    /// <summary>Convenience overload for a single context.</summary>
    public IDisposable SetContext(Context context) => SetContext(new[] { context });

    /// <summary>
    /// Eagerly initializes flags + configs + the WebSocket. Logging is opt-in
    /// via <see cref="LoggingClient.InstallAsync"/>.
    /// </summary>
    public async Task WaitUntilReadyAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = timeout ?? TimeSpan.FromSeconds(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        try
        {
            // Touch flags + configs to force initialization.
            Flags.EnsureInitialized();
            Config.EnsureInitialized();

            // Wait for WebSocket connection.
            var ws = EnsureSharedWebSocket();
            var pollInterval = TimeSpan.FromMilliseconds(50);
            while (ws.ConnectionStatus != "connected")
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(pollInterval, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Smplkit.Errors.TimeoutException(
                $"WaitUntilReadyAsync timed out after {deadline}. The SDK could not "
                + "fully initialize within the deadline (flags, configs, or WebSocket).");
        }
    }

    internal IReadOnlyList<Context> GetAmbientContext()
        => _ambientContext.Value ?? Array.Empty<Context>();

    /// <summary>Ensures the real-time connection is available.</summary>
    internal SharedWebSocket EnsureSharedWebSocket()
    {
        if (_sharedWs is not null) return _sharedWs;
        lock (_wsLock)
        {
            if (_sharedWs is not null) return _sharedWs;
            _sharedWs = new SharedWebSocket(_apiKey, metrics: _metrics, appBaseUrl: _appBaseUrl);
            _sharedWs.Start();
            return _sharedWs;
        }
    }

    /// <summary>Releases resources used by this client.</summary>
    public void Dispose()
    {
        DebugLog.Log("lifecycle", "SmplClient.Dispose() called");
        Flags.Close();
        Logging.Close();
        Audit.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (_sharedWs is not null)
        {
            _sharedWs.StopAsync().GetAwaiter().GetResult();
            _sharedWs = null;
        }

        _metrics?.Dispose();

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    /// <summary>Reverts the ambient eval context on dispose.</summary>
    private sealed class ContextScope : IDisposable
    {
        private readonly SmplClient _owner;
        private readonly IReadOnlyList<Context>? _previous;
        private bool _disposed;

        public ContextScope(SmplClient owner, IReadOnlyList<Context>? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner._ambientContext.Value = _previous;
        }
    }
}
