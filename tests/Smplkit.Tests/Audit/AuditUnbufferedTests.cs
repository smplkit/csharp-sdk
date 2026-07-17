using System.Net;
using System.Reflection;
using System.Text;
using Smplkit.Audit;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;
using HttpMethod = System.Net.Http.HttpMethod;

namespace Smplkit.Tests.Audit;

/// <summary>
/// Tests for the unbuffered audit mode (<c>buffered: false</c>): Record performs
/// one awaited POST per call and raises typed exceptions, Flush/Dispose are
/// no-ops, and no background buffer or worker is ever created.
/// </summary>
public class AuditUnbufferedTests
{
    private const string SuccessJson = "{\"data\":{\"id\":\"00000000-0000-0000-0000-000000000001\",\"type\":\"event\",\"attributes\":{\"event_type\":\"x.created\",\"resource_type\":\"x\",\"resource_id\":\"1\",\"occurred_at\":\"2026-05-06T12:00:00Z\",\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\",\"actor_id\":null,\"actor_label\":\"\",\"data\":{},\"idempotency_key\":\"\"}}}";

    private static (GenAudit.AuditClient gen, MockHttpMessageHandler mock) MakeGen(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mock = new MockHttpMessageHandler(handler);
        var http = new HttpClient(mock);
        var gen = new GenAudit.AuditClient("https://audit.example.com", http) { ReadResponseAsString = true };
        return (gen, mock);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.Created)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private static object? BufferOf(AuditEvents events)
        => typeof(AuditEvents).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(events);

    [Fact]
    public void Record_Unbuffered_PostsInline_BeforeReturning()
    {
        var (gen, mock) = MakeGen(_ => Task.FromResult(Json(SuccessJson)));
        var events = new AuditEvents(gen, "production", buffered: false);
        Assert.Null(BufferOf(events));

        events.Record(new CreateEventInput
        {
            EventType = "user.created",
            ResourceType = "user",
            ResourceId = "u-1",
            IdempotencyKey = "idem-1",
        });

        // The POST completed before Record returned — no drain, no polling.
        var req = Assert.Single(mock.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.EndsWith("/api/v1/events", req.RequestUri!.AbsolutePath);
        Assert.Contains("idem-1", req.Headers.GetValues("Idempotency-Key"));
    }

    [Fact]
    public void Record_Unbuffered_FlushArgument_IsIgnored()
    {
        var (gen, mock) = MakeGen(_ => Task.FromResult(Json(SuccessJson)));
        var events = new AuditEvents(gen, environment: null, buffered: false);

        // flush is meaningless in unbuffered mode — the event is already
        // durable on return; exactly one POST, no buffer-drain machinery.
        events.Record(new CreateEventInput
        {
            EventType = "user.created",
            ResourceType = "user",
            ResourceId = "u-1",
            Flush = true,
        });

        Assert.Single(mock.Requests);
    }

    [Fact]
    public void Record_Unbuffered_Throws_TypedException_OnFailure()
    {
        var (gen, _) = MakeGen(_ => Task.FromResult(Json(
            "{\"errors\":[{\"status\":\"404\",\"title\":\"Not Found\",\"detail\":\"nope\"}]}",
            HttpStatusCode.NotFound)));
        var events = new AuditEvents(gen, null, buffered: false);

        var ex = Assert.ThrowsAny<SmplkitException>(() => events.Record(new CreateEventInput
        {
            EventType = "user.created",
            ResourceType = "user",
            ResourceId = "u-1",
        }));
        Assert.IsType<NotFoundException>(ex);
    }

    [Fact]
    public async Task FlushAsync_Unbuffered_IsNoOp()
    {
        var (gen, mock) = MakeGen(_ => Task.FromResult(Json(SuccessJson)));
        var events = new AuditEvents(gen, null, buffered: false);

        await events.FlushAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(mock.Requests);
    }

    [Fact]
    public async Task DisposeAsync_Unbuffered_IsNoOp()
    {
        var (gen, mock) = MakeGen(_ => Task.FromResult(Json(SuccessJson)));
        var events = new AuditEvents(gen, null, buffered: false);

        await events.DisposeAsync();
        Assert.Empty(mock.Requests);
    }

    [Fact]
    public async Task AuditClient_Standalone_BufferedFalse_CreatesNoBuffer()
    {
        await using var audit = new AuditClient(
            apiKey: "sk_test", baseDomain: "example.test", buffered: false);
        Assert.Null(BufferOf(audit.Events));
    }

    [Fact]
    public async Task AuditClient_Standalone_BufferedDefault_CreatesBuffer()
    {
        await using var audit = new AuditClient(
            apiKey: "sk_test", baseDomain: "example.test");
        Assert.NotNull(BufferOf(audit.Events));
    }
}
