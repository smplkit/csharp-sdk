using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Distinct action slugs seen in the account's audit log.
/// Accessed via <see cref="AuditClient.Actions"/>.
///
/// <para>Without <c>FilterResourceType</c>, returns one row per distinct
/// action. With the filter, returns the actions seen with that specific
/// resource_type — useful for cascading filter dropdowns.</para>
///
/// <para>Sorted alphabetically; cursor pagination via <c>PageAfter</c>.</para>
/// </summary>
public sealed class AuditActions
{
    private readonly GenAudit.AuditClient _gen;

    internal AuditActions(GenAudit.AuditClient gen) => _gen = gen;

    /// <summary>List the distinct action slugs recorded for this account.</summary>
    public async Task<ListActionsPage> ListAsync(
        ListActionsInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListActionsInput();
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_actionsAsync(input.FilterResourceType, input.PageSize, input.PageAfter, ct)
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.ActionResource>())
            .Select(r => new AuditAction(r.Id ?? string.Empty, r.Attributes.Created_at))
            .ToList();
        return new ListActionsPage(rows, ExtractCursor(resp.Links?.Next));
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
