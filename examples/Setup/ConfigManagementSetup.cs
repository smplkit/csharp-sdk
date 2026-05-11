// Setup / cleanup helpers for ConfigManagementShowcase.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class ConfigManagementSetup
{
    private static readonly string[] DemoEnvironments = { "staging", "production" };
    private static readonly string[] DemoConfigIds = { "showcase-user-service", "showcase-common" };

    public static async Task SetupManagementShowcaseAsync(SmplManagementClient mgmt)
    {
        var existing = (await mgmt.Environments.ListAsync()).Select(e => e.Id).ToHashSet();
        foreach (var envId in DemoEnvironments)
        {
            if (!existing.Contains(envId))
                await mgmt.Environments.New(envId, char.ToUpper(envId[0]) + envId[1..]).SaveAsync();
        }
        await CleanupManagementShowcaseAsync(mgmt);
    }

    public static async Task CleanupManagementShowcaseAsync(SmplManagementClient mgmt)
    {
        // Delete any configs using showcase-common as parent before deleting
        // it — previous runs (including the runtime showcase) may have left
        // extra children that would block the parent's deletion.
        var allConfigs = await mgmt.Config.ListAsync();
        foreach (var cfg in allConfigs.Where(c => c.Parent == "showcase-common" && c.Id != null))
        {
            try { await mgmt.Config.DeleteAsync(cfg.Id!); }
            catch (NotFoundException) { }
        }
        foreach (var configId in DemoConfigIds)
        {
            try { await mgmt.Config.DeleteAsync(configId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
