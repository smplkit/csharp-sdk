// Setup / cleanup helpers for FlagsManagementShowcase.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class FlagsManagementSetup
{
    private static readonly string[] DemoEnvironments = { "staging", "production" };
    private static readonly string[] DemoFlagIds = { "checkout-v2", "banner-color", "max-retries", "ui-theme" };

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
        foreach (var flagId in DemoFlagIds)
        {
            try { await mgmt.Flags.DeleteAsync(flagId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
