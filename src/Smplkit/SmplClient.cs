using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Internal;

namespace Smplkit;

/// <summary>
/// Top-level client for the smplkit SDK. Provides access to service-specific
/// sub-clients (e.g., <see cref="Config"/>, <see cref="Flags"/>).
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// <code>
/// using var client = new SmplClient(new SmplClientOptions
/// {
///     ApiKey = "sk_api_...",
///     Environment = "production",
/// });
/// await client.ConnectAsync();
/// var flag = client.Flags.BoolFlag("my-flag", false);
/// </code>
/// </para>
/// </remarks>
public sealed class SmplClient : IDisposable
{
    private const string AppBaseUrl = "https://app.smplkit.com";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly Transport _transport;
    private SharedWebSocket? _sharedWs;
    private readonly object _wsLock = new();
    private volatile bool _connected;

    /// <summary>
    /// Gets the resolved environment key.
    /// </summary>
    public string Environment { get; }

    /// <summary>
    /// Gets the resolved service identifier, or <c>null</c> if none was configured.
    /// </summary>
    public string? Service { get; }

    /// <summary>
    /// Gets the Config service client.
    /// </summary>
    public ConfigClient Config { get; }

    /// <summary>
    /// Gets the Flags service client.
    /// </summary>
    public FlagsClient Flags { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="SmplClient"/> with automatic API key
    /// resolution from the <c>SMPLKIT_API_KEY</c> environment variable or
    /// <c>~/.smplkit</c> config file.
    /// </summary>
    public SmplClient()
        : this(new SmplClientOptions(), new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SmplClient"/> with the specified options.
    /// Creates and owns a new <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="options">Client configuration options.</param>
    public SmplClient(SmplClientOptions options)
        : this(options, new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SmplClient"/> with the specified options
    /// and a caller-provided <see cref="HttpClient"/>. The caller retains ownership and
    /// is responsible for disposing the <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="options">Client configuration options.</param>
    /// <param name="httpClient">An externally managed HTTP client (e.g., for testing).</param>
    public SmplClient(SmplClientOptions options, HttpClient httpClient)
        : this(options, httpClient, ownsHttpClient: false)
    {
    }

    private SmplClient(SmplClientOptions options, HttpClient httpClient, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);

        var resolvedApiKey = ApiKeyResolver.Resolve(options.ApiKey);

        // Resolve environment: option -> env var -> throw
        var resolvedEnvironment = options.Environment
            ?? System.Environment.GetEnvironmentVariable("SMPLKIT_ENVIRONMENT");
        if (string.IsNullOrEmpty(resolvedEnvironment))
            throw new SmplException(
                "Environment is required. Pass it via SmplClientOptions.Environment " +
                "or set the SMPLKIT_ENVIRONMENT environment variable.");

        // Resolve service: option -> env var -> null (valid)
        var resolvedService = options.Service
            ?? System.Environment.GetEnvironmentVariable("SMPLKIT_SERVICE");

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _apiKey = resolvedApiKey;
        Environment = resolvedEnvironment;
        Service = string.IsNullOrEmpty(resolvedService) ? null : resolvedService;

        // Create a copy of options with the resolved API key for Transport.
        var resolvedOptions = new SmplClientOptions
        {
            ApiKey = resolvedApiKey,
            Timeout = options.Timeout,
        };
        _transport = new Transport(_httpClient, resolvedOptions);
        Config = new ConfigClient(_transport, resolvedApiKey, this);
        Flags = new FlagsClient(_transport, resolvedApiKey, EnsureSharedWebSocket, this);
    }

    /// <summary>
    /// Connect the client: fetches flag definitions and config values for the
    /// configured environment. Idempotent — subsequent calls are no-ops.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;

        // Fire-and-forget service context registration
        if (Service is { Length: > 0 } svc)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _transport.PostAsync(
                        $"{AppBaseUrl}/api/v1/contexts/bulk",
                        new
                        {
                            contexts = new[]
                            {
                                new
                                {
                                    type = "service",
                                    key = svc,
                                    attributes = new Dictionary<string, object?>(),
                                },
                            },
                        },
                        ct).ConfigureAwait(false);
                }
                catch { /* fire-and-forget */ }
            }, ct);
        }

        await Flags.ConnectInternalAsync(Environment, ct).ConfigureAwait(false);
        await Config.ConnectInternalAsync(Environment, ct).ConfigureAwait(false);

        // Wait for the shared WebSocket to complete its initial connection attempt
        if (_sharedWs is not null)
        {
            await _sharedWs.WaitForInitialConnectAsync(ct).ConfigureAwait(false);
        }

        _connected = true;
    }

    /// <summary>
    /// Lazily creates and starts the shared WebSocket connection.
    /// </summary>
    internal SharedWebSocket EnsureSharedWebSocket()
    {
        if (_sharedWs is not null) return _sharedWs;
        lock (_wsLock)
        {
            if (_sharedWs is not null) return _sharedWs;
            _sharedWs = new SharedWebSocket(_apiKey);
            _sharedWs.Start();
            return _sharedWs;
        }
    }

    /// <summary>
    /// Disposes the underlying HTTP client if it is owned by this instance.
    /// </summary>
    public void Dispose()
    {
        if (_sharedWs is not null)
        {
            _sharedWs.StopAsync().GetAwaiter().GetResult();
            _sharedWs = null;
        }

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
