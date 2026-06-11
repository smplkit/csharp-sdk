// The Smpl Audit client.
//
// Audit installs no in-process machinery, so it has no runtime/management
// split: one AuditClient exposes the full surface, reachable as
// client.Audit, mgmt.Audit, or standalone:
//
//     audit.Events.Record(new CreateEventInput { ... })
//     audit.Events.FlushAsync(timeout: TimeSpan.FromSeconds(5))
//     audit.Events.ListAsync(...)
//     audit.Events.GetAsync(eventId)
//     audit.ResourceTypes.ListAsync(...)
//     audit.EventTypes.ListAsync(filterResourceType: null, ...)
//     audit.Categories.ListAsync(...)
//     audit.Forwarders.New/GetAsync/ListAsync/SaveAsync/DeleteAsync(...)
//
// The client is genuinely async: reads, discovery, and forwarder CRUD perform
// their network round-trips with await. Only Events.Record is fire-and-forget
// (it enqueues onto a background worker task and returns without awaiting),
// which is the correct shape for the hot path.
//
// By default Record enqueues onto an in-memory bounded buffer
// (Smplkit.Audit.AuditEventBuffer) and returns immediately; the buffer's
// worker task retries with exponential backoff on transient failures and
// drops the oldest item under back pressure. Call FlushAsync when the caller
// needs the event durable before continuing — typically in CLI tools, in-test
// assertions, or any flow about to terminate the process.
//
// All HTTP work is delegated to the auto-generated low-level client under
// Smplkit.Internal.Generated.Audit — the wrapper does not issue raw HTTP
// requests.

using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// The Smpl Audit client.
/// </summary>
/// <remarks>
/// <para>Audit installs no in-process machinery, so it has no runtime/management
/// split: one client exposes the full surface — event recording and reads,
/// distinct-value discovery, and SIEM forwarder CRUD — reachable as
/// <c>client.Audit</c> (<see cref="Smplkit.SmplClient"/>) or constructed
/// directly:</para>
/// <code>
/// await using var audit = new AuditClient(environment: "production");
/// audit.Events.Record(new CreateEventInput
/// {
///     EventType = "invoice.created", ResourceType = "invoice", ResourceId = "inv-1",
/// });
/// await audit.Events.FlushAsync(TimeSpan.FromSeconds(5));
/// foreach (var ft in (await audit.Forwarders.ListAsync()).Forwarders)
///     ...
/// </code>
/// <para>Namespaces: <see cref="Events"/> (record/flush/list/get),
/// <see cref="ResourceTypes"/>, <see cref="EventTypes"/>,
/// <see cref="Categories"/> (discovery), and <see cref="Forwarders"/> (CRUD).</para>
/// </remarks>
public sealed class AuditClient : IAsyncDisposable
{
    private readonly HttpClient? _ownedHttpClient;

    /// <summary>The <c>events</c> sub-client — record/flush/list/get.</summary>
    public AuditEvents Events { get; }

    /// <summary>The <c>resource_types</c> discovery sub-client.</summary>
    public AuditResourceTypes ResourceTypes { get; }

    /// <summary>The <c>event_types</c> discovery sub-client.</summary>
    public AuditEventTypes EventTypes { get; }

    /// <summary>The <c>categories</c> discovery sub-client.</summary>
    public AuditCategories Categories { get; }

    /// <summary>The <c>forwarders</c> sub-client — SIEM forwarder CRUD.</summary>
    public ForwardersClient Forwarders { get; }

    /// <summary>
    /// Initializes a new <see cref="AuditClient"/>.
    /// </summary>
    /// <param name="apiKey">API key. When omitted, resolved from <c>SMPLKIT_API_KEY</c> or <c>~/.smplkit</c>.</param>
    /// <param name="environment">Deployment environment to scope recording and reads to,
    /// sent as <c>X-Smplkit-Environment</c>. Optional — forwarder CRUD and discovery are
    /// environment-agnostic, and reads accept an explicit <c>environments: [...]</c> filter.
    /// When reached via <see cref="Smplkit.SmplClient"/> this is the SDK's configured runtime
    /// environment; via a standalone client without it, recording falls back to the
    /// server-side default environment.</param>
    /// <param name="baseUrl">Full audit-service base URL. Usually resolved from
    /// <paramref name="baseDomain"/>/<paramref name="scheme"/>; supplied directly by the
    /// top-level clients which have already computed it.</param>
    /// <param name="profile">Named <c>~/.smplkit</c> profile section.</param>
    /// <param name="baseDomain">Base domain for API requests (default <c>smplkit.com</c>).</param>
    /// <param name="scheme">URL scheme (default <c>https</c>).</param>
    /// <param name="debug">Enable SDK debug logging.</param>
    /// <param name="extraHeaders">Extra headers attached to every request. An
    /// <c>X-Smplkit-Environment</c> entry here wins over <paramref name="environment"/>.</param>
    public AuditClient(
        string? apiKey = null,
        string? environment = null,
        string? baseUrl = null,
        string? profile = null,
        string? baseDomain = null,
        string? scheme = null,
        bool? debug = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        // Build a standalone audit transport from resolved config. baseUrl/apiKey
        // are used directly when both are supplied (the path a top-level client
        // takes after it has already resolved them); otherwise the management
        // config resolver fills in whatever is missing (~/.smplkit / env vars /
        // defaults). environment is optional — when present it is stamped as
        // X-Smplkit-Environment so event recording and reads scope to it
        // server-side (ADR-055); when absent the client still works (forwarder
        // CRUD and discovery are environment-agnostic, and reads accept an
        // explicit environments: [...] filter).
        var generated = BuildTransport(
            apiKey, baseUrl, environment, profile, baseDomain, scheme, debug, extraHeaders,
            out _ownedHttpClient);
        Events = new AuditEvents(generated);
        ResourceTypes = new AuditResourceTypes(generated);
        EventTypes = new AuditEventTypes(generated);
        Categories = new AuditCategories(generated);
        Forwarders = new ForwardersClient(generated);
    }

    /// <summary>
    /// Internal — wired by a top-level client (<see cref="Smplkit.SmplClient"/>)
    /// from the audit base URL and api key it has already resolved, plus the SDK's
    /// configured runtime <paramref name="environment"/>. The audit surface builds
    /// its own generated transport over a fresh <see cref="HttpClient"/>, owning it
    /// so this instance's <see cref="DisposeAsync"/> tears it down.
    /// </summary>
    internal AuditClient(
        string apiKey,
        string baseUrl,
        string? environment,
        IReadOnlyDictionary<string, string>? extraHeaders)
        : this(apiKey: apiKey, environment: environment, baseUrl: baseUrl, profile: null, extraHeaders: extraHeaders)
    {
    }

    /// <summary>
    /// Resolve the (audit base URL, api key) and build a generated audit client over
    /// a fresh <see cref="HttpClient"/> that stamps <c>X-Smplkit-Environment</c>
    /// (ADR-055) when <paramref name="environment"/> is set.
    /// </summary>
    private static GenAudit.AuditClient BuildTransport(
        string? apiKey,
        string? baseUrl,
        string? environment,
        string? profile,
        string? baseDomain,
        string? scheme,
        bool? debug,
        IReadOnlyDictionary<string, string>? extraHeaders,
        out HttpClient ownedHttpClient)
    {
        string resolvedKey;
        string auditUrl;
        if (apiKey is not null && baseUrl is not null)
        {
            resolvedKey = apiKey;
            auditUrl = baseUrl;
        }
        else
        {
            var resolved = ConfigResolver.ResolveForManagement(new SmplClientOptions
            {
                ApiKey = apiKey,
                Profile = profile,
                BaseDomain = baseDomain,
                Scheme = scheme,
                Debug = debug,
            });
            if (resolved.Debug)
                Internal.Debug.Enabled = true;
            resolvedKey = apiKey ?? resolved.ApiKey;
            auditUrl = baseUrl ?? ConfigResolver.ServiceUrl(resolved.Scheme, "audit", resolved.BaseDomain);
        }

        ownedHttpClient = new HttpClient();
        // The shared generated-client factory configures the bearer token, the
        // JSON:API Accept header, the user agent, and any extra headers, then
        // hands back the generated Audit client bound to the resolved base URL.
        var clients = new GeneratedClientFactory(ownedHttpClient, new SmplClientOptions
        {
            ApiKey = resolvedKey,
            BaseDomain = ParseDomain(auditUrl, out var parsedScheme),
            Scheme = parsedScheme,
            Environment = environment,
            ExtraHeaders = extraHeaders is null ? null : new Dictionary<string, string>(extraHeaders),
        });
        // Runtime audit ops carry the configured environment as
        // X-Smplkit-Environment (ADR-055); the factory's AuditRuntime instance
        // stamps it per request, while forwarder CRUD and discovery — which are
        // environment-agnostic — tolerate the header being present.
        var generated = clients.AuditRuntime;
        // Pin the resolved audit base URL directly: the factory derives per-service
        // URLs from base domain + scheme, but a caller-supplied full base URL (the
        // top-level-client path) must be honored verbatim.
        generated.BaseUrl = auditUrl;
        return generated;
    }

    /// <summary>
    /// Split a full audit base URL (e.g. <c>https://audit.smplkit.com</c>) back into
    /// the (base domain, scheme) the generated-client factory expects, so the factory
    /// reconstructs the same audit URL. The base URL is re-pinned on the returned
    /// client regardless, so this only needs to round-trip the scheme.
    /// </summary>
    private static string ParseDomain(string baseUrl, out string scheme)
    {
        var idx = baseUrl.IndexOf("://", StringComparison.Ordinal);
        scheme = idx >= 0 ? baseUrl.Substring(0, idx) : "https";
        var host = idx >= 0 ? baseUrl.Substring(idx + 3) : baseUrl;
        host = host.TrimEnd('/');
        // Strip the leading "audit." sub-domain so the factory's audit.{domain}
        // composition reproduces the host. When the host has no such prefix
        // (e.g. a bare localhost test target), the BaseUrl re-pin below corrects it.
        const string prefix = "audit.";
        return host.StartsWith(prefix, StringComparison.Ordinal) ? host.Substring(prefix.Length) : host;
    }

    /// <summary>
    /// Release HTTP resources — the audit surface always owns its transport, so
    /// this drains the event buffer and tears the <see cref="HttpClient"/> down.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await Events.DisposeAsync().ConfigureAwait(false);
        _ownedHttpClient?.Dispose();
    }
}
