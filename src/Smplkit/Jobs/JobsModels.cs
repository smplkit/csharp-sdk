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
    /// <summary>An automatic retry of a failed run, per the job's retry policy.</summary>
    public const string Retry = "RETRY";
    /// <summary>The job's schedule fired.</summary>
    public const string Schedule = "SCHEDULE";
}

/// <summary>How the wait between retries grows (a retry policy's backoff strategy).</summary>
public enum Backoff
{
    /// <summary>Double the wait each retry — <c>DelaySeconds</c>, then <c>2×</c>,
    /// <c>4×</c>, … — capped at <c>MaxDelaySeconds</c>.</summary>
    Exponential,
    /// <summary>Wait a constant <c>DelaySeconds</c> before every retry.</summary>
    Fixed,
}

/// <summary>A failure category a <see cref="RetryPolicy"/> can retry on.</summary>
public enum RetryReason
{
    /// <summary>The endpoint could not be reached.</summary>
    ConnectionError,
    /// <summary>Any non-success response, regardless of <see cref="RetryOn.Statuses"/>.</summary>
    NonSuccessStatus,
    /// <summary>The run did not complete in time.</summary>
    Timeout,
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
/// Which failures a <see cref="RetryPolicy"/> retries.
/// </summary>
/// <remarks>An empty <see cref="RetryOn"/> (both lists empty) retries nothing.</remarks>
public sealed class RetryOn
{
    /// <summary>Response status codes to retry when a run fails because the response
    /// did not match the job's success status (e.g. <c>[429, 503]</c> for rate-limit
    /// and unavailable). Each is a 3-digit HTTP code. Empty (the default) matches no
    /// status.</summary>
    public IList<int> Statuses { get; set; } = new List<int>();

    /// <summary>Failure categories to retry (see <see cref="RetryReason"/>). Empty
    /// (the default) matches no reason.</summary>
    public IList<RetryReason> Reasons { get; set; } = new List<RetryReason>();
}

/// <summary>
/// Where a <c>RETRY</c> run sits in its retry chain (read-only).
/// </summary>
/// <param name="Of">Id of the chain's original run — the first attempt that failed
/// and started the chain.</param>
/// <param name="Attempt">Which retry this run is — <c>1</c> for the first retry,
/// <c>2</c> for the second, and so on.</param>
public sealed record RunRetry(string Of, int Attempt);

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

    /// <summary>Optional per-environment IANA timezone override for evaluating this
    /// environment's cron <see cref="Schedule"/> (recurring jobs only).
    /// <c>null</c> (the default) inherits the job's base <see cref="Job.Timezone"/>,
    /// else UTC. When set, it must be a valid IANA zone key (e.g.
    /// <c>"America/New_York"</c>); it may be set on an environment that inherits the
    /// base schedule (it need not also override <see cref="Schedule"/>). Sent on
    /// writes only when non-<c>null</c>.</summary>
    public string? Timezone { get; set; }

    /// <summary>Optional per-environment retry-policy override — the id of a
    /// <see cref="RetryPolicy"/> (or <c>"Default"</c>). <c>null</c> (the default)
    /// inherits the job's base <see cref="Job.RetryPolicy"/>. Sent on writes only
    /// when non-<c>null</c>.</summary>
    public string? RetryPolicy { get; set; }

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
    /// <summary>The base IANA timezone the cron <see cref="Schedule"/> is evaluated in
    /// (e.g. <c>"America/New_York"</c>); <c>null</c> means UTC. The base every
    /// environment inherits unless it sets its own <see cref="JobEnvironment.Timezone"/>.
    /// The cron fires on this zone's wall clock (DST-aware) while the per-environment
    /// next-run time is still reported as a UTC instant. Only valid on a recurring
    /// (cron) job — <c>null</c> for a manual or one-off job. Set it with
    /// <see cref="SetTimezone"/>. Sent on writes only when non-<c>null</c>.</summary>
    public string? Timezone { get; set; }
    /// <summary>The base retry policy for failed runs — the id of a
    /// <see cref="RetryPolicy"/> (or the built-in <c>"Default"</c>, which never
    /// retries), overridable per environment via
    /// <see cref="JobEnvironment.RetryPolicy"/>. <c>null</c> (the default, omitted on
    /// the wire) uses the server default <c>Default</c> policy. Set it with
    /// <see cref="SetRetryPolicy(RetryPolicy, string)"/> or
    /// <see cref="SetRetryPolicy(string, string)"/>.</summary>
    public string? RetryPolicy { get; set; }
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
        string? timezone = null,
        string? retryPolicy = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? deletedAt = null,
        int? version = null)
    {
        _client = client;
        Id = id;
        Name = name;
        Schedule = schedule;
        Timezone = timezone;
        RetryPolicy = retryPolicy;
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
    /// <param name="triggers">Restrict to runs started by any of these triggers (the
    /// <see cref="RunTrigger"/> constants) — e.g. <c>[RunTrigger.Retry]</c> for
    /// automatic retries. <c>null</c> (or empty) covers every trigger.</param>
    /// <param name="lastRunOnly">When <c>true</c>, return only the last completed run
    /// per environment (in-flight runs excluded). Defaults to <c>false</c>.</param>
    /// <param name="pageSize">Maximum number of runs to return in this page.</param>
    /// <param name="after">Opaque cursor from a previous page.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The runs in this page, as a list of <see cref="Run"/>.</returns>
    public Task<IReadOnlyList<Run>> ListRunsAsync(
        string? environment = null,
        IEnumerable<string>? triggers = null,
        bool lastRunOnly = false,
        int? pageSize = null,
        string? after = null,
        CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Job was constructed without a client; cannot list runs");
        return _client.Runs.ListAsync(
            job: Id,
            environments: environment is null ? null : new[] { environment },
            triggers: triggers,
            lastRunOnly: lastRunOnly,
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
    /// vice-versa.</para>
    ///
    /// <para>Because the timezone is an integral part of a cron cadence, a
    /// <paramref name="timezone"/> may be supplied alongside the schedule; when given
    /// it sets the same scope's timezone too (equivalent to a follow-up
    /// <see cref="SetTimezone"/>). Omit it to leave the timezone untouched. For a
    /// timezone-only change, use <see cref="SetTimezone"/>.</para></summary>
    /// <param name="schedule">The new schedule.</param>
    /// <param name="timezone">Optional IANA zone key (e.g. <c>"America/New_York"</c>)
    /// to set for the same scope. Omit to leave the timezone unchanged.</param>
    /// <param name="environment">Environment key to scope the change to. Omit to set
    /// the base schedule that all environments inherit.</param>
    public void SetSchedule(string schedule, string? timezone = null, string? environment = null)
    {
        if (environment is null)
            Schedule = schedule;
        else
            EnvironmentOverride(environment).Schedule = schedule;
        if (timezone is not null)
            SetTimezone(timezone, environment);
    }

    /// <summary>Set the IANA timezone the cron schedule is evaluated in, in memory —
    /// base (<paramref name="environment"/> omitted) or per-environment. Call
    /// <see cref="SaveAsync"/> to persist.
    ///
    /// <para>The base timezone is the zone every environment inherits unless it sets
    /// its own. A per-environment override applies to that environment only; it may
    /// be set even when the environment inherits the base schedule (it need not also
    /// override <see cref="SetSchedule"/>). A timezone is only valid on a recurring
    /// (cron) job.</para></summary>
    /// <param name="timezone">The IANA zone key (e.g. <c>"America/New_York"</c>).</param>
    /// <param name="environment">Environment key to scope the change to. Omit to set
    /// the base timezone that all environments inherit.</param>
    public void SetTimezone(string timezone, string? environment = null)
    {
        if (environment is null)
            Timezone = timezone;
        else
            EnvironmentOverride(environment).Timezone = timezone;
    }

    /// <summary>Set the retry policy for failed runs, in memory — base
    /// (<paramref name="environment"/> omitted) or per-environment. Call
    /// <see cref="SaveAsync"/> to persist.
    ///
    /// <para>The base policy is the one every environment inherits unless it sets its
    /// own. A per-environment override applies to that environment only, creating the
    /// override entry if it doesn't exist yet (preserving any already-set
    /// <c>Enabled</c> / <c>Schedule</c> / <c>Timezone</c> / <c>Configuration</c> on
    /// it).</para></summary>
    /// <param name="retryPolicy">The <see cref="RetryPolicy"/> whose id is applied.</param>
    /// <param name="environment">Environment key to scope the change to. Omit to set
    /// the base policy that all environments inherit.</param>
    public void SetRetryPolicy(RetryPolicy retryPolicy, string? environment = null)
        => SetRetryPolicy(retryPolicy.Id, environment);

    /// <summary>Set the retry policy for failed runs, in memory — base
    /// (<paramref name="environment"/> omitted) or per-environment. Call
    /// <see cref="SaveAsync"/> to persist.
    ///
    /// <para>The base policy is the one every environment inherits unless it sets its
    /// own. A per-environment override applies to that environment only, creating the
    /// override entry if it doesn't exist yet (preserving any already-set
    /// <c>Enabled</c> / <c>Schedule</c> / <c>Timezone</c> / <c>Configuration</c> on
    /// it).</para></summary>
    /// <param name="retryPolicyId">The id of a <see cref="RetryPolicy"/>, or the
    /// built-in <c>"Default"</c> (never-retry) policy.</param>
    /// <param name="environment">Environment key to scope the change to. Omit to set
    /// the base policy that all environments inherit.</param>
    public void SetRetryPolicy(string retryPolicyId, string? environment = null)
    {
        if (environment is null)
            RetryPolicy = retryPolicyId;
        else
            EnvironmentOverride(environment).RetryPolicy = retryPolicyId;
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
        Timezone = other.Timezone;
        RetryPolicy = other.RetryPolicy;
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
    /// <summary>Why the run exists: <c>SCHEDULE</c>, <c>MANUAL</c> (run now),
    /// <c>RERUN</c>, or <c>RETRY</c> (an automatic retry of a failed run). A raw
    /// string; compare it against the <see cref="RunTrigger"/> constants.</summary>
    public string Trigger { get; }
    /// <summary>The source run's id; set only when <see cref="Trigger"/> is <c>RERUN</c>.</summary>
    public string? RerunOf { get; }
    /// <summary>The run's position in its retry chain; populated only when
    /// <see cref="Trigger"/> is <c>RETRY</c>, otherwise <c>null</c>.</summary>
    public RunRetry? Retry { get; }
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
        RunRetry? retry = null,
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
        Retry = retry;
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

/// <summary>
/// A named, reusable retry policy. Mutate fields, then call <see cref="SaveAsync"/>.
/// </summary>
/// <remarks>Retry policies are account-global — never environment-scoped. Reference
/// one from a job's retry policy via
/// <see cref="Job.SetRetryPolicy(RetryPolicy, string)"/> or
/// <see cref="JobsClient.NewRecurringJob"/>.</remarks>
public sealed class RetryPolicy
{
    private readonly RetryPoliciesClient? _client;

    /// <summary>Caller-supplied unique identifier for the policy (the resource
    /// <c>id</c>). Unique within the account and immutable.</summary>
    public string Id { get; internal set; }
    /// <summary>Human-readable name for the policy.</summary>
    public string Name { get; set; }
    /// <summary>How many times a failed run is retried after the initial attempt —
    /// <c>3</c> means up to 4 attempts total. <c>0</c> disables retries. Maximum 10.</summary>
    public int MaxRetries { get; set; }
    /// <summary>How the wait between retries grows (see <see cref="Backoff"/>).</summary>
    public Backoff Backoff { get; set; }
    /// <summary>The wait before a retry, in seconds — the constant wait for
    /// <see cref="Backoff.Fixed"/>, or the base that doubles each retry for
    /// <see cref="Backoff.Exponential"/>.</summary>
    public int DelaySeconds { get; set; }
    /// <summary>Ceiling on the wait between retries, for <see cref="Backoff.Exponential"/>
    /// backoff only. <c>null</c> (the default) leaves it uncapped; omit it for
    /// <see cref="Backoff.Fixed"/> backoff. Sent on writes only when set.</summary>
    public int? MaxDelaySeconds { get; set; }
    /// <summary>Which failures to retry (see <see cref="RetryOn"/>). Defaults to an
    /// empty <see cref="RetryOn"/>, which retries nothing.</summary>
    public RetryOn RetryOn { get; set; } = new RetryOn();
    /// <summary>When the policy was created. <c>null</c> for an unsaved instance.</summary>
    public DateTimeOffset? CreatedAt { get; internal set; }
    /// <summary>When the policy was last modified.</summary>
    public DateTimeOffset? UpdatedAt { get; internal set; }
    /// <summary>When the policy was deleted; <c>null</c> for live policies.</summary>
    public DateTimeOffset? DeletedAt { get; internal set; }
    /// <summary>Monotonic version counter; bumped on every server-side write.</summary>
    public int? Version { get; internal set; }

    internal RetryPolicy(
        RetryPoliciesClient? client,
        string id,
        string name,
        int maxRetries,
        Backoff backoff,
        int delaySeconds,
        int? maxDelaySeconds = null,
        RetryOn? retryOn = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? deletedAt = null,
        int? version = null)
    {
        _client = client;
        Id = id;
        Name = name;
        MaxRetries = maxRetries;
        Backoff = backoff;
        DelaySeconds = delaySeconds;
        MaxDelaySeconds = maxDelaySeconds;
        RetryOn = retryOn ?? new RetryOn();
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        DeletedAt = deletedAt;
        Version = version;
    }

    /// <summary>Create this policy, or full-replace it if it already exists.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("RetryPolicy was constructed without a client; cannot save");
        var other = CreatedAt is null
            ? await _client.CreateAsync(this, ct).ConfigureAwait(false)
            : await _client.UpdateAsync(this, ct).ConfigureAwait(false);
        Apply(other);
    }

    /// <summary>Delete this policy.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("RetryPolicy was constructed without a client; cannot delete");
        return _client.DeleteAsync(Id, ct);
    }

    /// <summary>Copy every server-authoritative field from <paramref name="other"/> onto self.</summary>
    internal void Apply(RetryPolicy other)
    {
        Id = other.Id;
        Name = other.Name;
        MaxRetries = other.MaxRetries;
        Backoff = other.Backoff;
        DelaySeconds = other.DelaySeconds;
        MaxDelaySeconds = other.MaxDelaySeconds;
        RetryOn = other.RetryOn;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
        DeletedAt = other.DeletedAt;
        Version = other.Version;
    }

    /// <inheritdoc/>
    public override string ToString() => $"RetryPolicy(Id={Id}, Name={Name}, MaxRetries={MaxRetries})";
}
