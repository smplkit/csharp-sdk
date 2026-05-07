using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Audit events surface — accessed via <see cref="AuditClient.Events"/>.
///
/// <para><see cref="Create"/> is fire-and-forget per ADR-047 §2.6 — the
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
    public void Create(CreateEventInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrEmpty(input.Action))
            throw new ArgumentException("Action is required", nameof(input));
        if (string.IsNullOrEmpty(input.ResourceType))
            throw new ArgumentException("ResourceType is required", nameof(input));
        if (string.IsNullOrEmpty(input.ResourceId))
            throw new ArgumentException("ResourceId is required", nameof(input));

        var attrs = new GenAudit.Event
        {
            Action = input.Action,
            Resource_type = input.ResourceType,
            Resource_id = input.ResourceId,
        };
        if (input.OccurredAt.HasValue)
        {
            attrs.Occurred_at = input.OccurredAt.Value;
        }
        if (input.Snapshot != null)
        {
            attrs.Snapshot = new Dictionary<string, object>(
                input.Snapshot.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value!)));
        }
        if (input.Data != null)
        {
            attrs.Data = new Dictionary<string, object>(
                input.Data.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value!)));
        }

        var resource = new GenAudit.EventResource
        {
            Id = "",
            Type = "event",
            Attributes = attrs,
        };
        var body = new GenAudit.EventResponse { Data = resource };
        _buffer.Enqueue(body, input.IdempotencyKey);
    }

    /// <summary>Single-event retrieval.</summary>
    /// <exception cref="GenAudit.ApiException">If the server returns a non-2xx status (404 if not found).</exception>
    public async Task<AuditEvent> GetAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var resp = await _gen.Get_eventAsync(eventId, cancellationToken).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>List events with filters and cursor pagination.</summary>
    public async Task<ListEventsPage> ListAsync(ListEventsInput? input = null, CancellationToken cancellationToken = default)
    {
        input ??= new ListEventsInput();
        var resp = await _gen.List_eventsAsync(
            input.OccurredAtRange,
            input.ActorType,
            input.ActorId,
            input.Action,
            input.ResourceType,
            input.ResourceId,
            input.PageSize,
            input.PageAfter,
            cancellationToken
        ).ConfigureAwait(false);

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
            a.Action ?? string.Empty,
            a.Resource_type ?? string.Empty,
            a.Resource_id ?? string.Empty,
            a.Occurred_at ?? default,
            a.Created_at ?? default,
            a.Actor_type ?? string.Empty,
            a.Actor_id,
            a.Actor_label ?? string.Empty,
            ConvertJsonObject(a.Snapshot),
            ConvertJsonObject(a.Data) ?? new Dictionary<string, object?>(),
            a.Idempotency_key ?? string.Empty
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
            System.Text.Json.JsonValueKind.Null => null,
            System.Text.Json.JsonValueKind.Object => ConvertJsonObject(el),
            System.Text.Json.JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
            // Undefined and any future value kinds — surface the raw token text.
            _ => el.GetRawText(),
        };
    }
}
