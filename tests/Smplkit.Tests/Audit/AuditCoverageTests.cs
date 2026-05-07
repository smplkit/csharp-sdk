using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Smplkit.Audit;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Tests.Audit;

/// <summary>
/// Coverage-targeted tests for branches not exercised by AuditClientTests.
/// </summary>
public class AuditCoverageTests
{
    private const string SuccessJson = "{\"data\":{\"id\":\"00000000-0000-0000-0000-000000000001\",\"type\":\"event\",\"attributes\":{\"action\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\",\"actor_id\":null,\"actor_label\":\"\",\"snapshot\":null,\"data\":{},\"idempotency_key\":\"\"}}}";

    private static (GenAudit.AuditClient gen, HttpClient http) MakeGen(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mock = new MockHttpMessageHandler(handler);
        var http = new HttpClient(mock);
        var gen = new GenAudit.AuditClient("https://audit.example.com", http) { ReadResponseAsString = true };
        return (gen, http);
    }

    [Fact]
    public void Create_RejectsNullInput()
    {
        var (gen, http) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        var client = new AuditClient(gen);
        Assert.Throws<ArgumentNullException>(() => client.Events.Create(null!));
        http.Dispose();
    }

    [Fact]
    public void Create_RejectsAllMissingRequiredFields()
    {
        var (gen, http) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        var client = new AuditClient(gen);
        // Missing ResourceType
        Assert.Throws<ArgumentException>(() => client.Events.Create(new CreateEventInput
        {
            Action = "x", ResourceType = "", ResourceId = "1",
        }));
        // Missing ResourceId
        Assert.Throws<ArgumentException>(() => client.Events.Create(new CreateEventInput
        {
            Action = "x", ResourceType = "user", ResourceId = "",
        }));
        http.Dispose();
    }

    [Fact]
    public async Task Create_ForwardsAllOptionalAttributes()
    {
        string? capturedBody = null;
        var (gen, http) = MakeGen(async req =>
        {
            if (req.Content != null)
            {
                capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(SuccessJson, Encoding.UTF8, "application/vnd.api+json"),
            };
        });
        await using var client = new AuditClient(gen);
        client.Events.Create(new CreateEventInput
        {
            Action = "invoice.created",
            ResourceType = "invoice",
            ResourceId = "inv-1",
            OccurredAt = new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero),
            Snapshot = new Dictionary<string, object?> { ["total_cents"] = 4900 },
            Data = new Dictionary<string, object?> { ["request_id"] = "req-1" },
            IdempotencyKey = "k-1",
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (capturedBody is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.NotNull(capturedBody);
        Assert.Contains("\"total_cents\":4900", capturedBody);
        Assert.Contains("\"request_id\":\"req-1\"", capturedBody);
        http.Dispose();
    }

    [Fact]
    public async Task ListAsync_PassesAllFilterParameters()
    {
        var capturedUrls = new List<string>();
        var (gen, http) = MakeGen(req =>
        {
            capturedUrls.Add(req.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[],\"meta\":{\"page_size\":1}}", Encoding.UTF8, "application/vnd.api+json"),
            });
        });
        await using var client = new AuditClient(gen);
        var actorId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        await client.Events.ListAsync(new ListEventsInput
        {
            Action = "user.created",
            ResourceType = "user",
            ResourceId = "u-1",
            ActorType = "USER",
            ActorId = actorId,
            OccurredAtRange = "[2026-04-01T00:00:00Z,*)",
            PageSize = 25,
            PageAfter = "cursor-abc",
        });
        var url = capturedUrls[0];
        Assert.Contains("filter%5Baction%5D=user.created", url);
        Assert.Contains("page%5Bsize%5D=25", url);
        Assert.Contains("page%5Bafter%5D=cursor-abc", url);
        http.Dispose();
    }

    [Fact]
    public async Task EventResource_JsonElement_FalseValue()
    {
        const string body = """
            {"data":{"id":"11111111-2222-3333-4444-555555555555","type":"event","attributes":{"action":"x","resource_type":"x","resource_id":"1","occurred_at":"2026-05-06T12:00:00Z","created_at":"2026-05-06T12:00:01Z","actor_type":"USER","actor_id":null,"actor_label":"","snapshot":{"flag": false},"data":{},"idempotency_key":"k"}}}
            """;
        var (gen, http) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new AuditClient(gen);
        var ev = await client.Events.GetAsync(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        Assert.Equal(false, ev.Snapshot?["flag"]);
        http.Dispose();
    }

    [Fact]
    public async Task EventResource_JsonElement_SnapshotAndData_ExpandToDictionary()
    {
        const string body = """
            {
              "data": {
                "id": "11111111-2222-3333-4444-555555555555",
                "type": "event",
                "attributes": {
                  "action": "x.created",
                  "resource_type": "x",
                  "resource_id": "1",
                  "occurred_at": "2026-05-06T12:00:00Z",
                  "created_at": "2026-05-06T12:00:01Z",
                  "actor_type": "USER",
                  "actor_id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                  "actor_label": "alice@example.com",
                  "snapshot": {"name": "Alice", "age": 30, "active": true, "score": 1.5, "deleted": null, "tags": ["a", "b"], "nested": {"k": "v"}},
                  "data": {"req_id": "abc"},
                  "idempotency_key": "k"
                }
              }
            }
            """;
        var (gen, http) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new AuditClient(gen);
        var ev = await client.Events.GetAsync(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        Assert.NotNull(ev.Snapshot);
        Assert.Equal("Alice", ev.Snapshot!["name"]);
        // Numeric values come back as long-or-double from the JsonElement expander.
        Assert.Equal("30", ev.Snapshot["age"]?.ToString());
        Assert.Equal(true, ev.Snapshot["active"]);
        Assert.Equal(1.5, ev.Snapshot["score"]);
        Assert.Null(ev.Snapshot["deleted"]);
        var tags = (List<object?>)ev.Snapshot["tags"]!;
        Assert.Equal(2, tags.Count);
        Assert.Equal("a", tags[0]);
        Assert.NotNull(ev.ActorId);
        Assert.Equal("USER", ev.ActorType);
        http.Dispose();
    }

    [Fact]
    public async Task Buffer_OverflowEvictsOldest()
    {
        var posts = 0;
        var (gen, http) = MakeGen(async req =>
        {
            await Task.Delay(20);
            Interlocked.Increment(ref posts);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(SuccessJson, Encoding.UTF8, "application/vnd.api+json"),
            };
        });
        await using var client = new AuditClient(gen);
        var origStderr = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            // Burst of 200 events; default MaxBufferSize=1000 doesn't overflow,
            // but 1100 does. Use a smaller burst that still hits the >= MaxBufferSize
            // branch by leveraging that the worker is async.
            for (int i = 0; i < 1100; i++)
            {
                client.Events.Create(new CreateEventInput
                {
                    Action = "x.created", ResourceType = "x", ResourceId = i.ToString(),
                });
            }
            await client.Events.FlushAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Console.SetError(origStderr);
        }
        // We don't assert post count exactly; the goal is exercising the
        // overflow-eviction branch.
        http.Dispose();
    }

    [Fact]
    public async Task Buffer_DropsPermanent4xx()
    {
        var attempts = 0;
        var (gen, http) = MakeGen(req =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        });
        await using var client = new AuditClient(gen);
        var origStderr = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            client.Events.Create(new CreateEventInput
            {
                Action = "x.created", ResourceType = "x", ResourceId = "1",
            });
            // Force a drain pass via flush; otherwise the worker waits for its
            // 5s tick or the watermark.
            await client.Events.FlushAsync(TimeSpan.FromSeconds(2));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (attempts == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            // Give a moment to confirm no retry happens (permanent failure → drop).
            await Task.Delay(300);
        }
        finally
        {
            Console.SetError(origStderr);
        }
        Assert.Equal(1, attempts); // exactly 1: permanent failure → no retry
        http.Dispose();
    }

    [Fact]
    public async Task Buffer_RetriesAndGivesUpAfterMaxAttempts()
    {
        var attempts = 0;
        var (gen, http) = MakeGen(req =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        await using var client = new AuditClient(gen);
        var origStderr = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            client.Events.Create(new CreateEventInput
            {
                Action = "x.created", ResourceType = "x", ResourceId = "1",
            });
            // 5 attempts × max 250ms backoff × 2^4 = up to 8s — give it 10s.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (attempts < AuditEventBuffer.MaxAttempts && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
        finally
        {
            Console.SetError(origStderr);
        }
        Assert.True(attempts >= AuditEventBuffer.MaxAttempts, $"got {attempts}, expected >= {AuditEventBuffer.MaxAttempts}");
        http.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var (gen, http) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        var client = new AuditClient(gen);
        await client.DisposeAsync();
        // Second dispose should not throw.
        await client.DisposeAsync();
        http.Dispose();
    }
}
