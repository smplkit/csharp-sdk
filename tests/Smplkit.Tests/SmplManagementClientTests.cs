using Smplkit;
using Smplkit.Errors;
using Xunit;

namespace Smplkit.Tests;

/// <summary>
/// Tests that <see cref="SmplManagementClient"/> has zero side effects on
/// construction (PR #127 rule 1) and exposes the eight flat namespaces
/// (rule 2).
/// </summary>
public class SmplManagementClientTests
{
    [Fact]
    public void Construction_RequiresApiKey()
    {
        // Without an API key (env var or otherwise), construction should throw
        // with a clear message — and crucially, not start any network activity.
        var savedKey = Environment.GetEnvironmentVariable("SMPLKIT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
            Environment.SetEnvironmentVariable("HOME", "/tmp/smplkit-test-no-config");
            Assert.Throws<SmplkitException>(() => new SmplManagementClient(new SmplClientOptions()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", savedKey);
        }
    }

    [Fact]
    public void Construction_DoesNotRequireEnvironmentOrService()
    {
        // Management plane has no notion of environment/service.
        using var mgmt = new SmplManagementClient(new SmplClientOptions
        {
            ApiKey = "sk_test_dummy",
        });

        Assert.NotNull(mgmt);
    }

    [Fact]
    public void NineFlatNamespaces_AreExposed()
    {
        using var mgmt = new SmplManagementClient(new SmplClientOptions
        {
            ApiKey = "sk_test_dummy",
        });

        Assert.NotNull(mgmt.Contexts);
        Assert.NotNull(mgmt.ContextTypes);
        Assert.NotNull(mgmt.Environments);
        Assert.NotNull(mgmt.AccountSettings);
        Assert.NotNull(mgmt.Config);
        Assert.NotNull(mgmt.Flags);
        Assert.NotNull(mgmt.Loggers);
        Assert.NotNull(mgmt.LogGroups);
        Assert.NotNull(mgmt.Audit);
        Assert.NotNull(mgmt.Audit.Forwarders);
    }

    [Fact]
    public void Construction_MakesZeroOutboundHttpCalls()
    {
        // Rule 1: SmplManagementClient construction has zero side effects on
        // the network. Verify by injecting an HTTP handler that throws on every
        // request — construction must not trigger one.
        var handler = new Smplkit.Tests.Helpers.MockHttpMessageHandler(req =>
            throw new InvalidOperationException(
                $"Construction must not make HTTP calls (got {req.Method} {req.RequestUri})"));
        using var http = new HttpClient(handler);
        using var mgmt = new SmplManagementClient(
            new SmplClientOptions { ApiKey = "sk_test" }, http);

        // No requests recorded — neither during construction nor immediately
        // after (no service registration, no metrics, no WebSocket).
        Assert.Empty(handler.Requests);
        Assert.Equal(0, mgmt.Contexts.PendingCount);
    }
}
