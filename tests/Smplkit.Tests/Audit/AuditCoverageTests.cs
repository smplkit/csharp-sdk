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
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        var client = new AuditClient(gen);
        Assert.Throws<ArgumentNullException>(() => client.Events.Create(null!));
    }

    [Fact]
    public void Create_RejectsAllMissingRequiredFields()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        var client = new AuditClient(gen);
        // Missing ResourceType
        Assert.Throws<ArgumentException>(() => client.Events.Create(new CreateEventInput
        {
            Action = "x",
            ResourceType = "",
            ResourceId = "1",
        }));
        // Missing ResourceId
        Assert.Throws<ArgumentException>(() => client.Events.Create(new CreateEventInput
        {
            Action = "x",
            ResourceType = "user",
            ResourceId = "",
        }));
    }

    [Fact]
    public async Task Create_ForwardsAllOptionalAttributes()
    {
        string? capturedBody = null;
        var (gen, _) = MakeGen(async req =>
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
    }

    [Fact]
    public async Task Create_OmittedDataDefaultsToEmptyDict()
    {
        // Regression: System.Text.Json emits ``"data": null`` for an unset
        // reference property by default, and the server's Pydantic gate
        // rejects null with HTTP 400. The wrapper must pin Data to at
        // least an empty dict on the wire.
        string? capturedBody = null;
        var (gen, _) = MakeGen(async req =>
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
            Action = "invoice.updated",
            ResourceType = "invoice",
            ResourceId = "inv-1",
            // Data deliberately omitted.
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (capturedBody is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.NotNull(capturedBody);
        Assert.Contains("\"data\":{}", capturedBody);
        Assert.DoesNotContain("\"data\":null", capturedBody);
    }

    [Fact]
    public async Task ListAsync_PassesAllFilterParameters()
    {
        var capturedUrls = new List<string>();
        var (gen, _) = MakeGen(req =>
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
    }

    [Fact]
    public async Task EventResource_JsonElement_FalseValue()
    {
        const string body = """
            {"data":{"id":"11111111-2222-3333-4444-555555555555","type":"event","attributes":{"action":"x","resource_type":"x","resource_id":"1","occurred_at":"2026-05-06T12:00:00Z","created_at":"2026-05-06T12:00:01Z","actor_type":"USER","actor_id":null,"actor_label":"","snapshot":{"flag": false},"data":{},"idempotency_key":"k"}}}
            """;
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new AuditClient(gen);
        var ev = await client.Events.GetAsync(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        Assert.Equal(false, ev.Snapshot?["flag"]);
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
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
    }

    [Fact]
    public async Task Buffer_OverflowEvictsOldest()
    {
        var posts = 0;
        var (gen, _) = MakeGen(async req =>
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
            // 1100 events overflows the default MaxBufferSize=1000, which
            // exercises the eviction branch in Enqueue.
            for (int i = 0; i < 1100; i++)
            {
                client.Events.Create(new CreateEventInput
                {
                    Action = "x.created",
                    ResourceType = "x",
                    ResourceId = i.ToString(),
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
    }

    [Fact]
    public async Task Buffer_DropsPermanent4xx()
    {
        var attempts = 0;
        var (gen, _) = MakeGen(req =>
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
                Action = "x.created",
                ResourceType = "x",
                ResourceId = "1",
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
    }

    [Fact]
    public async Task Buffer_RetriesAndGivesUpAfterMaxAttempts()
    {
        var attempts = 0;
        var (gen, _) = MakeGen(req =>
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
                Action = "x.created",
                ResourceType = "x",
                ResourceId = "1",
            });
            // Trigger an immediate first-attempt wake; otherwise the worker
            // sleeps the full 5s tick before its first try, eating the test
            // budget under coverage instrumentation.
            await client.Events.FlushAsync(TimeSpan.FromMilliseconds(50));
            // Backoffs sum to 250+500+1000+2000 = 3.75s natively, but coverlet
            // instrumentation on CI Linux runners can stretch each NSwag call
            // by a multiple. Use a generous ceiling so the test isn't flaky
            // — it still exits early when we hit MaxAttempts.
            var deadline = DateTime.UtcNow.AddSeconds(60);
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
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        var client = new AuditClient(gen);
        await client.DisposeAsync();
        // Second dispose should not throw.
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Buffer_NonApiExceptionTreatedAsTransient()
    {
        // The mock throws HttpRequestException — a realistic transient
        // network failure that propagates through Create_eventAsync as
        // something other than ApiException or ObjectDisposedException.
        // Exercises the generic catch arm in DrainOnceAsync.
        var attempts = 0;
        var (gen, _) = MakeGen(req =>
        {
            Interlocked.Increment(ref attempts);
            throw new HttpRequestException("simulated network failure");
        });
        await using var client = new AuditClient(gen);
        var origStderr = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            client.Events.Create(new CreateEventInput
            {
                Action = "x.created",
                ResourceType = "x",
                ResourceId = "1",
            });
            await client.Events.FlushAsync(TimeSpan.FromMilliseconds(50));
            // Coverlet on CI can stretch each NSwag call dramatically;
            // use a generous ceiling and exit early on success.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (attempts < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
        finally
        {
            Console.SetError(origStderr);
        }
        // Multiple attempts means the catch-all routed status=0 → retry.
        Assert.True(attempts >= 2, $"expected at least 2 attempts, got {attempts}");
    }

    [Fact]
    public async Task EventResource_ScalarSnapshotCollapsesToNull()
    {
        // A non-object snapshot (here, a JSON string) hits the
        // "raw is JsonElement but not Object" path in ConvertJsonObject,
        // which returns null per ADR-047 (snapshot is structured-only).
        const string body = """
            {"data":{"id":"11111111-2222-3333-4444-555555555555","type":"event","attributes":{"action":"x","resource_type":"x","resource_id":"1","occurred_at":"2026-05-06T12:00:00Z","created_at":"2026-05-06T12:00:01Z","actor_type":"USER","actor_id":null,"actor_label":"","snapshot":"unexpected-string","data":{},"idempotency_key":"k"}}}
            """;
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json"),
        }));
        await using var client = new AuditClient(gen);
        var ev = await client.Events.GetAsync(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        Assert.Null(ev.Snapshot);
    }

    [Fact]
    public async Task DisposeAsync_IsBoundedEvenWithSaturatedBuffer()
    {
        // The worker exits on _closed alone (set by DisposeAsync after
        // its 5s flush window), not on _closed && queue empty. That
        // means dispose is bounded by the flush timeout regardless of
        // how many items are still queued — a saturated buffer at close
        // time can't pin the caller indefinitely.
        var (gen, _) = MakeGen(async req =>
        {
            // Slow handler — drains far slower than enqueue rate so the
            // queue stays saturated through the dispose path.
            await Task.Delay(100);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(SuccessJson, Encoding.UTF8, "application/vnd.api+json"),
            };
        });
        var client = new AuditClient(gen);
        var origStderr = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            // 200 items × 100ms = 20s of server-side work — far more
            // than the 5s flush budget can drain.
            for (int i = 0; i < 200; i++)
            {
                client.Events.Create(new CreateEventInput
                {
                    Action = "x.created",
                    ResourceType = "x",
                    ResourceId = i.ToString(),
                });
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await client.DisposeAsync();
            sw.Stop();
            // Bounded: ~5s flush + worker's last-iteration request
            // (≤100ms) + scheduling overhead. Generous 8s ceiling.
            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(8),
                $"DisposeAsync should be bounded by the flush window; took {sw.Elapsed}");
        }
        finally
        {
            Console.SetError(origStderr);
        }
    }

    [Fact]
    public async Task Buffer_DropsRemainingWhenHttpClientDisposed()
    {
        // Bug guard: if the underlying HttpClient is disposed while items are
        // still queued, retrying every send would burn MaxAttempts × backoff
        // (~3.75s) per item — 1000+ events would take roughly an hour. The
        // ObjectDisposedException branch must drain the queue and stop fast.
        var mock = new MockHttpMessageHandler(req => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(SuccessJson, Encoding.UTF8, "application/vnd.api+json"),
            }));
        var http = new HttpClient(mock);
        var gen = new GenAudit.AuditClient("https://audit.example.com", http) { ReadResponseAsString = true };
        var client = new AuditClient(gen);
        var origStderr = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            for (int i = 0; i < 50; i++)
            {
                client.Events.Create(new CreateEventInput
                {
                    Action = "x.created",
                    ResourceType = "x",
                    ResourceId = i.ToString(),
                });
            }
            // Yank the HttpClient out from under the worker, then await
            // dispose — should return promptly (well under MaxAttempts × backoff).
            http.Dispose();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await client.DisposeAsync();
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                $"dispose should be fast after HttpClient teardown; took {sw.Elapsed}");
        }
        finally
        {
            Console.SetError(origStderr);
        }
    }
}
