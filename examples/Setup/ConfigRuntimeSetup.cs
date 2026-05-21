// Setup, simulation, and cleanup helpers for ConfigRuntimeShowcase.
//
// The runtime showcase is intentionally runtime-only — declarations,
// typed getters, change listeners. In a real deployment the configs
// would either already exist (admin-curated) or be created by the
// SDK's discovery on first run. Here we pre-create them through the
// management API so the showcase can also demonstrate a live admin
// override end-to-end in a single process.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class ConfigRuntimeSetup
{
    private static readonly string[] DemoConfigIds = { "showcase-billing", "showcase-common" };

    public static async Task SetupRuntimeShowcaseAsync(SmplManagementClient mgmt)
    {
        await CleanupRuntimeShowcaseAsync(mgmt);

        var common = mgmt.Config.New(
            "showcase-common",
            description: "Shared defaults for showcase services.");
        common.SetString("app.name", "Acme SaaS");
        common.SetString("support.email", "support@acme.dev");
        await common.SaveAsync();

        var billing = mgmt.Config.New(
            "showcase-billing",
            description: "Plan-limit configuration for billing.",
            parent: "showcase-common");
        billing.SetNumber("plan.max_seats", 5, description: "Maximum seats per organization.");
        billing.SetNumber("plan.trial_days", 14);
        billing.SetString("plan.tier", "free");
        await billing.SaveAsync();
    }

    public static async Task SimulateAdminOverrideAsync(SmplManagementClient mgmt)
    {
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
