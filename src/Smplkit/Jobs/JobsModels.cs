namespace Smplkit.Jobs;

/// <summary>HTTP verb a job uses when it fires.</summary>
/// <remarks>Mirrors the jobs spec's <c>JobHttpConfigurationMethod</c> enum.</remarks>
public enum HttpMethod
{
    /// <summary><c>DELETE</c>.</summary>
    Delete,
    /// <summary><c>GET</c>.</summary>
    Get,
    /// <summary><c>PATCH</c>.</summary>
    Patch,
    /// <summary><c>POST</c>.</summary>
    Post,
    /// <summary><c>PUT</c>.</summary>
    Put,
}

/// <summary>Wire-value conversions for <see cref="HttpMethod"/>.</summary>
public static class HttpMethodExtensions
{
    /// <summary>Returns the uppercase wire slug — e.g. <c>"POST"</c>.</summary>
    public static string ToWireValue(this HttpMethod method) => method switch
    {
        HttpMethod.Delete => "DELETE",
        HttpMethod.Get => "GET",
        HttpMethod.Patch => "PATCH",
        HttpMethod.Post => "POST",
        HttpMethod.Put => "PUT",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    /// <summary>Parse a wire-format method slug. Unknown values default to <see cref="HttpMethod.Post"/>.</summary>
    public static HttpMethod FromWireValue(string value) => value?.ToUpperInvariant() switch
    {
        "DELETE" => HttpMethod.Delete,
        "GET" => HttpMethod.Get,
        "PATCH" => HttpMethod.Patch,
        "PUT" => HttpMethod.Put,
        _ => HttpMethod.Post,
    };
}

/// <summary>How a job runs, derived from its schedule (read-only).</summary>
public enum JobKind
{
    /// <summary>A cron schedule — fires on a repeating cadence.</summary>
    Recurring,
    /// <summary>No schedule — never auto-fires; runs only when triggered.</summary>
    Manual,
    /// <summary>A <c>now</c> or datetime schedule — runs a single time, then is spent.</summary>
    OneOff,
}

/// <summary>Wire-value conversion for <see cref="JobKind"/>.</summary>
public static class JobKindExtensions
{
    /// <summary>Returns the wire slug — e.g. <c>"one_off"</c>.</summary>
    public static string ToWireValue(this JobKind kind) => kind switch
    {
        JobKind.Recurring => "recurring",
        JobKind.Manual => "manual",
        JobKind.OneOff => "one_off",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>Known values of <see cref="Run.Trigger"/> — what started a run (read-only).</summary>
/// <remarks><see cref="Run.Trigger"/> is a raw string; compare it against these constants.</remarks>
public static class RunTrigger
{
    /// <summary>A run-now / trigger call started it on demand.</summary>
    public const string Manual = "MANUAL";
    /// <summary>It repeats an earlier run.</summary>
    public const string Rerun = "RERUN";
    /// <summary>The job's schedule fired.</summary>
    public const string Schedule = "SCHEDULE";
}

/// <summary>A single name/value HTTP header on the request a job performs.</summary>
/// <param name="Name">Header name (e.g. <c>"Authorization"</c>, <c>"Content-Type"</c>).</param>
/// <param name="Value">Header value. Returned in plaintext on reads, so a
/// get-mutate-put round-trip preserves it without re-entering secrets.</param>
public sealed record HttpHeader(string Name, string Value);

/// <summary>
/// The HTTP request a job performs when it fires (the <c>http</c> configuration).
/// </summary>
public sealed class HttpConfig
{
    /// <summary>HTTP verb used when the job fires. Defaults to <see cref="HttpMethod.Post"/>.</summary>
    public HttpMethod Method { get; set; } = HttpMethod.Post;
    /// <summary>Destination URL the job requests on each run.</summary>
    public required string Url { get; set; }
    /// <summary>Headers attached to every request. Values come back in plaintext
    /// on reads, so a fetched job round-trips through <see cref="Job.SaveAsync"/>
    /// without re-entering secrets.</summary>
    public IList<HttpHeader> Headers { get; set; } = new List<HttpHeader>();
    /// <summary>Request body sent on each run. <c>null</c> (the default) sends an
    /// empty body, suitable for a connectivity ping. Sent verbatim — pair with a
    /// matching <c>Content-Type</c> header.</summary>
    public string? Body { get; set; }
    /// <summary>Status the destination must return for the run to count as success —
    /// either an exact code (<c>"200"</c>, <c>"204"</c>) or a status class
    /// (<c>"2xx"</c>, <c>"4xx"</c>). Defaults to <c>"2xx"</c>.</summary>
    public string SuccessStatus { get; set; } = "2xx";
    /// <summary>Per-run timeout in seconds. A run that does not complete within this
    /// many seconds fails with reason <c>TIMEOUT</c>. Defaults to 30; bounded by
    /// your plan's maximum timeout.</summary>
    public int Timeout { get; set; } = 30;
    /// <summary>Whether to verify the destination's TLS certificate chain. Defaults
    /// to <c>true</c>; flip to <c>false</c> only for short-lived testing against an
    /// untrusted certificate. Prefer pinning the CA via <see cref="CaCert"/>.</summary>
    public bool TlsVerify { get; set; } = true;
    /// <summary>Optional PEM-encoded certificate (or bundle) trusted in addition to
    /// the system CA store. Ignored when <see cref="TlsVerify"/> is <c>false</c>.
    /// <c>null</c> (the default) means "use system CAs only".</summary>
    public string? CaCert { get; set; }
}

/// <summary>
/// Per-environment enablement, schedule, and configuration override for a job.
///
/// <para>A job runs in a given environment only when that environment has an entry
/// in <see cref="Job.Environments"/> with <see cref="Enabled"/> set to <c>true</c>
/// (scheduled there for a recurring job, triggerable there for a manual one); an
/// environment with no entry (or <see cref="Enabled"/> = <c>false</c>) is disabled
/// there.</para>
/// </summary>
public sealed class JobEnvironment
{
    /// <summary>Whether the job is enabled in this environment. Defaults to <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Optional per-environment schedule override — a 5-field cron
    /// expression evaluated in UTC (e.g. <c>"0 3 * * *"</c>) that varies the cadence
    /// for this environment only. <c>null</c> (the default) inherits the job's base
    /// <see cref="Job.Schedule"/>. Allowed only on a recurring (cron) job; it cannot
    /// turn a one-off job recurring or vice-versa.</summary>
    public string? Schedule { get; set; }

    /// <summary>Optional per-environment request configuration that fully replaces
    /// the job's base <see cref="Job.Configuration"/> for this environment.
    /// <c>null</c> (the default) inherits the base configuration. As with the base
    /// configuration, header values are returned in plaintext on reads, so a
    /// get-mutate-put round-trip preserves them without re-entering secrets.</summary>
    public HttpConfig? Configuration { get; set; }

    /// <summary>Read-only. The next scheduled fire time in this environment.
    /// <c>null</c> when the environment is not enabled, or once a one-off run has
    /// fired. Server-derived; the SDK never sends it.</summary>
    public DateTimeOffset? NextRunAt { get; internal set; }
}

/// <summary>A job definition. Mutate fields, then call <see cref="SaveAsync"/>.</summary>
public sealed class Job
{
    private readonly JobsClient? _client;

    /// <summary>Caller-supplied unique identifier for the job (the resource <c>id</c>).</summary>
    public string Id { get; internal set; }
    /// <summary>Human-readable name for the job.</summary>
    public string Name { get; set; }
    /// <summary>Free-text description. <c>null</c> when unset.</summary>
    public string? Description { get; set; }
    /// <summary>Per-environment overrides keyed by environment key (e.g.
    /// <c>"production"</c>, <c>"development"</c>). A job is enabled in an
    /// environment only when <c>Environments[env].Enabled</c> is <c>true</c>; each
    /// entry may carry an optional <see cref="HttpConfig"/> override that replaces
    /// the base <see cref="Configuration"/> for that environment (omit it to
    /// inherit the base). Set enablement with <see cref="SetEnabled"/> and the
    /// per-environment configuration with <see cref="SetConfiguration"/>; every
    /// referenced environment must exist and be managed for the account.</summary>
    public IDictionary<string, JobEnvironment> Environments { get; set; }
    /// <summary>Read-only roll-up: <c>true</c> when the job is enabled in at least
    /// one environment. Enablement is per-environment — set it with
    /// <see cref="SetEnabled"/> / <see cref="Environments"/>, not here. Derived from
    /// the environment map; the SDK never writes it.</summary>
    public bool Enabled => Environments.Values.Any(e => e.Enabled);
    /// <summary>Read-only server-derived kind (see <see cref="JobKind"/>): recurring
    /// for a cron schedule, manual for no schedule, or one-off for a datetime /
    /// <c>"now"</c> schedule. Derived server-side from <see cref="Schedule"/>;
    /// <c>null</c> on an unsaved instance. Query it with <see cref="IsRecurring"/> /
    /// <see cref="IsManual"/> / <see cref="IsOneOff"/>.</summary>
    public JobKind? Kind { get; internal set; }
    /// <summary>Job type. Only <c>"http"</c> is supported today.</summary>
    public string Type { get; set; }
    /// <summary>The base schedule every environment inherits unless it overrides it:
    /// a 5-field cron expression evaluated in UTC (recurring), an ISO-8601 datetime
    /// (a one-off run at that instant), or the literal <c>"now"</c> (run once, as
    /// soon as possible). <c>null</c> for a manual job, which never auto-fires. A
    /// datetime or <c>"now"</c> job disables itself after it fires. Set it with
    /// <see cref="SetSchedule"/>.</summary>
    public string? Schedule { get; set; }
    /// <summary>The base HTTP request to perform when the job fires. A
    /// per-environment override in <see cref="Environments"/> replaces this for
    /// that environment.</summary>
    public HttpConfig Configuration { get; set; }
    /// <summary>How overlapping runs are handled. <c>"ALLOW"</c> (the only value) permits them.</summary>
    public string ConcurrencyPolicy { get; set; }
    /// <summary>When the job was created. <c>null</c> for an unsaved instance.</summary>
    public DateTimeOffset? CreatedAt { get; internal set; }
    /// <summary>When the job was last modified.</summary>
    public DateTimeOffset? UpdatedAt { get; internal set; }
    /// <summary>When the job was deleted; <c>null</c> for live jobs.</summary>
    public DateTimeOffset? DeletedAt { get; internal set; }
    /// <summary>Monotonic version counter; bumped on every server-side write.</summary>
    public int? Version { get; internal set; }

    // Creation-time only: the environment a one-off job is born in, sent as the
    // X-Smplkit-Environment header by JobsClient.CreateAsync. Ignored for a
    // recurring job, whose environments come from the Environments map. Set by
    // JobsClient.New (explicit environment, else the client's configured one).
    internal string? BirthEnvironment { get; set; }

    internal Job(
        JobsClient? client,
        string id,
        string name,
        string? schedule,
        HttpConfig configuration,
        string? description = null,
        IDictionary<string, JobEnvironment>? environments = null,
        JobKind? kind = null,
        string type = "http",
        string concurrencyPolicy = "ALLOW",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? deletedAt = null,
        int? version = null)
    {
        _client = client;
        Id = id;
        Name = name;
        Schedule = schedule;
        Configuration = configuration;
        Description = description;
        Environments = environments ?? new Dictionary<string, JobEnvironment>();
        Kind = kind;
        Type = type;
        ConcurrencyPolicy = concurrencyPolicy;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        DeletedAt = deletedAt;
        Version = version;
    }

    /// <summary>Whether this is a recurring (cron-scheduled) job.</summary>
    public bool IsRecurring() => Kind == JobKind.Recurring;

    /// <summary>Whether this is a manual job — no schedule; runs only when triggered.</summary>
    public bool IsManual() => Kind == JobKind.Manual;

    /// <summary>Whether this is a one-off job — a single <c>"now"</c> / datetime run.</summary>
    public bool IsOneOff() => Kind == JobKind.OneOff;

    /// <summary>Create this job, or full-replace it if it already exists.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Job was constructed without a client; cannot save");
        var other = CreatedAt is null
            ? await _client.CreateAsync(this, ct).ConfigureAwait(false)
            : await _client.UpdateAsync(this, ct).ConfigureAwait(false);
        Apply(other);
    }

    /// <summary>Delete this job.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Job was constructed without a client; cannot delete");
        return _client.DeleteAsync(Id, ct);
    }

    /// <summary>Trigger one immediate, manual run of this job (a <c>MANUAL</c> run).</summary>
    /// <param name="environment">Environment the run executes in. Defaults to the
    /// client's configured environment; when the job is enabled in exactly one
    /// environment that environment is used.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The <see cref="Run"/> that was started.</returns>
    public Task<Run> TriggerAsync(string? environment = null, CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Job was constructed without a client; cannot trigger a run");
        return _client.RunAsync(Id, environment, ct);
    }

    /// <summary>List this job's run history, most recent first.</summary>
    /// <param name="environment">Restrict to runs stamped with this environment.
    /// <c>null</c> covers every environment you can access.</param>
    /// <param name="pageSize">Maximum number of runs to return in this page.</param>
    /// <param name="after">Opaque cursor from a previous page.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The runs in this page, as a list of <see cref="Run"/>.</returns>
    public Task<IReadOnlyList<Run>> ListRunsAsync(
        string? environment = null, int? pageSize = null, string? after = null, CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Job was constructed without a client; cannot list runs");
        return _client.Runs.ListAsync(
            job: Id,
            environments: environment is null ? null : new[] { environment },
            pageSize: pageSize,
            after: after,
            ct: ct);
    }

    /// <summary>
    /// Return the override for <paramref name="environment"/>, creating an empty
    /// one if absent so the per-environment mutators preserve an existing entry's
    /// other field when only one of <c>Enabled</c> / <c>Configuration</c> is set.
    /// </summary>
    private JobEnvironment EnvironmentOverride(string environment)
    {
        if (!Environments.TryGetValue(environment, out var env))
        {
            env = new JobEnvironment();
            Environments[environment] = env;
        }
        return env;
    }

    /// <summary>Enable or disable the job in a single environment (in memory).
    /// Call <see cref="SaveAsync"/> to persist.</summary>
    /// <param name="enabled">Whether the job fires in <paramref name="environment"/>.</param>
    /// <param name="environment">Environment key to scope the change to.</param>
    public void SetEnabled(bool enabled, string environment)
        => EnvironmentOverride(environment).Enabled = enabled;

    /// <summary>Whether the job is enabled.</summary>
    /// <param name="environment">With <c>null</c> (the default), returns the
    /// roll-up — <c>true</c> when the job is enabled in at least one environment.
    /// With an environment, returns whether the job is enabled in that specific
    /// environment.</param>
    /// <returns>The roll-up or the per-environment enablement.</returns>
    public bool IsEnabled(string? environment = null)
    {
        if (environment is null)
            return Enabled;
        return Environments.TryGetValue(environment, out var env) && env.Enabled;
    }

    /// <summary>Set the job's configuration in memory — base
    /// (<paramref name="environment"/> omitted) or per-environment. Call
    /// <see cref="SaveAsync"/> to persist.</summary>
    /// <param name="configuration">The <see cref="HttpConfig"/> to apply.</param>
    /// <param name="environment">Environment key to scope the change to. Omit to
    /// set the base configuration that all environments inherit.</param>
    public void SetConfiguration(HttpConfig configuration, string? environment = null)
    {
        if (environment is null)
            Configuration = configuration;
        else
            EnvironmentOverride(environment).Configuration = configuration;
    }

    /// <summary>The job's effective configuration.</summary>
    /// <param name="environment">With <c>null</c> (the default), returns the base
    /// configuration. With an environment, returns that environment's override when
    /// it has one, else the base configuration — the request the job actually sends
    /// when it fires in that environment.</param>
    /// <returns>The base or per-environment configuration.</returns>
    public HttpConfig GetConfiguration(string? environment = null)
    {
        if (environment is not null
            && Environments.TryGetValue(environment, out var env)
            && env.Configuration is not null)
        {
            return env.Configuration;
        }
        return Configuration;
    }

    /// <summary>Set the job's schedule in memory — base
    /// (<paramref name="environment"/> omitted) or per-environment. Call
    /// <see cref="SaveAsync"/> to persist.
    ///
    /// <para>The base schedule is the cron / datetime / <c>"now"</c> schedule every
    /// environment inherits. A per-environment override is a 5-field cron expression
    /// (UTC) that varies the cadence for that environment only; it is allowed only on
    /// a recurring (cron) job and cannot turn a one-off job recurring or
    /// vice-versa.</para></summary>
    /// <param name="schedule">The new schedule.</param>
    /// <param name="environment">Environment key to scope the change to. Omit to set
    /// the base schedule that all environments inherit.</param>
    public void SetSchedule(string schedule, string? environment = null)
    {
        if (environment is null)
            Schedule = schedule;
        else
            EnvironmentOverride(environment).Schedule = schedule;
    }

    /// <summary>Copy every server-authoritative field from <paramref name="other"/> onto self.</summary>
    internal void Apply(Job other)
    {
        Id = other.Id;
        Name = other.Name;
        Description = other.Description;
        // Enabled is a derived roll-up over Environments (no field to copy).
        Environments = other.Environments;
        Kind = other.Kind;
        Type = other.Type;
        Schedule = other.Schedule;
        Configuration = other.Configuration;
        ConcurrencyPolicy = other.ConcurrencyPolicy;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
        DeletedAt = other.DeletedAt;
        Version = other.Version;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var enabledIn = string.Join(", ", Environments
            .Where(kv => kv.Value.Enabled)
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal));
        return $"Job(Id={Id}, Name={Name}, EnabledIn=[{enabledIn}])";
    }
}

/// <summary>A single execution of a job (read-only) with <see cref="RerunAsync"/> / <see cref="CancelAsync"/>.</summary>
public sealed class Run
{
    private readonly RunsClient? _runs;

    /// <summary>Server-assigned UUID for this run.</summary>
    public string Id { get; }
    /// <summary>The id of the job this run belongs to.</summary>
    public string Job { get; }
    /// <summary>The environment this run executed in. A scheduled run inherits the
    /// firing job-environment; a manual run uses the environment named on the
    /// trigger; a rerun copies its source run's environment.</summary>
    public string Environment { get; }
    /// <summary>The job's version at the time the run executed.</summary>
    public int? JobVersion { get; }
    /// <summary>Why the run exists: <c>SCHEDULE</c>, <c>MANUAL</c> (run now), or
    /// <c>RERUN</c>. A raw string; compare it against the <see cref="RunTrigger"/> constants.</summary>
    public string Trigger { get; }
    /// <summary>The source run's id; set only when <see cref="Trigger"/> is <c>RERUN</c>.</summary>
    public string? RerunOf { get; }
    /// <summary>The intended fire time for a scheduled run; <c>null</c> for manual / rerun runs.</summary>
    public DateTimeOffset? ScheduledFor { get; }
    /// <summary>Lifecycle state of the run.</summary>
    public string Status { get; }
    /// <summary>When execution started.</summary>
    public DateTimeOffset? StartedAt { get; }
    /// <summary>When execution finished.</summary>
    public DateTimeOffset? FinishedAt { get; }
    /// <summary>Milliseconds the run waited as <c>PENDING</c> before starting.</summary>
    public int? PendingDurationMs { get; }
    /// <summary>Milliseconds the run spent executing.</summary>
    public int? RunDurationMs { get; }
    /// <summary>Milliseconds from enqueue to finish.</summary>
    public int? TotalDurationMs { get; }
    /// <summary>Why a <c>FAILED</c> run failed; <c>null</c> otherwise.</summary>
    public string? FailureReason { get; }
    /// <summary>Free-text failure detail, if any.</summary>
    public string? Error { get; }
    /// <summary>Snapshot of the request that was sent, for forensics. Header
    /// values are redacted in this run snapshot.</summary>
    public object? Request { get; }
    /// <summary>Outcome of the call (status, headers, body, ...).</summary>
    public object? Result { get; }
    /// <summary>When the run was enqueued (became <c>PENDING</c>).</summary>
    public DateTimeOffset? CreatedAt { get; }

    internal Run(
        string id,
        string job,
        string environment,
        string trigger,
        string status,
        RunsClient? runs = null,
        int? jobVersion = null,
        string? rerunOf = null,
        DateTimeOffset? scheduledFor = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null,
        int? pendingDurationMs = null,
        int? runDurationMs = null,
        int? totalDurationMs = null,
        string? failureReason = null,
        string? error = null,
        object? request = null,
        object? result = null,
        DateTimeOffset? createdAt = null)
    {
        _runs = runs;
        Id = id;
        Job = job;
        Environment = environment;
        Trigger = trigger;
        Status = status;
        JobVersion = jobVersion;
        RerunOf = rerunOf;
        ScheduledFor = scheduledFor;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        PendingDurationMs = pendingDurationMs;
        RunDurationMs = runDurationMs;
        TotalDurationMs = totalDurationMs;
        FailureReason = failureReason;
        Error = error;
        Request = request;
        Result = result;
        CreatedAt = createdAt;
    }

    /// <summary>Start a new run that repeats this one (a <c>RERUN</c>), in the same environment.</summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The new <see cref="Run"/>, with <see cref="RerunOf"/> set to this run's id.</returns>
    public Task<Run> RerunAsync(CancellationToken ct = default)
    {
        if (_runs is null)
            throw new InvalidOperationException("Run was constructed without a client; cannot rerun");
        return _runs.RerunAsync(Id, ct);
    }

    /// <summary>Cancel this run if it has not finished yet.</summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The updated <see cref="Run"/> reflecting the cancellation.</returns>
    public Task<Run> CancelAsync(CancellationToken ct = default)
    {
        if (_runs is null)
            throw new InvalidOperationException("Run was constructed without a client; cannot cancel");
        return _runs.CancelAsync(Id, ct);
    }

    /// <inheritdoc/>
    public override string ToString() => $"Run(Id={Id}, Job={Job}, Status={Status})";
}

/// <summary>Current-period usage against the account's plan allotments (read-only).</summary>
public sealed class Usage
{
    /// <summary>The usage period this report covers, as <c>YYYY-MM</c> (UTC).</summary>
    public string Period { get; }
    /// <summary>Runs metered so far this period.</summary>
    public int RunsUsed { get; }
    /// <summary>Runs included in the plan this period (<c>-1</c> means unlimited).</summary>
    public int RunsIncluded { get; }
    /// <summary>Number of currently-enabled jobs.</summary>
    public int ActiveJobs { get; }
    /// <summary>Maximum enabled jobs the plan allows (<c>-1</c> means unlimited).</summary>
    public int ActiveJobsLimit { get; }

    internal Usage(string period, int runsUsed, int runsIncluded, int activeJobs, int activeJobsLimit)
    {
        Period = period;
        RunsUsed = runsUsed;
        RunsIncluded = runsIncluded;
        ActiveJobs = activeJobs;
        ActiveJobsLimit = activeJobsLimit;
    }

    /// <inheritdoc/>
    public override string ToString() => $"Usage(Period={Period}, RunsUsed={RunsUsed}/{RunsIncluded})";
}
