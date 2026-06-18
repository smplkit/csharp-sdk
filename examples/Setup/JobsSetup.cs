// Setup / cleanup helpers for the Jobs showcase.

using Smplkit.Errors;
using Smplkit.Jobs;

namespace Smplkit.Examples.Setup;

public static class JobsSetup
{
    private static readonly string[] DemoJobIds = { "showcase-recurring", "showcase-oneoff" };

    public static async Task SetupShowcaseAsync(JobsClient jobs)
    {
        await CleanupShowcaseAsync(jobs);
    }

    public static async Task CleanupShowcaseAsync(JobsClient jobs)
    {
        foreach (var jobId in DemoJobIds)
        {
            try { await jobs.DeleteAsync(jobId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
