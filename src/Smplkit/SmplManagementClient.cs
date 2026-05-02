using Smplkit.Internal;
using Smplkit.Management;
using ContextRegistrationBuffer = Smplkit.Flags.ContextRegistrationBuffer;

namespace Smplkit;

/// <summary>
/// Top-level client for the smplkit management plane: setup scripts, CI tasks,
/// admin tooling. Provides eight flat namespaces under the management plane.
/// </summary>
/// <remarks>
/// <para>Construction has <b>zero</b> side effects: no service registration, no
/// metrics thread, no WebSocket, no logger discovery. This is the only client
/// you need for CRUD on configs, flags, loggers, log groups, environments,
/// context types, contexts, and account settings.</para>
/// <para>If you need both runtime and management in one process, use
/// <see cref="SmplClient"/> and access the management plane via
/// <c>client.Manage</c>.</para>
/// </remarks>
public sealed class SmplManagementClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly GeneratedClientFactory _clients;
    private readonly string _appBaseUrl;
    private readonly Smplkit.Flags.FlagsClient _runtimeFlags;
    private readonly Smplkit.Config.ConfigClient _runtimeConfig;
    private readonly Smplkit.Logging.LoggingClient _runtimeLogging;

    /// <summary>Gets the <c>contexts</c> namespace — context entity CRUD.</summary>
    public ContextsClient Contexts { get; }

    /// <summary>Gets the <c>context_types</c> namespace — context-type schemas.</summary>
    public ContextTypesClient ContextTypes { get; }

    /// <summary>Gets the <c>environments</c> namespace.</summary>
    public EnvironmentsClient Environments { get; }

    /// <summary>Gets the <c>account_settings</c> namespace — account-level settings.</summary>
    public AccountSettingsClient AccountSettings { get; }

    /// <summary>Gets the <c>config</c> namespace — configuration CRUD (singular, matches runtime <c>client.Config</c>).</summary>
    public ConfigsClient Config { get; }

    /// <summary>Gets the <c>flags</c> namespace — flag CRUD.</summary>
    public FlagsClient Flags { get; }

    /// <summary>Gets the <c>loggers</c> namespace — single-logger CRUD.</summary>
    public LoggersClient Loggers { get; }

    /// <summary>Gets the <c>log_groups</c> namespace — log-group CRUD (separate from loggers).</summary>
    public LogGroupsClient LogGroups { get; }

    /// <summary>
    /// Initializes a new <see cref="SmplManagementClient"/> with automatic config resolution.
    /// </summary>
    public SmplManagementClient()
        : this(new SmplClientOptions(), new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>Initializes a new <see cref="SmplManagementClient"/> with the specified options.</summary>
    public SmplManagementClient(SmplClientOptions options)
        : this(options, new HttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>Initializes a new <see cref="SmplManagementClient"/> with caller-owned <see cref="HttpClient"/>.</summary>
    public SmplManagementClient(SmplClientOptions options, HttpClient httpClient)
        : this(options, httpClient, ownsHttpClient: false)
    {
    }

    private SmplManagementClient(SmplClientOptions options, HttpClient httpClient, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);

        var resolved = ConfigResolver.ResolveForManagement(options);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _appBaseUrl = ConfigResolver.ServiceUrl(resolved.Scheme, "app", resolved.BaseDomain);

        var resolvedOptions = new SmplClientOptions
        {
            ApiKey = resolved.ApiKey,
            Timeout = options.Timeout,
            BaseDomain = resolved.BaseDomain,
            Scheme = resolved.Scheme,
        };
        _clients = new GeneratedClientFactory(_httpClient, resolvedOptions);

        var contextBuffer = new ContextRegistrationBuffer(lruSize: 10_000, flushSize: 100);

        _runtimeFlags = new Smplkit.Flags.FlagsClient(
            _clients, resolved.ApiKey,
            ensureWs: ThrowingWebSocketFactory,
            contextBuffer: contextBuffer);
        _runtimeConfig = new Smplkit.Config.ConfigClient(_clients);
        _runtimeLogging = new Smplkit.Logging.LoggingClient(
            _clients, resolved.ApiKey,
            ensureWs: ThrowingWebSocketFactory);

        Environments = new EnvironmentsClient(_clients.App);
        ContextTypes = new ContextTypesClient(_clients.App);
        Contexts = new ContextsClient(_clients.App, contextBuffer);
        AccountSettings = new AccountSettingsClient(_httpClient, _appBaseUrl);
        Config = new ConfigsClient(_runtimeConfig);
        Flags = new FlagsClient(_runtimeFlags);
        Loggers = new LoggersClient(_runtimeLogging);
        LogGroups = new LogGroupsClient(_runtimeLogging);
    }

    /// <summary>Internal constructor for <see cref="SmplClient.Manage"/> sharing wiring with the runtime client.</summary>
    internal SmplManagementClient(
        HttpClient httpClient,
        GeneratedClientFactory clients,
        string appBaseUrl,
        Smplkit.Flags.FlagsClient runtimeFlags,
        Smplkit.Config.ConfigClient runtimeConfig,
        Smplkit.Logging.LoggingClient runtimeLogging,
        ContextRegistrationBuffer contextBuffer)
    {
        _httpClient = httpClient;
        _ownsHttpClient = false;
        _clients = clients;
        _appBaseUrl = appBaseUrl;
        _runtimeFlags = runtimeFlags;
        _runtimeConfig = runtimeConfig;
        _runtimeLogging = runtimeLogging;

        Environments = new EnvironmentsClient(_clients.App);
        ContextTypes = new ContextTypesClient(_clients.App);
        Contexts = new ContextsClient(_clients.App, contextBuffer);
        AccountSettings = new AccountSettingsClient(_httpClient, _appBaseUrl);
        Config = new ConfigsClient(_runtimeConfig);
        Flags = new FlagsClient(_runtimeFlags);
        Loggers = new LoggersClient(_runtimeLogging);
        LogGroups = new LogGroupsClient(_runtimeLogging);
    }

    private static SharedWebSocket ThrowingWebSocketFactory()
        => throw new InvalidOperationException(
            "WebSocket access is not available on SmplManagementClient. "
            + "Use SmplClient for runtime operations.");

    /// <summary>Releases resources used by this client.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
