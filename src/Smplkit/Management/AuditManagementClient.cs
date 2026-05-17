using Smplkit.Audit;
using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Management;

/// <summary>
/// SIEM forwarder management surface for the management plane.
/// Accessed via <see cref="AuditManagementClient.Forwarders"/>.
///
/// <para>The customer-facing API is active-record: use <see cref="New"/> to
/// build an unsaved <see cref="Forwarder"/>, mutate fields directly, then
/// call <see cref="Forwarder.SaveAsync"/> to persist. Reads come from
/// <see cref="GetAsync"/> / <see cref="ListAsync"/>; the returned instances
/// are bound to this client so <c>save()</c>/<c>delete()</c> work.</para>
/// </summary>
public sealed class ManagementForwardersClient
{
    private readonly GenAudit.AuditClient _gen;

    internal ManagementForwardersClient(GenAudit.AuditClient gen) => _gen = gen;

    /// <summary>
    /// Returns an unsaved <see cref="Forwarder"/> bound to this client. Call
    /// <see cref="Forwarder.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="name">Display name. Free-form.</param>
    /// <param name="forwarderType">Destination type — see <see cref="ForwarderType"/>.</param>
    /// <param name="configuration">Destination HTTP request configuration.
    /// Headers carry credentials and are encrypted at rest server-side; reads
    /// return them redacted.</param>
    /// <param name="enabled">Whether the forwarder is active. Defaults to <c>true</c>.</param>
    /// <param name="description">Optional free-text description.</param>
    /// <param name="filter">Optional JSON Logic filter; events that don't match
    /// are recorded as <c>filtered_out</c> deliveries.</param>
    /// <param name="transform">Optional template applied to the event payload
    /// before POST. Shape depends on <paramref name="transformType"/>; the wire
    /// field is untyped so any compatible value is accepted. <c>null</c> sends
    /// the event JSON as-is.</param>
    /// <param name="transformType">Engine used to evaluate
    /// <paramref name="transform"/>. Required whenever
    /// <paramref name="transform"/> is non-null; passing <paramref name="transform"/>
    /// without <paramref name="transformType"/> throws
    /// <see cref="ArgumentException"/>.</param>
    public Forwarder New(
        string name,
        ForwarderType forwarderType,
        HttpConfiguration configuration,
        bool enabled = true,
        string? description = null,
        IDictionary<string, object?>? filter = null,
        object? transform = null,
        TransformType? transformType = null)
    {
        return new Forwarder(
            this,
            name: name,
            forwarderType: forwarderType,
            configuration: configuration,
            enabled: enabled,
            description: description,
            filter: filter,
            transform: transform,
            transformType: transformType);
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

    /// <summary>Retrieve a single forwarder by id. The returned instance is bound to
    /// this client so <see cref="Forwarder.SaveAsync"/> / <see cref="Forwarder.DeleteAsync"/> work.</summary>
    public async Task<Forwarder> GetAsync(Guid forwarderId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_forwarderAsync(forwarderId, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Soft-delete a forwarder by id. Prefer <see cref="Forwarder.DeleteAsync"/>
    /// when you already have a <see cref="Forwarder"/> instance.</summary>
    public Task DeleteAsync(Guid forwarderId, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _gen.Delete_forwarderAsync(forwarderId, ct));

    // ------------------------------------------------------------------
    // Internal: drive Forwarder.SaveAsync
    // ------------------------------------------------------------------

    /// <summary>POST a new forwarder. Called by <see cref="Forwarder.SaveAsync"/>; not for direct use.</summary>
    internal async Task<Forwarder> SaveCreateAsync(Forwarder forwarder, CancellationToken ct)
    {
        var body = WrapForwarder(null, forwarder);
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Create_forwarderAsync(body, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Full-replace PUT. Called by <see cref="Forwarder.SaveAsync"/>; not for direct use.</summary>
    internal async Task<Forwarder> SaveUpdateAsync(Forwarder forwarder, CancellationToken ct)
    {
        if (forwarder.Id is null)
            throw new InvalidOperationException("Cannot update a Forwarder with no id");
        var body = WrapForwarder(forwarder.Id, forwarder);
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Update_forwarderAsync(forwarder.Id.Value, body, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    // ------------------------------------------------------------------
    // Wire <-> wrapper conversions
    // ------------------------------------------------------------------

    private static GenAudit.ForwarderRequest WrapForwarder(Guid? id, Forwarder src)
    {
        var attrs = new GenAudit.Forwarder
        {
            Name = src.Name,
            Forwarder_type = ToGenForwarderType(src.ForwarderType),
            Enabled = src.Enabled,
            Configuration = ToGenHttpConfiguration(src.Configuration),
        };
        if (src.Description is not null) attrs.Description = src.Description;
        if (src.Filter != null)
        {
            attrs.Filter = new Dictionary<string, object>(
                src.Filter.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value!)));
        }
        if (src.Transform != null)
        {
            if (src.TransformType is null)
                throw new ArgumentException(
                    "TransformType is required when Transform is set.",
                    nameof(src));
            attrs.Transform = src.Transform;
            attrs.Transform_type = src.TransformType.Value.ToWireValue();
        }
        var r = new GenAudit.ForwarderResource
        {
            Id = id?.ToString() ?? string.Empty,
            Type = "forwarder",
            Attributes = attrs,
        };
        return new GenAudit.ForwarderRequest { Data = r };
    }

    private static GenAudit.HttpConfiguration ToGenHttpConfiguration(HttpConfiguration src)
    {
        var headers = new List<GenAudit.HttpHeader>(src.Headers.Count);
        foreach (var h in src.Headers)
            headers.Add(new GenAudit.HttpHeader { Name = h.Name, Value = h.Value });

        return new GenAudit.HttpConfiguration
        {
            Method = ToGenHttpMethod(src.Method),
            Url = src.Url,
            Headers = headers,
            Success_status = src.SuccessStatus,
        };
    }

    private static GenAudit.HttpConfigurationMethod ToGenHttpMethod(Smplkit.Audit.HttpMethod method) =>
        method switch
        {
            Smplkit.Audit.HttpMethod.Delete => GenAudit.HttpConfigurationMethod.DELETE,
            Smplkit.Audit.HttpMethod.Get => GenAudit.HttpConfigurationMethod.GET,
            Smplkit.Audit.HttpMethod.Patch => GenAudit.HttpConfigurationMethod.PATCH,
            Smplkit.Audit.HttpMethod.Post => GenAudit.HttpConfigurationMethod.POST,
            Smplkit.Audit.HttpMethod.Put => GenAudit.HttpConfigurationMethod.PUT,
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

    private Forwarder FromResource(GenAudit.ForwarderResource r)
    {
        var a = r.Attributes;
        return new Forwarder(
            this,
            name: a.Name ?? string.Empty,
            forwarderType: FromGenForwarderType(a.Forwarder_type),
            configuration: HttpFromGen(a.Configuration),
            enabled: a.Enabled,
            description: a.Description,
            filter: ConvertJson(a.Filter),
            transform: a.Transform as string,
            transformType: FromGenTransformType(a.Transform_type),
            id: string.IsNullOrEmpty(r.Id) ? null : Guid.Parse(r.Id),
            createdAt: a.Created_at,
            updatedAt: a.Updated_at,
            deletedAt: a.Deleted_at,
            version: a.Version);
    }

    private static TransformType? FromGenTransformType(string? wire)
    {
        if (string.IsNullOrEmpty(wire)) return null;
        return TransformTypeExtensions.FromWireValue(wire);
    }

    private static GenAudit.ForwarderType ToGenForwarderType(ForwarderType src) =>
        src switch
        {
            ForwarderType.Datadog => GenAudit.ForwarderType.DATADOG,
            ForwarderType.Elastic => GenAudit.ForwarderType.ELASTIC,
            ForwarderType.Honeycomb => GenAudit.ForwarderType.HONEYCOMB,
            ForwarderType.Http => GenAudit.ForwarderType.HTTP,
            ForwarderType.NewRelic => GenAudit.ForwarderType.NEW_RELIC,
            ForwarderType.SplunkHec => GenAudit.ForwarderType.SPLUNK_HEC,
            ForwarderType.SumoLogic => GenAudit.ForwarderType.SUMO_LOGIC,
            _ => throw new ArgumentOutOfRangeException(nameof(src), src, null),
        };

    private static ForwarderType FromGenForwarderType(GenAudit.ForwarderType src) =>
        src switch
        {
            GenAudit.ForwarderType.DATADOG => ForwarderType.Datadog,
            GenAudit.ForwarderType.ELASTIC => ForwarderType.Elastic,
            GenAudit.ForwarderType.HONEYCOMB => ForwarderType.Honeycomb,
            GenAudit.ForwarderType.HTTP => ForwarderType.Http,
            GenAudit.ForwarderType.NEW_RELIC => ForwarderType.NewRelic,
            GenAudit.ForwarderType.SPLUNK_HEC => ForwarderType.SplunkHec,
            GenAudit.ForwarderType.SUMO_LOGIC => ForwarderType.SumoLogic,
            _ => throw new ArgumentOutOfRangeException(nameof(src), src, null),
        };

    private static HttpConfiguration HttpFromGen(GenAudit.HttpConfiguration? src)
    {
        if (src == null) return new HttpConfiguration { Url = string.Empty };
        var out_ = new HttpConfiguration
        {
            Method = HttpMethodExtensions.FromWireValue(src.Method.ToString()),
            Url = src.Url ?? string.Empty,
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
    /// <summary>SIEM forwarder management surface.</summary>
    public ManagementForwardersClient Forwarders { get; }

    internal AuditManagementClient(GenAudit.AuditClient generated)
    {
        Forwarders = new ManagementForwardersClient(generated);
    }
}
