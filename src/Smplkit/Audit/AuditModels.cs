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
/// <param name="Snapshot">Full state snapshot, or null.</param>
/// <param name="Data">Free-form contextual extras.</param>
/// <param name="IdempotencyKey">Caller-supplied or server-derived idempotency key.</param>
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
    IDictionary<string, object?>? Snapshot,
    IDictionary<string, object?> Data,
    string IdempotencyKey
);

/// <summary>
/// Input for <see cref="AuditEvents.Create"/>.
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
    /// <summary>Optional full resource snapshot (ADR-047 §2.5).</summary>
    public IDictionary<string, object?>? Snapshot { get; set; }
    /// <summary>Optional contextual extras (request id, IP, etc.).</summary>
    public IDictionary<string, object?>? Data { get; set; }
    /// <summary>Optional. Server derives a content hash if null.</summary>
    public string? IdempotencyKey { get; set; }
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
    /// <summary>Page size; default 50, max 200 server-side.</summary>
    public int? PageSize { get; set; }
    /// <summary>Opaque cursor returned as <c>NextCursor</c> by the previous page.</summary>
    public string? PageAfter { get; set; }
}

/// <summary>One page of <see cref="AuditEvent"/>s plus the next-page cursor.</summary>
/// <param name="Events">The page's events in <c>-created_at</c> order.</param>
/// <param name="NextCursor">Cursor for the next page, or null on the last page.</param>
public sealed record ListEventsPage(IReadOnlyList<AuditEvent> Events, string? NextCursor);
