// Setup / cleanup helpers for ConfigManagementShowcase.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class ConfigManagementSetup
{
    private static readonly string[] DemoConfigIds = { "showcase-user-service", "showcase-common" };

    public static async Task SetupManagementShowcaseAsync(SmplClient client)
    {
        await CleanupManagementShowcaseAsync(client);
    }

    public static async Task CleanupManagementShowcaseAsync(SmplClient client)
    {
        // Delete any configs using showcase-common as parent before deleting
        // it — previous runs (including the runtime showcase) may have left
        // extra children that would block the parent's deletion.
        var allConfigs = await client.Config.ListAsync();
        foreach (var cfg in allConfigs.Where(c => c.Parent == "showcase-common" && c.Id != null))
        {
            try { await client.Config.DeleteAsync(cfg.Id!); }
            catch (NotFoundException) { }
        }
        foreach (var configId in DemoConfigIds)
        {
            try { await client.Config.DeleteAsync(configId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
