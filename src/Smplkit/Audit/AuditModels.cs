namespace Smplkit.Audit;

/// <summary>
/// Public-facing audit event resource — the JSON:API attribute object
/// flattened into a record so callers don't have to traverse the
/// envelope. ADR-047 §2.3.1.
/// </summary>
/// <param name="Id">Server-assigned event id.</param>
/// <param name="Action"><c>{resource_type}.{verb}</c> per ADR-047 §2.4.</param>
/// <param name="ResourceType">The resource type the event acts on.</param>
/// <param name="ResourceId">Identifier of the affected resource.</param>
/// <param name="OccurredAt">When the event happened in the originating system.</param>
/// <param name="CreatedAt">When the audit service recorded the event.</param>
/// <param name="ActorType"><c>USER</c>, <c>API_KEY</c>, or <c>SYSTEM</c>.</param>
/// <param name="ActorId">UUID of the user or API key; null for <c>API_KEY</c> or <c>SYSTEM</c>.</param>
/// <param name="ActorLabel">Denormalized display string captured at write time.</param>
/// <param name="Data">Free-form contextual extras. Any resource snapshot
/// recorded with the event lives inside <c>Data</c>; smplkit's internal
/// convention nests it at <c>Data["snapshot"]</c>, but the shape is
/// unconstrained.</param>
/// <param name="IdempotencyKey">Caller-supplied or server-derived idempotency key.</param>
/// <param name="DoNotForward">When true, the event was recorded but not forwarded to any SIEM forwarder.</param>
public sealed record AuditEvent(
    Guid Id,
    string Action,
    string ResourceType,
    string ResourceId,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt,
    string ActorType,
    Guid? ActorId,
    string ActorLabel,
    IDictionary<string, object?> Data,
    string IdempotencyKey,
    bool DoNotForward
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
    public required string Action { get; set; }
    /// <summary>Resource type the event acts on (must NOT start with <c>smpl.</c>).</summary>
    public required string ResourceType { get; set; }
    /// <summary>Identifier of the affected resource.</summary>
    public required string ResourceId { get; set; }
    /// <summary>Optional. Defaults to server-side <c>now()</c> if null.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
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
    /// <summary>Filter by exact-match action.</summary>
    public string? Action { get; set; }
    /// <summary>Filter by exact-match resource type.</summary>
    public string? ResourceType { get; set; }
    /// <summary>Filter by exact-match resource id.</summary>
    public string? ResourceId { get; set; }
    /// <summary>Filter by exact-match actor type (<c>USER</c>, <c>API_KEY</c>, etc.).</summary>
    public string? ActorType { get; set; }
    /// <summary>Filter by exact-match actor UUID.</summary>
    public Guid? ActorId { get; set; }
    /// <summary>Range syntax per ADR-014, e.g. <c>[2026-01-01T00:00:00Z,*)</c>.</summary>
    public string? OccurredAtRange { get; set; }
    /// <summary>Case-insensitive substring match against <c>resource_id</c>.</summary>
    public string? Search { get; set; }
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
// Resource types and actions
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
}

/// <summary>One page of <see cref="ResourceType"/>s plus the pagination meta block.</summary>
/// <param name="ResourceTypes">The page's resource types.</param>
/// <param name="Pagination">Pagination meta (page, size, and optionally total/total_pages).</param>
public sealed record ListResourceTypesPage(IReadOnlyList<ResourceType> ResourceTypes, Pagination Pagination);

/// <summary>A distinct action slug seen in the account's audit log.</summary>
/// <param name="Id">The action slug (same as the JSON:API id).</param>
/// <param name="CreatedAt">First sighting of this action for the account.</param>
public sealed record AuditAction(string Id, DateTimeOffset CreatedAt);

/// <summary>Filter + pagination input for <see cref="AuditActions.ListAsync"/>.</summary>
public sealed class ListActionsInput
{
    /// <summary>Restrict to actions seen with this resource type.</summary>
    public string? FilterResourceType { get; set; }
    /// <summary>1-based page number to fetch.</summary>
    public int? PageNumber { get; set; }
    /// <summary>Page size.</summary>
    public int? PageSize { get; set; }
    /// <summary>When true, request total counts in the response meta.</summary>
    public bool? MetaTotal { get; set; }
}

/// <summary>One page of <see cref="AuditAction"/>s plus the pagination meta block.</summary>
/// <param name="Actions">The page's actions.</param>
/// <param name="Pagination">Pagination meta (page, size, and optionally total/total_pages).</param>
public sealed record ListActionsPage(IReadOnlyList<AuditAction> Actions, Pagination Pagination);

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
    private readonly Smplkit.Management.ManagementForwardersClient? _client;

    /// <summary>Server-assigned UUID. <c>null</c> until <see cref="SaveAsync"/> has run for the first time.</summary>
    public Guid? Id { get; internal set; }
    /// <summary>Display name. Free-form.</summary>
    public string Name { get; set; }
    /// <summary>Destination type — see <see cref="Smplkit.Audit.ForwarderType"/>.</summary>
    public ForwarderType ForwarderType { get; set; }
    /// <summary>Destination request configuration.</summary>
    public HttpConfiguration Configuration { get; set; }
    /// <summary>When <c>false</c>, the audit service skips delivery for this
    /// forwarder but still records <c>filtered_out</c> deliveries.</summary>
    public bool Enabled { get; set; }
    /// <summary>Optional free-text description.</summary>
    public string? Description { get; set; }
    /// <summary>Optional JSON Logic expression evaluated per event. When set,
    /// events that don't match are recorded as <c>filtered_out</c> deliveries
    /// instead of being POSTed to the destination.</summary>
    public IDictionary<string, object?>? Filter { get; set; }
    /// <summary>Optional template applied to each event before delivery.
    /// Shape depends on <see cref="TransformType"/>; for
    /// <see cref="TransformType.Jsonata"/>, a JSONata expression. <c>null</c>
    /// delivers the event JSON as-is.</summary>
    public string? Transform { get; set; }
    /// <summary>Engine used to evaluate <see cref="Transform"/>. Set
    /// automatically by <see cref="SaveAsync"/> when <see cref="Transform"/>
    /// is non-null.</summary>
    public TransformType? TransformType { get; internal set; }
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
        Smplkit.Management.ManagementForwardersClient? client,
        string name,
        ForwarderType forwarderType,
        HttpConfiguration configuration,
        bool enabled = true,
        string? description = null,
        IDictionary<string, object?>? filter = null,
        string? transform = null,
        TransformType? transformType = null,
        Guid? id = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? deletedAt = null,
        int? version = null)
    {
        _client = client;
        Id = id;
        Name = name;
        ForwarderType = forwarderType;
        Configuration = configuration;
        Enabled = enabled;
        Description = description;
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
        return _client.DeleteAsync(Id.Value, ct);
    }

    /// <summary>Copy every server-authoritative field from <paramref name="other"/> onto self.</summary>
    internal void Apply(Forwarder other)
    {
        Id = other.Id;
        Name = other.Name;
        ForwarderType = other.ForwarderType;
        Configuration = other.Configuration;
        Enabled = other.Enabled;
        Description = other.Description;
        Filter = other.Filter;
        Transform = other.Transform;
        TransformType = other.TransformType;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
        DeletedAt = other.DeletedAt;
        Version = other.Version;
    }

    /// <inheritdoc/>
    public override string ToString() => $"Forwarder(Id={Id}, Name={Name}, Enabled={Enabled})";
}

/// <summary>Filter + pagination input for <see cref="Smplkit.Management.ManagementForwardersClient.ListAsync"/>.</summary>
public sealed class ListForwardersInput
{
    /// <summary>Filter by exact-match forwarder type.</summary>
    public ForwarderType? ForwarderType { get; set; }
    /// <summary>Filter by enabled flag.</summary>
    public bool? Enabled { get; set; }
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
