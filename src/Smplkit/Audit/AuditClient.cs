using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Audit-product entry point — accessed via <c>SmplClient.Audit</c>.
///
/// <para>Sub-clients: <see cref="Events"/> for event recording / listing /
/// retrieval, <see cref="Forwarders"/> for SIEM streaming destinations and
/// the delivery log (Pro tier only — lower tiers get a wrapped 402),
/// <see cref="Functions"/> for server-side proxy actions like
/// <c>test_forwarder/execute</c>.</para>
/// </summary>
public sealed class AuditClient : IAsyncDisposable
{
    /// <summary>Events sub-client.</summary>
    public AuditEvents Events { get; }

    /// <summary>SIEM forwarders sub-client.</summary>
    public AuditForwarders Forwarders { get; }

    /// <summary>Server-side functions sub-client.</summary>
    public AuditFunctions Functions { get; }

    internal AuditClient(GenAudit.AuditClient generated)
    {
        Events = new AuditEvents(generated);
        Forwarders = new AuditForwarders(generated);
        Functions = new AuditFunctions(generated);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Events.DisposeAsync().ConfigureAwait(false);
    }
}
