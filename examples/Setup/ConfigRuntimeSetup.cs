// Setup and simulation helpers for ConfigRuntimeShowcase.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class ConfigRuntimeSetup
{
    private static readonly string[] DemoConfigIds = new[]
    {
        "showcase-billing",
        "showcase-common",
        "showcase-database",
    };

    public static async Task SimulateAdminOverrideAsync(SmplClient client)
    {
        // Real customers never read back through the management API
        // immediately after binding via the runtime client — this is a
        // simulation-only step. Push pending runtime-side registrations
        // through so the management-API lookup below can find the
        // freshly-declared config.
        await client.Config.FlushAsync();
        var billing = await client.Config.GetAsync("showcase-billing");
        billing.SetNumber("plan.max_seats", 25, environment: "production");
        await billing.SaveAsync();
    }

    public static async Task CleanupRuntimeShowcaseAsync(SmplClient client)
    {
        foreach (var configId in DemoConfigIds)
        {
            try { await client.Config.DeleteAsync(configId); }
            catch (NotFoundException) { }
        }
    }
}
