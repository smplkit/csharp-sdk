namespace Smplkit;

/// <summary>
/// Configuration options for <see cref="SmplClient"/>.
/// </summary>
public sealed class SmplClientOptions
{
    /// <summary>
    /// Gets the API key used for authenticating with the smplkit platform.
    /// When <c>null</c>, the SDK resolves it from the <c>SMPLKIT_API_KEY</c>
    /// environment variable or the <c>~/.smplkit</c> configuration file.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the HTTP request timeout.
    /// Defaults to 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the environment key (e.g. "production", "staging").
    /// When <c>null</c>, the SDK falls back to the <c>SMPLKIT_ENVIRONMENT</c>
    /// environment variable. If neither is set, the <see cref="SmplClient"/>
    /// constructor throws.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Gets the service identifier for automatic context injection.
    /// When <c>null</c>, the SDK falls back to the <c>SMPLKIT_SERVICE</c>
    /// environment variable. A <c>null</c> service is valid (no auto-injection).
    /// </summary>
    public string? Service { get; init; }
}
