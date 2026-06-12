using System.Text.Json;
using Smplkit.Internal;
using GenApp = Smplkit.Internal.Generated.App;

namespace Smplkit.Platform;

/// <summary>
/// Context-type CRUD (<c>client.Platform.ContextTypes</c>).
/// </summary>
public sealed class ContextTypesClient
{
    private readonly GenApp.AppClient _appClient;

    internal ContextTypesClient(GenApp.AppClient appClient) => _appClient = appClient;

    /// <summary>Creates an unsaved <see cref="ContextType"/>. <c>name</c> defaults to <c>id</c>.</summary>
    /// <param name="id">Stable, human-readable identifier for the context type (for example <c>"user"</c>).</param>
    /// <param name="name">Display name shown in the Console. Defaults to <paramref name="id"/> when omitted.</param>
    /// <param name="attributes">Known-attribute slots, keyed by attribute name, with a metadata dictionary per slot. Defaults to no declared attributes.</param>
    /// <returns>An unsaved <see cref="ContextType"/> bound to this client.</returns>
    public ContextType New(
        string id,
        string? name = null,
        Dictionary<string, Dictionary<string, object?>>? attributes = null)
    {
        return new ContextType(
            this,
            id: id,
            name: name ?? id,
            attributes: attributes,
            createdAt: null,
            updatedAt: null);
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
    /// <param name="id">Identifier of the context type to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="ContextType"/>.</returns>
    /// <exception cref="Smplkit.Errors.NotFoundException">If no context type with that id exists.</exception>
    public async Task<ContextType> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_context_typeAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Deletes a context type by id.</summary>
    /// <param name="id">Identifier of the context type to delete.</param>
    /// <param name="ct">Cancellation token.</param>
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
