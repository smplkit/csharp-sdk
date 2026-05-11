using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// SIEM streaming forwarders for the authenticated account.
///
/// <para>Pro tier only — every method here returns a wrapped 402
/// (<see cref="GenAudit.ApiException"/> with <c>StatusCode</c>=402) on
/// lower tiers.</para>
/// </summary>
public sealed class AuditForwarders
{
    private readonly GenAudit.AuditClient _gen;

    internal AuditForwarders(GenAudit.AuditClient gen)
    {
        _gen = gen;
    }

    /// <summary>Create a new forwarder.</summary>
    public async Task<Forwarder> CreateAsync(CreateForwarderInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var body = WrapForwarder(null, input);
        var resp = await _gen.Create_forwarderAsync(body, ct).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>List forwarders for the authenticated account.</summary>
    public async Task<ListForwardersPage> ListAsync(
        ListForwardersInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListForwardersInput();
        // Generated client takes the filter as string?; convert the
        // typed enum to its wire slug.
        var filterType = input.ForwarderType?.ToWireValue();
        var resp = await _gen.List_forwardersAsync(
            filterType, input.Enabled, input.PageSize, input.PageAfter, ct
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.ForwarderResource>()).Select(FromResource).ToList();
        return new ListForwardersPage(rows, ExtractCursor(resp.Links?.Next));
    }

    /// <summary>Retrieve a single forwarder by id.</summary>
    public async Task<Forwarder> GetAsync(Guid forwarderId, CancellationToken ct = default)
    {
        var resp = await _gen.Get_forwarderAsync(forwarderId, ct).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Full-replace update. Re-supply real header values; reads return them redacted.</summary>
    public async Task<Forwarder> UpdateAsync(
        Guid forwarderId, CreateForwarderInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var body = WrapForwarder(forwarderId, input);
        var resp = await _gen.Update_forwarderAsync(forwarderId, body, ct).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Soft-delete a forwarder.</summary>
    public Task DeleteAsync(Guid forwarderId, CancellationToken ct = default)
        => _gen.Delete_forwarderAsync(forwarderId, ct);

    /// <summary>List delivery rows for a forwarder.</summary>
    public async Task<ListDeliveriesPage> ListDeliveriesAsync(
        Guid forwarderId, ListDeliveriesInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListDeliveriesInput();
        var eventIdStr = input.EventId.HasValue ? input.EventId.Value.ToString() : null;
        var resp = await _gen.List_forwarder_deliveriesAsync(
            forwarderId, input.Status, input.CreatedAtRange, eventIdStr, input.PageSize, input.PageAfter, ct
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.ForwarderDeliveryResource>())
            .Select(DeliveryFromResource).ToList();
        return new ListDeliveriesPage(rows, ExtractCursor(resp.Links?.Next));
    }

    /// <summary>Retry a single failed delivery; returns the new attempt row.</summary>
    public async Task<ForwarderDelivery> RetryDeliveryAsync(
        Guid forwarderId, Guid deliveryId, CancellationToken ct = default)
    {
        var resp = await _gen.Retry_forwarder_deliveryAsync(forwarderId, deliveryId, ct)
            .ConfigureAwait(false);
        return DeliveryFromResource(resp.Data);
    }

    /// <summary>Retry every failed delivery for a forwarder. Returns counts.</summary>
    public async Task<RetryFailedDeliveriesSummary> RetryFailedDeliveriesAsync(
        Guid forwarderId, CancellationToken ct = default)
    {
        var resp = await _gen.Retry_failed_forwarder_deliveriesAsync(forwarderId, ct)
            .ConfigureAwait(false);
        return new RetryFailedDeliveriesSummary(
            resp.Attempted, resp.Succeeded, resp.Failed);
    }

    // ------------------------------------------------------------------
    // Wire <-> wrapper conversions
    // ------------------------------------------------------------------

    private static GenAudit.ForwarderResponse WrapForwarder(Guid? id, CreateForwarderInput input)
    {
        var attrs = new GenAudit.Forwarder
        {
            Name = input.Name,
            Forwarder_type = ToGenForwarderType(input.ForwarderType),
            Enabled = input.Enabled,
            Http = ToGenHttp(input.Http),
        };
        if (input.Filter != null)
        {
            attrs.Filter = new Dictionary<string, object>(
                input.Filter.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value!)));
        }
        if (input.Transform != null) attrs.Transform = input.Transform;
        // Server-side validation rejects ``data: null`` (the field is
        // required-non-null in the OpenAPI schema). Emit at least an
        // empty dictionary even when the caller didn't supply Data.
        attrs.Data = input.Data != null
            ? new Dictionary<string, object>(
                input.Data.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value!)))
            : new Dictionary<string, object>();
        var r = new GenAudit.ForwarderResource
        {
            Id = id?.ToString() ?? string.Empty,
            Type = "forwarder",
            Attributes = attrs,
        };
        return new GenAudit.ForwarderResponse { Data = r };
    }

    private static GenAudit.ForwarderHttp ToGenHttp(ForwarderHttp src)
    {
        var headers = new List<GenAudit.HttpHeader>(src.Headers.Count);
        foreach (var h in src.Headers)
        {
            headers.Add(new GenAudit.HttpHeader { Name = h.Name, Value = h.Value });
        }
        var out_ = new GenAudit.ForwarderHttp
        {
            Method = src.Method,
            Url = src.Url,
            Headers = headers,
            Success_status = src.SuccessStatus,
        };
        if (src.Body != null) out_.Body = src.Body;
        return out_;
    }

    private static Forwarder FromResource(GenAudit.ForwarderResource r)
    {
        var a = r.Attributes;
        var http = HttpFromGen(a.Http);
        return new Forwarder(
            string.IsNullOrEmpty(r.Id) ? Guid.Empty : Guid.Parse(r.Id),
            a.Name ?? string.Empty,
            a.Slug ?? string.Empty,
            FromGenForwarderType(a.Forwarder_type),
            a.Enabled,
            ConvertJson(a.Filter),
            a.Transform,
            http,
            ConvertJson(a.Data) ?? new Dictionary<string, object?>(),
            a.Created_at,
            a.Updated_at,
            a.Deleted_at,
            a.Version);
    }

    /// <summary>Convert the wrapper's public enum to the codegen's internal one.</summary>
    private static GenAudit.ForwarderType ToGenForwarderType(ForwarderType src) =>
        src switch
        {
            ForwarderType.Http => GenAudit.ForwarderType.HTTP,
            ForwarderType.Datadog => GenAudit.ForwarderType.DATADOG,
            ForwarderType.SplunkHec => GenAudit.ForwarderType.SPLUNK_HEC,
            ForwarderType.SumoLogic => GenAudit.ForwarderType.SUMO_LOGIC,
            ForwarderType.NewRelic => GenAudit.ForwarderType.NEW_RELIC,
            ForwarderType.Honeycomb => GenAudit.ForwarderType.HONEYCOMB,
            ForwarderType.Elastic => GenAudit.ForwarderType.ELASTIC,
            _ => throw new ArgumentOutOfRangeException(nameof(src), src, null),
        };

    /// <summary>Convert the codegen's internal enum to the wrapper's public one.</summary>
    private static ForwarderType FromGenForwarderType(GenAudit.ForwarderType src) =>
        src switch
        {
            GenAudit.ForwarderType.HTTP => ForwarderType.Http,
            GenAudit.ForwarderType.DATADOG => ForwarderType.Datadog,
            GenAudit.ForwarderType.SPLUNK_HEC => ForwarderType.SplunkHec,
            GenAudit.ForwarderType.SUMO_LOGIC => ForwarderType.SumoLogic,
            GenAudit.ForwarderType.NEW_RELIC => ForwarderType.NewRelic,
            GenAudit.ForwarderType.HONEYCOMB => ForwarderType.Honeycomb,
            GenAudit.ForwarderType.ELASTIC => ForwarderType.Elastic,
            _ => throw new ArgumentOutOfRangeException(nameof(src), src, null),
        };

    private static ForwarderHttp HttpFromGen(GenAudit.ForwarderHttp? src)
    {
        if (src == null) return new ForwarderHttp { Url = string.Empty };
        var out_ = new ForwarderHttp
        {
            Method = src.Method ?? "POST",
            Url = src.Url ?? string.Empty,
            Body = src.Body,
            SuccessStatus = src.Success_status ?? "2xx",
        };
        if (src.Headers != null)
        {
            out_.Headers = src.Headers
                .Select(h => new HttpHeader(h.Name ?? string.Empty, h.Value ?? string.Empty))
                .ToList();
        }
        return out_;
    }

    private static ForwarderDelivery DeliveryFromResource(GenAudit.ForwarderDeliveryResource r)
    {
        var a = r.Attributes;
        return new ForwarderDelivery(
            string.IsNullOrEmpty(r.Id) ? Guid.Empty : Guid.Parse(r.Id),
            a.Forwarder_id,
            a.Event_id,
            a.Attempt_number,
            a.Status.ToString() ?? string.Empty,
            ConvertJson(a.Request),
            a.Response_status,
            a.Response_body,
            a.Latency_ms,
            a.Error,
            a.Created_at);
    }

    private static IDictionary<string, object?>? ConvertJson(object? raw)
    {
        if (raw is null) return null;
        if (raw is IDictionary<string, object?> dict) return dict;
        // NSwag deserializes the wire's free-form JSONB fields as JsonElement;
        // wrapper-side construction (tests, manual instantiation) uses
        // IDictionary<string, object?> directly via the branch above.
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

    private static object? JsonElementToObject(System.Text.Json.JsonElement el) => el.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => el.GetString(),
        System.Text.Json.JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Object => ConvertJson(el),
        System.Text.Json.JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => null,
    };

    private static string? ExtractCursor(string? link)
    {
        if (string.IsNullOrEmpty(link)) return null;
        var idx = link.IndexOf("page[after]=", StringComparison.Ordinal);
        if (idx < 0) return null;
        var token = link.Substring(idx + "page[after]=".Length);
        var amp = token.IndexOf('&');
        return amp >= 0 ? token.Substring(0, amp) : token;
    }
}
