using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Audit events surface — accessed via <see cref="AuditClient.Events"/>.
///
/// <para><see cref="Record"/> is fire-and-forget per ADR-047 §2.6 — the
/// call enqueues the event onto an in-memory bounded buffer and returns
/// immediately. Reads (<see cref="ListAsync"/>, <see cref="GetAsync"/>)
/// are async and synchronous on the wire.</para>
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

    /// <summary>Enqueue an audit event for asynchronous delivery. Returns immediately.</summary>
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
    }

    /// <summary>Single-event retrieval.</summary>
    /// <exception cref="Smplkit.Errors.NotFoundException">If the event does not exist in the caller's account.</exception>
    /// <exception cref="Smplkit.Errors.SmplkitException">On other non-2xx responses.</exception>
    public async Task<AuditEvent> GetAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_eventAsync(eventId, cancellationToken)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>List events with filters and cursor pagination.</summary>
    public async Task<ListEventsPage> ListAsync(ListEventsInput? input = null, CancellationToken cancellationToken = default)
    {
        input ??= new ListEventsInput();
        var resp = await ApiExceptionMapper.ExecuteAsync(() => _gen.List_eventsAsync(
            input.OccurredAtRange,
            input.ActorType,
            input.ActorId,
            input.EventType,
            input.ResourceType,
            input.ResourceId,
            input.Search,
            input.DoNotForward,
            input.PageSize,
            input.PageAfter,
            // format: null — wrapper always uses the paginated JSON:API
            // response; the CSV/JSONL streaming export is not exposed.
            null,
            // sort: null — server default (-occurred_at) is fine here.
            null,
            cancellationToken
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
            a.Occurred_at ?? default,
            a.Created_at ?? default,
            a.Actor_type,
            a.Actor_id,
            a.Actor_label,
            ConvertJsonObject(a.Data) ?? new Dictionary<string, object?>(),
            a.Idempotency_key ?? string.Empty,
            a.Do_not_forward
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
