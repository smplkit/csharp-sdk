// Setup / cleanup helpers for LoggingManagementShowcase.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class LoggingManagementSetup
{
    private static readonly string[] DemoLoggerIds = { "showcase", "showcase.db", "showcase.payments" };

    public static async Task SetupManagementShowcaseAsync(SmplManagementClient mgmt)
    {
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
