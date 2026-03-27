namespace Smplkit;

/// <summary>
/// Configuration options for <see cref="SmplkitClient"/>.
/// </summary>
public sealed class SmplkitClientOptions
{
    /// <summary>
    /// Gets the API key used for authenticating with the smplkit platform.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Gets the base URL for the smplkit Config API.
    /// Defaults to <c>https://config.smplkit.com</c>.
    /// </summary>
    public string BaseUrl { get; init; } = "https://config.smplkit.com";

    /// <summary>
    /// Gets the HTTP request timeout.
    /// Defaults to 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
