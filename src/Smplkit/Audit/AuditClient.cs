using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Audit-product entry point — accessed via <c>SmplClient.Audit</c>.
///
/// <para>Today the audit namespace is exclusively <see cref="Events"/>;
/// future iterations may add SIEM exports as additional sub-clients
/// (ADR-047 §2.7 lists SIEM streaming as a Pro-tier capability).</para>
/// </summary>
public sealed class AuditClient : IAsyncDisposable
{
    /// <summary>Events sub-client.</summary>
    public AuditEvents Events { get; }

    internal AuditClient(GenAudit.AuditClient generated)
    {
        Events = new AuditEvents(generated);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Events.DisposeAsync().ConfigureAwait(false);
    }
}
