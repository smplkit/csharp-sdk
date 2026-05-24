using Smplkit.Errors;
using Smplkit.Internal;
using GenApp = Smplkit.Internal.Generated.App;

namespace Smplkit.Management;

/// <summary>
/// Provides CRUD operations for services. Accessible via
/// <see cref="SmplManagementClient.Services"/>.
/// </summary>
public sealed class ServicesClient
{
    private readonly GenApp.AppClient _appClient;

    internal ServicesClient(GenApp.AppClient appClient) => _appClient = appClient;

    /// <summary>Creates an unsaved <see cref="Service"/>.</summary>
    public Service New(string id, string name)
    {
        return new Service(this, id: id, name: name, createdAt: null, updatedAt: null);
    }

    /// <summary>Lists services. Returns one page; defaults to the server's first page.</summary>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Service>> ListAsync(
        int? pageNumber = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.List_servicesAsync(
                pagenumber: pageNumber,
                pagesize: pageSize,
                cancellationToken: ct)).ConfigureAwait(false);
        return resp.Data.Select(MapResource).ToList();
    }

    /// <summary>Fetches a service by id.</summary>
    public async Task<Service> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_serviceAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Deletes a service by id.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _appClient.Delete_serviceAsync(id, ct));

    internal async Task<Service> SaveInternalAsync(Service svc, CancellationToken ct)
    {
        GenApp.ServiceResponse resp;
        if (svc.CreatedAt is null)
        {
            var createBody = BuildCreateBody(svc);
            resp = await ApiExceptionMapper.ExecuteAsync(
                () => _appClient.Create_serviceAsync(createBody, ct)).ConfigureAwait(false);
        }
        else
        {
            var updateBody = BuildBody(svc);
            resp = await ApiExceptionMapper.ExecuteAsync(
                () => _appClient.Update_serviceAsync(svc.Id!, updateBody, ct)).ConfigureAwait(false);
        }
        return MapResource(resp.Data);
    }

    private Service MapResource(GenApp.ServiceResource r)
    {
        var attrs = r.Attributes;
        return new Service(
            client: this,
            id: r.Id,
            name: attrs.Name ?? string.Empty,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static GenApp.ServiceRequest BuildBody(Service svc) =>
        new()
        {
            Data = new GenApp.ServiceResource
            {
                Id = svc.Id,
                Type = GenApp.ServiceResourceType.Service,
                Attributes = BuildAttributes(svc),
            },
        };

    private static GenApp.ServiceCreateRequest BuildCreateBody(Service svc) =>
        new()
        {
            Data = new GenApp.ServiceCreateResource
            {
                Id = svc.Id ?? throw new ValidationException("Cannot create a service without an id"),
                Type = GenApp.ServiceCreateResourceType.Service,
                Attributes = BuildAttributes(svc),
            },
        };

    private static GenApp.Service BuildAttributes(Service svc) =>
        new()
        {
            Name = svc.Name,
        };
}
