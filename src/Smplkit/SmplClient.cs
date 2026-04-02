using Smplkit.Config;
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
/// using var client = new SmplClient(new SmplClientOptions { ApiKey = "sk_api_..." });
/// var config = await client.Config.GetAsync("config-uuid");
/// var flag = await client.Flags.CreateAsync("my-flag", "My Flag", FlagType.Boolean, false);
/// </code>
/// </para>
/// </remarks>
public sealed class SmplClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private SharedWebSocket? _sharedWs;
    private readonly object _wsLock = new();

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

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _apiKey = resolvedApiKey;

        // Create a copy of options with the resolved API key for Transport.
        var resolvedOptions = new SmplClientOptions
        {
            ApiKey = resolvedApiKey,
            Timeout = options.Timeout,
        };
        var transport = new Transport(_httpClient, resolvedOptions);
        Config = new ConfigClient(transport, resolvedApiKey);
        Flags = new FlagsClient(transport, resolvedApiKey, EnsureSharedWebSocket);
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
