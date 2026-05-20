using System.Text;
using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Internal;
using GenApp = Smplkit.Internal.Generated.App;

namespace Smplkit.Management;

// ---------------------------------------------------------------------------
// Environments
// ---------------------------------------------------------------------------

/// <summary>
/// Provides CRUD operations for deployment environments. Accessible via
/// <see cref="SmplManagementClient.Environments"/>.
/// </summary>
public sealed class EnvironmentsClient
{
    private readonly GenApp.AppClient _appClient;

    internal EnvironmentsClient(GenApp.AppClient appClient) => _appClient = appClient;

    /// <summary>Creates an unsaved <see cref="Environment"/>.</summary>
    public Environment New(
        string id,
        string name,
        Color? color = null,
        EnvironmentClassification classification = EnvironmentClassification.Standard)
    {
        return new Environment(this, id: id, name: name, color: color,
            classification: classification, createdAt: null, updatedAt: null);
    }

    /// <summary>Convenience overload accepting a hex string for color (validated via <see cref="Color"/>).</summary>
    public Environment New(
        string id,
        string name,
        string color,
        EnvironmentClassification classification = EnvironmentClassification.Standard)
        => New(id, name, new Color(color), classification);

    /// <summary>Lists environments. Returns one page; defaults to the server's first page.</summary>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Environment>> ListAsync(
        int? pageNumber = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.List_environmentsAsync(
                pagenumber: pageNumber,
                pagesize: pageSize,
                cancellationToken: ct)).ConfigureAwait(false);
        return resp.Data.Select(MapResource).ToList();
    }

    /// <summary>Fetches an environment by id.</summary>
    public async Task<Environment> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_environmentAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Deletes an environment by id.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _appClient.Delete_environmentAsync(id, null, ct));

    internal async Task<Environment> SaveInternalAsync(Environment env, CancellationToken ct)
    {
        var body = BuildBody(env);
        GenApp.EnvironmentResponse resp;
        if (env.CreatedAt is null)
        {
            resp = await ApiExceptionMapper.ExecuteAsync(
                () => _appClient.Create_environmentAsync(body, ct)).ConfigureAwait(false);
        }
        else
        {
            resp = await ApiExceptionMapper.ExecuteAsync(
                () => _appClient.Update_environmentAsync(env.Id!, body, ct)).ConfigureAwait(false);
        }
        return MapResource(resp.Data);
    }

    private Environment MapResource(GenApp.EnvironmentResource r)
    {
        var attrs = r.Attributes;
        Color? color = string.IsNullOrEmpty(attrs.Color) ? null : new Color(attrs.Color);
        return new Environment(
            client: this,
            id: r.Id,
            name: attrs.Name ?? string.Empty,
            color: color,
            classification: EnvironmentClassificationExtensions.ParseClassification(attrs.Classification),
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static GenApp.EnvironmentRequest BuildBody(Environment env) =>
        new()
        {
            Data = new GenApp.EnvironmentResource
            {
                Id = env.Id,
                Type = GenApp.EnvironmentResourceType.Environment,
                Attributes = new GenApp.Environment
                {
                    Name = env.Name,
                    Color = env.Color?.Hex,
                    Classification = env.Classification.ToWireString(),
                },
            },
        };
}

// ---------------------------------------------------------------------------
// ContextTypes
// ---------------------------------------------------------------------------

/// <summary>
/// Provides CRUD operations for context types. Accessible via
/// <see cref="SmplManagementClient.ContextTypes"/>.
/// </summary>
public sealed class ContextTypesClient
{
    private readonly GenApp.AppClient _appClient;

    internal ContextTypesClient(GenApp.AppClient appClient) => _appClient = appClient;

    /// <summary>Creates an unsaved <see cref="ContextType"/>. <c>name</c> defaults to <c>id</c>.</summary>
    public ContextType New(
        string id,
        string? name = null,
        Dictionary<string, Dictionary<string, object?>>? attributes = null)
    {
        return new ContextType(this, id: id, name: name ?? id, attributes: attributes,
            createdAt: null, updatedAt: null);
    }

    /// <summary>Lists context types. Returns one page; defaults to the server's first page.</summary>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<ContextType>> ListAsync(
        int? pageNumber = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.List_context_typesAsync(null, pageNumber, pageSize, null, ct)).ConfigureAwait(false);
        return resp.Data.Select(MapResource).ToList();
    }

    /// <summary>Fetches a context type by id.</summary>
    public async Task<ContextType> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_context_typeAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Deletes a context type by id.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _appClient.Delete_context_typeAsync(id, ct));

    internal async Task<ContextType> SaveInternalAsync(ContextType ct, CancellationToken cancellationToken)
    {
        var body = BuildBody(ct);
        GenApp.ContextTypeResponse resp;
        if (ct.CreatedAt is null)
        {
            resp = await ApiExceptionMapper.ExecuteAsync(
                () => _appClient.Create_context_typeAsync(body, cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            resp = await ApiExceptionMapper.ExecuteAsync(
                () => _appClient.Update_context_typeAsync(ct.Id!, body, cancellationToken)).ConfigureAwait(false);
        }
        return MapResource(resp.Data);
    }

    private ContextType MapResource(GenApp.ContextTypeResource r)
    {
        var attrs = r.Attributes;
        var attributeMap = ParseAttributeMap(attrs.Attributes);
        return new ContextType(
            client: this,
            id: r.Id,
            name: attrs.Name ?? string.Empty,
            attributes: attributeMap,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static Dictionary<string, Dictionary<string, object?>> ParseAttributeMap(object? raw)
    {
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, Dictionary<string, object?>>();
            foreach (var prop in je.EnumerateObject())
            {
                var meta = new Dictionary<string, object?>();
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var innerProp in prop.Value.EnumerateObject())
                        meta[innerProp.Name] = Smplkit.Config.Resolver.Normalize(innerProp.Value);
                }
                result[prop.Name] = meta;
            }
            return result;
        }
        return new Dictionary<string, Dictionary<string, object?>>();
    }

    private static GenApp.ContextTypeRequest BuildBody(ContextType ct)
    {
        var attrsJson = JsonSerializer.Serialize(ct.Attributes);
        using var doc = JsonDocument.Parse(attrsJson);
        return new GenApp.ContextTypeRequest
        {
            Data = new GenApp.ContextTypeResource
            {
                Id = ct.Id,
                Type = GenApp.ContextTypeResourceType.Context_type,
                Attributes = new GenApp.ContextType
                {
                    Name = ct.Name,
                    Attributes = doc.RootElement.Clone(),
                },
            },
        };
    }
}

// ---------------------------------------------------------------------------
// Contexts
// ---------------------------------------------------------------------------

/// <summary>
/// Provides context registration and read/delete operations.
/// Accessible via <see cref="SmplManagementClient.Contexts"/>.
/// </summary>
public sealed class ContextsClient : IContextSink
{
    private readonly GenApp.AppClient _appClient;
    private readonly ContextRegistrationBuffer _buffer;

    internal ContextsClient(GenApp.AppClient appClient, ContextRegistrationBuffer buffer)
    {
        _appClient = appClient;
        _buffer = buffer;
    }

    /// <summary>Buffers a single context for registration; optionally flushes immediately.</summary>
    public Task RegisterAsync(Smplkit.Context context, bool flush = false, CancellationToken ct = default)
        => RegisterAsync(new[] { context }, flush, ct);

    /// <summary>Buffers contexts for registration; optionally flushes immediately.</summary>
    public async Task RegisterAsync(IEnumerable<Smplkit.Context> contexts, bool flush = false, CancellationToken ct = default)
    {
        _buffer.Observe(contexts);
        if (flush)
            await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns the count of pending context registrations not yet flushed.</summary>
    public int PendingCount => _buffer.PendingCount;

    /// <summary>Sends any pending context registrations to the server.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var batch = _buffer.Drain();
        if (batch.Count == 0) return;

        var items = batch.Select(b =>
        {
            var composite = b.TryGetValue("id", out var idVal) && idVal is string idStr ? idStr : "";
            var colonIdx = composite.IndexOf(':');
            return new GenApp.ContextBulkItem
            {
                Type = colonIdx >= 0 ? composite[..colonIdx] : composite,
                Key = colonIdx >= 0 ? composite[(colonIdx + 1)..] : "",
                Attributes = b.TryGetValue("attributes", out var attrs) ? attrs ?? new object() : new object(),
            };
        }).ToList();

        await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Bulk_register_contextsAsync(
                new GenApp.ContextBulkRegister { Contexts = items }, ct)).ConfigureAwait(false);
    }

    /// <summary>Lists contexts of a given type. Returns one page; defaults to the server's first page.</summary>
    /// <param name="type">Context type filter (required).</param>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Smplkit.Context>> ListAsync(
        string type,
        int? pageNumber = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.List_contextsAsync(type, null, null, pageNumber, pageSize, null, ct)).ConfigureAwait(false);
        return resp.Data.Select(MapResource).ToList();
    }

    /// <summary>Fetches a context by composite <c>"{type}:{key}"</c> id.</summary>
    public async Task<Smplkit.Context> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_contextAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Fetches a context by type and key.</summary>
    public Task<Smplkit.Context> GetAsync(string type, string key, CancellationToken ct = default)
        => GetAsync($"{type}:{key}", ct);

    /// <summary>Deletes a context by composite <c>"{type}:{key}"</c> id.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _appClient.Delete_contextAsync(id, ct));

    /// <summary>Deletes a context by type and key.</summary>
    public Task DeleteAsync(string type, string key, CancellationToken ct = default)
        => DeleteAsync($"{type}:{key}", ct);

    Task<Smplkit.Context> IContextSink.SaveContextAsync(Smplkit.Context ctx, CancellationToken ct)
        => SaveContextInternalAsync(ctx, ct);

    Task IContextSink.DeleteContextAsync(string id, CancellationToken ct) => DeleteAsync(id, ct);

    private async Task<Smplkit.Context> SaveContextInternalAsync(Smplkit.Context ctx, CancellationToken ct)
    {
        await RegisterAsync(new[] { ctx }, flush: true, ct).ConfigureAwait(false);
        return await GetAsync(ctx.Id, ct).ConfigureAwait(false);
    }

    private Smplkit.Context MapResource(GenApp.ContextResource r)
    {
        var composite = r.Id ?? "";
        var colonIdx = composite.IndexOf(':');
        var ctxType = colonIdx >= 0 ? composite[..colonIdx] : composite;
        var ctxKey = colonIdx >= 0 ? composite[(colonIdx + 1)..] : "";

        var attrs = r.Attributes;
        var attrDict = ParseAttributeDict(attrs.Attributes);

        return new Smplkit.Context(
            sink: this,
            type: ctxType,
            key: ctxKey,
            attributes: attrDict,
            name: attrs.Name,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static Dictionary<string, object?> ParseAttributeDict(object? raw)
    {
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
            return je.EnumerateObject().ToDictionary(p => p.Name, p => Smplkit.Config.Resolver.Normalize(p.Value));
        return new Dictionary<string, object?>();
    }
}

// ---------------------------------------------------------------------------
// AccountSettings
// ---------------------------------------------------------------------------

/// <summary>
/// Provides get/save operations for account-level settings.
/// Accessible via <see cref="SmplManagementClient.AccountSettings"/>.
/// </summary>
public sealed class AccountSettingsClient
{
    private readonly HttpClient _httpClient;
    private readonly string _appBaseUrl;

    internal AccountSettingsClient(HttpClient httpClient, string appBaseUrl)
    {
        _httpClient = httpClient;
        _appBaseUrl = appBaseUrl.TrimEnd('/');
    }

    /// <summary>Fetches the current account settings.</summary>
    public async Task<AccountSettings> GetAsync(CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_appBaseUrl}/api/v1/accounts/current/settings");
        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var data = await ReadSettingsDictAsync(response, ct).ConfigureAwait(false);
        return new AccountSettings(this, data);
    }

    internal async Task<AccountSettings> SaveInternalAsync(Dictionary<string, object?> data, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        var request = new HttpRequestMessage(HttpMethod.Put,
            $"{_appBaseUrl}/api/v1/accounts/current/settings")
        {
            Content = new ByteArrayContent(json),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var returned = await ReadSettingsDictAsync(response, ct).ConfigureAwait(false);
        return new AccountSettings(this, returned);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw ApiErrorParser.CreateException((int)response.StatusCode, body);
    }

    private static async Task<Dictionary<string, object?>> ReadSettingsDictAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0) return new Dictionary<string, object?>();

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new Dictionary<string, object?>();

        return root.EnumerateObject()
            .ToDictionary(p => p.Name, p => Smplkit.Config.Resolver.Normalize(p.Value));
    }
}
