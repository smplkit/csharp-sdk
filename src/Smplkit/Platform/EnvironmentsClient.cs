using Smplkit.Errors;
using Smplkit.Internal;
using GenApp = Smplkit.Internal.Generated.App;

namespace Smplkit.Platform;

/// <summary>
/// Environment CRUD (<c>client.Platform.Environments</c>).
/// </summary>
public sealed class EnvironmentsClient
{
    private readonly GenApp.AppClient _appClient;

    internal EnvironmentsClient(GenApp.AppClient appClient) => _appClient = appClient;

    /// <summary>Return an unsaved <see cref="Environment"/>. Call <c>SaveAsync()</c> to persist.</summary>
    /// <param name="id">Stable, human-readable identifier for the environment (for example <c>"production"</c>).</param>
    /// <param name="name">Display name shown in the Console.</param>
    /// <param name="color">Accent color for the environment, as a <see cref="Color"/> or a CSS hex string. Defaults to no color.</param>
    /// <param name="classification">Whether the environment participates in the standard environment ordering. Defaults to <see cref="EnvironmentClassification.Standard"/>.</param>
    /// <returns>An unsaved <see cref="Environment"/> bound to this client.</returns>
    public Environment New(
        string id,
        string name,
        Color? color = null,
        EnvironmentClassification classification = EnvironmentClassification.Standard)
    {
        return new Environment(
            this,
            id: id,
            name: name,
            color: color,
            classification: classification,
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>Return an unsaved <see cref="Environment"/>. Call <c>SaveAsync()</c> to persist.</summary>
    /// <remarks>Convenience overload accepting a hex string for color (validated via <see cref="Color"/>).</remarks>
    /// <param name="id">Stable, human-readable identifier for the environment (for example <c>"production"</c>).</param>
    /// <param name="name">Display name shown in the Console.</param>
    /// <param name="color">Accent color for the environment, as a CSS hex string. Defaults to no color.</param>
    /// <param name="classification">Whether the environment participates in the standard environment ordering. Defaults to <see cref="EnvironmentClassification.Standard"/>.</param>
    /// <returns>An unsaved <see cref="Environment"/> bound to this client.</returns>
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
    /// <param name="id">Identifier of the environment to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Environment"/>.</returns>
    /// <exception cref="Smplkit.Errors.NotFoundException">If no environment with that id exists.</exception>
    public async Task<Environment> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_environmentAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Deletes an environment by id.</summary>
    /// <param name="id">Identifier of the environment to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _appClient.Delete_environmentAsync(id, null, ct));

    internal async Task<Environment> SaveInternalAsync(Environment env, CancellationToken ct)
    {
        GenApp.EnvironmentResponse resp;
        if (env.CreatedAt is null)
        {
            var createBody = BuildCreateBody(env);
            resp = await ApiExceptionMapper.ExecuteAsync(
                () => _appClient.Create_environmentAsync(createBody, ct)).ConfigureAwait(false);
        }
        else
        {
            var updateBody = BuildBody(env);
            resp = await ApiExceptionMapper.ExecuteAsync(
                () => _appClient.Update_environmentAsync(env.Id!, updateBody, ct)).ConfigureAwait(false);
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
                Attributes = BuildAttributes(env),
            },
        };

    private static GenApp.EnvironmentCreateRequest BuildCreateBody(Environment env) =>
        new()
        {
            Data = new GenApp.EnvironmentCreateResource
            {
                Id = env.Id ?? throw new ValidationException("Cannot create an environment without an id"),
                Type = GenApp.EnvironmentCreateResourceType.Environment,
                Attributes = BuildAttributes(env),
            },
        };

    private static GenApp.Environment BuildAttributes(Environment env) =>
        new()
        {
            Name = env.Name,
            Color = env.Color?.Hex,
            Classification = env.Classification.ToWireString(),
        };
}
