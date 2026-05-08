using System.Net;
using System.Text;
using System.Threading;
using Smplkit.Audit;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Tests.Audit;

public class AuditClientTests
{
    private const string SuccessJson = "{\"data\":{\"id\":\"00000000-0000-0000-0000-000000000001\",\"type\":\"event\",\"attributes\":{\"action\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\",\"actor_id\":null,\"actor_label\":\"\",\"data\":{},\"idempotency_key\":\"\"}}}";

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
        await using var client = new AuditClient(gen);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            client.Events.Record(new CreateEventInput
            {
                Action = "user.created",
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
        var client = new AuditClient(gen);
        Assert.Throws<ArgumentException>(() => client.Events.Record(new CreateEventInput
        {
            Action = "",
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
        await using var client = new AuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            Action = "user.created",
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
    public async Task GetAsync_RoundTripsAnEvent()
    {
        var eventId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var (gen, _, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"data\":{\"id\":\"11111111-2222-3333-4444-555555555555\",\"type\":\"event\",\"attributes\":{\"action\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\",\"actor_id\":null,\"actor_label\":\"\",\"data\":{},\"idempotency_key\":\"k\"}}}",
                Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new AuditClient(gen);

        var ev = await client.Events.GetAsync(eventId);
        Assert.Equal(eventId, ev.Id);
        Assert.Equal("x.created", ev.Action);
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
                "{\"data\":[{\"id\":\"11111111-2222-3333-4444-555555555555\",\"type\":\"event\",\"attributes\":{\"action\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\",\"actor_id\":null,\"actor_label\":\"\",\"data\":{},\"idempotency_key\":\"k\"}}],\"meta\":{\"page_size\":1},\"links\":{\"next\":\"/api/v1/events?page[size]=1&page[after]=tok-xyz\"}}",
                Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new AuditClient(gen);

        var page = await client.Events.ListAsync(new ListEventsInput { PageSize = 1 });
        Assert.Single(page.Events);
        Assert.Equal("tok-xyz", page.NextCursor);
    }

    [Fact]
    public async Task GetAsync_404_ThrowsApiException()
    {
        var (gen, _, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var client = new AuditClient(gen);

        await Assert.ThrowsAsync<GenAudit.ApiException>(
            () => client.Events.GetAsync(Guid.NewGuid()));
    }
}
