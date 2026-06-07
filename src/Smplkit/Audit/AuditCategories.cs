using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Distinct category values seen in the account's audit log.
/// Accessed via <see cref="AuditClient.Categories"/>.
///
/// <para>Backed by a maintain-by-write side table populated whenever an
/// event is recorded with a non-null <c>category</c>. Sorted alphabetically;
/// offset pagination via <c>PageNumber</c> / <c>PageSize</c>.</para>
/// </summary>
public sealed class AuditCategories
{
    private readonly GenAudit.AuditClient _gen;

    internal AuditCategories(GenAudit.AuditClient gen) => _gen = gen;

    /// <summary>List the distinct category values recorded for this account.</summary>
    public async Task<ListCategoriesPage> ListAsync(
        ListCategoriesInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListCategoriesInput();
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_categoriesAsync(
                filterenvironment: Helpers.JoinEnvironments(input.Environments),
                sort: null,
                pagenumber: input.PageNumber,
                pagesize: input.PageSize,
                metatotal: input.MetaTotal,
                cancellationToken: ct)
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.CategoryResource>())
            .Select(r => new AuditCategory(r.Id ?? string.Empty, r.Attributes.Created_at))
            .ToList();
        return new ListCategoriesPage(rows, AuditResourceTypes.ExtractPagination(resp.Meta));
    }
}
