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
/// Provides CRUD operations for deployment environments.
/// Accessible via <see cref="ManagementClient.Environments"/>.
/// </summary>
public sealed class EnvironmentsClient
{
    private readonly GenApp.AppClient _appClient;

    internal EnvironmentsClient(GenApp.AppClient appClient) => _appClient = appClient;

    /// <summary>
    /// Creates an unsaved <see cref="SmplEnvironment"/>. Call <see cref="SmplEnvironment.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The environment slug.</param>
    /// <param name="name">Display name.</param>
    /// <param name="color">Optional hex color string.</param>
    /// <param name="classification">Standard or AdHoc.</param>
    /// <returns>An unsaved <see cref="SmplEnvironment"/>.</returns>
    public SmplEnvironment New(
        string id,
        string name,
        string? color = null,
        EnvironmentClassification classification = EnvironmentClassification.Standard)
    {
        return new SmplEnvironment(this, id: id, name: name, color: color,
            classification: classification, createdAt: null, updatedAt: null);
    }

    /// <summary>Lists all environments.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<SmplEnvironment>> ListAsync(CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.List_environmentsAsync(ct)).ConfigureAwait(false);
        return resp.Data.Select(r => MapResource(r)).ToList();
    }

    /// <summary>Fetches an environment by its identifier.</summary>
    /// <param name="id">The environment slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching environment exists.</exception>
    public async Task<SmplEnvironment> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_environmentAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Deletes an environment by its identifier.</summary>
    /// <param name="id">The environment slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching environment exists.</exception>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _appClient.Delete_environmentAsync(id, ct));

    internal async Task<SmplEnvironment> SaveInternalAsync(SmplEnvironment env, CancellationToken ct)
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

    private SmplEnvironment MapResource(GenApp.EnvironmentResource r)
    {
        var attrs = r.Attributes;
        return new SmplEnvironment(
            client: this,
            id: r.Id,
            name: attrs.Name ?? string.Empty,
            color: attrs.Color,
            classification: EnvironmentClassificationExtensions.ParseClassification(attrs.Classification),
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static GenApp.EnvironmentResponse BuildBody(SmplEnvironment env) =>
        new()
        {
            Data = new GenApp.EnvironmentResource
            {
                Id = env.Id,
                Type = GenApp.EnvironmentResourceType.Environment,
                Attributes = new GenApp.Environment
                {
                    Name = env.Name,
                    Color = env.Color,
                    Classification = env.Classification.ToWireString(),
                },
            },
        };
}

// ---------------------------------------------------------------------------
// ContextTypes
// ---------------------------------------------------------------------------

/// <summary>
/// Provides CRUD operations for context types.
/// Accessible via <see cref="ManagementClient.ContextTypes"/>.
/// </summary>
public sealed class ContextTypesClient
{
    private readonly GenApp.AppClient _appClient;

    internal ContextTypesClient(GenApp.AppClient appClient) => _appClient = appClient;

    /// <summary>
    /// Creates an unsaved <see cref="ContextType"/>. Call <see cref="ContextType.SaveAsync"/> to persist.
    /// </summary>
    /// <param name="id">The context type slug.</param>
    /// <param name="name">Optional display name; defaults to id if null.</param>
    /// <param name="attributes">Optional initial attribute map.</param>
    /// <returns>An unsaved <see cref="ContextType"/>.</returns>
    public ContextType New(
        string id,
        string? name = null,
        Dictionary<string, Dictionary<string, object?>>? attributes = null)
    {
        return new ContextType(this, id: id, name: name ?? id, attributes: attributes,
            createdAt: null, updatedAt: null);
    }

    /// <summary>Lists all context types.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<ContextType>> ListAsync(CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.List_context_typesAsync(ct)).ConfigureAwait(false);
        return resp.Data.Select(r => MapResource(r)).ToList();
    }

    /// <summary>Fetches a context type by its identifier.</summary>
    /// <param name="id">The context type slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching context type exists.</exception>
    public async Task<ContextType> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_context_typeAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Deletes a context type by its identifier.</summary>
    /// <param name="id">The context type slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching context type exists.</exception>
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
                        meta[innerProp.Name] = Config.Resolver.Normalize(innerProp.Value);
                }
                result[prop.Name] = meta;
            }
            return result;
        }
        return new Dictionary<string, Dictionary<string, object?>>();
    }

    private static GenApp.ContextTypeResponse BuildBody(ContextType ct)
    {
        // Serialize attributes map as a JSON object string to pass through the opaque 'object' field
        var attrsJson = JsonSerializer.Serialize(ct.Attributes);
        using var doc = JsonDocument.Parse(attrsJson);
        return new GenApp.ContextTypeResponse
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
/// Accessible via <see cref="ManagementClient.Contexts"/>.
/// </summary>
/// <remarks>
/// <para>
/// Write path: <see cref="RegisterAsync(Context, bool, CancellationToken)"/> /
/// <see cref="RegisterAsync(IEnumerable{Context}, bool, CancellationToken)"/>. With
/// <c>flush: false</c> (default) contexts are queued for background flush — right for
/// high-frequency runtime observation. With <c>flush: true</c> the call awaits the
/// network round-trip — right for IaC scripts that need confirmation.
/// </para>
/// <para>
/// Read path: <see cref="ListAsync"/>, <see cref="GetAsync(string, CancellationToken)"/>,
/// <see cref="DeleteAsync(string, CancellationToken)"/>.
/// The composite <c>"{type}:{key}"</c> id form is accepted everywhere an id appears;
/// a two-argument <c>(type, key)</c> overload is also provided for ergonomics.
/// </para>
/// </remarks>
public sealed class ContextsClient
{
    private readonly GenApp.AppClient _appClient;
    private readonly ContextRegistrationBuffer _buffer;

    internal ContextsClient(GenApp.AppClient appClient, ContextRegistrationBuffer buffer)
    {
        _appClient = appClient;
        _buffer = buffer;
    }

    /// <summary>
    /// Buffers a single context for registration; optionally flushes immediately.
    /// </summary>
    /// <param name="context">The context to register.</param>
    /// <param name="flush">When true, sends buffered contexts to the server before returning.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RegisterAsync(Context context, bool flush = false, CancellationToken ct = default)
        => RegisterAsync(new[] { context }, flush, ct);

    /// <summary>
    /// Buffers contexts for registration; optionally flushes immediately.
    /// </summary>
    /// <param name="contexts">Contexts to register.</param>
    /// <param name="flush">When true, sends buffered contexts to the server before returning.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RegisterAsync(IEnumerable<Context> contexts, bool flush = false, CancellationToken ct = default)
    {
        _buffer.Observe(contexts);
        if (flush)
            await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sends any pending context registrations to the server.</summary>
    /// <param name="ct">Cancellation token.</param>
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

    /// <summary>Lists all contexts of a given type.</summary>
    /// <param name="type">The context type key (e.g., "user", "account").</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<ContextEntity>> ListAsync(string type, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.List_contextsAsync(filtercontext_type: type, cancellationToken: ct)).ConfigureAwait(false);
        return resp.Data.Select(MapResource).ToList();
    }

    /// <summary>Fetches a context by its composite <c>"{type}:{key}"</c> identifier.</summary>
    /// <param name="id">Composite id in <c>"{type}:{key}"</c> form.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching context exists.</exception>
    public async Task<ContextEntity> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_contextAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Fetches a context by type and key.</summary>
    /// <param name="type">The context type key.</param>
    /// <param name="key">The entity identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching context exists.</exception>
    public Task<ContextEntity> GetAsync(string type, string key, CancellationToken ct = default)
        => GetAsync($"{type}:{key}", ct);

    /// <summary>Deletes a context by its composite <c>"{type}:{key}"</c> identifier.</summary>
    /// <param name="id">Composite id in <c>"{type}:{key}"</c> form.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching context exists.</exception>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _appClient.Delete_contextAsync(id, ct));

    /// <summary>Deletes a context by type and key.</summary>
    /// <param name="type">The context type key.</param>
    /// <param name="key">The entity identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SmplNotFoundException">If no matching context exists.</exception>
    public Task DeleteAsync(string type, string key, CancellationToken ct = default)
        => DeleteAsync($"{type}:{key}", ct);

    private static ContextEntity MapResource(GenApp.ContextResource r)
    {
        var composite = r.Id ?? "";
        var colonIdx = composite.IndexOf(':');
        var ctxType = colonIdx >= 0 ? composite[..colonIdx] : composite;
        var ctxKey = colonIdx >= 0 ? composite[(colonIdx + 1)..] : "";

        var attrs = r.Attributes;
        var attrDict = ParseAttributeDict(attrs.Attributes);

        return new ContextEntity(
            type: ctxType,
            key: ctxKey,
            name: attrs.Name,
            attributes: attrDict,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static Dictionary<string, object?> ParseAttributeDict(object? raw)
    {
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
            return je.EnumerateObject().ToDictionary(p => p.Name, p => Config.Resolver.Normalize(p.Value));
        return new Dictionary<string, object?>();
    }
}

// ---------------------------------------------------------------------------
// AccountSettings
// ---------------------------------------------------------------------------

/// <summary>
/// Provides get/save operations for account-level settings.
/// Accessible via <see cref="ManagementClient.AccountSettings"/>.
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
    /// <param name="ct">Cancellation token.</param>
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
            .ToDictionary(p => p.Name, p => Config.Resolver.Normalize(p.Value));
    }
}

// ---------------------------------------------------------------------------
// ManagementClient (top-level facade)
// ---------------------------------------------------------------------------

/// <summary>
/// Management plane for app-service-owned resources: environments, context types,
/// contexts, and account settings. Accessible via <see cref="SmplClient.Management"/>.
/// </summary>
public sealed class ManagementClient
{
    /// <summary>Gets the environments sub-client.</summary>
    public EnvironmentsClient Environments { get; }

    /// <summary>Gets the context types sub-client.</summary>
    public ContextTypesClient ContextTypes { get; }

    /// <summary>Gets the contexts sub-client (registration + read/delete).</summary>
    public ContextsClient Contexts { get; }

    /// <summary>Gets the account settings sub-client.</summary>
    public AccountSettingsClient AccountSettings { get; }

    internal ManagementClient(
        GenApp.AppClient appClient,
        HttpClient httpClient,
        ContextRegistrationBuffer buffer,
        string appBaseUrl)
    {
        Environments = new EnvironmentsClient(appClient);
        ContextTypes = new ContextTypesClient(appClient);
        Contexts = new ContextsClient(appClient, buffer);
        AccountSettings = new AccountSettingsClient(httpClient, appBaseUrl);
    }
}
