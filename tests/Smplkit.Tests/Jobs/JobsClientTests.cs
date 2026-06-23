using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Jobs;
using Smplkit.Tests.Helpers;
using Xunit;
using GenJobs = Smplkit.Internal.Generated.Jobs;
using HttpMethod = Smplkit.Jobs.HttpMethod;

namespace Smplkit.Tests.Jobs;

/// <summary>
/// Tests for the management-plane Smpl Jobs wrapper (<c>mgmt.Jobs</c>) with
/// per-environment scoping.
///
/// <para>Stubs the jobs service via <see cref="MockHttpMessageHandler"/>; no real
/// network. Coverage on the wrapper must reach 100% to satisfy the SDK CI gate.
/// Exercises the active-record API: <c>mgmt.Jobs.NewRecurringJob(...)</c> /
/// <c>NewManualJob(...)</c> / <c>Schedule(...)</c> → mutate →
/// <c>SaveAsync</c> / <c>DeleteAsync</c>, plus per-environment enablement /
/// configuration overrides, the environment header on create/update/run, the
/// <c>filter[environment]</c> resolution on run reads, run history, run
/// active-record actions, and usage.</para>
/// </summary>
public class JobsClientTests
{
    private const string JobId = "my-job";
    private const string RunId = "8f2b1c4a-0000-4a1b-9c3d-1e2f3a4b5c6d";

    // environments map fixture (ADR-056 flat overlay): production enabled with NO
    // request-leaf override but per-environment schedule / timezone overrides + a
    // next_run_at; development disabled WITH flat url + header-leaf overrides (and no
    // schedule / next_run_at). The header leaf `headers.X-Env` exercises the
    // first-dot parse.
    private const string EnvsJson =
        "{\"production\":{\"enabled\":true,\"schedule\":\"0 3 * * *\","
        + "\"timezone\":\"Europe/London\","
        + "\"next_run_at\":\"2026-06-19T03:00:00Z\"},"
        + "\"development\":{\"enabled\":false,\"url\":\"https://dev.example.com/hook\","
        + "\"headers.X-Env\":\"dev\"}}";

    private static (GenJobs.JobsClient gen, MockHttpMessageHandler mock) MakeGen(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mock = new MockHttpMessageHandler(handler);
        var http = new HttpClient(mock);
        var gen = new GenJobs.JobsClient("https://jobs.example.com", http) { ReadResponseAsString = true };
        return (gen, mock);
    }

    private static JobsClient MakeJobs(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler, string? environment = null)
    {
        var (gen, _) = MakeGen(handler);
        return new JobsClient(gen, environment);
    }

    private static StringContent JsonApi(string body) =>
        new(body, Encoding.UTF8, "application/vnd.api+json");

    private static string JobResource(
        string id = JobId, string name = "My Job", int version = 1,
        string kind = "recurring", bool created = true, string environmentsJson = "{}")
    {
        // The top-level `enabled` and `next_run_at` attributes were removed from the
        // wire: enablement is a derived roll-up over `environments`, and the next
        // fire time is now per-environment (environments[<env>].next_run_at). The
        // server-derived `kind` (recurring / manual / one_off) replaces `recurring`.
        var ts = created ? "\"2026-06-04T00:00:00Z\"" : "null";
        return "{\"id\":\"" + id + "\",\"type\":\"job\",\"attributes\":{"
            + "\"name\":\"" + name + "\",\"description\":\"does a thing\","
            + "\"type\":\"http\","
            + "\"schedule\":\"0 * * * *\","
            + "\"timezone\":\"America/New_York\","
            + "\"configuration\":{\"method\":\"POST\",\"url\":\"https://api.example.com/hook\","
            + "\"headers\":{\"X-Api-Key\":\"secret\"},"
            + "\"body\":\"{}\",\"success_status\":\"2xx\",\"timeout\":30,"
            + "\"tls_verify\":true,\"ca_cert\":null},"
            + "\"environments\":" + environmentsJson + ","
            + "\"concurrency_policy\":\"ALLOW\","
            + "\"kind\":\"" + kind + "\","
            + "\"created_at\":" + ts + ",\"updated_at\":" + ts + ",\"deleted_at\":null,"
            + "\"version\":" + version + "}}";
    }

    private static string RunResource(
        string id = RunId, string status = "SUCCEEDED", string trigger = "SCHEDULE",
        string? rerunOf = null, string environment = "production", string retry = "null")
    {
        var ro = rerunOf is null ? "null" : "\"" + rerunOf + "\"";
        return "{\"id\":\"" + id + "\",\"type\":\"run\",\"attributes\":{"
            + "\"job\":\"" + JobId + "\",\"job_version\":1,\"environment\":\"" + environment + "\","
            + "\"trigger\":\"" + trigger + "\",\"rerun_of\":" + ro + ",\"retry\":" + retry + ","
            + "\"scheduled_for\":\"2026-06-05T00:00:00Z\",\"status\":\"" + status + "\","
            + "\"started_at\":\"2026-06-05T00:00:00.1Z\",\"finished_at\":\"2026-06-05T00:00:00.4Z\","
            + "\"pending_duration_ms\":100,\"run_duration_ms\":300,\"total_duration_ms\":400,"
            + "\"failure_reason\":null,\"error\":null,"
            + "\"request\":{\"method\":\"POST\",\"url\":\"https://api.example.com/hook\"},"
            + "\"result\":{\"status\":200},\"created_at\":\"2026-06-05T00:00:00Z\"}}";
    }

    private const string UsageBody =
        "{\"data\":{\"id\":\"current\",\"type\":\"usage\",\"attributes\":{"
        + "\"period\":\"2026-06\",\"runs_used\":12,\"runs_included\":3000,"
        + "\"active_jobs\":2,\"active_jobs_limit\":10}}}";

    private const string RetryPolicyId = "showcase-retry";

    private static string RetryPolicyJson(
        string id = RetryPolicyId, string name = "Retry on server errors",
        int maxRetries = 5, string backoff = "exponential", int delaySeconds = 2,
        string maxDelaySeconds = "60", string retryOnTimeout = "true",
        string retryOnConnectionError = "true", string retryStatuses = "[\"429\",\"5xx\"]",
        string retryStatusesExcept = "[\"501\"]", string createdAt = "\"2026-06-04T00:00:00Z\"",
        string updatedAt = "\"2026-06-04T00:00:00Z\"", string deletedAt = "null", int version = 1)
    {
        return "{\"id\":\"" + id + "\",\"type\":\"retry_policy\",\"attributes\":{"
            + "\"name\":\"" + name + "\",\"max_retries\":" + maxRetries + ","
            + "\"backoff\":\"" + backoff + "\",\"delay_seconds\":" + delaySeconds + ","
            + "\"max_delay_seconds\":" + maxDelaySeconds + ","
            + "\"retry_on_timeout\":" + retryOnTimeout + ","
            + "\"retry_on_connection_error\":" + retryOnConnectionError + ","
            + "\"retry_statuses\":" + retryStatuses + ","
            + "\"retry_statuses_except\":" + retryStatusesExcept + ","
            + "\"created_at\":" + createdAt + ",\"updated_at\":" + updatedAt
            + ",\"deleted_at\":" + deletedAt + ",\"version\":" + version + "}}";
    }

    private static HttpConfig Cfg() => new()
    {
        Url = "https://api.example.com/hook",
        Method = HttpMethod.Post,
        Headers = new Dictionary<string, string> { ["X-Api-Key"] = "secret" },
        Body = "{}",
    };

    // ----------------------------------------------------------------------
    // Active record — New + SaveAsync (create then update)
    // ----------------------------------------------------------------------

    [Fact]
    public void New_ReturnsUnsavedJob_NoNetwork()
    {
        var calls = 0;
        var jobs = MakeJobs(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var job = jobs.NewRecurringJob(JobId, name: "My Job", schedule: "0 * * * *", configuration: Cfg());
        Assert.Equal(JobId, job.Id);
        Assert.Null(job.CreatedAt);
        Assert.False(job.Enabled);
        Assert.Empty(job.Environments);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void New_WithEnvironmentsDictionary_SeedsMap()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg(),
            environments: new Dictionary<string, JobEnvironment>
            {
                ["production"] = new JobEnvironment { Enabled = true },
            });
        Assert.True(job.Environment("production").Enabled);
    }

    [Fact]
    public async Task SaveAsync_CreatesThenUpdatesViaVersion()
    {
        var capturedMethods = new List<string>();
        string? capturedBody = null;
        var jobs = MakeJobs(async req =>
        {
            capturedMethods.Add(req.Method.Method);
            if (req.Method == System.Net.Http.HttpMethod.Post)
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonApi("{\"data\":" + JobResource(version: 1) + "}"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + JobResource(name: "renamed", version: 2) + "}"),
            };
        });

        var job = jobs.NewRecurringJob(JobId, name: "My Job", schedule: "0 * * * *", configuration: Cfg(), description: "d");
        Assert.Null(job.CreatedAt);
        await job.SaveAsync();
        Assert.Equal("POST", capturedMethods[0]);
        // Body + timeout reach the wire (the two fields jobs add over a forwarder).
        Assert.Contains("\"body\":\"{}\"", capturedBody);
        Assert.Contains("\"timeout\":30", capturedBody);
        // Base headers travel as a name→value object (ADR-056), not an array.
        Assert.Contains("\"headers\":{\"X-Api-Key\":\"secret\"}", capturedBody);
        Assert.NotNull(job.CreatedAt);
        Assert.Equal(1, job.Version);

        job.Name = "renamed";
        await job.SaveAsync();
        Assert.Equal("PUT", capturedMethods[1]);
        Assert.Equal(2, job.Version);
        Assert.Equal("renamed", job.Name);
    }

    [Fact]
    public async Task SaveAsync_WithoutClient_Throws()
    {
        var job = BuildClientlessJob();
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.SaveAsync());
    }

    [Fact]
    public async Task DeleteAsync_OnInstance_IssuesDelete()
    {
        string? method = null;
        var jobs = MakeJobs(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + JobResource() + "}"),
                });
            method = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var job = await jobs.GetAsync(JobId);
        await job.DeleteAsync();
        Assert.Equal("DELETE", method);
    }

    [Fact]
    public async Task DeleteAsync_WithoutClient_Throws()
    {
        var job = BuildClientlessJob();
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.DeleteAsync());
    }

    // ----------------------------------------------------------------------
    // Get / List / Delete / Run / Usage
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Get_Success_ReturnsClientBoundInstance()
    {
        var job = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + JobResource() + "}"),
        })).GetAsync(JobId);
        Assert.Equal(JobId, job.Id);
        Assert.Equal(HttpMethod.Post, job.Configuration.Method);
        Assert.Equal("{}", job.Configuration.Body);
        Assert.Equal(30, job.Configuration.Timeout);
        Assert.Single(job.Configuration.Headers);
        Assert.Equal("secret", job.Configuration.GetHeader("X-Api-Key"));
        Assert.Equal(JobKind.Recurring, job.Kind);
        Assert.True(job.IsRecurring());
    }

    [Fact]
    public async Task List_ReturnsRows_AndSendsFilters()
    {
        string? url = null;
        var jobs = MakeJobs(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[" + JobResource("a") + "," + JobResource("b")
                    + "],\"meta\":{\"pagination\":{\"page\":1,\"size\":50}}}"),
            });
        });
        var rows = await jobs.ListAsync(kind: JobKind.Manual, scheduled: true, name: "health", pageNumber: 1, pageSize: 10);
        Assert.Equal(2, rows.Count);
        // The dropped recurring filter is never emitted; JobKind serializes to its slug.
        Assert.DoesNotContain("filter%5Brecurring%5D", url!);
        Assert.Contains("filter%5Bkind%5D=manual", url!);
        Assert.Contains("filter%5Bscheduled%5D=true", url!);
        Assert.Contains("filter%5Bname%5D=health", url!);
        Assert.Contains("page%5Bnumber%5D=1", url!);
        Assert.Contains("page%5Bsize%5D=10", url!);
    }

    [Fact]
    public async Task List_DefaultArgs_NoFilters()
    {
        var rows = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"pagination\":{\"page\":1,\"size\":1000}}}"),
        })).ListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Delete_ById_Success()
    {
        string? method = null;
        await MakeJobs(req =>
        {
            method = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }).DeleteAsync(JobId);
        Assert.Equal("DELETE", method);
    }

    [Fact]
    public async Task Run_TriggersManualRun_ParsesEnvironment()
    {
        var run = await MakeJobs(req =>
        {
            Assert.EndsWith("/actions/run", req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RunResource(trigger: "MANUAL") + "}"),
            });
        }).RunAsync(JobId);
        Assert.Equal("MANUAL", run.Trigger);
        Assert.Equal(JobId, run.Job);
        Assert.Equal("production", run.Environment);
        Assert.Equal("SUCCEEDED", run.Status);
    }

    [Fact]
    public async Task Usage_ReturnsCounters()
    {
        var usage = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(UsageBody),
        })).UsageAsync();
        Assert.Equal("2026-06", usage.Period);
        Assert.Equal(12, usage.RunsUsed);
        Assert.Equal(3000, usage.RunsIncluded);
        Assert.Equal(2, usage.ActiveJobs);
        Assert.Equal(10, usage.ActiveJobsLimit);
    }

    // ----------------------------------------------------------------------
    // Per-environment enablement, configuration overrides, roll-up
    // ----------------------------------------------------------------------

    [Fact]
    public void Environment_Enabled_RollupDerivedFromEnvironmentMap()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        // Roll-up is a derived value over the environment map: false until at least
        // one environment is enabled.
        Assert.False(job.Enabled);

        job.Environment("production").Enabled = true;
        job.Environment("development").Enabled = false;
        Assert.True(job.Environment("production").Enabled);
        Assert.False(job.Environment("development").Enabled);
        // Roll-up is now true (production is enabled) — derived, not server-only.
        Assert.True(job.Enabled);

        // Disable the only enabled environment → roll-up flips back to false.
        job.Environment("production").Enabled = false;
        Assert.False(job.Enabled);
    }

    [Fact]
    public void Environment_Accessor_LazilyCreatesAndReturnsSameInstance()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        Assert.Empty(job.Environments);

        var prod = job.Environment("production");
        // First access creates and inserts an empty (disabled) override...
        Assert.False(prod.Enabled);
        Assert.Single(job.Environments);
        // ...and repeated access returns the same stored instance.
        Assert.Same(prod, job.Environment("production"));
        Assert.Same(prod, job.Environments["production"]);
    }

    [Fact]
    public void Environment_RequestLeafOverrides_ArePureOverrides_BaseUntouched()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var baseCfg = new HttpConfig { Url = "https://base.example.com" };
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: baseCfg);

        var prod = job.Environment("production");
        // Pure override: a leaf the environment does not set reads null (NOT the base).
        Assert.Null(prod.Url);
        Assert.Null(prod.Method);
        Assert.Null(prod.Timeout);
        Assert.Null(prod.Body);
        Assert.Null(prod.SuccessStatus);
        Assert.Null(prod.TlsVerify);
        Assert.Null(prod.CaCert);
        Assert.Null(prod.GetHeader("X-Api-Key"));

        prod.Url = "https://prod.example.com";
        prod.Method = HttpMethod.Put;
        prod.Timeout = 60;
        prod.SetHeader("Authorization", "Bearer prod");
        Assert.Equal("https://prod.example.com", job.Environment("production").Url);
        Assert.Equal("Bearer prod", job.Environment("production").GetHeader("Authorization"));
        // The base configuration is untouched by the per-environment override.
        Assert.Same(baseCfg, job.Configuration);
        Assert.Equal("https://base.example.com", job.Configuration.Url);
    }

    [Fact]
    public void BaseFields_SetByDirectAssignment_LeaveEnvironmentsEmpty()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.Schedule = "30 2 * * *";
        job.Timezone = "America/New_York";
        job.Configuration.SetHeader("X-Trace", "on");
        Assert.Equal("30 2 * * *", job.Schedule);
        Assert.Equal("America/New_York", job.Timezone);
        Assert.Equal("on", job.Configuration.GetHeader("X-Trace"));
        Assert.Empty(job.Environments);
    }

    [Fact]
    public void Environment_ScheduleAndTimezone_PerEnvironment_PreserveBaseAndReuseOverride()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.Timezone = "America/Chicago";

        // Enable first, then add schedule + timezone overrides — the same override
        // entry is reused so the enablement survives, and the base is untouched.
        job.Environment("production").Enabled = true;
        job.Environment("production").Schedule = "0 3 * * *";
        job.Environment("production").Timezone = "Europe/London";
        Assert.Equal("0 * * * *", job.Schedule);          // base schedule untouched
        Assert.Equal("America/Chicago", job.Timezone);    // base timezone untouched
        Assert.True(job.Environments["production"].Enabled);
        Assert.Equal("0 3 * * *", job.Environments["production"].Schedule);
        Assert.Equal("Europe/London", job.Environments["production"].Timezone);
    }

    [Fact]
    public async Task Get_ParsesEnvironments_WithAndWithoutOverride()
    {
        var job = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + JobResource(environmentsJson: EnvsJson) + "}"),
        })).GetAsync(JobId);

        // Roll-up + per-environment enablement.
        Assert.True(job.Enabled);
        Assert.True(job.Environments["production"].Enabled);
        Assert.False(job.Environments["development"].Enabled);

        // production: no request-leaf override → those leaves read null (pure
        // override, NOT merged from base), but it carries per-environment schedule /
        // timezone overrides and a server-derived next_run_at surfaced read-only.
        Assert.Null(job.Environments["production"].Url);
        Assert.Null(job.Environments["production"].Method);
        Assert.Equal("0 3 * * *", job.Environments["production"].Schedule);
        // Base + per-environment timezone decode from the wire.
        Assert.Equal("America/New_York", job.Timezone);
        Assert.Equal("Europe/London", job.Environments["production"].Timezone);
        Assert.Equal(
            DateTimeOffset.Parse("2026-06-19T03:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            job.Environments["production"].NextRunAt);

        // development: flat url + header-leaf overrides, no schedule override, and
        // next_run_at omitted (the environment is disabled). The base config is
        // read from the base definition, unaffected by the per-env overrides.
        Assert.Equal("https://dev.example.com/hook", job.Environments["development"].Url);
        Assert.Equal("dev", job.Environments["development"].GetHeader("X-Env"));
        Assert.Null(job.Environments["development"].Schedule);
        Assert.Null(job.Environments["development"].Timezone);
        Assert.Null(job.Environments["development"].NextRunAt);
        Assert.Equal("https://api.example.com/hook", job.Configuration.Url);
    }

    [Fact]
    public async Task SaveAsync_BuildBody_AllEnvironmentRequestLeaves_FlatOverlay()
    {
        string? capturedBody = null;
        var jobs = MakeJobs(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource() + "}"),
            };
        });
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        var prod = job.Environment("production");
        prod.Enabled = true;
        prod.Url = "https://prod.example.com/hook";
        prod.Method = HttpMethod.Put;
        prod.Timeout = 90;
        prod.Body = "{\"warm\":true}";
        prod.SuccessStatus = "200";
        prod.TlsVerify = false;
        prod.CaCert = "-----BEGIN CERTIFICATE-----\nx\n-----END CERTIFICATE-----";
        prod.SetHeader("Authorization", "Bearer prod");
        prod.SetHeader("X-Foo.Bar", "v");   // dotted header name survives the wire
        await job.SaveAsync();

        using var doc = JsonDocument.Parse(capturedBody!);
        var p = doc.RootElement.GetProperty("data").GetProperty("attributes")
            .GetProperty("environments").GetProperty("production");
        // Each overridden request leaf travels as a flat top-level key.
        Assert.True(p.GetProperty("enabled").GetBoolean());
        Assert.Equal("https://prod.example.com/hook", p.GetProperty("url").GetString());
        Assert.Equal("PUT", p.GetProperty("method").GetString());
        Assert.Equal(90, p.GetProperty("timeout").GetInt32());
        Assert.Equal("{\"warm\":true}", p.GetProperty("body").GetString());
        Assert.Equal("200", p.GetProperty("success_status").GetString());
        Assert.False(p.GetProperty("tls_verify").GetBoolean());
        Assert.Contains("BEGIN CERTIFICATE", p.GetProperty("ca_cert").GetString());
        Assert.Equal("Bearer prod", p.GetProperty("headers.Authorization").GetString());
        // Each header is a `headers.<name>` leaf, keyed on the FIRST dot so a dotted
        // header name survives.
        Assert.Equal("v", p.GetProperty("headers.X-Foo.Bar").GetString());
        // It is a flat overlay — never a nested `configuration` object.
        Assert.False(p.TryGetProperty("configuration", out _));
    }

    [Fact]
    public async Task Get_ParsesAllEnvironmentRequestLeaves_FirstDotHeaders_IgnoresUnknown()
    {
        const string envs =
            "{\"production\":{\"enabled\":true,\"url\":\"https://p\",\"method\":\"PUT\","
            + "\"timeout\":9,\"body\":\"b\",\"success_status\":\"200\",\"tls_verify\":false,"
            + "\"ca_cert\":\"CA\",\"headers.Authorization\":\"Bearer x\","
            + "\"headers.X-Foo.Bar\":\"v\",\"next_run_at\":null,"
            + "\"unknown_leaf\":\"ignored\",\"also.dotted\":\"ignored\"}}";
        var job = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + JobResource(environmentsJson: envs) + "}"),
        })).GetAsync(JobId);
        var prod = job.Environments["production"];
        Assert.True(prod.Enabled);
        Assert.Equal("https://p", prod.Url);
        Assert.Equal(HttpMethod.Put, prod.Method);
        Assert.Equal(9, prod.Timeout);
        Assert.Equal("b", prod.Body);
        Assert.Equal("200", prod.SuccessStatus);
        Assert.False(prod.TlsVerify);
        Assert.Equal("CA", prod.CaCert);
        Assert.Equal("Bearer x", prod.GetHeader("Authorization"));
        // First-dot split preserves the dotted header name.
        Assert.Equal("v", prod.GetHeader("X-Foo.Bar"));
        // next_run_at present-but-null stays null; unknown / dotted-non-header leaves
        // are ignored for forward compatibility.
        Assert.Null(prod.NextRunAt);
    }

    [Fact]
    public async Task Get_EnvironmentNonObjectOrNullValue_YieldsEmptyOverride()
    {
        // Forward-compat: a JSON null and a non-object env value both parse to an
        // empty (disabled, all-null) override rather than throwing.
        const string envs = "{\"production\":null,\"staging\":\"not-an-object\"}";
        var job = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + JobResource(environmentsJson: envs) + "}"),
        })).GetAsync(JobId);
        Assert.False(job.Environments["production"].Enabled);
        Assert.Null(job.Environments["production"].Url);
        Assert.False(job.Environments["staging"].Enabled);
        Assert.Empty(job.Environments["staging"].Headers);
    }

    // ----------------------------------------------------------------------
    // Build-to-wire: drop base `enabled`, emit `environments`
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_BuildBody_DropsBaseEnabled_EmitsEnvironments()
    {
        string? capturedBody = null;
        var jobs = MakeJobs(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource() + "}"),
            };
        });
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg(), description: "d");
        // development: flat url + header-leaf overrides, disabled.
        job.Environment("development").Url = "https://dev.example.com/hook";
        job.Environment("development").SetHeader("X-Env", "dev");
        job.Environment("development").Enabled = false;
        // production: enabled + per-env schedule/timezone overrides.
        job.Environment("production").Enabled = true;
        job.Environment("production").Schedule = "0 3 * * *";
        job.Timezone = "America/New_York";                          // base timezone
        job.Environment("production").Timezone = "Europe/London";   // per-env override
        await job.SaveAsync();

        using var doc = JsonDocument.Parse(capturedBody!);
        var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
        // Base read-only roll-ups are never written.
        Assert.False(attrs.TryGetProperty("enabled", out _));
        Assert.False(attrs.TryGetProperty("kind", out _));
        Assert.False(attrs.TryGetProperty("version", out _));
        // The base timezone is sent when set.
        Assert.Equal("America/New_York", attrs.GetProperty("timezone").GetString());
        // environments map is emitted as a flat sparse overlay; each entry carries
        // its own enabled flag.
        var envs = attrs.GetProperty("environments");
        Assert.True(envs.GetProperty("production").GetProperty("enabled").GetBoolean());
        Assert.False(envs.GetProperty("development").GetProperty("enabled").GetBoolean());
        // production overrides no request leaf (url absent); development overrides the
        // flat `url` leaf and the `headers.X-Env` leaf (NOT a nested configuration).
        Assert.False(envs.GetProperty("production").TryGetProperty("url", out _));
        Assert.False(envs.GetProperty("development").TryGetProperty("configuration", out _));
        Assert.Equal("https://dev.example.com/hook",
            envs.GetProperty("development").GetProperty("url").GetString());
        Assert.Equal("dev", envs.GetProperty("development").GetProperty("headers.X-Env").GetString());
        // A per-environment schedule override is sent when set; development (no
        // override) and the read-only next_run_at are omitted on every entry.
        Assert.Equal("0 3 * * *", envs.GetProperty("production").GetProperty("schedule").GetString());
        Assert.False(envs.GetProperty("development").TryGetProperty("schedule", out _));
        // A per-environment timezone override is sent when set; development (no
        // override) omits it.
        Assert.Equal("Europe/London", envs.GetProperty("production").GetProperty("timezone").GetString());
        Assert.False(envs.GetProperty("development").TryGetProperty("timezone", out _));
        Assert.False(envs.GetProperty("production").TryGetProperty("next_run_at", out _));
        Assert.False(envs.GetProperty("development").TryGetProperty("next_run_at", out _));
    }

    [Fact]
    public async Task SaveAsync_BuildBody_NoEnvironments_OmitsEnvironmentsKey()
    {
        string? capturedBody = null;
        var jobs = MakeJobs(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource() + "}"),
            };
        });
        var job = jobs.NewManualJob(JobId, name: "n", configuration: Cfg());
        await job.SaveAsync();

        using var doc = JsonDocument.Parse(capturedBody!);
        var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
        Assert.False(attrs.TryGetProperty("enabled", out _));
        Assert.False(attrs.TryGetProperty("environments", out _));
        // A manual job has no schedule — the null base schedule is omitted on the wire.
        Assert.False(attrs.TryGetProperty("schedule", out _));
    }

    // ----------------------------------------------------------------------
    // X-Smplkit-Environment header on create / update / run
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Schedule_OneOff_SerializesDatetimeAndBirthEnvironment()
    {
        string? header = null;
        string? capturedBody = null;
        var jobs = MakeJobs(async req =>
        {
            header = req.Headers.TryGetValues("X-Smplkit-Environment", out var v) ? v.First() : null;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource("showcase-oneoff", kind: "one_off") + "}"),
            };
        });
        var when = new DateTimeOffset(2030, 1, 1, 12, 30, 0, TimeSpan.Zero);
        var job = jobs.Schedule("showcase-oneoff", name: "n", schedule: when, configuration: Cfg(), environment: "development");
        await job.SaveAsync();
        Assert.Equal("development", header);  // birth environment
        // The datetime is serialized to its ISO-8601 round-trip string on the wire.
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal(
            when.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            doc.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("schedule").GetString());
    }

    [Fact]
    public async Task Create_BirthEnvironmentDefaultsToClientEnvironment()
    {
        string? header = null;
        var jobs = MakeJobs(req =>
        {
            header = req.Headers.TryGetValues("X-Smplkit-Environment", out var v) ? v.First() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource() + "}"),
            });
        }, environment: "production");
        var job = jobs.Schedule(JobId, name: "n", schedule: new DateTimeOffset(2030, 1, 1, 12, 30, 0, TimeSpan.Zero), configuration: Cfg());
        await job.SaveAsync();
        Assert.Equal("production", header);
    }

    [Fact]
    public async Task Create_NoEnvironment_OmitsHeader()
    {
        bool hasHeader = true;
        var jobs = MakeJobs(req =>
        {
            hasHeader = req.Headers.Contains("X-Smplkit-Environment");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource() + "}"),
            });
        });
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        await job.SaveAsync();
        Assert.False(hasHeader);
    }

    [Fact]
    public async Task Update_SendsClientEnvironmentHeader()
    {
        string? putHeader = null;
        var jobs = MakeJobs(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + JobResource() + "}"),
                });
            putHeader = req.Headers.TryGetValues("X-Smplkit-Environment", out var v) ? v.First() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + JobResource(version: 2) + "}"),
            });
        }, environment: "production");
        var job = await jobs.GetAsync(JobId);
        job.Name = "renamed";
        await job.SaveAsync();
        Assert.Equal("production", putHeader);
    }

    [Fact]
    public async Task Update_NoClientEnvironment_OmitsHeader()
    {
        bool hasHeader = true;
        var jobs = MakeJobs(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + JobResource() + "}"),
                });
            hasHeader = req.Headers.Contains("X-Smplkit-Environment");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + JobResource(version: 2) + "}"),
            });
        });
        var job = await jobs.GetAsync(JobId);
        await job.SaveAsync();
        Assert.False(hasHeader);
    }

    [Fact]
    public async Task Run_SendsExplicitEnvironmentHeader()
    {
        string? header = null;
        var run = await MakeJobs(req =>
        {
            header = req.Headers.TryGetValues("X-Smplkit-Environment", out var v) ? v.First() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RunResource(trigger: "MANUAL") + "}"),
            });
        }, environment: "development").RunAsync(JobId, environment: "production");
        Assert.Equal("production", header);
        Assert.Equal("MANUAL", run.Trigger);
    }

    [Fact]
    public async Task Run_DefaultsToClientEnvironmentHeader()
    {
        string? header = null;
        await MakeJobs(req =>
        {
            header = req.Headers.TryGetValues("X-Smplkit-Environment", out var v) ? v.First() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RunResource(trigger: "MANUAL") + "}"),
            });
        }, environment: "production").RunAsync(JobId);
        Assert.Equal("production", header);
    }

    [Fact]
    public async Task Run_NoEnvironment_OmitsHeader()
    {
        bool hasHeader = true;
        await MakeJobs(req =>
        {
            hasHeader = req.Headers.Contains("X-Smplkit-Environment");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RunResource(trigger: "MANUAL") + "}"),
            });
        }).RunAsync(JobId);
        Assert.False(hasHeader);
    }

    // ----------------------------------------------------------------------
    // Runs sub-client + filter[environment] resolution
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Runs_List_SendsFilters()
    {
        string? url = null;
        var rows = await MakeJobs(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[" + RunResource() + "],\"meta\":{\"page_size\":50}}"),
            });
        }).Runs.ListAsync(job: JobId, pageSize: 2, after: "cur");
        Assert.Single(rows);
        Assert.Equal(RunId, rows[0].Id);
        Assert.Contains("filter%5Bjob%5D=" + JobId, url!);
        Assert.Contains("page%5Bsize%5D=2", url!);
        Assert.Contains("page%5Bafter%5D=cur", url!);
    }

    [Fact]
    public async Task Runs_List_DefaultArgs()
    {
        var rows = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":50}}"),
        })).Runs.ListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Runs_List_FilterEnvironment_ExplicitListWins()
    {
        string? url = null;
        await MakeJobs(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":50}}"),
            });
        }, environment: "production").Runs.ListAsync(environments: new[] { "production", "development" });
        // Explicit, comma-joined list wins over the client default.
        Assert.Contains("filter%5Benvironment%5D=production%2Cdevelopment", url!);
    }

    [Fact]
    public async Task Runs_List_FilterEnvironment_FallsBackToClientDefault()
    {
        string? url = null;
        await MakeJobs(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":50}}"),
            });
        }, environment: "production").Runs.ListAsync();
        Assert.Contains("filter%5Benvironment%5D=production", url!);
    }

    [Fact]
    public async Task Runs_List_FilterEnvironment_OmittedWhenUnset()
    {
        string? url = null;
        await MakeJobs(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":50}}"),
            });
        }).Runs.ListAsync();
        Assert.DoesNotContain("filter%5Benvironment%5D", url!);
    }

    [Fact]
    public async Task Runs_Get_ReturnsRun()
    {
        var run = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + RunResource() + "}"),
        })).Runs.GetAsync(RunId);
        Assert.Equal(RunId, run.Id);
        Assert.Equal("SUCCEEDED", run.Status);
        Assert.Equal("production", run.Environment);
        Assert.Equal(400, run.TotalDurationMs);
        Assert.Equal(1, run.JobVersion);
        Assert.NotNull(run.Request);
        Assert.NotNull(run.Result);
        Assert.NotNull(run.StartedAt);
        Assert.NotNull(run.ScheduledFor);
        Assert.NotNull(run.FinishedAt);
        Assert.NotNull(run.CreatedAt);
        Assert.Equal(100, run.PendingDurationMs);
        Assert.Equal(300, run.RunDurationMs);
        Assert.Null(run.FailureReason);
        Assert.Null(run.Error);
    }

    [Fact]
    public async Task Runs_Cancel_ReturnsCanceled()
    {
        var run = await MakeJobs(req =>
        {
            Assert.EndsWith("/actions/cancel", req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RunResource(status: "CANCELED") + "}"),
            });
        }).Runs.CancelAsync(RunId);
        Assert.Equal("CANCELED", run.Status);
    }

    [Fact]
    public async Task Runs_Rerun_ReturnsRerun()
    {
        var run = await MakeJobs(req =>
        {
            Assert.EndsWith("/actions/rerun", req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RunResource(trigger: "RERUN", rerunOf: RunId) + "}"),
            });
        }).Runs.RerunAsync(RunId);
        Assert.Equal("RERUN", run.Trigger);
        Assert.Equal(RunId, run.RerunOf);
    }

    // ----------------------------------------------------------------------
    // Run active-record actions (rerun / cancel via the run's client backref)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Run_RerunAsync_ActiveRecord()
    {
        const string rerunId = "1a2b3c4d-0000-4a1b-9c3d-aaaaaaaaaaaa";
        var jobs = MakeJobs(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/actions/rerun"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + RunResource(id: rerunId, trigger: "RERUN", rerunOf: RunId) + "}"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RunResource() + "}"),
            });
        });
        var run = await jobs.Runs.GetAsync(RunId);
        var rerun = await run.RerunAsync();
        Assert.Equal("RERUN", rerun.Trigger);
        Assert.Equal(rerunId, rerun.Id);
        Assert.Equal(RunId, rerun.RerunOf);
    }

    [Fact]
    public async Task Run_CancelAsync_ActiveRecord()
    {
        var jobs = MakeJobs(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/actions/cancel"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + RunResource(status: "CANCELED") + "}"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RunResource() + "}"),
            });
        });
        var run = await jobs.Runs.GetAsync(RunId);
        var canceled = await run.CancelAsync();
        Assert.Equal("CANCELED", canceled.Status);
    }

    [Fact]
    public async Task Run_RerunAsync_WithoutClient_Throws()
    {
        var run = BuildClientlessRun();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RerunAsync());
    }

    [Fact]
    public async Task Run_CancelAsync_WithoutClient_Throws()
    {
        var run = BuildClientlessRun();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.CancelAsync());
    }

    // ----------------------------------------------------------------------
    // Job active-record run helpers (TriggerAsync / ListRunsAsync)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Job_TriggerAsync_DelegatesToRun_WithEnvironment()
    {
        string? header = null;
        var jobs = MakeJobs(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + JobResource() + "}"),
                });
            header = req.Headers.TryGetValues("X-Smplkit-Environment", out var v) ? v.First() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RunResource(trigger: "MANUAL") + "}"),
            });
        });
        var job = await jobs.GetAsync(JobId);
        var run = await job.TriggerAsync(environment: "production");
        Assert.Equal("MANUAL", run.Trigger);
        Assert.Equal("production", header);
    }

    [Fact]
    public async Task Job_TriggerAsync_WithoutClient_Throws()
    {
        var job = BuildClientlessJob();
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.TriggerAsync());
    }

    [Fact]
    public async Task Job_ListRunsAsync_DelegatesAndFiltersByEnvironment()
    {
        string? url = null;
        var jobs = MakeJobs(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/" + JobId))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + JobResource() + "}"),
                });
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[" + RunResource() + "],\"meta\":{\"page_size\":50}}"),
            });
        });
        var job = await jobs.GetAsync(JobId);
        var runs = await job.ListRunsAsync(environment: "production", pageSize: 5);
        Assert.Single(runs);
        Assert.Contains("filter%5Bjob%5D=" + JobId, url!);
        Assert.Contains("filter%5Benvironment%5D=production", url!);
        Assert.Contains("page%5Bsize%5D=5", url!);
    }

    [Fact]
    public async Task Job_ListRunsAsync_NoEnvironment_OmitsFilter()
    {
        string? url = null;
        var jobs = MakeJobs(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/" + JobId))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + JobResource() + "}"),
                });
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":50}}"),
            });
        });
        var job = await jobs.GetAsync(JobId);
        await job.ListRunsAsync();
        Assert.Contains("filter%5Bjob%5D=" + JobId, url!);
        Assert.DoesNotContain("filter%5Benvironment%5D", url!);
    }

    [Fact]
    public async Task Job_ListRunsAsync_WithoutClient_Throws()
    {
        var job = BuildClientlessJob();
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.ListRunsAsync());
    }

    // ----------------------------------------------------------------------
    // Error mapping
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Get_NotFound_MapsToNotFoundException()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = JsonApi("{\"errors\":[{\"detail\":\"missing\"}]}"),
        }));
        await Assert.ThrowsAsync<NotFoundException>(() => jobs.GetAsync("missing"));
    }

    [Fact]
    public async Task Save_Conflict_MapsToConflictException()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonApi("{\"errors\":[{\"detail\":\"dup\"}]}"),
        }));
        var job = jobs.NewManualJob("dup", name: "D", configuration: Cfg());
        await Assert.ThrowsAsync<ConflictException>(() => job.SaveAsync());
    }

    // ----------------------------------------------------------------------
    // Wire mapping edge cases + enums
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Get_MinimalConfiguration_DefaultsApplied()
    {
        // No configuration block, no environments, no version → defaults flow through.
        var job = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + JobId + "\",\"type\":\"job\",\"attributes\":{"
                + "\"name\":\"x\",\"schedule\":\"now\",\"type\":\"http\","
                + "\"concurrency_policy\":\"ALLOW\"}}}"),
        })).GetAsync(JobId);
        Assert.Equal(string.Empty, job.Configuration.Url);
        Assert.Empty(job.Configuration.Headers);
        Assert.Equal(HttpMethod.Post, job.Configuration.Method);
        Assert.True(job.Configuration.TlsVerify);
        Assert.Null(job.Version);
        Assert.Empty(job.Environments);
        Assert.Null(job.Kind);
        Assert.False(job.IsRecurring());
        Assert.False(job.IsManual());
        Assert.False(job.IsOneOff());
        Assert.False(job.Enabled);
    }

    [Theory]
    [InlineData(HttpMethod.Get, "GET")]
    [InlineData(HttpMethod.Post, "POST")]
    [InlineData(HttpMethod.Put, "PUT")]
    [InlineData(HttpMethod.Patch, "PATCH")]
    [InlineData(HttpMethod.Delete, "DELETE")]
    public async Task HttpMethod_RoundTripsOnWire(HttpMethod method, string wire)
    {
        string? capturedBody = null;
        var jobs = MakeJobs(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource() + "}"),
            };
        });
        var cfg = Cfg();
        cfg.Method = method;
        var job = jobs.NewManualJob(JobId, name: "n", configuration: cfg);
        await job.SaveAsync();
        Assert.Contains($"\"method\":\"{wire}\"", capturedBody!);
    }

    [Fact]
    public async Task HttpMethod_ToGenOutOfRange_Throws()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var cfg = Cfg();
        cfg.Method = (HttpMethod)999;
        var job = jobs.NewManualJob(JobId, name: "n", configuration: cfg);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => job.SaveAsync());
    }

    [Theory]
    [InlineData(HttpMethod.Get, "GET")]
    [InlineData(HttpMethod.Post, "POST")]
    [InlineData(HttpMethod.Put, "PUT")]
    [InlineData(HttpMethod.Patch, "PATCH")]
    [InlineData(HttpMethod.Delete, "DELETE")]
    public void HttpMethodExtensions_ToWireValue(HttpMethod method, string wire)
    {
        Assert.Equal(wire, method.ToWireValue());
    }

    [Fact]
    public void HttpMethodExtensions_ToWireValue_OutOfRangeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((HttpMethod)999).ToWireValue());
    }

    [Theory]
    [InlineData("GET", HttpMethod.Get)]
    [InlineData("POST", HttpMethod.Post)]
    [InlineData("PUT", HttpMethod.Put)]
    [InlineData("PATCH", HttpMethod.Patch)]
    [InlineData("DELETE", HttpMethod.Delete)]
    public void HttpMethodExtensions_FromWireValue_Known(string wire, HttpMethod expected)
    {
        Assert.Equal(expected, HttpMethodExtensions.FromWireValue(wire));
    }

    [Fact]
    public void HttpMethodExtensions_FromWireValue_UnknownDefaultsToPost()
    {
        Assert.Equal(HttpMethod.Post, HttpMethodExtensions.FromWireValue("UNKNOWN"));
        Assert.Equal(HttpMethod.Post, HttpMethodExtensions.FromWireValue(null!));
    }

    [Theory]
    [InlineData("recurring", true, false, false)]
    [InlineData("manual", false, true, false)]
    [InlineData("one_off", false, false, true)]
    public async Task Get_ParsesKind_PredicatesReflectIt(string kind, bool isRecurring, bool isManual, bool isOneOff)
    {
        var job = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + JobResource(kind: kind) + "}"),
        })).GetAsync(JobId);
        Assert.Equal(isRecurring, job.IsRecurring());
        Assert.Equal(isManual, job.IsManual());
        Assert.Equal(isOneOff, job.IsOneOff());
    }

    [Theory]
    [InlineData(JobKind.Recurring, "recurring")]
    [InlineData(JobKind.Manual, "manual")]
    [InlineData(JobKind.OneOff, "one_off")]
    public void JobKindExtensions_ToWireValue(JobKind kind, string wire)
    {
        Assert.Equal(wire, kind.ToWireValue());
    }

    [Fact]
    public void JobKindExtensions_ToWireValue_OutOfRangeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((JobKind)999).ToWireValue());
    }

    [Fact]
    public async Task Run_Trigger_MatchesRunTriggerConstants()
    {
        // Trigger is a raw string, equal to the RunTrigger constant and the raw value.
        var run = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + RunResource(trigger: "SCHEDULE") + "}"),
        })).Runs.GetAsync(RunId);
        Assert.Equal(RunTrigger.Schedule, run.Trigger);
        Assert.Equal("SCHEDULE", run.Trigger);
    }

    [Fact]
    public async Task SaveAsync_SendsTlsVerifyAndCaCert_OnWire()
    {
        string? capturedBody = null;
        var jobs = MakeJobs(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource() + "}"),
            };
        });
        var cfg = Cfg();
        cfg.TlsVerify = false;
        cfg.CaCert = "-----BEGIN CERTIFICATE-----\nfoo\n-----END CERTIFICATE-----";
        var job = jobs.NewManualJob(JobId, name: "n", configuration: cfg);
        await job.SaveAsync();
        Assert.Contains("\"tls_verify\":false", capturedBody);
        Assert.Contains("\"ca_cert\":\"-----BEGIN CERTIFICATE-----", capturedBody);
    }

    // ----------------------------------------------------------------------
    // ToString + model coverage
    // ----------------------------------------------------------------------

    [Fact]
    public void Job_ToString_IncludesIdNameEnabledEnvironments()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.Environment("production").Enabled = true;
        job.Environment("development").Enabled = false;
        var s = job.ToString();
        Assert.Contains("Id=my-job", s);
        Assert.Contains("Name=n", s);
        // Only enabled environments appear, ordinal-sorted.
        Assert.Contains("EnabledIn=[production]", s);
    }

    [Fact]
    public async Task Run_ToString_IncludesIdJobStatus()
    {
        var run = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + RunResource() + "}"),
        })).Runs.GetAsync(RunId);
        var s = run.ToString();
        Assert.Contains("Run(", s);
        Assert.Contains("Status=SUCCEEDED", s);
    }

    [Fact]
    public async Task Usage_ToString_IncludesPeriodAndRuns()
    {
        var usage = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(UsageBody),
        })).UsageAsync();
        var s = usage.ToString();
        Assert.Contains("Usage(", s);
        Assert.Contains("12/3000", s);
    }

    // ----------------------------------------------------------------------
    // Retry policies — active record (New + SaveAsync create/update), CRUD
    // ----------------------------------------------------------------------

    [Fact]
    public void RetryPolicies_New_ReturnsUnsavedPolicy_NoNetwork()
    {
        var calls = 0;
        var jobs = MakeJobs(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var policy = jobs.RetryPolicies.New(
            RetryPolicyId, name: "Retry on server errors", maxRetries: 5,
            backoff: Backoff.Exponential, delaySeconds: 2, maxDelaySeconds: 60,
            retryOnTimeout: true, retryOnConnectionError: true,
            retryStatuses: new List<string> { "429", "5xx" },
            retryStatusesExcept: new List<string> { "501" });
        Assert.Equal(RetryPolicyId, policy.Id);
        Assert.Null(policy.CreatedAt);
        Assert.Equal(Backoff.Exponential, policy.Backoff);
        Assert.Equal(60, policy.MaxDelaySeconds);
        Assert.True(policy.RetryOnTimeout);
        Assert.True(policy.RetryOnConnectionError);
        Assert.Equal(new[] { "429", "5xx" }, policy.RetryStatuses);
        Assert.Equal(new[] { "501" }, policy.RetryStatusesExcept);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RetryPolicy_SaveAsync_CreatesThenUpdates_AndShapesWireBody()
    {
        var capturedMethods = new List<string>();
        string? createBody = null;
        var jobs = MakeJobs(async req =>
        {
            capturedMethods.Add(req.Method.Method);
            if (req.Method == System.Net.Http.HttpMethod.Post)
            {
                createBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonApi("{\"data\":" + RetryPolicyJson(version: 1) + "}"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + RetryPolicyJson(name: "Renamed", version: 2) + "}"),
            };
        });

        var policy = jobs.RetryPolicies.New(
            RetryPolicyId, name: "Retry on server errors", maxRetries: 5,
            backoff: Backoff.Exponential, delaySeconds: 2, maxDelaySeconds: 60,
            retryOnTimeout: true, retryOnConnectionError: true,
            retryStatuses: new List<string> { "429", "5xx" },
            retryStatusesExcept: new List<string> { "501" });
        Assert.Null(policy.CreatedAt);
        await policy.SaveAsync();
        Assert.Equal("POST", capturedMethods[0]);
        Assert.NotNull(policy.CreatedAt);
        Assert.Equal(1, policy.Version);

        // Wire body: lowercase backoff slug, the four discrete match fields, max_delay
        // sent, and the server-managed read-only fields omitted.
        using var doc = JsonDocument.Parse(createBody!);
        var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
        Assert.Equal("exponential", attrs.GetProperty("backoff").GetString());
        Assert.Equal(5, attrs.GetProperty("max_retries").GetInt32());
        Assert.Equal(2, attrs.GetProperty("delay_seconds").GetInt32());
        Assert.Equal(60, attrs.GetProperty("max_delay_seconds").GetInt32());
        Assert.True(attrs.GetProperty("retry_on_timeout").GetBoolean());
        Assert.True(attrs.GetProperty("retry_on_connection_error").GetBoolean());
        Assert.Equal(
            new[] { "429", "5xx" },
            attrs.GetProperty("retry_statuses").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(
            new[] { "501" },
            attrs.GetProperty("retry_statuses_except").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(attrs.TryGetProperty("created_at", out _));
        Assert.False(attrs.TryGetProperty("version", out _));
        Assert.Equal("retry_policy", doc.RootElement.GetProperty("data").GetProperty("type").GetString());
        Assert.Equal(RetryPolicyId, doc.RootElement.GetProperty("data").GetProperty("id").GetString());

        // Parsed back from the response: backoff, match fields, max_delay round-trip.
        Assert.Equal(Backoff.Exponential, policy.Backoff);
        Assert.Equal(60, policy.MaxDelaySeconds);
        Assert.True(policy.RetryOnTimeout);
        Assert.True(policy.RetryOnConnectionError);
        Assert.Equal(new[] { "429", "5xx" }, policy.RetryStatuses);
        Assert.Equal(new[] { "501" }, policy.RetryStatusesExcept);

        // Second save → full-replace PUT.
        policy.Name = "Renamed";
        await policy.SaveAsync();
        Assert.Equal("PUT", capturedMethods[1]);
        Assert.Equal(2, policy.Version);
        Assert.Equal("Renamed", policy.Name);
    }

    [Fact]
    public async Task RetryPolicy_Create_FixedBackoff_NoMaxDelay_EmptyMatchFields()
    {
        string? body = null;
        var jobs = MakeJobs(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + RetryPolicyJson(
                    backoff: "fixed", maxDelaySeconds: "null", retryOnTimeout: "false",
                    retryOnConnectionError: "false", retryStatuses: "[]", retryStatusesExcept: "[]") + "}"),
            };
        });
        // New without the match args → bools default false and lists default empty.
        var policy = jobs.RetryPolicies.New("p", name: "n", maxRetries: 0, backoff: Backoff.Fixed, delaySeconds: 1);
        Assert.False(policy.RetryOnTimeout);
        Assert.False(policy.RetryOnConnectionError);
        Assert.Empty(policy.RetryStatuses);
        Assert.Empty(policy.RetryStatusesExcept);
        await policy.SaveAsync();

        using var doc = JsonDocument.Parse(body!);
        var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
        Assert.Equal("fixed", attrs.GetProperty("backoff").GetString());
        // max_delay_seconds omitted when unset.
        Assert.False(attrs.TryGetProperty("max_delay_seconds", out _));
        Assert.False(attrs.GetProperty("retry_on_timeout").GetBoolean());
        Assert.False(attrs.GetProperty("retry_on_connection_error").GetBoolean());
        Assert.Empty(attrs.GetProperty("retry_statuses").EnumerateArray());
        Assert.Empty(attrs.GetProperty("retry_statuses_except").EnumerateArray());
        // Parsed back: fixed backoff, no max_delay.
        Assert.Equal(Backoff.Fixed, policy.Backoff);
        Assert.Null(policy.MaxDelaySeconds);
    }

    [Fact]
    public async Task RetryPolicy_Create_AllMatchFields_RoundTrip()
    {
        string? body = null;
        var jobs = MakeJobs(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + RetryPolicyJson(
                    retryStatuses: "[\"1xx\",\"2xx\",\"3xx\",\"4xx\",\"5xx\"]",
                    retryStatusesExcept: "[\"404\",\"501\"]") + "}"),
            };
        });
        var policy = jobs.RetryPolicies.New(
            "p", name: "n", maxRetries: 3, backoff: Backoff.Exponential, delaySeconds: 1,
            retryOnTimeout: true, retryOnConnectionError: true,
            retryStatuses: new List<string> { "1xx", "2xx", "3xx", "4xx", "5xx" },
            retryStatusesExcept: new List<string> { "404", "501" });
        await policy.SaveAsync();

        using var doc = JsonDocument.Parse(body!);
        var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
        var wireStatuses = attrs.GetProperty("retry_statuses").EnumerateArray().Select(e => e.GetString()).ToList();
        var wireExcept = attrs.GetProperty("retry_statuses_except").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "1xx", "2xx", "3xx", "4xx", "5xx" }, wireStatuses);
        Assert.Equal(new[] { "404", "501" }, wireExcept);
        // Parsed back: all match fields round-trip onto the wrapper.
        Assert.True(policy.RetryOnTimeout);
        Assert.True(policy.RetryOnConnectionError);
        Assert.Equal(new[] { "1xx", "2xx", "3xx", "4xx", "5xx" }, policy.RetryStatuses);
        Assert.Equal(new[] { "404", "501" }, policy.RetryStatusesExcept);
    }

    [Fact]
    public async Task RetryPolicies_List_ReturnsRows_AndSendsFilters()
    {
        string? url = null;
        var rows = await MakeJobs(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[" + RetryPolicyJson("a") + "," + RetryPolicyJson("b")
                    + "],\"meta\":{\"pagination\":{\"page\":1,\"size\":50}}}"),
            });
        }).RetryPolicies.ListAsync(name: "server", pageNumber: 1, pageSize: 10);
        Assert.Equal(2, rows.Count);
        Assert.Contains("filter%5Bname%5D=server", url!);
        Assert.Contains("page%5Bnumber%5D=1", url!);
        Assert.Contains("page%5Bsize%5D=10", url!);
    }

    [Fact]
    public async Task RetryPolicies_List_DefaultArgs_NoFilters()
    {
        var rows = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"pagination\":{\"page\":1,\"size\":1000}}}"),
        })).RetryPolicies.ListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task RetryPolicies_Get_ReturnsClientBoundInstance()
    {
        var policy = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + RetryPolicyJson() + "}"),
        })).RetryPolicies.GetAsync(RetryPolicyId);
        Assert.Equal(RetryPolicyId, policy.Id);
        Assert.Equal("Retry on server errors", policy.Name);
        Assert.Equal(5, policy.MaxRetries);
        Assert.Equal(Backoff.Exponential, policy.Backoff);
        Assert.Equal(2, policy.DelaySeconds);
        Assert.Equal(60, policy.MaxDelaySeconds);
        Assert.True(policy.RetryOnTimeout);
        Assert.True(policy.RetryOnConnectionError);
        Assert.Equal(new[] { "429", "5xx" }, policy.RetryStatuses);
        Assert.Equal(new[] { "501" }, policy.RetryStatusesExcept);
        Assert.NotNull(policy.CreatedAt);
        Assert.NotNull(policy.UpdatedAt);
        Assert.Equal(1, policy.Version);
    }

    [Fact]
    public async Task RetryPolicies_Get_ParsesDeletedAtAndFixedBackoff()
    {
        // A soft-deleted policy fixture exercises the converter's deleted_at read path
        // and the fixed-backoff parse arm.
        var policy = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + RetryPolicyJson(
                backoff: "fixed", maxDelaySeconds: "null", deletedAt: "\"2026-06-10T00:00:00Z\"") + "}"),
        })).RetryPolicies.GetAsync(RetryPolicyId);
        Assert.Equal(Backoff.Fixed, policy.Backoff);
        Assert.Null(policy.MaxDelaySeconds);
        Assert.NotNull(policy.DeletedAt);
    }

    [Fact]
    public async Task RetryPolicies_Delete_ById()
    {
        string? method = null;
        await MakeJobs(req =>
        {
            method = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }).RetryPolicies.DeleteAsync(RetryPolicyId);
        Assert.Equal("DELETE", method);
    }

    [Fact]
    public async Task RetryPolicy_DeleteAsync_OnInstance()
    {
        string? method = null;
        var jobs = MakeJobs(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + RetryPolicyJson() + "}"),
                });
            method = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var policy = await jobs.RetryPolicies.GetAsync(RetryPolicyId);
        await policy.DeleteAsync();
        Assert.Equal("DELETE", method);
    }

    [Fact]
    public async Task RetryPolicy_SaveAsync_WithoutClient_Throws()
    {
        var policy = new RetryPolicy(null, "p", "n", 1, Backoff.Fixed, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.SaveAsync());
    }

    [Fact]
    public async Task RetryPolicy_DeleteAsync_WithoutClient_Throws()
    {
        var policy = new RetryPolicy(null, "p", "n", 1, Backoff.Fixed, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.DeleteAsync());
    }

    [Fact]
    public void RetryPolicy_ToString_IncludesIdNameMaxRetries()
    {
        var policy = new RetryPolicy(null, "showcase-retry", "Retry on server errors", 5, Backoff.Exponential, 2);
        var s = policy.ToString();
        Assert.Contains("RetryPolicy(", s);
        Assert.Contains("Id=showcase-retry", s);
        Assert.Contains("MaxRetries=5", s);
    }

    [Fact]
    public void RetryPolicy_MatchFields_DefaultToFalseAndEmpty()
    {
        var policy = new RetryPolicy(null, "p", "n", 1, Backoff.Fixed, 1);
        Assert.False(policy.RetryOnTimeout);
        Assert.False(policy.RetryOnConnectionError);
        Assert.Empty(policy.RetryStatuses);
        Assert.Empty(policy.RetryStatusesExcept);
    }

    [Fact]
    public async Task RetryPolicies_Get_ToleratesAbsentMatchFields()
    {
        // A response that omits the retry_* attributes entirely exercises the
        // converter's defaulting (bools false, lists empty) read path.
        var attrs = "{\"name\":\"n\",\"max_retries\":1,\"backoff\":\"fixed\","
            + "\"delay_seconds\":1,\"max_delay_seconds\":null,"
            + "\"created_at\":\"2026-06-04T00:00:00Z\",\"updated_at\":\"2026-06-04T00:00:00Z\","
            + "\"deleted_at\":null,\"version\":1}";
        var policy = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":{\"id\":\"p\",\"type\":\"retry_policy\",\"attributes\":" + attrs + "}}"),
        })).RetryPolicies.GetAsync("p");
        Assert.False(policy.RetryOnTimeout);
        Assert.False(policy.RetryOnConnectionError);
        Assert.Empty(policy.RetryStatuses);
        Assert.Empty(policy.RetryStatusesExcept);
    }

    // ----------------------------------------------------------------------
    // Job retry policy (base + per-environment) and SetSchedule timezone
    // ----------------------------------------------------------------------

    [Fact]
    public void RetryPolicy_Base_AcceptsObjectOrId()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var policy = jobs.RetryPolicies.New("rp", name: "n", maxRetries: 1, backoff: Backoff.Fixed, delaySeconds: 1);
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        // Assigning a RetryPolicy object coerces to its id (implicit conversion).
        job.RetryPolicy = policy;
        Assert.Equal("rp", job.RetryPolicy);
        // Assigning a bare id string works too.
        job.RetryPolicy = "Default";
        Assert.Equal("Default", job.RetryPolicy);
        Assert.Empty(job.Environments);
    }

    [Fact]
    public void RetryPolicy_PerEnvironment_AcceptsObjectOrId()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var policy = jobs.RetryPolicies.New("retry-prod", name: "n", maxRetries: 1, backoff: Backoff.Fixed, delaySeconds: 1);
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        // Enable first, then set the per-env policy via a RetryPolicy object — the
        // same override entry is reused so the enablement survives.
        job.Environment("production").Enabled = true;
        job.Environment("production").RetryPolicy = policy;
        Assert.Null(job.RetryPolicy);
        Assert.True(job.Environments["production"].Enabled);
        Assert.Equal("retry-prod", job.Environments["production"].RetryPolicy);
        // A bare id string is also accepted.
        job.Environment("development").RetryPolicy = "retry-dev";
        Assert.Equal("retry-dev", job.Environments["development"].RetryPolicy);
    }

    [Fact]
    public async Task NewRecurringJob_WithTimezoneAndRetryPolicy_OnWire()
    {
        string? body = null;
        var jobs = MakeJobs(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource() + "}"),
            };
        });
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 2 * * *", configuration: Cfg(),
            timezone: "America/New_York", retryPolicy: "base-retry");
        Assert.Equal("America/New_York", job.Timezone);
        Assert.Equal("base-retry", job.RetryPolicy);
        await job.SaveAsync();

        using var doc = JsonDocument.Parse(body!);
        var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
        Assert.Equal("America/New_York", attrs.GetProperty("timezone").GetString());
        Assert.Equal("base-retry", attrs.GetProperty("retry_policy").GetString());
    }

    [Fact]
    public async Task NewManualJob_WithRetryPolicy_OnWire()
    {
        string? body = null;
        var jobs = MakeJobs(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource(kind: "manual") + "}"),
            };
        });
        var job = jobs.NewManualJob(JobId, name: "n", configuration: Cfg(), retryPolicy: "manual-retry");
        await job.SaveAsync();
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("manual-retry",
            doc.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("retry_policy").GetString());
    }

    [Fact]
    public async Task Schedule_OneOff_WithRetryPolicy_OnWire()
    {
        string? body = null;
        var jobs = MakeJobs(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource(kind: "one_off") + "}"),
            };
        });
        var job = jobs.Schedule(JobId, name: "n", schedule: new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            configuration: Cfg(), retryPolicy: "oneoff-retry");
        await job.SaveAsync();
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("oneoff-retry",
            doc.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("retry_policy").GetString());
    }

    [Fact]
    public async Task SaveAsync_PerEnvRetryPolicy_OnWire()
    {
        string? body = null;
        var jobs = MakeJobs(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + JobResource() + "}"),
            };
        });
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.Environment("production").Enabled = true;
        job.Environment("production").RetryPolicy = "prod-retry";
        await job.SaveAsync();

        using var doc = JsonDocument.Parse(body!);
        var prod = doc.RootElement.GetProperty("data").GetProperty("attributes")
            .GetProperty("environments").GetProperty("production");
        Assert.Equal("prod-retry", prod.GetProperty("retry_policy").GetString());
        // Base retry_policy omitted when unset.
        Assert.False(doc.RootElement.GetProperty("data").GetProperty("attributes")
            .TryGetProperty("retry_policy", out _));
    }

    [Fact]
    public async Task Get_ParsesRetryPolicy_BaseAndPerEnvironment()
    {
        const string envs =
            "{\"production\":{\"enabled\":true,\"retry_policy\":\"prod-retry\"},"
            + "\"development\":{\"enabled\":false}}";
        var job = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + JobResourceWithRetryPolicy("base-retry", envs) + "}"),
        })).GetAsync(JobId);
        Assert.Equal("base-retry", job.RetryPolicy);
        Assert.Equal("prod-retry", job.Environments["production"].RetryPolicy);
        Assert.Null(job.Environments["development"].RetryPolicy);
    }

    [Fact]
    public void Schedule_And_Timezone_Base_SetByDirectAssignment()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.Schedule = "30 2 * * *";
        job.Timezone = "America/Los_Angeles";
        Assert.Equal("30 2 * * *", job.Schedule);
        Assert.Equal("America/Los_Angeles", job.Timezone);
    }

    [Fact]
    public void Schedule_And_Timezone_PerEnvironment_PreserveBase()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.Environment("development").Schedule = "0 */6 * * *";
        job.Environment("development").Timezone = "America/New_York";
        Assert.Equal("0 * * * *", job.Schedule);  // base untouched
        Assert.Null(job.Timezone);                 // base timezone untouched
        Assert.Equal("0 */6 * * *", job.Environments["development"].Schedule);
        Assert.Equal("America/New_York", job.Environments["development"].Timezone);
    }

    // ----------------------------------------------------------------------
    // Run retry chain + run-list trigger / last_run_only filters
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Run_ParsesRetry_OnRetryTrigger()
    {
        const string origin = "2c3d4e5f-0000-4a1b-9c3d-bbbbbbbbbbbb";
        var run = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + RunResource(
                trigger: "RETRY", retry: "{\"of\":\"" + origin + "\",\"attempt\":2}") + "}"),
        })).Runs.GetAsync(RunId);
        Assert.Equal(RunTrigger.Retry, run.Trigger);
        Assert.NotNull(run.Retry);
        Assert.Equal(origin, run.Retry!.Of);
        Assert.Equal(2, run.Retry.Attempt);
    }

    [Fact]
    public async Task Run_Retry_NullWhenAbsent()
    {
        var run = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + RunResource() + "}"),
        })).Runs.GetAsync(RunId);
        Assert.Null(run.Retry);
    }

    [Fact]
    public async Task Runs_List_SendsTriggerFilterAndLastRunOnly()
    {
        string? url = null;
        await MakeJobs(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":50}}"),
            });
        }).Runs.ListAsync(triggers: new[] { RunTrigger.Retry, RunTrigger.Schedule }, lastRunOnly: true);
        Assert.Contains("filter%5Btrigger%5D=RETRY%2CSCHEDULE", url!);
        Assert.Contains("last_run_only=true", url!);
    }

    [Fact]
    public async Task Runs_List_EmptyTriggers_OmitsTriggerFilter()
    {
        string? url = null;
        await MakeJobs(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":50}}"),
            });
        }).Runs.ListAsync(triggers: Array.Empty<string>());
        Assert.DoesNotContain("filter%5Btrigger%5D", url!);
    }

    [Fact]
    public async Task Runs_List_DefaultArgs_OmitTriggerAndLastRunOnly()
    {
        string? url = null;
        await MakeJobs(req =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":50}}"),
            });
        }).Runs.ListAsync();
        Assert.DoesNotContain("filter%5Btrigger%5D", url!);
        Assert.DoesNotContain("last_run_only", url!);
    }

    [Fact]
    public async Task Job_ListRunsAsync_ForwardsTriggersAndLastRunOnly()
    {
        string? url = null;
        var jobs = MakeJobs(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/" + JobId))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + JobResource() + "}"),
                });
            url = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":50}}"),
            });
        });
        var job = await jobs.GetAsync(JobId);
        await job.ListRunsAsync(environment: "production", triggers: new[] { RunTrigger.Retry }, lastRunOnly: true);
        Assert.Contains("filter%5Bjob%5D=" + JobId, url!);
        Assert.Contains("filter%5Benvironment%5D=production", url!);
        Assert.Contains("filter%5Btrigger%5D=RETRY", url!);
        Assert.Contains("last_run_only=true", url!);
    }

    /// <summary>A job resource carrying a base + per-environment <c>retry_policy</c>.</summary>
    private static string JobResourceWithRetryPolicy(string baseRetryPolicy, string environmentsJson)
    {
        return "{\"id\":\"" + JobId + "\",\"type\":\"job\",\"attributes\":{"
            + "\"name\":\"My Job\",\"description\":\"does a thing\",\"type\":\"http\","
            + "\"schedule\":\"0 * * * *\",\"retry_policy\":\"" + baseRetryPolicy + "\","
            + "\"configuration\":{\"method\":\"POST\",\"url\":\"https://api.example.com/hook\","
            + "\"headers\":{},\"body\":null,\"success_status\":\"2xx\",\"timeout\":30,"
            + "\"tls_verify\":true,\"ca_cert\":null},"
            + "\"environments\":" + environmentsJson + ","
            + "\"concurrency_policy\":\"ALLOW\",\"kind\":\"recurring\","
            + "\"created_at\":\"2026-06-04T00:00:00Z\",\"updated_at\":\"2026-06-04T00:00:00Z\","
            + "\"deleted_at\":null,\"version\":1}}";
    }

    /// <summary>Construct a <see cref="Job"/> with no bound client, for the
    /// SaveAsync / DeleteAsync / TriggerAsync / ListRunsAsync guard branches.</summary>
    private static Job BuildClientlessJob() =>
        (Job)System.Activator.CreateInstance(
            typeof(Job),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            binder: null,
            args: new object?[]
            {
                null,           // client
                "x",            // id
                "X",            // name
                "now",          // schedule
                new HttpConfig { Url = "u" },
                null,           // description
                null,           // environments
                null,           // kind
                "http",         // type
                "ALLOW",        // concurrencyPolicy
                null,           // timezone
                null,           // retryPolicy
                null,           // createdAt
                null,           // updatedAt
                null,           // deletedAt
                null,           // version
            },
            culture: null)!;

    /// <summary>Construct a <see cref="Run"/> with no runs-client backref, for the
    /// RerunAsync / CancelAsync guard branches. Goes through the real
    /// <c>RunFromResource</c> parse path with <c>runs: null</c>.</summary>
    private static Run BuildClientlessRun()
    {
        var resource = new GenJobs.RunResource
        {
            Id = RunId,
            Type = "run",
            Attributes = new GenJobs.Run
            {
                Job = JobId,
                Environment = "production",
                Trigger = GenJobs.RunTrigger.MANUAL,
                Status = GenJobs.RunStatus.SUCCEEDED,
            },
        };
        return JobsClient.RunFromResource(resource, runs: null);
    }
}
