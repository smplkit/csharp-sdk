using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Audit events surface — accessed via <see cref="AuditClient.Events"/>.
///
/// <para><see cref="Record"/> is fire-and-forget — the call enqueues the
/// event onto an in-memory bounded buffer and returns immediately. Reads
/// (<see cref="ListAsync"/>, <see cref="GetAsync"/>) await their network
/// round-trip.</para>
/// </summary>
public sealed class AuditEvents
{
    private readonly GenAudit.AuditClient _gen;
    private readonly AuditEventBuffer _buffer;

    internal AuditEvents(GenAudit.AuditClient gen)
    {
        _gen = gen;
        _buffer = new AuditEventBuffer(gen);
    }

    /// <summary>Record an audit event. Fire-and-forget by default; blocks when <see cref="CreateEventInput.Flush"/> is set.</summary>
    /// <remarks>
    /// By default the event is queued onto an in-memory bounded buffer whose
    /// background worker performs the POST with retry on transient failures, and
    /// the call returns immediately. Set <see cref="CreateEventInput.Flush"/> to
    /// block until the event has been delivered (or
    /// <see cref="CreateEventInput.FlushTimeout"/> elapses) — use it when the event
    /// must be durable before continuing (CLI tools, tests, or any flow about to
    /// exit the process). <see cref="FlushAsync"/> drains the whole buffer the same
    /// way without recording a new event. A
    /// <see cref="CreateEventInput.ResourceType"/> beginning with <c>smpl.</c> is
    /// reserved for smplkit-emitted events; the server rejects customer attempts
    /// with a 403 and the buffer drops the item.
    /// </remarks>
    /// <param name="input">The event to record. <see cref="CreateEventInput.EventType"/>,
    /// <see cref="CreateEventInput.ResourceType"/>, and
    /// <see cref="CreateEventInput.ResourceId"/> are required.</param>
    public void Record(CreateEventInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrEmpty(input.EventType))
            throw new ArgumentException("EventType is required", nameof(input));
        if (string.IsNullOrEmpty(input.ResourceType))
            throw new ArgumentException("ResourceType is required", nameof(input));
        if (string.IsNullOrEmpty(input.ResourceId))
            throw new ArgumentException("ResourceId is required", nameof(input));

        var attrs = new GenAudit.Event
        {
            Event_type = input.EventType,
            Resource_type = input.ResourceType,
            Resource_id = input.ResourceId,
        };
        if (input.Category is not null)
        {
            attrs.Category = input.Category;
        }
        if (input.OccurredAt.HasValue)
        {
            attrs.Occurred_at = input.OccurredAt.Value;
        }
        if (input.ActorType is not null)
        {
            attrs.Actor_type = input.ActorType;
        }
        if (input.ActorId is not null)
        {
            attrs.Actor_id = input.ActorId;
        }
        if (input.ActorLabel is not null)
        {
            attrs.Actor_label = input.ActorLabel;
        }
        // Server-side validation rejects ``data: null`` (the field is
        // required-non-null in the OpenAPI schema). System.Text.Json
        // emits ``"data": null`` for an unset reference property by
        // default, so always populate Data with at least an empty dict.
        attrs.Data = input.Data != null
            ? new Dictionary<string, object>(
                input.Data.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value!)))
            : new Dictionary<string, object>();
        if (input.DoNotForward)
        {
            attrs.Do_not_forward = true;
        }

        var resource = new GenAudit.EventResource
        {
            Id = "",
            Type = "event",
            Attributes = attrs,
        };
        var body = new GenAudit.EventRequest { Data = resource };
        _buffer.Enqueue(body, input.IdempotencyKey);

        // Inline flush: block until the buffer drains (or the timeout elapses) so
        // the event is durable before the caller continues. Mirrors the canonical
        // record(flush=True, flush_timeout=...) blocking path.
        if (input.Flush)
        {
            _buffer.FlushAsync(input.FlushTimeout).GetAwaiter().GetResult();
        }
    }

    /// <summary>Retrieve a single audit event by id.</summary>
    /// <param name="eventId">The event's id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching <see cref="AuditEvent"/>.</returns>
    /// <exception cref="Smplkit.Errors.NotFoundException">If no event with that id exists in the caller's account.</exception>
    /// <exception cref="Smplkit.Errors.SmplkitException">On other non-2xx responses.</exception>
    public async Task<AuditEvent> GetAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_eventAsync(eventId, cancellationToken)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>
    /// List audit events for the authenticated account.
    ///
    /// <para>Filters apply server-side; pagination uses an opaque cursor
    /// (<see cref="ListEventsInput.PageAfter"/>), and the returned page exposes
    /// <see cref="ListEventsPage.NextCursor"/> when more pages are available.</para>
    ///
    /// <para><see cref="ListEventsInput.Search"/> is an optional free-text filter
    /// matching an event's resource id or description as a case-insensitive
    /// substring. It must be scoped — combine it with
    /// <see cref="ListEventsInput.OccurredAtRange"/>, or with both
    /// <see cref="ListEventsInput.ResourceType"/> and
    /// <see cref="ListEventsInput.ResourceId"/> — or the request is rejected.</para>
    ///
    /// <para><see cref="ListEventsInput.Environments"/> scopes the read to a set
    /// of environment keys (and/or the reserved <c>"smplkit"</c> bucket). When
    /// omitted, the filter is left off and the server scopes to your accessible
    /// environment.</para>
    /// </summary>
    /// <param name="input">Filters and pagination cursor; null lists with defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of matching <see cref="AuditEvent"/>s plus the next-page cursor.</returns>
    public async Task<ListEventsPage> ListAsync(ListEventsInput? input = null, CancellationToken cancellationToken = default)
    {
        input ??= new ListEventsInput();
        // Named arguments throughout so an additive query param (e.g. a future
        // leading filter[*]) inserted by the generator can't silently re-bind
        // these positionally.
        var resp = await ApiExceptionMapper.ExecuteAsync(() => _gen.List_eventsAsync(
            filterenvironment: Helpers.JoinEnvironments(input.Environments),
            filteroccurred_at: input.OccurredAtRange,
            filteractor_type: input.ActorType,
            filteractor_id: input.ActorId,
            filterevent_type: input.EventType,
            filterresource_type: input.ResourceType,
            filterresource_id: input.ResourceId,
            filtercategory: input.Category,
            filtersearch: input.Search,
            pagesize: input.PageSize,
            pageafter: input.PageAfter,
            // format: null — wrapper always uses the paginated JSON:API
            // response; the CSV/JSONL streaming export is not exposed.
            format: null,
            // sort: null — server default (-occurred_at) is fine here.
            sort: null,
            cancellationToken: cancellationToken
        )).ConfigureAwait(false);

        var events = (resp.Data ?? new List<GenAudit.EventResource>()).Select(FromResource).ToList();
        string? nextCursor = null;
        if (resp.Links?.Next is string next)
        {
            var idx = next.IndexOf("page[after]=", StringComparison.Ordinal);
            if (idx >= 0)
            {
                nextCursor = next.Substring(idx + "page[after]=".Length);
                var amp = nextCursor.IndexOf('&');
                if (amp >= 0) nextCursor = nextCursor.Substring(0, amp);
            }
        }
        return new ListEventsPage(events, nextCursor);
    }

    /// <summary>Block until the in-memory buffer is drained or timeout elapses.</summary>
    /// <param name="timeout">Upper bound on the blocking flush.</param>
    public Task FlushAsync(TimeSpan timeout) => _buffer.FlushAsync(timeout);

    /// <summary>Drains best-effort and stops the background worker. Called from <see cref="AuditClient.DisposeAsync"/>.</summary>
    internal ValueTask DisposeAsync() => _buffer.DisposeAsync();

    private static AuditEvent FromResource(GenAudit.EventResource r)
    {
        var a = r.Attributes;
        return new AuditEvent(
            string.IsNullOrEmpty(r.Id) ? Guid.Empty : Guid.Parse(r.Id),
            a.Event_type ?? string.Empty,
            a.Resource_type ?? string.Empty,
            a.Resource_id ?? string.Empty,
            a.Category,
            a.Occurred_at ?? default,
            a.Created_at ?? default,
            a.Actor_type,
            a.Actor_id,
            a.Actor_label,
            ConvertJsonObject(a.Data) ?? new Dictionary<string, object?>(),
            a.Idempotency_key ?? string.Empty,
            a.Do_not_forward,
            a.Environment
        );
    }

    private static IDictionary<string, object?>? ConvertJsonObject(object? raw)
    {
        if (raw is null) return null;
        if (raw is IDictionary<string, object?> dict) return dict;
        // System.Text.Json deserializes untyped fields as JsonElement; expand
        // a one-level object into a plain Dictionary so callers don't have to
        // know about JsonElement.
        if (raw is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var result = new Dictionary<string, object?>();
            foreach (var prop in el.EnumerateObject())
            {
                result[prop.Name] = JsonElementToObject(prop.Value);
            }
            return result;
        }
        return null;
    }

    private static object? JsonElementToObject(System.Text.Json.JsonElement el)
    {
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => el.GetString(),
            System.Text.Json.JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Object => ConvertJsonObject(el),
            System.Text.Json.JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
            // Null + Undefined (the latter only arises from default(JsonElement)) collapse to null.
            _ => null,
        };
    }
}
