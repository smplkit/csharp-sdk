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

/// <summary>A single name/value HTTP header on a forwarder destination.</summary>
/// <param name="Name">Header name.</param>
/// <param name="Value">Header value (redacted when echoed back on reads).</param>
public sealed record HttpHeader(string Name, string Value);

/// <summary>
/// Forwarder destination HTTP request shape.
///
/// <para><c>SuccessStatus</c> is a 3-character string: an exact code
/// (e.g. <c>"200"</c>) or a class (e.g. <c>"2xx"</c>).</para>
/// </summary>
public sealed class ForwarderHttp
{
    /// <summary>HTTP method to use against the destination. Defaults to <c>POST</c>.</summary>
    public string Method { get; set; } = "POST";
    /// <summary>Destination URL. Must be <c>http</c> or <c>https</c>.</summary>
    public required string Url { get; set; }
    /// <summary>Headers sent to the destination.</summary>
    public IList<HttpHeader> Headers { get; set; } = new List<HttpHeader>();
    /// <summary>Optional body to send. If null, the transformed event payload is sent.</summary>
    public string? Body { get; set; }
    /// <summary>Status code or class that signals delivery success.</summary>
    public string SuccessStatus { get; set; } = "2xx";
}

/// <summary>
/// SIEM streaming forwarder configured on the customer's account.
///
/// <para>Header values returned on reads are always redacted.
/// Re-supply real values when calling update.</para>
/// </summary>
/// <param name="Id">Server-assigned forwarder id.</param>
/// <param name="Name">Customer-supplied display name.</param>
/// <param name="Slug">Server-derived snake_case key, unique per account.</param>
/// <param name="ForwarderType">One of <c>http</c>, <c>datadog</c>, <c>splunk_hec</c>, etc.</param>
/// <param name="Enabled">Whether the forwarder is active.</param>
/// <param name="Filter">Optional JSON Logic expression; events that don't match are filtered out.</param>
/// <param name="Transform">Optional JSONata template applied to the event payload.</param>
/// <param name="Http">Destination HTTP configuration (header values redacted on reads).</param>
/// <param name="CreatedAt">When the forwarder was created.</param>
/// <param name="UpdatedAt">When the forwarder was last updated.</param>
/// <param name="DeletedAt">Soft-delete timestamp, or null.</param>
/// <param name="Version">Optimistic-concurrency version counter.</param>
public sealed record Forwarder(
    Guid Id,
    string Name,
    string Slug,
    ForwarderType ForwarderType,
    bool Enabled,
    IDictionary<string, object?>? Filter,
    string? Transform,
    ForwarderHttp Http,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? DeletedAt,
    int? Version
);

/// <summary>Input for forwarder create and full-replace update.</summary>
public sealed class CreateForwarderInput
{
    /// <summary>Display name. Server derives the slug from this.</summary>
    public required string Name { get; set; }
    /// <summary>The destination type — see <see cref="Smplkit.Audit.ForwarderType"/>.</summary>
    public required ForwarderType ForwarderType { get; set; }
    /// <summary>Destination HTTP configuration.</summary>
    public required ForwarderHttp Http { get; set; }
    /// <summary>Whether the forwarder is active. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Optional JSON Logic filter; non-matching events become <c>filtered_out</c> deliveries.</summary>
    public IDictionary<string, object?>? Filter { get; set; }
    /// <summary>Optional JSONata template applied to the event payload before POST.</summary>
    public string? Transform { get; set; }
}

/// <summary>Filter + pagination input for the forwarders list.</summary>
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
    /// <summary>When true, request total counts in the response meta.</summary>
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
