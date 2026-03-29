using Smplkit.Config;
using Smplkit.Internal;

namespace Smplkit;

/// <summary>
/// Top-level client for the smplkit SDK. Provides access to service-specific
/// sub-clients (e.g., <see cref="Config"/>).
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// <code>
/// using var client = new SmplClient(new SmplClientOptions { ApiKey = "sk_api_..." });
/// var config = await client.Config.GetAsync("config-uuid");
/// </code>
/// </para>
/// </remarks>
public sealed class SmplClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Gets the Config service client.
    /// </summary>
    public ConfigClient Config { get; }

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

        // Create a copy of options with the resolved API key for Transport.
        var resolvedOptions = new SmplClientOptions
        {
            ApiKey = resolvedApiKey,
            Timeout = options.Timeout,
        };
        var transport = new Transport(_httpClient, resolvedOptions);
        Config = new ConfigClient(transport, resolvedApiKey);
    }

    /// <summary>
    /// Disposes the underlying HTTP client if it is owned by this instance.
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
