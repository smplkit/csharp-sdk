// Setup / cleanup helpers for LoggingManagementShowcase.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class LoggingManagementSetup
{
    private static readonly string[] DemoEnvironments = { "staging", "production" };
    private static readonly string[] DemoLoggerIds = { "showcase", "showcase.db", "showcase.payments" };

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
        foreach (var loggerId in DemoLoggerIds)
        {
            try { await mgmt.Loggers.DeleteAsync(loggerId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
