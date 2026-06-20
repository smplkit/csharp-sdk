// Setup / cleanup helpers for the Jobs showcase.

using Smplkit.Errors;
using Smplkit.Jobs;

namespace Smplkit.Examples.Setup;

public static class JobsSetup
{
    // Every job and retry policy the jobs showcase creates. Start-of-run cleanup
    // removes residue from a prior run; the matching finally cleanup tears them down
    // even when it fails mid-way, so a failed run never leaves orphans behind.
    private static readonly string[] DemoJobIds = { "showcase-recurring", "showcase-manual", "showcase-oneoff" };
    private static readonly string[] DemoRetryPolicyIds = { "showcase-retry" };

    public static async Task SetupShowcaseAsync(JobsClient jobs)
    {
        await CleanupShowcaseAsync(jobs);
    }

    public static async Task CleanupShowcaseAsync(JobsClient jobs)
    {
        // Jobs first, then the policies they reference.
        foreach (var jobId in DemoJobIds)
        {
            try { await jobs.DeleteAsync(jobId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
        foreach (var policyId in DemoRetryPolicyIds)
        {
            try { await jobs.RetryPolicies.DeleteAsync(policyId); }
            catch (NotFoundException) { /* not present — that's fine */ }
        }
    }
}
