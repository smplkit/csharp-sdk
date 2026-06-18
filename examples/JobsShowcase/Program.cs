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
const string OneoffJobId = "showcase-oneoff";

// standalone jobs client
using var jobs = new JobsClient();
await JobsSetup.SetupShowcaseAsync(jobs);
try
{
    // create a recurring job, enabled in production with a development override
    var job = jobs.New(
        RecurringJobId,
        name: "Nightly cache warm",
        description: "Warms the product cache every night at 02:00 UTC.",
        schedule: "0 2 * * *",
        configuration: new HttpConfig
        {
            Method = HttpMethod.Post,
            Url = "https://httpbin.org/post",
            Headers = new List<HttpHeader> { new("Authorization", "Bearer s3cr3t") },
            Body = "{\"scope\": \"all\"}",
            Timeout = 30,
        });
    job.SetConfiguration(
        new HttpConfig
        {
            Method = HttpMethod.Post,
            Url = "https://development.example.com/cache/warm",
            Headers = new List<HttpHeader> { new("Authorization", "Bearer development-s3cr3t") },
            Body = "{\"scope\": \"all\"}",
        },
        environment: "development");
    job.SetEnabled(false, environment: "development");
    job.SetEnabled(true, environment: "production");
    await job.SaveAsync();
    Debug.Assert(job.Version == 1);
    Debug.Assert(job.IsEnabled(environment: "development") == false);
    Debug.Assert(job.IsEnabled(environment: "production") == true);
    Console.WriteLine($"Created recurring job {job.Id} (v{job.Version})");

    // get a job
    var fetched = await jobs.GetAsync(RecurringJobId);
    Debug.Assert(fetched.IsEnabled(environment: "development") == false);
    Debug.Assert(fetched.IsEnabled(environment: "production") == true);
    Debug.Assert(fetched.GetConfiguration(environment: "development").Url == "https://development.example.com/cache/warm");
    Console.WriteLine($"Fetched job {RecurringJobId}");

    // list jobs
    var listing = await jobs.ListAsync();
    Debug.Assert(listing.Any(j => j.Id == RecurringJobId));
    Console.WriteLine($"Found job {RecurringJobId} in the listing");

    // update a job (the schedule is environment-agnostic)
    job.Name = "Nightly cache warm (v2)";
    job.SetSchedule("30 2 * * *");
    job.SetEnabled(true, environment: "development");
    await job.SaveAsync();
    Debug.Assert(job.Version == 2 && job.IsEnabled(environment: "development") == true);
    Console.WriteLine($"Updated job to v{job.Version}: now enabled in production and development");

    // trigger an immediate run
    var run = await job.TriggerAsync(environment: "production");
    Debug.Assert(run.Trigger == "MANUAL" && run.Environment == "production");
    Console.WriteLine($"Triggered run {run.Id} (trigger={run.Trigger}, env={run.Environment})");

    // get this job's runs
    var runs = await job.ListRunsAsync(environment: "production");
    Debug.Assert(runs.Any(r => r.Id == run.Id));
    Console.WriteLine($"Listed {runs.Count} production run(s)");

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

    // create a one-off job, born in a single environment
    var oneoff = jobs.New(
        OneoffJobId,
        name: "One-shot reindex",
        schedule: "now",
        configuration: new HttpConfig { Method = HttpMethod.Post, Url = "https://httpbin.org/post" },
        environment: "development");
    await oneoff.SaveAsync();
    Debug.Assert(oneoff.Version == 1 && oneoff.IsEnabled(environment: "development") == true);
    Console.WriteLine($"Created one-off job {oneoff.Id} born in development");

    // delete a job
    await job.DeleteAsync();
    Debug.Assert(!(await jobs.ListAsync()).Any(j => j.Id == RecurringJobId));
    Console.WriteLine($"Deleted job {RecurringJobId} — jobs showcase complete.");
}
finally
{
    await JobsSetup.CleanupShowcaseAsync(jobs);
}
