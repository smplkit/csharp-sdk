namespace Smplkit.Audit;

/// <summary>
/// Public-facing audit event resource — the JSON:API attribute object
/// flattened into a record so callers don't have to traverse the
/// envelope. ADR-047 §2.3.1.
/// </summary>
/// <param name="Id">Server-assigned event id.</param>
/// <param name="EventType"><c>{resource_type}.{verb}</c> per ADR-047 §2.4.</param>
/// <param name="ResourceType">The resource type the event acts on.</param>
/// <param name="ResourceId">Identifier of the affected resource.</param>
/// <param name="Severity">Severity. One of <c>TRACE</c>, <c>DEBUG</c>, <c>INFO</c>, <c>WARN</c>, <c>ERROR</c>, <c>FATAL</c>. Always present on read.</param>
/// <param name="Category">Optional free-form bucket label. Null when not supplied.</param>
/// <param name="OccurredAt">When the event happened in the originating system.</param>
/// <param name="CreatedAt">When the audit service recorded the event.</param>
/// <param name="ActorType">Free-form label for the kind of actor that caused the
/// event (e.g. <c>USER</c>, <c>API_KEY</c>, <c>SYSTEM</c>, or any custom value).
/// Null when not supplied; the audit service never backfills from the request credential.</param>
/// <param name="ActorId">Free-form identifier of the actor — any string scheme is
/// accepted, including non-UUID values. Null when not supplied.</param>
/// <param name="ActorLabel">Human-readable label for the actor (e.g. an email address
/// or API key name). Null when not supplied.</param>
/// <param name="Data">Free-form contextual extras. Any resource snapshot
/// recorded with the event lives inside <c>Data</c>; smplkit's internal
/// convention nests it at <c>Data["snapshot"]</c>, but the shape is
/// unconstrained.</param>
/// <param name="IdempotencyKey">Caller-supplied or server-derived idempotency key.</param>
/// <param name="DoNotForward">When true, the event was recorded but not forwarded to any SIEM forwarder.</param>
/// <param name="Environment">The environment the event was recorded in. Read-only
/// and present on every read — the audit service resolves it when the event is
/// recorded (from a single-environment credential, or from the runtime SDK's
/// configured environment, which the SDK sends on every recording call). Never
/// set on the recording request; <c>null</c> only for an event constructed
/// locally before a server round-trip.</param>
public sealed record AuditEvent(
    Guid Id,
    string EventType,
    string ResourceType,
    string ResourceId,
    string Severity,
    string? Category,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt,
    string? ActorType,
    string? ActorId,
    string? ActorLabel,
    IDictionary<string, object?> Data,
    string IdempotencyKey,
    bool DoNotForward,
    string? Environment = null
);

/// <summary>
/// Input for <see cref="AuditEvents.Record"/>.
///
/// <para><c>ResourceType</c> beginning with <c>smpl.</c> is reserved
/// for smplkit-emitted events; the server returns 403 for customer
/// attempts and the buffer drops the item.</para>
/// </summary>
public sealed class CreateEventInput
{
    /// <summary><c>{resource_type}.{verb}</c>, e.g. <c>"invoice.created"</c>.</summary>
    public required string EventType { get; set; }
    /// <summary>Resource type the event acts on (must NOT start with <c>smpl.</c>).</summary>
    public required string ResourceType { get; set; }
    /// <summary>Identifier of the affected resource.</summary>
    public required string ResourceId { get; set; }
    /// <summary>Severity. One of <c>TRACE</c>, <c>DEBUG</c>, <c>INFO</c>, <c>WARN</c>, <c>ERROR</c>, <c>FATAL</c>. Null records the event at <c>INFO</c>.</summary>
    public string? Severity { get; set; }
    /// <summary>Optional free-form bucket label. Null round-trips as null on read.</summary>
    public string? Category { get; set; }
    /// <summary>Optional. Defaults to server-side <c>now()</c> if null.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
    /// <summary>
    /// Optional free-form label for the kind of actor that caused the event
    /// (e.g. <c>USER</c>, <c>API_KEY</c>, <c>SYSTEM</c>, or any custom value).
    /// The audit service never backfills this from the request credential —
    /// set it explicitly when you want the event attributed.
    /// </summary>
    public string? ActorType { get; set; }
    /// <summary>Optional free-form identifier of the actor. Any string scheme is accepted.</summary>
    public string? ActorId { get; set; }
    /// <summary>Optional human-readable label for the actor (e.g. email or API key name).</summary>
    public string? ActorLabel { get; set; }
    /// <summary>
    /// Optional contextual extras. To record a resource snapshot, nest
    /// it inside <c>Data</c> -- smplkit's internal convention is
    /// <c>Data["snapshot"]</c>, but the shape is unconstrained.
    /// </summary>
    public IDictionary<string, object?>? Data { get; set; }
    /// <summary>Optional. Server derives a content hash if null.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>
    /// When true, the audit service records the event normally but does NOT
    /// POST it through any configured SIEM forwarder. A
    /// <c>skipped_do_not_forward</c> delivery row is recorded for each enabled
    /// forwarder so the skip is visible in the delivery log.
    /// </summary>
    public bool DoNotForward { get; set; }
}

/// <summary>Filters and pagination cursor for <see cref="AuditEvents.ListAsync"/>.</summary>
public sealed class ListEventsInput
{
    /// <summary>Filter by exact-match event type.</summary>
    public string? EventType { get; set; }
    /// <summary>Filter by exact-match resource type.</summary>
    public string? ResourceType { get; set; }
    /// <summary>Filter by exact-match resource id.</summary>
    public string? ResourceId { get; set; }
    /// <summary>Filter by exact-match actor type — the literal string stored on the event.</summary>
    public string? ActorType { get; set; }
    /// <summary>Filter by exact-match actor id — the literal string stored on the event.</summary>
    public string? ActorId { get; set; }
    /// <summary>Filter by exact-match severity. One of <c>TRACE</c>, <c>DEBUG</c>, <c>INFO</c>, <c>WARN</c>, <c>ERROR</c>, <c>FATAL</c>.</summary>
    public string? Severity { get; set; }
    /// <summary>Filter by exact-match category.</summary>
    public string? Category { get; set; }
    /// <summary>Range syntax per ADR-014, e.g. <c>[2026-01-01T00:00:00Z,*)</c>.</summary>
    public string? OccurredAtRange { get; set; }
    /// <summary>Case-insensitive substring match against <c>resource_id</c>.</summary>
    public string? Search { get; set; }
    /// <summary>
    /// Scope results to one or more environment keys (e.g.
    /// <c>["production", "staging"]</c>). When <c>null</c> (the default) or
    /// empty, the filter is omitted and the server scopes to your single
    /// accessible environment. The reserved value <c>"smplkit"</c> selects
    /// platform change events that smplkit records about your own resources.
    /// </summary>
    public IEnumerable<string>? Environments { get; set; }
    /// <summary>
    /// Restrict to events whose <c>do_not_forward</c> flag matches the given
    /// boolean. Forwarder previews typically pass <c>false</c> to match
    /// live-pipeline semantics (events flagged <c>do_not_forward=true</c>
    /// are skipped by the forwarder pipeline). <c>null</c> leaves the
    /// filter unset.
    /// </summary>
    public bool? DoNotForward { get; set; }
    /// <summary>Page size; default 50, max 200 server-side.</summary>
    public int? PageSize { get; set; }
    /// <summary>Opaque cursor returned as <c>NextCursor</c> by the previous page.</summary>
    public string? PageAfter { get; set; }
}

/// <summary>One page of <see cref="AuditEvent"/>s plus the next-page cursor.</summary>
/// <param name="Events">The page's events in <c>-created_at</c> order.</param>
/// <param name="NextCursor">Cursor for the next page, or null on the last page.</param>
public sealed record ListEventsPage(IReadOnlyList<AuditEvent> Events, string? NextCursor);

// ---------------------------------------------------------------------------
// Resource types and event types
// ---------------------------------------------------------------------------

/// <summary>A distinct resource_type slug seen in the account's audit log.</summary>
/// <param name="Id">The resource_type slug (same as the JSON:API id).</param>
/// <param name="CreatedAt">First sighting of this resource_type for the account.</param>
public sealed record ResourceType(string Id, DateTimeOffset CreatedAt);

/// <summary>Pagination input for <see cref="AuditResourceTypes.ListAsync"/>.</summary>
public sealed class ListResourceTypesInput
{
    /// <summary>1-based page number to fetch.</summary>
    public int? PageNumber { get; set; }
    /// <summary>Page size.</summary>
    public int? PageSize { get; set; }
    /// <summary>When true, request total counts in the response meta.</summary>
    public bool? MetaTotal { get; set; }
    /// <summary>
    /// Scope results to one or more environment keys (e.g.
    /// <c>["production", "staging"]</c>). When <c>null</c> (the default) or
    /// empty, the filter is omitted and the server scopes to your single
    /// accessible environment. The reserved value <c>"smplkit"</c> selects
    /// platform change events that smplkit records about your own resources.
    /// </summary>
    public IEnumerable<string>? Environments { get; set; }
}

/// <summary>One page of <see cref="ResourceType"/>s plus the pagination meta block.</summary>
/// <param name="ResourceTypes">The page's resource types.</param>
/// <param name="Pagination">Pagination meta (page, size, and optionally total/total_pages).</param>
public sealed record ListResourceTypesPage(IReadOnlyList<ResourceType> ResourceTypes, Pagination Pagination);

/// <summary>A distinct event type slug seen in the account's audit log.</summary>
/// <param name="Id">The event type slug (same as the JSON:API id).</param>
/// <param name="CreatedAt">First sighting of this event type for the account.</param>
public sealed record AuditEventType(string Id, DateTimeOffset CreatedAt);

/// <summary>Filter + pagination input for <see cref="AuditEventTypes.ListAsync"/>.</summary>
public sealed class ListEventTypesInput
{
    /// <summary>Restrict to event types seen with this resource type.</summary>
    public string? FilterResourceType { get; set; }
    /// <summary>1-based page number to fetch.</summary>
    public int? PageNumber { get; set; }
    /// <summary>Page size.</summary>
    public int? PageSize { get; set; }
    /// <summary>When true, request total counts in the response meta.</summary>
    public bool? MetaTotal { get; set; }
    /// <summary>
    /// Scope results to one or more environment keys (e.g.
    /// <c>["production", "staging"]</c>). When <c>null</c> (the default) or
    /// empty, the filter is omitted and the server scopes to your single
    /// accessible environment. The reserved value <c>"smplkit"</c> selects
    /// platform change events that smplkit records about your own resources.
    /// </summary>
    public IEnumerable<string>? Environments { get; set; }
}

/// <summary>One page of <see cref="AuditEventType"/>s plus the pagination meta block.</summary>
/// <param name="EventTypes">The page's event types.</param>
/// <param name="Pagination">Pagination meta (page, size, and optionally total/total_pages).</param>
public sealed record EventTypeListPage(IReadOnlyList<AuditEventType> EventTypes, Pagination Pagination);

/// <summary>A distinct category value seen in the account's audit log.</summary>
/// <param name="Id">The category value (same as the JSON:API id).</param>
/// <param name="CreatedAt">First sighting of this category for the account.</param>
public sealed record AuditCategory(string Id, DateTimeOffset CreatedAt);

/// <summary>Pagination input for <see cref="AuditCategories.ListAsync"/>.</summary>
public sealed class ListCategoriesInput
{
    /// <summary>1-based page number to fetch.</summary>
    public int? PageNumber { get; set; }
    /// <summary>Page size.</summary>
    public int? PageSize { get; set; }
    /// <summary>When true, request total counts in the response meta.</summary>
    public bool? MetaTotal { get; set; }
    /// <summary>
    /// Scope results to one or more environment keys (e.g.
    /// <c>["production", "staging"]</c>). When <c>null</c> (the default) or
    /// empty, the filter is omitted and the server scopes to your single
    /// accessible environment. The reserved value <c>"smplkit"</c> selects
    /// platform change events that smplkit records about your own resources.
    /// </summary>
    public IEnumerable<string>? Environments { get; set; }
}

/// <summary>One page of <see cref="AuditCategory"/> values plus the pagination meta block.</summary>
/// <param name="Categories">The page's categories.</param>
/// <param name="Pagination">Pagination meta (page, size, and optionally total/total_pages).</param>
public sealed record ListCategoriesPage(IReadOnlyList<AuditCategory> Categories, Pagination Pagination);

// ---------------------------------------------------------------------------
// Forwarders (SIEM streaming) — domain models shared with the management plane
// ---------------------------------------------------------------------------

/// <summary>HTTP verb used by a forwarder's outbound delivery request.</summary>
/// <remarks>Mirrors the audit spec's <c>HttpConfigurationMethod</c> enum.</remarks>
public enum HttpMethod
{
    /// <summary><c>DELETE</c>.</summary>
    Delete,
    /// <summary><c>GET</c>.</summary>
    Get,
    /// <summary><c>PATCH</c>.</summary>
    Patch,
    /// <summary><c>POST</c>.</summary>
    Post,
    /// <summary><c>PUT</c>.</summary>
    Put,
}

/// <summary>Wire-value conversions for <see cref="HttpMethod"/>.</summary>
public static class HttpMethodExtensions
{
    /// <summary>Returns the uppercase wire slug — e.g. <c>"POST"</c>.</summary>
    public static string ToWireValue(this HttpMethod method) => method switch
    {
        HttpMethod.Delete => "DELETE",
        HttpMethod.Get => "GET",
        HttpMethod.Patch => "PATCH",
        HttpMethod.Post => "POST",
        HttpMethod.Put => "PUT",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    /// <summary>Parse a wire-format method slug. Unknown values default to <see cref="HttpMethod.Post"/>.</summary>
    public static HttpMethod FromWireValue(string value) => value?.ToUpperInvariant() switch
    {
        "DELETE" => HttpMethod.Delete,
        "GET" => HttpMethod.Get,
        "PATCH" => HttpMethod.Patch,
        "PUT" => HttpMethod.Put,
        _ => HttpMethod.Post,
    };
}

/// <summary>Template engine used to evaluate a forwarder's <c>Transform</c>.</summary>
/// <remarks>Single-member today (JSONATA). Reserved for future engines.</remarks>
public enum TransformType
{
    /// <summary>JSONata expression — see <see href="https://jsonata.org"/>.</summary>
    Jsonata,
}

/// <summary>Wire-value conversions for <see cref="TransformType"/>.</summary>
public static class TransformTypeExtensions
{
    /// <summary>Returns the uppercase wire slug — e.g. <c>"JSONATA"</c>.</summary>
    public static string ToWireValue(this TransformType type) => type switch
    {
        TransformType.Jsonata => "JSONATA",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Parse a wire-format engine slug. Unknown values throw.</summary>
    public static TransformType FromWireValue(string value) => value?.ToUpperInvariant() switch
    {
        "JSONATA" => TransformType.Jsonata,
        _ => throw new ArgumentException($"Unknown TransformType: {value}", nameof(value)),
    };
}

/// <summary>A single name/value HTTP header on a forwarder destination.</summary>
/// <param name="Name">Header name (e.g. <c>"Authorization"</c>, <c>"DD-API-KEY"</c>).</param>
/// <param name="Value">Header value, plaintext on writes. The audit service encrypts
/// values at rest; reads return them as <c>"&lt;redacted&gt;"</c>.</param>
public sealed record HttpHeader(string Name, string Value);

/// <summary>
/// Per-environment enablement and optional configuration override for a forwarder.
///
/// <para>A forwarder delivers events in a given environment only when that
/// environment has an entry in <see cref="Forwarder.Environments"/> with
/// <see cref="Enabled"/> set to <c>true</c>. An environment with no entry (or
/// <see cref="Enabled"/> = <c>false</c>) receives no deliveries.</para>
/// </summary>
public sealed class ForwarderEnvironment
{
    /// <summary>Whether the forwarder delivers events in this environment.
    /// Defaults to <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Optional per-environment destination configuration that fully
    /// replaces the forwarder's base <see cref="Forwarder.Configuration"/> for
    /// this environment. <c>null</c> (the default) inherits the base
    /// configuration. As with the base configuration, header values are
    /// plaintext on writes and returned redacted on reads — re-supply real
    /// values before <see cref="Forwarder.SaveAsync"/>.</summary>
    public HttpConfiguration? Configuration { get; set; }
}

/// <summary>
/// Forwarder destination HTTP request shape.
///
/// <para><c>SuccessStatus</c> is either an exact status code
/// (e.g. <c>"200"</c>, <c>"204"</c>) or a class (e.g. <c>"2xx"</c>).</para>
/// </summary>
public sealed class HttpConfiguration
{
    /// <summary>HTTP method used for delivery. Defaults to <see cref="HttpMethod.Post"/>.</summary>
    public HttpMethod Method { get; set; } = HttpMethod.Post;
    /// <summary>Destination URL the audit service posts each event to.</summary>
    public required string Url { get; set; }
    /// <summary>Headers attached to every outbound request. Values carry
    /// credentials and are encrypted at rest server-side; reads return them
    /// redacted.</summary>
    public IList<HttpHeader> Headers { get; set; } = new List<HttpHeader>();
    /// <summary>Status code or class that signals delivery success.
    /// Defaults to <c>"2xx"</c>.</summary>
    public string SuccessStatus { get; set; } = "2xx";
    /// <summary>Whether to verify the destination's TLS certificate chain.
    /// Defaults to <c>true</c>; set to <c>false</c> only for short-lived
    /// testing against a destination that serves an untrusted certificate.
    /// Prefer pinning the issuing CA via <see cref="CaCert"/> for long-lived
    /// self-signed setups.</summary>
    public bool TlsVerify { get; set; } = true;
    /// <summary>Optional PEM-encoded certificate (or bundle) trusted in
    /// addition to the system CA store. Ignored when
    /// <see cref="TlsVerify"/> is <c>false</c>. <c>null</c> (the default)
    /// means "use system CAs only".</summary>
    public string? CaCert { get; set; }
}

/// <summary>
/// A SIEM streaming forwarder configured on the customer's account.
///
/// <para>Active-record style: mutate fields directly and call
/// <see cref="SaveAsync"/> to persist, or <see cref="DeleteAsync"/> to remove.
/// Header values in <see cref="Configuration"/>.Headers are always returned
/// redacted on reads — the GET path on the audit API replaces every header
/// value with <c>"&lt;redacted&gt;"</c>. Re-supply the real values before
/// calling <see cref="SaveAsync"/> (the SDK does not cache them client-side).</para>
/// </summary>
public sealed class Forwarder
{
    private readonly ForwardersClient? _client;

    /// <summary>Caller-supplied key for this forwarder. Required at create
    /// time (the audit service does not auto-generate it). <c>null</c> until
    /// <see cref="SaveAsync"/> has run for an unsaved instance constructed
    /// without an id.</summary>
    public string? Id { get; internal set; }
    /// <summary>Display name. Free-form.</summary>
    public string Name { get; set; }
    /// <summary>Destination type — see <see cref="Smplkit.Audit.ForwarderType"/>.</summary>
    public ForwarderType ForwarderType { get; set; }
    /// <summary>Destination request configuration. A per-environment override in
    /// <see cref="Environments"/> replaces this base configuration for that
    /// environment.</summary>
    public HttpConfiguration Configuration { get; set; }
    /// <summary>Read-only. Always <c>false</c> — the base enablement is pinned
    /// off server-side. Whether a forwarder actually delivers is decided per
    /// environment via <see cref="Environments"/>; this field round-trips the
    /// server value but setting it has no effect.</summary>
    public bool Enabled { get; internal set; }
    /// <summary>Per-environment overrides keyed by environment key (e.g.
    /// <c>"production"</c>, <c>"staging"</c>). A forwarder delivers in an
    /// environment only when <c>Environments[env].Enabled</c> is <c>true</c>.
    /// Each entry may carry an optional <see cref="HttpConfiguration"/> override;
    /// omit it to inherit the base <see cref="Configuration"/>. Every referenced
    /// environment must exist and be managed for the account. On update, this is
    /// a full replace for the environments you can manage; overrides for
    /// environments outside your access are preserved server-side.</summary>
    public IDictionary<string, ForwarderEnvironment> Environments { get; set; }
    /// <summary>Optional free-text description.</summary>
    public string? Description { get; set; }
    /// <summary>When <c>true</c>, this forwarder also receives smplkit's own
    /// platform change events (flag, configuration, and similar changes that
    /// smplkit records about your account). Each such event is delivered through
    /// every environment this forwarder is enabled in, using that environment's
    /// resolved configuration. Independent of the per-environment
    /// <see cref="Environments"/> enablement, since platform change events are
    /// not tied to a deployment environment. Defaults to <c>false</c> — platform
    /// change events are not forwarded unless you opt in.</summary>
    public bool ForwardSmplkitEvents { get; set; }
    /// <summary>Optional JSON Logic expression evaluated per event. When set,
    /// events that don't match are recorded as <c>filtered_out</c> deliveries
    /// instead of being POSTed to the destination.</summary>
    public IDictionary<string, object?>? Filter { get; set; }
    /// <summary>Optional template applied to each event before delivery.
    /// Shape depends on <see cref="TransformType"/>; for
    /// <see cref="TransformType.Jsonata"/>, a JSONata expression (string).
    /// Future engines may accept other shapes — the wire field is
    /// untyped, so any value compatible with the chosen engine is accepted
    /// here. <c>null</c> delivers the event JSON as-is.</summary>
    public object? Transform { get; set; }
    /// <summary>Engine used to evaluate <see cref="Transform"/>. Must be
    /// non-null whenever <see cref="Transform"/> is non-null; the SDK
    /// enforces this at construction time and on <see cref="SaveAsync"/>.</summary>
    public TransformType? TransformType { get; set; }
    /// <summary>When the audit service first persisted this forwarder.
    /// <c>null</c> for an unsaved instance.</summary>
    public DateTimeOffset? CreatedAt { get; internal set; }
    /// <summary>When this forwarder was last mutated.</summary>
    public DateTimeOffset? UpdatedAt { get; internal set; }
    /// <summary>Soft-delete timestamp. <c>null</c> for live forwarders.</summary>
    public DateTimeOffset? DeletedAt { get; internal set; }
    /// <summary>Monotonic version counter; bumped on every server-side write.</summary>
    public int? Version { get; internal set; }

    internal Forwarder(
        ForwardersClient? client,
        string name,
        ForwarderType forwarderType,
        HttpConfiguration configuration,
        bool enabled = false,
        IDictionary<string, ForwarderEnvironment>? environments = null,
        string? description = null,
        bool forwardSmplkitEvents = false,
        IDictionary<string, object?>? filter = null,
        object? transform = null,
        TransformType? transformType = null,
        string? id = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? deletedAt = null,
        int? version = null)
    {
        // Validation lives at customer entry points (New + SaveAsync via
        // WrapForwarder), NOT here — FromResource reconstructs instances from
        // wire responses and must tolerate whatever the server sent.
        _client = client;
        Id = id;
        Name = name;
        ForwarderType = forwarderType;
        Configuration = configuration;
        Enabled = enabled;
        Environments = environments ?? new Dictionary<string, ForwarderEnvironment>();
        Description = description;
        ForwardSmplkitEvents = forwardSmplkitEvents;
        Filter = filter;
        Transform = transform;
        TransformType = transformType;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        DeletedAt = deletedAt;
        Version = version;
    }

    /// <summary>
    /// Create or update this forwarder on the server. Upsert behavior is
    /// driven by <see cref="CreatedAt"/>: an unsaved forwarder is POSTed,
    /// otherwise full-replace PUT. After the call, every server-authoritative
    /// field is refreshed from the response.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Forwarder was constructed without a client; cannot save.");
        var refreshed = CreatedAt is null
            ? await _client.SaveCreateAsync(this, ct).ConfigureAwait(false)
            : await _client.SaveUpdateAsync(this, ct).ConfigureAwait(false);
        Apply(refreshed);
    }

    /// <summary>Soft-delete this forwarder on the server.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (_client is null || Id is null)
            throw new InvalidOperationException("Forwarder was constructed without a client or id; cannot delete.");
        return _client.DeleteAsync(Id, ct);
    }

    /// <summary>
    /// Return the override for <paramref name="environment"/>, creating an empty one if absent.
    ///
    /// <para>Mirrors <c>Smplkit.Config.Config.ItemsTarget</c> — the per-environment
    /// mutators reach through here so an existing override's other field is
    /// preserved when only one of <c>Enabled</c> / <c>Configuration</c> is being
    /// set.</para>
    /// </summary>
    private ForwarderEnvironment EnvironmentOverride(string environment)
    {
        if (!Environments.TryGetValue(environment, out var env))
        {
            env = new ForwarderEnvironment();
            Environments[environment] = env;
        }
        return env;
    }

    /// <summary>
    /// Set this forwarder's destination configuration in memory.
    ///
    /// <para>With <paramref name="environment"/> omitted, replaces the base
    /// <see cref="Configuration"/>. With <paramref name="environment"/> given,
    /// sets the per-environment override's configuration on
    /// <see cref="Environments"/>, creating the override entry if it doesn't
    /// exist yet (preserving any already-set <c>Enabled</c> on it). Call
    /// <see cref="SaveAsync"/> to persist.</para>
    /// </summary>
    public void SetConfiguration(HttpConfiguration configuration, string? environment = null)
    {
        if (environment is null)
            Configuration = configuration;
        else
            EnvironmentOverride(environment).Configuration = configuration;
    }

    /// <summary>
    /// Set this forwarder's enablement in memory.
    ///
    /// <para>With <paramref name="environment"/> omitted, sets the base
    /// <see cref="Enabled"/> (which the server pins false regardless —
    /// enablement is per-environment). With <paramref name="environment"/> given,
    /// sets the per-environment override's <c>Enabled</c> on
    /// <see cref="Environments"/>, creating the override entry if it doesn't
    /// exist yet (preserving any already-set <c>Configuration</c> on it). Call
    /// <see cref="SaveAsync"/> to persist.</para>
    /// </summary>
    public void SetEnabled(bool enabled, string? environment = null)
    {
        if (environment is null)
            Enabled = enabled;
        else
            EnvironmentOverride(environment).Enabled = enabled;
    }

    /// <summary>Copy every server-authoritative field from <paramref name="other"/> onto self.</summary>
    internal void Apply(Forwarder other)
    {
        Id = other.Id;
        Name = other.Name;
        ForwarderType = other.ForwarderType;
        Configuration = other.Configuration;
        Enabled = other.Enabled;
        Environments = other.Environments;
        Description = other.Description;
        ForwardSmplkitEvents = other.ForwardSmplkitEvents;
        Filter = other.Filter;
        Transform = other.Transform;
        TransformType = other.TransformType;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
        DeletedAt = other.DeletedAt;
        Version = other.Version;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var enabledIn = string.Join(", ", Environments
            .Where(kv => kv.Value.Enabled)
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal));
        return $"Forwarder(Id={Id}, Name={Name}, EnabledIn=[{enabledIn}])";
    }

    /// <summary>
    /// Enforces the transform/transformType pairing rules:
    /// <list type="bullet">
    ///   <item>If either is set, both must be set.</item>
    ///   <item>When <paramref name="transformType"/> is <see cref="Smplkit.Audit.TransformType.Jsonata"/>,
    ///         <paramref name="transform"/> must be a <see cref="string"/>.</item>
    /// </list>
    /// Called from the constructor and re-checked in the wire-mapping path so
    /// that mutating <see cref="Transform"/> / <see cref="TransformType"/>
    /// after construction is also validated.
    /// </summary>
    internal static void ValidateTransformPairing(object? transform, TransformType? transformType)
    {
        if (transform is not null && transformType is null)
            throw new ArgumentException(
                $"{nameof(transformType)} is required when {nameof(transform)} is set.",
                nameof(transformType));
        if (transform is null && transformType is not null)
            throw new ArgumentException(
                $"{nameof(transform)} is required when {nameof(transformType)} is set.",
                nameof(transform));
        if (transformType == Smplkit.Audit.TransformType.Jsonata && transform is not null && transform is not string)
            throw new ArgumentException(
                $"{nameof(transform)} must be a string when {nameof(transformType)} is JSONATA.",
                nameof(transform));
    }
}

/// <summary>Filter + pagination input for <see cref="Smplkit.Audit.ForwardersClient.ListAsync"/>.</summary>
public sealed class ListForwardersInput
{
    /// <summary>Filter by exact-match forwarder type.</summary>
    public ForwarderType? ForwarderType { get; set; }
    /// <summary>1-based page number to fetch.</summary>
    public int? PageNumber { get; set; }
    /// <summary>Page size.</summary>
    public int? PageSize { get; set; }
    /// <summary>When <c>true</c>, request total counts in the response meta.</summary>
    public bool? MetaTotal { get; set; }
}

/// <summary>One page of <see cref="Forwarder"/>s plus the pagination meta block.</summary>
/// <param name="Forwarders">The page's forwarders.</param>
/// <param name="Pagination">Pagination meta (page, size, and optionally total/total_pages).</param>
public sealed record ListForwardersPage(IReadOnlyList<Forwarder> Forwarders, Pagination Pagination);

/// <summary>Offset-pagination meta returned in JSON:API list responses.</summary>
/// <param name="Page">1-based page number returned.</param>
/// <param name="Size">Number of items per page.</param>
/// <param name="Total">Total matching items across all pages. Present only when the request included <c>MetaTotal=true</c>.</param>
/// <param name="TotalPages">Total pages at the requested page size. Present only when the request included <c>MetaTotal=true</c>.</param>
public sealed record Pagination(int Page, int Size, int? Total, int? TotalPages);
