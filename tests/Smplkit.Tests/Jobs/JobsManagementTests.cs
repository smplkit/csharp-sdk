using System.Net;
using System.Text;
using Smplkit.Errors;
using Smplkit.Jobs;
using Smplkit.Management;
using Smplkit.Tests.Helpers;
using Xunit;
using GenJobs = Smplkit.Internal.Generated.Jobs;
using HttpMethod = Smplkit.Jobs.HttpMethod;

namespace Smplkit.Tests.Jobs;

/// <summary>
/// Tests for the management-plane Smpl Jobs wrapper (<c>mgmt.Jobs</c>).
///
/// <para>Stubs the jobs service via <see cref="MockHttpMessageHandler"/>; no real
/// network. Coverage on the wrapper must reach 100% to satisfy the SDK CI gate.
/// Exercises the active-record API: <c>mgmt.Jobs.New(...)</c> → mutate →
/// <c>SaveAsync</c> / <c>DeleteAsync</c>, plus run history and usage.</para>
/// </summary>
public class JobsManagementTests
{
    private const string JobId = "my-job";
    private const string RunId = "8f2b1c4a-0000-4a1b-9c3d-1e2f3a4b5c6d";

    private static (GenJobs.JobsClient gen, MockHttpMessageHandler mock) MakeGen(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mock = new MockHttpMessageHandler(handler);
        var http = new HttpClient(mock);
        var gen = new GenJobs.JobsClient("https://jobs.example.com", http) { ReadResponseAsString = true };
        return (gen, mock);
    }

    private static JobsManagementClient MakeJobs(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var (gen, _) = MakeGen(handler);
        return new JobsManagementClient(gen);
    }

    private static StringContent JsonApi(string body) =>
        new(body, Encoding.UTF8, "application/vnd.api+json");

    private static string JobResource(
        string id = JobId, string name = "My Job", int version = 1, bool enabled = true, bool created = true)
    {
        var ts = created ? "\"2026-06-04T00:00:00Z\"" : "null";
        return "{\"id\":\"" + id + "\",\"type\":\"job\",\"attributes\":{"
            + "\"name\":\"" + name + "\",\"description\":\"does a thing\","
            + "\"enabled\":" + (enabled ? "true" : "false") + ",\"type\":\"http\","
            + "\"schedule\":\"0 * * * *\","
            + "\"configuration\":{\"method\":\"POST\",\"url\":\"https://api.example.com/hook\","
            + "\"headers\":[{\"name\":\"X-Api-Key\",\"value\":\"secret\"}],"
            + "\"body\":\"{}\",\"success_status\":\"2xx\",\"timeout\":30,"
            + "\"tls_verify\":true,\"ca_cert\":null},"
            + "\"concurrency_policy\":\"ALLOW\","
            + "\"next_run_at\":\"2026-06-05T00:00:00Z\","
            + "\"created_at\":" + ts + ",\"updated_at\":" + ts + ",\"deleted_at\":null,"
            + "\"version\":" + version + "}}";
    }

    private static string RunResource(
        string id = RunId, string status = "SUCCEEDED", string trigger = "SCHEDULE", string? rerunOf = null)
    {
        var ro = rerunOf is null ? "null" : "\"" + rerunOf + "\"";
        return "{\"id\":\"" + id + "\",\"type\":\"run\",\"attributes\":{"
            + "\"job\":\"" + JobId + "\",\"job_version\":1,"
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
        var job = jobs.New(JobId, name: "My Job", schedule: "0 * * * *", configuration: Cfg());
        Assert.Equal(JobId, job.Id);
        Assert.Null(job.CreatedAt);
        Assert.Equal(0, calls);
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

        var job = jobs.New(JobId, name: "My Job", schedule: "0 * * * *", configuration: Cfg(), description: "d");
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
        Assert.NotNull(job.NextRunAt);
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
        var rows = await jobs.ListAsync(enabled: true, pageNumber: 1, pageSize: 10);
        Assert.Equal(2, rows.Count);
        Assert.Contains("filter%5Benabled%5D=true", url!);
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
    public async Task Run_TriggersManualRun()
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
    // Runs sub-client
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
    public async Task Runs_Get_ReturnsRun()
    {
        var run = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + RunResource() + "}"),
        })).Runs.GetAsync(RunId);
        Assert.Equal(RunId, run.Id);
        Assert.Equal("SUCCEEDED", run.Status);
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
        var job = jobs.New("dup", name: "D", schedule: "now", configuration: Cfg());
        await Assert.ThrowsAsync<ConflictException>(() => job.SaveAsync());
    }

    // ----------------------------------------------------------------------
    // Wire mapping edge cases + enums
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Get_MinimalConfiguration_DefaultsApplied()
    {
        // No configuration block, no version → defaults flow through.
        var job = await MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + JobId + "\",\"type\":\"job\",\"attributes\":{"
                + "\"name\":\"x\",\"schedule\":\"now\",\"enabled\":true,\"type\":\"http\","
                + "\"concurrency_policy\":\"ALLOW\"}}}"),
        })).GetAsync(JobId);
        Assert.Equal(string.Empty, job.Configuration.Url);
        Assert.Empty(job.Configuration.Headers);
        Assert.Equal(HttpMethod.Post, job.Configuration.Method);
        Assert.True(job.Configuration.TlsVerify);
        Assert.Null(job.Version);
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
        var job = jobs.New(JobId, name: "n", schedule: "now", configuration: cfg);
        await job.SaveAsync();
        Assert.Contains($"\"method\":\"{wire}\"", capturedBody!);
    }

    [Fact]
    public async Task HttpMethod_ToGenOutOfRange_Throws()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var cfg = Cfg();
        cfg.Method = (HttpMethod)999;
        var job = jobs.New(JobId, name: "n", schedule: "now", configuration: cfg);
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
        var job = jobs.New(JobId, name: "n", schedule: "now", configuration: cfg);
        await job.SaveAsync();
        Assert.Contains("\"tls_verify\":false", capturedBody);
        Assert.Contains("\"ca_cert\":\"-----BEGIN CERTIFICATE-----", capturedBody);
    }

    // ----------------------------------------------------------------------
    // ToString + model coverage
    // ----------------------------------------------------------------------

    [Fact]
    public void Job_ToString_IncludesIdNameEnabled()
    {
        var jobs = MakeJobs(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var job = jobs.New(JobId, name: "n", schedule: "now", configuration: Cfg());
        var s = job.ToString();
        Assert.Contains("Id=my-job", s);
        Assert.Contains("Name=n", s);
        Assert.Contains("Enabled=True", s);
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
    /// SaveAsync / DeleteAsync guard branches.</summary>
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
                true,           // enabled
                "http",         // type
                "ALLOW",        // concurrencyPolicy
                null,           // nextRunAt
                null,           // createdAt
                null,           // updatedAt
                null,           // deletedAt
                null,           // version
            },
            culture: null)!;
}
