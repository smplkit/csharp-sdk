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
    public void EightFlatNamespaces_AreExposed()
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
    }

    [Fact]
    public void Construction_ZeroSideEffects()
    {
        // Constructing should not register the service, start metrics, or
        // open a WebSocket — verified by completing quickly with no network.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var mgmt = new SmplManagementClient(new SmplClientOptions
        {
            ApiKey = "sk_test_dummy",
        });
        sw.Stop();

        // Construction should be fast (< 1s); a network call would be slower.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"SmplManagementClient construction took {sw.ElapsedMilliseconds}ms — likely has unwanted side effects.");
    }
}
