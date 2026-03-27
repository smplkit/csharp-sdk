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
/// using var client = new SmplkitClient(new SmplkitClientOptions { ApiKey = "sk_api_..." });
/// var config = await client.Config.GetAsync("config-uuid");
/// </code>
/// </para>
/// </remarks>
public sealed class SmplkitClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Gets the Config service client.
    /// </summary>
    public ConfigClient Config { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="SmplkitClient"/> with the specified options.
    /// Creates and owns a new <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="options">Client configuration options.</param>
    public SmplkitClient(SmplkitClientOptions options)
        : this(options, new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SmplkitClient"/> with the specified options
    /// and a caller-provided <see cref="HttpClient"/>. The caller retains ownership and
    /// is responsible for disposing the <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="options">Client configuration options.</param>
    /// <param name="httpClient">An externally managed HTTP client (e.g., for testing).</param>
    public SmplkitClient(SmplkitClientOptions options, HttpClient httpClient)
        : this(options, httpClient, ownsHttpClient: false)
    {
    }

    private SmplkitClient(SmplkitClientOptions options, HttpClient httpClient, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("ApiKey must not be null or empty.", nameof(options));

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;

        var transport = new Transport(_httpClient, options);
        Config = new ConfigClient(transport);
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
