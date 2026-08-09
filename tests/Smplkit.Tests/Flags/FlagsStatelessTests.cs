using System.Net;
using System.Reflection;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Internal;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Tests for the stateless flags mode (<c>streaming: false</c>): the first live
/// call fetches once with no event stream and no periodic flush timer, discovery and
/// context threshold flushes run inline, and RefreshAsync re-fetches on demand.
/// </summary>
public class FlagsStatelessTests
{
    private static readonly Func<EventStream> ThrowingEvents =
        () => throw new InvalidOperationException("stateless mode must not create an event stream");

    private static (FlagsClient flags, MockHttpMessageHandler handler, ContextRegistrationBuffer buffer) MakeStateless(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, SmplClient? parent = null, HttpClient? http = null)
    {
        var handler = new MockHttpMessageHandler(respond);
        http ??= new HttpClient(handler);
        var factory = new GeneratedClientFactory(http, new SmplClientOptions
        {
            ApiKey = TestData.ApiKey,
            BaseDomain = "example.test",
        });
        var buffer = new ContextRegistrationBuffer(lruSize: 10_000, flushSize: 100);
        var flags = new FlagsClient(factory, TestData.ApiKey, ThrowingEvents, buffer, parent, metrics: null, streaming: false);
        return (flags, handler, buffer);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private static bool IsFlagsBulkPost(HttpRequestMessage req)
        => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/flags/bulk");

    private static bool IsContextsBulkPost(HttpRequestMessage req)
        => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/contexts/bulk");

    private const string BooleanFlagListJson = """
        {
            "data": [
                {
                    "id": "f1",
                    "type": "flag",
                    "attributes": {
                        "id": "f1",
                        "name": "f1",
                        "type": "BOOLEAN",
                        "default": true,
                        "values": [],
                        "description": null,
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private static object? Field(FlagsClient flags, string name)
        => typeof(FlagsClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(flags);

    [Fact]
    public void StatelessConnect_NoEventStreamNoTimer_HandleEvaluates()
    {
        var (flags, _, _) = MakeStateless(_ => Task.FromResult(Json(BooleanFlagListJson)));

        var handle = flags.BooleanFlag("f1", defaultValue: false);

        // Connected, evaluating from the one-time fetch...
        Assert.True(flags._connected);
        Assert.True(handle.Get());
        // ...with no live machinery: no event stream, no subscription, no timer.
        Assert.Null(Field(flags, "_eventStream"));
        Assert.False(flags._eventsSubscribed);
        Assert.Null(Field(flags, "_flagFlushTimer"));
        Assert.Equal("disconnected", flags.ConnectionStatus);
    }

    [Fact]
    public void StatelessConnect_WithParentService_RegistersInitialContextsInline()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(Json(TestData.EmptyListJson())));
        var http = new HttpClient(handler);
        using var parent = new SmplClient(TestData.DefaultOptions(), http);
        var (flags, statelessHandler, _) = MakeStateless(
            _ => Task.FromResult(Json(TestData.EmptyListJson())), parent: parent);

        var handle = flags.StringFlag("greeting", "hi");

        // The best-effort environment + service registration completed inline —
        // no background task is left running past the call.
        Assert.NotNull(flags._initRegistrationTask);
        Assert.True(flags._initRegistrationTask!.IsCompleted);
        Assert.Contains(statelessHandler.Requests, IsContextsBulkPost);
        Assert.Equal("hi", handle.Get());
    }

    [Fact]
    public void Register_FlushTrue_Stateless_FlushesInline()
    {
        var (flags, handler, _) = MakeStateless(_ => Task.FromResult(Json(TestData.EmptyListJson())));

        flags.Register(new FlagDeclaration("f", "BOOLEAN", true), flush: true);

        // The bulk POST happened before Register returned.
        Assert.Contains(handler.Requests, IsFlagsBulkPost);
        Assert.Equal(0, flags.PendingCount);
        Assert.True(flags._lastFlagBufferFlushTask!.IsCompleted);
    }

    [Fact]
    public void Register_FlushTrue_Stateless_PropagatesFailure()
    {
        var (flags, _, _) = MakeStateless(_ => Task.FromResult(Json(
            "{\"errors\":[{\"status\":\"500\",\"title\":\"boom\"}]}", HttpStatusCode.InternalServerError)));

        Assert.ThrowsAny<SmplkitException>(
            () => flags.Register(new FlagDeclaration("f", "BOOLEAN", true), flush: true));
        // Peek+commit: the failed batch stays queued for the next flush.
        Assert.Equal(1, flags.PendingCount);
    }

    [Fact]
    public void Register_Threshold_Stateless_FlushesInline()
    {
        var (flags, handler, _) = MakeStateless(_ => Task.FromResult(Json(TestData.EmptyListJson())));

        for (int i = 0; i < 50; i++)
            flags.Register(new FlagDeclaration($"f-{i}", "BOOLEAN", true));

        // The threshold flush ran inline — drained synchronously, no polling.
        Assert.Contains(handler.Requests, IsFlagsBulkPost);
        Assert.Equal(0, flags.PendingCount);
        Assert.True(flags._lastFlagBufferFlushTask!.IsCompleted);
    }

    [Fact]
    public void Register_Threshold_Stateless_SwallowsFailure()
    {
        var (flags, handler, _) = MakeStateless(_ => Task.FromResult(Json(
            "{\"errors\":[{\"status\":\"500\",\"title\":\"boom\"}]}", HttpStatusCode.InternalServerError)));

        for (int i = 0; i < 50; i++)
            flags.Register(new FlagDeclaration($"f-{i}", "BOOLEAN", true));

        // Best-effort: the failure was swallowed and the batch stays queued.
        Assert.Contains(handler.Requests, IsFlagsBulkPost);
        Assert.Equal(50, flags.PendingCount);
        Assert.True(flags._lastFlagBufferFlushTask!.IsCompleted);
    }

    [Fact]
    public void EvaluateHandle_ContextThreshold_Stateless_FlushesInline()
    {
        var (flags, handler, buffer) = MakeStateless(_ => Task.FromResult(Json(BooleanFlagListJson)));
        var handle = flags.BooleanFlag("f1", defaultValue: false);

        // Pre-load 99 pending contexts; the evaluation's context tips it to 100.
        buffer.Observe(Enumerable.Range(0, 99).Select(i => new Context("user", $"u-{i}")).ToList());
        handle.Get(new[] { new Context("user", "u-tip") });

        // The context flush ran inline — the bulk POST is already recorded.
        Assert.Contains(handler.Requests, IsContextsBulkPost);
        Assert.True(flags._lastContextBufferFlushTask!.IsCompleted);
        Assert.Equal(0, buffer.PendingCount);
    }

    [Fact]
    public async Task RefreshAsync_Stateless_RefetchesAndFiresListeners()
    {
        var (flags, _, _) = MakeStateless(_ => Task.FromResult(Json(BooleanFlagListJson)));
        var handle = flags.BooleanFlag("f1", defaultValue: false);
        Assert.True(handle.Get());

        var fired = new List<string>();
        flags.OnChange(evt => fired.Add(evt.Id));

        await flags.RefreshAsync();

        Assert.Contains("f1", fired);
        Assert.True(handle.Get());
    }
}
