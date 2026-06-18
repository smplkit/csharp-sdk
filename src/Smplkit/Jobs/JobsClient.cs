// Smpl Jobs SDK client (client.Jobs on SmplClient, or standalone JobsClient).
//
// Unlike Config/Flags/Logging, Jobs installs no in-process machinery — no
// environment registration, no WebSocket, no in-process logging hooks. It is a
// product you *use*, not infrastructure you *install*: a single JobsClient
// exposes the full surface, reachable two ways:
//
// * client.Jobs.* on SmplClient
// * directly — new JobsClient(apiKey: ...) — for callers that only need jobs.
//
// A Job is an active record: build it with JobsClient.New, set fields, and
// call SaveAsync() (create when new, full-replace update when it already
// exists) or DeleteAsync(). Runs are read-only views with rerun/cancel actions;
// run history and run actions also live on jobs.Runs.
//
// Environment scoping: a job carries a per-environment `environments` map
// (enablement + optional configuration override per environment); the base
// `enabled` is a read-only server-derived roll-up the SDK never writes. A
// one-off job is born in a single environment named via the
// X-Smplkit-Environment header on create; a manual run executes in the
// environment named on the trigger; run reads accept a filter[environment]
// scope. The client's configured environment defaults all three.
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
    private readonly string? _environment;

    internal RunsClient(GenJobs.JobsClient gen, string? environment = null)
    {
        _gen = gen;
        _environment = environment;
    }

    /// <summary>List past runs, most recent first (cursor paginated).</summary>
    /// <param name="job">Return only runs of the job with this id. <c>null</c>
    /// lists runs across all jobs in the account.</param>
    /// <param name="environments">Restrict to runs stamped with any of these
    /// environment keys. <c>null</c> (or empty) falls back to the client's
    /// configured environment (if any); with none, covers every environment you
    /// can access.</param>
    /// <param name="pageSize">Maximum number of runs to return in this page.
    /// <c>null</c> uses the server default.</param>
    /// <param name="after">Opaque cursor from a previous page; returns the runs
    /// that follow it. <c>null</c> starts from the first page.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The runs in this page, as a list of <see cref="Run"/>.</returns>
    public async Task<IReadOnlyList<Run>> ListAsync(
        string? job = null,
        IEnumerable<string>? environments = null,
        int? pageSize = null,
        string? after = null,
        CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_runsAsync(
                filterjob: job,
                filterenvironment: Helpers.ResolveEnvironmentFilter(environments, _environment),
                pagesize: pageSize,
                pageafter: after,
                cancellationToken: ct)).ConfigureAwait(false);
        return (resp.Data ?? new List<GenJobs.RunResource>())
            .Select(r => JobsClient.RunFromResource(r, this)).ToList();
    }

    /// <summary>Fetch a single run by its id.</summary>
    /// <param name="runId">Identifier of the run to fetch.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The matching <see cref="Run"/>.</returns>
    public async Task<Run> GetAsync(string runId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_runAsync(Guid.Parse(runId), ct)).ConfigureAwait(false);
        return JobsClient.RunFromResource(resp.Data, this);
    }

    /// <summary>Cancel a run that has not finished yet.</summary>
    /// <param name="runId">Identifier of the run to cancel.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The updated <see cref="Run"/> reflecting the cancellation.</returns>
    public async Task<Run> CancelAsync(string runId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Cancel_runAsync(Guid.Parse(runId), ct)).ConfigureAwait(false);
        return JobsClient.RunFromResource(resp.Data, this);
    }

    /// <summary>Start a new run that repeats a previous one.</summary>
    /// <param name="runId">Identifier of the run to repeat.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The new <see cref="Run"/>, with <see cref="Run.RerunOf"/> set to <paramref name="runId"/>.</returns>
    public async Task<Run> RerunAsync(string runId, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Rerun_runAsync(Guid.Parse(runId), ct)).ConfigureAwait(false);
        return JobsClient.RunFromResource(resp.Data, this);
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
    private readonly string? _environment;

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
    /// <param name="environment">Default environment for environment-scoped
    /// operations — the environment a one-off job created through this client is
    /// born in, the default a manual run executes in, and the default scope for
    /// <see cref="RunsClient.ListAsync"/>. <c>null</c> leaves these unset (the
    /// credential's permitted environment is implied where unambiguous).</param>
    public JobsClient(
        string? apiKey = null,
        string? profile = null,
        string? baseDomain = null,
        string? scheme = null,
        bool? debug = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        string? environment = null)
    {
        // Reuse the account-global config resolver (jobs is never
        // environment-scoped on the transport — env scoping rides the
        // X-Smplkit-Environment header / filter[environment]) and the shared
        // per-service URL helper, so a standalone jobs client resolves
        // credentials/base-domain from ~/.smplkit / env vars / constructor args
        // exactly like the top-level clients do.
        var resolved = ConfigResolver.ResolveAccountGlobal(new SmplClientOptions
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
        _environment = environment;
        Runs = new RunsClient(_gen, environment);
    }

    /// <summary>
    /// Internal — wired by a top-level client so the jobs surface shares one
    /// connection pool. The borrowed generated client is owned by the parent;
    /// this instance must not tear it down. <paramref name="environment"/> is the
    /// SDK's configured runtime environment, which defaults the one-off birth /
    /// manual-run / run-filter scoping just like the standalone constructor.
    /// </summary>
    internal JobsClient(GenJobs.JobsClient gen, string? environment = null)
    {
        _gen = gen;
        _ownedHttpClient = null;
        _environment = environment;
        Runs = new RunsClient(_gen, environment);
    }

    /// <summary>
    /// Return an unsaved <see cref="Job"/>. Call <see cref="Job.SaveAsync"/> to create it.
    /// </summary>
    /// <param name="id">Caller-supplied unique identifier for the job. Unique within
    /// the account and immutable; the service returns 409 if another live job already
    /// uses this id.</param>
    /// <param name="name">Human-readable name for the job.</param>
    /// <param name="schedule">When the job runs. One of: a 5-field cron expression
    /// evaluated in UTC (recurring), an ISO-8601 datetime (a one-off run at that
    /// instant), or the literal <c>"now"</c> (run once, as soon as possible). A
    /// datetime or <c>"now"</c> job disables itself after it fires.</param>
    /// <param name="configuration">The HTTP request the job sends each time it fires.</param>
    /// <param name="description">Free-text description for the job. Defaults to none.</param>
    /// <param name="environments">Per-environment overrides for a recurring job,
    /// keyed by environment key — each a <see cref="JobEnvironment"/> setting
    /// <c>Enabled</c> and an optional configuration override. A recurring job fires
    /// only in environments enabled here. Ignored for a one-off job, which is born
    /// in <paramref name="environment"/>.</param>
    /// <param name="concurrencyPolicy">How overlapping runs are handled. <c>"ALLOW"</c>
    /// (the default and only value today) permits a new run to start while a previous
    /// one is still in flight.</param>
    /// <param name="environment">For a one-off job (<c>"now"</c> / datetime schedule),
    /// the environment it is born in. Defaults to the client's configured environment.
    /// Ignored for a recurring job.</param>
    /// <returns>An unsaved <see cref="Job"/> bound to this client.</returns>
    public Job New(
        string id,
        string name,
        string schedule,
        HttpConfig configuration,
        string? description = null,
        IDictionary<string, JobEnvironment>? environments = null,
        string concurrencyPolicy = "ALLOW",
        string? environment = null)
    {
        var job = new Job(
            this,
            id: id,
            name: name,
            schedule: schedule,
            configuration: configuration,
            description: description,
            environments: environments,
            concurrencyPolicy: concurrencyPolicy);
        job.BirthEnvironment = environment ?? _environment;
        return job;
    }

    /// <summary>List jobs in the account.</summary>
    /// <param name="recurring">Return only recurring (<c>true</c>) or only one-off
    /// (<c>false</c>) jobs. <c>null</c> lists both.</param>
    /// <param name="name">Return only jobs whose name contains this text
    /// (case-insensitive). <c>null</c> lists all.</param>
    /// <param name="pageNumber">1-based page to return. <c>null</c> returns the first page.</param>
    /// <param name="pageSize">Maximum number of jobs to return in this page.
    /// <c>null</c> uses the server default.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The jobs in this page, as a list of <see cref="Job"/>.</returns>
    public async Task<IReadOnlyList<Job>> ListAsync(
        bool? recurring = null, string? name = null, int? pageNumber = null, int? pageSize = null, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_jobsAsync(filterrecurring: recurring, filtername: name, pagenumber: pageNumber, pagesize: pageSize, cancellationToken: ct)).ConfigureAwait(false);
        return (resp.Data ?? new List<GenJobs.JobResource>())
            .Select(FromResource).ToList();
    }

    /// <summary>Fetch a single job by its id. The returned instance is bound to this
    /// client so <see cref="Job.SaveAsync"/> / <see cref="Job.DeleteAsync"/> work.</summary>
    /// <param name="id">Identifier of the job to fetch.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The matching <see cref="Job"/>.</returns>
    public async Task<Job> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_jobAsync(id, ct)).ConfigureAwait(false);
        return FromResource(resp.Data);
    }

    /// <summary>Delete a job by its id. Prefer <see cref="Job.DeleteAsync"/> when you
    /// already have a <see cref="Job"/> instance.</summary>
    /// <param name="id">Identifier of the job to delete.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _gen.Delete_jobAsync(id, ct));

    /// <summary>Trigger one immediate, manual run of a job, ignoring its schedule.
    ///
    /// <para>This starts an ad-hoc run right now in addition to any scheduled
    /// runs; it does not alter the job's schedule. To read or act on existing
    /// runs, use <see cref="Runs"/>.</para></summary>
    /// <param name="id">Identifier of the job to run.</param>
    /// <param name="environment">Environment the manual run executes in. Defaults
    /// to the client's configured environment; when the job is enabled in exactly
    /// one environment that environment is used, and a single-environment
    /// credential implies it.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The <see cref="Run"/> that was started, with <see cref="Run.Trigger"/> set to <c>MANUAL</c>.</returns>
    public async Task<Run> RunAsync(string id, string? environment = null, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Run_job_nowAsync(id, x_Smplkit_Environment: environment ?? _environment, cancellationToken: ct)).ConfigureAwait(false);
        return RunFromResource(resp.Data, Runs);
    }

    /// <summary>Report current-period usage against the account's plan allotments.</summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A <see cref="Usage"/> snapshot with runs used/included and
    /// active-job counts for the current period.</returns>
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
        // A one-off job is born in its birth environment (explicit New(environment:)
        // or the client default); recurring jobs ignore the header and route via
        // their environments map.
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Create_jobAsync(body, x_Smplkit_Environment: job.BirthEnvironment, cancellationToken: ct)).ConfigureAwait(false);
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
        // Updates carry the client's configured environment (if any); a recurring
        // job ignores it and routes via its environments map.
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Update_jobAsync(job.Id, body, x_Smplkit_Environment: _environment, cancellationToken: ct)).ConfigureAwait(false);
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
        var attrs = new GenJobs.Job
        {
            Name = src.Name,
            Description = src.Description,
            Type = src.Type,
            Schedule = src.Schedule,
            Configuration = ToGenConfiguration(src.Configuration),
            Concurrency_policy = src.ConcurrencyPolicy,
        };
        // The base `enabled` is a read-only server-derived roll-up; never send it.
        // Enablement travels entirely through the per-environment overrides. The
        // generated `Enabled` is left null and omitted by the jobs serializer
        // (DefaultIgnoreCondition.WhenWritingNull — see JobsClientSerialization).
        if (src.Environments.Count > 0)
        {
            attrs.Environments = src.Environments.ToDictionary(
                kv => kv.Key,
                kv => new GenJobs.JobEnvironment
                {
                    Enabled = kv.Value.Enabled,
                    // Per-environment cron override; sent only when set (omitted by
                    // the jobs serializer when null, inheriting the base schedule).
                    Schedule = kv.Value.Schedule,
                    Configuration = kv.Value.Configuration is { } cfg ? ToGenConfiguration(cfg) : null,
                    // Next_run_at is read-only/server-derived; never sent.
                });
        }
        return attrs;
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
        // The base `enabled` is no longer a wire attribute; the wrapper derives it
        // as a roll-up over the per-environment map (see Job.Enabled). NextRunAt is
        // now per-environment (JobEnvironment.NextRunAt), not a top-level field.
        return new Job(
            this,
            id: r.Id ?? string.Empty,
            name: a.Name ?? string.Empty,
            schedule: a.Schedule ?? string.Empty,
            configuration: ConfigFromGen(a.Configuration),
            description: a.Description,
            environments: EnvironmentsFromGen(a.Environments),
            recurring: a.Recurring,
            type: a.Type ?? "http",
            concurrencyPolicy: a.Concurrency_policy ?? "ALLOW",
            createdAt: a.Created_at,
            updatedAt: a.Updated_at,
            deletedAt: a.Deleted_at,
            version: a.Version);
    }

    private static IDictionary<string, JobEnvironment> EnvironmentsFromGen(
        IDictionary<string, GenJobs.JobEnvironment>? src)
    {
        var result = new Dictionary<string, JobEnvironment>();
        if (src == null) return result;
        foreach (var (key, env) in src)
        {
            result[key] = new JobEnvironment
            {
                Enabled = env.Enabled,
                Schedule = env.Schedule,
                Configuration = env.Configuration is { } cfg ? ConfigFromGen(cfg) : null,
                NextRunAt = env.Next_run_at,
            };
        }
        return result;
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

    internal static Run RunFromResource(GenJobs.RunResource r, RunsClient? runs = null)
    {
        var a = r.Attributes;
        return new Run(
            id: r.Id ?? string.Empty,
            job: a.Job ?? string.Empty,
            environment: a.Environment ?? string.Empty,
            trigger: a.Trigger.ToString(),
            status: a.Status.ToString(),
            runs: runs,
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
