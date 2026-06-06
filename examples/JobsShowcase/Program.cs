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
using Smplkit;
using Smplkit.Errors;
using Smplkit.Jobs;
using HttpMethod = Smplkit.Jobs.HttpMethod;

// create the client
using var manage = new SmplManagementClient();
var jobId = $"showcase-mgmt-{Guid.NewGuid().ToString("N")[..8]}";

try
{
    // create a job
    var job = manage.Jobs.New(
        jobId,
        name: "Nightly cache warm",
        description: "Warms the product cache every night at 02:00 UTC.",
        schedule: "0 2 * * *", // 5-field cron, UTC
        enabled: false,
        configuration: new HttpConfig
        {
            Method = HttpMethod.Post,
            Url = "https://api.example.com/cache/warm",
            Headers = new List<HttpHeader> { new("Authorization", "Bearer s3cr3t") },
            Body = "{\"scope\": \"all\"}",
            Timeout = 30,
        });
    await job.SaveAsync();
    Debug.Assert(job.Version == 1);
    Console.WriteLine($"Created job {job.Id} (v{job.Version})");

    // get a job
    var fetched = await manage.Jobs.GetAsync(jobId);
    Debug.Assert(fetched.Configuration.Url == "https://api.example.com/cache/warm");
    Console.WriteLine($"Fetched job {jobId}");

    // list jobs
    var jobs = await manage.Jobs.ListAsync(enabled: false);
    Debug.Assert(jobs.Any(j => j.Id == jobId));
    Console.WriteLine($"Found job {jobId} and in the listing");

    // update a job
    job.Name = "Nightly cache warm (v2)";
    job.Schedule = "30 2 * * *";
    job.Enabled = true;
    await job.SaveAsync();
    Debug.Assert(job.Version == 2 && job.Enabled == true);
    Console.WriteLine($"Updated job to v{job.Version}: schedule={job.Schedule}");

    // trigger an immediate run (a MANUAL run)
    var run = await manage.Jobs.RunAsync(jobId);
    Debug.Assert(run.Trigger == "MANUAL" && run.Job == jobId);
    Console.WriteLine($"Triggered run {run.Id} (trigger={run.Trigger}, status={run.Status})");

    // read run history for this job, and fetch a single run
    var runs = await manage.Jobs.Runs.ListAsync(job: jobId);
    Debug.Assert(runs.Any(r => r.Id == run.Id));
    var got = await manage.Jobs.Runs.GetAsync(run.Id);
    Debug.Assert(got.Id == run.Id);
    Console.WriteLine($"Listed {runs.Count} run(s); fetched run {got.Id} (status={got.Status})");

    // re-run from a prior run, then cancel it while it's still pending
    var rerun = await manage.Jobs.Runs.RerunAsync(run.Id);
    Debug.Assert(rerun.Trigger == "RERUN" && rerun.RerunOf == run.Id);
    var canceled = await manage.Jobs.Runs.CancelAsync(rerun.Id);
    Debug.Assert(canceled.Status == "CANCELED");
    Console.WriteLine($"Re-ran ({rerun.Id}) then canceled it -> {canceled.Status}");

    // delete a job
    await job.DeleteAsync();
    Debug.Assert(!(await manage.Jobs.ListAsync()).Any(j => j.Id == jobId));
    Console.WriteLine($"Deleted job {jobId} — management showcase complete.");
}
finally
{
    // tear-down: never leave the showcase job behind, even on failure
    try
    {
        await manage.Jobs.DeleteAsync(jobId);
    }
    catch (NotFoundException)
    {
        // already gone
    }
}
