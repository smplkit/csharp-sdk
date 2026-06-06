using Smplkit.Internal;
using Smplkit.Jobs;
using GenJobs = Smplkit.Internal.Generated.Jobs;
using HttpMethod = Smplkit.Jobs.HttpMethod;

namespace Smplkit.Management;

/// <summary>
/// Run history and run actions — accessed via <see cref="JobsManagementClient.Runs"/>.
///
/// <para>Read-only views plus the <c>cancel</c> / <c>rerun</c> run actions. Runs are
/// created and mutated by the jobs service, not by clients.</para>
/// </summary>
public sealed class JobsRunsClient
{
    private readonly GenJobs.JobsClient _gen;

    internal JobsRunsClient(GenJobs.JobsClient gen) => _gen = gen;

    /// <summary>List runs for the authenticated account, newest first (cursor paginated).</summary>
    /// <param name="job">Filter to a single job's run history, by job id.</param>
    /// <param name="pageSize">Items per page (cursor pagination).</param>
    /// <param name="after">Opaque cursor token from a prior page's <c>next</c> link.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public async Task<IReadOnlyList<Run>> ListAsync(
        string? job = null, int? pageSize = null, string? after = null, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_runsAsync(job, pageSize, after, ct)).ConfigureAwait(false);
        return (resp.Data ?? new List<GenJobs.RunResource>())
            .Select(JobsManagementClient.RunFromResource).ToList();
    }

    /// <summary>Fetch a single run by id.</summary>
    public async Task<Run> GetAsync(string runId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_runAsync(Guid.Parse(runId), ct)).ConfigureAwait(false);
        return JobsManagementClient.RunFromResource(resp.Data);
    }

    /// <summary>Cancel a pending run.</summary>
    public async Task<Run> CancelAsync(string runId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Cancel_runAsync(Guid.Parse(runId), ct)).ConfigureAwait(false);
        return JobsManagementClient.RunFromResource(resp.Data);
    }

    /// <summary>Re-run a prior run, spawning a new <c>RERUN</c> run.</summary>
    public async Task<Run> RerunAsync(string runId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Rerun_runAsync(Guid.Parse(runId), ct)).ConfigureAwait(false);
        return JobsManagementClient.RunFromResource(resp.Data);
    }
}

/// <summary>
/// Smpl Jobs management surface — accessed via <c>SmplManagementClient.Jobs</c>.
///
/// <para>Unlike Config/Flags/Logging, Jobs has no live "phone-home" agent — no
/// environment registration, no WebSocket — so its entire surface lives on the
/// management client. Defining a job, triggering a run, and reading run history are
/// all plain request/response calls here. A <see cref="Job"/> is an active record:
/// build it with <see cref="New"/>, set fields, and call <see cref="Job.SaveAsync"/>
/// (create when new, full-replace update when it already exists) or
/// <see cref="Job.DeleteAsync"/>. Runs are read-only views; run actions live on
/// <see cref="Runs"/>.</para>
/// </summary>
public sealed class JobsManagementClient
{
    private readonly GenJobs.JobsClient _gen;

    /// <summary>Run history and run actions.</summary>
    public JobsRunsClient Runs { get; }

    internal JobsManagementClient(GenJobs.JobsClient gen)
    {
        _gen = gen;
        Runs = new JobsRunsClient(gen);
    }

    /// <summary>
    /// Returns an unsaved <see cref="Job"/> bound to this client. Call
    /// <see cref="Job.SaveAsync"/> to create it.
    /// </summary>
    /// <param name="id">Caller-supplied unique identifier for the job. Unique within
    /// the account and immutable; the service returns 409 if another live job already
    /// uses this id.</param>
    /// <param name="name">Human-readable name for the job.</param>
    /// <param name="schedule">An ISO-8601 datetime, a 5-field UTC cron expression, or
    /// the literal <c>"now"</c>.</param>
    /// <param name="configuration">The HTTP request the job performs.</param>
    /// <param name="description">Optional free-text description.</param>
    /// <param name="enabled">Whether the job schedules runs. Defaults to <c>true</c>.</param>
    /// <param name="concurrencyPolicy">How overlapping runs are handled. Defaults to <c>"ALLOW"</c>.</param>
    public Job New(
        string id,
        string name,
        string schedule,
        HttpConfig configuration,
        string? description = null,
        bool enabled = true,
        string concurrencyPolicy = "ALLOW")
    {
        return new Job(
            this,
            id: id,
            name: name,
            schedule: schedule,
            configuration: configuration,
            description: description,
            enabled: enabled,
            concurrencyPolicy: concurrencyPolicy);
    }

    /// <summary>List jobs for the authenticated account, newest first.</summary>
    /// <param name="enabled">Filter to jobs matching this enabled state.</param>
    /// <param name="pageNumber">1-based page number to return.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public async Task<IReadOnlyList<Job>> ListAsync(
        bool? enabled = null, int? pageNumber = null, int? pageSize = null, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_jobsAsync(enabled, pageNumber, pageSize, null, ct)).ConfigureAwait(false);
        return (resp.Data ?? new List<GenJobs.JobResource>())
            .Select(FromResource).ToList();
    }

    /// <summary>Retrieve a single job by id. The returned instance is bound to this
    /// client so <see cref="Job.SaveAsync"/> / <see cref="Job.DeleteAsync"/> work.</summary>
    public async Task<Job> GetAsync(string jobId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_jobAsync(jobId, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Soft-delete a job by id. Prefer <see cref="Job.DeleteAsync"/> when you
    /// already have a <see cref="Job"/> instance.</summary>
    public Task DeleteAsync(string jobId, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _gen.Delete_jobAsync(jobId, ct));

    /// <summary>Trigger one immediate <c>MANUAL</c> run of the job.</summary>
    public async Task<Run> RunAsync(string jobId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Run_job_nowAsync(jobId, ct)).ConfigureAwait(false);
        return RunFromResource(resp.Data);
    }

    /// <summary>Current-period usage counters for the account.</summary>
    public async Task<Usage> UsageAsync(CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_usageAsync(null, ct)).ConfigureAwait(false);
        var a = resp.Data.Attributes;
        return new Usage(a.Period, a.Runs_used, a.Runs_included, a.Active_jobs, a.Active_jobs_limit);
    }

    // ------------------------------------------------------------------
    // Internal: drive Job.SaveAsync
    // ------------------------------------------------------------------

    /// <summary>POST a new job. Called by <see cref="Job.SaveAsync"/>; not for direct use.</summary>
    internal async Task<Job> SaveCreateAsync(Job job, CancellationToken ct)
    {
        var body = new GenJobs.JobCreateRequest
        {
            Data = new GenJobs.JobCreateResource
            {
                Id = job.Id,
                Type = "job",
                Attributes = BuildJobAttributes(job),
            },
        };
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Create_jobAsync(body, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Full-replace PUT. Called by <see cref="Job.SaveAsync"/>; not for direct use.</summary>
    internal async Task<Job> SaveUpdateAsync(Job job, CancellationToken ct)
    {
        var body = new GenJobs.JobRequest
        {
            Data = new GenJobs.JobResource
            {
                Id = job.Id,
                Type = "job",
                Attributes = BuildJobAttributes(job),
            },
        };
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Update_jobAsync(job.Id, body, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    // ------------------------------------------------------------------
    // Wire <-> wrapper conversions
    // ------------------------------------------------------------------

    private static GenJobs.Job BuildJobAttributes(Job src)
    {
        return new GenJobs.Job
        {
            Name = src.Name,
            Description = src.Description,
            Enabled = src.Enabled,
            Type = src.Type,
            Schedule = src.Schedule,
            Configuration = ToGenConfiguration(src.Configuration),
            Concurrency_policy = src.ConcurrencyPolicy,
        };
    }

    private static GenJobs.JobHttpConfiguration ToGenConfiguration(HttpConfig src)
    {
        var headers = new List<GenJobs.HttpHeader>(src.Headers.Count);
        foreach (var h in src.Headers)
            headers.Add(new GenJobs.HttpHeader { Name = h.Name, Value = h.Value });

        return new GenJobs.JobHttpConfiguration
        {
            Method = ToGenHttpMethod(src.Method),
            Url = src.Url,
            Headers = headers,
            Body = src.Body,
            Success_status = src.SuccessStatus,
            Timeout = src.Timeout,
            Tls_verify = src.TlsVerify,
            Ca_cert = src.CaCert,
        };
    }

    private static GenJobs.JobHttpConfigurationMethod ToGenHttpMethod(HttpMethod method) =>
        method switch
        {
            HttpMethod.Delete => GenJobs.JobHttpConfigurationMethod.DELETE,
            HttpMethod.Get => GenJobs.JobHttpConfigurationMethod.GET,
            HttpMethod.Patch => GenJobs.JobHttpConfigurationMethod.PATCH,
            HttpMethod.Post => GenJobs.JobHttpConfigurationMethod.POST,
            HttpMethod.Put => GenJobs.JobHttpConfigurationMethod.PUT,
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

    private Job FromResource(GenJobs.JobResource r)
    {
        var a = r.Attributes;
        return new Job(
            this,
            id: r.Id ?? string.Empty,
            name: a.Name ?? string.Empty,
            schedule: a.Schedule ?? string.Empty,
            configuration: ConfigFromGen(a.Configuration),
            description: a.Description,
            enabled: a.Enabled,
            type: a.Type ?? "http",
            concurrencyPolicy: a.Concurrency_policy ?? "ALLOW",
            nextRunAt: a.Next_run_at,
            createdAt: a.Created_at,
            updatedAt: a.Updated_at,
            deletedAt: a.Deleted_at,
            version: a.Version);
    }

    private static HttpConfig ConfigFromGen(GenJobs.JobHttpConfiguration? src)
    {
        if (src == null) return new HttpConfig { Url = string.Empty };
        var cfg = new HttpConfig
        {
            Method = HttpMethodExtensions.FromWireValue(src.Method.ToString()),
            Url = src.Url ?? string.Empty,
            Body = src.Body,
            SuccessStatus = src.Success_status ?? "2xx",
            Timeout = src.Timeout,
            TlsVerify = src.Tls_verify,
            CaCert = src.Ca_cert,
        };
        if (src.Headers != null)
        {
            cfg.Headers = src.Headers
                .Select(h => new HttpHeader(h.Name ?? string.Empty, h.Value ?? string.Empty))
                .ToList();
        }
        return cfg;
    }

    internal static Run RunFromResource(GenJobs.RunResource r)
    {
        var a = r.Attributes;
        return new Run(
            id: r.Id ?? string.Empty,
            job: a.Job ?? string.Empty,
            trigger: a.Trigger.ToString(),
            status: a.Status.ToString(),
            jobVersion: a.Job_version,
            rerunOf: a.Rerun_of?.ToString(),
            scheduledFor: a.Scheduled_for,
            startedAt: a.Started_at,
            finishedAt: a.Finished_at,
            pendingDurationMs: a.Pending_duration_ms,
            runDurationMs: a.Run_duration_ms,
            totalDurationMs: a.Total_duration_ms,
            failureReason: a.Failure_reason?.ToString(),
            error: a.Error,
            request: a.Request,
            result: a.Result,
            createdAt: a.Created_at);
    }
}
