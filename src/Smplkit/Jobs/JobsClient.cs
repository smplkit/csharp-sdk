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
// A Job is an active record: build it with JobsClient.NewRecurringJob /
// NewManualJob / Schedule, set fields, and call SaveAsync() (create when new,
// full-replace update when it already exists) or DeleteAsync(). Runs are
// read-only views with rerun/cancel actions;
// run history and run actions also live on jobs.Runs.
//
// Environment scoping: a job carries a per-environment `environments` map
// (enablement + optional configuration override per environment); the base
// `enabled` is a read-only server-derived roll-up the SDK never writes. A
// one-off job is born in a single environment named as an enabled entry in
// that map on create; a manual run names its environment in the run-now request
// body; run reads accept a filter[environment] scope. The client's configured
// environment defaults all three.
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
    /// <param name="triggers">Restrict to runs started by any of these triggers (the
    /// <see cref="RunTrigger"/> constants) — e.g. <c>[RunTrigger.Retry]</c> for
    /// automatic retries — serialized as a comma-joined <c>filter[trigger]</c>.
    /// <c>null</c> (or empty) covers every trigger.</param>
    /// <param name="lastRunOnly">When <c>true</c>, collapse the result to the last
    /// completed (succeeded / failed / canceled) run per job-and-environment;
    /// in-flight runs are excluded. The other filters apply first, then the collapse.
    /// Defaults to <c>false</c>; the query parameter is sent only when <c>true</c>.</param>
    /// <param name="pageSize">Maximum number of runs to return in this page.
    /// <c>null</c> uses the server default.</param>
    /// <param name="after">Opaque cursor from a previous page; returns the runs
    /// that follow it. <c>null</c> starts from the first page.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The runs in this page, as a list of <see cref="Run"/>.</returns>
    public async Task<IReadOnlyList<Run>> ListAsync(
        string? job = null,
        IEnumerable<string>? environments = null,
        IEnumerable<string>? triggers = null,
        bool lastRunOnly = false,
        int? pageSize = null,
        string? after = null,
        CancellationToken ct = default)
    {
        var triggerFilter = triggers is null ? null : string.Join(",", triggers);
        if (triggerFilter is { Length: 0 }) triggerFilter = null;
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_runsAsync(
                filterjob: job,
                filterenvironment: Helpers.ResolveEnvironmentFilter(environments, _environment),
                filtertrigger: triggerFilter,
                // The generated default would emit last_run_only=false on every call;
                // send the parameter only when explicitly requested.
                last_run_only: lastRunOnly ? true : (bool?)null,
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

/// <summary>Manage reusable retry policies (<c>jobs.RetryPolicies</c>).</summary>
/// <remarks>
/// <para>Reached as <c>client.Jobs.RetryPolicies</c>. A <see cref="RetryPolicy"/> is
/// an active record: build one with <see cref="New"/>, set fields, and call
/// <see cref="RetryPolicy.SaveAsync"/>; then reference it from a job's retry policy
/// (see <see cref="JobsClient.NewRecurringJob"/>, or assign it to
/// <see cref="Job.RetryPolicy"/> / <see cref="JobEnvironment.RetryPolicy"/>). Retry
/// policies are account-global — never environment-scoped.</para>
/// </remarks>
public sealed class RetryPoliciesClient
{
    private readonly GenJobs.JobsClient _gen;

    internal RetryPoliciesClient(GenJobs.JobsClient gen)
    {
        _gen = gen;
    }

    /// <summary>Return an unsaved <see cref="RetryPolicy"/>. Call
    /// <see cref="RetryPolicy.SaveAsync"/> to create it.</summary>
    /// <param name="id">Caller-supplied unique identifier for the policy. Unique
    /// within the account and immutable; the service returns 409 if another live
    /// policy already uses this id.</param>
    /// <param name="name">Human-readable name for the policy.</param>
    /// <param name="maxRetries">How many times a failed run is retried after the
    /// initial attempt — <c>3</c> means up to 4 attempts total. <c>0</c> disables
    /// retries. Maximum 10.</param>
    /// <param name="backoff">How the wait between retries grows (see <see cref="Backoff"/>).</param>
    /// <param name="delaySeconds">The wait before a retry, in seconds — the constant
    /// wait for <see cref="Backoff.Fixed"/>, or the base that doubles each retry for
    /// <see cref="Backoff.Exponential"/>.</param>
    /// <param name="maxDelaySeconds">Ceiling on the wait between retries, for
    /// <see cref="Backoff.Exponential"/> backoff only. <c>null</c> (the default)
    /// leaves it uncapped; omit it for <see cref="Backoff.Fixed"/> backoff.</param>
    /// <param name="retryOnTimeout">Retry a run that timed out. Defaults to
    /// <c>false</c>.</param>
    /// <param name="retryOnConnectionError">Retry a run whose destination could not be
    /// reached (DNS, connection refused, TLS, or transport error). Defaults to
    /// <c>false</c>.</param>
    /// <param name="retryStatuses">Allowlist of response status patterns to retry on a
    /// non-success response — each an exact 3-digit code (<c>"429"</c>) or a status class
    /// (<c>"5xx"</c>). <c>null</c> (the default) retries no status.</param>
    /// <param name="retryStatusesExcept">Patterns subtracted from
    /// <paramref name="retryStatuses"/> (<c>except</c> wins on overlap). <c>null</c> (the
    /// default) subtracts nothing.</param>
    /// <returns>An unsaved <see cref="RetryPolicy"/> bound to this client.</returns>
    public RetryPolicy New(
        string id,
        string name,
        int maxRetries,
        Backoff backoff,
        int delaySeconds,
        int? maxDelaySeconds = null,
        bool retryOnTimeout = false,
        bool retryOnConnectionError = false,
        IList<string>? retryStatuses = null,
        IList<string>? retryStatusesExcept = null)
        => new RetryPolicy(
            this, id, name, maxRetries, backoff, delaySeconds, maxDelaySeconds,
            retryOnTimeout, retryOnConnectionError, retryStatuses, retryStatusesExcept);

    /// <summary>List retry policies in the account.</summary>
    /// <param name="name">Return only policies whose name contains this text
    /// (case-insensitive). <c>null</c> lists all.</param>
    /// <param name="pageNumber">1-based page to return. <c>null</c> returns the first page.</param>
    /// <param name="pageSize">Maximum number of policies to return in this page.
    /// <c>null</c> uses the server default.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The policies in this page, as a list of <see cref="RetryPolicy"/>.</returns>
    public async Task<IReadOnlyList<RetryPolicy>> ListAsync(
        string? name = null, int? pageNumber = null, int? pageSize = null, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_retry_policiesAsync(
                filtername: name, pagenumber: pageNumber, pagesize: pageSize, cancellationToken: ct)).ConfigureAwait(false);
        return (resp.Data ?? new List<GenJobs.RetryPolicyResource>())
            .Select(RetryPolicyFromResource).ToList();
    }

    /// <summary>Fetch a single retry policy by its id. The returned instance is bound
    /// to this client so <see cref="RetryPolicy.SaveAsync"/> /
    /// <see cref="RetryPolicy.DeleteAsync"/> work.</summary>
    /// <param name="id">Identifier of the policy to fetch.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The matching <see cref="RetryPolicy"/>.</returns>
    public async Task<RetryPolicy> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Get_retry_policyAsync(id, ct)).ConfigureAwait(false);
        return RetryPolicyFromResource(resp.Data);
    }

    /// <summary>Delete a retry policy by its id. Prefer
    /// <see cref="RetryPolicy.DeleteAsync"/> when you already have a
    /// <see cref="RetryPolicy"/> instance.</summary>
    /// <param name="id">Identifier of the policy to delete.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _gen.Delete_retry_policyAsync(id, ct));

    /// <summary>POST a new policy. Called by <see cref="RetryPolicy.SaveAsync"/>; not for direct use.</summary>
    internal async Task<RetryPolicy> CreateAsync(RetryPolicy policy, CancellationToken ct)
    {
        var body = new GenJobs.RetryPolicyCreateRequest
        {
            Data = new GenJobs.RetryPolicyCreateResource
            {
                Id = policy.Id,
                Type = "retry_policy",
                Attributes = BuildRetryPolicyAttributes(policy),
            },
        };
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Create_retry_policyAsync(body, ct)).ConfigureAwait(false);
        return RetryPolicyFromResource(resp.Data);
    }

    /// <summary>Full-replace PUT. Called by <see cref="RetryPolicy.SaveAsync"/>; not for direct use.</summary>
    internal async Task<RetryPolicy> UpdateAsync(RetryPolicy policy, CancellationToken ct)
    {
        var body = new GenJobs.RetryPolicyRequest
        {
            Data = new GenJobs.RetryPolicyResource
            {
                Id = policy.Id,
                Type = "retry_policy",
                Attributes = BuildRetryPolicyAttributes(policy),
            },
        };
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Update_retry_policyAsync(policy.Id, body, ct)).ConfigureAwait(false);
        return RetryPolicyFromResource(resp.Data);
    }

    // ------------------------------------------------------------------
    // Wire <-> wrapper conversions
    // ------------------------------------------------------------------

    private static GenJobs.RetryPolicy BuildRetryPolicyAttributes(RetryPolicy src)
    {
        var attrs = new GenJobs.RetryPolicy
        {
            Name = src.Name,
            Max_retries = src.MaxRetries,
            Backoff = ToGenBackoff(src.Backoff),
            Delay_seconds = src.DelaySeconds,
            // Only valid with exponential backoff; the RetryPolicy converter omits it
            // on the wire when null.
            Max_delay_seconds = src.MaxDelaySeconds,
            Retry_on_timeout = src.RetryOnTimeout,
            Retry_on_connection_error = src.RetryOnConnectionError,
            Retry_statuses = new List<string>(src.RetryStatuses),
            Retry_statuses_except = new List<string>(src.RetryStatusesExcept),
        };
        return attrs;
    }

    private static GenJobs.RetryPolicyBackoff ToGenBackoff(Backoff backoff) => backoff switch
    {
        Backoff.Fixed => GenJobs.RetryPolicyBackoff.Fixed,
        _ => GenJobs.RetryPolicyBackoff.Exponential,
    };

    private RetryPolicy RetryPolicyFromResource(GenJobs.RetryPolicyResource r)
    {
        var a = r.Attributes;
        return new RetryPolicy(
            this,
            id: r.Id ?? string.Empty,
            name: a.Name ?? string.Empty,
            maxRetries: a.Max_retries,
            backoff: FromGenBackoff(a.Backoff),
            delaySeconds: a.Delay_seconds,
            maxDelaySeconds: a.Max_delay_seconds,
            retryOnTimeout: a.Retry_on_timeout,
            retryOnConnectionError: a.Retry_on_connection_error,
            // The RetryPolicyJsonConverter always populates these lists (never null).
            retryStatuses: new List<string>(a.Retry_statuses ?? new List<string>()),
            retryStatusesExcept: new List<string>(a.Retry_statuses_except ?? new List<string>()),
            createdAt: a.Created_at,
            updatedAt: a.Updated_at,
            deletedAt: a.Deleted_at,
            version: a.Version);
    }

    private static Backoff FromGenBackoff(GenJobs.RetryPolicyBackoff backoff) => backoff switch
    {
        GenJobs.RetryPolicyBackoff.Fixed => Backoff.Fixed,
        _ => Backoff.Exponential,
    };
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

    /// <summary>Reusable retry policies (account-global).</summary>
    public RetryPoliciesClient RetryPolicies { get; }

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
        // environment-scoped on the transport — env scoping rides the request
        // body's environments map / run-now body / filter[environment]) and the shared
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
        RetryPolicies = new RetryPoliciesClient(_gen);
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
        RetryPolicies = new RetryPoliciesClient(_gen);
    }

    /// <summary>Build an unsaved <see cref="Job"/> bound to this client. Shared by
    /// the public factory methods.</summary>
    private Job NewJob(
        string id,
        string name,
        string? schedule,
        HttpConfig configuration,
        string? description,
        IDictionary<string, JobEnvironment>? environments,
        string concurrencyPolicy,
        string? timezone = null,
        string? retryPolicy = null)
        => new Job(
            this,
            id: id,
            name: name,
            schedule: schedule,
            configuration: configuration,
            description: description,
            environments: environments,
            concurrencyPolicy: concurrencyPolicy,
            timezone: timezone,
            retryPolicy: retryPolicy);

    /// <summary>A one-off job's birth environment as an enabled entry in the
    /// <c>environments</c> map. The target environment of a one-off job is conveyed
    /// by the keys of the body's <c>environments</c> map (there is no request
    /// header). <c>null</c> when the environment is unknown, leaving the map empty
    /// so a single-environment credential implies it server-side.</summary>
    private IDictionary<string, JobEnvironment>? BirthEnvironmentMap(string? environment)
    {
        var env = environment ?? _environment;
        return env is null
            ? null
            : new Dictionary<string, JobEnvironment> { [env] = new JobEnvironment { Enabled = true } };
    }

    /// <summary>
    /// Return an unsaved recurring <see cref="Job"/>. Call <see cref="Job.SaveAsync"/> to create it.
    /// </summary>
    /// <param name="id">Caller-supplied unique identifier for the job. Unique within
    /// the account and immutable; the service returns 409 if another live job already
    /// uses this id.</param>
    /// <param name="name">Human-readable name for the job.</param>
    /// <param name="schedule">The base cadence — a 5-field cron expression evaluated
    /// in the job's <paramref name="timezone"/> (UTC by default), e.g.
    /// <c>"0 2 * * *"</c> — that every environment inherits unless it sets its own
    /// override.</param>
    /// <param name="configuration">The HTTP request the job sends each time it fires.</param>
    /// <param name="description">Free-text description for the job. Defaults to none.</param>
    /// <param name="timezone">Base IANA timezone the cron <paramref name="schedule"/>
    /// is evaluated in (e.g. <c>"America/New_York"</c>), DST-aware. <c>null</c> (the
    /// default) means UTC. Every environment inherits it unless it overrides it.</param>
    /// <param name="retryPolicy">Base retry policy for failed runs — the id of a
    /// <see cref="RetryPolicy"/>, overridable per environment. <c>null</c> (the
    /// default) uses the built-in <c>Default</c> policy, which never retries.</param>
    /// <param name="environments">Per-environment overrides keyed by environment key —
    /// each a <see cref="JobEnvironment"/> setting <c>Enabled</c> and optional
    /// schedule / configuration overrides. The job is scheduled only in environments
    /// enabled here.</param>
    /// <param name="concurrencyPolicy">How overlapping runs are handled. <c>"ALLOW"</c>
    /// (the default and only value today) permits a new run to start while a previous
    /// one is still in flight.</param>
    /// <returns>An unsaved recurring <see cref="Job"/> bound to this client.</returns>
    public Job NewRecurringJob(
        string id,
        string name,
        string schedule,
        HttpConfig configuration,
        string? description = null,
        string? timezone = null,
        string? retryPolicy = null,
        IDictionary<string, JobEnvironment>? environments = null,
        string concurrencyPolicy = "ALLOW")
        => NewJob(id, name, schedule, configuration, description, environments, concurrencyPolicy,
            timezone: timezone, retryPolicy: retryPolicy);

    /// <summary>
    /// Return an unsaved manual <see cref="Job"/>. Call <see cref="Job.SaveAsync"/> to create it.
    ///
    /// <para>A manual job has no schedule — it never auto-fires and runs only when
    /// triggered via <see cref="RunAsync"/> / <see cref="Job.TriggerAsync"/>.</para>
    /// </summary>
    /// <param name="id">Caller-supplied unique identifier for the job. Unique within
    /// the account and immutable; the service returns 409 if another live job already
    /// uses this id.</param>
    /// <param name="name">Human-readable name for the job.</param>
    /// <param name="configuration">The HTTP request the job sends each time it runs.</param>
    /// <param name="description">Free-text description for the job. Defaults to none.</param>
    /// <param name="environments">Per-environment overrides keyed by environment key —
    /// each a <see cref="JobEnvironment"/> setting <c>Enabled</c> and an optional
    /// configuration override. The job is triggerable only in environments enabled
    /// here.</param>
    /// <param name="concurrencyPolicy">How overlapping runs are handled. <c>"ALLOW"</c>
    /// (the default and only value today) permits a new run to start while a previous
    /// one is still in flight.</param>
    /// <param name="retryPolicy">Retry policy for failed runs — the id of a
    /// <see cref="RetryPolicy"/>, overridable per environment. <c>null</c> (the
    /// default) uses the built-in <c>Default</c> policy, which never retries.</param>
    /// <returns>An unsaved manual <see cref="Job"/> bound to this client.</returns>
    public Job NewManualJob(
        string id,
        string name,
        HttpConfig configuration,
        string? description = null,
        IDictionary<string, JobEnvironment>? environments = null,
        string concurrencyPolicy = "ALLOW",
        string? retryPolicy = null)
        => NewJob(id, name, schedule: null, configuration, description, environments, concurrencyPolicy,
            retryPolicy: retryPolicy);

    /// <summary>
    /// Return an unsaved one-off <see cref="Job"/>. Call <see cref="Job.SaveAsync"/> to create it.
    ///
    /// <para>A one-off job runs a single time at <paramref name="schedule"/> and is
    /// then spent.</para>
    /// </summary>
    /// <param name="id">Caller-supplied unique identifier for the job. Unique within
    /// the account and immutable; the service returns 409 if another live job already
    /// uses this id.</param>
    /// <param name="name">Human-readable name for the job.</param>
    /// <param name="schedule">The instant the single run fires.</param>
    /// <param name="configuration">The HTTP request the job sends when it runs.</param>
    /// <param name="description">Free-text description for the job. Defaults to none.</param>
    /// <param name="concurrencyPolicy">How overlapping runs are handled. <c>"ALLOW"</c>
    /// (the default and only value today) permits a new run to start while a previous
    /// one is still in flight.</param>
    /// <param name="retryPolicy">Retry policy for failed runs — the id of a
    /// <see cref="RetryPolicy"/>. <c>null</c> (the default) uses the built-in
    /// <c>Default</c> policy, which never retries.</param>
    /// <param name="environment">The environment the job is born in. Defaults to the
    /// client's configured environment.</param>
    /// <returns>An unsaved one-off <see cref="Job"/> bound to this client.</returns>
    public Job Schedule(
        string id,
        string name,
        DateTimeOffset schedule,
        HttpConfig configuration,
        string? description = null,
        string concurrencyPolicy = "ALLOW",
        string? retryPolicy = null,
        string? environment = null)
        => NewJob(
            id, name,
            schedule.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            configuration, description, BirthEnvironmentMap(environment), concurrencyPolicy,
            retryPolicy: retryPolicy);

    /// <summary>List jobs in the account.</summary>
    /// <param name="kind">Return only jobs of this <see cref="JobKind"/>. <c>null</c>
    /// lists recurring and manual jobs; one-off jobs are omitted unless you pass
    /// <see cref="JobKind.OneOff"/>.</param>
    /// <param name="scheduled">Return only jobs that have an upcoming fire in some
    /// environment (<c>true</c>) or none (<c>false</c>) — the feed for an
    /// upcoming-runs view, which includes one-offs. <c>null</c> does not filter on
    /// scheduling.</param>
    /// <param name="name">Return only jobs whose name contains this text
    /// (case-insensitive). <c>null</c> lists all.</param>
    /// <param name="pageNumber">1-based page to return. <c>null</c> returns the first page.</param>
    /// <param name="pageSize">Maximum number of jobs to return in this page.
    /// <c>null</c> uses the server default.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The jobs in this page, as a list of <see cref="Job"/>.</returns>
    public async Task<IReadOnlyList<Job>> ListAsync(
        JobKind? kind = null, bool? scheduled = null, string? name = null, int? pageNumber = null, int? pageSize = null, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.List_jobsAsync(filterkind: kind?.ToWireValue(), filterscheduled: scheduled, filtername: name, pagenumber: pageNumber, pagesize: pageSize, cancellationToken: ct)).ConfigureAwait(false);
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
    /// credential implies it. The job must be enabled in the chosen environment.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The <see cref="Run"/> that was started, with <see cref="Run.Trigger"/> set to <c>MANUAL</c>.</returns>
    public async Task<Run> RunAsync(string id, string? environment = null, CancellationToken ct = default)
    {
        // The target environment travels in the request body. When neither an
        // explicit arg nor the client default resolves it, Environment stays null
        // and the serializer (DefaultIgnoreCondition.WhenWritingNull) emits an empty
        // body, letting the service imply the environment.
        var body = new GenJobs.RunNowRequest { Environment = environment ?? _environment };
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Run_job_nowAsync(id, body, cancellationToken: ct)).ConfigureAwait(false);
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
        // A one-off job's birth environment travels as an enabled entry in the
        // body's environments map (built by Schedule); recurring and manual jobs
        // carry their own map. There is no longer an environment request header.
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Create_jobAsync(body, cancellationToken: ct)).ConfigureAwait(false);
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
        // Updates carry no environment header; enablement and per-environment
        // routing travel entirely through the body's environments map.
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _gen.Update_jobAsync(job.Id, body, cancellationToken: ct)).ConfigureAwait(false);
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
            // Base IANA timezone for the cron schedule (recurring jobs only); sent
            // only when set (omitted by the jobs serializer when null, leaving the
            // server default of UTC).
            Timezone = src.Timezone,
            // Base retry-policy id; sent only when set (omitted by the jobs serializer
            // when null, leaving the server default `Default` policy, which never
            // retries).
            Retry_policy = src.RetryPolicy,
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
                kv => (object)EnvironmentToOverlay(kv.Value));
        }
        return attrs;
    }

    // ADR-056: an environment is a flat, sparse leaf-path overlay — `enabled` plus
    // only the leaves this environment overrides, with each header as a
    // `headers.<name>` entry. Unset leaves are omitted (the server resolves
    // base ⊕ overrides); the read-only `next_run_at` is never sent.
    private static Dictionary<string, object> EnvironmentToOverlay(JobEnvironment env)
    {
        var overlay = new Dictionary<string, object> { ["enabled"] = env.Enabled };
        if (env.Schedule is { } schedule) overlay["schedule"] = schedule;
        if (env.Timezone is { } timezone) overlay["timezone"] = timezone;
        if (env.RetryPolicy is { } retryPolicy) overlay["retry_policy"] = retryPolicy;
        if (env.Url is { } url) overlay["url"] = url;
        if (env.Method is { } method) overlay["method"] = method.ToWireValue();
        if (env.Timeout is { } timeout) overlay["timeout"] = timeout;
        if (env.Body is { } body) overlay["body"] = body;
        if (env.SuccessStatus is { } successStatus) overlay["success_status"] = successStatus;
        if (env.TlsVerify is { } tlsVerify) overlay["tls_verify"] = tlsVerify;
        if (env.CaCert is { } caCert) overlay["ca_cert"] = caCert;
        foreach (var (name, value) in env.Headers)
            overlay[$"headers.{name}"] = value;
        return overlay;
    }

    private static GenJobs.JobHttpConfiguration ToGenConfiguration(HttpConfig src)
    {
        return new GenJobs.JobHttpConfiguration
        {
            Method = ToGenHttpMethod(src.Method),
            Url = src.Url,
            // Headers travel as a name→value object (ADR-056).
            Headers = new Dictionary<string, string>(src.Headers),
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
        // now per-environment (JobEnvironment.NextRunAt), not a top-level field. A
        // manual job has no schedule, so Schedule stays null rather than "".
        return new Job(
            this,
            id: r.Id ?? string.Empty,
            name: a.Name ?? string.Empty,
            schedule: a.Schedule,
            configuration: ConfigFromGen(a.Configuration),
            description: a.Description,
            environments: EnvironmentsFromGen(a.Environments),
            kind: KindFromGen(a.Kind),
            type: a.Type ?? "http",
            concurrencyPolicy: a.Concurrency_policy ?? "ALLOW",
            timezone: a.Timezone,
            retryPolicy: a.Retry_policy,
            createdAt: a.Created_at,
            updatedAt: a.Updated_at,
            deletedAt: a.Deleted_at,
            version: a.Version);
    }

    // The base `kind` is a read-only, server-derived enum; map the generated value
    // to the wrapper's JobKind (null when the server omits it, e.g. unsaved jobs).
    private static JobKind? KindFromGen(GenJobs.JobKind? kind) => kind switch
    {
        GenJobs.JobKind.Recurring => JobKind.Recurring,
        GenJobs.JobKind.Manual => JobKind.Manual,
        GenJobs.JobKind.One_off => JobKind.OneOff,
        _ => null,
    };

    private static IDictionary<string, JobEnvironment> EnvironmentsFromGen(
        IDictionary<string, object>? src)
    {
        var result = new Dictionary<string, JobEnvironment>();
        if (src == null) return result;
        foreach (var (key, raw) in src)
            result[key] = EnvironmentFromOverlay(raw);
        return result;
    }

    // Parse the flat leaf-path overlay the server returns (ADR-056). The generated
    // client deserializes each environment value as a System.Text.Json.JsonElement
    // object. Header leaves arrive as `headers.<name>`, parsed on the FIRST dot so a
    // dotted header name like `X-Foo.Bar` is preserved; the read-only `next_run_at`
    // is stripped; unknown leaves are ignored for forward compatibility.
    private static JobEnvironment EnvironmentFromOverlay(object? raw)
    {
        var env = new JobEnvironment();
        if (raw is not System.Text.Json.JsonElement el
            || el.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return env;
        }
        foreach (var prop in el.EnumerateObject())
        {
            var name = prop.Name;
            var dot = name.IndexOf('.');
            if (dot >= 0)
            {
                if (name.AsSpan(0, dot).SequenceEqual("headers") && dot + 1 < name.Length)
                    env.Headers[name[(dot + 1)..]] = prop.Value.GetString() ?? string.Empty;
                continue;
            }
            switch (name)
            {
                case "enabled": env.Enabled = prop.Value.GetBoolean(); break;
                case "schedule": env.Schedule = prop.Value.GetString(); break;
                case "timezone": env.Timezone = prop.Value.GetString(); break;
                case "retry_policy": env.RetryPolicy = prop.Value.GetString(); break;
                case "url": env.Url = prop.Value.GetString(); break;
                case "method": env.Method = HttpMethodExtensions.FromWireValue(prop.Value.GetString()!); break;
                case "timeout": env.Timeout = prop.Value.GetInt32(); break;
                case "body": env.Body = prop.Value.GetString(); break;
                case "success_status": env.SuccessStatus = prop.Value.GetString(); break;
                case "tls_verify": env.TlsVerify = prop.Value.GetBoolean(); break;
                case "ca_cert": env.CaCert = prop.Value.GetString(); break;
                case "next_run_at":
                    if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Null)
                        env.NextRunAt = prop.Value.GetDateTimeOffset();
                    break;
            }
        }
        return env;
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
        // Headers arrive as a name→value object (ADR-056).
        if (src.Headers != null)
            cfg.Headers = new Dictionary<string, string>(src.Headers);
        return cfg;
    }

    internal static Run RunFromResource(GenJobs.RunResource r, RunsClient? runs = null)
    {
        var a = r.Attributes;
        // The retry-chain position is present only on RETRY runs; the generated
        // model leaves it null otherwise.
        var retry = a.Retry is { } gr ? new RunRetry(gr.Of.ToString(), gr.Attempt) : null;
        return new Run(
            id: r.Id ?? string.Empty,
            job: a.Job ?? string.Empty,
            environment: a.Environment ?? string.Empty,
            trigger: a.Trigger.ToString(),
            status: a.Status.ToString(),
            runs: runs,
            jobVersion: a.Job_version,
            rerunOf: a.Rerun_of?.ToString(),
            retry: retry,
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
