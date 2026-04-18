using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Internal;
using Smplkit.Logging;
using GenApp = Smplkit.Internal.Generated.App;
using DebugLog = Smplkit.Internal.Debug;

namespace Smplkit;

/// <summary>
/// Top-level client for the smplkit SDK. Provides access to service-specific
/// sub-clients (<see cref="Config"/>, <see cref="Flags"/>, <see cref="Logging"/>).
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
/// var flag = client.Flags.BooleanFlag("my-flag", false);
/// bool value = flag.Get();
/// </code>
/// </para>
/// </remarks>
public sealed class SmplClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private readonly string _appBaseUrl;
    private readonly GeneratedClientFactory _clients;
    private readonly MetricsReporter? _metrics;
    private SharedWebSocket? _sharedWs;
    private readonly object _wsLock = new();

    /// <summary>
    /// Gets the resolved environment key.
    /// </summary>
    public string Environment { get; }

    /// <summary>
    /// Gets the resolved service identifier.
    /// </summary>
    public string Service { get; }

    /// <summary>
    /// Gets the Config service client.
    /// </summary>
    public ConfigClient Config { get; }

    /// <summary>
    /// Gets the Flags service client.
    /// </summary>
    public FlagsClient Flags { get; }

    /// <summary>
    /// Gets the Logging service client.
    /// </summary>
    public LoggingClient Logging { get; }

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

        // 4-step resolution: defaults -> file -> env vars -> constructor args
        var config = ConfigResolver.Resolve(options);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _apiKey = config.ApiKey;
        _appBaseUrl = ConfigResolver.ServiceUrl(config.Scheme, "app", config.BaseDomain);
        Environment = config.Environment;
        Service = config.Service;

        // Apply resolved debug setting (only enable, never disable — multiple
        // SmplClient instances or tests may coexist and we must not clobber
        // an earlier explicit enable).
        if (config.Debug)
            DebugLog.Enabled = true;

        // Create options with resolved values and build generated clients.
        var resolvedOptions = new SmplClientOptions
        {
            ApiKey = config.ApiKey,
            Timeout = options.Timeout,
            BaseDomain = config.BaseDomain,
            Scheme = config.Scheme,
        };
        _clients = new GeneratedClientFactory(_httpClient, resolvedOptions);

        // Telemetry reporter (null when disabled)
        _metrics = config.DisableTelemetry
            ? null
            : new MetricsReporter(_httpClient, config.Environment, config.Service, appBaseUrl: _appBaseUrl);

        Config = new ConfigClient(_clients, EnsureSharedWebSocket, this, _metrics);
        Flags = new FlagsClient(_clients, _apiKey, EnsureSharedWebSocket, this, _metrics);
        Logging = new LoggingClient(_clients, _apiKey, EnsureSharedWebSocket, this, _metrics);

        var maskedKey = config.ApiKey.Length > 10
            ? config.ApiKey[..10] + "..."
            : config.ApiKey + "...";
        DebugLog.Log("lifecycle", $"SmplClient created (api_key={maskedKey}, environment={config.Environment}, service={config.Service})");
    }

    /// <summary>
    /// Ensures the real-time connection is available.
    /// </summary>
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

    /// <summary>
    /// Releases resources used by this client.
    /// </summary>
    public void Dispose()
    {
        DebugLog.Log("lifecycle", "SmplClient.Dispose() called");
        Flags.Close();
        Logging.Close();

        if (_sharedWs is not null)
        {
            _sharedWs.StopAsync().GetAwaiter().GetResult();
            _sharedWs = null;
        }

        _metrics?.Dispose();

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
