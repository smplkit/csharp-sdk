using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Distinct resource_type slugs seen in the account's audit log.
/// Accessed via <see cref="AuditClient.ResourceTypes"/>.
///
/// <para>Backed by a maintain-by-write side table so the response time
/// is independent of how many years of events the account has accumulated.
/// Sorted alphabetically; offset pagination via <c>PageNumber</c> / <c>PageSize</c>.</para>
/// </summary>
public sealed class AuditResourceTypes
{
    private readonly GenAudit.AuditClient _gen;
    private readonly string? _environment;

    internal AuditResourceTypes(GenAudit.AuditClient gen, string? environment = null)
    {
        _gen = gen;
        _environment = environment;
    }

    /// <summary>List the distinct resource_type slugs recorded for this account.</summary>
    /// <param name="input">Pagination and environments scope; null lists with
    /// defaults. Set <see cref="ListResourceTypesInput.MetaTotal"/> to populate
    /// the total counts in the returned pagination meta.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A page of <see cref="ResourceType"/>s plus the pagination meta.</returns>
    public async Task<ListResourceTypesPage> ListAsync(
        ListResourceTypesInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListResourceTypesInput();
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_resource_typesAsync(
                filterenvironment: Helpers.ResolveEnvironmentFilter(input.Environments, _environment),
                sort: null,
                pagenumber: input.PageNumber,
                pagesize: input.PageSize,
                metatotal: input.MetaTotal,
                cancellationToken: ct)
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.ResourceTypeResource>())
            .Select(r => new ResourceType(r.Id ?? string.Empty, r.Attributes.Created_at))
            .ToList();
        return new ListResourceTypesPage(rows, ExtractPagination(resp.Meta));
    }

    internal static Pagination ExtractPagination(GenAudit.ListMeta? meta)
    {
        var p = meta?.Pagination ?? new GenAudit.PaginationMeta();
        return new Pagination(p.Page, p.Size, p.Total, p.Total_pages);
    }
}
