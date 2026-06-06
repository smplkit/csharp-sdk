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
    /// <param name="key">Caller-supplied forwarder key — required at create
    /// time (the audit service does not auto-generate it). Use a stable,
    /// human-readable identifier (e.g. <c>"splunk-prod"</c>); the key is what
    /// appears in every URL and audit-log line for this forwarder.</param>
    /// <param name="name">Display name. Free-form.</param>
    /// <param name="forwarderType">Destination type — see <see cref="ForwarderType"/>.</param>
    /// <param name="configuration">Destination HTTP request configuration.
    /// Headers carry credentials and are encrypted at rest server-side; reads
    /// return them redacted.</param>
    /// <param name="environments">Per-environment overrides keyed by environment
    /// key (e.g. <c>"production"</c>). A forwarder delivers in an environment
    /// only when that environment's entry has <c>Enabled = true</c>. Each entry
    /// may carry an optional <see cref="HttpConfiguration"/> override; omit it to
    /// inherit the base <paramref name="configuration"/>. Omit the whole argument
    /// to create a forwarder that delivers nowhere until enabled per
    /// environment. Every referenced environment must exist and be managed for
    /// the account.</param>
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
        string key,
        string name,
        ForwarderType forwarderType,
        HttpConfiguration configuration,
        IDictionary<string, ForwarderEnvironment>? environments = null,
        string? description = null,
        IDictionary<string, object?>? filter = null,
        object? transform = null,
        TransformType? transformType = null)
    {
        Forwarder.ValidateTransformPairing(transform, transformType);
        return new Forwarder(
            this,
            name: name,
            forwarderType: forwarderType,
            configuration: configuration,
            environments: environments,
            description: description,
            filter: filter,
            transform: transform,
            transformType: transformType,
            id: key);
    }

    /// <summary>List forwarders for the authenticated account.</summary>
    public async Task<ListForwardersPage> ListAsync(
        ListForwardersInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListForwardersInput();
        var filterType = input.ForwarderType?.ToWireValue();
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_forwardersAsync(filterType, null, input.PageNumber, input.PageSize, input.MetaTotal, ct)
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.ForwarderResource>()).Select(FromResource).ToList();
        return new ListForwardersPage(rows, AuditResourceTypes.ExtractPagination(resp.Meta));
    }

    /// <summary>Retrieve a single forwarder by id. The returned instance is bound to
    /// this client so <see cref="Forwarder.SaveAsync"/> / <see cref="Forwarder.DeleteAsync"/> work.</summary>
    public async Task<Forwarder> GetAsync(string forwarderId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_forwarderAsync(forwarderId, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Soft-delete a forwarder by id. Prefer <see cref="Forwarder.DeleteAsync"/>
    /// when you already have a <see cref="Forwarder"/> instance.</summary>
    public Task DeleteAsync(string forwarderId, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _gen.Delete_forwarderAsync(forwarderId, ct));

    // ------------------------------------------------------------------
    // Internal: drive Forwarder.SaveAsync
    // ------------------------------------------------------------------

    /// <summary>POST a new forwarder. Called by <see cref="Forwarder.SaveAsync"/>; not for direct use.</summary>
    internal async Task<Forwarder> SaveCreateAsync(Forwarder forwarder, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(forwarder.Id))
            throw new InvalidOperationException("Cannot create a Forwarder with no key");
        var body = WrapForwarderForCreate(forwarder);
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Create_forwarderAsync(body, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Full-replace PUT. Called by <see cref="Forwarder.SaveAsync"/>; not for direct use.</summary>
    internal async Task<Forwarder> SaveUpdateAsync(Forwarder forwarder, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(forwarder.Id))
            throw new InvalidOperationException("Cannot update a Forwarder with no id");
        var body = WrapForwarderForUpdate(forwarder);
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Update_forwarderAsync(forwarder.Id, body, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    // ------------------------------------------------------------------
    // Wire <-> wrapper conversions
    // ------------------------------------------------------------------

    /// <summary>
    /// Build the shared <see cref="GenAudit.Forwarder"/> attribute payload
    /// from a wrapper instance. Re-validates the transform / transformType
    /// pairing so mutations made after construction (e.g. setting
    /// <see cref="Forwarder.Transform"/> on a fetched instance) are caught
    /// at the wire boundary too.
    /// </summary>
    private static GenAudit.Forwarder BuildForwarderAttributes(Forwarder src)
    {
        // The base `enabled` is server-pinned false (ADR-055); we never send it.
        // Enablement travels entirely through the per-environment overrides.
        var attrs = new GenAudit.Forwarder
        {
            Name = src.Name,
            Forwarder_type = ToGenForwarderType(src.ForwarderType),
            Configuration = ToGenHttpConfiguration(src.Configuration),
        };
        if (src.Environments.Count > 0)
        {
            attrs.Environments = src.Environments.ToDictionary(
                kv => kv.Key,
                kv => new GenAudit.ForwarderEnvironment
                {
                    Enabled = kv.Value.Enabled,
                    Configuration = kv.Value.Configuration is { } cfg ? ToGenHttpConfiguration(cfg) : null,
                });
        }
        if (src.Description is not null) attrs.Description = src.Description;
        if (src.Filter != null)
        {
            attrs.Filter = new Dictionary<string, object>(
                src.Filter.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value!)));
        }
        Forwarder.ValidateTransformPairing(src.Transform, src.TransformType);
        if (src.Transform != null)
        {
            attrs.Transform = src.Transform;
            attrs.Transform_type = src.TransformType!.Value.ToWireValue();
        }
        return attrs;
    }

    private static GenAudit.ForwarderCreateRequest WrapForwarderForCreate(Forwarder src)
    {
        var r = new GenAudit.ForwarderCreateResource
        {
            Id = src.Id!,
            Type = "forwarder",
            Attributes = BuildForwarderAttributes(src),
        };
        return new GenAudit.ForwarderCreateRequest { Data = r };
    }

    private static GenAudit.ForwarderRequest WrapForwarderForUpdate(Forwarder src)
    {
        var r = new GenAudit.ForwarderResource
        {
            Id = src.Id,
            Type = "forwarder",
            Attributes = BuildForwarderAttributes(src),
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
            Tls_verify = src.TlsVerify,
            Ca_cert = src.CaCert,
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
            // The base `enabled` is server-pinned false; round-trip whatever the
            // server returned (always false) without assuming a default of true.
            enabled: a.Enabled,
            environments: EnvironmentsFromGen(a.Environments),
            description: a.Description,
            filter: ConvertJson(a.Filter),
            transform: ConvertTransform(a.Transform),
            transformType: FromGenTransformType(a.Transform_type),
            id: string.IsNullOrEmpty(r.Id) ? null : r.Id,
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
            ForwarderType.Datadog => GenAudit.ForwarderType.Datadog,
            ForwarderType.Elastic => GenAudit.ForwarderType.Elastic,
            ForwarderType.Honeycomb => GenAudit.ForwarderType.Honeycomb,
            ForwarderType.Http => GenAudit.ForwarderType.Http,
            ForwarderType.NewRelic => GenAudit.ForwarderType.New_relic,
            ForwarderType.SplunkHec => GenAudit.ForwarderType.Splunk_hec,
            ForwarderType.SumoLogic => GenAudit.ForwarderType.Sumo_logic,
            _ => throw new ArgumentOutOfRangeException(nameof(src), src, null),
        };

    private static ForwarderType FromGenForwarderType(GenAudit.ForwarderType src) =>
        src switch
        {
            GenAudit.ForwarderType.Datadog => ForwarderType.Datadog,
            GenAudit.ForwarderType.Elastic => ForwarderType.Elastic,
            GenAudit.ForwarderType.Honeycomb => ForwarderType.Honeycomb,
            GenAudit.ForwarderType.Http => ForwarderType.Http,
            GenAudit.ForwarderType.New_relic => ForwarderType.NewRelic,
            GenAudit.ForwarderType.Splunk_hec => ForwarderType.SplunkHec,
            GenAudit.ForwarderType.Sumo_logic => ForwarderType.SumoLogic,
            _ => throw new ArgumentOutOfRangeException(nameof(src), src, null),
        };

    private static IDictionary<string, ForwarderEnvironment> EnvironmentsFromGen(
        IDictionary<string, GenAudit.ForwarderEnvironment>? src)
    {
        var result = new Dictionary<string, ForwarderEnvironment>();
        if (src == null) return result;
        foreach (var (key, env) in src)
        {
            result[key] = new ForwarderEnvironment
            {
                Enabled = env.Enabled,
                Configuration = env.Configuration is { } cfg ? HttpFromGen(cfg) : null,
            };
        }
        return result;
    }

    private static HttpConfiguration HttpFromGen(GenAudit.HttpConfiguration? src)
    {
        if (src == null) return new HttpConfiguration { Url = string.Empty };
        var out_ = new HttpConfiguration
        {
            Method = HttpMethodExtensions.FromWireValue(src.Method.ToString()),
            Url = src.Url ?? string.Empty,
            SuccessStatus = src.Success_status ?? "2xx",
            TlsVerify = src.Tls_verify,
            CaCert = src.Ca_cert,
        };
        if (src.Headers != null)
        {
            out_.Headers = src.Headers
                .Select(h => new HttpHeader(h.Name ?? string.Empty, h.Value ?? string.Empty))
                .ToList();
        }
        return out_;
    }

    /// <summary>
    /// Normalize the wire <c>transform</c> field to a typed wrapper value.
    /// System.Text.Json deserializes the untyped wire field as <see cref="System.Text.Json.JsonElement"/>;
    /// unwrap it to the matching CLR type so customers see a real string for
    /// JSONATA responses (or a dict/list/scalar for future engines).
    /// </summary>
    private static object? ConvertTransform(object? raw) => raw switch
    {
        null => null,
        System.Text.Json.JsonElement el => JsonElementToObject(el),
        _ => raw,
    };

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
/// Runtime read surfaces (events, resource types, event types) live on
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
