using Smplkit.Internal;
using Smplkit.Management;
using ContextRegistrationBuffer = Smplkit.Flags.ContextRegistrationBuffer;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit;

/// <summary>
/// Top-level client for the smplkit management plane: setup scripts, CI tasks,
/// admin tooling. Provides eight flat namespaces under the management plane.
/// </summary>
/// <remarks>
/// <para>Construction has <b>zero</b> side effects: no service registration, no
/// metrics thread, no WebSocket, no logger discovery, no outbound HTTP traffic.
/// This is the only client you need for CRUD on configs, flags, loggers, log
/// groups, environments, context types, contexts, and account settings.</para>
/// <para>If you need both runtime and management in one process, use
/// <see cref="SmplClient"/> and access the management plane via
/// <c>client.Manage</c>. The runtime and management clients are <b>peers</b> —
/// neither owns the other's transports. They share an <see cref="HttpClient"/>
/// and a context registration buffer when constructed via
/// <see cref="SmplClient.Manage"/>; otherwise each owns its own.</para>
/// </remarks>
public sealed class SmplManagementClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

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

    /// <summary>Gets the audit management namespace — SIEM forwarder CRUD.</summary>
    public AuditManagementClient Audit { get; }

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
        var appBaseUrl = ConfigResolver.ServiceUrl(resolved.Scheme, "app", resolved.BaseDomain);

        var resolvedOptions = new SmplClientOptions
        {
            ApiKey = resolved.ApiKey,
            Timeout = options.Timeout,
            BaseDomain = resolved.BaseDomain,
            Scheme = resolved.Scheme,
        };
        var clients = new GeneratedClientFactory(_httpClient, resolvedOptions);

        var contextBuffer = new ContextRegistrationBuffer(lruSize: 10_000, flushSize: 100);

        Environments = new EnvironmentsClient(clients.App);
        ContextTypes = new ContextTypesClient(clients.App);
        Contexts = new ContextsClient(clients.App, contextBuffer);
        AccountSettings = new AccountSettingsClient(_httpClient, appBaseUrl);
        Config = new ConfigsClient(clients);
        Flags = new FlagsClient(clients);
        Loggers = new LoggersClient(clients);
        LogGroups = new LogGroupsClient(clients);
        Audit = new AuditManagementClient(clients.Audit);
    }

    /// <summary>
    /// Internal constructor used by <see cref="SmplClient.Manage"/> to share the
    /// runtime client's <see cref="HttpClient"/>, generated factory, and context
    /// registration buffer. The management client does not hold or wrap any
    /// runtime sub-client — this is purely transport sharing.
    /// </summary>
    internal SmplManagementClient(
        HttpClient httpClient,
        GeneratedClientFactory clients,
        string appBaseUrl,
        ContextRegistrationBuffer contextBuffer)
    {
        _httpClient = httpClient;
        _ownsHttpClient = false;

        Environments = new EnvironmentsClient(clients.App);
        ContextTypes = new ContextTypesClient(clients.App);
        Contexts = new ContextsClient(clients.App, contextBuffer);
        AccountSettings = new AccountSettingsClient(_httpClient, appBaseUrl);
        Config = new ConfigsClient(clients);
        Flags = new FlagsClient(clients);
        Loggers = new LoggersClient(clients);
        LogGroups = new LogGroupsClient(clients);
        Audit = new AuditManagementClient(clients.Audit);
    }

    /// <summary>Releases resources used by this client.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
