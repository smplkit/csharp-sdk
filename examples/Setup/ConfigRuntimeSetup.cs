// Setup and simulation helpers for ConfigRuntimeShowcase.
//
// The runtime showcase declares its own configs via client.Config.Bind(),
// so this helper only handles cleanup and the live admin-override
// simulation that stands in for an operator editing values in the
// smplkit console.

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

    public static async Task SimulateAdminOverrideAsync(SmplManagementClient mgmt)
    {
        // Real customers never read back through the management API
        // immediately after binding via the runtime client — this is a
        // simulation-only step. Push pending runtime-side registrations
        // through so the management-API lookup below can find the
        // freshly-declared config.
        await mgmt.Config.FlushAsync();
        var billing = await mgmt.Config.GetAsync("showcase-billing");
        billing.SetNumber("plan.max_seats", 25, environment: "production");
        await billing.SaveAsync();
    }

    public static async Task CleanupRuntimeShowcaseAsync(SmplManagementClient mgmt)
    {
        foreach (var configId in DemoConfigIds)
        {
            try { await mgmt.Config.DeleteAsync(configId); }
            catch (NotFoundException) { }
        }
    }
}
