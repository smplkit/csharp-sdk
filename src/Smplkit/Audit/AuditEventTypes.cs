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
    private readonly string? _environment;

    internal AuditEventTypes(GenAudit.AuditClient gen, string? environment = null)
    {
        _gen = gen;
        _environment = environment;
    }

    /// <summary>List the distinct event type slugs recorded for this account.</summary>
    /// <param name="input">Resource-type filter, pagination, and environments
    /// scope; null lists with defaults. Set <see cref="ListEventTypesInput.MetaTotal"/>
    /// to populate the total counts in the returned pagination meta.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A page of <see cref="AuditEventType"/>s plus the pagination meta.</returns>
    public async Task<EventTypeListPage> ListAsync(
        ListEventTypesInput? input = null, CancellationToken ct = default)
    {
        input ??= new ListEventTypesInput();
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_event_typesAsync(
                filterenvironment: Helpers.ResolveEnvironmentFilter(input.Environments, _environment),
                filterresource_type: input.FilterResourceType,
                sort: null,
                pagenumber: input.PageNumber,
                pagesize: input.PageSize,
                metatotal: input.MetaTotal,
                cancellationToken: ct)
        ).ConfigureAwait(false);
        var rows = (resp.Data ?? new List<GenAudit.EventTypeResource>())
            .Select(r => new AuditEventType(r.Id ?? string.Empty, r.Attributes.Created_at))
            .ToList();
        return new EventTypeListPage(rows, AuditResourceTypes.ExtractPagination(resp.Meta));
    }
}
