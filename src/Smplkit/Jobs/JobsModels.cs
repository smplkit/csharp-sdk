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

/// <summary>A single name/value HTTP header on the request a job performs.</summary>
/// <param name="Name">Header name (e.g. <c>"Authorization"</c>, <c>"Content-Type"</c>).</param>
/// <param name="Value">Header value, plaintext on writes. The jobs service encrypts
/// values at rest; reads return them redacted.</param>
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
    /// <summary>Headers attached to every request. Values are redacted on reads.</summary>
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
    /// <summary>Whether the job is scheduling runs. <c>false</c> pauses without deleting.</summary>
    public bool Enabled { get; set; }
    /// <summary>Job type. Only <c>"http"</c> is supported today.</summary>
    public string Type { get; set; }
    /// <summary>When the job runs: an ISO-8601 datetime (a one-off run), a 5-field
    /// cron expression evaluated in UTC (recurring), or the literal <c>"now"</c>
    /// (run once, as soon as possible). A datetime or <c>"now"</c> job disables
    /// itself after it fires.</summary>
    public string Schedule { get; set; }
    /// <summary>The HTTP request to perform when the job fires.</summary>
    public HttpConfig Configuration { get; set; }
    /// <summary>How overlapping runs are handled. <c>"ALLOW"</c> (the only value) permits them.</summary>
    public string ConcurrencyPolicy { get; set; }
    /// <summary>The next scheduled fire time. <c>null</c> once a one-off job has fired.</summary>
    public DateTimeOffset? NextRunAt { get; internal set; }
    /// <summary>When the job was created. <c>null</c> for an unsaved instance.</summary>
    public DateTimeOffset? CreatedAt { get; internal set; }
    /// <summary>When the job was last modified.</summary>
    public DateTimeOffset? UpdatedAt { get; internal set; }
    /// <summary>Soft-delete timestamp. <c>null</c> for live jobs.</summary>
    public DateTimeOffset? DeletedAt { get; internal set; }
    /// <summary>Monotonic version counter; bumped on every server-side write.</summary>
    public int? Version { get; internal set; }

    internal Job(
        JobsClient? client,
        string id,
        string name,
        string schedule,
        HttpConfig configuration,
        string? description = null,
        bool enabled = true,
        string type = "http",
        string concurrencyPolicy = "ALLOW",
        DateTimeOffset? nextRunAt = null,
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
        Enabled = enabled;
        Type = type;
        ConcurrencyPolicy = concurrencyPolicy;
        NextRunAt = nextRunAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        DeletedAt = deletedAt;
        Version = version;
    }

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

    /// <summary>Soft-delete this job.</summary>
    public Task DeleteAsync(CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Job was constructed without a client; cannot delete");
        return _client.DeleteAsync(Id, ct);
    }

    /// <summary>Copy every server-authoritative field from <paramref name="other"/> onto self.</summary>
    internal void Apply(Job other)
    {
        Id = other.Id;
        Name = other.Name;
        Description = other.Description;
        Enabled = other.Enabled;
        Type = other.Type;
        Schedule = other.Schedule;
        Configuration = other.Configuration;
        ConcurrencyPolicy = other.ConcurrencyPolicy;
        NextRunAt = other.NextRunAt;
        CreatedAt = other.CreatedAt;
        UpdatedAt = other.UpdatedAt;
        DeletedAt = other.DeletedAt;
        Version = other.Version;
    }

    /// <inheritdoc/>
    public override string ToString() => $"Job(Id={Id}, Name={Name}, Enabled={Enabled})";
}

/// <summary>A single execution of a job (read-only).</summary>
public sealed class Run
{
    /// <summary>Server-assigned UUID for this run.</summary>
    public string Id { get; }
    /// <summary>The id of the job this run belongs to.</summary>
    public string Job { get; }
    /// <summary>The job's version at the time the run executed.</summary>
    public int? JobVersion { get; }
    /// <summary>Why the run exists: <c>SCHEDULE</c>, <c>MANUAL</c> (run now), or <c>RERUN</c>.</summary>
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
    /// <summary>Snapshot of the request that was sent (header values redacted).</summary>
    public object? Request { get; }
    /// <summary>Outcome of the call (status, headers, body, ...).</summary>
    public object? Result { get; }
    /// <summary>When the run was enqueued (became <c>PENDING</c>).</summary>
    public DateTimeOffset? CreatedAt { get; }

    internal Run(
        string id,
        string job,
        string trigger,
        string status,
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
        Id = id;
        Job = job;
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
