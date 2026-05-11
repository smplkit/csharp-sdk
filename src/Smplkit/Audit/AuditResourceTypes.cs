using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Distinct resource_type slugs seen in the account's audit log.
/// Accessed via <see cref="AuditClient.ResourceTypes"/>.
///
/// <para>Backed by a maintain-by-write side table so the response time
/// is independent of how many years of events the account has accumulated.
/// Sorted alphabetically; cursor pagination via <c>PageAfter</c>.</para>
/// </summary>
public sealed class AuditResourceTypes
{
    private readonly GenAudit.AuditClient _gen;

    internal AuditResourceTypes(GenAudit.AuditClient gen) => _gen = gen;

    /// <summary>List the distinct resource_type slugs recorded for this account.</summary>
    public async Task<ListResourceTypesPage> ListAsync(
        ListResourceTypesInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListResourceTypesInput();
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_resource_typesAsync(input.PageSize, input.PageAfter, ct)
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.ResourceTypeResource>())
            .Select(r => new ResourceType(r.Id ?? string.Empty, r.Attributes.Created_at))
            .ToList();
        return new ListResourceTypesPage(rows, ExtractCursor(resp.Links?.Next));
    }

    private static string? ExtractCursor(string? link)
    {
        if (string.IsNullOrEmpty(link)) return null;
        var idx = link.IndexOf("page[after]=", StringComparison.Ordinal);
        if (idx < 0) return null;
        var token = link.Substring(idx + "page[after]=".Length);
        var amp = token.IndexOf('&');
        return amp >= 0 ? token.Substring(0, amp) : token;
    }
}
