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

// ---------------------------------------------------------------------------
// Forwarders (SIEM streaming, Pro tier)
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
/// Re-supply real values when calling
/// <see cref="AuditForwarders.UpdateAsync"/>.</para>
/// </summary>
/// <param name="Id">Server-assigned forwarder id.</param>
/// <param name="Name">Customer-supplied display name.</param>
/// <param name="Slug">Server-derived snake_case key, unique per account.</param>
/// <param name="ForwarderType">One of <c>http</c>, <c>datadog</c>, <c>splunk_hec</c>, etc.</param>
/// <param name="Enabled">Whether the forwarder is active.</param>
/// <param name="Filter">Optional JSON Logic expression; events that don't match are filtered out.</param>
/// <param name="Transform">Optional JSONata template applied to the event payload.</param>
/// <param name="Http">Destination HTTP configuration (header values redacted on reads).</param>
/// <param name="Data">Free-form attributes JSON.</param>
/// <param name="CreatedAt">When the forwarder was created.</param>
/// <param name="UpdatedAt">When the forwarder was last updated.</param>
/// <param name="DeletedAt">Soft-delete timestamp, or null.</param>
/// <param name="Version">Optimistic-concurrency version counter.</param>
public sealed record Forwarder(
    Guid Id,
    string Name,
    string Slug,
    string ForwarderType,
    bool Enabled,
    IDictionary<string, object?>? Filter,
    string? Transform,
    ForwarderHttp Http,
    IDictionary<string, object?> Data,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? DeletedAt,
    int? Version
);

/// <summary>Input for <see cref="AuditForwarders.CreateAsync"/> and <see cref="AuditForwarders.UpdateAsync"/>.</summary>
public sealed class CreateForwarderInput
{
    /// <summary>Display name. Server derives the slug from this.</summary>
    public required string Name { get; set; }
    /// <summary>One of <c>http</c>, <c>datadog</c>, <c>splunk_hec</c>, <c>sumo_logic</c>, <c>new_relic</c>, <c>honeycomb</c>, <c>elastic</c>.</summary>
    public required string ForwarderType { get; set; }
    /// <summary>Destination HTTP configuration.</summary>
    public required ForwarderHttp Http { get; set; }
    /// <summary>Whether the forwarder is active. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Optional JSON Logic filter; non-matching events become <c>filtered_out</c> deliveries.</summary>
    public IDictionary<string, object?>? Filter { get; set; }
    /// <summary>Optional JSONata template applied to the event payload before POST.</summary>
    public string? Transform { get; set; }
    /// <summary>Optional free-form attributes.</summary>
    public IDictionary<string, object?>? Data { get; set; }
}

/// <summary>Filter + pagination input for the forwarders list.</summary>
public sealed class ListForwardersInput
{
    /// <summary>Filter by exact-match forwarder type.</summary>
    public string? ForwarderType { get; set; }
    /// <summary>Filter by enabled flag.</summary>
    public bool? Enabled { get; set; }
    /// <summary>Page size.</summary>
    public int? PageSize { get; set; }
    /// <summary>Opaque cursor returned by the previous page.</summary>
    public string? PageAfter { get; set; }
}

/// <summary>One page of <see cref="Forwarder"/>s plus the next-page cursor.</summary>
/// <param name="Forwarders">The page's forwarders.</param>
/// <param name="NextCursor">Cursor for the next page, or null on the last page.</param>
public sealed record ListForwardersPage(IReadOnlyList<Forwarder> Forwarders, string? NextCursor);

/// <summary>Read-only delivery row from the forwarder delivery log.</summary>
/// <param name="Id">Server-assigned delivery row id.</param>
/// <param name="ForwarderId">Forwarder this delivery belongs to.</param>
/// <param name="EventId">Audit event this delivery was for.</param>
/// <param name="AttemptNumber">1 for the in-line attempt; >1 for manual retries.</param>
/// <param name="Status">One of <c>succeeded</c>, <c>failed</c>, <c>filtered_out</c>, <c>skipped_do_not_forward</c>.</param>
/// <param name="Request">The rendered HTTP request, with header values redacted.</param>
/// <param name="ResponseStatus">Destination HTTP status code, or null on network errors / filter / skip.</param>
/// <param name="ResponseBody">Captured response body (truncated to 4 KB).</param>
/// <param name="LatencyMs">Round-trip time for the destination call.</param>
/// <param name="Error">Error message for failed attempts.</param>
/// <param name="CreatedAt">When this delivery row was recorded.</param>
public sealed record ForwarderDelivery(
    Guid Id,
    Guid ForwarderId,
    Guid EventId,
    int AttemptNumber,
    string Status,
    IDictionary<string, object?>? Request,
    int? ResponseStatus,
    string? ResponseBody,
    int? LatencyMs,
    string? Error,
    DateTimeOffset? CreatedAt
);

/// <summary>Filter + pagination input for the per-forwarder delivery log.</summary>
public sealed class ListDeliveriesInput
{
    /// <summary>One of <c>succeeded</c>, <c>failed</c>, <c>filtered_out</c>, <c>skipped_do_not_forward</c>.</summary>
    public string? Status { get; set; }
    /// <summary>Range syntax per ADR-014, e.g. <c>"[2026-01-01T00:00:00Z,*)"</c>.</summary>
    public string? CreatedAtRange { get; set; }
    /// <summary>Page size.</summary>
    public int? PageSize { get; set; }
    /// <summary>Opaque cursor returned by the previous page.</summary>
    public string? PageAfter { get; set; }
}

/// <summary>One page of <see cref="ForwarderDelivery"/> rows plus the next-page cursor.</summary>
/// <param name="Deliveries">The page's delivery rows.</param>
/// <param name="NextCursor">Cursor for the next page, or null on the last page.</param>
public sealed record ListDeliveriesPage(IReadOnlyList<ForwarderDelivery> Deliveries, string? NextCursor);

/// <summary>Summary returned by <see cref="AuditForwarders.RetryFailedDeliveriesAsync"/>.</summary>
/// <param name="Attempted">Total deliveries re-attempted.</param>
/// <param name="Succeeded">How many of the retries succeeded.</param>
/// <param name="Failed">How many of the retries failed again.</param>
public sealed record RetryFailedDeliveriesSummary(int Attempted, int Succeeded, int Failed);

/// <summary>Input for <see cref="AuditFunctions.ExecuteTestForwarderAsync"/>.</summary>
public sealed class TestForwarderInput
{
    /// <summary>Destination URL.</summary>
    public required string Url { get; set; }
    /// <summary>HTTP method. Defaults to <c>POST</c>.</summary>
    public string Method { get; set; } = "POST";
    /// <summary>Headers sent to the destination.</summary>
    public IList<HttpHeader> Headers { get; set; } = new List<HttpHeader>();
    /// <summary>Body to send.</summary>
    public string? Body { get; set; }
    /// <summary>Status code or class that signals success. Defaults to <c>"2xx"</c>.</summary>
    public string SuccessStatus { get; set; } = "2xx";
    /// <summary>Capped at 30s server-side.</summary>
    public int? TimeoutMs { get; set; }
}

/// <summary>Plain-JSON response from the test_forwarder/execute proxy.</summary>
/// <param name="Succeeded">Whether the destination's response matched <c>SuccessStatus</c>.</param>
/// <param name="ResponseStatus">HTTP status returned by the destination, or null on network errors.</param>
/// <param name="ResponseHeaders">Response headers echoed back unredacted (for debugging).</param>
/// <param name="ResponseBody">Captured response body (truncated to 64 KB).</param>
/// <param name="LatencyMs">Round-trip time for the destination call.</param>
/// <param name="Error">Error message for failed / blocked attempts (e.g. SSRF rejection).</param>
public sealed record TestForwarderResult(
    bool Succeeded,
    int? ResponseStatus,
    IDictionary<string, string> ResponseHeaders,
    string ResponseBody,
    int? LatencyMs,
    string? Error
);

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
    /// <summary>Page size; default 50, max 200 server-side.</summary>
    public int? PageSize { get; set; }
    /// <summary>Opaque cursor returned as <c>NextCursor</c> by the previous page.</summary>
    public string? PageAfter { get; set; }
}

/// <summary>One page of <see cref="AuditEvent"/>s plus the next-page cursor.</summary>
/// <param name="Events">The page's events in <c>-created_at</c> order.</param>
/// <param name="NextCursor">Cursor for the next page, or null on the last page.</param>
public sealed record ListEventsPage(IReadOnlyList<AuditEvent> Events, string? NextCursor);
