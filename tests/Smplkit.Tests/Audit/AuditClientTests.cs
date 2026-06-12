using System.Net;
using System.Text;
using System.Threading;
using Smplkit.Audit;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Tests.Audit;

public class AuditClientTests
{
    private const string SuccessJson = "{\"data\":{\"id\":\"00000000-0000-0000-0000-000000000001\",\"type\":\"event\",\"attributes\":{\"event_type\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\",\"actor_id\":null,\"actor_label\":\"\",\"data\":{},\"idempotency_key\":\"\"}}}";

    private static (GenAudit.AuditClient gen, HttpClient http, MockHttpMessageHandler mock) MakeGen(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mock = new MockHttpMessageHandler(handler);
        var http = new HttpClient(mock);
        var gen = new GenAudit.AuditClient("https://audit.example.com", http) { ReadResponseAsString = true };
        return (gen, http, mock);
    }

    [Fact]
    public async Task Create_ReturnsImmediately_ThenPostsInBackground()
    {
        var posts = 0;
        var (gen, _, _) = MakeGen(async req =>
        {
            await Task.Delay(50).ConfigureAwait(false);
            Interlocked.Increment(ref posts);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(SuccessJson, Encoding.UTF8, "application/vnd.api+json"),
            };
        });
        await using var client = new TestAuditClient(gen);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            client.Events.Record(new CreateEventInput
            {
                EventType = "user.created",
                ResourceType = "user",
                ResourceId = $"u-{i}",
            });
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(200), $"create should return quickly; took {sw.Elapsed}");

        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));
        // Drain may return as soon as queue is empty (item in flight).
        // Wait until the handler has actually run.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (posts == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(posts >= 1);
    }

    [Fact]
    public void Create_RejectsMissingFields()
    {
        var (gen, _, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        var client = new TestAuditClient(gen);
        Assert.Throws<ArgumentException>(() => client.Events.Record(new CreateEventInput
        {
            EventType = "",
            ResourceType = "user",
            ResourceId = "u-1",
        }));
    }

    [Fact]
    public async Task Create_PassesIdempotencyKeyHeader()
    {
        string? capturedKey = null;
        var (gen, _, _) = MakeGen(req =>
        {
            if (req.Headers.TryGetValues("Idempotency-Key", out var values))
            {
                capturedKey = values.FirstOrDefault();
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(SuccessJson, Encoding.UTF8, "application/vnd.api+json"),
            });
        });
        await using var client = new TestAuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            EventType = "user.created",
            ResourceType = "user",
            ResourceId = "u-1",
            IdempotencyKey = "key-abc",
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (capturedKey is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.Equal("key-abc", capturedKey);
    }

    [Fact]
    public async Task Record_ForwardsCustomerSuppliedActorFields()
    {
        string? capturedBody = null;
        var (gen, _, _) = MakeGen(async req =>
        {
            if (req.Content is not null)
            {
                capturedBody = await req.Content.ReadAsStringAsync();
            }
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(SuccessJson, Encoding.UTF8, "application/vnd.api+json"),
            };
        });
        await using var client = new TestAuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            EventType = "user.created",
            ResourceType = "user",
            ResourceId = "u-1",
            ActorType = "EXTERNAL_SERVICE",
            ActorId = "not-a-uuid:billing-bot",
            ActorLabel = "Billing Bot",
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (capturedBody is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.NotNull(capturedBody);
        Assert.Contains("\"actor_type\":\"EXTERNAL_SERVICE\"", capturedBody);
        Assert.Contains("\"actor_id\":\"not-a-uuid:billing-bot\"", capturedBody);
        Assert.Contains("\"actor_label\":\"Billing Bot\"", capturedBody);
    }

    [Fact]
    public async Task GetAsync_RoundTripsAnEvent()
    {
        var eventId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var (gen, _, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":{\"id\":\"11111111-2222-3333-4444-555555555555\",\"type\":\"event\",\"attributes\":{\"event_type\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\",\"actor_id\":null,\"actor_label\":\"\",\"data\":{},\"idempotency_key\":\"k\"}}}",
                Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new TestAuditClient(gen);

        var ev = await client.Events.GetAsync(eventId);
        Assert.Equal(eventId, ev.Id);
        Assert.Equal("x.created", ev.EventType);
        Assert.Equal("x", ev.ResourceType);
        Assert.Equal("1", ev.ResourceId);
        Assert.Equal(DateTimeOffset.Parse("2026-05-06T12:00:00Z"), ev.OccurredAt);
        Assert.Equal(DateTimeOffset.Parse("2026-05-06T12:00:01Z"), ev.CreatedAt);
        Assert.Equal("API_KEY", ev.ActorType);
        Assert.Null(ev.ActorId);
        Assert.Equal(string.Empty, ev.ActorLabel);
        Assert.Empty(ev.Data);
        Assert.Equal("k", ev.IdempotencyKey);
    }

    [Fact]
    public async Task ListAsync_ParsesNextCursor()
    {
        var (gen, _, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":[{\"id\":\"11111111-2222-3333-4444-555555555555\",\"type\":\"event\",\"attributes\":{\"event_type\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\",\"actor_id\":null,\"actor_label\":\"\",\"data\":{},\"idempotency_key\":\"k\"}}],\"meta\":{\"page_size\":1},\"links\":{\"next\":\"/api/v1/events?page[size]=1&page[after]=tok-xyz\"}}",
                Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new TestAuditClient(gen);

        var page = await client.Events.ListAsync(new ListEventsInput { PageSize = 1 });
        Assert.Single(page.Events);
        Assert.Equal("tok-xyz", page.NextCursor);
    }

    [Fact]
    public async Task ListAsync_EnvironmentsFilter_JoinsAndSendsQueryParam()
    {
        string? capturedUrl = null;
        var (gen, _, _) = MakeGen(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[],\"meta\":{\"page_size\":1}}",
                    Encoding.UTF8, "application/vnd.api+json"),
            });
        });
        await using var client = new TestAuditClient(gen);

        var page = await client.Events.ListAsync(new ListEventsInput
        {
            Environments = new[] { "production", "staging" },
        });
        Assert.Empty(page.Events);
        Assert.NotNull(capturedUrl);
        // filter[environment]=production,staging — bracket + comma are URL-encoded.
        Assert.Contains("filter%5Benvironment%5D=production%2Cstaging", capturedUrl!);
    }

    [Fact]
    public async Task ListAsync_EmptyEnvironments_OmitsQueryParam()
    {
        string? capturedUrl = null;
        var (gen, _, _) = MakeGen(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[],\"meta\":{\"page_size\":1}}",
                    Encoding.UTF8, "application/vnd.api+json"),
            });
        });
        await using var client = new TestAuditClient(gen);

        await client.Events.ListAsync(new ListEventsInput
        {
            Environments = Array.Empty<string>(),
        });
        Assert.NotNull(capturedUrl);
        Assert.DoesNotContain("filter%5Benvironment%5D", capturedUrl!);
    }

    [Fact]
    public async Task GetAsync_404_ThrowsNotFoundException()
    {
        var (gen, _, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var client = new TestAuditClient(gen);

        await Assert.ThrowsAsync<NotFoundException>(
            () => client.Events.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetAsync_ReturnsDoNotForwardTrue()
    {
        var eventId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var (gen, _, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":{\"id\":\"11111111-2222-3333-4444-555555555555\",\"type\":\"event\",\"attributes\":{\"event_type\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\",\"actor_id\":null,\"actor_label\":\"\",\"data\":{},\"idempotency_key\":\"\",\"do_not_forward\":true}}}",
                Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new TestAuditClient(gen);

        var ev = await client.Events.GetAsync(eventId);
        Assert.True(ev.DoNotForward);
    }

    [Fact]
    public async Task GetAsync_ReadsEnvironmentField()
    {
        var eventId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var (gen, _, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":{\"id\":\"11111111-2222-3333-4444-555555555555\",\"type\":\"event\",\"attributes\":{\"event_type\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"data\":{},\"idempotency_key\":\"\",\"environment\":\"production\"}}}",
                Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new TestAuditClient(gen);

        var ev = await client.Events.GetAsync(eventId);
        Assert.Equal("production", ev.Environment);
    }

    [Fact]
    public async Task GetAsync_EnvironmentNull_WhenWireOmitsIt()
    {
        var eventId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var (gen, _, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SuccessJson.Replace(
                "00000000-0000-0000-0000-000000000001",
                "11111111-2222-3333-4444-555555555555"),
                Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new TestAuditClient(gen);

        var ev = await client.Events.GetAsync(eventId);
        Assert.Null(ev.Environment);
    }

    [Fact]
    public async Task RuntimeEnvironment_StampsHeaderOnRecord()
    {
        string? capturedEnv = null;
        var (gen, _, _) = MakeGen(req =>
        {
            if (req.Headers.TryGetValues("X-Smplkit-Environment", out var v))
                capturedEnv = v.FirstOrDefault();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(SuccessJson, Encoding.UTF8, "application/vnd.api+json"),
            });
        });
        gen.RuntimeEnvironment = "production";
        await using var client = new TestAuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            EventType = "user.created",
            ResourceType = "user",
            ResourceId = "u-1",
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (capturedEnv is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.Equal("production", capturedEnv);
    }

    [Fact]
    public async Task RuntimeEnvironment_StampsHeaderOnListAndGet()
    {
        var seen = new List<string?>();
        var (gen, _, _) = MakeGen(req =>
        {
            seen.Add(req.Headers.TryGetValues("X-Smplkit-Environment", out var v) ? v.FirstOrDefault() : null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[],\"meta\":{\"page_size\":1}}",
                    Encoding.UTF8, "application/vnd.api+json"),
            });
        });
        gen.RuntimeEnvironment = "staging";
        await using var client = new TestAuditClient(gen);

        await client.Events.ListAsync();
        Assert.Contains("staging", seen);
    }

    [Fact]
    public async Task RuntimeEnvironment_Null_DoesNotStampHeader()
    {
        bool hadHeader = true;
        var (gen, _, _) = MakeGen(req =>
        {
            hadHeader = req.Headers.Contains("X-Smplkit-Environment");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[],\"meta\":{\"page_size\":1}}",
                    Encoding.UTF8, "application/vnd.api+json"),
            });
        });
        // RuntimeEnvironment left null (account-global behavior).
        await using var client = new TestAuditClient(gen);

        await client.Events.ListAsync();
        Assert.False(hadHeader);
    }

    [Fact]
    public async Task RuntimeEnvironment_DoesNotOverrideCallerSuppliedHeader()
    {
        // Explicit X-Smplkit-Environment on the shared HttpClient default
        // headers wins over the client-level stamp.
        string? capturedEnv = null;
        var mock = new MockHttpMessageHandler(req =>
        {
            if (req.Headers.TryGetValues("X-Smplkit-Environment", out var v))
                capturedEnv = v.FirstOrDefault();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[],\"meta\":{\"page_size\":1}}",
                    Encoding.UTF8, "application/vnd.api+json"),
            });
        });
        var http = new HttpClient(mock);
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Smplkit-Environment", "explicit-env");
        var gen = new GenAudit.AuditClient("https://audit.example.com", http)
        {
            ReadResponseAsString = true,
            RuntimeEnvironment = "configured-env",
        };
        await using var client = new TestAuditClient(gen);

        await client.Events.ListAsync();
        Assert.Equal("explicit-env", capturedEnv);
    }

    [Fact]
    public async Task Record_DoNotForward_IncludesFlagInPayload()
    {
        string? capturedBody = null;
        var (gen, _, _) = MakeGen(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(SuccessJson, Encoding.UTF8, "application/vnd.api+json"),
            };
        });
        await using var client = new TestAuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            EventType = "invoice.created",
            ResourceType = "invoice",
            ResourceId = "u-1",
            DoNotForward = true,
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (capturedBody is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.NotNull(capturedBody);
        Assert.Contains("do_not_forward", capturedBody!);
    }
}
