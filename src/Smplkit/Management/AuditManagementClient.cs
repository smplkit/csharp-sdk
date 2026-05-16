using Smplkit.Audit;
using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Management;

/// <summary>
/// SIEM forwarder CRUD surface for the management plane.
/// Accessed via <see cref="AuditManagementClient.Forwarders"/>.
///
/// <para>Create/get/list/update/delete only — delivery log, retry, and
/// test_forwarder endpoints are internal and not exposed here.</para>
/// </summary>
public sealed class ManagementForwardersClient
{
    private readonly GenAudit.AuditClient _gen;

    internal ManagementForwardersClient(GenAudit.AuditClient gen) => _gen = gen;

    /// <summary>Create a forwarder.</summary>
    public async Task<Forwarder> CreateAsync(CreateForwarderInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var body = WrapForwarder(null, input);
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Create_forwarderAsync(body, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>List forwarders for the authenticated account.</summary>
    public async Task<ListForwardersPage> ListAsync(
        ListForwardersInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListForwardersInput();
        var filterType = input.ForwarderType?.ToWireValue();
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_forwardersAsync(filterType, input.Enabled, null, input.PageNumber, input.PageSize, input.MetaTotal, ct)
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.ForwarderResource>()).Select(FromResource).ToList();
        return new ListForwardersPage(rows, AuditResourceTypes.ExtractPagination(resp.Meta));
    }

    /// <summary>Retrieve a single forwarder by id.</summary>
    public async Task<Forwarder> GetAsync(Guid forwarderId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_forwarderAsync(forwarderId, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Full-replace update. Re-supply real header values; reads return them redacted.</summary>
    public async Task<Forwarder> UpdateAsync(
        Guid forwarderId, CreateForwarderInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var body = WrapForwarder(forwarderId, input);
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Update_forwarderAsync(forwarderId, body, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Soft-delete a forwarder.</summary>
    public Task DeleteAsync(Guid forwarderId, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _gen.Delete_forwarderAsync(forwarderId, ct));

    // ------------------------------------------------------------------
    // Wire <-> wrapper conversions
    // ------------------------------------------------------------------

    private static GenAudit.ForwarderRequest WrapForwarder(Guid? id, CreateForwarderInput input)
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
        var r = new GenAudit.ForwarderResource
        {
            Id = id?.ToString() ?? string.Empty,
            Type = "forwarder",
            Attributes = attrs,
        };
        return new GenAudit.ForwarderRequest { Data = r };
    }

    private static GenAudit.ForwarderHttp ToGenHttp(ForwarderHttp src)
    {
        var headers = new List<GenAudit.HttpHeader>(src.Headers.Count);
        foreach (var h in src.Headers)
            headers.Add(new GenAudit.HttpHeader { Name = h.Name, Value = h.Value });

        var method = ParseHttpMethod(src.Method);
        var out_ = new GenAudit.ForwarderHttp
        {
            Method = method,
            Url = src.Url,
            Headers = headers,
            Success_status = src.SuccessStatus,
        };
        if (src.Body != null) out_.Body = src.Body;
        return out_;
    }

    private static GenAudit.ForwarderHttpMethod ParseHttpMethod(string method) =>
        method.ToUpperInvariant() switch
        {
            "GET" => GenAudit.ForwarderHttpMethod.GET,
            "PUT" => GenAudit.ForwarderHttpMethod.PUT,
            "PATCH" => GenAudit.ForwarderHttpMethod.PATCH,
            "DELETE" => GenAudit.ForwarderHttpMethod.DELETE,
            _ => GenAudit.ForwarderHttpMethod.POST,
        };

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
            a.Created_at,
            a.Updated_at,
            a.Deleted_at,
            a.Version);
    }

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
            Method = src.Method.ToString(),
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

    private static IDictionary<string, object?>? ConvertJson(object? raw)
    {
        if (raw is null) return null;
        if (raw is IDictionary<string, object?> dict) return dict;
        if (raw is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var result = new Dictionary<string, object?>();
            foreach (var prop in el.EnumerateObject())
                result[prop.Name] = JsonElementToObject(prop.Value);
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

}

/// <summary>
/// Audit management surface — accessed via <c>SmplManagementClient.Audit</c>.
///
/// <para>Currently exposes SIEM forwarder CRUD via <see cref="Forwarders"/>.
/// Runtime read surfaces (events, resource types, actions) live on
/// <c>SmplClient.Audit</c>.</para>
/// </summary>
public sealed class AuditManagementClient
{
    /// <summary>SIEM forwarder CRUD.</summary>
    public ManagementForwardersClient Forwarders { get; }

    internal AuditManagementClient(GenAudit.AuditClient generated)
    {
        Forwarders = new ManagementForwardersClient(generated);
    }
}
