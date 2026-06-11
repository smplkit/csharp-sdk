// Smpl Jobs SDK client (client.Jobs on SmplClient, or standalone JobsClient).
//
// Unlike Config/Flags/Logging, Jobs installs no in-process machinery — no
// environment registration, no WebSocket, no logger monkey-patching. It is a
// product you *use*, not infrastructure you *install*, so it has no
// runtime/management split: a single JobsClient exposes the full surface,
// reachable two ways:
//
// * client.Jobs.* on SmplClient
// * directly — new JobsClient(apiKey: ...) — for callers that only need jobs.
//
// A Job is an active record: build it with JobsClient.New, set fields, and
// call SaveAsync() (create when new, full-replace update when it already
// exists) or DeleteAsync(). Runs are read-only views; run actions live on
// jobs.Runs.
//
// Every call delegates HTTP to the auto-generated Smplkit.Internal.Generated.Jobs
// client; this wrapper only shapes models and raises SDK exceptions.

using Smplkit.Internal;
using GenJobs = Smplkit.Internal.Generated.Jobs;
using HttpMethod = Smplkit.Jobs.HttpMethod;

namespace Smplkit.Jobs;

/// <summary>Run history and run actions (<c>jobs.Runs</c>).</summary>
public sealed class RunsClient
{
    private readonly GenJobs.JobsClient _gen;

    internal RunsClient(GenJobs.JobsClient gen) => _gen = gen;

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
            .Select(JobsClient.RunFromResource).ToList();
    }

    /// <summary>Fetch a single run by id.</summary>
    public async Task<Run> GetAsync(string runId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_runAsync(Guid.Parse(runId), ct)).ConfigureAwait(false);
        return JobsClient.RunFromResource(resp.Data);
    }

    /// <summary>Cancel a pending run.</summary>
    public async Task<Run> CancelAsync(string runId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Cancel_runAsync(Guid.Parse(runId), ct)).ConfigureAwait(false);
        return JobsClient.RunFromResource(resp.Data);
    }

    /// <summary>Re-run a prior run, spawning a new <c>RERUN</c> run.</summary>
    public async Task<Run> RerunAsync(string runId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Rerun_runAsync(Guid.Parse(runId), ct)).ConfigureAwait(false);
        return JobsClient.RunFromResource(resp.Data);
    }
}

/// <summary>
/// Smpl Jobs client.
/// </summary>
/// <remarks>
/// <para>Reachable as <c>client.Jobs</c> (<see cref="Smplkit.SmplClient"/>) or
/// constructed directly:</para>
/// <code>
/// using var jobs = new JobsClient();
/// foreach (var job in await jobs.ListAsync())
///     Console.WriteLine(job.Id);
/// </code>
/// </remarks>
public sealed class JobsClient : IDisposable
{
    private readonly GenJobs.JobsClient _gen;
    private readonly HttpClient? _ownedHttpClient;

    /// <summary>Run history and run actions.</summary>
    public RunsClient Runs { get; }

    /// <summary>
    /// Initializes a new <see cref="JobsClient"/>.
    /// </summary>
    /// <param name="apiKey">API key. When omitted, resolved from <c>SMPLKIT_API_KEY</c> or <c>~/.smplkit</c>.</param>
    /// <param name="profile">Named <c>~/.smplkit</c> profile section.</param>
    /// <param name="baseDomain">Base domain for API requests (default <c>smplkit.com</c>).</param>
    /// <param name="scheme">URL scheme (default <c>https</c>).</param>
    /// <param name="debug">Enable SDK debug logging.</param>
    /// <param name="extraHeaders">Extra headers attached to every request.</param>
    public JobsClient(
        string? apiKey = null,
        string? profile = null,
        string? baseDomain = null,
        string? scheme = null,
        bool? debug = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        // Reuse the management config resolver (jobs is account-global and never
        // environment-scoped) and the shared per-service URL helper, so a
        // standalone jobs client resolves credentials/base-domain from
        // ~/.smplkit / env vars / constructor args exactly like the top-level
        // clients do.
        var resolved = ConfigResolver.ResolveForManagement(new SmplClientOptions
        {
            ApiKey = apiKey,
            Profile = profile,
            BaseDomain = baseDomain,
            Scheme = scheme,
            Debug = debug,
        });
        _ownedHttpClient = new HttpClient();
        var clients = new GeneratedClientFactory(_ownedHttpClient, new SmplClientOptions
        {
            ApiKey = resolved.ApiKey,
            BaseDomain = resolved.BaseDomain,
            Scheme = resolved.Scheme,
            ExtraHeaders = extraHeaders is null ? null : new Dictionary<string, string>(extraHeaders),
        });
        _gen = clients.Jobs;
        Runs = new RunsClient(_gen);
    }

    /// <summary>
    /// Internal — wired by a top-level client so the jobs surface shares one
    /// connection pool. The borrowed generated client is owned by the parent;
    /// this instance must not tear it down.
    /// </summary>
    internal JobsClient(GenJobs.JobsClient gen)
    {
        _gen = gen;
        _ownedHttpClient = null;
        Runs = new RunsClient(_gen);
    }

    /// <summary>
    /// Return an unsaved <see cref="Job"/>. Call <see cref="Job.SaveAsync"/> to create it.
    /// </summary>
    /// <param name="id">Caller-supplied unique identifier for the job. Unique within
    /// the account and immutable; the service returns 409 if another live job already
    /// uses this id.</param>
    /// <param name="name">Human-readable name for the job.</param>
    /// <param name="schedule">When the job runs.</param>
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
    public async Task<Job> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_jobAsync(id, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Soft-delete a job by id. Prefer <see cref="Job.DeleteAsync"/> when you
    /// already have a <see cref="Job"/> instance.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _gen.Delete_jobAsync(id, ct));

    /// <summary>Trigger one immediate <c>MANUAL</c> run of the job.</summary>
    public async Task<Run> RunAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Run_job_nowAsync(id, ct)).ConfigureAwait(false);
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

    /// <summary>POST a new job. Called by <see cref="Job.SaveAsync"/>; not for direct use.</summary>
    internal async Task<Job> CreateAsync(Job job, CancellationToken ct)
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
    internal async Task<Job> UpdateAsync(Job job, CancellationToken ct)
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

    /// <summary>
    /// Release HTTP resources — only when this client owns its transport.
    /// </summary>
    /// <remarks>
    /// A jobs client wired by a top-level client shares that client's transport
    /// and must not close it here; the owning client's <c>Dispose()</c> handles
    /// teardown.
    /// </remarks>
    public void Dispose()
    {
        _ownedHttpClient?.Dispose();
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
