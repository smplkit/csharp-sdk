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

    // environments map fixture: production enabled with NO config override but a
    // per-environment schedule override + a next_run_at; development disabled WITH a
    // per-environment configuration override (and no schedule / next_run_at).
    private const string EnvsJson =
        "{\"production\":{\"enabled\":true,\"schedule\":\"0 3 * * *\","
        + "\"timezone\":\"Europe/London\","
        + "\"next_run_at\":\"2026-06-19T03:00:00Z\"},"
        + "\"development\":{\"enabled\":false,\"configuration\":{\"method\":\"POST\","
        + "\"url\":\"https://dev.example.com/hook\",\"headers\":[],\"body\":null,"
        + "\"success_status\":\"2xx\",\"timeout\":30,\"tls_verify\":true,\"ca_cert\":null}}}";

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
            + "\"headers\":[{\"name\":\"X-Api-Key\",\"value\":\"secret\"}],"
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
        string? rerunOf = null, string environment = "production")
    {
        var ro = rerunOf is null ? "null" : "\"" + rerunOf + "\"";
        return "{\"id\":\"" + id + "\",\"type\":\"run\",\"attributes\":{"
            + "\"job\":\"" + JobId + "\",\"job_version\":1,\"environment\":\"" + environment + "\","
            + "\"trigger\":\"" + trigger + "\",\"rerun_of\":" + ro + ","
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

    private static HttpConfig Cfg() => new()
    {
        Url = "https://api.example.com/hook",
        Method = HttpMethod.Post,
        Headers = new List<HttpHeader> { new("X-Api-Key", "secret") },
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
        Assert.True(job.IsEnabled(environment: "production"));
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
        Assert.Equal("X-Api-Key", job.Configuration.Headers[0].Name);
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
    public void SetEnabled_PerEnvironment_RollupDerivedFromEnvironmentMap()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        // Roll-up (no arg) is a derived value over the environment map: false until
        // at least one environment is enabled.
        Assert.False(job.IsEnabled());
        Assert.False(job.Enabled);

        job.SetEnabled(true, environment: "production");
        job.SetEnabled(false, environment: "development");
        Assert.True(job.IsEnabled(environment: "production"));
        Assert.False(job.IsEnabled(environment: "development"));
        // Absent environment → not enabled.
        Assert.False(job.IsEnabled(environment: "staging"));
        // Roll-up is now true (production is enabled) — derived, not server-only.
        Assert.True(job.IsEnabled());
        Assert.True(job.Enabled);

        // Disable the only enabled environment → roll-up flips back to false.
        job.SetEnabled(false, environment: "production");
        Assert.False(job.IsEnabled());
        Assert.False(job.Enabled);
    }

    [Fact]
    public void SetConfiguration_BaseAndPerEnvironment_GetConfigurationResolves()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());

        var baseCfg = new HttpConfig { Url = "https://base.example.com" };
        job.SetConfiguration(baseCfg);
        Assert.Same(baseCfg, job.Configuration);
        Assert.Same(baseCfg, job.GetConfiguration());

        var devCfg = new HttpConfig { Url = "https://dev.example.com" };
        job.SetConfiguration(devCfg, environment: "development");
        Assert.Same(devCfg, job.GetConfiguration(environment: "development"));
        // An environment override entry is created on demand, defaulting disabled.
        Assert.False(job.Environments["development"].Enabled);
        // Environment without an override falls back to the base configuration.
        Assert.Same(baseCfg, job.GetConfiguration(environment: "production"));
    }

    [Fact]
    public void EnvironmentOverride_Reused_PreservesOtherField()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());

        // Configuration first, then enablement — the same override entry is reused
        // (the EnvironmentOverride get-existing path) so the configuration survives.
        var devCfg = new HttpConfig { Url = "https://dev.example.com" };
        job.SetConfiguration(devCfg, environment: "development");
        job.SetEnabled(true, environment: "development");
        Assert.True(job.Environments["development"].Enabled);
        Assert.Same(devCfg, job.Environments["development"].Configuration);
    }

    [Fact]
    public void SetSchedule_Base_ReplacesBaseSchedule()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.SetSchedule("30 2 * * *");
        Assert.Equal("30 2 * * *", job.Schedule);
        Assert.Empty(job.Environments);
    }

    [Fact]
    public void SetSchedule_PerEnvironment_SetsOverrideAndPreservesBase()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.SetSchedule("0 3 * * *", environment: "production");
        // Base schedule is untouched; the override lands on the environment entry.
        Assert.Equal("0 * * * *", job.Schedule);
        Assert.Equal("0 3 * * *", job.Environments["production"].Schedule);
        // The override entry is created on demand, defaulting disabled.
        Assert.False(job.Environments["production"].Enabled);
    }

    [Fact]
    public void SetSchedule_PerEnvironment_ReusesExistingOverride()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        // Enable first, then schedule — the same override entry is reused so the
        // enablement survives.
        job.SetEnabled(true, environment: "production");
        job.SetSchedule("0 3 * * *", environment: "production");
        Assert.True(job.Environments["production"].Enabled);
        Assert.Equal("0 3 * * *", job.Environments["production"].Schedule);
    }

    [Fact]
    public void SetTimezone_Base_ReplacesBaseTimezone()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.SetTimezone("America/New_York");
        Assert.Equal("America/New_York", job.Timezone);
        Assert.Empty(job.Environments);
    }

    [Fact]
    public void SetTimezone_PerEnvironment_SetsOverrideAndPreservesBase()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        job.SetTimezone("America/Chicago");
        job.SetTimezone("Europe/London", environment: "production");
        // Base timezone is untouched; the override lands on the environment entry.
        Assert.Equal("America/Chicago", job.Timezone);
        Assert.Equal("Europe/London", job.Environments["production"].Timezone);
        // The override entry is created on demand, defaulting disabled.
        Assert.False(job.Environments["production"].Enabled);
    }

    [Fact]
    public void SetTimezone_PerEnvironment_ReusesExistingOverride()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.NewRecurringJob(JobId, name: "n", schedule: "0 * * * *", configuration: Cfg());
        // Set a per-env schedule first, then a timezone — the same override entry is
        // reused so the existing schedule survives.
        job.SetSchedule("0 3 * * *", environment: "production");
        job.SetTimezone("Europe/London", environment: "production");
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
        Assert.True(job.IsEnabled());
        Assert.True(job.IsEnabled(environment: "production"));
        Assert.False(job.IsEnabled(environment: "development"));

        // production: no config override → inherits base configuration, but it
        // carries a per-environment schedule override and a server-derived
        // next_run_at that the wrapper surfaces read-only.
        Assert.Null(job.Environments["production"].Configuration);
        Assert.Equal("https://api.example.com/hook", job.GetConfiguration(environment: "production").Url);
        Assert.Equal("0 3 * * *", job.Environments["production"].Schedule);
        // Base + per-environment timezone decode from the wire.
        Assert.Equal("America/New_York", job.Timezone);
        Assert.Equal("Europe/London", job.Environments["production"].Timezone);
        Assert.Equal(
            DateTimeOffset.Parse("2026-06-19T03:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            job.Environments["production"].NextRunAt);

        // development: per-environment config override present, no schedule override,
        // and next_run_at omitted (the environment is disabled).
        Assert.NotNull(job.Environments["development"].Configuration);
        Assert.Equal("https://dev.example.com/hook", job.GetConfiguration(environment: "development").Url);
        Assert.Null(job.Environments["development"].Schedule);
        Assert.Null(job.Environments["development"].Timezone);
        Assert.Null(job.Environments["development"].NextRunAt);

        // base + unknown environment fall back to the base configuration.
        Assert.Equal("https://api.example.com/hook", job.GetConfiguration().Url);
        Assert.Equal("https://api.example.com/hook", job.GetConfiguration(environment: "staging").Url);
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
        job.SetConfiguration(new HttpConfig { Url = "https://dev.example.com/hook" }, environment: "development");
        job.SetEnabled(false, environment: "development");
        job.SetEnabled(true, environment: "production");
        job.SetSchedule("0 3 * * *", environment: "production");
        job.SetTimezone("America/New_York");                     // base timezone
        job.SetTimezone("Europe/London", environment: "production"); // per-env override
        await job.SaveAsync();

        using var doc = JsonDocument.Parse(capturedBody!);
        var attrs = doc.RootElement.GetProperty("data").GetProperty("attributes");
        // Base read-only roll-ups are never written.
        Assert.False(attrs.TryGetProperty("enabled", out _));
        Assert.False(attrs.TryGetProperty("kind", out _));
        Assert.False(attrs.TryGetProperty("version", out _));
        // The base timezone is sent when set.
        Assert.Equal("America/New_York", attrs.GetProperty("timezone").GetString());
        // environments map is emitted; each entry carries its own enabled flag.
        var envs = attrs.GetProperty("environments");
        Assert.True(envs.GetProperty("production").GetProperty("enabled").GetBoolean());
        Assert.False(envs.GetProperty("development").GetProperty("enabled").GetBoolean());
        // production has no config override (inherits base); development does.
        Assert.False(envs.GetProperty("production").TryGetProperty("configuration", out _));
        Assert.Equal("https://dev.example.com/hook",
            envs.GetProperty("development").GetProperty("configuration").GetProperty("url").GetString());
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
        job.SetEnabled(true, environment: "production");
        job.SetEnabled(false, environment: "development");
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
