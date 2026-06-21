// Demonstrates the smplkit management SDK for Smpl Jobs.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key, provided via one of:
//         - SMPLKIT_API_KEY environment variable
//         - ~/.smplkit configuration file (see SDK docs)
//
// Usage:
//     dotnet run --project examples/JobsShowcase

using System.Diagnostics;
using Smplkit.Errors;
using Smplkit.Examples.Setup;
using Smplkit.Jobs;
using HttpMethod = Smplkit.Jobs.HttpMethod;

const string RecurringJobId = "showcase-recurring";
const string ManualJobId = "showcase-manual";
const string OneoffJobId = "showcase-oneoff";
const string RetryPolicyId = "showcase-retry";

// standalone jobs client
using var jobs = new JobsClient();
await JobsSetup.SetupShowcaseAsync(jobs);
try
{
    // create a retry policy
    var retryPolicy = jobs.RetryPolicies.New(
        RetryPolicyId,
        name: "Retry on server errors",
        maxRetries: 5,
        backoff: Backoff.Exponential,
        delaySeconds: 2,
        maxDelaySeconds: 60,
        retryOnTimeout: true,
        retryOnConnectionError: true,
        retryStatuses: new List<string> { "429", "5xx" },
        retryStatusesExcept: new List<string> { "501" });
    await retryPolicy.SaveAsync();
    Debug.Assert((await jobs.RetryPolicies.ListAsync()).Any(p => p.Id == RetryPolicyId));
    Console.WriteLine($"Created retry policy '{retryPolicy.Id}'");

    // create a recurring job
    var job = jobs.NewRecurringJob(
        RecurringJobId,
        name: "Nightly cache warm",
        description: "Warms the product cache nightly.",
        schedule: "0 2 * * *",
        configuration: new HttpConfig
        {
            Method = HttpMethod.Post,
            Url = "https://httpbin.org/post",
            Headers = new List<HttpHeader> { new("Authorization", "Bearer s3cr3t") },
            Body = "{\"scope\": \"all\"}",
            Timeout = 30,
        });
    job.SetEnabled(true, environment: "development");
    job.SetEnabled(true, environment: "production");
    job.SetSchedule("0 */6 * * *", timezone: "America/New_York", environment: "development");
    job.SetConfiguration(
        new HttpConfig
        {
            Method = HttpMethod.Post,
            Url = "https://development.example.com/cache/warm",
            Headers = new List<HttpHeader> { new("Authorization", "Bearer development-s3cr3t") },
            Body = "{\"scope\": \"all\"}",
        },
        environment: "development");
    await job.SaveAsync();
    Debug.Assert(job.IsRecurring());
    Debug.Assert(job.IsEnabled(environment: "development"));
    Debug.Assert(job.IsEnabled(environment: "production"));
    Debug.Assert(job.Environments["development"].Timezone == "America/New_York");
    Debug.Assert(job.GetConfiguration(environment: "development").Url == "https://development.example.com/cache/warm");
    Console.WriteLine($"Created recurring job '{job.Id}' (v{job.Version})");

    // get a job
    var fetched = await jobs.GetAsync(RecurringJobId);
    Debug.Assert(fetched.Environments["development"].Schedule == "0 */6 * * *");
    Console.WriteLine($"Fetched job '{RecurringJobId}'");

    // list jobs, filtered to recurring jobs
    var listing = await jobs.ListAsync(kind: JobKind.Recurring);
    Debug.Assert(listing.Any(j => j.Id == RecurringJobId));
    Console.WriteLine($"Found job '{RecurringJobId}' in the listing");

    // update a job
    job.Name = "Nightly cache warm (v2)";
    job.SetRetryPolicy(retryPolicy, environment: "production");
    job.SetSchedule("30 2 * * *", timezone: "America/Los_Angeles", environment: "production");
    await job.SaveAsync();
    Debug.Assert(job.Version == 2);
    Console.WriteLine($"Updated job to v{job.Version}");

    // trigger an immediate run
    var run = await job.TriggerAsync(environment: "production");
    Debug.Assert(run.Trigger == "MANUAL" && run.Environment == "production");
    Console.WriteLine($"Triggered run {run.Id} (trigger={run.Trigger}, env={run.Environment})");

    // get this job's runs
    var runs = await job.ListRunsAsync(environment: "production");
    Debug.Assert(runs.Any(r => r.Id == run.Id));
    Console.WriteLine($"Listed {runs.Count} production run(s)");

    // get the last completed run in production
    var recent = await job.ListRunsAsync(environment: "production", lastRunOnly: true);
    Console.WriteLine($"Last completed production run(s): {recent.Count}");

    // get a run
    run = await jobs.Runs.GetAsync(run.Id);
    Debug.Assert(run.Environment == "production");
    Console.WriteLine($"Fetched run {run.Id} (env={run.Environment})");

    // re-run a prior run (inherits its environment)
    var rerun = await run.RerunAsync();
    Debug.Assert(rerun.Trigger == "RERUN" && rerun.Environment == run.Environment);
    Console.WriteLine($"Re-ran {run.Id} -> {rerun.Id} (env={rerun.Environment})");

    // cancel a run (best-effort: a finished run can no longer be canceled)
    try
    {
        var canceled = await rerun.CancelAsync();
        Console.WriteLine($"Canceled run {canceled.Id} -> {canceled.Status}");
    }
    catch (ConflictException)
    {
        Console.WriteLine($"Run {rerun.Id} already finished before it could be canceled");
    }

    // create a manual job (no schedule, runs only when triggered)
    var manual = jobs.NewManualJob(
        ManualJobId,
        name: "On-demand reindex",
        configuration: new HttpConfig { Method = HttpMethod.Post, Url = "https://httpbin.org/post" });
    manual.SetEnabled(true, environment: "production");
    await manual.SaveAsync();
    Debug.Assert(manual.IsManual());
    var manualRun = await manual.TriggerAsync(environment: "production");
    Debug.Assert(manualRun.Trigger == RunTrigger.Manual);
    Console.WriteLine($"Created manual job '{manual.Id}' and triggered it on demand");

    // schedule a one-off job to run tomorrow
    var tomorrow = DateTimeOffset.Now.AddDays(1);
    var oneoff = jobs.Schedule(
        OneoffJobId,
        name: "One-shot reindex",
        schedule: tomorrow,
        configuration: new HttpConfig { Method = HttpMethod.Post, Url = "https://httpbin.org/post" },
        environment: "development");
    await oneoff.SaveAsync();
    Debug.Assert(oneoff.IsOneOff());
    Debug.Assert(oneoff.IsEnabled(environment: "development"));
    Debug.Assert(oneoff.Environments["development"].NextRunAt != null);
    Console.WriteLine($"Created one-off job '{oneoff.Id}' to run in development");

    // delete a job
    await job.DeleteAsync();
    Debug.Assert(!(await jobs.ListAsync()).Any(j => j.Id == RecurringJobId));

    // delete the retry policy
    await retryPolicy.DeleteAsync();
    Console.WriteLine($"Deleted job '{RecurringJobId}' and retry policy — jobs showcase complete.");
}
finally
{
    await JobsSetup.CleanupShowcaseAsync(jobs);
}
