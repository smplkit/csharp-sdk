// SIEM forwarder CRUD for the Smpl Audit client.
//
// Forwarders are part of the single unified audit surface (see
// Smplkit.Audit.AuditClient). This file holds the forwarder CRUD sub-client
// that the unified AuditClient exposes as .Forwarders:
//
// * ForwardersClient — Forwarders.New/GetAsync/ListAsync/SaveAsync/DeleteAsync,
//   returning Forwarder active records whose SaveAsync() / DeleteAsync() perform
//   their network round-trips with await.
//
// The forwarder models (Forwarder, ForwarderEnvironment, HttpConfiguration, …)
// live in Smplkit.Audit.AuditModels.

using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// SIEM forwarder CRUD surface — accessed via <see cref="AuditClient.Forwarders"/>.
///
/// <para>Genuinely async — every method performs its network round-trip with
/// <c>await</c> and returns <see cref="Forwarder"/> active records whose
/// <see cref="Forwarder.SaveAsync"/> / <see cref="Forwarder.DeleteAsync"/> are
/// also asynchronous.</para>
///
/// <para>The customer-facing API is active-record: use <see cref="New"/> to
/// build an unsaved <see cref="Forwarder"/>, mutate fields directly, then
/// call <see cref="Forwarder.SaveAsync"/> to persist. Reads come from
/// <see cref="GetAsync"/> / <see cref="ListAsync"/>; the returned instances
/// are bound to this client so <c>SaveAsync()</c>/<c>DeleteAsync()</c> work.</para>
/// </summary>
public sealed class ForwardersClient
{
    private readonly GenAudit.AuditClient _gen;

    internal ForwardersClient(GenAudit.AuditClient gen) => _gen = gen;

    /// <summary>
    /// Return an unsaved <see cref="Forwarder"/>. Call <see cref="Forwarder.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">Caller-supplied unique identifier (the forwarder's key).
    /// Unique within the account; immutable for the lifetime of the forwarder.
    /// The audit service returns 409 if another live forwarder already uses
    /// this id.</param>
    /// <param name="forwarderType">A <see cref="Smplkit.Audit.ForwarderType"/> enum member
    /// (e.g. <c>ForwarderType.Http</c>, <c>ForwarderType.Datadog</c>).</param>
    /// <param name="configuration">Destination HTTP request configuration — an
    /// <see cref="HttpConfiguration"/> instance. Header values often carry
    /// credentials and are returned in plaintext on reads, so a get-mutate-put
    /// round-trip preserves them without re-entering secrets.</param>
    /// <param name="name">Display name. Free-form. Defaults to <paramref name="id"/>
    /// when not supplied.</param>
    /// <param name="environments">Per-environment overrides keyed by environment
    /// key (e.g. <c>"production"</c>). A forwarder delivers in an environment only
    /// when that environment's entry has <c>Enabled = true</c>. Omit to create a
    /// forwarder that delivers nowhere until enabled per environment.</param>
    /// <param name="description">Optional free-text description.</param>
    /// <param name="forwardSmplkitEvents">When <c>true</c>, this forwarder also
    /// receives platform change events that smplkit records about your own
    /// resources (flag, configuration, and similar changes), delivered through
    /// every environment this forwarder is enabled in. Independent of the
    /// per-environment <paramref name="environments"/> settings. Defaults to
    /// <c>false</c> — platform change events are not forwarded unless you opt
    /// in.</param>
    /// <param name="filter">Optional JSON Logic filter; events that don't match
    /// are recorded as <c>filtered_out</c> deliveries.</param>
    /// <param name="transform">Optional template applied to the event payload
    /// before POST. Shape depends on <paramref name="transformType"/> — for
    /// <see cref="TransformType.Jsonata"/>, a string containing a JSONata
    /// expression. Any value of any type is accepted. <c>null</c> sends the
    /// event as-is.</param>
    /// <param name="transformType">A <see cref="Smplkit.Audit.TransformType"/> enum member
    /// naming the engine that evaluates <paramref name="transform"/>. Must be
    /// provided together with <paramref name="transform"/> — neither field is
    /// meaningful in isolation.</param>
    /// <exception cref="ArgumentException">If exactly one of
    /// <paramref name="transform"/> / <paramref name="transformType"/> is
    /// provided, or if <paramref name="transformType"/> is
    /// <see cref="TransformType.Jsonata"/> and <paramref name="transform"/> is
    /// not a string.</exception>
    public Forwarder New(
        string id,
        ForwarderType forwarderType,
        HttpConfiguration configuration,
        string? name = null,
        IDictionary<string, ForwarderEnvironment>? environments = null,
        string? description = null,
        bool forwardSmplkitEvents = false,
        IDictionary<string, object?>? filter = null,
        object? transform = null,
        TransformType? transformType = null)
    {
        Forwarder.ValidateTransformPairing(transform, transformType);
        return new Forwarder(
            this,
            id: id,
            name: name ?? id,
            forwarderType: forwarderType,
            configuration: configuration,
            environments: environments,
            description: description,
            forwardSmplkitEvents: forwardSmplkitEvents,
            filter: filter,
            transform: transform,
            transformType: transformType);
    }

    /// <summary>List forwarders for the authenticated account.
    ///
    /// <para>Offset paginated: pass <c>PageNumber</c> (1-based) and
    /// <c>PageSize</c> (default 1000, max 1000). Pass <c>MetaTotal = true</c> to
    /// populate <c>Total</c> and <c>TotalPages</c> in the returned
    /// <c>Pagination</c> (costs an extra COUNT query server-side).</para></summary>
    /// <param name="input">Forwarder-type filter and pagination; null lists with defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A page of <see cref="Forwarder"/>s plus the pagination meta.</returns>
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

    /// <summary>Fetch a single forwarder by id; returned instance is bound to this
    /// client so <see cref="Forwarder.SaveAsync"/> and <see cref="Forwarder.DeleteAsync"/> work.
    /// Header values come back in plaintext, so a fetched forwarder round-trips
    /// through <see cref="Forwarder.SaveAsync"/> without re-entering secrets.</summary>
    /// <param name="forwarderId">Identifier of the forwarder to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Forwarder"/>, bound to this client.</returns>
    /// <exception cref="Smplkit.Errors.NotFoundException">If no forwarder with that id exists for the account.</exception>
    public async Task<Forwarder> GetAsync(string forwarderId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_forwarderAsync(forwarderId, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Delete a forwarder. Prefer <see cref="Forwarder.DeleteAsync"/>
    /// when you already have a <see cref="Forwarder"/> instance.</summary>
    /// <param name="forwarderId">Identifier of the forwarder to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task DeleteAsync(string forwarderId, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _gen.Delete_forwarderAsync(forwarderId, ct));

    // ------------------------------------------------------------------
    // Internal: drive Forwarder.SaveAsync
    // ------------------------------------------------------------------

    /// <summary>POST a new forwarder. Called by <see cref="Forwarder.SaveAsync"/> on unsaved
    /// instances; not intended for direct use.</summary>
    internal async Task<Forwarder> SaveCreateAsync(Forwarder forwarder, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(forwarder.Id))
            throw new InvalidOperationException("Forwarder.Id is required on create (caller-supplied key)");
        var body = WrapForwarderForCreate(forwarder);
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Create_forwarderAsync(body, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Full-replace PUT for an existing forwarder. Called by
    /// <see cref="Forwarder.SaveAsync"/> on instances with <c>CreatedAt</c>; not
    /// intended for direct use.
    ///
    /// <para>Header values come back in plaintext on the GET path, so a fetched
    /// forwarder round-trips through this full-replace PUT with its header values
    /// intact — no need to re-enter secrets.</para></summary>
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
        // Additive opt-in; only put it on the wire when enabled so the default
        // (false) stays implicit and existing callers' bodies are unchanged.
        attrs.Forward_smplkit_events = src.ForwardSmplkitEvents;
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
            forwardSmplkitEvents: a.Forward_smplkit_events,
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
