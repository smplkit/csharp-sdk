// Setup / cleanup helpers for LoggingManagementShowcase.

using Smplkit;
using Smplkit.Errors;

namespace Smplkit.Examples.Setup;

public static class LoggingManagementSetup
{
    private static readonly string[] DemoLoggerIds = { "showcase", "showcase.db", "showcase.payments" };

    public static async Task SetupManagementShowcaseAsync(SmplClient client)
    {
        await CleanupManagementShowcaseAsync(client);
    }

    public static async Task CleanupManagementShowcaseAsync(SmplClient client)
    {
        foreach (var loggerId in DemoLoggerIds)
        {
            try { await client.Logging.Loggers.DeleteAsync(loggerId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
