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
    /// Gets the environment key (e.g. "production", "staging"). Optional.
    /// When <c>null</c>, the SDK falls back to the <c>SMPLKIT_ENVIRONMENT</c>
    /// environment variable or the <c>~/.smplkit</c> configuration file.
    /// When unset everywhere, the SDK sends no environment signal and the
    /// server derives the environment from the API key — an audit-only or
    /// jobs-only client needs none.
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Gets the service identifier for automatic context injection. Optional.
    /// When <c>null</c>, the SDK falls back to the <c>SMPLKIT_SERVICE</c>
    /// environment variable or the <c>~/.smplkit</c> configuration file.
    /// When unset everywhere, no service signal is sent.
    /// </summary>
    public string? Service { get; init; }

    /// <summary>
    /// Gets a value indicating whether anonymous SDK usage telemetry is enabled.
    /// When <c>null</c>, the SDK resolves this from the <c>SMPLKIT_TELEMETRY</c>
    /// environment variable or the <c>~/.smplkit</c> configuration file.
    /// Defaults to <c>true</c> if unset everywhere; set to <c>false</c> to disable.
    /// </summary>
    public bool? Telemetry { get; init; }

    /// <summary>
    /// Gets the configuration profile name to read from <c>~/.smplkit</c>.
    /// When <c>null</c>, the SDK falls back to the <c>SMPLKIT_PROFILE</c>
    /// environment variable, then <c>"default"</c>.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Gets the base domain used to construct service URLs.
    /// When <c>null</c>, the SDK resolves this from the <c>SMPLKIT_BASE_DOMAIN</c>
    /// environment variable or the <c>~/.smplkit</c> configuration file.
    /// Defaults to <c>"smplkit.com"</c> if unset everywhere, producing URLs such as
    /// <c>https://config.smplkit.com</c> and <c>https://flags.smplkit.com</c>.
    /// </summary>
    public string? BaseDomain { get; init; }

    /// <summary>
    /// Gets the URL scheme used when constructing service URLs.
    /// When <c>null</c>, the SDK resolves this from the <c>SMPLKIT_SCHEME</c>
    /// environment variable or the <c>~/.smplkit</c> configuration file.
    /// Defaults to <c>"https"</c> if unset everywhere.
    /// </summary>
    public string? Scheme { get; init; }

    /// <summary>
    /// Gets a value indicating whether debug logging is enabled.
    /// When <c>null</c>, the SDK resolves this from the <c>SMPLKIT_DEBUG</c>
    /// environment variable or the <c>~/.smplkit</c> configuration file.
    /// Defaults to <c>false</c> if unset everywhere.
    /// </summary>
    public bool? Debug { get; init; }

    /// <summary>
    /// Gets additional HTTP headers sent on every request.
    /// SDK-owned headers (Authorization, Accept, Content-Type) take precedence
    /// and cannot be overridden through this property. A <c>User-Agent</c>
    /// entry (any casing) replaces the SDK's default
    /// <c>smplkit-sdk-csharp/&lt;version&gt;</c> User-Agent.
    /// </summary>
    public IDictionary<string, string>? ExtraHeaders { get; init; }
}
