using Smplkit.Internal;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Distinct event type slugs seen in the account's audit log.
/// Accessed via <see cref="AuditClient.EventTypes"/>.
///
/// <para>Without <c>FilterResourceType</c>, returns one row per distinct
/// event type. With the filter, returns the event types seen with that specific
/// resource_type — useful for cascading filter dropdowns.</para>
///
/// <para>Sorted alphabetically; offset pagination via <c>PageNumber</c> / <c>PageSize</c>.</para>
/// </summary>
public sealed class AuditEventTypes
{
    private readonly GenAudit.AuditClient _gen;

    internal AuditEventTypes(GenAudit.AuditClient gen) => _gen = gen;

    /// <summary>List the distinct event type slugs recorded for this account.</summary>
    public async Task<EventTypeListPage> ListAsync(
        ListEventTypesInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListEventTypesInput();
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_event_typesAsync(input.FilterResourceType, null, input.PageNumber, input.PageSize, input.MetaTotal, ct)
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.EventTypeResource>())
            .Select(r => new AuditEventType(r.Id ?? string.Empty, r.Attributes.Created_at))
            .ToList();
        return new EventTypeListPage(rows, AuditResourceTypes.ExtractPagination(resp.Meta));
    }
}
