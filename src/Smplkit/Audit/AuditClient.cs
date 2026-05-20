using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Audit-product runtime entry point — accessed via <c>SmplClient.Audit</c>.
///
/// <para>Sub-clients: <see cref="Events"/> for event recording / listing /
/// retrieval (fire-and-forget <c>Record</c> plus list/get reads);
/// <see cref="ResourceTypes"/> for distinct resource_type slugs;
/// <see cref="EventTypes"/> for distinct event_type slugs.</para>
///
/// <para>SIEM forwarder CRUD lives on the management plane —
/// <c>SmplManagementClient.Audit.Forwarders</c>.</para>
/// </summary>
public sealed class AuditClient : IAsyncDisposable
{
    /// <summary>Events sub-client.</summary>
    public AuditEvents Events { get; }

    /// <summary>Distinct resource_type slugs sub-client.</summary>
    public AuditResourceTypes ResourceTypes { get; }

    /// <summary>Distinct event_type slugs sub-client.</summary>
    public AuditEventTypes EventTypes { get; }

    internal AuditClient(GenAudit.AuditClient generated)
    {
        Events = new AuditEvents(generated);
        ResourceTypes = new AuditResourceTypes(generated);
        EventTypes = new AuditEventTypes(generated);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Events.DisposeAsync().ConfigureAwait(false);
    }
}
