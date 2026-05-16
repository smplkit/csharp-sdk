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
/// <para>Sorted alphabetically; offset pagination via <c>PageNumber</c> / <c>PageSize</c>.</para>
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
            () => _gen.List_actionsAsync(input.FilterResourceType, null, input.PageNumber, input.PageSize, input.MetaTotal, ct)
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.ActionResource>())
            .Select(r => new AuditAction(r.Id ?? string.Empty, r.Attributes.Created_at))
            .ToList();
        return new ListActionsPage(rows, AuditResourceTypes.ExtractPagination(resp.Meta));
    }
}
