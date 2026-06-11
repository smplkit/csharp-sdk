// Setup / cleanup helpers for FlagsManagementShowcase.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class FlagsManagementSetup
{
    private static readonly string[] DemoFlagIds = { "checkout-v2", "banner-color", "max-retries", "ui-theme" };

    public static async Task SetupManagementShowcaseAsync(SmplClient client)
    {
        await CleanupManagementShowcaseAsync(client);
    }

    public static async Task CleanupManagementShowcaseAsync(SmplClient client)
    {
        foreach (var flagId in DemoFlagIds)
        {
            try { await client.Flags.DeleteAsync(flagId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
