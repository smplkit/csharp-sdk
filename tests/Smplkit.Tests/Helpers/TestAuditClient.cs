using Smplkit.Audit;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Tests.Helpers;

/// <summary>
/// Test-only stand-in for <see cref="Smplkit.Audit.AuditClient"/> wired directly
/// over a mock-backed generated audit client.
/// </summary>
/// <remarks>
/// The production <see cref="Smplkit.Audit.AuditClient"/> resolves config and
/// owns its own <see cref="HttpClient"/>, so it offers no seam to inject a
/// <see cref="GenAudit.AuditClient"/> wired to a <see cref="MockHttpMessageHandler"/>.
/// Each audit sub-client (<see cref="AuditEvents"/>, <see cref="AuditResourceTypes"/>,
/// …) does expose an <c>internal</c> ctor taking the generated client, so this
/// helper reproduces the production wiring (all sub-clients share one generated
/// client) without the network/credential resolution the standalone ctor performs.
/// </remarks>
internal sealed class TestAuditClient : IAsyncDisposable
{
    /// <summary>The <c>events</c> sub-client.</summary>
    public AuditEvents Events { get; }

    /// <summary>The <c>resource_types</c> discovery sub-client.</summary>
    public AuditResourceTypes ResourceTypes { get; }

    /// <summary>The <c>event_types</c> discovery sub-client.</summary>
    public AuditEventTypes EventTypes { get; }

    /// <summary>The <c>categories</c> discovery sub-client.</summary>
    public AuditCategories Categories { get; }

    /// <summary>The <c>forwarders</c> CRUD sub-client.</summary>
    public ForwardersClient Forwarders { get; }

    public TestAuditClient(GenAudit.AuditClient gen)
    {
        Events = new AuditEvents(gen);
        ResourceTypes = new AuditResourceTypes(gen);
        EventTypes = new AuditEventTypes(gen);
        Categories = new AuditCategories(gen);
        Forwarders = new ForwardersClient(gen);
    }

    public async ValueTask DisposeAsync()
    {
        await Events.DisposeAsync().ConfigureAwait(false);
    }
}
