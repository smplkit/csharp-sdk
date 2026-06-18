using Smplkit.Account;
using Smplkit.Audit;
using Smplkit.Config;
using Smplkit.Flags;
using Smplkit.Internal;
using Smplkit.Jobs;
using Smplkit.Logging;
using Smplkit.Platform;
using DebugLog = Smplkit.Internal.Debug;

namespace Smplkit;

/// <summary>
/// Entry point for the smplkit SDK.
/// </summary>
/// <remarks>
/// <para>Usage:</para>
/// <code>
/// using var client = new SmplClient(new SmplClientOptions { Environment = "production", Service = "my-svc" });
/// var checkoutV2 = client.Flags.BooleanFlag("checkout-v2", defaultValue: false);
/// if (checkoutV2.Get()) { ... }
/// </code>
/// <para>All parameters are optional. When omitted, the SDK resolves them from
/// environment variables (<c>SMPLKIT_*</c>) or the <c>~/.smplkit</c> configuration
/// file. Resolution follows a precedence order, from lowest to highest: built-in
/// defaults, then the <c>~/.smplkit</c> file, then <c>SMPLKIT_*</c> environment
/// variables, then explicit constructor arguments.</para>
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

    /// <summary>Platform's cross-cutting CRUD: environments, services, contexts, context types.</summary>
    public PlatformClient Platform { get; }

    /// <summary>Account-level settings.</summary>
    public AccountClient Account { get; }

    /// <summary>Config's full surface: reads, listeners, and CRUD.</summary>
    public ConfigClient Config { get; }

    /// <summary>Flags' full surface: evaluation, listeners, and CRUD.</summary>
    public FlagsClient Flags { get; }

    /// <summary>Logging's full surface: install, level control, loggers, and log groups.</summary>
    public LoggingClient Logging { get; }

    /// <summary>
    /// Audit's full surface: events, discovery, categories, and forwarders. Use
    /// <c>client.Audit.Events.Record(...)</c> to record an event; the call is
    /// fire-and-forget and returns immediately while a background task issues the
    /// POST and retries transient failures.
    /// </summary>
    public AuditClient Audit { get; }

    /// <summary>Jobs' full surface: job CRUD, runs, run history, and usage.</summary>
    public JobsClient Jobs { get; }

    /// <summary>
    /// Initializes a new <see cref="SmplClient"/> with automatic config resolution.
    /// </summary>
    public SmplClient()
        : this(new SmplClientOptions(), new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>Initializes a new <see cref="SmplClient"/> with the specified options.</summary>
    /// <param name="options">The options bag that seeds config resolution. Any value left
    /// unset is resolved from <c>SMPLKIT_*</c> environment variables or the <c>~/.smplkit</c>
    /// file; explicit values here take precedence over both.</param>
    public SmplClient(SmplClientOptions options)
        : this(options, new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>Initializes a new <see cref="SmplClient"/> with caller-owned <see cref="HttpClient"/>.</summary>
    /// <param name="options">The options bag that seeds config resolution. Any value left
    /// unset is resolved from <c>SMPLKIT_*</c> environment variables or the <c>~/.smplkit</c>
    /// file; explicit values here take precedence over both.</param>
    /// <param name="httpClient">A caller-owned <see cref="HttpClient"/> reused for all requests.
    /// Because the caller owns it, <see cref="SmplClient"/> does not dispose it; the caller is
    /// responsible for its lifetime.</param>
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
            ExtraHeaders = options.ExtraHeaders,
        };
        // Audit env scoping rides the event body and filter[environment] (ADR-055),
        // not the transport, so the factory no longer needs the configured
        // environment; the audit wrapper below receives it directly.
        _clients = new GeneratedClientFactory(_httpClient, resolvedOptions);

        _metrics = resolved.Telemetry
            ? new MetricsReporter(_httpClient, resolved.Environment, resolved.Service, appBaseUrl: _appBaseUrl)
            : null;

        var auditUrl = ConfigResolver.ServiceUrl(resolved.Scheme, "audit", resolved.BaseDomain);
        var extraHeaders = options.ExtraHeaders is null
            ? null
            : new Dictionary<string, string>(options.ExtraHeaders);

        _contextBuffer = new ContextRegistrationBuffer(lruSize: 10_000, flushSize: 100);

        // Platform's cross-cutting CRUD on one client; wired into this parent so
        // it borrows the shared app transport and owns the context-registration
        // buffer. Built BEFORE flags so the contexts seam below is available.
        Platform = new PlatformClient(_clients.App, _contextBuffer);
        // Account-level settings on one client; built from the app url + api key
        // (the settings sub-client uses HttpClient directly).
        Account = new AccountClient(apiKey: resolved.ApiKey, baseUrl: _appBaseUrl, extraHeaders: extraHeaders);
        // Config's full surface on one client; wired into this parent so it
        // borrows the shared config transport and WebSocket.
        Config = new ConfigClient(_clients, EnsureSharedWebSocket, this, _metrics);
        // Flags' full surface on one client; wired into this parent so it borrows
        // the shared flags transport and WebSocket. The context buffer is the
        // injection seam for evaluation-context registration, wired to
        // client.Platform.Contexts.
        Flags = new FlagsClient(_clients, _apiKey, EnsureSharedWebSocket, _contextBuffer, this, _metrics);
        // Logging's full surface on one client; wired into this parent so it
        // borrows the shared logging transport and WebSocket. The two CRUD
        // sub-clients live at client.Logging.Loggers / client.Logging.LogGroups.
        Logging = new LoggingClient(_clients, _apiKey, EnsureSharedWebSocket, this, _metrics);
        // Audit's full surface; this runtime instance scopes recording and reads
        // to the configured environment via the event body and filter[environment]
        // (ADR-055) and owns its own transport (closed in Dispose()).
        Audit = new AuditClient(resolved.ApiKey, auditUrl, resolved.Environment, extraHeaders);
        // Jobs installs no in-process machinery — reuse the shared jobs transport
        // (single connection pool) so client.Jobs is one-stop. The SDK's
        // configured runtime environment defaults the one-off birth / manual-run /
        // run-filter scoping, mirroring the audit surface.
        Jobs = new JobsClient(_clients.Jobs, resolved.Environment);

        // Wire up ambient-context bridge for flag evaluation.
        Flags.SetContextProvider(GetAmbientContext);

        var maskedKey = resolved.ApiKey.Length > 10
            ? resolved.ApiKey[..10] + "..."
            : resolved.ApiKey + "...";
        DebugLog.Log("lifecycle", $"SmplClient created (api_key={maskedKey}, environment={resolved.Environment}, service={resolved.Service})");
    }

    /// <summary>
    /// Stash <paramref name="contexts"/> as the current request's evaluation context.
    /// </summary>
    /// <remarks>
    /// Typical use is from middleware — set the context once at request entry and
    /// every <c>flag.Get()</c> (and other context-sensitive evaluations) inside that
    /// request automatically picks it up. The returned <see cref="IDisposable"/>
    /// reverts the context when disposed (e.g. <c>using (client.SetContext(ctx)) { ... }</c>).
    /// Each unique <c>(type, key)</c> is also queued for bulk registration on the
    /// platform API (deduplicated via an LRU; flushed in the background).
    /// </remarks>
    /// <param name="contexts">The contexts to register as the current ambient evaluation
    /// context. An empty list clears any registration step but still returns a scope.</param>
    /// <returns>An <see cref="IDisposable"/> scope that reverts the ambient context to its
    /// previous value when disposed. Use it in a <c>using</c> block to bound the context to a
    /// request, or ignore the return value for fire-and-forget registration.</returns>
    public IDisposable SetContext(IEnumerable<Context> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        var list = contexts as IReadOnlyList<Context> ?? contexts.ToList();
        var previous = _ambientContext.Value;
        _ambientContext.Value = list;

        // Queue contexts for background registration (best-effort).
        // The discarded Task automatically captures any failure — the
        // registration is best-effort and never propagates back to the caller.
        if (list.Count > 0)
            _ = Platform.Contexts.RegisterAsync(list);

        return new ContextScope(this, previous);
    }

    /// <summary>Convenience overload for a single context.</summary>
    /// <param name="context">The context to register as the current ambient evaluation context.</param>
    /// <returns>An <see cref="IDisposable"/> scope that reverts the ambient context on dispose;
    /// use it in a <c>using</c> block, or ignore it for fire-and-forget registration.</returns>
    public IDisposable SetContext(Context context) => SetContext(new[] { context });

    /// <summary>
    /// Optionally pre-warm the SDK and block until the live socket is up.
    /// </summary>
    /// <remarks>
    /// Eagerly connects config and flags — flushing discovery, pre-fetching all
    /// flags and configs into the local cache, opening the live-updates WebSocket —
    /// and waits for the handshake to complete. After this returns, <c>flag.Get()</c> /
    /// <c>client.Config.Subscribe()</c> hit cache (no first-request connect tax) and any
    /// <c>OnChange</c> listeners receive every server event from this point forward.
    /// Optional: config and flags connect lazily on first live use, so this is purely
    /// a pre-warm / WebSocket-ready barrier. Logging integration is <i>not</i> connected
    /// here — call <see cref="LoggingClient.InstallAsync"/> separately if you want it (it
    /// installs adapters and hooks into your application's logger, which should be opt-in).
    /// </remarks>
    /// <param name="timeout">Maximum time to wait for the live-updates WebSocket handshake
    /// before giving up. Defaults to 10 seconds.</param>
    /// <param name="ct">A token to cancel the wait.</param>
    /// <exception cref="Smplkit.Errors.TimeoutException">If the WebSocket fails to connect within <paramref name="timeout"/>.</exception>
    public async Task WaitUntilReadyAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var deadline = timeout ?? TimeSpan.FromSeconds(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        try
        {
            // Touch flags + configs to force initialization.
            Flags.EnsureConnected();
            Config.EnsureConnected();

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

    /// <summary>Releases all resources held by this client.</summary>
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
