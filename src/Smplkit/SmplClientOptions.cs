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
    /// environment variable. If neither is set, the <see cref="SmplClient"/>
    /// constructor throws.
    /// </summary>
    public string? Service { get; init; }

    /// <summary>
    /// Gets a value indicating whether SDK telemetry reporting is disabled.
    /// Defaults to <c>false</c>. When set to <c>true</c>, no usage metrics
    /// are collected or transmitted.
    /// </summary>
    public bool DisableTelemetry { get; init; }

    /// <summary>
    /// Gets the base domain used to construct service URLs.
    /// Defaults to <c>"smplkit.com"</c>, which produces URLs such as
    /// <c>https://config.smplkit.com</c> and <c>https://flags.smplkit.com</c>.
    /// Override this to target a self-hosted or alternate deployment.
    /// </summary>
    public string BaseDomain { get; init; } = "smplkit.com";

    /// <summary>
    /// Gets the URL scheme used when constructing service URLs.
    /// Defaults to <c>"https"</c>. Set to <c>"http"</c> for local development
    /// or environments without TLS.
    /// </summary>
    public string Scheme { get; init; } = "https";
}
